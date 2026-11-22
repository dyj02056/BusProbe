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
  uint64_t key;  // CAN: 식별자, 1553B: (RT<<10)|(T/R<<9)|서브주소
  std::vector<uint8_t> raw;
};

struct CanTx { std::string node; CanFrame f; uint64_t period_ms, offset_ms; };
struct CanSub { std::string node; int64_t id; };  // id -1은 전체 수신
struct CanRsp { std::string node; uint32_t on_id; CanFrame f; uint64_t delay_ms; };
struct Rt { std::string node; unsigned addr; bool silent; uint16_t flags; std::vector<uint16_t> tx_words; };
struct Bc {
  std::string node;
  unsigned rt, tr, sa, wc;
  uint64_t period_ms, offset_ms;
  std::vector<uint16_t> words;  // BC→RT일 때 보낼 데이터 워드
};

struct Scenario {
  bool realtime = false;  // false면 가상 시간(결정적), true면 실제 시간 대기
  uint64_t duration_ms = 0;
  std::vector<CanTx> can_tx;
  std::vector<CanSub> can_sub;
  std::vector<CanRsp> can_rsp;
  std::vector<Rt> rts;
  std::vector<Bc> bcs;
};

// 잘못된 프레임이나 파라미터는 std::invalid_argument
std::vector<Ev> simulate(const Scenario& s);

}  // namespace bp
