using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Devinno.Communication.Tests.Secs;

/// Python과 secsgem(LGPL, 별도 프로세스로만 실행·라이브러리에 포함하지 않음)이 있을 때만 실행하는 테스트. 없으면 건너뛴다.
/// Python 실행 파일은 환경 변수 DEVINNO_PYTHON(없으면 PATH의 python). secsgem은 그 Python에 설치돼 있거나 PYTHONPATH로 찾을 수 있어야 한다.
public sealed class SecsgemFactAttribute : FactAttribute
{
    public SecsgemFactAttribute()
    {
        if (!Secsgem.IsAvailable) Skip = $"Python({Secsgem.Python})이나 secsgem이 없어 건너뜀 (pip install secsgem)";
    }
}

internal static class Secsgem
{
    public static string Python => Environment.GetEnvironmentVariable("DEVINNO_PYTHON") is { Length: > 0 } p ? p : "python";
    static readonly Lazy<bool> available = new(Probe);
    public static bool IsAvailable => available.Value;

    static bool Probe()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Python, "-c \"import secsgem.hsms, secsgem.gem\"")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            });
            if (p == null) return false;
            if (!p.WaitForExit(20000)) { p.Kill(true); return false; }
            return p.ExitCode == 0;
        }
        catch (Exception) { return false; }
    }

    /// 127.0.0.1에서 비어 있는 포트
    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}

/// secsgem 스크립트를 별도 프로세스로 실행한다. 표준 출력은 줄 단위로 모으고, Dispose하면 표준 입력을 닫아 끝내게 한 뒤 기다린다.
internal sealed class SecsgemProcess : IDisposable
{
    readonly Process process;
    readonly string scriptPath;
    readonly BlockingCollection<string> lines = [];
    readonly ConcurrentQueue<string> errors = new();

    public SecsgemProcess(string script, params string[] args)
    {
        scriptPath = Path.Combine(Path.GetTempPath(), $"devinno-secsgem-{Guid.NewGuid():N}.py");
        File.WriteAllText(scriptPath, script);
        var psi = new ProcessStartInfo(Secsgem.Python)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(scriptPath);
        foreach (var a in args) psi.ArgumentList.Add(a);
        process = Process.Start(psi)!;
        process.OutputDataReceived += (s, e) => { if (e.Data != null) lines.Add(e.Data); };
        process.ErrorDataReceived += (s, e) => { if (e.Data != null) errors.Enqueue(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    /// 스크립트의 표준 입력에 한 줄을 쓴다(스크립트에 동작을 시킬 때)
    public void Send(string line)
    {
        process.StandardInput.WriteLine(line);
        process.StandardInput.Flush();
    }

    /// prefix로 시작하는 줄이 올 때까지 기다려 돌려준다
    public string WaitLine(string prefix, int timeoutMs = 20000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var left = (int)(until - Environment.TickCount64);
            if (left <= 0 || !lines.TryTake(out var line, left))
                throw new TimeoutException($"secsgem 출력 \"{prefix}\"이(가) 오지 않았습니다. stderr: {string.Join(" / ", errors.TakeLast(10))}");
            if (line.StartsWith(prefix, StringComparison.Ordinal)) return line;
        }
    }

    public void Dispose()
    {
        try { process.StandardInput.Close(); } catch (Exception) { }
        if (!process.WaitForExit(10000)) { try { process.Kill(true); } catch (Exception) { } }
        process.Dispose();
        try { File.Delete(scriptPath); } catch (Exception) { }
    }
}
