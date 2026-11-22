# BusProbe

CAN과 MIL-STD-1553B 형태의 메시지를 가상 버스에서 주고받고, 시나리오(YAML)의 기대값과 비교해 합격/불합격과 결과서를 자동으로 만드는 시험 도구.
전용 하드웨어가 필요 없다. 규격 범위는 [docs/assumptions.md](docs/assumptions.md).

```
시나리오(YAML) → C# 러너 → TCP(루프백) → C++ 가상 버스 → 트레이스 → 판정 → 결과서(HTML)·추적표(CSV)·Neon(Postgres)
```

## 빌드와 실행

필요: CMake, C++17 컴파일러, .NET 10 SDK. (Docker는 선택)

```powershell
cmake -S . -B build -G Ninja
cmake --build build
ctest --test-dir build --output-on-failure      # C++ 단위 테스트
dotnet test runner                               # C# 단위·통합 테스트
dotnet run --project runner/BusProbe.Cli -- run scenarios --simd build/busprobe-simd.exe --out reports
```

`reports/report.html`(결과서), `reports/traceability.csv`(요구사항 추적표), `reports/traces/*.ndjson`(전체 트래픽)이 생긴다.
시나리오 문법은 [docs/scenario.md](docs/scenario.md), 러너와 시뮬레이터 프로토콜은 [docs/protocol.md](docs/protocol.md).

## 결과서 화면

결과서는 파일 하나로 된 HTML이라 서버 없이 브라우저로 연다. 시스템 설정에 따라 밝은 화면과 어두운 화면으로 바뀐다.
아래는 저장소의 시나리오 11개를 실행해 만든 실제 결과서다.

**실행 요약.** 맨 위에 한 문장 판정과 케이스별 상태 막대가 있고, 실패·오류만 골라 보거나 ID·제목·요구사항으로 찾을 수 있다.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/images/report-overview-dark.png">
  <img alt="결과서 첫 화면: 11개 시나리오가 모두 통과했다는 판정, 케이스별 상태 막대, 시나리오 목록" src="docs/images/report-overview-light.png" width="800">
</picture>

**CAN 케이스 상세.** 케이스를 펼치면 기대값과 실제값 비교, 노드별 송수신 타임라인, 트래픽 기록이 나온다.
아래는 요청에서 응답까지의 지연을 판정하는 SC-CAN-004다.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/images/report-case-dark.png">
  <img alt="SC-CAN-004 상세: latency와 count 기대값 비교, ECU_A·ECU_B 타임라인, 트래픽 기록 표" src="docs/images/report-case-light.png" width="800">
</picture>

**1553B 케이스 상세.** BC→RT 전송에서 RT가 돌려준 상태 워드의 비트를 기대값과 비교한다.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/images/report-m1553-dark.png">
  <img alt="SC-M1553-001 상세: 상태 워드 비트 비교, BC·RT5 타임라인, 1553B 워드 트래픽" src="docs/images/report-m1553-light.png" width="800">
</picture>

**요구사항 추적.** 요구사항마다 연결된 시나리오와 그 결과를 보여 주고, 시험이 없는 요구사항은 "시험 없음"으로 남긴다.
같은 내용이 `traceability.csv`로도 나온다.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/images/report-reqs-dark.png">
  <img alt="요구사항 추적 탭: 요구사항 11개 중 10개에 시나리오가 연결되어 있고 REQ-CAN-007은 시험 없음" src="docs/images/report-reqs-light.png" width="800">
</picture>

### 종료 코드

CI에서 그대로 쓸 수 있다.

| 코드 | 뜻 |
| --- | --- |
| 0 | 모두 통과 (`--no-realtime`으로 건너뛴 SKIP 포함) |
| 1 | 실패(FAIL)가 하나 이상, 오류는 없음 |
| 2 | 오류(ERROR)가 하나 이상, 또는 명령 자체 오류 (인자, 파일, DB 연결 등) |

`--no-realtime`은 실시간 시나리오를 건너뛰고 결과서·추적표에 SKIP으로 남긴다 (CI처럼 타이머가 불안정한 환경용).

## 결과 이력을 Neon에 저장

1. Neon 콘솔에서 프로젝트를 만들고 직접 연결(non-pooler) 문자열을 복사한다.
2. 환경변수로 넣는다 (저장소에 올리지 않는다. 형식은 `.env.example`).

```powershell
$env:DATABASE_URL = "postgresql://USER:PASSWORD@HOST/DBNAME?sslmode=require"
dotnet run --project runner/BusProbe.Cli -- db migrate
dotnet run --project runner/BusProbe.Cli -- run scenarios --simd build/busprobe-simd.exe
dotnet run --project runner/BusProbe.Cli -- history
```

`DATABASE_URL`이 없거나 `--no-db`이면 DB 없이 동작한다. 저장이 실패해도 결과서는 만들어진다.
메시지는 케이스당 5000건까지만 저장하고 전체는 `reports/traces`에 남는다 (Neon 무료 저장량 보호).
잘렸는지는 `cases.trace_stored < cases.trace_total`로 알 수 있다.
URI의 `sslmode`를 따르며, 없으면 `require`다. 연결은 네트워크·타임아웃 같은 일시적 오류일 때만 최대 3회 시도한다.

0.1.0에서 올린 경우 `db migrate`를 다시 실행한다 (`003_skip_truncation.sql`: SKIP 상태, 잘림 건수 컬럼).
0.2.0부터 확장 ID 메시지의 `msg_key`에는 `0x80000000` 비트가 붙으므로, 이전 실행 이력과 비교할 때는 이 비트를 빼고 본다.

## Docker

```powershell
docker build -t busprobe .
docker run --rm -v ${PWD}/reports:/work/reports busprobe
```

컨테이너는 root가 아닌 사용자(`app`)로 실행된다. 리눅스에서 결과 폴더를 마운트할 때 쓰기 권한 오류가 나면
`--user "$(id -u):$(id -g)"`를 붙이거나 폴더 권한을 열어 둔다.
