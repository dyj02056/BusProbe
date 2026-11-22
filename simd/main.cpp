// busprobe-simd: 루프백 TCP 서버. 연결 1개 = 시나리오 1개 (프로토콜은 docs/protocol.md).
// 줄 단위 텍스트: 설정 줄들 → "run" → 응답으로 "ev ..." 줄들과 "done N" (또는 "error ...").
#include <cstdio>
#include <iostream>
#include <sstream>
#include <string>
#include <vector>

#include "m1553.hpp"
#include "sim.hpp"

#ifdef _WIN32
#include <winsock2.h>
#include <ws2tcpip.h>
using sock_t = SOCKET;
static void sock_close(sock_t s) { closesocket(s); }
#else
#include <arpa/inet.h>
#include <netinet/in.h>
#include <sys/socket.h>
#include <unistd.h>
using sock_t = int;
static void sock_close(sock_t s) { close(s); }
#endif

using namespace bp;

// "0100ff" → 바이트열, "-" 는 빈 값
static std::vector<uint8_t> hex_bytes(const std::string& h) {
  std::vector<uint8_t> o;
  if (h == "-") return o;
  if (h.size() % 2) throw std::invalid_argument("odd hex length");
  for (size_t i = 0; i < h.size(); i += 2) o.push_back(uint8_t(std::stoul(h.substr(i, 2), nullptr, 16)));
  return o;
}
// 4자리 16진수 묶음 → 16비트 워드들
static std::vector<uint16_t> hex_words(const std::string& h) {
  std::vector<uint16_t> o;
  if (h == "-") return o;
  if (h.size() % 4) throw std::invalid_argument("hex words must be 4 digits each");
  for (size_t i = 0; i < h.size(); i += 4) o.push_back(uint16_t(std::stoul(h.substr(i, 4), nullptr, 16)));
  return o;
}
static CanFrame make_frame(uint32_t id, bool ext, unsigned dlc, const std::string& hex) {
  CanFrame f;
  f.id = id; f.ext = ext; f.dlc = uint8_t(dlc);
  auto b = hex_bytes(hex);
  if (b.size() > 8) throw std::invalid_argument("data longer than 8 bytes");
  for (size_t i = 0; i < b.size(); i++) f.data[i] = b[i];
  return f;
}

// 설정 한 줄을 Scenario에 반영. 잘못된 줄은 예외 (at()이 out_of_range를 던짐)
static void apply_line(Scenario& s, const std::string& line) {
  std::istringstream is(line);
  std::vector<std::string> t;
  for (std::string w; is >> w;) t.push_back(w);
  if (t.empty()) return;
  auto num = [&](size_t i) { return std::stoull(t.at(i)); };
  const std::string& c = t[0];
  if (c == "mode") s.realtime = t.at(1) == "realtime";
  else if (c == "duration_ms") s.duration_ms = num(1);
  else if (c == "can_tx")  // node id ext period offset dlc hex
    s.can_tx.push_back({t.at(1), make_frame(uint32_t(num(2)), num(3), unsigned(num(6)), t.at(7)), num(4), num(5)});
  else if (c == "can_sub")  // node id|*
    s.can_sub.push_back({t.at(1), t.at(2) == "*" ? -1 : int64_t(num(2))});
  else if (c == "can_rsp")  // node on_id out_id ext delay dlc hex
    s.can_rsp.push_back({t.at(1), uint32_t(num(2)), make_frame(uint32_t(num(3)), num(4), unsigned(num(6)), t.at(7)), num(5)});
  else if (c == "m_rt")  // node addr silent flags txwords
    s.rts.push_back({t.at(1), unsigned(num(2)), num(3) != 0, uint16_t(num(4)), hex_words(t.at(5))});
  else if (c == "m_bc")  // node rt tr sa wc period offset words
    s.bcs.push_back({t.at(1), unsigned(num(2)), unsigned(num(3)), unsigned(num(4)), unsigned(num(5)), num(6), num(7), hex_words(t.at(8))});
  else throw std::invalid_argument("unknown command " + c);
}

static void send_all(sock_t c, const std::string& s) {
  size_t off = 0;
  while (off < s.size()) {
    int n = send(c, s.data() + off, int(s.size() - off), 0);
    if (n <= 0) return;
    off += size_t(n);
  }
}

// 시뮬레이션 실행 후 트레이스를 한 번에 응답
static void run_and_reply(sock_t c, const Scenario& s) {
  std::string out;
  try {
    auto tr = simulate(s);
    static const char* H = "0123456789abcdef";
    for (auto& e : tr) {
      out += "ev " + std::to_string(e.ts) + " " + e.dir + " " + e.node + " " + e.bus + " " + std::to_string(e.key) + " ";
      if (e.raw.empty()) out += "-";
      for (auto b : e.raw) { out += H[b >> 4]; out += H[b & 15]; }
      out += "\n";
    }
    out += "done " + std::to_string(tr.size()) + "\n";
  } catch (const std::exception& e) {
    out = std::string("error ") + e.what() + "\n";
  }
  send_all(c, out);
}

// 연결 하나를 처리: 줄을 모아 "run"이 오면 실행하고 끝낸다
static void serve(sock_t c) {
  Scenario sc;
  std::string buf;
  char tmp[4096];
  for (;;) {
    int n = recv(c, tmp, sizeof tmp, 0);
    if (n <= 0) return;
    buf.append(tmp, size_t(n));
    size_t p;
    while ((p = buf.find('\n')) != std::string::npos) {
      std::string line = buf.substr(0, p);
      buf.erase(0, p + 1);
      if (!line.empty() && line.back() == '\r') line.pop_back();
      if (line == "run") return run_and_reply(c, sc);
      try {
        apply_line(sc, line);
      } catch (const std::exception& e) {
        return send_all(c, "error bad line '" + line + "': " + e.what() + "\n");
      }
    }
  }
}

int main(int argc, char** argv) {
  unsigned port = argc > 1 ? unsigned(std::stoul(argv[1])) : 0;  // 0이면 OS가 빈 포트 선택
#ifdef _WIN32
  WSADATA w;
  if (WSAStartup(MAKEWORD(2, 2), &w)) return 1;
#endif
  sock_t l = socket(AF_INET, SOCK_STREAM, 0);
  sockaddr_in a{};
  a.sin_family = AF_INET;
  a.sin_addr.s_addr = htonl(INADDR_LOOPBACK);  // 루프백 전용: 외부에 열지 않는다
  a.sin_port = htons(uint16_t(port));
  if (bind(l, (sockaddr*)&a, sizeof a) || listen(l, 4)) { std::fprintf(stderr, "bind/listen failed\n"); return 1; }
  socklen_t len = sizeof a;
  getsockname(l, (sockaddr*)&a, &len);
  std::cout << "LISTENING " << ntohs(a.sin_port) << std::endl;  // 러너가 이 줄로 포트를 알아낸다
  for (;;) {
    sock_t c = accept(l, nullptr, nullptr);
    if (c == (sock_t)-1) continue;
    serve(c);
    sock_close(c);
  }
}
