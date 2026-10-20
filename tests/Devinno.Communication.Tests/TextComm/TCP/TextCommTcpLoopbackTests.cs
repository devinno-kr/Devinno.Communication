using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Devinno.Communication.TextComm;
using Devinno.Communication.TextComm.TCP;

namespace Devinno.Communication.Tests.TextComm.TCP;

public class TextCommTcpLoopbackTests : IDisposable
{
    readonly TextCommTCPSlave slave;
    readonly TextCommTCPMaster master;
    int requests;

    public TextCommTcpLoopbackTests()
    {
        slave = new TextCommTCPSlave { LocalPort = 0 };
        // 명령 0x41: "OK " + 요청, 0x42: 메시지 없이 응답, 0x43: 응답 안 함(null)
        slave.MessageRequest += (s, e) =>
        {
            Interlocked.Increment(ref requests);
            if (e.Command == 0x41) e.ResponseMessage = "OK " + e.RequestMessage;
            else if (e.Command == 0x42) e.ResponseMessage = "";
        };
        slave.Start();
        master = new TextCommTCPMaster { RemoteIP = "127.0.0.1", RemotePort = slave.BoundPort, Timeout = 500, Interval = 1 };
    }

    public void Dispose()
    {
        master.Dispose();
        slave.Dispose();
    }

    static TaskCompletionSource<T> Tcs<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    static Task<T> Wait<T>(TaskCompletionSource<T> tcs) => tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));

    [Fact]
    public void Defaults()
    {
        var m = new TextCommTCPMaster();
        Assert.Equal(("127.0.0.1", 7897, 100, 10, true, 1024), (m.RemoteIP, m.RemotePort, m.Timeout, m.Interval, m.AutoReconnect, m.BufferSize));
        Assert.Same(Encoding.ASCII, m.MessageEncoding);
    }

    [Fact]
    public async Task AutoSend_RoundTrip()
    {
        var tcs = Tcs<TextCommTCPMaster.MessageEventArgs>();
        master.MessageReceived += (s, e) => tcs.TrySetResult(e);
        master.AutoSend(1, 7, 0x41, "READ");
        master.Start();
        var e = await Wait(tcs);
        Assert.Equal((1, 7, 0x41, "OK READ"), (e.MessageID, e.Slave, e.Command, e.Message));
    }

    [Fact]
    public async Task AutoSend_Repeats()
    {
        int count = 0;
        master.MessageReceived += (s, e) => Interlocked.Increment(ref count);
        master.AutoSend(1, 1, 0x41, "A");
        master.Start();
        await TestUtil.Until(() => count >= 3);
    }

    [Fact]
    public async Task EmptyResponse_RaisesEmptyMessage()
    {
        var tcs = Tcs<string>();
        master.MessageReceived += (s, e) => tcs.TrySetResult(e.Message);
        master.Start();
        master.ManualSend(1, 1, 0x42, "PING");
        Assert.Equal("", await Wait(tcs));
    }

    [Fact]
    public async Task NullResponse_MasterTimesOut_RepeatCountPlusOne()
    {
        master.Timeout = 100;
        int timeouts = 0, received = 0;
        var done = Tcs<bool>();
        master.MessageReceived += (s, e) => Interlocked.Increment(ref received);
        master.TimeoutReceived += (s, e) => { if (Interlocked.Increment(ref timeouts) == 3) done.TrySetResult(true); };
        master.ManualSend(5, 1, 0x43, "X", repeatCount: 2);
        master.Start();
        await Wait(done);
        await Task.Delay(300);
        Assert.Equal((3, 0, 3), (timeouts, received, requests));
    }

    [Fact]
    public async Task ManualRunsBeforeAuto()
    {
        var order = new List<int>();
        var done = Tcs<bool>();
        master.MessageReceived += (s, e) => { lock (order) { order.Add(e.MessageID); if (order.Count == 2) done.TrySetResult(true); } };
        master.AutoSend(1, 1, 0x41, "AUTO");
        master.ManualSend(2, 1, 0x41, "MANUAL");
        master.Start();
        await Wait(done);
        Assert.Equal(new[] { 2, 1 }, order.Take(2));
    }

    [Fact]
    public async Task Utf8_BothSides()
    {
        slave.MessageEncoding = Encoding.UTF8;
        master.MessageEncoding = Encoding.UTF8;
        var tcs = Tcs<string>();
        master.MessageReceived += (s, e) => tcs.TrySetResult(e.Message);
        master.Start();
        master.ManualSend(1, 1, 0x41, "온도");
        Assert.Equal("OK 온도", await Wait(tcs));
    }

    [Fact]
    public async Task HandlerThrows_LoopContinues()
    {
        int count = 0;
        master.MessageReceived += (s, e) => { Interlocked.Increment(ref count); throw new InvalidOperationException(); };
        master.AutoSend(1, 1, 0x41, "A");
        master.Start();
        await TestUtil.Until(() => count >= 3);
    }

    [Fact]
    public async Task Stop_ReturnsQuickly_AndCanRestart()
    {
        var opened = Tcs<bool>();
        master.DeviceOpened += (s, e) => opened.TrySetResult(true);
        master.AutoSend(1, 1, 0x41, "A");
        master.Start();
        await Wait(opened);

        var sw = Stopwatch.StartNew();
        master.Stop();
        Assert.True(sw.ElapsedMilliseconds < 500, $"Stop took {sw.ElapsedMilliseconds} ms");
        Assert.False(master.IsStart);
        Assert.False(master.IsOpen);

        var again = Tcs<bool>();
        master.MessageReceived += (s, e) => again.TrySetResult(true);
        master.Start();
        Assert.True(await Wait(again));
    }

    [Fact]
    public async Task Stop_FromEventHandler_DoesNotDeadlock()
    {
        var stopped = Tcs<long>();
        master.MessageReceived += (s, e) =>
        {
            var sw = Stopwatch.StartNew();
            master.Stop();
            stopped.TrySetResult(sw.ElapsedMilliseconds);
        };
        master.AutoSend(1, 1, 0x41, "A");
        master.Start();
        long ms = await Wait(stopped);
        Assert.True(ms < 500, $"Stop in handler took {ms} ms");
        await TestUtil.Until(() => !master.IsStart);
    }

    [Fact]
    public async Task AutoReconnect_AfterSlaveRestart()
    {
        int opened = 0, closed = 0;
        master.DeviceOpened += (s, e) => Interlocked.Increment(ref opened);
        master.DeviceClosed += (s, e) => Interlocked.Increment(ref closed);
        master.AutoSend(1, 1, 0x41, "A");
        master.Start();
        await TestUtil.Until(() => opened == 1);

        int port = slave.BoundPort;
        slave.Stop();
        await TestUtil.Until(() => closed >= 1);
        slave.LocalPort = port;
        slave.Start();
        await TestUtil.Until(() => opened >= 2, 5000);
    }

    [Fact]
    public async Task ConnectFail_NoAutoReconnect_StopsLoop()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int freePort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var m = new TextCommTCPMaster { RemoteIP = "127.0.0.1", RemotePort = freePort, AutoReconnect = false };
        m.Start();
        await TestUtil.Until(() => !m.IsStart);
    }

    [Fact]
    public async Task SlaveClientEvents_RaisedForMaster()
    {
        int connected = 0, disconnected = 0;
        slave.ClientConnected += (s, e) => Interlocked.Increment(ref connected);
        slave.ClientDisconnected += (s, e) => Interlocked.Increment(ref disconnected);
        master.Start();
        await TestUtil.Until(() => connected == 1 && slave.ClientCount == 1);
        master.Stop();
        await TestUtil.Until(() => disconnected == 1 && slave.ClientCount == 0);
    }

    /// 가짜 Slave: 요청 하나를 받을 때마다 reply(요청 번호)를 돌려준다(null이면 응답 안 함)
    static async Task<(TcpListener, Task)> RawSlave(Func<int, byte[]?> reply, Action<int>? onRequest = null)
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var task = Task.Run(async () =>
        {
            using var c = await l.AcceptTcpClientAsync();
            var s = c.GetStream();
            var pending = new List<byte>();
            var buf = new byte[256];
            int index = 0;
            while (true)
            {
                int n;
                try { n = await s.ReadAsync(buf); } catch { return; }
                if (n == 0) return;
                pending.AddRange(buf[..n]);
                foreach (var f in TextCommPacket.Extract(pending, 1024))
                {
                    onRequest?.Invoke(index);
                    if (reply(index++) is byte[] r) await s.WriteAsync(r);
                }
            }
        });
        await Task.Yield();
        return (l, task);
    }

    [Fact]
    public async Task CheckSumError_FromRawSlave_RaisesEventAndRetries()
    {
        int requestsSeen = 0;
        var (l, _) = await RawSlave(i => i == 0 ? [0x02, 0x01, 0x41, 0x00, 0x03] : TextCommPacket.Build(1, 0x41, "GOOD"u8), i => Interlocked.Increment(ref requestsSeen));
        using var m = new TextCommTCPMaster { RemoteIP = "127.0.0.1", RemotePort = ((IPEndPoint)l.LocalEndpoint).Port, Timeout = 500, Interval = 1 };
        int sumErrors = 0, timeouts = 0;
        var tcs = Tcs<string>();
        m.CheckSumErrorReceived += (s, e) => Interlocked.Increment(ref sumErrors);
        m.TimeoutReceived += (s, e) => Interlocked.Increment(ref timeouts);
        m.MessageReceived += (s, e) => tcs.TrySetResult(e.Message);
        m.ManualSend(1, 1, 0x41, "X", repeatCount: 1);
        m.Start();
        Assert.Equal("GOOD", await Wait(tcs));
        Assert.Equal((1, 0, 2), (sumErrors, timeouts, requestsSeen));
        m.Stop();
        l.Stop();
    }

    [Fact]
    public async Task StaleResponse_FromRawSlave_IsSkipped()
    {
        // 요청 하나에 다른 명령의 응답(늦게 온 이전 응답)과 맞는 응답을 한 번에 보낸다
        var stale = TextCommPacket.Build(1, 0x40, "OLD"u8);
        var match = TextCommPacket.Build(1, 0x41, "NEW"u8);
        var (l, _) = await RawSlave(i => [.. stale, .. match]);
        using var m = new TextCommTCPMaster { RemoteIP = "127.0.0.1", RemotePort = ((IPEndPoint)l.LocalEndpoint).Port, Timeout = 500, Interval = 1 };
        var got = new List<string>();
        var tcs = Tcs<bool>();
        m.MessageReceived += (s, e) => { lock (got) got.Add(e.Message); tcs.TrySetResult(true); };
        m.ManualSend(1, 1, 0x41, "X");
        m.Start();
        await Wait(tcs);
        await Task.Delay(100);
        Assert.Equal(new[] { "NEW" }, got);
        m.Stop();
        l.Stop();
    }

    [Fact]
    public async Task LateBytes_AreDrainedBeforeNextRequest()
    {
        // 첫 요청에는 늦게(타임아웃 뒤) 응답하고, 둘째 요청에는 바로 응답한다. 늦은 응답은 둘째 요청 전에 버려져야 한다
        var late = TextCommPacket.Build(1, 0x41, "LATE"u8);
        var (l, _) = await RawSlave(i =>
        {
            if (i == 0) { Thread.Sleep(250); return late; }
            return TextCommPacket.Build(1, 0x41, "FRESH"u8);
        });
        using var m = new TextCommTCPMaster { RemoteIP = "127.0.0.1", RemotePort = ((IPEndPoint)l.LocalEndpoint).Port, Timeout = 100, Interval = 1 };
        var got = new List<string>();
        var tcs = Tcs<bool>();
        int timeouts = 0;
        m.TimeoutReceived += (s, e) => Interlocked.Increment(ref timeouts);
        m.MessageReceived += (s, e) => { lock (got) got.Add(e.Message); tcs.TrySetResult(true); };
        m.ManualSend(1, 1, 0x41, "FIRST");
        m.Start();
        await TestUtil.Until(() => timeouts == 1);
        await Task.Delay(300);   // 늦은 응답이 소켓에 도착할 때까지
        m.ManualSend(2, 1, 0x41, "SECOND");
        await Wait(tcs);
        Assert.Equal(new[] { "FRESH" }, got);
        m.Stop();
        l.Stop();
    }

    [Fact]
    public async Task UndecodableResponse_IsDiscarded_AndLoopContinues()
    {
        var strict = Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var (l, _) = await RawSlave(i => i == 0 ? TextCommPacket.Build(1, 0x41, [0x80, 0x41]) : TextCommPacket.Build(1, 0x41, "GOOD"u8));
        using var m = new TextCommTCPMaster { RemoteIP = "127.0.0.1", RemotePort = ((IPEndPoint)l.LocalEndpoint).Port, Timeout = 150, Interval = 1, MessageEncoding = strict };
        var got = new List<string>();
        int timeouts = 0;
        var tcs = Tcs<bool>();
        m.TimeoutReceived += (s, e) => Interlocked.Increment(ref timeouts);
        m.MessageReceived += (s, e) => { lock (got) got.Add(e.Message); tcs.TrySetResult(true); };
        m.ManualSend(1, 1, 0x41, "X");
        m.Start();
        await TestUtil.Until(() => timeouts == 1);
        Assert.Empty(got);
        m.ManualSend(2, 1, 0x41, "Y");
        await Wait(tcs);
        Assert.Equal(new[] { "GOOD" }, got);
        m.Stop();
        l.Stop();
    }

    [Fact]
    public void TakeNextWork_ManualFirst()
    {
        master.AutoSend(1, 1, 0x41, "A");
        master.ManualSend(2, 1, 0x41, "B");
        Assert.Equal(2, master.TakeNextWork()!.MessageID);
        Assert.Equal(1, master.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void Register_Invalid_Throws()
    {
        Assert.Equal("slave", Assert.Throws<ArgumentOutOfRangeException>(() => master.AutoSend(1, 256, 0, "A")).ParamName);
        Assert.Equal("command", Assert.Throws<ArgumentOutOfRangeException>(() => master.ManualSend(1, 0, -1, "A")).ParamName);
        Assert.Equal("message", Assert.Throws<ArgumentNullException>(() => master.ManualSend(1, 0, 0, null!)).ParamName);
        Assert.Null(master.TakeNextWork());
    }
}
