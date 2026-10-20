using System.Net.Sockets;
using System.Text;
using Devinno.Communication.TextComm;
using Devinno.Communication.TextComm.TCP;

namespace Devinno.Communication.Tests.TextComm.TCP;

public class TextCommTCPSlaveTests : IDisposable
{
    readonly TextCommTCPSlave slave;
    int requests;

    public TextCommTCPSlaveTests()
    {
        slave = new TextCommTCPSlave { LocalPort = 0 };
        // 명령 0x41: "OK " + 요청, 0x42: 메시지 없이 응답, 그 밖: 응답 안 함
        slave.MessageRequest += (s, e) =>
        {
            Interlocked.Increment(ref requests);
            if (e.Command == 0x41) e.ResponseMessage = "OK " + e.RequestMessage;
            else if (e.Command == 0x42) e.ResponseMessage = "";
        };
        slave.Start();
    }

    public void Dispose() => slave.Dispose();

    async Task<(TcpClient, NetworkStream)> Connect()
    {
        var c = new TcpClient();
        await c.ConnectAsync("127.0.0.1", slave.BoundPort);
        return (c, c.GetStream());
    }

    static async Task<byte[]> ReadExact(NetworkStream s, int count)
    {
        var buf = new byte[count];
        int n = 0;
        using var cts = new CancellationTokenSource(3000);
        while (n < count)
        {
            int r = await s.ReadAsync(buf.AsMemory(n), cts.Token);
            if (r == 0) throw new IOException("연결이 끊겼습니다.");
            n += r;
        }
        return buf;
    }

    /// 짧은 시간 안에 아무것도 오지 않는지 확인한다
    static async Task AssertSilent(NetworkStream s)
    {
        using var cts = new CancellationTokenSource(300);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await s.ReadAsync(new byte[16], cts.Token));
    }

    [Fact]
    public void Defaults()
    {
        var d = new TextCommTCPSlave();
        Assert.Equal((7897, 10), (d.LocalPort, d.MaxClients));
        Assert.Same(Encoding.ASCII, d.MessageEncoding);
    }

    [Fact]
    public async Task Request_GetsResponse()
    {
        var (c, s) = await Connect();
        using var _ = c;
        await s.WriteAsync(TextCommPacket.Build(1, 0x41, "READ"u8));
        var expected = TextCommPacket.Build(1, 0x41, "OK READ"u8);
        Assert.Equal(expected, await ReadExact(s, expected.Length));
    }

    [Fact]
    public async Task EmptyResponse_FrameWithoutMessage()
    {
        var (c, s) = await Connect();
        using var _ = c;
        await s.WriteAsync(TextCommPacket.Build(1, 0x42, "X"u8));
        Assert.Equal(new byte[] { 0x02, 0x01, 0x42, 0x43, 0x03 }, await ReadExact(s, 5));   // slave 1, command 0x42, 메시지 없음, sum 0x43
    }

    [Fact]
    public async Task NullResponse_NoReply_ThenNextRequestAnswered()
    {
        var (c, s) = await Connect();
        using var _ = c;
        await s.WriteAsync(TextCommPacket.Build(1, 0x50, "X"u8));
        await AssertSilent(s);
        await s.WriteAsync(TextCommPacket.Build(1, 0x41, "A"u8));
        var expected = TextCommPacket.Build(1, 0x41, "OK A"u8);
        Assert.Equal(expected, await ReadExact(s, expected.Length));
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task TwoFramesInOneSegment_TwoResponses()
    {
        var (c, s) = await Connect();
        using var _ = c;
        await s.WriteAsync(TextCommPacket.Build(1, 0x41, "A"u8).Concat(TextCommPacket.Build(2, 0x41, "B"u8)).ToArray());
        var a = TextCommPacket.Build(1, 0x41, "OK A"u8);
        var b = TextCommPacket.Build(2, 0x41, "OK B"u8);
        Assert.Equal(a.Concat(b).ToArray(), await ReadExact(s, a.Length + b.Length));
    }

    [Fact]
    public async Task FrameSplitAcrossSegments_OneResponse()
    {
        var (c, s) = await Connect();
        using var _ = c;
        var f = TextCommPacket.Build(0x10, 0x41, "SPLIT"u8);   // slave 0x10은 DLE 이스케이프 → 2번째 조각이 DLE 바로 뒤에서 시작
        await s.WriteAsync(f.AsMemory(0, 2));
        await Task.Delay(50);
        await s.WriteAsync(f.AsMemory(2));
        var expected = TextCommPacket.Build(0x10, 0x41, "OK SPLIT"u8);
        Assert.Equal(expected, await ReadExact(s, expected.Length));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task BadChecksum_NoEvent_NoReply_ConnectionKept()
    {
        var (c, s) = await Connect();
        using var _ = c;
        await s.WriteAsync(new byte[] { 0x02, 0x01, 0x41, 0x41, 0x00, 0x03 });
        await AssertSilent(s);
        Assert.Equal(0, requests);
        await s.WriteAsync(TextCommPacket.Build(1, 0x41, "A"u8));
        var expected = TextCommPacket.Build(1, 0x41, "OK A"u8);
        Assert.Equal(expected, await ReadExact(s, expected.Length));
    }

    [Fact]
    public async Task OversizedFrame_Dropped_ConnectionKept()
    {
        var (c, s) = await Connect();
        using var _ = c;
        await s.WriteAsync(TextCommPacket.Build(1, 0x41, new byte[9000]));   // 8192바이트 초과
        await AssertSilent(s);
        Assert.Equal(0, requests);
        await s.WriteAsync(TextCommPacket.Build(1, 0x41, "A"u8));
        var expected = TextCommPacket.Build(1, 0x41, "OK A"u8);
        Assert.Equal(expected, await ReadExact(s, expected.Length));
    }

    [Fact]
    public async Task HandlerThrows_NoReply()
    {
        slave.MessageRequest += (s, e) => { if (e.Command == 0x41) throw new InvalidOperationException(); };
        var (c, s) = await Connect();
        using var _ = c;
        await s.WriteAsync(TextCommPacket.Build(1, 0x41, "A"u8));
        await AssertSilent(s);
    }

    [Fact]
    public async Task ConnectEvents_Raised()
    {
        int connected = 0, disconnected = 0;
        slave.ClientConnected += (o, e) => Interlocked.Increment(ref connected);
        slave.ClientDisconnected += (o, e) => Interlocked.Increment(ref disconnected);
        var (c, _) = await Connect();
        await TestUtil.Until(() => connected == 1 && slave.ClientCount == 1);
        c.Dispose();
        await TestUtil.Until(() => disconnected == 1 && slave.ClientCount == 0);
    }

    [Fact]
    public async Task MaxClients_ExtraConnectionClosed()
    {
        slave.MaxClients = 1;
        var (c1, _) = await Connect();
        using var _1 = c1;
        await TestUtil.Until(() => slave.ClientCount == 1);
        var (c2, s2) = await Connect();
        using var _2 = c2;
        using var cts = new CancellationTokenSource(3000);
        Assert.Equal(0, await s2.ReadAsync(new byte[16], cts.Token));
    }

    [Fact]
    public async Task Stop_ClosesClients_ThenStartAgain()
    {
        var (c, s) = await Connect();
        using var _ = c;
        await TestUtil.Until(() => slave.ClientCount == 1);
        slave.Stop();
        Assert.False(slave.IsStart);
        using var cts = new CancellationTokenSource(3000);
        Assert.Equal(0, await s.ReadAsync(new byte[16], cts.Token));
        slave.Start();
        Assert.True(slave.IsStart);
        var (c2, s2) = await Connect();
        using var _2 = c2;
        await s2.WriteAsync(TextCommPacket.Build(1, 0x41, "A"u8));
        var expected = TextCommPacket.Build(1, 0x41, "OK A"u8);
        Assert.Equal(expected, await ReadExact(s2, expected.Length));
    }

    [Fact]
    public async Task EncodingFailure_NoReply_ConnectionKept()
    {
        slave.MessageEncoding = Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var (c, s) = await Connect();
        using var _ = c;
        // 요청 해석 실패: 0xC3은 ASCII가 아님 → 이벤트 없음
        await s.WriteAsync(TextCommPacket.Build(1, 0x41, new byte[] { 0xC3, 0xA9 }));
        // 응답 인코딩 실패: 응답 문자열이 ASCII가 아님 → 응답 없음
        slave.MessageRequest += (o, e) => { if (e.Command == 0x43) e.ResponseMessage = "é"; };
        await s.WriteAsync(TextCommPacket.Build(1, 0x43, "A"u8));
        await AssertSilent(s);
        Assert.Equal(1, requests);   // 0x43 요청만 이벤트 발생
        await s.WriteAsync(TextCommPacket.Build(1, 0x41, "A"u8));
        var expected = TextCommPacket.Build(1, 0x41, "OK A"u8);
        Assert.Equal(expected, await ReadExact(s, expected.Length));
    }
}
