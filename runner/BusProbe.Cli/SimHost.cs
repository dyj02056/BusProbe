using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace BusProbe;

// 트레이스 한 줄 (시뮬레이터의 ev 줄과 1:1)
public record Ev(long TsNs, string Dir, string Node, string Bus, long Key, byte[] Raw);

public class SimException : Exception
{
    public SimException(string m) : base(m) { }
}

// busprobe-simd 프로세스를 띄우고, 시나리오마다 루프백 TCP로 한 번씩 접속한다
public sealed class SimHost : IDisposable
{
    readonly Process _proc;
    readonly int _port;

    public SimHost(string exe)
    {
        exe = Path.GetFullPath(exe); // 상대 경로와 / 구분자를 CreateProcess가 받아들이게 정규화
        if (!File.Exists(exe)) throw new SimException($"시뮬레이터 실행 파일 없음: {exe}");
        _proc = Process.Start(new ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false })
                ?? throw new SimException("시뮬레이터 시작 실패");
        // 첫 줄 "LISTENING <port>"로 포트를 알아낸다
        var read = Task.Run(() => _proc.StandardOutput.ReadLine());
        if (!read.Wait(5000) || read.Result?.StartsWith("LISTENING ") != true)
        {
            Dispose();
            throw new SimException("시뮬레이터가 5초 안에 포트를 알리지 않음");
        }
        _port = int.Parse(read.Result![10..]);
    }

    // 설정을 보내고 "done"까지 트레이스를 받는다. 시뮬레이터가 error를 보내면 SimException
    public List<Ev> Run(Scenario s)
    {
        using var c = new TcpClient();
        c.Connect("127.0.0.1", _port);
        c.ReceiveTimeout = s.DurationMs + 15000; // 실시간 모드는 duration만큼 걸린다
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
            if (t.Length != 7 || t[0] != "ev") throw new SimException("알 수 없는 응답: " + line);
            evs.Add(new Ev(long.Parse(t[1]), t[2], t[3], t[4], long.Parse(t[5]),
                t[6] == "-" ? Array.Empty<byte>() : Convert.FromHexString(t[6])));
        }
        throw new SimException("done 없이 연결이 끊어짐");
    }

    public void Dispose()
    {
        try { if (!_proc.HasExited) _proc.Kill(); } catch { /* 이미 종료 */ }
        _proc.Dispose();
    }
}
