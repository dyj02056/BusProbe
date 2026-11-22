#include "protocol.hpp"

#include <sstream>
#include <stdexcept>

namespace bp {

uint64_t parse_uint(const std::string& s, uint64_t max) {
  if (s.empty() || s.size() > 20) throw std::invalid_argument("bad number '" + s + "'");
  uint64_t v = 0;
  for (char c : s) {
    if (c < '0' || c > '9') throw std::invalid_argument("bad number '" + s + "'");
    unsigned d = unsigned(c - '0');
    if (v > (UINT64_MAX - d) / 10) throw std::invalid_argument("number too large '" + s + "'");
    v = v * 10 + d;
  }
  if (v > max) throw std::invalid_argument("number out of range '" + s + "' (max " + std::to_string(max) + ")");
  return v;
}

static unsigned hex_digit(char c) {
  if (c >= '0' && c <= '9') return unsigned(c - '0');
  if (c >= 'a' && c <= 'f') return unsigned(c - 'a' + 10);
  if (c >= 'A' && c <= 'F') return unsigned(c - 'A' + 10);
  throw std::invalid_argument(std::string("bad hex digit '") + c + "'");
}

std::vector<uint8_t> hex_bytes(const std::string& h) {
  std::vector<uint8_t> o;
  if (h == "-") return o;
  if (h.empty() || h.size() % 2) throw std::invalid_argument("hex bytes must be 2 digits each");
  for (size_t i = 0; i < h.size(); i += 2) o.push_back(uint8_t(hex_digit(h[i]) << 4 | hex_digit(h[i + 1])));
  return o;
}

std::vector<uint16_t> hex_words(const std::string& h) {
  std::vector<uint16_t> o;
  if (h == "-") return o;
  if (h.empty() || h.size() % 4) throw std::invalid_argument("hex words must be 4 digits each");
  for (size_t i = 0; i < h.size(); i += 4) {
    unsigned w = 0;
    for (size_t k = 0; k < 4; k++) w = w << 4 | hex_digit(h[i + k]);
    o.push_back(uint16_t(w));
  }
  return o;
}

static CanFrame make_frame(uint64_t id, uint64_t ext, uint64_t dlc, const std::string& hex) {
  CanFrame f;
  f.id = uint32_t(id); f.ext = ext != 0; f.dlc = uint8_t(dlc);
  auto b = hex_bytes(hex);
  if (b.size() > dlc) throw std::invalid_argument("data longer than dlc");
  for (size_t i = 0; i < b.size(); i++) f.data[i] = b[i];
  return f;
}

void apply_line(Scenario& s, const std::string& line) {
  std::istringstream is(line);
  std::vector<std::string> t;
  for (std::string w; is >> w;) t.push_back(w);
  if (t.empty()) return;
  auto need = [&](size_t n) {
    if (t.size() != n) throw std::invalid_argument(t[0] + " needs " + std::to_string(n - 1) + " fields");
  };
  auto num = [&](size_t i, uint64_t max) { return parse_uint(t.at(i), max); };
  constexpr uint64_t MS_MAX = MAX_DURATION_MS, ID_MAX = 0x1FFFFFFF, KEY_MAX = 0xFFFFFFFF;
  const std::string& c = t[0];
  if (c == "mode") {
    need(2);
    if (t[1] != "virtual" && t[1] != "realtime") throw std::invalid_argument("mode must be virtual|realtime");
    s.realtime = t[1] == "realtime";
  } else if (c == "duration_ms") {
    need(2);
    s.duration_ms = num(1, MS_MAX);
  } else if (c == "can_tx") {  // node id ext period offset dlc hex
    need(8);
    s.can_tx.push_back({t[1], make_frame(num(2, ID_MAX), num(3, 1), num(6, 8), t[7]), num(4, MS_MAX), num(5, MS_MAX)});
  } else if (c == "can_sub") {  // node key|*
    need(3);
    s.can_sub.push_back({t[1], t[2] == "*" ? -1 : int64_t(num(2, KEY_MAX))});
  } else if (c == "can_rsp") {  // node on_key out_id ext delay dlc hex
    need(8);
    s.can_rsp.push_back({t[1], uint32_t(num(2, KEY_MAX)), make_frame(num(3, ID_MAX), num(4, 1), num(6, 8), t[7]), num(5, MS_MAX)});
  } else if (c == "m_rt") {  // node addr silent flags txwords
    need(6);
    s.rts.push_back({t[1], unsigned(num(2, 31)), num(3, 1) != 0, uint16_t(num(4, 0x7FF)), hex_words(t[5])});
  } else if (c == "m_bc") {  // node rt tr sa wc period offset words
    need(9);
    s.bcs.push_back({t[1], unsigned(num(2, 31)), unsigned(num(3, 1)), unsigned(num(4, 31)), unsigned(num(5, 32)),
                     num(6, MS_MAX), num(7, MS_MAX), hex_words(t[8])});
  } else {
    throw std::invalid_argument("unknown command " + c);
  }
}

std::string format_event(const Ev& e) {
  static const char* H = "0123456789abcdef";
  std::string out = "ev " + std::to_string(e.ts) + " " + e.dir + " " + e.node + " " + e.bus + " " + std::to_string(e.key) + " ";
  if (e.raw.empty()) out += "-";
  for (auto b : e.raw) { out += H[b >> 4]; out += H[b & 15]; }
  out += "\n";
  return out;
}

}  // namespace bp
