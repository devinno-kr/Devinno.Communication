using Devinno.Communication.Modbus;
using Devinno.Communication.Modbus.RTU;

namespace Devinno.Communication.Tests.Modbus;

public class ModbusRtuSlaveTests
{
    [Fact]
    public void Build_AppendsSlaveAndCrc()
    {
        var f = ModbusRtuFrame.Build(1, new byte[] { 3, 0, 0, 0, 0x0A });
        Assert.Equal(new byte[] { 1, 3, 0, 0, 0, 0x0A, 0xC5, 0xCD }, f);
    }

    [Theory]
    [InlineData(new byte[] { 1 }, null)]
    [InlineData(new byte[] { 1, 3 }, 8)]
    [InlineData(new byte[] { 1, 6 }, 8)]
    [InlineData(new byte[] { 1, 16, 0, 0, 0, 2 }, null)]
    [InlineData(new byte[] { 1, 16, 0, 0, 0, 2, 4 }, 13)]
    [InlineData(new byte[] { 1, 26 }, 9)]
    [InlineData(new byte[] { 1, 7 }, null)]
    public void RequestLength_ByFunction(byte[] buffer, int? expected)
    {
        Assert.Equal(expected, ModbusRtuFrame.RequestLength(buffer));
    }

    static ModbusRTUSlave NewSlave(int[] hold) => new() { Slave = 1, HoldingRegisters = hold };

    [Fact]
    public void HandleFrame_ReadHolding_RespondsWithData()
    {
        var hold = new int[10]; hold[0] = 0x0102;
        var res = NewSlave(hold).HandleFrame(ModbusRtuFrame.Build(1, ModbusPdu.ReadRequest(ModbusFunction.WORDREAD_F3, 0, 1)));
        Assert.Equal(ModbusRtuFrame.Build(1, new byte[] { 3, 2, 0x01, 0x02 }), res);
    }

    [Fact]
    public void HandleFrame_OtherSlave_NoResponse()
    {
        var res = NewSlave(new int[10]).HandleFrame(ModbusRtuFrame.Build(2, ModbusPdu.ReadRequest(ModbusFunction.WORDREAD_F3, 0, 1)));
        Assert.Null(res);
    }

    [Fact]
    public void HandleFrame_BadCrc_NoResponse()
    {
        var f = ModbusRtuFrame.Build(1, ModbusPdu.ReadRequest(ModbusFunction.WORDREAD_F3, 0, 1));
        f[^1] ^= 0xFF;
        Assert.Null(NewSlave(new int[10]).HandleFrame(f));
    }

    [Fact]
    public void HandleFrame_Broadcast_WritesButNoResponse()
    {
        var hold = new int[10];
        var res = NewSlave(hold).HandleFrame(ModbusRtuFrame.Build(0, ModbusPdu.WriteSingleRegister(2, 77)));
        Assert.Null(res);
        Assert.Equal(77, hold[2]);
    }

    [Fact]
    public void HandleFrame_Write_RaisesWordWrittenAfterArrayUpdate()
    {
        var hold = new int[10];
        var slave = NewSlave(hold);
        int seen = -1;
        ModbusRTUSlave.WrittenEventArgs? args = null;
        slave.WordWritten += (s, e) => { args = e; seen = hold[5]; };
        slave.HandleFrame(ModbusRtuFrame.Build(1, ModbusPdu.WriteMultipleRegisters(5, new[] { 9, 8 })));
        Assert.Equal(9, seen);
        Assert.NotNull(args);
        Assert.Equal((1, ModbusFunction.MULTIWORDWRITE_F16, 5, 2), (args!.Slave, args.Function, args.StartAddress, args.Length));
    }

    [Fact]
    public void HandleFrame_CoilWrite_RaisesBitWritten()
    {
        var slave = new ModbusRTUSlave { Slave = 1, Coils = new bool[8] };
        int count = 0;
        slave.BitWritten += (s, e) => count++;
        slave.HandleFrame(ModbusRtuFrame.Build(1, ModbusPdu.WriteSingleCoil(1, true)));
        Assert.Equal(1, count);
        Assert.True(slave.Coils![1]);
    }

    [Fact]
    public void HandleFrame_HandlerThrows_StillResponds()
    {
        var slave = NewSlave(new int[10]);
        slave.WordWritten += (s, e) => throw new InvalidOperationException();
        Assert.NotNull(slave.HandleFrame(ModbusRtuFrame.Build(1, ModbusPdu.WriteSingleRegister(0, 1))));
    }

    [Fact]
    public async Task Start_InvalidPort_IsStartBecomesFalse()
    {
        using var slave = new ModbusRTUSlave { Port = "COM_NOT_EXIST", AutoReconnect = false };
        slave.Start();
        await TestUtil.Until(() => !slave.IsStart);
        Assert.False(slave.IsOpen);
    }

    [Fact]
    public void Stop_ClearsIsStartImmediately_AndStartRestarts()
    {
        using var slave = new ModbusRTUSlave { Port = "COM_NOT_EXIST", AutoReconnect = true };
        slave.Start();
        Assert.True(slave.IsStart);
        slave.Stop();
        Assert.False(slave.IsStart);
        slave.Start();
        Assert.True(slave.IsStart);
        slave.Stop();
    }

    [Fact]
    public async Task Start_InvalidSettings_DoesNotCrash()
    {
        using var slave = new ModbusRTUSlave { Port = "COM_NOT_EXIST", Baudrate = 0, AutoReconnect = false };
        slave.Start();
        await TestUtil.Until(() => !slave.IsStart);
        Assert.False(slave.IsOpen);
    }
}
