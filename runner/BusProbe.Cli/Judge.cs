namespace BusProbe;

// 기대값 1건의 판정 결과. Status: PASS | FAIL | ERROR
public record Result(string Kind, string Status, object Expected, object? Actual, string Message);

// 트레이스를 기대값과 비교한다. 시뮬레이터는 판정하지 않고 여기서만 판정한다.
public static class Judge
{
    const double NsPerMs = 1e6;

    // expect 목록을 순서대로 평가. 평가 중 예외는 그 항목만 ERROR로 둔다
    public static List<Result> Evaluate(Scenario s, List<Ev> trace) =>
        s.Expect.Select(e =>
        {
            try { return Eval(s, e, trace); }
            catch (Exception ex) { return new Result(e.Kind, "ERROR", new { }, null, ex.Message); }
        }).ToList();

    // 케이스 판정: 하나라도 ERROR면 ERROR, 아니면 하나라도 FAIL이면 FAIL
    public static string Verdict(IEnumerable<Result> rs)
    {
        var l = rs.ToList();
        return l.Any(r => r.Status == "ERROR") ? "ERROR" : l.Any(r => r.Status == "FAIL") ? "FAIL" : "PASS";
    }

    // CAN은 식별자, 1553B는 (RT<<10)|(T/R<<9)|서브주소 (시뮬레이터 key와 동일)
    static long Key(Scenario s, string? canId, Expect e) =>
        s.Bus == "can" ? ScenarioLoader.Num(canId!) : ((long)e.Rt! << 10) | ((long)e.Tr! << 9) | (long)e.Sa!;

    static IEnumerable<Ev> Match(Scenario s, Expect e, List<Ev> t)
    {
        long key = Key(s, e.Id, e);
        long from = (e.FromMs ?? 0) * 1_000_000, to = e.ToMs is null ? long.MaxValue : e.ToMs.Value * 1_000_000;
        return t.Where(x => x.Node == e.Node && x.Dir == e.Dir && x.Key == key && x.TsNs >= from && x.TsNs < to);
    }

    static Result Done(string kind, bool ok, object expected, object actual, string failMsg) =>
        new(kind, ok ? "PASS" : "FAIL", expected, actual, ok ? "" : failMsg);

    // 가장 가까운 순위(nearest-rank) 백분위
    static double Percentile(List<double> v, double p)
    {
        var a = v.OrderBy(x => x).ToList();
        return a[Math.Max(0, (int)Math.Ceiling(p * a.Count) - 1)];
    }

    static Result Eval(Scenario s, Expect e, List<Ev> t) => e.Kind switch
    {
        "count" => Count(s, e, t),
        "period" => Period(s, e, t),
        "latency" => Latency(s, e, t),
        "signal" => Signal(s, e, t),
        "status_word" => StatusWord(s, e, t),
        _ => throw new InvalidOperationException("unknown kind " + e.Kind),
    };

    // 구간 내 개수가 min~max 안인가 (max: 0이면 "나오면 안 됨" 시험)
    static Result Count(Scenario s, Expect e, List<Ev> t)
    {
        int n = Match(s, e, t).Count(), min = e.Min ?? 0, max = e.Max ?? int.MaxValue;
        return Done("count", n >= min && n <= max, new { min, max = e.Max }, new { count = n }, $"개수 {n}가 [{min}, {e.Max?.ToString() ?? "∞"}] 밖");
    }

    // 평균 주기 ± 허용오차, 지터(|dt-평균|)의 p99 한계
    static Result Period(Scenario s, Expect e, List<Ev> t)
    {
        var ts = Match(s, e, t).Select(x => x.TsNs).OrderBy(x => x).ToList();
        var exp = new { mean_ms = e.MeanMs, tolerance_ms = e.ToleranceMs ?? 0, jitter_p99_ms = e.JitterP99Ms };
        if (ts.Count < 2) return Done("period", false, exp, new { count = ts.Count }, "주기 계산에는 이벤트 2개 이상 필요");
        var dt = ts.Zip(ts.Skip(1), (a, b) => (b - a) / NsPerMs).ToList();
        double mean = dt.Average(), p99 = Percentile(dt.Select(d => Math.Abs(d - mean)).ToList(), 0.99);
        bool ok = Math.Abs(mean - e.MeanMs!.Value) <= (e.ToleranceMs ?? 0) && (e.JitterP99Ms is null || p99 <= e.JitterP99Ms);
        return Done("period", ok, exp, new { mean_ms = Math.Round(mean, 3), jitter_p99_ms = Math.Round(p99, 3), n = ts.Count },
            $"평균 {mean:F3}ms, 지터 p99 {p99:F3}ms가 기준 밖");
    }

    // 송신(node,id) 이후 처음 도착한 수신(to_node,to_id)까지의 지연. 응답이 없으면 실패
    static Result Latency(Scenario s, Expect e, List<Ev> t)
    {
        long sk = ScenarioLoader.Num(e.Id!), ek = ScenarioLoader.Num(e.ToId!);
        var starts = t.Where(x => x.Node == e.Node && x.Dir == "tx" && x.Key == sk).ToList();
        var ends = t.Where(x => x.Node == e.ToNode && x.Dir == "rx" && x.Key == ek).ToList();
        var exp = new { p95_ms = e.P95Ms };
        if (starts.Count == 0) return Done("latency", false, exp, new { starts = 0 }, "시작 송신 이벤트 없음");
        var lat = new List<double>();
        int missing = 0;
        foreach (var a in starts)
        {
            var b = ends.FirstOrDefault(x => x.TsNs >= a.TsNs);
            if (b is null) missing++; else lat.Add((b.TsNs - a.TsNs) / NsPerMs);
        }
        if (missing > 0) return Done("latency", false, exp, new { missing }, $"응답이 없는 요청 {missing}건");
        double p95 = Percentile(lat, 0.95);
        return Done("latency", p95 <= e.P95Ms, exp, new { p95_ms = Math.Round(p95, 3), max_ms = lat.Max(), n = lat.Count }, $"지연 p95 {p95:F3}ms가 한계 초과");
    }

    // 신호 추출(인텔 바이트 순서, 64비트 이하). 모든 프레임의 물리값이 [lo, hi] 안이어야 통과
    // shortcut: 모토로라(빅엔디언) 순서 미지원, 필요해지면 start_bit 해석을 추가
    static Result Signal(Scenario s, Expect e, List<Ev> t)
    {
        var evs = Match(s, e, t).ToList();
        var exp = new { lo = e.Lo, hi = e.Hi };
        if (evs.Count == 0) return Done("signal", false, exp, new { count = 0 }, "대상 프레임 없음");
        double factor = e.Factor ?? 1, offset = e.Offset ?? 0, lo = double.MaxValue, hi = double.MinValue;
        foreach (var ev in evs)
        {
            int dlc = ev.Raw[4];
            ulong v = 0;
            for (int i = 0; i < e.Length!.Value; i++)
            {
                int bit = e.StartBit!.Value + i, by = bit / 8;
                if (by >= dlc) return Done("signal", false, exp, new { dlc }, $"신호가 DLC({dlc}) 밖의 비트를 사용");
                v |= (ulong)((ev.Raw[8 + by] >> (bit % 8)) & 1) << i;
            }
            double phys = v * factor + offset;
            lo = Math.Min(lo, phys); hi = Math.Max(hi, phys);
        }
        return Done("signal", lo >= e.Lo && hi <= e.Hi, exp, new { min = lo, max = hi, n = evs.Count }, $"물리값 [{lo}, {hi}]가 범위 밖");
    }

    // 첫 응답의 상태 워드 비트 비교
    static Result StatusWord(Scenario s, Expect e, List<Ev> t)
    {
        var ev = Match(s, e, t).FirstOrDefault();
        var exp = e.Bits!;
        if (ev is null) return Done("status_word", false, exp, new { }, "상태 워드를 받지 못함");
        uint w = BitConverter.ToUInt32(ev.Raw, 0);
        int data = (int)((w >> 1) & 0xFFFF);
        var actual = ScenarioLoader.StatusBits.ToDictionary(k => k.Key, k => (data & k.Value) != 0);
        var bad = e.Bits!.Where(b => actual[b.Key] != b.Value).Select(b => b.Key).ToList();
        return Done("status_word", bad.Count == 0, exp, actual, "불일치 비트: " + string.Join(", ", bad));
    }
}
