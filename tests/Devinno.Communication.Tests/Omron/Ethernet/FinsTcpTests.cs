using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Devinno.Communication.Omron.Data;
using Devinno.Communication.Omron.Ethernet;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Omron.Ethernet;

public class FinsTcpTests : IDisposable
{
    readonly FakeOmronPlc plc = new() { Node = 0x0A };
    readonly FinsTcp fins;

    public FinsTcpTests()
    {
        plc.Start();
        fins = new FinsTcp { RemoteIP = "127.0.0.1", RemotePort = plc.TcpPort, Timeout = 500, Interval = 1 };
    }

    public void Dispose()
    {
        fins.Dispose();
        plc.Dispose();
    }

    static TaskCompletionSource<T> Tcs<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    static Task<T> Wait<T>(TaskCompletionSource<T> t) => t.Task.WaitAsync(TimeSpan.FromSeconds(3));

    async Task<FinsTcp.DataEventArgs> ReadOnce(Action register)
    {
        var tcs = Tcs<FinsTcp.DataEventArgs>();
        fins.DataReceived += (s, e) => tcs.TrySetResult(e);
        register();
        fins.Start();
        return await Wait(tcs);
    }

    async Task Open()
    {
        var opened = Tcs<bool>();
        fins.DeviceOpened += (s, e) => opened.TrySetResult(true);
        fins.Start();
        await Wait(opened);
    }

    #region 연결 / 노드 교환
    [Fact]
    public async Task Handshake_AutoNode_UsedInFinsHeader()
    {
        Assert.Equal((0, 0), (fins.AssignedClientNode, fins.ServerNode));
        await Open();
        Assert.True(fins.IsOpen);
        Assert.Equal((239, 0x0A, 0), (fins.AssignedClientNode, fins.ServerNode, plc.LastRequestedClient));
        await ReadOnce(() => fins.ManualRead(1, ["D0"]));
        Assert.Equal(new byte[] { 0x80, 0x00, 0x02, 0x00, 0x0A, 0x00, 0x00, 239, 0x00 }, plc.LastHeader[..9]);   // DA1 = 서버 노드, SA1 = 받은 클라이언트 노드
    }

    [Fact]
    public async Task Handshake_FixedClientNode()
    {
        fins.ClientNode = 5;
        await Open();
        Assert.Equal((5, 5), (plc.LastRequestedClient, fins.AssignedClientNode));
    }

    [Fact]
    public async Task HandshakeError_NeverOpens_RetriesEverySecond()
    {
        plc.HandshakeError = 0x21;
        int opened = 0;
        fins.DeviceOpened += (s, e) => Interlocked.Increment(ref opened);
        fins.Start();
        await TestUtil.Until(() => plc.Connections >= 2, 3000);
        Assert.Equal(0, Volatile.Read(ref opened));
        Assert.False(fins.IsOpen);
        Assert.Equal(0, fins.AssignedClientNode);
        Assert.True(fins.IsStart);
    }

    [Fact]
    public async Task ConnectFails_NoAutoReconnect_LoopEnds()
    {
        plc.HandshakeError = 0x20;
        fins.AutoReconnect = false;
        fins.Start();
        await TestUtil.Until(() => !fins.IsStart, 3000);
        await Task.Delay(1200);
        Assert.Equal(1, plc.Connections);
        Assert.False(fins.IsOpen);
    }

    [Fact]
    public async Task Stop_DuringStalledHandshake_ReturnsQuickly()
    {
        // 접속은 받지만 노드 주소 요청에 답하지 않는 서버
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        TcpClient? held = null;
        try
        {
            var accepted = Tcs<TcpClient>();
            _ = Task.Run(async () =>
            {
                try { accepted.TrySetResult(await l.AcceptTcpClientAsync()); }
                catch (Exception ex) { accepted.TrySetException(ex); }
            });
            using var m = new FinsTcp { RemoteIP = "127.0.0.1", RemotePort = ((IPEndPoint)l.LocalEndpoint).Port, Timeout = 10000 };
            m.Start();
            held = await Wait(accepted);
            await Task.Delay(200);
            var sw = Stopwatch.StartNew();
            m.Stop();
            Assert.True(sw.ElapsedMilliseconds < 1500, $"Stop {sw.ElapsedMilliseconds}ms");
            Assert.False(m.IsStart);
            Assert.False(m.IsOpen);
        }
        finally
        {
            held?.Dispose();
            l.Stop();
        }
    }

    [Fact]
    public void Properties_RangeChecked_AndDefaults()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => fins.ClientNode = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => fins.ClientNode = 255);
        Assert.Throws<ArgumentOutOfRangeException>(() => fins.MaxWordsPerRequest = 998);
        var d = new FinsTcp();
        Assert.Equal(("127.0.0.1", 9600, (int?)null, 499, 1000, 10, true), (d.RemoteIP, d.RemotePort, d.ClientNode, d.MaxWordsPerRequest, d.Timeout, d.Interval, d.AutoReconnect));
    }
    #endregion

    #region 읽기 / 쓰기
    [Fact]
    public async Task ReadWrite_RoundTrip_MultipleAndBits()
    {
        int writes = 0;
        fins.WriteReceived += (s, e) => Interlocked.Increment(ref writes);
        fins.ManualWrite(1, "D100", 0x1234);
        fins.ManualWrite(2, "W10.03", 1);
        var e = await ReadOnce(() => fins.ManualRead(3, ["D100", "W10.03", "W10"]));
        Assert.Equal(new long[] { 0x1234, 1, 8 }, e.Values);
        Assert.Equal(2, Volatile.Read(ref writes));
    }

    [Fact]
    public async Task ReadBlock_SplitAtMaxWordsPerRequest()
    {
        fins.MaxWordsPerRequest = 250;
        var pattern = Enumerable.Range(0, 600).Select(i => (ushort)(i * 3)).ToArray();
        plc.SetWords("E3_100", pattern);
        var e = await ReadOnce(() => fins.ManualReadBlock(1, "E3_100", 600));
        Assert.Equal(pattern.Select(x => (long)x), e.Values);
        Assert.Equal(new (byte, byte, int, int, int)[] { (1, 0xA3, 100, 0, 250), (1, 0xA3, 350, 0, 250), (1, 0xA3, 600, 0, 100) }, plc.Requests);
    }

    [Fact]
    public async Task FragmentedReplies_ConfirmationAndStale_AreHandled()
    {
        plc.FragmentSize = 3;
        plc.Confirmation = true;
        plc.StaleReply = true;
        var pattern = Enumerable.Range(0, 600).Select(i => (ushort)i).ToArray();
        plc.SetWords("D0", pattern);
        var e = await ReadOnce(() => fins.ManualReadBlock(1, "D0", 600));
        Assert.Equal(pattern.Select(x => (long)x), e.Values);
        Assert.True(fins.IsOpen);
        Assert.Equal(1, plc.Connections);
    }
    #endregion

    #region 오류
    [Fact]
    public async Task EndCode_RaisesNak_NoRetry()
    {
        plc.EndCode = 0x2102;
        var nak = Tcs<FinsTcp.NAKEventArgs>();
        int timeouts = 0;
        fins.NAKReceived += (s, e) => nak.TrySetResult(e);
        fins.TimeoutReceived += (s, e) => Interlocked.Increment(ref timeouts);
        fins.ManualWrite(1, "D0", 1, repeatCount: 3);
        fins.Start();
        var e = await Wait(nak);
        Assert.Equal((FinsCommand.Write, 0x2102), (e.Command, e.ErrorCode));
        await Task.Delay(200);
        Assert.Single(plc.Requests);
        Assert.Equal(0, Volatile.Read(ref timeouts));
    }

    [Fact]
    public async Task NonFatalFlag_IsNotError()
    {
        plc.FlagBits = 0x0040;
        plc.Set("D1", 11);
        Assert.Equal(11, (await ReadOnce(() => fins.ManualRead(1, ["D1"]))).Values[0]);
    }

    [Fact]
    public async Task Silent_RaisesTimeout_RepeatCountPlusOne()
    {
        plc.Silent = true;
        fins.Timeout = 100;
        int count = 0;
        var done = Tcs<bool>();
        fins.TimeoutReceived += (s, e) => { if (Interlocked.Increment(ref count) == 3) done.TrySetResult(true); };
        fins.ManualRead(1, ["D0"], repeatCount: 2);
        fins.Start();
        await Wait(done);
        await Task.Delay(300);
        Assert.Equal(3, Volatile.Read(ref count));
        Assert.Equal(1, plc.Connections);
    }

    [Fact]
    public async Task ErrorNotice_ClosesAndReconnects()
    {
        plc.ErrorNotice = true;
        int opened = 0, closed = 0, timeouts = 0;
        fins.DeviceOpened += (s, e) => Interlocked.Increment(ref opened);
        fins.DeviceClosed += (s, e) => Interlocked.Increment(ref closed);
        fins.TimeoutReceived += (s, e) => Interlocked.Increment(ref timeouts);
        fins.AutoRead(1, ["D0"]);
        fins.Start();
        await TestUtil.Until(() => Volatile.Read(ref opened) >= 2, 4000);
        Assert.True(Volatile.Read(ref closed) >= 1);
        Assert.True(Volatile.Read(ref timeouts) >= 1);
        plc.ErrorNotice = false;
        var data = Tcs<bool>();
        fins.DataReceived += (s, e) => data.TrySetResult(true);
        Assert.True(await Wait(data));
    }

    [Fact]
    public async Task ConnectionDropped_ReconnectsAndContinues()
    {
        int opened = 0, closed = 0, data = 0;
        fins.DeviceOpened += (s, e) => Interlocked.Increment(ref opened);
        fins.DeviceClosed += (s, e) => Interlocked.Increment(ref closed);
        fins.DataReceived += (s, e) => Interlocked.Increment(ref data);
        fins.AutoRead(1, ["D0"]);
        fins.Start();
        await TestUtil.Until(() => Volatile.Read(ref data) > 0);
        plc.Kick();
        await TestUtil.Until(() => Volatile.Read(ref opened) == 2, 4000);
        int before = Volatile.Read(ref data);
        await TestUtil.Until(() => Volatile.Read(ref data) > before);
        Assert.Equal(1, Volatile.Read(ref closed));
        Assert.Equal(240, fins.AssignedClientNode);   // 새 연결은 새 노드
    }

    [Fact]
    public async Task MalformedHeader_Reconnects()
    {
        // 직접 만든 서버: 노드 교환은 정상, 첫 요청에 "FINX" 머리 → 스트림이 깨진 것으로 보고 다시 접속
        var l = new TcpListener(IPAddress.Loopback, 0);
        using var cts = new CancellationTokenSource();
        l.Start();
        try
        {
            int accepted = 0;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        using var c = await l.AcceptTcpClientAsync(cts.Token);
                        Interlocked.Increment(ref accepted);
                        var s = c.GetStream();
                        var buf = new byte[512];
                        await s.ReadExactlyAsync(buf.AsMemory(0, 20), cts.Token);
                        await s.WriteAsync(Convert.FromHexString("46494E53000000100000000100000000000000EF0000000A"), cts.Token);
                        await s.ReadAsync(buf, cts.Token);
                        await s.WriteAsync(Convert.FromHexString("46494E5800000008000000020000000000000000"), cts.Token);
                        await Task.Delay(500, cts.Token);
                    }
                }
                catch { }
            });
            using var m = new FinsTcp { RemoteIP = "127.0.0.1", RemotePort = ((IPEndPoint)l.LocalEndpoint).Port, Timeout = 500, Interval = 1 };
            int opened = 0, closed = 0, timeouts = 0;
            m.DeviceOpened += (s, e) => Interlocked.Increment(ref opened);
            m.DeviceClosed += (s, e) => Interlocked.Increment(ref closed);
            m.TimeoutReceived += (s, e) => Interlocked.Increment(ref timeouts);
            m.AutoRead(1, ["D0"]);
            m.Start();
            await TestUtil.Until(() => Volatile.Read(ref opened) >= 2, 4000);
            Assert.True(Volatile.Read(ref closed) >= 1);
            Assert.True(Volatile.Read(ref timeouts) >= 1);
            Assert.True(Volatile.Read(ref accepted) >= 2);
            m.Stop();
        }
        finally
        {
            cts.Cancel();
            l.Stop();
        }
    }
    #endregion

    #region 스케줄 / 수명주기
    [Fact]
    public async Task ManualRunsBeforeAuto()
    {
        var first = Tcs<int>();
        fins.DataReceived += (s, e) => first.TrySetResult(e.MessageID);
        fins.AutoRead(1, ["D0"]);
        fins.ManualRead(2, ["D1"]);
        fins.Start();
        Assert.Equal(2, await Wait(first));
    }

    [Fact]
    public async Task Stop_ReturnsQuickly_AndCanRestart()
    {
        await Open();
        fins.AutoRead(1, ["D0"]);
        var sw = Stopwatch.StartNew();
        fins.Stop();
        Assert.True(sw.ElapsedMilliseconds < 2000);
        Assert.False(fins.IsStart);
        Assert.False(fins.IsOpen);
        Assert.Equal((0, 0), (fins.AssignedClientNode, fins.ServerNode));

        var again = Tcs<bool>();
        fins.DataReceived += (s, e) => again.TrySetResult(true);
        fins.Start();
        Assert.True(await Wait(again));
    }

    [Fact]
    public void Register_Invalid_Throws()
    {
        Assert.Throws<ArgumentException>(() => fins.AutoRead(1, ["C0"]));
        Assert.Contains("1~167", Assert.Throws<ArgumentException>(() => fins.AutoRead(1, [.. Enumerable.Range(0, 168).Select(i => $"D{i}")])).Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => fins.ManualReadBlock(1, "A959", 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => fins.ManualWrite(1, "D0", -32769));
        Assert.Null(fins.TakeNextWork());
    }
    #endregion
}
