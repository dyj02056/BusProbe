using System.Diagnostics;
using System.Reflection;
using System.Text;
using BusProbe;

// busprobe CLI: run | history | db migrate
Console.OutputEncoding = Encoding.UTF8;

string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
bool Flag(string name) => args.Contains(name);
string? dbUrl = Environment.GetEnvironmentVariable("DATABASE_URL");

if (args.Length == 0) { Console.WriteLine("사용법: busprobe run <시나리오 폴더> --simd <busprobe-simd> [--out reports] [--reqs docs/requirements.yaml] [--no-db] [--no-realtime]\n        busprobe history [--last 10]\n        busprobe db migrate [--dir db/migrations]"); return 2; }

try
{
    switch (args[0])
    {
        case "run": return RunCmd();
        case "history":
            if (dbUrl is null) { Console.Error.WriteLine("DATABASE_URL 환경변수가 필요합니다."); return 2; }
            foreach (var l in PgStore.History(dbUrl, int.Parse(Opt("--last") ?? "10"))) Console.WriteLine(l);
            return 0;
        case "db" when args.Length > 1 && args[1] == "migrate":
            if (dbUrl is null) { Console.Error.WriteLine("DATABASE_URL 환경변수가 필요합니다."); return 2; }
            Console.WriteLine($"마이그레이션 {PgStore.Migrate(dbUrl, Opt("--dir") ?? "db/migrations")}건 적용");
            return 0;
        default: Console.Error.WriteLine("알 수 없는 명령: " + args[0]); return 2;
    }
}
catch (Exception e) when (e is SimException or IOException or ArgumentException or FormatException)
{
    Console.Error.WriteLine("오류: " + e.Message); // 연결 문자열 등 비밀은 메시지에 싣지 않는다
    return 2;
}

// 시나리오 폴더 전체 실행 → 판정 → 파일 생성 → (선택) Neon 저장. 종료 코드: 0 통과, 1 실패, 2 오류
int RunCmd()
{
    if (args.Length < 2) throw new ArgumentException("시나리오 폴더를 지정하세요");
    var simd = Opt("--simd") ?? Environment.GetEnvironmentVariable("BUSPROBE_SIMD") ?? throw new ArgumentException("--simd 또는 BUSPROBE_SIMD 필요");
    var outDir = Opt("--out") ?? "reports";
    var reqsPath = Opt("--reqs") ?? "docs/requirements.yaml";
    var files = Directory.GetFiles(args[1], "*.yaml").OrderBy(f => f, StringComparer.Ordinal).ToList();

    var run = new RunInfo(DateTime.UtcNow, GitSha(), Assembly.GetEntryAssembly()!.GetName().Version!.ToString(3));
    var cases = new List<CaseResult>();
    using (var host = new SimHost(simd))
        foreach (var f in files)
        {
            var r = Runner.RunOne(host, f, Flag("--no-realtime"));
            if (r is null) { Console.WriteLine($"건너뜀 {Path.GetFileName(f)} (실시간)"); continue; }
            cases.Add(r);
            Console.WriteLine($"{r.Status,-5} {r.Scenario.Id,-14} {r.Scenario.Title}" + (r.Status == "PASS" ? "" : "\n        " + (r.Error ?? string.Join(" / ", r.Results.Where(x => x.Status != "PASS").Select(x => $"{x.Kind}: {x.Message}")))));
        }

    var reqs = Report.LoadReqs(reqsPath);
    Directory.CreateDirectory(Path.Combine(outDir, "traces"));
    File.WriteAllText(Path.Combine(outDir, "report.html"), Report.Html(run, cases, reqs), new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(outDir, "traceability.csv"), Report.Csv(run.StartedAt.ToString("yyyyMMdd-HHmmss"), cases, reqs), new UTF8Encoding(true)); // BOM: 엑셀 한글
    foreach (var c in cases) File.WriteAllText(Path.Combine(outDir, "traces", c.Scenario.Id + ".ndjson"), Report.Ndjson(c.Trace));

    int pass = cases.Count(c => c.Status == "PASS"), fail = cases.Count(c => c.Status == "FAIL"), err = cases.Count(c => c.Status == "ERROR");
    Console.WriteLine($"\n전체 {cases.Count} · 통과 {pass} · 실패 {fail} · 오류 {err}\n결과서: {Path.GetFullPath(Path.Combine(outDir, "report.html"))}");

    if (dbUrl is not null && !Flag("--no-db"))
    {
        try { Console.WriteLine($"Neon에 저장했습니다 (run #{PgStore.Save(dbUrl, run, cases, reqs)})"); }
        catch (Exception e) { Console.Error.WriteLine("경고: DB 저장 실패, 결과서는 생성되었습니다. " + e.GetType().Name + ": " + e.Message); } // DB 장애로 시험 결과를 잃지 않는다
    }
    return err > 0 ? 2 : fail > 0 ? 1 : 0;
}

string? GitSha()
{
    try
    {
        var p = Process.Start(new ProcessStartInfo("git", "rev-parse --short HEAD") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
        var o = p!.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return p.ExitCode == 0 && o.Length > 0 ? o : null;
    }
    catch { return null; }
}
