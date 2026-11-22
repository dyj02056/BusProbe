// 가상 버스 시뮬레이터: 시나리오를 받아 송수신 이벤트(트레이스)를 만든다. 판정은 하지 않는다.
#pragma once
#include <cstdint>
#include <string>
#include <vector>

#include "can.hpp"

namespace bp {

// 트레이스 한 줄. dir: tx 송신 / rx 수신 / to 1553B 응답 타임아웃
struct Ev {
  uint64_t ts;  // 시작 기준 경과 ns
  std::string dir, node, bus;
  uint64_t key;  // CAN: can_key() (확장 ID는 CAN_EFF 비트 포함), 1553B: (RT<<10)|(T/R<<9)|서브주소
  std::vector<uint8_t> raw;
};

struct CanTx { std::string node; CanFrame f; uint64_t period_ms, offset_ms; };
struct CanSub { std::string node; int64_t key; };  // can_key 값, -1은 전체 수신
struct CanRsp { std::string node; uint32_t on_key; CanFrame f; uint64_t delay_ms; };  // on_key: can_key 값
struct Rt { std::string node; unsigned addr; bool silent; uint16_t flags; std::vector<uint16_t> tx_words; };
struct Bc {
  std::string node;
  unsigned rt, tr, sa, wc;
  uint64_t period_ms, offset_ms;
  std::vector<uint16_t> words;  // BC→RT일 때 보낼 데이터 워드
};

// 시뮬레이션 길이 상한 (1시간)
constexpr uint64_t MAX_DURATION_MS = 3'600'000;

// CAN 트레이스 키: 표준 ID는 그대로, 확장 ID는 CAN_EFF 비트를 붙여 같은 값의 표준 ID와 구분
inline uint32_t can_key(const CanFrame& f) { return f.id | (f.ext ? CAN_EFF : 0); }

struct Scenario {
  bool realtime = false;  // false면 가상 시간(결정적), true면 실제 시간 대기
  uint64_t duration_ms = 0;
  // 폭주 방지 상한: 전체 트레이스 이벤트 수, 같은 시각에 연쇄로 처리되는 예약 수 (지연 0 응답 루프)
  uint64_t max_events = 1'000'000;
  uint64_t max_same_time = 10'000;
  std::vector<CanTx> can_tx;
  std::vector<CanSub> can_sub;
  std::vector<CanRsp> can_rsp;
  std::vector<Rt> rts;
  std::vector<Bc> bcs;
};

// 잘못된 프레임이나 파라미터는 std::invalid_argument, 이벤트 상한 초과는 std::runtime_error
std::vector<Ev> simulate(const Scenario& s);

}  // namespace bp
