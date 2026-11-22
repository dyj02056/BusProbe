using BusProbe;
using Xunit;

namespace BusProbe.Tests;

// 판정기 테스트: 손으로 만든 트레이스로 PASS/FAIL/ERROR를 각각 확인
public class JudgeTests
{
    static Scenario Sc(params Expect[] e) => new() { Id = "T", Bus = "can", DurationMs = 100, Nodes = { new() { Name = "A" }, new() { Name = "B" } }, Expect = e.ToList() };
    static Ev Rx(long ms, string node = "B", long key = 0x100, byte[]? raw = null) => new(ms * 1_000_000, "rx", node, "can", key, raw ?? new byte[16]);
    static Ev Tx(long ms, string node, long key) => new(ms * 1_000_000, "tx", node, "can", key, new byte[16]);
    static List<Ev> Every(int periodMs, int n) => Enumerable.Range(0, n).Select(i => Rx(i * periodMs)).ToList();
    static string Status(Scenario s, List<Ev> t) => Judge.Evaluate(s, t)[0].Status;

    [Fact]
    public void Count_PassesInsideRange_FailsOutside()
    {
        var s = Sc(new Expect { Kind = "count", Node = "B", Id = "0x100", Min = 3, Max = 5 });
        Assert.Equal("PASS", Status(s, Every(10, 4)));
        Assert.Equal("FAIL", Status(s, Every(10, 2)));
        Assert.Equal("FAIL", Status(s, Every(10, 6)));
    }

    [Fact]
    public void Count_MaxZeroDetectsUnexpectedMessage()
    {
        var s = Sc(new Expect { Kind = "count", Node = "B", Id = "0x100", Max = 0, FromMs = 50 });
        Assert.Equal("PASS", Status(s, Every(10, 5)));   // 0~40ms만 있음 → 구간 밖
        Assert.Equal("FAIL", Status(s, Every(10, 8)));
    }

    [Fact]
    public void Period_ChecksMeanAndJitter()
    {
        var e = new Expect { Kind = "period", Node = "B", Id = "0x100", MeanMs = 10, ToleranceMs = 1, JitterP99Ms = 2 };
        Assert.Equal("PASS", Status(Sc(e), Every(10, 10)));
        Assert.Equal("FAIL", Status(Sc(e), Every(20, 10)));                                  // 평균 불일치
        var jittery = new List<Ev> { Rx(0), Rx(10), Rx(20), Rx(36), Rx(40), Rx(50) };       // 평균 ≈ 10인데 지터 큼
        Assert.Equal("FAIL", Status(Sc(e), jittery));
        Assert.Equal("FAIL", Status(Sc(e), Every(10, 1)));                                   // 이벤트 1개
    }

    [Fact]
    public void Latency_UsesFirstResponseAndFailsWhenMissing()
    {
        var e = new Expect { Kind = "latency", Node = "A", Id = "0x200", ToNode = "A", ToId = "0x201", P95Ms = 3 };
        var good = new List<Ev> { Tx(0, "A", 0x200), Rx(2, "A", 0x201), Tx(50, "A", 0x200), Rx(52, "A", 0x201) };
        Assert.Equal("PASS", Status(Sc(e), good));
        var slow = new List<Ev> { Tx(0, "A", 0x200), Rx(10, "A", 0x201) };
        Assert.Equal("FAIL", Status(Sc(e), slow));
        var lost = new List<Ev> { Tx(0, "A", 0x200) };
        Assert.Equal("FAIL", Status(Sc(e), lost));
    }

    [Fact]
    public void Signal_AppliesFactorAndOffset()
    {
        var raw = new byte[16]; raw[4] = 2; raw[8] = 100; // dlc 2, 바이트0 = 100
        var e = new Expect { Kind = "signal", Node = "B", Id = "0x100", StartBit = 0, Length = 8, Factor = 0.5, Offset = -10, Lo = 39, Hi = 41 }; // 100*0.5-10 = 40
        Assert.Equal("PASS", Status(Sc(e), new() { Rx(0, raw: raw) }));
        e.Hi = 39.5;
        Assert.Equal("FAIL", Status(Sc(e), new() { Rx(0, raw: raw) }));
        e.StartBit = 16;  // DLC 밖
        Assert.Equal("FAIL", Status(Sc(e), new() { Rx(0, raw: raw) }));
    }

    [Fact]
    public void StatusWord_ComparesBits()
    {
        var s = new Scenario { Id = "M", Bus = "m1553", DurationMs = 10, Nodes = { new() { Name = "BC" } } };
        s.Expect.Add(new Expect { Kind = "status_word", Node = "BC", Rt = 5, Tr = 0, Sa = 3, Bits = new() { ["busy"] = true, ["message_error"] = false } });
        uint word = (4u << 17) | ((uint)(5 << 11 | 1 << 3) << 1); // 패리티는 판정기가 보지 않음
        var raw = BitConverter.GetBytes(word);
        long key = (5L << 10) | 3;
        Assert.Equal("PASS", Status(s, new() { new Ev(0, "rx", "BC", "m1553", key, raw) }));
        s.Expect[0].Bits!["busy"] = false;
        Assert.Equal("FAIL", Status(s, new() { new Ev(0, "rx", "BC", "m1553", key, raw) }));
        Assert.Equal("FAIL", Status(s, new()));  // 응답 없음
    }

    [Fact]
    public void Verdict_ErrorBeatsFailBeatsPass()
    {
        Result R(string st) => new("count", st, new { }, null, "");
        Assert.Equal("PASS", Judge.Verdict(new[] { R("PASS") }));
        Assert.Equal("FAIL", Judge.Verdict(new[] { R("PASS"), R("FAIL") }));
        Assert.Equal("ERROR", Judge.Verdict(new[] { R("FAIL"), R("ERROR") }));
    }

    [Fact]
    public void Evaluate_TurnsExceptionIntoError()
    {
        var s = Sc(new Expect { Kind = "count", Node = "B", Id = "0xZZ" });  // 검증을 우회한 잘못된 값
        Assert.Equal("ERROR", Status(s, new()));
    }

    // 판정기 자체가 항상 PASS를 내지 않는지 확인하는 메타 시험은 위 FAIL 케이스들이 담당
}
