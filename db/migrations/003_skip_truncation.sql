-- 0.2.0: 건너뛴 시나리오(SKIP) 기록, 트래픽 저장 잘림 여부, 확장 ID 키

ALTER TABLE cases DROP CONSTRAINT IF EXISTS cases_status_check;
ALTER TABLE cases ADD CONSTRAINT cases_status_check CHECK (status IN ('PASS','FAIL','ERROR','SKIP'));

ALTER TABLE runs ADD COLUMN skipped INT;

-- 전체 트래픽 건수와 DB에 저장한 건수. trace_stored < trace_total 이면 messages가 잘렸다 (전체는 reports/traces)
ALTER TABLE cases ADD COLUMN trace_total INT, ADD COLUMN trace_stored INT;

COMMENT ON COLUMN messages.msg_key IS
  'CAN: 식별자, 확장(29비트) ID는 0x80000000 비트를 붙인다 (0.2.0부터). 1553B: (RT<<10)|(T/R<<9)|서브주소';
COMMENT ON COLUMN cases.trace_stored IS 'messages에 저장한 건수 (케이스당 상한 5000)';
