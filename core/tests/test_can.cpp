// CAN 코덱 단위 테스트
#include <gtest/gtest.h>

#include "can.hpp"

using namespace bp;

static CanFrame make(uint32_t id, bool ext, uint8_t dlc) {
  CanFrame f;
  f.id = id; f.ext = ext; f.dlc = dlc;
  for (int i = 0; i < dlc && i < 8; i++) f.data[i] = uint8_t(0xA0 + i);
  return f;
}

TEST(Can, RoundTripStandard) {
  auto f = make(0x123, false, 8);
  auto raw = encode(f);
  auto d = decode(raw.data(), raw.size());
  ASSERT_TRUE(d.has_value());
  EXPECT_EQ(d->id, 0x123u);
  EXPECT_FALSE(d->ext);
  EXPECT_EQ(d->dlc, 8);
  EXPECT_EQ(d->data, f.data);
}

TEST(Can, RoundTripExtendedSetsEffBit) {
  auto f = make(0x1ABCDEF0, true, 3);
  auto raw = encode(f);
  EXPECT_EQ(raw[3] & 0x80, 0x80);  // 최상위 바이트의 EFF 비트
  auto d = decode(raw.data(), raw.size());
  ASSERT_TRUE(d.has_value());
  EXPECT_TRUE(d->ext);
  EXPECT_EQ(d->id, 0x1ABCDEF0u);
}

TEST(Can, RtrRoundTrip) {
  CanFrame f; f.id = 0x7FF; f.rtr = true; f.dlc = 4;
  auto raw = encode(f);
  auto d = decode(raw.data(), raw.size());
  ASSERT_TRUE(d.has_value());
  EXPECT_TRUE(d->rtr);
}

TEST(Can, IdBoundaries) {
  EXPECT_TRUE(valid(make(0x7FF, false, 0)));
  EXPECT_FALSE(valid(make(0x800, false, 0)));
  EXPECT_TRUE(valid(make(0x1FFFFFFF, true, 0)));
  EXPECT_FALSE(valid(make(0x20000000, true, 0)));
}

TEST(Can, DlcBoundaries) {
  EXPECT_TRUE(valid(make(1, false, 0)));
  EXPECT_TRUE(valid(make(1, false, 8)));
  EXPECT_FALSE(valid(make(1, false, 9)));
}

TEST(Can, RtrWithDataIsInvalid) {
  auto f = make(1, false, 2);
  f.rtr = true;
  EXPECT_FALSE(valid(f));
}

TEST(Can, DecodeRejectsWrongLength) {
  uint8_t b[15] = {};
  EXPECT_FALSE(decode(b, 15).has_value());
}

TEST(Can, DecodeRejectsDlcAbove8) {
  auto raw = encode(make(1, false, 8));
  raw[4] = 9;
  EXPECT_FALSE(decode(raw.data(), raw.size()).has_value());
}

TEST(Can, DecodeRejectsStandardIdTooLarge) {
  auto raw = encode(make(1, false, 0));
  raw[1] = 0x08;  // id = 0x801, EFF 없음
  EXPECT_FALSE(decode(raw.data(), raw.size()).has_value());
}
