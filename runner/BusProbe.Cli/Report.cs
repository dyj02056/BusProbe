using System.Text;
using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BusProbe;

// 요구사항 YAML 한 항목 (docs/requirements.yaml)
public class ReqRow
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
}

// 결과서(HTML)와 요구사항 추적표(CSV) 생성. DB 없이 메모리의 결과만으로 만든다.
public static class Report
{
    const int HtmlTraceCap = 3000; // HTML 용량 보호. 전체는 traces/*.ndjson 에 저장

    public static List<Req> LoadReqs(string path) =>
        !File.Exists(path) ? new() :
        new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).Build()
            .Deserialize<List<ReqRow>>(File.ReadAllText(path))?.Select(r => new Req(r.Id, r.Title)).ToList() ?? new();

    // 요구사항 파일 + 시나리오가 참조했지만 파일에 없는 ID
    static List<Req> Merge(List<Req> reqs, List<CaseResult> cases) =>
        reqs.Concat(cases.SelectMany(c => c.Scenario.Requirements).Distinct()
            .Where(id => reqs.All(r => r.Id != id)).Select(id => new Req(id, "(요구사항 파일에 없음)"))).ToList();

    public static string Html(RunInfo run, List<CaseResult> cases, List<Req> reqs)
    {
        using var s = typeof(Report).Assembly.GetManifestResourceStream("report.template.html")!;
        var tpl = new StreamReader(s, Encoding.UTF8).ReadToEnd();
        var data = new
        {
            run = new
            {
                startedAt = run.StartedAt, gitSha = run.GitSha, toolVersion = run.ToolVersion, total = cases.Count,
                passed = cases.Count(c => c.Status == "PASS"), failed = cases.Count(c => c.Status == "FAIL"), errored = cases.Count(c => c.Status == "ERROR"),
            },
            cases = cases.Select(c => new
            {
                id = c.Scenario.Id, title = c.Scenario.Title, bus = c.Scenario.Bus, timeMode = c.Scenario.TimeMode, status = c.Status,
                durationMs = c.DurationMs, scenarioMs = c.Scenario.DurationMs, requirements = c.Scenario.Requirements, error = c.Error,
                results = c.Results.Select(r => new { r.Kind, r.Status, r.Expected, r.Actual, r.Message }),
                traceTotal = c.Trace.Count,
                trace = c.Trace.Take(HtmlTraceCap).Select(e => new object[] { e.TsNs, e.Dir, e.Node, e.Key, Convert.ToHexString(e.Raw).ToLowerInvariant() }),
            }),
            requirements = Merge(reqs, cases).Select(r => new
            {
                id = r.Id, title = r.Title,
                caseIds = cases.Where(c => c.Scenario.Requirements.Contains(r.Id)).Select(c => c.Scenario.Id),
            }),
        };
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        // < 를 이스케이프해 </script> 로 페이지가 깨지는 것을 막는다
        return tpl.Replace("__DATA__", json.Replace("<", "\\u003c"));
    }

    // 요구사항마다 연결된 시나리오 한 줄씩. 시험이 없는 요구사항은 "미시험"으로 남긴다
    public static string Csv(string runLabel, List<CaseResult> cases, List<Req> reqs)
    {
        static string Q(string v) => v.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        var sb = new StringBuilder("요구사항ID,제목,시나리오ID,결과,실행\r\n");
        foreach (var r in Merge(reqs, cases))
        {
            var linked = cases.Where(c => c.Scenario.Requirements.Contains(r.Id)).ToList();
            if (linked.Count == 0) sb.Append($"{Q(r.Id)},{Q(r.Title)},,미시험,{runLabel}\r\n");
            foreach (var c in linked) sb.Append($"{Q(r.Id)},{Q(r.Title)},{Q(c.Scenario.Id)},{c.Status},{runLabel}\r\n");
        }
        return sb.ToString();
    }

    // 전체 트래픽 (NDJSON): 이벤트 한 줄에 JSON 하나
    public static string Ndjson(List<Ev> trace) =>
        string.Concat(trace.Select(e => JsonSerializer.Serialize(new
        { ts_ns = e.TsNs, dir = e.Dir, node = e.Node, bus = e.Bus, key = e.Key, raw = Convert.ToHexString(e.Raw).ToLowerInvariant() }) + "\n"));
}
