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

## Docker

```powershell
docker build -t busprobe .
docker run --rm -v ${PWD}/reports:/work/reports busprobe
```
