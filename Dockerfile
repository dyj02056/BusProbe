# 리눅스 컨테이너에서 빌드부터 시험 실행까지 재현한다.
FROM ubuntu:24.04 AS cpp
RUN apt-get update && apt-get install -y --no-install-recommends cmake g++ ninja-build && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY CMakeLists.txt .
COPY core core
COPY simd simd
RUN cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release -DBUSPROBE_TESTS=OFF && cmake --build build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS cs
WORKDIR /src
COPY runner/BusProbe.sln runner/BusProbe.sln
COPY runner/BusProbe.Cli runner/BusProbe.Cli
RUN dotnet publish runner/BusProbe.Cli -c Release -o /out

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /work
COPY --from=cs /out /app
COPY --from=cpp /src/build/busprobe-simd /app/busprobe-simd
COPY scenarios scenarios
COPY docs/requirements.yaml docs/requirements.yaml
COPY db db
ENV BUSPROBE_SIMD=/app/busprobe-simd
ENTRYPOINT ["dotnet", "/app/BusProbe.Cli.dll"]
CMD ["run", "scenarios", "--out", "reports", "--no-realtime"]
