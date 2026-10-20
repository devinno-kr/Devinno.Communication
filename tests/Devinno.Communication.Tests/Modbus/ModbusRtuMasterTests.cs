using Devinno.Communication.Modbus;
using Devinno.Communication.Modbus.RTU;

namespace Devinno.Communication.Tests.Modbus;

public class ModbusRtuMasterTests
{
    [Fact]
    public void TakeNextWork_ManualFirst_ThenAutoInOrder_ThenRefill()
    {
        var m = new ModbusRTUMaster();
        m.AutoWordRead_FC3(1, 1, 0, 1);
        m.AutoWordRead_FC3(2, 1, 0, 1);
        m.ManualWordWrite_FC6(10, 1, 0, 5);

        Assert.Equal(10, m.TakeNextWork()!.MessageID);
        Assert.Equal(1, m.TakeNextWork()!.MessageID);
        m.ManualWordWrite_FC6(11, 1, 0, 5);
        Assert.Equal(11, m.TakeNextWork()!.MessageID);
        Assert.Equal(2, m.TakeNextWork()!.MessageID);
        Assert.Equal(1, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void TakeNextWork_Empty_Null()
    {
        Assert.Null(new ModbusRTUMaster().TakeNextWork());
    }

    [Fact]
    public void RemoveAndContain()
    {
        var m = new ModbusRTUMaster();
        m.AutoBitRead_FC1(1, 1, 0, 8);
        m.ManualBitRead_FC2(2, 1, 0, 8);
        Assert.True(m.ContainAutoID(1));
        Assert.True(m.RemoveAuto(1));
        Assert.False(m.ContainAutoID(1));
        Assert.True(m.RemoveManual(2));
        Assert.False(m.RemoveManual(2));
        Assert.Null(m.TakeNextWork());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(248)]
    public void AutoRead_InvalidSlave_Throws(int slave)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModbusRTUMaster().AutoWordRead_FC3(1, slave, 0, 1));
    }

    [Fact]
    public void ManualWrite_BroadcastAllowed()
    {
        var m = new ModbusRTUMaster();
        m.ManualWordWrite_FC6(1, 0, 0, 1);
        Assert.Equal(0, m.TakeNextWork()!.Slave);
    }

    [Fact]
    public void HandleResponse_WordRead_RaisesEvent()
    {
        var m = new ModbusRTUMaster();
        ModbusRTUMaster.WordReadEventArgs? got = null;
        m.WordReadReceived += (s, e) => got = e;
        m.ManualWordRead_FC4(7, 3, 100, 2);
        var w = m.TakeNextWork()!;
        Assert.True(m.HandleResponse(w, new byte[] { 4, 4, 0, 1, 0, 2 }));
        Assert.Equal((7, 3, ModbusFunction.WORDREAD_F4, 100), (got!.MessageID, got.Slave, got.Function, got.StartAddress));
        Assert.Equal(new[] { 1, 2 }, got.ReceiveData);
    }

    [Fact]
    public void HandleResponse_MultiBitWrite_ReportsValues()
    {
        var m = new ModbusRTUMaster();
        ModbusRTUMaster.MultiBitWriteEventArgs? got = null;
        m.MultiBitWriteReceived += (s, e) => got = e;
        m.ManualMultiBitWrite_FC15(1, 1, 0, new[] { true, false, true });
        var w = m.TakeNextWork()!;
        Assert.True(m.HandleResponse(w, new byte[] { 15, 0, 0, 0, 3 }));
        Assert.Equal(new[] { true, false, true }, got!.WriteValues);
    }

    [Fact]
    public void HandleResponse_Exception_RaisesExceptionReceived()
    {
        var m = new ModbusRTUMaster();
        int code = 0;
        m.ExceptionReceived += (s, e) => code = e.ExceptionCode;
        m.ManualWordRead_FC3(1, 1, 0, 1);
        Assert.True(m.HandleResponse(m.TakeNextWork()!, new byte[] { 0x83, 2 }));
        Assert.Equal(2, code);
    }

    [Fact]
    public void HandleResponse_Malformed_ReturnsFalse_NoEvent()
    {
        var m = new ModbusRTUMaster();
        bool raised = false;
        m.WordReadReceived += (s, e) => raised = true;
        m.ManualWordRead_FC3(1, 1, 0, 2);
        Assert.False(m.HandleResponse(m.TakeNextWork()!, new byte[] { 3, 2, 0, 1 }));
        Assert.False(raised);
    }

    [Fact]
    public async Task Start_InvalidPort_IsStartBecomesFalse()
    {
        using var m = new ModbusRTUMaster { Port = "COM_NOT_EXIST" };
        m.Start();
        await TestUtil.Until(() => !m.IsStart);
    }

    [Fact]
    public void Stop_ClearsIsStartImmediately_AndStartRestarts()
    {
        using var m = new ModbusRTUMaster { Port = "COM_NOT_EXIST", AutoReconnect = true };
        m.Start();
        Assert.True(m.IsStart);
        m.Stop();
        Assert.False(m.IsStart);
        m.Start();
        Assert.True(m.IsStart);
        m.Stop();
    }

    [Fact]
    public async Task Start_InvalidSettings_DoesNotCrash()
    {
        using var m = new ModbusRTUMaster { Baudrate = 0, AutoReconnect = false };
        m.Start();
        await TestUtil.Until(() => !m.IsStart);
    }
}
