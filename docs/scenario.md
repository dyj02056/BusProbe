# 시나리오 YAML 문법

```yaml
id: SC-CAN-004                # 영문/숫자/-/_
title: 요청에서 응답까지 지연이 3ms 이내
requirements: [REQ-CAN-004]   # docs/requirements.yaml 의 ID
bus: can                      # can | m1553
time_mode: virtual            # virtual(기본) | realtime
duration_ms: 1000             # 1 ~ 3600000
nodes: [...]
expect: [...]
```

모르는 키는 오류다 (오타 검출). `0x100` 같은 16진 표기를 쓸 수 있다.
모든 값은 시뮬레이터에 보내기 전에 범위를 검사하며, 범위를 벗어나면 그 시나리오는 ERROR가 된다.

## nodes

CAN 노드: `periodic`(`id, ext, dlc, data, period_ms, offset_ms`), `subscribe`(`['0x100']`, `['ext:0x100']` 또는 `['*']`),
`respond`(`on, on_ext, id, ext, dlc, data, delay_ms`).

1553B 노드: RT는 `rt: 주소`, `silent`, `status`(`message_error, service_request, broadcast_received, busy, subsystem_flag, terminal_flag`),
`tx_words`. BC는 `schedule`(`rt, dir: bc_to_rt|rt_to_bc, sa, wc, words, period_ms, offset_ms`).

### 값 범위

| 필드 | 범위 |
| --- | --- |
| CAN `id` | `ext: false`(기본) 0~0x7FF, `ext: true` 0~0x1FFFFFFF |
| `dlc` | 0~8 |
| `data` | 각 0~0xFF, 개수 ≤ `dlc` |
| `period_ms` | 1~3600000 |
| `offset_ms`, `delay_ms` | 0~3600000 |
| RT 노드 `rt` | 0~30, 시나리오 안에서 중복 불가 (31은 방송 주소) |
| `schedule.rt`, `sa` | 0~31 |
| `wc` | 1~32. `bc_to_rt`는 `words` 개수와 같아야 하고, 생략하면 `words` 개수 |
| `words`, `tx_words` | 각 0~0xFFFF, 32개 이하. `rt_to_bc`에는 `words`를 쓰지 않는다 (RT의 `tx_words`가 응답) |

### 표준 ID와 확장 ID

같은 값이라도 표준 ID와 확장(29비트) ID는 다른 메시지로 구분한다.
`subscribe`, `respond.on`, expect의 `id`/`to_id`는 `ext`를 생략하면 **0x7FF보다 큰 값만 확장 ID**로 본다.
0x7FF 이하인 확장 ID는 `ext:0x100`(subscribe), `on_ext: true`(respond), `ext: true`/`to_ext: true`(expect)로 명시한다.

### 주의

- 지연 0인 응답끼리 서로를 부르면(A가 B에, B가 A에 `delay_ms: 0`으로 응답) 같은 시각에 끝없이 이어진다.
  시뮬레이터는 같은 시각 이벤트 10000건 또는 전체 이벤트 1000000건을 넘으면 멈추고 그 시나리오를 ERROR로 만든다.

## expect

공통: `node`, `dir`(기본 `rx`, `tx|rx|to`), 식별자(CAN은 `id`, `ext`, 1553B는 `rt, tr, sa`), 구간 `from_ms`, `to_ms`(`from_ms < to_ms`).

| kind | 필드 | 통과 조건 |
| --- | --- | --- |
| `count` | `min`, `max` | 구간 내 개수가 범위 안. `max: 0`이면 나오면 안 됨 |
| `period` | `mean_ms`, `tolerance_ms`, `jitter_p99_ms` | 평균 주기와 지터 p99 |
| `latency` | `to_node`, `to_id`, `to_ext`, `p95_ms` | 요청 송신과 응답 수신을 1:1로 짝지은 지연 (CAN만) |
| `signal` | `start_bit`, `length`, `factor`, `offset`, `lo`, `hi` | 모든 프레임의 물리값이 범위 안 (CAN만, `start_bit + length ≤ 64`) |
| `status_word` | `bits: {busy: false, ...}` | BC 노드가 받은 첫 응답 상태 워드의 비트 (1553B만, `dir: rx`) |

`latency`: 구간(`from_ms`~`to_ms`)에 든 요청마다, 그 요청 시각부터 다음 요청 시각까지 도착한 응답 중 아직 쓰지 않은 첫 응답과 짝짓는다.
응답 하나를 여러 요청에 쓰지 않으므로, 짝이 없는 요청이 하나라도 있으면 FAIL이다.

`signal`: 프레임의 DLC 밖 비트를 읽게 되면 시험 대상이 아니라 시나리오 문제이므로 ERROR다.

## 판정과 종료 코드

| 상태 | 뜻 |
| --- | --- |
| PASS | 모든 기대값 통과 |
| FAIL | 기대값 하나 이상 불합격 (시험 대상의 문제) |
| ERROR | 시나리오 형식·범위 오류, 시뮬레이터 오류 (도구나 입력의 문제) |
| SKIP | `--no-realtime`으로 건너뛴 실시간 시나리오. 결과서와 추적표에 남는다 |

종료 코드: 오류가 있으면 2, 아니면 실패가 있으면 1, 모두 통과(SKIP 포함)면 0.
