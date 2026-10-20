using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Devinno.Communication.Mitsubishi.Data;
using Devinno.Communication.Mitsubishi.Ethernet;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Mitsubishi.Ethernet;

public class McEthernetTests : IDisposable
{
    readonly FakeMcPlc plc = new() { XYOctal = false };   // 3E(Q/L): X/Y 16진수
    readonly McEthernet mc;

    public McEthernetTests()
    {
        plc.Start(McFrame.E3);
        mc = new McEthernet { RemoteIP = "127.0.0.1", RemotePort = plc.Port, Timeout = 500, Interval = 1 };
    }

    public void Dispose()
    {
        mc.Dispose();
        plc.Dispose();
    }

    static TaskCompletionSource<T> Tcs<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    static Task<T> Wait<T>(TaskCompletionSource<T> t) => t.Task.WaitAsync(TimeSpan.FromSeconds(3));

    [Fact]
    public async Task AutoWR_ReadsFakeMemory_DeviceNameNormalized()
    {
        plc.Set("D100", 0x1234); plc.Set("D101", 0xFFFF);
        var tcs = Tcs<McEthernet.DataEventArgs>();
        mc.DataReceived += (s, e) => tcs.TrySetResult(e);
        mc.AutoWR(1, "d100", 2);
        mc.Start();
        var e = await Wait(tcs);
        Assert.Equal((1, McCommand.WR, "D100"), (e.MessageID, e.Command, e.Device));
        Assert.Equal(new long[] { 0x1234, 0xFFFF }, e.Values);
    }

    [Fact]
    public async Task AutoBR_OddCount_HexX()
    {
        plc.Set("X1F", 1); plc.Set("X21", 1);
        var tcs = Tcs<long[]>();
        mc.DataReceived += (s, e) => tcs.TrySetResult(e.Values);
        mc.AutoBR(1, "X1F", 3);
        mc.Start();
        Assert.Equal(new long[] { 1, 0, 1 }, await Wait(tcs));
    }

    [Fact]
    public async Task ManualWrites_UpdateFake()
    {
        var done = Tcs<bool>();
        int writes = 0;
        mc.WriteReceived += (s, e) => { if (Interlocked.Increment(ref writes) == 2) done.TrySetResult(true); };
        mc.Start();
        await TestUtil.Until(() => mc.IsOpen);
        mc.ManualBW(1, "M10", [1, 0, 1]);
        mc.ManualWW(2, "D20", [100, -1, 65535]);
        await Wait(done);
        Assert.Equal((1L, 0L, 1L), (plc.Get("M10"), plc.Get("M11"), plc.Get("M12")));
        Assert.Equal((100L, 0xFFFFL, 0xFFFFL), (plc.Get("D20"), plc.Get("D21"), plc.Get("D22")));
    }

    [Fact]
    public async Task ManualRunsBeforeAuto()
    {
        var order = new List<int>();
        var done = Tcs<bool>();
        mc.DataReceived += (s, e) => { lock (order) { order.Add(e.MessageID); if (order.Count == 2) done.TrySetResult(true); } };
        mc.AutoWR(1, "D0", 1);
        mc.ManualWR(2, "D0", 1);
        mc.Start();
        await Wait(done);
        Assert.Equal(new[] { 2, 1 }, order.Take(2));
    }

    [Fact]
    public async Task ForcedError_E3_RaisesNak_NoRetry()
    {
        plc.ForceError = 0xC051;
        var tcs = Tcs<int>();
        bool timedOut = false;
        mc.NAKReceived += (s, e) => tcs.TrySetResult(e.ErrorCode);
        mc.TimeoutReceived += (s, e) => timedOut = true;
        mc.Start();
        mc.ManualWR(1, "D0", 1, repeatCount: 2);
        Assert.Equal(0xC051, await Wait(tcs));
        await Task.Delay(200);
        Assert.False(timedOut);
        Assert.Equal(1, plc.Requests);
    }

    [Fact]
    public async Task Silent_RaisesTimeout_RepeatCountPlusOne()
    {
        plc.Silent = true;
        int count = 0;
        var done = Tcs<bool>();
        mc.Timeout = 100;
        mc.TimeoutReceived += (s, e) => { if (Interlocked.Increment(ref count) == 3) done.TrySetResult(true); };
        mc.Start();
        mc.ManualWR(1, "D0", 1, repeatCount: 2);
        await Wait(done);
        await Task.Delay(300);
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task LateResponse_IsDrainedBeforeNextRequest()
    {
        // 직접 만든 서버: 1번 요청에는 늦게(타임아웃 뒤) 응답하고, 2번 요청에는 바로 응답한다
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        _ = Task.Run(async () =>
        {
            using var c = await l.AcceptTcpClientAsync();
            var s = c.GetStream();
            async Task ReadRequest()
            {
                var h = new byte[9];
                await s.ReadExactlyAsync(h);
                await s.ReadExactlyAsync(new byte[h[7] | h[8] << 8]);
            }
            static byte[] Reply(int v) => [0xD0, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00, 0x04, 0x00, 0x00, 0x00, (byte)v, (byte)(v >> 8)];
            await ReadRequest();
            await Task.Delay(400);
            await s.WriteAsync(Reply(0x0BAD));   // 1번의 늦은 응답
            await ReadRequest();
            await s.WriteAsync(Reply(0x600D));
            await Task.Delay(1000);
        });
        using var m = new McEthernet { RemoteIP = "127.0.0.1", RemotePort = ((IPEndPoint)l.LocalEndpoint).Port, Timeout = 200 };
        var tcs = Tcs<(int, long)>();
        int scheduled = 0;
        m.DataReceived += (s, e) => tcs.TrySetResult((e.MessageID, e.Values[0]));
        m.TimeoutReceived += (s, e) =>
        {
            if (Interlocked.Increment(ref scheduled) == 1)
                _ = Task.Run(async () => { await Task.Delay(500); m.ManualWR(2, "D0", 1); });
        };
        m.ManualWR(1, "D0", 1);
        m.Start();
        Assert.Equal((2, 0x600DL), await Wait(tcs));
        l.Stop();
    }

    [Fact]
    public async Task BadSubheader_Reconnects()
    {
        // 직접 만든 서버: 첫 연결에는 엉뚱한 머리를 보내고, 두 번째 연결은 그냥 유지한다
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        _ = Task.Run(async () =>
        {
            using var c1 = await l.AcceptTcpClientAsync();
            var s = c1.GetStream();
            await s.ReadExactlyAsync(new byte[21]);
            await s.WriteAsync(new byte[] { 0xFF, 0xFF, 0x00, 0xFF, 0xFF, 0x03, 0x00, 0x02, 0x00, 0x00, 0x00 });
            using var c2 = await l.AcceptTcpClientAsync();
            await Task.Delay(2000);
        });
        using var m = new McEthernet { RemoteIP = "127.0.0.1", RemotePort = ((IPEndPoint)l.LocalEndpoint).Port, Timeout = 500 };
        int opened = 0, closed = 0;
        var twice = Tcs<bool>();
        m.DeviceOpened += (s, e) => { if (Interlocked.Increment(ref opened) == 2) twice.TrySetResult(true); };
        m.DeviceClosed += (s, e) => Interlocked.Increment(ref closed);
        m.ManualWR(1, "D0", 1);
        m.Start();
        Assert.True(await Wait(twice));
        Assert.Equal(1, Volatile.Read(ref closed));
        l.Stop();
    }

    [Fact]
    public async Task Stop_ReturnsQuickly_AndCanRestart()
    {
        var opened = Tcs<bool>();
        mc.DeviceOpened += (s, e) => opened.TrySetResult(true);
        mc.AutoWR(1, "D0", 1);
        mc.Start();
        await Wait(opened);
        var sw = Stopwatch.StartNew();
        mc.Stop();
        Assert.True(sw.ElapsedMilliseconds < 2000);
        Assert.False(mc.IsStart);

        var again = Tcs<bool>();
        mc.DataReceived += (s, e) => again.TrySetResult(true);
        mc.Start();
        Assert.True(await Wait(again));
    }

    [Fact]
    public void Register_Invalid_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => mc.AutoBR(1, "M0", 3585));
        Assert.ThrowsAny<ArgumentException>(() => mc.AutoWR(1, "D0", 961));
        Assert.ThrowsAny<ArgumentException>(() => mc.AutoBR(1, "D0", 1));
        Assert.ThrowsAny<ArgumentException>(() => mc.AutoWR(1, "Q0", 1));
        Assert.ThrowsAny<ArgumentException>(() => mc.ManualWW(1, "D0", [-32769]));
        mc.Frame = McFrame.E1;
        Assert.ThrowsAny<ArgumentException>(() => mc.AutoWR(1, "SM400", 1));   // 1E에 없음
        Assert.ThrowsAny<ArgumentException>(() => mc.AutoBR(1, "X1F", 1));     // E1 Auto = 8진수
        Assert.ThrowsAny<ArgumentException>(() => mc.AutoWR(1, "D0", 65));
        Assert.ThrowsAny<ArgumentException>(() => mc.AutoBR(1, "M0", 257));
        Assert.Null(mc.TakeNextWork());
        mc.AutoBR(1, "M0", 256);
        mc.Frame = McFrame.E3;
        mc.AutoWR(2, "SM400", 1);
        mc.AutoWR(3, "D0", 960);
    }

    [Fact]
    public void MonitoringTimer_RoundsUpTo250ms()
    {
        Assert.Equal(4, McEthernet.MonitoringTimer(1000));
        Assert.Equal(1, McEthernet.MonitoringTimer(1));
        Assert.Equal(2, McEthernet.MonitoringTimer(251));
        Assert.Equal(240, McEthernet.MonitoringTimer(60000));
        mc.ManualWR(1, "D0", 1, timeout: 2600);
        Assert.Equal(new byte[] { 0x0B, 0x00 }, mc.BuildRequest(mc.TakeNextWork()!)[9..11]);
    }

    [Fact]
    public void FrameAndNotation_CapturedAtRegistration()
    {
        mc.AutoBR(1, "X10", 1);            // E3, Auto = 16진수 → 10H
        mc.Frame = McFrame.E1;
        mc.AutoBR(2, "X10", 1);            // E1, Auto = 8진수 → 08H
        var a = mc.BuildRequest(mc.TakeNextWork()!);
        var b = mc.BuildRequest(mc.TakeNextWork()!);
        Assert.Equal(new byte[] { 0x50, 0x00 }, a[..2]);
        Assert.Equal((byte)0x10, a[15]);
        Assert.Equal(new byte[] { 0x00, 0xFF }, b[..2]);
        Assert.Equal((byte)0x08, b[4]);
    }
}

public class McEthernet1ETests : IDisposable
{
    readonly FakeMcPlc plc = new();   // FX3: X/Y 8진수
    readonly McEthernet mc;

    public McEthernet1ETests()
    {
        plc.Start(McFrame.E1);
        mc = new McEthernet { RemoteIP = "127.0.0.1", RemotePort = plc.Port, Frame = McFrame.E1, Timeout = 500, Interval = 1 };
    }

    public void Dispose()
    {
        mc.Dispose();
        plc.Dispose();
    }

    static TaskCompletionSource<T> Tcs<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    static Task<T> Wait<T>(TaskCompletionSource<T> t) => t.Task.WaitAsync(TimeSpan.FromSeconds(3));

    [Fact]
    public async Task ReadWrite_RoundTrip_OctalXY()
    {
        plc.Set("X17", 1); plc.Set("X20", 1);
        var done = Tcs<bool>();
        var reads = new Dictionary<int, long[]>();
        mc.DataReceived += (s, e) => { lock (reads) { reads[e.MessageID] = e.Values; if (reads.Count == 2) done.TrySetResult(true); } };
        mc.Start();
        await TestUtil.Until(() => mc.IsOpen);
        mc.ManualWW(1, "D100", [0x1234, -2]);
        mc.ManualBW(2, "Y10", [1, 1, 0]);
        mc.ManualBR(3, "X17", 2);
        mc.ManualWR(4, "D100", 2);
        await Wait(done);
        Assert.Equal(new long[] { 1, 1 }, reads[3]);
        Assert.Equal(new long[] { 0x1234, 0xFFFE }, reads[4]);
        Assert.Equal((1L, 1L, 0L), (plc.Get("Y10"), plc.Get("Y11"), plc.Get("Y12")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AbnormalError_ThreeOrFourBytes_StreamStaysAligned(bool trailingZero)
    {
        plc.AbnormalTrailingZero = trailingZero;
        plc.ForceError = 0x5B10;
        var nak = Tcs<int>();
        var data = Tcs<long>();
        mc.NAKReceived += (s, e) => { nak.TrySetResult(e.ErrorCode); plc.ForceError = null; };
        mc.DataReceived += (s, e) => data.TrySetResult(e.Values[0]);
        plc.Set("D0", 77);
        mc.Start();
        mc.ManualWR(1, "D0", 1);
        Assert.Equal(0x5B10, await Wait(nak));
        mc.ManualWR(2, "D0", 1);
        Assert.Equal(77, await Wait(data));
    }

    [Fact]
    public async Task AbnormalError_LateTrailingZero_IsSkipped_NoReconnect()
    {
        // 5BH 오류 본체를 먼저 보내고, 뒤따르는 00H는 다음 요청을 받은 뒤 따로 늦게 온다
        mc.Timeout = 1500;
        plc.ForceError = 0x5B10;
        plc.TrailingZeroDelayMs = 300;
        int opened = 0, closed = 0;
        var nak = Tcs<int>();
        var data = Tcs<long>();
        mc.DeviceOpened += (s, e) => Interlocked.Increment(ref opened);
        mc.DeviceClosed += (s, e) => Interlocked.Increment(ref closed);
        mc.NAKReceived += (s, e) => { nak.TrySetResult(e.ErrorCode); plc.ForceError = null; };
        mc.DataReceived += (s, e) => data.TrySetResult(e.Values[0]);
        plc.Set("D0", 77);
        mc.Start();
        mc.ManualWR(1, "D0", 1);
        Assert.Equal(0x5B10, await Wait(nak));
        mc.ManualWR(2, "D0", 1);
        Assert.Equal(77, await Wait(data));
        Assert.Equal((1, 0), (Volatile.Read(ref opened), Volatile.Read(ref closed)));
    }

    [Fact]
    public async Task EndCode_RaisesNak()
    {
        plc.ForceError = 0x56;
        var nak = Tcs<int>();
        mc.NAKReceived += (s, e) => nak.TrySetResult(e.ErrorCode);
        mc.Start();
        mc.ManualBR(1, "M0", 1);
        Assert.Equal(0x56, await Wait(nak));
    }
}
