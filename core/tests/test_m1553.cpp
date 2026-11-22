// 1553B 워드 코덱 단위 테스트
#include <gtest/gtest.h>

#include "m1553.hpp"

using namespace bp;

TEST(M1553, RoundTripAllDataValues) {
  for (uint32_t d = 0; d <= 0xFFFF; d++) {
    for (Sync s : {SYNC_DATA, SYNC_CMD}) {
      auto w = decode_word(encode_word(uint16_t(d), s));
      ASSERT_TRUE(w.has_value());
      ASSERT_EQ(w->data, d);
      ASSERT_EQ(w->sync, s);
    }
  }
}

TEST(M1553, ParityIsOdd) {
  // 데이터 + 패리티 비트의 1 개수는 항상 홀수
  for (uint16_t d : {0x0000, 0x0001, 0xFFFF, 0x1234}) {
    uint32_t w = encode_word(d, SYNC_DATA) & 0x1FFFF;
    int ones = 0;
    for (; w; w >>= 1) ones += w & 1;
    EXPECT_EQ(ones % 2, 1);
  }
}

TEST(M1553, DetectsParityError) {
  uint32_t w = encode_word(0x1234, SYNC_DATA) ^ 1;  // 패리티 비트 뒤집기
  EXPECT_FALSE(decode_word(w).has_value());
}

TEST(M1553, DetectsSingleDataBitFlip) {
  uint32_t w = encode_word(0x1234, SYNC_CMD) ^ (1u << 5);
  EXPECT_FALSE(decode_word(w).has_value());
}

TEST(M1553, RejectsBadSync) {
  uint32_t w = encode_word(0x1234, SYNC_DATA) & 0x1FFFF;  // 동기 000
  EXPECT_FALSE(decode_word(w).has_value());
}

TEST(M1553, RejectsBitsAbove20) {
  EXPECT_FALSE(decode_word(encode_word(0, SYNC_DATA) | (1u << 20)).has_value());
}

TEST(M1553, CommandWordFields) {
  uint16_t c = cmd_word(5, 1, 3, 4);
  EXPECT_EQ(c >> 11, 5);
  EXPECT_EQ((c >> 10) & 1, 1);
  EXPECT_EQ((c >> 5) & 31, 3);
  EXPECT_EQ(c & 31, 4);
}

TEST(M1553, WordCountZeroMeans32) {
  EXPECT_EQ(word_count(cmd_word(1, 0, 1, 32)), 32u);  // 32는 필드에 0으로 들어감
  EXPECT_EQ(word_count(cmd_word(1, 0, 1, 1)), 1u);
  EXPECT_EQ(word_count(cmd_word(1, 0, 1, 31)), 31u);
}
