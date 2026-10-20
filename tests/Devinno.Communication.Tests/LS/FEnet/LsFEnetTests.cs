using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Devinno.Communication.LS.Data;
using Devinno.Communication.LS.FEnet;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.LS.FEnet;

public class LsFEnetTests : IDisposable
{
    readonly FakeXgtPlc plc = new();
    readonly LsFEnet fenet;

    public LsFEnetTests()
    {
        plc.Start();
        fenet = new LsFEnet { RemoteIP = "127.0.0.1", RemotePort = plc.Port, Timeout = 500, Interval = 1 };
    }

    public void Dispose()
    {
        fenet.Dispose();
        plc.Dispose();
    }

    static TaskCompletionSource<T> Tcs<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    static Task<T> Wait<T>(TaskCompletionSource<T> t) => t.Task.WaitAsync(TimeSpan.FromSeconds(3));

    [Fact]
    public async Task AutoRSS_ReadsFakeMemory()
    {
        plc.Set("%MW100", 0x1234); plc.Set("%MW105", 77);
        var tcs = Tcs<LsFEnet.DataEventArgs>();
        fenet.DataReceived += (s, e) => tcs.TrySetResult(e);
        fenet.AutoRSS(1, ["%MW100", "%MW105"]);
        fenet.Start();
        var e = await Wait(tcs);
        Assert.Equal((1, LsCommand.RSS), (e.MessageID, e.Command));
        Assert.Equal(new long[] { 0x1234, 77 }, e.Values);
    }

    [Fact]
    public async Task DataEventArgs_Devices_IsCopy()
    {
        var second = Tcs<string>();
        int n = 0;
        fenet.DataReceived += (s, e) =>
        {
            if (Interlocked.Increment(ref n) == 1) e.Devices[0] = "%MW999";
            else second.TrySetResult(e.Devices[0]);
        };
        fenet.AutoRSS(1, ["%MW100"]);
        fenet.Start();
        Assert.Equal("%MW100", await Wait(second));
        Assert.Equal("%MW100", ((LsFEnetRssConfig)fenet.ToConfig().Autos[0]).Devices);
    }

    [Fact]
    public async Task AutoRSB_Words_ConvertedToBytes()
    {
        for (int i = 0; i < 5; i++) plc.Set($"%DW{10 + i}", i + 1);
        var tcs = Tcs<long[]>();
        fenet.DataReceived += (s, e) => tcs.TrySetResult(e.Values);
        fenet.AutoRSB(1, "%DW10", 5);
        fenet.Start();
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, await Wait(tcs));
    }

    [Fact]
    public async Task ManualWrites_UpdateFake()
    {
        var done = Tcs<bool>();
        int writes = 0;
        fenet.WriteReceived += (s, e) => { if (Interlocked.Increment(ref writes) == 2) done.TrySetResult(true); };
        fenet.Start();
        await TestUtil.Until(() => fenet.IsOpen);
        fenet.ManualWSS(1, [new LsValue("%MX3", 1), new LsValue("%MX4", 1)]);
        fenet.ManualWSB(2, "%MW20", [100, -1, 300]);
        await Wait(done);
        Assert.Equal((1L, 1L), (plc.Get("%MX3"), plc.Get("%MX4")));
        Assert.Equal((100L, 0xFFFFL, 300L), (plc.Get("%MW20"), plc.Get("%MW21"), plc.Get("%MW22")));
    }

    [Fact]
    public async Task ForcedError_RaisesNakReceived()
    {
        plc.ForceNak = 0x0004;
        var tcs = Tcs<int>();
        bool timedOut = false;
        fenet.NAKReceived += (s, e) => tcs.TrySetResult(e.ErrorCode);
        fenet.TimeoutReceived += (s, e) => timedOut = true;
        fenet.Start();
        fenet.ManualRSS(1, ["%MW0"], repeatCount: 2);
        Assert.Equal(4, await Wait(tcs));
        await Task.Delay(200);
        Assert.False(timedOut);
    }

    [Fact]
    public async Task Silent_RaisesTimeout_RepeatCountPlusOne()
    {
        plc.Silent = true;
        int count = 0;
        var done = Tcs<bool>();
        fenet.Timeout = 100;
        fenet.TimeoutReceived += (s, e) => { if (Interlocked.Increment(ref count) == 3) done.TrySetResult(true); };
        fenet.Start();
        fenet.ManualRSS(1, ["%MW0"], repeatCount: 2);
        await Wait(done);
        await Task.Delay(300);
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task StaleInvokeId_IsDiscarded()
    {
        // 직접 만든 서버: 먼저 Invoke ID가 다른 응답, 이어서 맞는 응답을 보낸다
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        _ = Task.Run(async () =>
        {
            using var c = await l.AcceptTcpClientAsync();
            var s = c.GetStream();
            var head = new byte[20];
            await s.ReadExactlyAsync(head);
            var body = new byte[BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(16))];
            await s.ReadExactlyAsync(body);
            int invoke = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(14));
            byte[] Reply(int id, ushort value)
            {
                byte[] b = [0x55, 0, 2, 0, 0, 0, 0, 0, 1, 0, 2, 0, (byte)value, (byte)(value >> 8)];
                var h = new byte[20];
                "LSIS-XGT"u8.CopyTo(h);
                h[13] = 0x11;
                BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(14), (ushort)id);
                BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(16), (ushort)b.Length);
                return [.. h, .. b];
            }
            await s.WriteAsync(Reply(invoke + 100, 0xBAD));
            await s.WriteAsync(Reply(invoke, 0x600D));
            await Task.Delay(1000);
        });
        using var m = new LsFEnet { RemoteIP = "127.0.0.1", RemotePort = ((IPEndPoint)l.LocalEndpoint).Port, Timeout = 1000 };
        var tcs = Tcs<long>();
        m.DataReceived += (s, e) => tcs.TrySetResult(e.Values[0]);
        m.ManualRSS(1, ["%MW0"]);
        m.Start();
        Assert.Equal(0x600D, await Wait(tcs));
        l.Stop();
    }

    [Fact]
    public async Task Stop_ReturnsQuickly_AndCanRestart()
    {
        var opened = Tcs<bool>();
        fenet.DeviceOpened += (s, e) => opened.TrySetResult(true);
        fenet.AutoRSS(1, ["%MW0"]);
        fenet.Start();
        await Wait(opened);
        var sw = Stopwatch.StartNew();
        fenet.Stop();
        Assert.True(sw.ElapsedMilliseconds < 2000);
        Assert.False(fenet.IsStart);

        var again = Tcs<bool>();
        fenet.DataReceived += (s, e) => again.TrySetResult(true);
        fenet.Start();
        Assert.True(await Wait(again));
    }

    [Fact]
    public void Register_Invalid_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => fenet.AutoRSB(1, "%IW0.0.0", 1));
        Assert.ThrowsAny<ArgumentException>(() => fenet.AutoRSB(1, "%MW0", 701));
        Assert.ThrowsAny<ArgumentException>(() => fenet.AutoRSS(1, ["%MW0", "%MX0"]));
        Assert.Null(fenet.TakeNextWork());
    }
}
