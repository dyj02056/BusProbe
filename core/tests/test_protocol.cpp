// 시뮬레이터 텍스트 프로토콜 파서 테스트: 외부 입력이므로 잘못된 줄을 모두 예외로 거부해야 한다
#include <gtest/gtest.h>

#include <random>

#include "protocol.hpp"

using namespace bp;

TEST(Protocol, ParsesValidLines) {
  Scenario s;
  apply_line(s, "mode realtime");
  apply_line(s, "duration_ms 100");
  apply_line(s, "can_tx A 256 0 10 0 2 01ff");
  apply_line(s, "can_sub B *");
  apply_line(s, "can_rsp B 256 257 0 2 0 -");
  apply_line(s, "m_rt RT5 5 0 8 aaaa");
  apply_line(s, "m_bc BC 5 0 3 2 10 0 11112222");
  EXPECT_TRUE(s.realtime);
  EXPECT_EQ(s.duration_ms, 100u);
  ASSERT_EQ(s.can_tx.size(), 1u);
  EXPECT_EQ(s.can_tx[0].f.data[1], 0xFF);
  EXPECT_EQ(s.can_sub[0].key, -1);
  EXPECT_EQ(s.rts[0].tx_words[0], 0xAAAA);
  EXPECT_EQ(s.bcs[0].words.size(), 2u);
}

TEST(Protocol, RejectsMalformedLines) {
  const char* bad[] = {
      "unknown 1",
      "mode fast",
      "duration_ms -1",               // 부호
      "duration_ms 0x10",             // 16진
      "duration_ms 99999999999999999999999",
      "duration_ms 3600001",          // 상한 초과
      "can_tx A 256 0 10 0 2",        // 필드 부족
      "can_tx A 256 0 10 0 2 01ff x", // 필드 초과
      "can_tx A 256 0 10 0 2 1ff",    // 홀수 hex
      "can_tx A 256 0 10 0 2 zz00",   // hex 아님
      "can_tx A 256 0 10 0 1 0102",   // dlc보다 긴 데이터
      "can_tx A 256 0 10 0 9 -",      // dlc 초과
      "can_tx A 536870912 1 10 0 0 -",// 29비트 초과
      "can_tx A 256 2 10 0 0 -",      // ext는 0/1
      "m_rt RT 32 0 0 -",             // 주소 초과
      "m_rt RT 1 0 0 abc",            // 워드는 4자리
      "m_bc BC 5 0 3 33 10 0 -",      // wc 초과
  };
  for (auto* l : bad) {
    Scenario s;
    EXPECT_ANY_THROW(apply_line(s, l)) << l;
  }
}

TEST(Protocol, ParseUintBounds) {
  EXPECT_EQ(parse_uint("0", 0), 0u);
  EXPECT_EQ(parse_uint("18446744073709551615", UINT64_MAX), UINT64_MAX);
  EXPECT_THROW(parse_uint("18446744073709551616", UINT64_MAX), std::invalid_argument);
  EXPECT_THROW(parse_uint("", 10), std::invalid_argument);
  EXPECT_THROW(parse_uint(" 1", 10), std::invalid_argument);
}

TEST(Protocol, FormatsEvent) {
  Ev e{5, "tx", "A", "can", 256, {0x01, 0xab}};
  EXPECT_EQ(format_event(e), "ev 5 tx A can 256 01ab\n");
  e.raw.clear();
  EXPECT_EQ(format_event(e), "ev 5 tx A can 256 -\n");
}

// 간이 퍼징: 무작위 토큰 조합이 예외 외의 방식(크래시, 무한 루프)으로 실패하지 않아야 한다
TEST(Protocol, RandomLinesNeverCrash) {
  const char* tok[] = {"mode", "duration_ms", "can_tx", "can_sub", "can_rsp", "m_rt", "m_bc", "A", "*", "-", "0",
                       "1", "8", "31", "32", "255", "-1", "4294967296", "ff", "fff", "0102030405060708", "zz", "\t", ""};
  std::mt19937 rng(12345);
  for (int i = 0; i < 20000; i++) {
    std::string line;
    int n = int(rng() % 10);
    for (int k = 0; k < n; k++) line += std::string(tok[rng() % (sizeof tok / sizeof *tok)]) + " ";
    if (rng() % 4 == 0) line += char(rng() % 256);
    Scenario s;
    try { apply_line(s, line); } catch (const std::exception&) {}
  }
  SUCCEED();
}
