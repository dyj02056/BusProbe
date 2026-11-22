-- BusProbe 초기 스키마 (Neon / PostgreSQL)

-- 시험 실행 1회
CREATE TABLE runs (
  id           BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  started_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
  finished_at  TIMESTAMPTZ,
  git_sha      TEXT,
  tool_version TEXT NOT NULL,
  total INT, passed INT, failed INT, errored INT
);

-- 요구사항 (docs/requirements.yaml 에서 upsert)
CREATE TABLE requirements (
  req_id TEXT PRIMARY KEY,
  title  TEXT NOT NULL,
  source TEXT
);

-- 실행 안의 시나리오 1건
CREATE TABLE cases (
  id            BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  run_id        BIGINT NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
  scenario_id   TEXT NOT NULL,
  title         TEXT NOT NULL,
  bus           TEXT NOT NULL,
  time_mode     TEXT NOT NULL CHECK (time_mode IN ('virtual','realtime')),
  status        TEXT NOT NULL CHECK (status IN ('PASS','FAIL','ERROR')),
  duration_ms   INT,
  error         TEXT,
  scenario_yaml TEXT NOT NULL,
  UNIQUE (run_id, scenario_id)
);

CREATE TABLE case_requirements (
  case_id BIGINT REFERENCES cases(id) ON DELETE CASCADE,
  req_id  TEXT   REFERENCES requirements(req_id),
  PRIMARY KEY (case_id, req_id)
);

-- 기대값 1건당 1행
CREATE TABLE results (
  id       BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  case_id  BIGINT NOT NULL REFERENCES cases(id) ON DELETE CASCADE,
  idx      INT NOT NULL,
  kind     TEXT NOT NULL,
  status   TEXT NOT NULL CHECK (status IN ('PASS','FAIL','ERROR')),
  expected JSONB NOT NULL,
  actual   JSONB,
  message  TEXT
);

-- 버스 트래픽 로그 (케이스당 상한 있음, 전체는 로컬 reports/ 에 보관)
CREATE TABLE messages (
  id      BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  case_id BIGINT NOT NULL REFERENCES cases(id) ON DELETE CASCADE,
  seq     INT    NOT NULL,
  ts_ns   BIGINT NOT NULL,   -- 시뮬레이션 시작 기준 경과 ns
  bus     TEXT   NOT NULL,
  dir     TEXT   NOT NULL CHECK (dir IN ('tx','rx','to')),
  node    TEXT   NOT NULL,
  msg_key BIGINT NOT NULL,   -- CAN: 식별자, 1553B: (RT<<10)|(T/R<<9)|서브주소
  raw     BYTEA  NOT NULL
);

CREATE INDEX cases_run_idx ON cases(run_id);
CREATE INDEX results_case_idx ON results(case_id);
CREATE INDEX messages_case_idx ON messages(case_id, node, msg_key, ts_ns);
