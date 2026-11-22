// CAN 2.0A/B 프레임 모델. CAN FD, CRC, 비트 스터핑은 범위 밖 (docs/assumptions.md).
#pragma once
#include <array>
#include <cstddef>
#include <cstdint>
#include <optional>

namespace bp {

constexpr uint32_t CAN_EFF = 0x80000000u;  // 확장 ID 플래그 (SocketCAN과 동일)
constexpr uint32_t CAN_RTR = 0x40000000u;  // 원격 요청 플래그

struct CanFrame {
  uint32_t id = 0;
  bool ext = false, rtr = false;
  uint8_t dlc = 0;
  std::array<uint8_t, 8> data{};
};

// ID 비트 수, DLC 범위, RTR 프레임 데이터 없음을 검사
bool valid(const CanFrame& f);

// SocketCAN can_frame 호환 16바이트 직렬화
std::array<uint8_t, 16> encode(const CanFrame& f);

// 길이나 내용이 잘못되면 nullopt
std::optional<CanFrame> decode(const uint8_t* b, size_t n);

}  // namespace bp
