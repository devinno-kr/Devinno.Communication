using Devinno.Communication.Modbus;

namespace Devinno.Communication.Tests.Modbus;

public class ModbusExecuteTests
{
    readonly bool[] coils = new bool[16];
    readonly bool[] contacts = new bool[16];
    readonly int[] holding = new int[16];
    readonly int[] input = new int[16];

    byte[] Exec(byte[] req, out ModbusWrite? w) => ModbusPdu.Execute(req, coils, contacts, holding, input, out w);

    [Fact]
    public void FC3_ReadsLow16Bits()
    {
        holding[2] = 0x1234; holding[3] = 0x7FFFF;
        var res = Exec(ModbusPdu.ReadRequest(ModbusFunction.WORDREAD_F3, 2, 2), out var w);
        Assert.Equal(new byte[] { 3, 4, 0x12, 0x34, 0xFF, 0xFF }, res);
        Assert.Null(w);
    }

    [Fact]
    public void FC1_And_FC2_UseSeparateAreas()
    {
        coils[0] = true; contacts[1] = true;
        Assert.Equal(new byte[] { 1, 1, 0x01 }, Exec(ModbusPdu.ReadRequest(ModbusFunction.BITREAD_F1, 0, 2), out _));
        Assert.Equal(new byte[] { 2, 1, 0x02 }, Exec(ModbusPdu.ReadRequest(ModbusFunction.BITREAD_F2, 0, 2), out _));
    }

    [Fact]
    public void FC4_ReadsInputRegisters()
    {
        input[0] = 7;
        Assert.Equal(new byte[] { 4, 2, 0x00, 0x07 }, Exec(ModbusPdu.ReadRequest(ModbusFunction.WORDREAD_F4, 0, 1), out _));
    }

    [Fact]
    public void FC5_WritesCoil_EchoesRequest()
    {
        var req = ModbusPdu.WriteSingleCoil(3, true);
        Assert.Equal(req, Exec(req, out var w));
        Assert.True(coils[3]);
        Assert.Equal(new ModbusWrite(ModbusFunction.BITWRITE_F5, 3, 1, true), w);
    }

    [Fact]
    public void FC5_InvalidValue_Exception03()
    {
        Assert.Equal(new byte[] { 0x85, 3 }, Exec(new byte[] { 5, 0, 0, 0x12, 0x34 }, out var w));
        Assert.Null(w);
    }

    [Fact]
    public void FC6_StoresUnsigned16()
    {
        Exec(ModbusPdu.WriteSingleRegister(1, -1), out var w);
        Assert.Equal(0xFFFF, holding[1]);
        Assert.Equal(new ModbusWrite(ModbusFunction.WORDWRITE_F6, 1, 1, false), w);
    }

    [Fact]
    public void FC15_WritesCoils_ResponseHasCount()
    {
        var res = Exec(ModbusPdu.WriteMultipleCoils(4, new[] { true, false, true }), out var w);
        Assert.Equal(new byte[] { 15, 0, 4, 0, 3 }, res);
        Assert.Equal(new[] { true, false, true }, coils[4..7]);
        Assert.Equal(new ModbusWrite(ModbusFunction.MULTIBITWRITE_F15, 4, 3, true), w);
    }

    [Fact]
    public void FC16_WritesRegisters()
    {
        var res = Exec(ModbusPdu.WriteMultipleRegisters(10, new[] { 1, 2, 3 }), out var w);
        Assert.Equal(new byte[] { 16, 0, 10, 0, 3 }, res);
        Assert.Equal(new[] { 1, 2, 3 }, holding[10..13]);
        Assert.Equal(new ModbusWrite(ModbusFunction.MULTIWORDWRITE_F16, 10, 3, false), w);
    }

    [Fact]
    public void FC16_ByteCountMismatch_Exception03()
    {
        Assert.Equal(new byte[] { 0x90, 3 }, Exec(new byte[] { 16, 0, 0, 0, 2, 3, 0, 1, 0 }, out _));
    }

    [Fact]
    public void FC26_SetsAndClearsBit()
    {
        holding[0] = 0x0001;
        var res = Exec(ModbusPdu.WordBitSet(0, 4, true), out var w);
        Assert.Equal(new byte[] { 26, 0, 0, 0xFF, 0x00 }, res);
        Assert.Equal(0x0011, holding[0]);
        Exec(ModbusPdu.WordBitSet(0, 0, false), out _);
        Assert.Equal(0x0010, holding[0]);
        Assert.Equal(new ModbusWrite(ModbusFunction.WORDBITSET_F26, 0, 1, false), w);
    }

    [Fact]
    public void OutOfRange_Exception02()
    {
        Assert.Equal(new byte[] { 0x83, 2 }, Exec(ModbusPdu.ReadRequest(ModbusFunction.WORDREAD_F3, 15, 2), out _));
        Assert.Equal(new byte[] { 0x86, 2 }, Exec(ModbusPdu.WriteSingleRegister(16, 1), out var w));
        Assert.Null(w);
    }

    [Fact]
    public void Execute_NullArea_ReturnsException02()
    {
        var res = ModbusPdu.Execute(ModbusPdu.ReadRequest(ModbusFunction.WORDREAD_F3, 0, 1), coils, contacts, null, input, out _);
        Assert.Equal(new byte[] { 0x83, 2 }, res);
    }

    [Fact]
    public void UnknownFunction_Exception01()
    {
        Assert.Equal(new byte[] { 0x87, 1 }, Exec(new byte[] { 7 }, out _));
    }

    [Fact]
    public void TruncatedRequest_Exception03()
    {
        Assert.Equal(new byte[] { 0x83, 3 }, Exec(new byte[] { 3, 0, 0 }, out _));
    }
}
