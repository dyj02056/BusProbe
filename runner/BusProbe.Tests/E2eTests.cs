using BusProbe;
using Xunit;

namespace BusProbe.Tests;

// 실제 busprobe-simd 를 띄우는 통합 테스트. 빌드된 시뮬레이터가 없으면 로컬에서는 건너뛰지만,
// BUSPROBE_REQUIRE_SIMD=1(CI)이면 조용히 통과하지 않고 실패한다
public class E2eTests
{
    static string? Simd()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            foreach (var n in new[] { "busprobe-simd.exe", "busprobe-simd" })
            {
                var p = Path.Combine(d.FullName, "build", n);
                if (File.Exists(p)) return p;
            }
        if (Environment.GetEnvironmentVariable("BUSPROBE_REQUIRE_SIMD") == "1")
            throw new FileNotFoundException("BUSPROBE_REQUIRE_SIMD=1 인데 build/busprobe-simd 가 없음");
        return null;
    }
    static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "scenarios"))) return d.FullName;
        throw new DirectoryNotFoundException("scenarios");
    }
    static CaseResult RunYaml(SimHost h, string yaml)
    {
        var f = Path.GetTempFileName();
        File.WriteAllText(f, yaml);
        try { return Runner.RunOne(h, f); } finally { File.Delete(f); }
    }
    const string Base = @"
id: SC-E2E
title: e2e
bus: can
duration_ms: 100
nodes:
  - { name: A, periodic: [{ id: 0x100, dlc: 1, data: [1], period_ms: 10 }] }
  - { name: B, subscribe: ['0x100'] }
expect:
";

    [Fact]
    public void AllShippedScenariosPass()
    {
        var simd = Simd(); if (simd is null) return;
        using var h = new SimHost(simd);
        foreach (var f in Directory.GetFiles(Path.Combine(Root(), "scenarios"), "*.yaml"))
        {
            var r = Runner.RunOne(h, f, skipRealtime: true);
            if (r.Status != "SKIP") Assert.True(r.Status == "PASS", $"{r.Scenario.Id}: {r.Error} {string.Join("; ", r.Results.Select(x => x.Message))}");
        }
    }

    [Fact]
    public void ImpossibleExpectationFails()  // 판정기가 항상 PASS를 내지 않는지 실제 시뮬레이터로 확인
    {
        var simd = Simd(); if (simd is null) return;
        using var h = new SimHost(simd);
        Assert.Equal("FAIL", RunYaml(h, Base + "  - { kind: count, node: B, id: 0x100, min: 500 }").Status);
        Assert.Equal("FAIL", RunYaml(h, Base + "  - { kind: count, node: B, id: 0x100, max: 0 }").Status);
    }

    [Fact]
    public void InvalidFrameIsErrorNotFail()  // 도구/입력 문제는 FAIL이 아니라 ERROR
    {
        var simd = Simd(); if (simd is null) return;
        using var h = new SimHost(simd);
        var r = RunYaml(h, Base.Replace("id: 0x100, dlc: 1", "id: 0x800, dlc: 1") + "  - { kind: count, node: B, id: 0x800, min: 1 }");
        Assert.Equal("ERROR", r.Status);
        Assert.Contains("표준 11비트", r.Error);
    }

    [Fact]
    public void SimulatorSurvivesErrorAndKeepsServing()
    {
        var simd = Simd(); if (simd is null) return;
        using var h = new SimHost(simd);
        RunYaml(h, Base.Replace("period_ms: 10", "dlc2: 1") + "  - { kind: count, node: B, id: 0x100, min: 1 }"); // YAML 오류
        Assert.Equal("PASS", RunYaml(h, Base + "  - { kind: count, node: B, id: 0x100, min: 5 }").Status);
    }
}

// 시뮬레이터 폭주·재시작 통합 테스트
public class E2eRobustnessTests
{
    static string? Simd() => typeof(E2eTests).GetMethod("Simd", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, null) as string;
    static CaseResult RunYaml(SimHost h, string yaml)
    {
        var f = Path.GetTempFileName();
        File.WriteAllText(f, yaml);
        try { return Runner.RunOne(h, f); } finally { File.Delete(f); }
    }
    const string Ok = @"
id: SC-OK
bus: can
duration_ms: 100
nodes:
  - { name: A, periodic: [{ id: 0x100, dlc: 1, data: [1], period_ms: 10 }] }
  - { name: B, subscribe: ['0x100'] }
expect:
  - { kind: count, node: B, id: 0x100, min: 10, max: 10 }
";

    [Fact]
    public void ZeroDelayResponseLoopIsErrorAndNextScenarioStillRuns()
    {
        var simd = Simd(); if (simd is null) return;
        using var h = new SimHost(simd);
        var loop = RunYaml(h, @"
id: SC-LOOP
bus: can
duration_ms: 100
nodes:
  - { name: A, periodic: [{ id: 0x100, dlc: 0, period_ms: 10 }], respond: [{ on: 0x101, id: 0x100, dlc: 0, delay_ms: 0 }] }
  - { name: B, respond: [{ on: 0x100, id: 0x101, dlc: 0, delay_ms: 0 }] }
expect:
  - { kind: count, node: B, id: 0x100, min: 1 }
");
        Assert.Equal("ERROR", loop.Status);
        Assert.Contains("event limit", loop.Error);
        Assert.Equal("PASS", RunYaml(h, Ok).Status);
    }

    [Fact]
    public void RestartedHostServesNextScenario()
    {
        var simd = Simd(); if (simd is null) return;
        using var h = new SimHost(simd);
        h.Restart();
        Assert.Equal("PASS", RunYaml(h, Ok).Status);
        Assert.Equal(1, h.Restarts);
    }

    [Fact]
    public void ExtendedAndStandardSameIdAreSeparate()
    {
        var simd = Simd(); if (simd is null) return;
        using var h = new SimHost(simd);
        var r = RunYaml(h, @"
id: SC-EXT
bus: can
duration_ms: 100
nodes:
  - { name: A, periodic: [{ id: 0x100, dlc: 0, period_ms: 10 }] }
  - { name: C, periodic: [{ id: 0x100, ext: true, dlc: 0, period_ms: 20 }] }
  - { name: B, subscribe: ['0x100', 'ext:0x100'] }
expect:
  - { kind: count, node: B, id: 0x100, min: 10, max: 10 }
  - { kind: count, node: B, id: 0x100, ext: true, min: 5, max: 5 }
");
        Assert.True(r.Status == "PASS", string.Join("; ", r.Results.Select(x => x.Message)) + r.Error);
    }

    [Fact]
    public void BroadcastBitAppearsInNextStatusWord()
    {
        var simd = Simd(); if (simd is null) return;
        using var h = new SimHost(simd);
        var r = RunYaml(h, @"
id: SC-BC
bus: m1553
duration_ms: 10
nodes:
  - name: BC
    schedule:
      - { rt: 31, dir: bc_to_rt, sa: 1, words: [1], period_ms: 100 }
      - { rt: 1, dir: bc_to_rt, sa: 2, words: [2], period_ms: 100, offset_ms: 5 }
  - { name: RT1, rt: 1 }
expect:
  - { kind: status_word, node: BC, rt: 1, tr: 0, sa: 2, bits: { broadcast_received: true } }
");
        Assert.True(r.Status == "PASS", string.Join("; ", r.Results.Select(x => x.Message)) + r.Error);
    }
}
