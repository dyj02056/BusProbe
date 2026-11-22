using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace BusProbe;

// 트레이스 한 줄 (시뮬레이터의 ev 줄과 1:1)
public record Ev(long TsNs, string Dir, string Node, string Bus, long Key, byte[] Raw);

// Fatal: 시뮬레이터 프로세스나 연결 상태를 더는 믿을 수 없음 (재시작 필요).
// 시뮬레이터가 "error ..."로 정상 응답한 입력 오류는 Fatal이 아니다.
public class SimException : Exception
{
    public bool Fatal { get; }
    public SimException(string m, bool fatal = false) : base(m) { Fatal = fatal; }
}

// busprobe-simd 프로세스를 띄우고, 시나리오마다 루프백 TCP로 한 번씩 접속한다.
// 시뮬레이터가 멈추거나 죽으면 Restart()로 새 프로세스를 띄워 다음 시나리오에 영향이 없게 한다.
public sealed class SimHost : IDisposable
{
    const int StartTimeoutMs = 5000;
    const int ReplyMarginMs = 15000; // 실시간 모드는 duration만큼 걸리므로 그 위에 더하는 여유

    readonly string _exe;
    Process? _proc;
    int _port;

    public SimHost(string exe)
    {
        _exe = Path.GetFullPath(exe); // 상대 경로와 / 구분자를 CreateProcess가 받아들이게 정규화
        if (!File.Exists(_exe)) throw new SimException($"시뮬레이터 실행 파일 없음: {_exe}");
        Start();
    }

    public int Restarts { get; private set; }

    void Start()
    {
        var p = Process.Start(new ProcessStartInfo(_exe) { RedirectStandardOutput = true, UseShellExecute = false })
                ?? throw new SimException("시뮬레이터 시작 실패", fatal: true);
        _proc = p;
        // 첫 줄 "LISTENING <port>"로 포트를 알아낸다
        var read = Task.Run(() => p.StandardOutput.ReadLine());
        if (!read.Wait(StartTimeoutMs) || read.Result?.StartsWith("LISTENING ") != true || !int.TryParse(read.Result[10..], out _port))
        {
            Stop();
            throw new SimException("시뮬레이터가 5초 안에 포트를 알리지 않음", fatal: true);
        }
    }

    void Stop()
    {
        if (_proc is null) return;
        try { if (!_proc.HasExited) { _proc.Kill(); _proc.WaitForExit(2000); } } catch { /* 이미 종료 */ }
        _proc.Dispose();
        _proc = null;
    }

    // 현재 프로세스를 버린다. 다음 Run()에서 새로 띄운다 (시작 실패가 이번 케이스 결과를 덮지 않게)
    public void Restart()
    {
        Stop();
        Restarts++;
    }

    // 설정을 보내고 "done"까지 트레이스를 받는다
    public List<Ev> Run(Scenario s)
    {
        if (_proc is null || _proc.HasExited) { Stop(); Start(); }
        try
        {
            using var c = new TcpClient();
            c.Connect("127.0.0.1", _port);
            c.ReceiveTimeout = (int)Math.Min(int.MaxValue, (long)s.DurationMs + ReplyMarginMs);
            var ns = c.GetStream();
            var req = Encoding.ASCII.GetBytes(ScenarioLoader.BuildConfig(s) + "run\n");
            ns.Write(req, 0, req.Length);

            var evs = new List<Ev>();
            using var rd = new StreamReader(ns, Encoding.ASCII);
            for (string? line; (line = rd.ReadLine()) is not null;)
            {
                if (line.StartsWith("error ")) throw new SimException(line[6..]);
                if (line.StartsWith("done ")) return evs;
                var t = line.Split(' ');
                if (t.Length != 7 || t[0] != "ev") throw new SimException("알 수 없는 응답: " + line, fatal: true);
                evs.Add(new Ev(long.Parse(t[1]), t[2], t[3], t[4], long.Parse(t[5]),
                    t[6] == "-" ? Array.Empty<byte>() : Convert.FromHexString(t[6])));
            }
            throw new SimException("done 없이 연결이 끊어짐", fatal: true);
        }
        catch (Exception e) when (e is IOException or SocketException or FormatException or OverflowException or ObjectDisposedException)
        {
            throw new SimException("시뮬레이터 통신 실패 (응답 없음 또는 형식 오류): " + e.Message, fatal: true);
        }
    }

    public void Dispose() => Stop();
}
