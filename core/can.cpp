#include "can.hpp"

namespace bp {

// 표준 11비트 / 확장 29비트 범위, DLC 0~8, RTR은 데이터 없음
bool valid(const CanFrame& f) {
  if (f.dlc > 8) return false;
  if (f.id > (f.ext ? 0x1FFFFFFFu : 0x7FFu)) return false;
  if (f.rtr)
    for (auto b : f.data)
      if (b) return false;
  return true;
}

// [0..3] 리틀엔디언 id|플래그, [4] dlc, [5..7] 패딩, [8..15] 데이터
std::array<uint8_t, 16> encode(const CanFrame& f) {
  std::array<uint8_t, 16> o{};
  uint32_t w = f.id | (f.ext ? CAN_EFF : 0) | (f.rtr ? CAN_RTR : 0);
  for (int i = 0; i < 4; i++) o[i] = uint8_t(w >> (8 * i));
  o[4] = f.dlc;
  for (int i = 0; i < 8; i++) o[8 + i] = f.data[i];
  return o;
}

// 외부에서 들어온 바이트는 항상 valid()로 다시 검증
std::optional<CanFrame> decode(const uint8_t* b, size_t n) {
  if (n != 16) return std::nullopt;
  uint32_t w = 0;
  for (int i = 0; i < 4; i++) w |= uint32_t(b[i]) << (8 * i);
  CanFrame f;
  f.ext = w & CAN_EFF;
  f.rtr = w & CAN_RTR;
  f.id = w & 0x1FFFFFFFu;
  f.dlc = b[4];
  for (int i = 0; i < 8; i++) f.data[i] = b[8 + i];
  if (!valid(f)) return std::nullopt;
  return f;
}

}  // namespace bp
