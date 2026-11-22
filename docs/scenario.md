# 시나리오 YAML 문법

```yaml
id: SC-CAN-004                # 영문/숫자/-/_
title: 요청에서 응답까지 지연이 3ms 이내
requirements: [REQ-CAN-004]   # docs/requirements.yaml 의 ID
bus: can                      # can | m1553
time_mode: virtual            # virtual(기본) | realtime
duration_ms: 1000
nodes: [...]
expect: [...]
```

모르는 키는 오류다 (오타 검출). `0x100` 같은 16진 표기를 쓸 수 있다.

## nodes

CAN 노드: `periodic`(`id, ext, dlc, data, period_ms, offset_ms`), `subscribe`(`['0x100']` 또는 `['*']`),
`respond`(`on, id, ext, dlc, data, delay_ms`).

1553B 노드: RT는 `rt: 주소`, `silent`, `status`(`message_error, service_request, broadcast_received, busy, subsystem_flag, terminal_flag`),
`tx_words`. BC는 `schedule`(`rt, dir: bc_to_rt|rt_to_bc, sa, wc, words, period_ms, offset_ms`).

## expect

공통: `node`, `dir`(기본 `rx`, `tx|rx|to`), 식별자(CAN은 `id`, 1553B는 `rt, tr, sa`), 구간 `from_ms`, `to_ms`.

| kind | 필드 | 통과 조건 |
| --- | --- | --- |
| `count` | `min`, `max` | 구간 내 개수가 범위 안. `max: 0`이면 나오면 안 됨 |
| `period` | `mean_ms`, `tolerance_ms`, `jitter_p99_ms` | 평균 주기와 지터 p99 |
| `latency` | `to_node`, `to_id`, `p95_ms` | 송신 이후 첫 수신까지 지연 (CAN만) |
| `signal` | `start_bit`, `length`, `factor`, `offset`, `lo`, `hi` | 모든 프레임의 물리값이 범위 안 (CAN만) |
| `status_word` | `bits: {busy: false, ...}` | 첫 응답 상태 워드의 비트 (1553B만) |

판정: 하나라도 FAIL이면 케이스 FAIL, 시뮬레이터/입력 오류는 ERROR. 종료 코드는 통과 0, 실패 1, 오류 2.
