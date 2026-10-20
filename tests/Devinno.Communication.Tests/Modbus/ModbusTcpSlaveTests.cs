using System.Net.Sockets;
using Devinno.Communication.Modbus;
using Devinno.Communication.Modbus.TCP;

namespace Devinno.Communication.Tests.Modbus;

public class ModbusTcpSlaveTests : IDisposable
{
    readonly int[] hold = new int[10];
    readonly ModbusTCPSlave slave;

    public ModbusTcpSlaveTests()
    {
        slave = new ModbusTCPSlave { LocalPort = 0, Slave = 1, HoldingRegisters = hold };
        slave.Start();
    }

    public void Dispose() => slave.Dispose();

    [Fact]
    public void FrameLength_Cases()
    {
        Assert.Null(ModbusTcpFrame.FrameLength(new byte[] { 0, 1, 0, 0, 0, 6 }));
        Assert.Equal(12, ModbusTcpFrame.FrameLength(new byte[] { 0, 1, 0, 0, 0, 6, 1 }));
        Assert.Equal(-1, ModbusTcpFrame.FrameLength(new byte[] { 0, 1, 0, 1, 0, 6, 1 }));
        Assert.Equal(-1, ModbusTcpFrame.FrameLength(new byte[] { 0, 1, 0, 0, 1, 0, 1 }));
    }

    [Fact]
    public void Build_Mbap()
    {
        Assert.Equal(new byte[] { 0x12, 0x34, 0, 0, 0, 6, 1, 3, 0, 0, 0, 1 },
            ModbusTcpFrame.Build(0x1234, 1, ModbusPdu.ReadRequest(ModbusFunction.WORDREAD_F3, 0, 1)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(255)]
    public void HandleFrame_AcceptedUnitIds_Respond(int unit)
    {
        hold[0] = 5;
        var res = slave.HandleFrame(ModbusTcpFrame.Build(9, unit, ModbusPdu.ReadRequest(ModbusFunction.WORDREAD_F3, 0, 1)));
        Assert.Equal(ModbusTcpFrame.Build(9, unit, new byte[] { 3, 2, 0, 5 }), res);
    }

    [Fact]
    public void HandleFrame_OtherUnit_NoResponse()
    {
        Assert.Null(slave.HandleFrame(ModbusTcpFrame.Build(9, 2, ModbusPdu.ReadRequest(ModbusFunction.WORDREAD_F3, 0, 1))));
    }

    static async Task<byte[]> ReadExact(NetworkStream s, int count)
    {
        var buf = new byte[count];
        int n = 0;
        using var cts = new CancellationTokenSource(3000);
        while (n < count) n += await s.ReadAsync(buf.AsMemory(n), cts.Token);
        return buf;
    }

    async Task<(TcpClient, NetworkStream)> Connect()
    {
        var c = new TcpClient();
        await c.ConnectAsync("127.0.0.1", slave.BoundPort);
        return (c, c.GetStream());
    }

    [Fact]
    public async Task TwoFramesInOneSegment_TwoResponses()
    {
        hold[0] = 1; hold[1] = 2;
        var (c, s) = await Connect();
        using var _ = c;
        var a = ModbusTcpFrame.Build(1, 1, ModbusPdu.ReadRequest(ModbusFunction.WORDREAD_F3, 0, 1));
        var b = ModbusTcpFrame.Build(2, 1, ModbusPdu.ReadRequest(ModbusFunction.WORDREAD_F3, 1, 1));
        await s.WriteAsync(a.Concat(b).ToArray());
        Assert.Equal(ModbusTcpFrame.Build(1, 1, new byte[] { 3, 2, 0, 1 }), await ReadExact(s, 11));
        Assert.Equal(ModbusTcpFrame.Build(2, 1, new byte[] { 3, 2, 0, 2 }), await ReadExact(s, 11));
    }

    [Fact]
    public async Task SplitFrame_OneResponse()
    {
        hold[3] = 33;
        var (c, s) = await Connect();
        using var _ = c;
        var f = ModbusTcpFrame.Build(7, 1, ModbusPdu.ReadRequest(ModbusFunction.WORDREAD_F3, 3, 1));
        await s.WriteAsync(f.AsMemory(0, 4));
        await Task.Delay(50);
        await s.WriteAsync(f.AsMemory(4));
        Assert.Equal(ModbusTcpFrame.Build(7, 1, new byte[] { 3, 2, 0, 33 }), await ReadExact(s, 11));
    }

    [Fact]
    public async Task AreaSetToNullWhileRunning_ReturnsException02()
    {
        var (c, s) = await Connect();
        using var _ = c;
        slave.HoldingRegisters = null;
        await s.WriteAsync(ModbusTcpFrame.Build(1, 1, ModbusPdu.ReadRequest(ModbusFunction.WORDREAD_F3, 0, 1)));
        Assert.Equal(ModbusTcpFrame.Build(1, 1, new byte[] { 0x83, 2 }), await ReadExact(s, 9));
        slave.HoldingRegisters = hold;
        await s.WriteAsync(ModbusTcpFrame.Build(2, 1, ModbusPdu.ReadRequest(ModbusFunction.WORDREAD_F3, 0, 1)));
        Assert.Equal(11, (await ReadExact(s, 11)).Length);
    }

    [Fact]
    public async Task Write_UpdatesArray_RaisesWordWritten()
    {
        var tcs = new TaskCompletionSource<ModbusTCPSlave.WrittenEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        slave.WordWritten += (o, e) => tcs.TrySetResult(e);
        var (c, s) = await Connect();
        using var _ = c;
        await s.WriteAsync(ModbusTcpFrame.Build(1, 1, ModbusPdu.WriteSingleRegister(4, 44)));
        await ReadExact(s, 12);
        var e = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(44, hold[4]);
        Assert.Equal((ModbusFunction.WORDWRITE_F6, 4, 1), (e.Function, e.StartAddress, e.Length));
    }

    [Fact]
    public async Task BrokenMbap_ClosesConnection()
    {
        var (c, s) = await Connect();
        using var _ = c;
        await s.WriteAsync(new byte[] { 0, 1, 0, 9, 0, 6, 1, 3, 0, 0, 0, 1 });
        using var cts = new CancellationTokenSource(3000);
        Assert.Equal(0, await s.ReadAsync(new byte[16], cts.Token));
    }

    [Fact]
    public async Task ConnectEvents_Raised()
    {
        int connected = 0, disconnected = 0;
        slave.ClientConnected += (o, e) => Interlocked.Increment(ref connected);
        slave.ClientDisconnected += (o, e) => Interlocked.Increment(ref disconnected);
        var (c, _) = await Connect();
        await TestUtil.Until(() => connected == 1);
        c.Dispose();
        await TestUtil.Until(() => disconnected == 1);
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
    public void Stop_ThenStartAgain()
    {
        slave.Stop();
        Assert.False(slave.IsStart);
        slave.Start();
        Assert.True(slave.IsStart);
    }
}
