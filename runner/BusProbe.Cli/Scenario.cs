using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BusProbe;

// 시나리오 YAML 모델. 0x100 같은 16진 표기를 받기 위해 숫자 필드는 문자열로 읽고 Num()으로 바꾼다.
public class Scenario
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public List<string> Requirements { get; set; } = new();
    public string Bus { get; set; } = "can";          // can | m1553
    public string TimeMode { get; set; } = "virtual"; // virtual | realtime
    public int DurationMs { get; set; }
    public List<NodeDef> Nodes { get; set; } = new();
    public List<Expect> Expect { get; set; } = new();
}

public class NodeDef
{
    public string Name { get; set; } = "";
    // CAN 노드
    public List<CanTx> Periodic { get; set; } = new();
    public List<string> Subscribe { get; set; } = new(); // "0x100", "ext:0x100"(확장 ID 명시) 또는 "*"
    public List<CanRsp> Respond { get; set; } = new();
    // 1553B: rt가 있으면 RT 노드, schedule이 있으면 BC 노드
    public int? Rt { get; set; }
    public bool Silent { get; set; }
    public List<string> Status { get; set; } = new();   // 상태 워드 비트 이름들
    public List<string> TxWords { get; set; } = new();  // RT→BC 때 돌려줄 데이터 워드
    public List<Transfer> Schedule { get; set; } = new();
}

public class CanTx
{
    public string Id { get; set; } = "";
    public bool Ext { get; set; }
    public int Dlc { get; set; }
    public List<string> Data { get; set; } = new();
    public int PeriodMs { get; set; }
    public int OffsetMs { get; set; }
}

public class CanRsp
{
    public string On { get; set; } = "";
    public bool? OnExt { get; set; }               // 생략하면 on > 0x7FF일 때 확장 ID로 본다
    public string Id { get; set; } = "";
    public bool Ext { get; set; }
    public int Dlc { get; set; }
    public List<string> Data { get; set; } = new();
    public int DelayMs { get; set; }
}

public class Transfer
{
    public int Rt { get; set; }
    public string Dir { get; set; } = "bc_to_rt"; // bc_to_rt | rt_to_bc
    public int Sa { get; set; }
    public int? Wc { get; set; }                  // 생략하면 words 개수
    public List<string> Words { get; set; } = new();
    public int PeriodMs { get; set; }
    public int OffsetMs { get; set; }
}

// 기대값. kind마다 쓰는 필드만 채운다 (사용하는 필드는 Scenario 검증에서 확인)
public class Expect
{
    public string Kind { get; set; } = "";
    public string? Node { get; set; }
    public string Dir { get; set; } = "rx";
    public string? Id { get; set; }          // CAN 식별자
    public bool? Ext { get; set; }           // 생략하면 id > 0x7FF일 때 확장 ID로 본다
    public int? Rt { get; set; }             // 1553B 키: rt, tr(0/1), sa
    public int? Tr { get; set; }
    public int? Sa { get; set; }
    public long? FromMs { get; set; }        // 관찰 구간
    public long? ToMs { get; set; }
    public int? Min { get; set; }
    public int? Max { get; set; }
    public double? MeanMs { get; set; }
    public double? ToleranceMs { get; set; }
    public double? JitterP99Ms { get; set; }
    public string? ToNode { get; set; }      // latency 도착 노드/ID
    public string? ToId { get; set; }
    public bool? ToExt { get; set; }
    public double? P95Ms { get; set; }
    public int? StartBit { get; set; }
    public int? Length { get; set; }
    public double? Factor { get; set; }
    public double? Offset { get; set; }
    public double? Lo { get; set; }
    public double? Hi { get; set; }
    public Dictionary<string, bool>? Bits { get; set; }
}

public class ScenarioException : Exception
{
    public ScenarioException(string m) : base(m) { }
}

public static class ScenarioLoader
{
    // 1553B 상태 워드 비트 이름 → 마스크 (core/m1553.hpp와 일치해야 함)
    public static readonly Dictionary<string, int> StatusBits = new()
    {
        ["message_error"] = 1 << 10, ["service_request"] = 1 << 8, ["broadcast_received"] = 1 << 4,
        ["busy"] = 1 << 3, ["subsystem_flag"] = 1 << 2, ["terminal_flag"] = 1,
    };

    public const int MaxDurationMs = 3_600_000; // core/sim.hpp MAX_DURATION_MS와 일치
    const long CanEff = 0x80000000L;             // 확장 ID 키 비트 (core/can.hpp CAN_EFF)

    static readonly string[] Kinds = { "count", "period", "latency", "signal", "status_word" };
    static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance).Build(); // 모르는 키는 예외 → 오타 검출

    // "0x1F" 또는 "31"
    public static long Num(string s) =>
        s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt64(s[2..], 16) : long.Parse(s);

    // CAN 트레이스 키: 확장 ID는 0x80000000 비트를 붙인다. ext를 생략하면 11비트를 넘는 ID만 확장으로 본다
    public static long CanKey(string id, bool? ext)
    {
        long n = Num(id);
        return (ext ?? n > 0x7FF) ? n | CanEff : n;
    }

    // subscribe 항목: "*"는 -1, "ext:0x100"은 확장 ID 명시
    public static long SubKey(string sub) =>
        sub == "*" ? -1 : sub.StartsWith("ext:", StringComparison.OrdinalIgnoreCase) ? CanKey(sub[4..], true) : CanKey(sub, null);

    public static Scenario Parse(string yaml)
    {
        try
        {
            var s = Yaml.Deserialize<Scenario>(yaml) ?? throw new ScenarioException("빈 시나리오");
            Validate(s);
            BuildConfig(s); // 16진/숫자 문자열 형식 오류를 여기서 미리 잡는다
            return s;
        }
        catch (YamlException e) { throw new ScenarioException("YAML 오류: " + e.Message + (e.InnerException is null ? "" : " " + e.InnerException.Message)); }
        catch (Exception e) when (e is FormatException or OverflowException) { throw new ScenarioException("숫자 형식 오류: " + e.Message); }
    }

    // 신뢰 경계: 시뮬레이터에 보내기 전에 필수 필드, 값 범위, 참조를 모두 확인
    static void Validate(Scenario s)
    {
        void Need(bool ok, string msg) { if (!ok) throw new ScenarioException(msg); }
        Need(Regex.IsMatch(s.Id, @"^[A-Za-z0-9_-]+$"), "id는 영문/숫자/-/_ 만 가능");
        Need(s.Bus is "can" or "m1553", "bus는 can 또는 m1553");
        Need(s.TimeMode is "virtual" or "realtime", "time_mode는 virtual 또는 realtime");
        Need(s.DurationMs is > 0 and <= MaxDurationMs, $"duration_ms는 1~{MaxDurationMs}");
        Need(s.Nodes.Count > 0, "nodes가 비어 있음");
        Need(s.Expect.Count > 0, "expect가 비어 있음");
        foreach (var n in s.Nodes) Need(Regex.IsMatch(n.Name, @"^\w+$"), $"노드 이름 '{n.Name}'은 영문/숫자/_ 만 가능 (공백 불가)");
        Need(s.Nodes.Select(n => n.Name).Distinct().Count() == s.Nodes.Count, "노드 이름 중복");

        void Frame(string at, string id, bool ext, int dlc, List<string> data)
        {
            long n = Num(id);
            Need(n >= 0 && n <= (ext ? 0x1FFFFFFF : 0x7FF), $"{at}: id {id}가 {(ext ? "확장 29비트" : "표준 11비트(확장이면 ext: true)")} 범위 밖");
            Need(dlc is >= 0 and <= 8, $"{at}: dlc는 0~8");
            Need(data.Count <= dlc, $"{at}: data {data.Count}바이트가 dlc {dlc}보다 많음");
            foreach (var d in data) Need(Num(d) is >= 0 and <= 0xFF, $"{at}: data 값 {d}는 0~0xFF 범위 밖");
        }
        void Words(string at, List<string> words)
        {
            Need(words.Count <= 32, $"{at}: 워드는 최대 32개");
            foreach (var w in words) Need(Num(w) is >= 0 and <= 0xFFFF, $"{at}: 워드 값 {w}는 0~0xFFFF 범위 밖");
        }
        void Ms(string at, long v, bool positive) =>
            Need(positive ? v is > 0 and <= MaxDurationMs : v is >= 0 and <= MaxDurationMs, $"{at}: 시간 값 {v}ms가 범위 밖");

        foreach (var n in s.Nodes)
        {
            foreach (var t in n.Periodic)
            {
                Frame($"{n.Name}.periodic", t.Id, t.Ext, t.Dlc, t.Data);
                Ms($"{n.Name}.periodic.period_ms", t.PeriodMs, true);
                Ms($"{n.Name}.periodic.offset_ms", t.OffsetMs, false);
            }
            foreach (var r in n.Respond)
            {
                Frame($"{n.Name}.respond", r.Id, r.Ext, r.Dlc, r.Data);
                long on = Num(r.On);
                Need(on >= 0 && on <= ((r.OnExt ?? on > 0x7FF) ? 0x1FFFFFFF : 0x7FF), $"{n.Name}.respond.on {r.On}가 ID 범위 밖");
                Ms($"{n.Name}.respond.delay_ms", r.DelayMs, false);
            }
            foreach (var sub in n.Subscribe)
            {
                if (sub == "*") continue;
                bool explicitExt = sub.StartsWith("ext:", StringComparison.OrdinalIgnoreCase);
                long id = Num(explicitExt ? sub[4..] : sub);
                Need(id >= 0 && id <= 0x1FFFFFFF, $"{n.Name}.subscribe '{sub}'가 ID 범위 밖");
            }
            foreach (var st in n.Status) Need(StatusBits.ContainsKey(st), $"{n.Name}: 알 수 없는 상태 비트 '{st}'");
            if (n.Rt is int addr) Need(addr is >= 0 and <= 30, $"{n.Name}: rt 주소는 0~30 (31은 방송)");
            Words($"{n.Name}.tx_words", n.TxWords);
            foreach (var t in n.Schedule)
            {
                string at = $"{n.Name}.schedule";
                Need(t.Dir is "bc_to_rt" or "rt_to_bc", $"{at}: dir은 bc_to_rt 또는 rt_to_bc");
                Need(t.Rt is >= 0 and <= 31, $"{at}: rt는 0~31");
                Need(t.Sa is >= 0 and <= 31, $"{at}: sa는 0~31");
                Ms($"{at}.period_ms", t.PeriodMs, true);
                Ms($"{at}.offset_ms", t.OffsetMs, false);
                Words(at, t.Words);
                int wc = t.Wc ?? t.Words.Count;
                Need(wc is >= 1 and <= 32, $"{at}: 워드 수(wc)는 1~32");
                if (t.Dir == "bc_to_rt") Need(t.Words.Count == wc, $"{at}: words 개수 {t.Words.Count}가 wc {wc}와 다름");
                else
                {
                    Need(t.Words.Count == 0, $"{at}: rt_to_bc에는 words를 쓰지 않음 (RT의 tx_words 사용)");
                    Need(t.Rt != 31, $"{at}: 방송(rt 31)은 rt_to_bc 불가");
                }
            }
        }
        var rtAddrs = s.Nodes.Where(n => n.Rt is not null).Select(n => n.Rt!.Value).ToList();
        Need(rtAddrs.Distinct().Count() == rtAddrs.Count, "RT 주소 중복");

        var names = s.Nodes.Select(n => n.Name).ToHashSet();
        var bcNames = s.Nodes.Where(n => n.Schedule.Count > 0).Select(n => n.Name).ToHashSet();
        for (int i = 0; i < s.Expect.Count; i++)
        {
            var e = s.Expect[i];
            string at = $"expect[{i}] ({e.Kind})";
            Need(Kinds.Contains(e.Kind), $"{at}: 알 수 없는 kind");
            Need(e.Dir is "tx" or "rx" or "to", $"{at}: dir은 tx|rx|to");
            Need(e.Node is not null && names.Contains(e.Node), $"{at}: node가 없거나 nodes에 없음");
            Need(e.FromMs is null or >= 0 && e.ToMs is null or >= 0, $"{at}: from_ms/to_ms는 0 이상");
            Need(e.FromMs is null || e.ToMs is null || e.FromMs < e.ToMs, $"{at}: from_ms < to_ms 필요");
            if (s.Bus == "can")
            {
                Need(e.Id is not null, $"{at}: id 필요");
                CanKey(e.Id!, e.Ext);
                if (e.ToId is not null) CanKey(e.ToId, e.ToExt);
            }
            else Need(e.Rt is >= 0 and <= 31 && e.Tr is 0 or 1 && e.Sa is >= 0 and <= 31, $"{at}: rt(0~31), tr(0/1), sa(0~31) 필요");
            switch (e.Kind)
            {
                case "count":
                    Need(e.Min is null or >= 0 && e.Max is null or >= 0, $"{at}: min/max는 0 이상");
                    Need(e.Min is null || e.Max is null || e.Min <= e.Max, $"{at}: min <= max 필요");
                    break;
                case "period":
                    Need(e.MeanMs is > 0, $"{at}: mean_ms(> 0) 필요");
                    Need(e.ToleranceMs is null or >= 0 && e.JitterP99Ms is null or >= 0, $"{at}: tolerance_ms/jitter_p99_ms는 0 이상");
                    break;
                case "latency":
                    Need(s.Bus == "can", $"{at}: latency는 can만 지원");
                    Need(e.P95Ms is >= 0 && e.ToId is not null && e.ToNode is not null && names.Contains(e.ToNode), $"{at}: p95_ms, to_node, to_id 필요");
                    break;
                case "signal":
                    Need(s.Bus == "can", $"{at}: signal은 can만 지원");
                    Need(e.StartBit is >= 0 && e.Length is >= 1 and <= 64 && e.Lo is not null && e.Hi is not null, $"{at}: start_bit, length(1~64), lo, hi 필요");
                    Need(e.StartBit + e.Length <= 64, $"{at}: start_bit + length가 64비트(8바이트)를 넘음");
                    Need(e.Lo <= e.Hi, $"{at}: lo <= hi 필요");
                    break;
                case "status_word":
                    Need(s.Bus == "m1553", $"{at}: status_word는 m1553만 지원");
                    Need(e.Dir == "rx" && bcNames.Contains(e.Node!), $"{at}: status_word는 BC 노드의 rx(응답 수신)만 판정 가능");
                    Need(e.Rt != 31, $"{at}: 방송(rt 31)에는 상태 워드가 없음");
                    Need(e.Bits is { Count: > 0 } && e.Bits.Keys.All(StatusBits.ContainsKey), $"{at}: bits에 알려진 상태 비트 이름 필요");
                    break;
            }
        }
    }

    // 시뮬레이터 설정 텍스트 생성 (형식은 docs/protocol.md)
    public static string BuildConfig(Scenario s)
    {
        // 범위는 Validate에서 확인했지만, 자릿수가 어긋나면 바이트 경계가 밀리므로 한 번 더 막는다
        string Hex(string x, int digits)
        {
            var h = Num(x).ToString("x" + digits);
            if (h.Length != digits) throw new ScenarioException($"값 {x}가 {digits / 2}바이트를 넘음");
            return h;
        }
        string Bytes(List<string> d) => d.Count == 0 ? "-" : string.Concat(d.Select(x => Hex(x, 2)));
        string Words(List<string> d) => d.Count == 0 ? "-" : string.Concat(d.Select(x => Hex(x, 4)));
        var sb = new StringBuilder();
        sb.Append($"mode {s.TimeMode}\nduration_ms {s.DurationMs}\n");
        foreach (var n in s.Nodes)
        {
            foreach (var t in n.Periodic)
                sb.Append($"can_tx {n.Name} {Num(t.Id)} {(t.Ext ? 1 : 0)} {t.PeriodMs} {t.OffsetMs} {t.Dlc} {Bytes(t.Data)}\n");
            foreach (var id in n.Subscribe)
                sb.Append($"can_sub {n.Name} {(id == "*" ? "*" : SubKey(id))}\n");
            foreach (var r in n.Respond)
                sb.Append($"can_rsp {n.Name} {CanKey(r.On, r.OnExt)} {Num(r.Id)} {(r.Ext ? 1 : 0)} {r.DelayMs} {r.Dlc} {Bytes(r.Data)}\n");
            if (n.Rt is int addr)
            {
                int flags = n.Status.Sum(x => StatusBits[x]);
                sb.Append($"m_rt {n.Name} {addr} {(n.Silent ? 1 : 0)} {flags} {Words(n.TxWords)}\n");
            }
            foreach (var t in n.Schedule)
            {
                int wc = t.Wc ?? t.Words.Count;
                sb.Append($"m_bc {n.Name} {t.Rt} {(t.Dir == "rt_to_bc" ? 1 : 0)} {t.Sa} {wc} {t.PeriodMs} {t.OffsetMs} {Words(t.Words)}\n");
            }
        }
        return sb.ToString();
    }
}
