using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace BusProbe;

public record CaseResult(Scenario Scenario, string Yaml, string Status, long DurationMs, List<Result> Results, List<Ev> Trace, string? Error);
public record RunInfo(DateTime StartedAt, string? GitSha, string ToolVersion);
public record Req(string Id, string Title);

// Neon(Postgres) 저장소. 연결 정보는 환경변수 DATABASE_URL (postgresql://... 또는 Npgsql 형식)
public static class PgStore
{
    public const int MaxMessagesPerCase = 5000; // 무료 티어 저장량 보호

    // Neon이 주는 URI를 Npgsql 연결 문자열로. URI의 sslmode를 따르고, 없으면 Require (원격 DB 기본값)
    public static string ConnString(string url)
    {
        if (!url.Contains("://")) return url;
        var u = new Uri(url);
        var ui = u.UserInfo.Split(':', 2);
        return new NpgsqlConnectionStringBuilder
        {
            Host = u.Host,
            Port = u.Port > 0 ? u.Port : 5432,
            Database = Uri.UnescapeDataString(u.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(ui[0]),
            Password = ui.Length > 1 ? Uri.UnescapeDataString(ui[1]) : null,
            SslMode = SslModeOf(u.Query),
            Timeout = 15,
        }.ConnectionString;
    }

    // libpq 표기(sslmode=verify-full 등) → Npgsql SslMode. 모르는 값은 ArgumentException
    static SslMode SslModeOf(string query)
    {
        var v = query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0].Equals("sslmode", StringComparison.OrdinalIgnoreCase) && p.Length == 2)?[1];
        return v?.ToLowerInvariant() switch
        {
            null => SslMode.Require,
            "disable" => SslMode.Disable,
            "allow" => SslMode.Allow,
            "prefer" => SslMode.Prefer,
            "require" => SslMode.Require,
            "verify-ca" => SslMode.VerifyCA,
            "verify-full" => SslMode.VerifyFull,
            _ => throw new ArgumentException("DATABASE_URL의 sslmode 값을 알 수 없음: " + v),
        };
    }

    // 재시도할 만한 일시적 오류인가 (인증 실패, 없는 DB 같은 영구 오류는 바로 알린다)
    public static bool IsTransient(Exception e) =>
        e is TimeoutException or System.Net.Sockets.SocketException || e is NpgsqlException { IsTransient: true };

    // 콜드 스타트 대비: 일시적 오류일 때만 1초, 2초 간격으로 최대 3회 시도
    public static NpgsqlConnection Open(string url)
    {
        for (int attempt = 1; ; attempt++)
        {
            var c = new NpgsqlConnection(ConnString(url));
            try { c.Open(); return c; }
            catch (Exception e) when (attempt < 3 && IsTransient(e)) { c.Dispose(); Thread.Sleep(1000 * attempt); }
            catch { c.Dispose(); throw; }
        }
    }

    // db/migrations/NNN_*.sql 을 번호순으로 적용하고 schema_migrations 에 기록
    public static int Migrate(string url, string dir)
    {
        using var c = Open(url);
        Exec(c, "CREATE TABLE IF NOT EXISTS schema_migrations (name TEXT PRIMARY KEY, applied_at TIMESTAMPTZ NOT NULL DEFAULT now())");
        int applied = 0;
        foreach (var f in Directory.GetFiles(dir, "*.sql").OrderBy(x => x, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(f);
            using var chk = new NpgsqlCommand("SELECT 1 FROM schema_migrations WHERE name=@n", c);
            chk.Parameters.AddWithValue("n", name);
            if (chk.ExecuteScalar() is not null) continue;
            using var tx = c.BeginTransaction();
            Exec(c, File.ReadAllText(f), tx);
            using var ins = new NpgsqlCommand("INSERT INTO schema_migrations(name) VALUES (@n)", c, tx);
            ins.Parameters.AddWithValue("n", name);
            ins.ExecuteNonQuery();
            tx.Commit();
            applied++;
        }
        return applied;
    }

    // 실행 1회를 한 트랜잭션으로 저장하고 run id를 돌려준다
    public static long Save(string url, RunInfo run, List<CaseResult> cases, List<Req> reqs)
    {
        using var c = Open(url);
        using var tx = c.BeginTransaction();

        foreach (var r in reqs) // 요구사항 파일의 제목을 반영
            Run(c, tx, "INSERT INTO requirements(req_id,title) VALUES (@i,@t) ON CONFLICT (req_id) DO UPDATE SET title=EXCLUDED.title", ("i", r.Id), ("t", r.Title));
        foreach (var id in cases.SelectMany(x => x.Scenario.Requirements).Distinct()) // 파일에 없는 참조는 제목 없이 등록
            Run(c, tx, "INSERT INTO requirements(req_id,title) VALUES (@i,@i) ON CONFLICT (req_id) DO NOTHING", ("i", id));

        long runId = (long)Scalar(c, tx,
            "INSERT INTO runs(started_at,git_sha,tool_version,total,passed,failed,errored,skipped) VALUES (@s,@g,@v,@a,@p,@f,@e,@k) RETURNING id",
            ("s", run.StartedAt), ("g", (object?)run.GitSha ?? DBNull.Value), ("v", run.ToolVersion),
            ("a", cases.Count), ("p", cases.Count(x => x.Status == "PASS")), ("f", cases.Count(x => x.Status == "FAIL")),
            ("e", cases.Count(x => x.Status == "ERROR")), ("k", cases.Count(x => x.Status == "SKIP")))!;
        Run(c, tx, "UPDATE runs SET finished_at=now() WHERE id=@i", ("i", runId));

        foreach (var k in cases)
        {
            long caseId = (long)Scalar(c, tx,
                "INSERT INTO cases(run_id,scenario_id,title,bus,time_mode,status,duration_ms,error,scenario_yaml,trace_total,trace_stored) " +
                "VALUES (@r,@s,@t,@b,@m,@st,@d,@er,@y,@tt,@ts) RETURNING id",
                ("r", runId), ("s", k.Scenario.Id), ("t", k.Scenario.Title), ("b", k.Scenario.Bus), ("m", k.Scenario.TimeMode),
                ("st", k.Status), ("d", (int)Math.Min(int.MaxValue, k.DurationMs)), ("er", (object?)k.Error ?? DBNull.Value), ("y", k.Yaml),
                ("tt", k.Trace.Count), ("ts", Math.Min(k.Trace.Count, MaxMessagesPerCase)))!; // 잘림 여부: trace_stored < trace_total
            foreach (var req in k.Scenario.Requirements.Distinct())
                Run(c, tx, "INSERT INTO case_requirements(case_id,req_id) VALUES (@c,@r)", ("c", caseId), ("r", req));
            for (int i = 0; i < k.Results.Count; i++)
            {
                var r = k.Results[i];
                using var cmd = new NpgsqlCommand("INSERT INTO results(case_id,idx,kind,status,expected,actual,message) VALUES (@c,@i,@k,@s,@e,@a,@m)", c, tx);
                cmd.Parameters.AddWithValue("c", caseId); cmd.Parameters.AddWithValue("i", i);
                cmd.Parameters.AddWithValue("k", r.Kind); cmd.Parameters.AddWithValue("s", r.Status);
                cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(r.Expected) });
                cmd.Parameters.Add(new NpgsqlParameter("a", NpgsqlDbType.Jsonb) { Value = r.Actual is null ? DBNull.Value : JsonSerializer.Serialize(r.Actual) });
                cmd.Parameters.AddWithValue("m", r.Message);
                cmd.ExecuteNonQuery();
            }
            // 트래픽은 COPY로 한 번에 (상한 초과분은 로컬 파일에만)
            using var w = c.BeginBinaryImport("COPY messages (case_id,seq,ts_ns,bus,dir,node,msg_key,raw) FROM STDIN (FORMAT BINARY)");
            int seq = 0;
            foreach (var e in k.Trace.Take(MaxMessagesPerCase))
            {
                w.StartRow();
                w.Write(caseId, NpgsqlDbType.Bigint); w.Write(seq++, NpgsqlDbType.Integer); w.Write(e.TsNs, NpgsqlDbType.Bigint);
                w.Write(e.Bus, NpgsqlDbType.Text); w.Write(e.Dir, NpgsqlDbType.Text); w.Write(e.Node, NpgsqlDbType.Text);
                w.Write(e.Key, NpgsqlDbType.Bigint); w.Write(e.Raw, NpgsqlDbType.Bytea);
            }
            w.Complete();
        }
        tx.Commit();
        return runId;
    }

    // 최근 실행 목록 (busprobe history)
    public static List<string> History(string url, int last)
    {
        using var c = Open(url);
        using var cmd = new NpgsqlCommand("SELECT id, started_at, git_sha, total, passed, failed, errored, COALESCE(skipped, 0) FROM runs ORDER BY id DESC LIMIT @n", c);
        cmd.Parameters.AddWithValue("n", last);
        using var r = cmd.ExecuteReader();
        var rows = new List<string>();
        while (r.Read())
            rows.Add($"#{r.GetInt64(0),-4} {r.GetDateTime(1):yyyy-MM-dd HH:mm}  {(r.IsDBNull(2) ? "-" : r.GetString(2)),-8} 전체 {r.GetInt32(3)} · 통과 {r.GetInt32(4)} · 실패 {r.GetInt32(5)} · 오류 {r.GetInt32(6)} · 건너뜀 {r.GetInt32(7)}");
        return rows;
    }

    static void Exec(NpgsqlConnection c, string sql, NpgsqlTransaction? tx = null)
    {
        using var cmd = new NpgsqlCommand(sql, c, tx);
        cmd.ExecuteNonQuery();
    }

    static void Run(NpgsqlConnection c, NpgsqlTransaction tx, string sql, params (string, object)[] p) => Scalar(c, tx, sql, p);

    static object? Scalar(NpgsqlConnection c, NpgsqlTransaction tx, string sql, params (string, object)[] p)
    {
        using var cmd = new NpgsqlCommand(sql, c, tx);
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v);
        return cmd.ExecuteScalar();
    }
}
