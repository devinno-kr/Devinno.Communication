using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Devinno.Communication.Secs.Hsms;

namespace Devinno.Communication.Tests.Fakes;

/// 테스트 전용 HSMS 상대. 규칙을 어기는 상대(늦은 응답, 깨진 길이, 짝 없는 응답, 다른 장치 ID, 무응답)를 흉내 내려고
/// 프레임을 바이트 그대로 주고받는다. 127.0.0.1에만 바인드·접속한다.
internal sealed class FakeHsmsPeer : IDisposable
{
    readonly TcpListener? listener;
    Socket? socket;

    FakeHsmsPeer(TcpListener? listener, Socket? socket)
    {
        this.listener = listener;
        this.socket = socket;
    }

    /// 접속을 기다리는 상대 (우리 Active가 접속해 옴). Port로 접속할 포트를 알려 준다.
    public static FakeHsmsPeer Listen()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return new FakeHsmsPeer(l, null);
    }

    /// 우리 Passive에 접속하는 상대
    public static FakeHsmsPeer Connect(int port)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        s.Connect(IPAddress.Loopback, port);
        return new FakeHsmsPeer(null, s);
    }

    public int Port => ((IPEndPoint)listener!.LocalEndpoint).Port;

    /// Listen일 때 접속을 받는다
    public void Accept(int timeoutMs = 5000)
    {
        var t = listener!.AcceptSocketAsync();
        Assert.True(t.Wait(timeoutMs), "접속이 오지 않았습니다.");
        socket?.Dispose();
        socket = t.Result;
        socket.NoDelay = true;
    }

    public void Send(byte[] bytes) => socket!.Send(bytes);

    /// 프레임 하나를 받는다. timeoutMs 안에 없으면 null. 연결이 닫혔으면 IOException
    public (HsmsHeader Header, byte[] Body)? Read(int timeoutMs = 3000)
    {
        var len = ReadExact(4, timeoutMs);
        if (len == null) return null;
        int n = (int)BinaryPrimitives.ReadUInt32BigEndian(len);
        var rest = ReadExact(n, 3000) ?? throw new IOException("프레임이 중간에 끊겼습니다.");
        return (HsmsHeader.Parse(rest.AsSpan(0, 10)), rest[10..]);
    }

    /// 지정한 SType 프레임이 올 때까지 읽는다(다른 프레임은 건너뜀)
    public (HsmsHeader Header, byte[] Body) ReadUntil(HsmsSType type, int timeoutMs = 3000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var left = (int)(until - Environment.TickCount64);
            Assert.True(left > 0, $"{type} 프레임이 오지 않았습니다.");
            var f = Read(left);
            if (f is { } x && x.Header.SType == (byte)type) return x;
        }
    }

    byte[]? ReadExact(int n, int timeoutMs)
    {
        var buf = new byte[n];
        int got = 0;
        var until = Environment.TickCount64 + timeoutMs;
        while (got < n)
        {
            var left = until - Environment.TickCount64;
            if (left <= 0)
            {
                if (got == 0) return null;
                throw new TimeoutException("프레임 일부만 왔습니다.");
            }
            if (!socket!.Poll((int)Math.Min(left, 100) * 1000, SelectMode.SelectRead)) continue;
            int r = socket.Receive(buf, got, n - got, SocketFlags.None);
            if (r == 0) throw new IOException("연결이 닫혔습니다.");
            got += r;
        }
        return buf;
    }

    /// 상대(우리 쪽)가 연결을 닫았는지: timeoutMs 안에 EOF가 오면 true (그 사이 온 프레임은 버림)
    public bool WaitClosed(int timeoutMs)
    {
        var until = Environment.TickCount64 + timeoutMs;
        var buf = new byte[4096];
        try
        {
            while (Environment.TickCount64 < until)
            {
                if (!socket!.Poll(50_000, SelectMode.SelectRead)) continue;
                if (socket.Receive(buf) == 0) return true;
            }
        }
        catch (SocketException) { return true; }
        return false;
    }

    /// Active인 우리 쪽의 Select.req에 상태 status로 답한다
    public HsmsHeader AnswerSelect(byte status = 0)
    {
        var req = ReadUntil(HsmsSType.SelectReq).Header;
        Send(HsmsFrame.Control(HsmsSType.SelectRsp, req.SystemBytes, req.SessionId, byte3: status));
        return req;
    }

    /// 우리 Passive에 Select를 요청하고 응답 상태를 돌려준다
    public byte Select(uint sys = 1)
    {
        Send(HsmsFrame.Control(HsmsSType.SelectReq, sys));
        var rsp = ReadUntil(HsmsSType.SelectRsp).Header;
        Assert.Equal(sys, rsp.SystemBytes);
        return rsp.Byte3;
    }

    public void Dispose()
    {
        try { socket?.Shutdown(SocketShutdown.Both); } catch (Exception) { }
        socket?.Dispose();
        listener?.Stop();
    }
}
