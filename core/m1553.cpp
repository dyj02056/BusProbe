#include "m1553.hpp"

#include <bitset>

namespace bp {

static unsigned ones(uint32_t x) { return unsigned(std::bitset<32>(x).count()); }

// 워드 배치: [19:17] 동기, [16:1] 데이터, [0] 패리티
uint32_t encode_word(uint16_t data, Sync s) {
  uint32_t parity = (ones(data) % 2 == 0) ? 1 : 0;  // 데이터+패리티의 1 개수가 홀수가 되게
  return (uint32_t(s) << 17) | (uint32_t(data) << 1) | parity;
}

std::optional<Word> decode_word(uint32_t w) {
  if (w >> 20) return std::nullopt;
  uint8_t s = (w >> 17) & 7;
  if (s != SYNC_DATA && s != SYNC_CMD) return std::nullopt;
  if (ones(w & 0x1FFFF) % 2 == 0) return std::nullopt;  // 패리티 오류
  return Word{uint16_t(w >> 1), Sync(s)};
}

uint16_t cmd_word(unsigned rt, unsigned tr, unsigned sa, unsigned wc) {
  return uint16_t((rt & 31) << 11 | (tr & 1) << 10 | (sa & 31) << 5 | (wc & 31));
}

unsigned word_count(uint16_t cmd) {
  unsigned c = cmd & 31;
  return c ? c : 32;
}

}  // namespace bp
