// busprobe-simd: 루프백 TCP 서버. 연결 1개 = 시나리오 1개 (프로토콜은 docs/protocol.md).
// 줄 단위 텍스트: 설정 줄들 → "run" → 응답으로 "ev ..." 줄들과 "done N" (또는 "error ...").
#include <cstdio>
#include <iostream>
#include <string>

#include "protocol.hpp"
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
    for (auto& e : tr) out += format_event(e);
    out += "done " + std::to_string(tr.size()) + "\n";
  } catch (const std::exception& e) {
    out = std::string("error ") + e.what() + "\n";
  }
  send_all(c, out);
}

constexpr size_t MAX_LINE = 64 * 1024;     // 한 줄 최대 길이
constexpr size_t MAX_LINES = 100 * 1000;   // 시나리오 1개의 설정 줄 수 상한

// 연결 하나를 처리: 줄을 모아 "run"이 오면 실행하고 끝낸다
static void serve(sock_t c) {
  Scenario sc;
  std::string buf;
  char tmp[4096];
  size_t lines = 0;
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
      if (++lines > MAX_LINES) return send_all(c, "error too many config lines\n");
      try {
        apply_line(sc, line);
      } catch (const std::exception& e) {
        return send_all(c, "error bad line '" + line + "': " + e.what() + "\n");
      }
    }
    if (buf.size() > MAX_LINE) return send_all(c, "error line too long\n");  // 줄바꿈 없는 거대 입력 차단
  }
}

int main(int argc, char** argv) {
  unsigned port = 0;  // 0이면 OS가 빈 포트 선택
  try {
    if (argc > 1) port = unsigned(parse_uint(argv[1], 65535));
  } catch (const std::exception& e) {
    std::fprintf(stderr, "bad port: %s\n", e.what());
    return 2;
  }
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
