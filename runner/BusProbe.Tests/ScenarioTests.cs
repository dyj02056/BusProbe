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

// 값 범위와 참조 검증 (잘못된 값이 조용히 다른 바이트로 바뀌지 않게)
public class ScenarioRangeTests
{
    const string Can = @"
id: SC-R
bus: can
duration_ms: 100
nodes:
  - name: A
    periodic: [{ id: 0x100, dlc: 2, data: [0x01, 0x02], period_ms: 10 }]
  - name: B
    subscribe: ['0x100']
expect:
  - { kind: count, node: B, id: 0x100, min: 1 }
";
    const string M = @"
id: SC-M
bus: m1553
duration_ms: 20
nodes:
  - { name: BC, schedule: [{ rt: 5, dir: bc_to_rt, sa: 3, words: [0x1111], period_ms: 10 }] }
  - { name: RT5, rt: 5 }
expect:
  - { kind: status_word, node: BC, rt: 5, tr: 0, sa: 3, bits: { busy: false } }
";

    [Theory]
    [InlineData("data: [0x01, 0x02]", "data: [0x100, 0x100]")]          // 바이트 범위 초과 (이전에는 '100100'으로 밀림)
    [InlineData("data: [0x01, 0x02]", "data: [-1, 0x02]")]              // 음수
    [InlineData("dlc: 2, data: [0x01, 0x02]", "dlc: 1, data: [0x01, 0x02]")] // dlc보다 많은 데이터
    [InlineData("dlc: 2", "dlc: 9")]
    [InlineData("id: 0x100, dlc", "id: 0x800, dlc")]                    // 표준 ID 범위 초과
    [InlineData("period_ms: 10", "period_ms: -5")]
    [InlineData("duration_ms: 100", "duration_ms: 3600001")]
    [InlineData("min: 1 }", "min: 5, max: 2 }")]
    [InlineData("min: 1 }", "min: 1, from_ms: 50, to_ms: 10 }")]
    [InlineData("subscribe: ['0x100']", "subscribe: ['0x2000000000']")]
    public void Can_RejectsOutOfRange(string from, string to)
    {
        Assert.Contains(from, Can);
        Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(Can.Replace(from, to)));
    }

    [Theory]
    [InlineData("words: [0x1111]", "words: [0x10000]")]                 // 워드 범위 초과
    [InlineData("words: [0x1111]", "words: [0x1111], wc: 2")]           // words 개수 != wc
    [InlineData("{ name: RT5, rt: 5 }", "{ name: RT5, rt: 31 }")]       // RT 주소 31은 방송
    [InlineData("{ name: RT5, rt: 5 }", "{ name: RT5, rt: 5 }\n  - { name: RT5b, rt: 5 }")] // RT 주소 중복
    [InlineData("sa: 3, words", "sa: 32, words")]
    [InlineData("node: BC, rt: 5", "node: RT5, rt: 5")]                 // status_word는 BC에서만
    [InlineData("kind: status_word, node: BC,", "kind: status_word, node: BC, dir: tx,")]
    public void M1553_RejectsOutOfRange(string from, string to)
    {
        Assert.Contains(from, M);
        Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(M.Replace(from, to).Replace("\\n", "\n")));
    }

    [Fact]
    public void Signal_RejectsBitsBeyond64() =>
        Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(Can.Replace("{ kind: count, node: B, id: 0x100, min: 1 }",
            "{ kind: signal, node: B, id: 0x100, start_bit: 60, length: 8, lo: 0, hi: 1 }")));

    [Fact]
    public void CanKey_InfersExtendedAboveElevenBits()
    {
        Assert.Equal(0x100, ScenarioLoader.CanKey("0x100", null));
        Assert.Equal(0x100 | 0x80000000L, ScenarioLoader.CanKey("0x100", true));
        Assert.Equal(0x1ABCDE | 0x80000000L, ScenarioLoader.CanKey("0x1ABCDE", null));
        Assert.Equal(0x100 | 0x80000000L, ScenarioLoader.SubKey("ext:0x100"));
        Assert.Equal(-1, ScenarioLoader.SubKey("*"));
    }

    [Fact]
    public void BuildConfig_SendsExtendedKeysForSubscribeAndRespond()
    {
        var s = ScenarioLoader.Parse(@"
id: SC-X
bus: can
duration_ms: 100
nodes:
  - { name: A, periodic: [{ id: 0x1ABCDE, ext: true, dlc: 0, period_ms: 10 }] }
  - { name: B, subscribe: ['0x1ABCDE'], respond: [{ on: 0x1ABCDE, id: 0x101, dlc: 0 }] }
expect:
  - { kind: count, node: B, id: 0x1ABCDE, min: 1 }
");
        var cfg = ScenarioLoader.BuildConfig(s);
        long key = 0x1ABCDE | 0x80000000L;
        Assert.Contains($"can_sub B {key}\n", cfg);
        Assert.Contains($"can_rsp B {key} 257 0 0 0 -\n", cfg);
    }
}
