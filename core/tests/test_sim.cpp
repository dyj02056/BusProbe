// 시뮬레이터 단위 테스트 (가상 시간 위주, 실시간은 스모크 1건)
#include <gtest/gtest.h>

#include <algorithm>

#include "m1553.hpp"
#include "sim.hpp"

using namespace bp;

static CanFrame frame(uint32_t id, uint8_t dlc = 1) {
  CanFrame f; f.id = id; f.dlc = dlc; f.data[0] = 0x11;
  return f;
}
static std::vector<Ev> pick(const std::vector<Ev>& t, const std::string& dir, const std::string& node) {
  std::vector<Ev> o;
  for (auto& e : t) if (e.dir == dir && e.node == node) o.push_back(e);
  return o;
}
static uint16_t word_at(const Ev& e, size_t i) {
  uint32_t w = 0;
  for (int k = 0; k < 4; k++) w |= uint32_t(e.raw[i * 4 + k]) << (8 * k);
  auto d = decode_word(w);
  EXPECT_TRUE(d.has_value());
  return d ? d->data : 0;
}

TEST(Sim, PeriodicVirtualIsExact) {
  Scenario s; s.duration_ms = 1000;
  s.can_tx.push_back({"A", frame(0x100), 10, 0});
  s.can_sub.push_back({"B", 0x100});
  auto t = simulate(s);
  auto rx = pick(t, "rx", "B");
  ASSERT_EQ(rx.size(), 100u);
  for (size_t i = 0; i < rx.size(); i++) EXPECT_EQ(rx[i].ts, i * 10'000'000ull);
  EXPECT_EQ(pick(t, "tx", "A").size(), 100u);
}

TEST(Sim, SenderDoesNotReceiveOwnFrame) {
  Scenario s; s.duration_ms = 100;
  s.can_tx.push_back({"A", frame(1), 10, 0});
  s.can_sub.push_back({"A", -1});
  EXPECT_TRUE(pick(simulate(s), "rx", "A").empty());
}

TEST(Sim, LowerIdWinsArbitration) {
  Scenario s; s.duration_ms = 10;
  s.can_tx.push_back({"A", frame(0x200), 10, 0});
  s.can_tx.push_back({"B", frame(0x100), 10, 0});
  auto t = simulate(s);
  ASSERT_GE(t.size(), 2u);
  EXPECT_EQ(t[0].key, 0x100u);
  EXPECT_EQ(t[0].dir, "tx");
}

TEST(Sim, ResponderRepliesAfterDelay) {
  Scenario s; s.duration_ms = 100;
  s.can_tx.push_back({"A", frame(0x200), 50, 0});
  s.can_sub.push_back({"A", 0x201});
  s.can_rsp.push_back({"B", 0x200, frame(0x201), 2});
  auto t = simulate(s);
  auto rx = pick(t, "rx", "A");
  ASSERT_EQ(rx.size(), 2u);
  EXPECT_EQ(rx[0].ts, 2'000'000ull);
  EXPECT_EQ(rx[1].ts, 52'000'000ull);
}

TEST(Sim, SilentSenderProducesNothing) {
  Scenario s; s.duration_ms = 100;
  s.can_sub.push_back({"B", -1});
  EXPECT_TRUE(simulate(s).empty());
}

TEST(Sim, RejectsInvalidFrame) {
  Scenario s; s.duration_ms = 10;
  s.can_tx.push_back({"A", frame(0x800), 10, 0});  // 표준 ID 범위 초과
  EXPECT_THROW(simulate(s), std::invalid_argument);
}

TEST(Sim, RejectsZeroPeriod) {
  Scenario s; s.duration_ms = 10;
  s.can_tx.push_back({"A", frame(1), 0, 0});
  EXPECT_THROW(simulate(s), std::invalid_argument);
}

TEST(Sim, BcToRtGetsStatusWord) {
  Scenario s; s.duration_ms = 10;
  s.rts.push_back({"RT5", 5, false, ST_BUSY, {}});
  s.bcs.push_back({"BC", 5, 0, 3, 2, 10, 0, {0x1111, 0x2222}});
  auto t = simulate(s);
  ASSERT_EQ(t.size(), 4u);  // BC tx, RT rx, RT tx(상태), BC rx
  EXPECT_EQ(t[0].dir, "tx"); EXPECT_EQ(t[0].node, "BC");
  EXPECT_EQ(word_at(t[1], 1), 0x1111);
  auto rx = pick(t, "rx", "BC");
  ASSERT_EQ(rx.size(), 1u);
  uint16_t st = word_at(rx[0], 0);
  EXPECT_EQ(st >> 11, 5);
  EXPECT_TRUE(st & ST_BUSY);
  EXPECT_EQ(t[0].key, (5u << 10) | 3u);
}

TEST(Sim, RtToBcReturnsDataPaddedToWordCount) {
  Scenario s; s.duration_ms = 10;
  s.rts.push_back({"RT2", 2, false, 0, {0xAAAA}});
  s.bcs.push_back({"BC", 2, 1, 1, 3, 10, 0, {}});
  auto rx = pick(simulate(s), "rx", "BC");
  ASSERT_EQ(rx.size(), 1u);
  EXPECT_EQ(rx[0].raw.size(), 4u * 4);  // 상태 1 + 데이터 3
  EXPECT_EQ(word_at(rx[0], 1), 0xAAAA);
  EXPECT_EQ(word_at(rx[0], 3), 0);
}

TEST(Sim, WordCount32EncodedAsZero) {
  Scenario s; s.duration_ms = 10;
  s.rts.push_back({"RT2", 2, false, 0, {}});
  s.bcs.push_back({"BC", 2, 1, 1, 32, 10, 0, {}});
  auto rx = pick(simulate(s), "rx", "BC");
  ASSERT_EQ(rx.size(), 1u);
  EXPECT_EQ(rx[0].raw.size(), 33u * 4);
}

TEST(Sim, SilentRtCausesTimeout) {
  Scenario s; s.duration_ms = 10;
  s.rts.push_back({"RT5", 5, true, 0, {}});
  s.bcs.push_back({"BC", 5, 0, 1, 1, 10, 0, {1}});
  auto t = simulate(s);
  EXPECT_EQ(pick(t, "to", "BC").size(), 1u);
  EXPECT_TRUE(pick(t, "rx", "BC").empty());
}

TEST(Sim, MissingRtCausesTimeout) {
  Scenario s; s.duration_ms = 10;
  s.bcs.push_back({"BC", 9, 0, 1, 1, 10, 0, {1}});
  EXPECT_EQ(pick(simulate(s), "to", "BC").size(), 1u);
}

TEST(Sim, BroadcastReachesAllRtsWithoutResponse) {
  Scenario s; s.duration_ms = 10;
  s.rts.push_back({"RT1", 1, false, 0, {}});
  s.rts.push_back({"RT2", 2, false, 0, {}});
  s.bcs.push_back({"BC", 31, 0, 1, 1, 10, 0, {7}});
  auto t = simulate(s);
  EXPECT_EQ(pick(t, "rx", "RT1").size(), 1u);
  EXPECT_EQ(pick(t, "rx", "RT2").size(), 1u);
  EXPECT_TRUE(pick(t, "rx", "BC").empty());
  EXPECT_TRUE(pick(t, "to", "BC").empty());
}

TEST(Sim, RejectsBadBcParameters) {
  Scenario s; s.duration_ms = 10;
  s.bcs.push_back({"BC", 5, 0, 1, 2, 10, 0, {1}});  // 워드 수 불일치
  EXPECT_THROW(simulate(s), std::invalid_argument);
  s.bcs[0] = {"BC", 31, 1, 1, 1, 10, 0, {}};         // 방송 수신 요청 불가
  EXPECT_THROW(simulate(s), std::invalid_argument);
  s.bcs[0] = {"BC", 5, 0, 1, 33, 10, 0, {}};
  EXPECT_THROW(simulate(s), std::invalid_argument);
}

TEST(Sim, RealtimeSmoke) {
  Scenario s; s.duration_ms = 30; s.realtime = true;
  s.can_tx.push_back({"A", frame(1), 10, 0});
  auto t = simulate(s);
  ASSERT_EQ(t.size(), 3u);
  EXPECT_GE(t[2].ts, 20'000'000ull);
  EXPECT_LT(t[2].ts, 200'000'000ull);  // CI 지연 여유
  EXPECT_TRUE(std::is_sorted(t.begin(), t.end(), [](auto& a, auto& b) { return a.ts < b.ts; }));
}

TEST(Sim, ZeroDelayResponseLoopHitsEventLimit) {  // 지연 0으로 서로 응답하면 멈추지 않고 오류
  Scenario s; s.duration_ms = 100;
  s.can_tx.push_back({"A", frame(0x100), 10, 0});
  s.can_rsp.push_back({"B", 0x100, frame(0x101), 0});
  s.can_rsp.push_back({"A", 0x101, frame(0x100), 0});
  EXPECT_THROW(simulate(s), std::runtime_error);
}

TEST(Sim, TraceSizeLimit) {
  Scenario s; s.duration_ms = 1000; s.max_events = 50;
  s.can_tx.push_back({"A", frame(1), 1, 0});
  EXPECT_THROW(simulate(s), std::runtime_error);
}

TEST(Sim, RejectsTooLongDuration) {
  Scenario s; s.duration_ms = MAX_DURATION_MS + 1;
  EXPECT_THROW(simulate(s), std::invalid_argument);
}

TEST(Sim, ExtendedIdKeyDiffersFromStandard) {  // 표준 0x100과 확장 0x100은 다른 메시지
  Scenario s; s.duration_ms = 10;
  CanFrame e = frame(0x100); e.ext = true;
  s.can_tx.push_back({"A", frame(0x100), 10, 0});
  s.can_tx.push_back({"C", e, 10, 0});
  s.can_sub.push_back({"B", 0x100});
  auto t = simulate(s);
  auto rx = pick(t, "rx", "B");
  ASSERT_EQ(rx.size(), 1u);
  EXPECT_EQ(rx[0].key, 0x100u);
  bool saw_ext = false;
  for (auto& ev : t) saw_ext |= ev.dir == "tx" && ev.node == "C" && ev.key == (0x100u | CAN_EFF);
  EXPECT_TRUE(saw_ext);
}

TEST(Sim, RejectsDuplicateRtAddress) {
  Scenario s; s.duration_ms = 10;
  s.rts.push_back({"RT5", 5, false, 0, {}});
  s.rts.push_back({"RT5b", 5, false, 0, {}});
  EXPECT_THROW(simulate(s), std::invalid_argument);
}

TEST(Sim, BroadcastSetsBitInNextStatusOnly) {
  Scenario s; s.duration_ms = 30;
  s.rts.push_back({"RT1", 1, false, 0, {}});
  s.bcs.push_back({"BC", 31, 0, 1, 1, 100, 0, {7}});     // 0ms 방송
  s.bcs.push_back({"BC", 1, 0, 2, 1, 10, 5, {1}});       // 5, 15, 25ms RT1 전송
  auto rx = pick(simulate(s), "rx", "BC");
  ASSERT_EQ(rx.size(), 3u);
  EXPECT_TRUE(word_at(rx[0], 0) & ST_BROADCAST);
  EXPECT_FALSE(word_at(rx[1], 0) & ST_BROADCAST);
}

TEST(M1553, CorruptedWordsRejected) {
  uint32_t w = encode_word(0x1234, SYNC_DATA);
  EXPECT_FALSE(decode_word(w ^ 1).has_value());              // 패리티 오류
  EXPECT_FALSE(decode_word(w ^ (1u << 5)).has_value());      // 데이터 비트 1개 반전
  EXPECT_FALSE(decode_word((w & 0x1FFFF) | (7u << 17)).has_value());  // 잘못된 동기
  EXPECT_FALSE(decode_word(w | (1u << 20)).has_value());     // 20비트 초과
}
