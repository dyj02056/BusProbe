#include "sim.hpp"

#include <chrono>
#include <functional>
#include <queue>
#include <set>
#include <stdexcept>
#include <string>
#include <thread>

#include "m1553.hpp"

namespace bp {
namespace {

using Clock = std::chrono::steady_clock;
constexpr uint64_t MS = 1000000;  // ms → ns

// 1553B 워드들을 4바이트 리틀엔디언씩 이어 붙인 트레이스용 raw (첫 워드는 명령/상태 동기)
std::vector<uint8_t> words_raw(uint16_t head, const std::vector<uint16_t>& data) {
  std::vector<uint8_t> o;
  auto put = [&](uint32_t w) { for (int i = 0; i < 4; i++) o.push_back(uint8_t(w >> (8 * i))); };
  put(encode_word(head, SYNC_CMD));
  for (auto d : data) put(encode_word(d, SYNC_DATA));
  return o;
}

class Sim {
 public:
  explicit Sim(const Scenario& s) : s_(s), dur_(s.duration_ms * MS) {
    for (auto& r : s.rts) rts_.push_back({r, false});
  }

  std::vector<Ev> run() {
    validate();
    for (auto& t : s_.can_tx)
      periodic(t.offset_ms * MS, t.period_ms, t.f.id, [this, t] { can_send(t.node, t.f); });
    for (auto& b : s_.bcs)
      periodic(b.offset_ms * MS, b.period_ms, 0, [this, b] { bc_transfer(b); });

    auto start = Clock::now();
    uint64_t last_t = UINT64_MAX, same = 0;
    while (!q_.empty()) {
      Item it = q_.top();
      q_.pop();
      // 같은 시각에 끝없이 예약이 생기면(지연 0 응답 루프 등) 멈추지 않고 오류로 끝낸다
      same = it.t == last_t ? same + 1 : 1;
      last_t = it.t;
      if (same > s_.max_same_time)
        throw std::runtime_error("event limit exceeded: more than " + std::to_string(s_.max_same_time) +
                                 " events at t=" + std::to_string(it.t) + "ns (zero-delay response loop?)");
      if (s_.realtime) {  // 실시간 모드: 예정 시각까지 대기 후 실제 경과 시간을 기록
        std::this_thread::sleep_until(start + std::chrono::nanoseconds(it.t));
        now_ = uint64_t(std::chrono::duration_cast<std::chrono::nanoseconds>(Clock::now() - start).count());
      } else {
        now_ = it.t;
      }
      it.fn();
    }
    return std::move(tr_);
  }

 private:
  struct Item {
    uint64_t t;
    uint32_t prio;  // 같은 시각이면 낮은 값 먼저 (CAN 중재: ID가 낮은 쪽 우선)
    uint64_t seq;
    std::function<void()> fn;
    bool operator>(const Item& o) const {
      if (t != o.t) return t > o.t;
      if (prio != o.prio) return prio > o.prio;
      return seq > o.seq;
    }
  };

  // 시나리오 파라미터 검증: 신뢰 경계이므로 시뮬레이션 전에 모두 확인
  void validate() const {
    if (s_.duration_ms > MAX_DURATION_MS)
      throw std::invalid_argument("duration_ms must be <= " + std::to_string(MAX_DURATION_MS));
    for (auto& t : s_.can_tx) {
      if (!valid(t.f)) throw std::invalid_argument("invalid CAN frame from " + t.node);
      if (t.period_ms == 0) throw std::invalid_argument("period_ms must be > 0 for " + t.node);
    }
    for (auto& r : s_.can_rsp)
      if (!valid(r.f)) throw std::invalid_argument("invalid CAN response frame from " + r.node);
    std::set<unsigned> addrs;
    for (auto& r : s_.rts) {
      if (r.addr > 30) throw std::invalid_argument("RT address must be 0..30: " + r.node);
      if (!addrs.insert(r.addr).second) throw std::invalid_argument("duplicate RT address " + std::to_string(r.addr) + ": " + r.node);
      if (r.tx_words.size() > 32) throw std::invalid_argument("tx_words must be <= 32: " + r.node);
    }
    for (auto& b : s_.bcs) {
      if (b.period_ms == 0) throw std::invalid_argument("period_ms must be > 0 for " + b.node);
      if (b.rt > 31 || b.sa > 31 || b.tr > 1) throw std::invalid_argument("rt/sa/tr out of range");
      if (b.wc < 1 || b.wc > 32) throw std::invalid_argument("word count must be 1..32");
      if (b.rt == 31 && b.tr == 1) throw std::invalid_argument("broadcast cannot request transmit");
      if (b.tr == 0 && b.words.size() != b.wc) throw std::invalid_argument("words size != word count");
    }
  }

  void at(uint64_t t, uint32_t prio, std::function<void()> fn) {
    if (t < dur_) q_.push({t, prio, seq_++, std::move(fn)});  // 종료 시각 이후 이벤트는 버림
  }

  // 예정 시각 기준으로 다음 주기를 잡아 누적 오차를 막는다
  void periodic(uint64_t t, uint64_t period_ms, uint32_t prio, std::function<void()> send) {
    at(t, prio, [this, t, period_ms, prio, send] {
      send();
      periodic(t + period_ms * MS, period_ms, prio, send);
    });
  }

  void emit(const char* dir, const std::string& node, const char* bus, uint64_t key, std::vector<uint8_t> raw) {
    if (tr_.size() >= s_.max_events)
      throw std::runtime_error("event limit exceeded: trace larger than " + std::to_string(s_.max_events) + " events");
    tr_.push_back({now_, dir, node, bus, key, std::move(raw)});
  }

  // CAN: 송신 기록 → 구독자/응답자 수신 기록 → 응답 예약
  void can_send(const std::string& node, const CanFrame& f) {
    auto e = encode(f);
    std::vector<uint8_t> raw(e.begin(), e.end());
    uint32_t key = can_key(f);
    emit("tx", node, "can", key, raw);
    std::set<std::string> rx;  // set이라 수신 기록 순서가 항상 같다
    for (auto& s : s_.can_sub)
      if (s.node != node && (s.key < 0 || uint32_t(s.key) == key)) rx.insert(s.node);
    for (auto& r : s_.can_rsp)
      if (r.node != node && r.on_key == key) rx.insert(r.node);
    for (auto& n : rx) emit("rx", n, "can", key, raw);
    for (auto& r : s_.can_rsp)
      if (r.node != node && r.on_key == key)
        at(now_ + r.delay_ms * MS, r.f.id, [this, r] { can_send(r.node, r.f); });
  }

  // 1553B: BC가 명령 → RT가 상태(+데이터)로 응답. 방송은 응답 없음, 무응답 RT는 to 이벤트.
  // 방송을 받은 RT는 다음 상태 워드 1회에 broadcast_received 비트를 싣는다 (단순화, docs/assumptions.md)
  void bc_transfer(const Bc& b) {
    uint64_t key = uint64_t(b.rt) << 10 | uint64_t(b.tr) << 9 | b.sa;
    auto out = words_raw(cmd_word(b.rt, b.tr, b.sa, b.wc), b.tr ? std::vector<uint16_t>{} : b.words);
    emit("tx", b.node, "m1553", key, out);
    if (b.rt == 31) {
      for (auto& r : rts_) {
        emit("rx", r.def.node, "m1553", key, out);
        r.bcast_pending = true;
      }
      return;
    }
    RtState* rt = nullptr;
    for (auto& r : rts_)
      if (r.def.addr == b.rt) rt = &r;
    if (rt) emit("rx", rt->def.node, "m1553", key, out);
    if (!rt || rt->def.silent) {
      emit("to", b.node, "m1553", key, {});
      return;
    }
    std::vector<uint16_t> d;
    if (b.tr) {
      d = rt->def.tx_words;
      d.resize(b.wc, 0);  // 부족한 워드는 0으로 채움
    }
    uint16_t flags = rt->def.flags | (rt->bcast_pending ? ST_BROADCAST : 0);
    rt->bcast_pending = false;
    auto resp = words_raw(uint16_t(rt->def.addr << 11 | flags), d);
    emit("tx", rt->def.node, "m1553", key, resp);
    emit("rx", b.node, "m1553", key, resp);
  }

  struct RtState {
    Rt def;
    bool bcast_pending;  // 방송 수신 후 아직 상태 워드로 알리지 않음
  };

  const Scenario& s_;
  std::vector<RtState> rts_;
  uint64_t dur_, now_ = 0, seq_ = 0;
  std::priority_queue<Item, std::vector<Item>, std::greater<Item>> q_;
  std::vector<Ev> tr_;
};

}  // namespace

std::vector<Ev> simulate(const Scenario& s) { return Sim(s).run(); }

}  // namespace bp
