using System.Diagnostics;

namespace BusProbe;

public static class Runner
{
    // 시나리오 파일 1개: 파싱 → 시뮬레이션 → 판정. 실패 원인이 시험 대상이 아니라 도구/입력이면 ERROR.
    // 실시간 시나리오를 건너뛰는 옵션이면 SKIP (결과서·추적표에 건너뜀으로 남긴다)
    public static CaseResult RunOne(SimHost host, string path, bool skipRealtime = false)
    {
        var yaml = File.ReadAllText(path);
        Scenario s;
        try { s = ScenarioLoader.Parse(yaml); }
        catch (ScenarioException e)
        {
            var stub = new Scenario { Id = Path.GetFileNameWithoutExtension(path), Title = "(시나리오 오류)" };
            return new CaseResult(stub, yaml, "ERROR", 0, new(), new(), e.Message);
        }
        if (skipRealtime && s.TimeMode == "realtime")
            return new CaseResult(s, yaml, "SKIP", 0, new(), new(), "실시간 시나리오 (--no-realtime)");

        var sw = Stopwatch.StartNew();
        try
        {
            var trace = host.Run(s);
            var rs = Judge.Evaluate(s, trace);
            return new CaseResult(s, yaml, Judge.Verdict(rs), sw.ElapsedMilliseconds, rs, trace, null);
        }
        catch (SimException e)
        {
            // 멈추거나 죽은 시뮬레이터가 다음 시나리오까지 오류로 만들지 않게 새로 띄운다
            if (e.Fatal) host.Restart();
            return new CaseResult(s, yaml, "ERROR", sw.ElapsedMilliseconds, new(), new(), e.Message + (e.Fatal ? " (시뮬레이터 재시작)" : ""));
        }
    }
}
