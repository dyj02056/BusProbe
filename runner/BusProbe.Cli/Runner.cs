using System.Diagnostics;

namespace BusProbe;

public static class Runner
{
    // 시나리오 파일 1개: 파싱 → 시뮬레이션 → 판정. 실패 원인이 시험 대상이 아니라 도구/입력이면 ERROR.
    // 실시간 시나리오를 건너뛰는 옵션이면 null
    public static CaseResult? RunOne(SimHost host, string path, bool skipRealtime = false)
    {
        var yaml = File.ReadAllText(path);
        Scenario s;
        try { s = ScenarioLoader.Parse(yaml); }
        catch (ScenarioException e)
        {
            var stub = new Scenario { Id = Path.GetFileNameWithoutExtension(path), Title = "(시나리오 오류)" };
            return new CaseResult(stub, yaml, "ERROR", 0, new(), new(), e.Message);
        }
        if (skipRealtime && s.TimeMode == "realtime") return null;

        var sw = Stopwatch.StartNew();
        try
        {
            var trace = host.Run(s);
            var rs = Judge.Evaluate(s, trace);
            return new CaseResult(s, yaml, Judge.Verdict(rs), sw.ElapsedMilliseconds, rs, trace, null);
        }
        catch (Exception e) when (e is SimException or IOException or System.Net.Sockets.SocketException)
        {
            return new CaseResult(s, yaml, "ERROR", sw.ElapsedMilliseconds, new(), new(), e.Message);
        }
    }
}
