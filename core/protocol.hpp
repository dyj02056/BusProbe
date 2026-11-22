// 러너 ↔ 시뮬레이터 줄 단위 텍스트 프로토콜 (docs/protocol.md). 외부 입력이므로 모든 필드를 엄격히 검사한다.
#pragma once
#include <cstdint>
#include <string>
#include <vector>

#include "sim.hpp"

namespace bp {

// 10진수 숫자만 허용 (부호, 공백, 16진 접두사 불가). max를 넘으면 std::invalid_argument
uint64_t parse_uint(const std::string& s, uint64_t max);

// "0100ff" → 바이트열, "-" 는 빈 값
std::vector<uint8_t> hex_bytes(const std::string& h);

// 4자리 16진수 묶음 → 16비트 워드들, "-" 는 빈 값
std::vector<uint16_t> hex_words(const std::string& h);

// 설정 한 줄을 Scenario에 반영. 잘못된 줄은 std::exception 계열 예외
void apply_line(Scenario& s, const std::string& line);

// 트레이스 한 줄: "ev <ts> <dir> <node> <bus> <key> <hex|->"
std::string format_event(const Ev& e);

}  // namespace bp
