using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Devinno.Communication.Modbus.TCP;

namespace Devinno.Communication.Tests.Modbus;

public class ModbusTcpLoopbackTests : IDisposable
{
    readonly int[] hold = new int[100];
    readonly bool[] coils = new bool[100];
    readonly ModbusTCPSlave slave;
    readonly ModbusTCPMaster master;

    public ModbusTcpLoopbackTests()
    {
        slave = new ModbusTCPSlave { LocalPort = 0, Slave = 1, HoldingRegisters = hold, Coils = coils };
        slave.Start();
        master = new ModbusTCPMaster { RemoteIP = "127.0.0.1", RemotePort = slave.BoundPort, Timeout = 500, Interval = 1 };
    }

    public void Dispose()
    {
        master.Dispose();
        slave.Dispose();
    }

    static TaskCompletionSource<T> Tcs<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    static Task<T> Wait<T>(TaskCompletionSource<T> tcs) => tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));

    [Fact]
    public async Task AutoRead_FC3_ReceivesSlaveArray()
    {
        hold[0] = 11; hold[1] = 22; hold[2] = 0xFFFF;
        var tcs = Tcs<int[]>();
        master.WordReadReceived += (s, e) => { if (e.MessageID == 1) tcs.TrySetResult(e.ReceiveData); };
        master.AutoWordRead_FC3(1, 1, 0, 3);
        master.Start();
        Assert.Equal(new[] { 11, 22, 0xFFFF }, await Wait(tcs));
    }

    [Fact]
    public async Task AutoRead_RepeatsAndSeesArrayChanges()
    {
        var tcs = Tcs<bool>();
        master.WordReadReceived += (s, e) => { if (e.ReceiveData[0] == 99) tcs.TrySetResult(true); };
        master.AutoWordRead_FC3(1, 1, 5, 1);
        master.Start();
        await Task.Delay(100);
        hold[5] = 99;
        Assert.True(await Wait(tcs));
    }

    [Fact]
    public async Task ManualWrite_FC16_UpdatesSlave()
    {
        var tcs = Tcs<int[]>();
        master.MultiWordWriteReceived += (s, e) => tcs.TrySetResult(e.WriteValues);
        master.Start();
        master.ManualMultiWordWrite_FC16(2, 1, 10, new[] { 7, 8, 9 });
        Assert.Equal(new[] { 7, 8, 9 }, await Wait(tcs));
        Assert.Equal(new[] { 7, 8, 9 }, hold[10..13]);
    }

    [Fact]
    public async Task ManualWrite_FC5_And_FC26()
    {
        var bit = Tcs<bool>();
        var bitSet = Tcs<int>();
        master.BitWriteReceived += (s, e) => bit.TrySetResult(e.WriteValue);
        master.WordBitSetReceived += (s, e) => bitSet.TrySetResult(e.BitIndex);
        master.Start();
        master.ManualBitWrite_FC5(1, 1, 3, true);
        master.ManualWordBitSet_FC26(2, 1, 0, 2, true);
        Assert.True(await Wait(bit));
        Assert.Equal(2, await Wait(bitSet));
        Assert.True(coils[3]);
        Assert.Equal(4, hold[0]);
    }

    [Fact]
    public async Task ReadOutOfRange_RaisesException02()
    {
        var tcs = Tcs<int>();
        bool timedOut = false;
        master.ExceptionReceived += (s, e) => tcs.TrySetResult(e.ExceptionCode);
        master.TimeoutReceived += (s, e) => timedOut = true;
        master.Start();
        master.ManualWordRead_FC3(1, 1, 99, 2);
        Assert.Equal(2, await Wait(tcs));
        Assert.False(timedOut);
    }

    [Fact]
    public async Task Manual_NoResponse_RaisesTimeoutRepeatCountPlusOne()
    {
        var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var accepted = silent.AcceptTcpClientAsync();
        using var m = new ModbusTCPMaster { RemoteIP = "127.0.0.1", RemotePort = ((IPEndPoint)silent.LocalEndpoint).Port, Timeout = 100, Interval = 1 };
        int count = 0;
        var done = Tcs<bool>();
        m.TimeoutReceived += (s, e) => { if (Interlocked.Increment(ref count) == 3) done.TrySetResult(true); };
        m.ManualWordRead_FC3(5, 1, 0, 1, repeatCount: 2);
        m.Start();
        await Wait(done);
        await Task.Delay(300);
        Assert.Equal(3, count);
        m.Stop();
        (await accepted).Dispose();
        silent.Stop();
    }

    [Fact]
    public async Task HandlerThrows_LoopContinues()
    {
        int count = 0;
        master.WordReadReceived += (s, e) => { Interlocked.Increment(ref count); throw new InvalidOperationException(); };
        master.AutoWordRead_FC3(1, 1, 0, 1);
        master.Start();
        await TestUtil.Until(() => count >= 3);
    }

    [Fact]
    public async Task Stop_ReturnsQuickly_AndCanRestart()
    {
        var opened = Tcs<bool>();
        master.DeviceOpened += (s, e) => opened.TrySetResult(true);
        master.AutoWordRead_FC3(1, 1, 0, 1);
        master.Start();
        await Wait(opened);

        var sw = Stopwatch.StartNew();
        master.Stop();
        Assert.True(sw.ElapsedMilliseconds < 2000);
        Assert.False(master.IsStart);
        Assert.False(master.IsOpen);

        var again = Tcs<bool>();
        master.WordReadReceived += (s, e) => again.TrySetResult(true);
        master.Start();
        Assert.True(await Wait(again));
    }

    [Fact]
    public void Stop_ClearsIsStartImmediately()
    {
        master.AutoWordRead_FC3(1, 1, 0, 1);
        master.Start();
        Assert.True(master.IsStart);
        master.Stop();
        Assert.False(master.IsStart);
    }

    [Fact]
    public async Task Stop_FromEventHandler_DoesNotDeadlock()
    {
        var stopped = Tcs<bool>();
        master.WordReadReceived += (s, e) => { master.Stop(); stopped.TrySetResult(true); };
        master.AutoWordRead_FC3(1, 1, 0, 1);
        master.Start();
        await Wait(stopped);
        await TestUtil.Until(() => !master.IsStart);
    }

    [Fact]
    public async Task AutoReconnect_AfterSlaveRestart()
    {
        int opened = 0, closed = 0;
        master.DeviceOpened += (s, e) => Interlocked.Increment(ref opened);
        master.DeviceClosed += (s, e) => Interlocked.Increment(ref closed);
        master.AutoWordRead_FC3(1, 1, 0, 1);
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
        using var m = new ModbusTCPMaster { RemoteIP = "127.0.0.1", RemotePort = freePort, AutoReconnect = false };
        m.Start();
        await TestUtil.Until(() => !m.IsStart);
    }

    [Fact]
    public void TakeNextWork_ManualFirst()
    {
        master.AutoWordRead_FC3(1, 1, 0, 1);
        master.ManualWordWrite_FC6(2, 1, 0, 1);
        Assert.Equal(2, master.TakeNextWork()!.MessageID);
        Assert.Equal(1, master.TakeNextWork()!.MessageID);
    }
}
