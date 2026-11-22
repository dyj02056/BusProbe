// MIL-STD-1553B 워드 모델 (공개 자료 수준). 전기적 특성과 이중 버스 절체는 범위 밖.
// 20비트 워드 = 동기 3 + 데이터 16 + 홀수 패리티 1.
#pragma once
#include <cstdint>
#include <optional>

namespace bp {

enum Sync : uint8_t { SYNC_DATA = 3, SYNC_CMD = 4 };  // 3비트 동기 패턴: 데이터 011, 명령/상태 100

struct Word {
  uint16_t data;
  Sync sync;
};

// 상태 워드 비트 (하위 11비트; RT 주소는 상위 5비트)
enum StatusBit : uint16_t {
  ST_MSG_ERROR = 1 << 10,
  ST_SERVICE_REQ = 1 << 8,
  ST_BROADCAST = 1 << 4,
  ST_BUSY = 1 << 3,
  ST_SUBSYS = 1 << 2,
  ST_TERMINAL = 1 << 0,
};

// 데이터 16비트를 패리티와 동기를 붙인 20비트 워드로
uint32_t encode_word(uint16_t data, Sync s);

// 동기 패턴이나 패리티가 틀리면 nullopt
std::optional<Word> decode_word(uint32_t w);

// 커맨드 워드: RT[15:11] T/R[10] 서브주소[9:5] 워드수[4:0]
uint16_t cmd_word(unsigned rt, unsigned tr, unsigned sa, unsigned wc);

// 워드 수 필드 0은 32워드를 뜻함
unsigned word_count(uint16_t cmd);

}  // namespace bp
