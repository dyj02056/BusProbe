-- 수신 메시지 주기 통계 (윈도우 함수). 판정은 C#에서 하고, 이 뷰는 조회·교차검증용.
CREATE VIEW v_period_stats AS
WITH d AS (
  SELECT case_id, node, msg_key,
         ts_ns - lag(ts_ns) OVER (PARTITION BY case_id, node, msg_key ORDER BY ts_ns) AS dt
  FROM messages WHERE dir = 'rx'
), m AS (
  SELECT case_id, node, msg_key, avg(dt) AS mean_dt
  FROM d WHERE dt IS NOT NULL GROUP BY case_id, node, msg_key
)
SELECT d.case_id, d.node, d.msg_key,
       count(*) + 1                         AS n,
       (m.mean_dt / 1e6)::double precision  AS mean_ms,
       max(d.dt) / 1e6                      AS max_ms,
       percentile_cont(0.99) WITHIN GROUP (ORDER BY abs(d.dt - m.mean_dt)::double precision) / 1e6 AS jitter_p99_ms
FROM d JOIN m USING (case_id, node, msg_key)
WHERE d.dt IS NOT NULL
GROUP BY d.case_id, d.node, d.msg_key, m.mean_dt;
