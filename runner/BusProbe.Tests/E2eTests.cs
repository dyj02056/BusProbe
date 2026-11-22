using BusProbe;
using Xunit;

namespace BusProbe.Tests;

// 실제 busprobe-simd 를 띄우는 통합 테스트. 빌드된 시뮬레이터가 없으면 건너뛴다 (CI는 먼저 빌드함)
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
        try { return Runner.RunOne(h, f)!; } finally { File.Delete(f); }
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
            if (r is not null) Assert.True(r.Status == "PASS", $"{r.Scenario.Id}: {r.Error} {string.Join("; ", r.Results.Select(x => x.Message))}");
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
        Assert.Contains("invalid CAN frame", r.Error);
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
