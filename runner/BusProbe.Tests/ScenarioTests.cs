using BusProbe;
using Xunit;

namespace BusProbe.Tests;

// 시나리오 파서·검증·설정 생성 테스트
public class ScenarioTests
{
    const string Ok = @"
id: SC-T-1
title: t
requirements: [REQ-1]
bus: can
duration_ms: 100
nodes:
  - name: A
    periodic: [{ id: 0x100, dlc: 2, data: [0x01, 0xFF], period_ms: 10 }]
  - name: B
    subscribe: ['0x100']
expect:
  - { kind: count, node: B, id: 0x100, min: 5 }
";

    [Fact]
    public void Parse_AcceptsHexIds()
    {
        var s = ScenarioLoader.Parse(Ok);
        Assert.Equal("SC-T-1", s.Id);
        Assert.Equal(0x100, ScenarioLoader.Num(s.Nodes[0].Periodic[0].Id));
    }

    [Fact]
    public void BuildConfig_ProducesSimdLines()
    {
        var cfg = ScenarioLoader.BuildConfig(ScenarioLoader.Parse(Ok));
        Assert.Contains("can_tx A 256 0 10 0 2 01ff\n", cfg);
        Assert.Contains("can_sub B 256\n", cfg);
        Assert.Contains("duration_ms 100\n", cfg);
    }

    [Theory]
    [InlineData("typo_field: 1")]                                  // 모르는 키
    [InlineData("duration_ms: 0")]                                 // 지속 시간 0
    [InlineData("bus: lin")]                                       // 지원하지 않는 버스
    public void Parse_RejectsBadTopLevel(string patch)
    {
        var key = patch.Split(':')[0];
        var yaml = string.Join('\n', Ok.Split('\n').Where(l => !l.StartsWith(key + ":"))) + "\n" + patch;
        Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(yaml));
    }

    [Fact]
    public void Parse_RejectsNodeNameWithSpace() =>
        Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(Ok.Replace("name: A", "name: 'A B'")));

    [Fact]
    public void Parse_RejectsExpectWithUnknownNode() =>
        Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(Ok.Replace("node: B, id", "node: Z, id")));

    [Fact]
    public void Parse_RejectsPeriodWithoutMean() =>
        Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(Ok.Replace("kind: count", "kind: period")));

    [Fact]
    public void Parse_RejectsBadHex() =>
        Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(Ok.Replace("0x01", "0xZZ")));

    [Fact]
    public void BuildConfig_1553_EncodesFlagsAndWords()
    {
        var s = ScenarioLoader.Parse(@"
id: SC-M
bus: m1553
duration_ms: 20
nodes:
  - { name: BC, schedule: [{ rt: 5, dir: bc_to_rt, sa: 3, words: [0x1111, 0x2222], period_ms: 10 }] }
  - { name: RT5, rt: 5, status: [busy] }
expect:
  - { kind: status_word, node: BC, rt: 5, tr: 0, sa: 3, bits: { busy: true } }
");
        var cfg = ScenarioLoader.BuildConfig(s);
        Assert.Contains("m_bc BC 5 0 3 2 10 0 11112222\n", cfg);
        Assert.Contains("m_rt RT5 5 0 8 -\n", cfg);
    }
}
