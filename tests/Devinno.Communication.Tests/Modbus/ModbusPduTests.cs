using Devinno.Communication.Internal;
using Devinno.Communication.Modbus;

namespace Devinno.Communication.Tests.Modbus;

public class ModbusPduTests
{
    [Fact]
    public void PackUnpack_RoundTrip_LsbFirst()
    {
        var bits = new[] { true, false, true, true, false, false, false, false, true };
        var packed = BitUtil.PackBits(bits);
        Assert.Equal(new byte[] { 0x0D, 0x01 }, packed);
        Assert.Equal(bits, BitUtil.UnpackBits(packed, 9));
    }

    [Fact]
    public void ReadRequest_FC3()
    {
        Assert.Equal(new byte[] { 3, 0x00, 0x6B, 0x00, 0x03 }, ModbusPdu.ReadRequest(ModbusFunction.WORDREAD_F3, 0x6B, 3));
    }

    [Theory]
    [InlineData(ModbusFunction.WORDREAD_F3, 0, 0)]
    [InlineData(ModbusFunction.WORDREAD_F3, 0, 126)]
    [InlineData(ModbusFunction.BITREAD_F1, 0, 2001)]
    [InlineData(ModbusFunction.WORDREAD_F3, 65535, 2)]
    [InlineData(ModbusFunction.WORDREAD_F3, -1, 1)]
    public void ReadRequest_OutOfRange_Throws(ModbusFunction fn, int start, int length)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusPdu.ReadRequest(fn, start, length));
    }

    [Fact]
    public void ReadRequest_WriteFunction_Throws()
    {
        Assert.Throws<ArgumentException>(() => ModbusPdu.ReadRequest(ModbusFunction.WORDWRITE_F6, 0, 1));
    }

    [Fact]
    public void WriteRequests_Encoding()
    {
        Assert.Equal(new byte[] { 5, 0x00, 0xAC, 0xFF, 0x00 }, ModbusPdu.WriteSingleCoil(0xAC, true));
        Assert.Equal(new byte[] { 6, 0x00, 0x01, 0xFF, 0xFF }, ModbusPdu.WriteSingleRegister(1, -1));
        Assert.Equal(new byte[] { 15, 0x00, 0x13, 0x00, 0x0A, 0x02, 0xCD, 0x01 },
            ModbusPdu.WriteMultipleCoils(0x13, new[] { true, false, true, true, false, false, true, true, true, false }));
        Assert.Equal(new byte[] { 16, 0x00, 0x01, 0x00, 0x02, 0x04, 0x00, 0x0A, 0x01, 0x02 },
            ModbusPdu.WriteMultipleRegisters(1, new[] { 0x000A, 0x0102 }));
        Assert.Equal(new byte[] { 26, 0x00, 0x05, 3, 0xFF, 0x00 }, ModbusPdu.WordBitSet(5, 3, true));
    }

    [Fact]
    public void WordBitSet_BitIndexOver15_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusPdu.WordBitSet(0, 16, true));
    }

    [Fact]
    public void ExpectedResponseLength_ByFunction()
    {
        Assert.Equal(2 + 2, ModbusPdu.ExpectedResponseLength(ModbusPdu.ReadRequest(ModbusFunction.BITREAD_F1, 0, 9)));
        Assert.Equal(2 + 6, ModbusPdu.ExpectedResponseLength(ModbusPdu.ReadRequest(ModbusFunction.WORDREAD_F4, 0, 3)));
        Assert.Equal(5, ModbusPdu.ExpectedResponseLength(ModbusPdu.WriteMultipleRegisters(0, new[] { 1, 2 })));
    }

    [Fact]
    public void ParseWords_AndValidation()
    {
        var req = ModbusPdu.ReadRequest(ModbusFunction.WORDREAD_F3, 0, 2);
        var res = new byte[] { 3, 4, 0x00, 0x0A, 0xFF, 0xFF };
        Assert.True(ModbusPdu.IsValidResponse(req, res));
        Assert.Equal(new[] { 10, 0xFFFF }, ModbusPdu.ParseWords(res));
    }

    [Fact]
    public void ParseBits_TrimsToRequestedLength()
    {
        var req = ModbusPdu.ReadRequest(ModbusFunction.BITREAD_F1, 0, 3);
        var res = new byte[] { 1, 1, 0xFF };
        Assert.Equal(new[] { true, true, true }, ModbusPdu.ParseBits(req, res));
    }

    [Fact]
    public void IsValidResponse_WrongLengthOrEcho_False()
    {
        var req = ModbusPdu.ReadRequest(ModbusFunction.WORDREAD_F3, 0, 2);
        Assert.False(ModbusPdu.IsValidResponse(req, new byte[] { 3, 2, 0x00, 0x0A }));
        var wreq = ModbusPdu.WriteSingleRegister(5, 1);
        Assert.False(ModbusPdu.IsValidResponse(wreq, new byte[] { 6, 0x00, 0x06, 0x00, 0x01 }));
        Assert.True(ModbusPdu.IsValidResponse(wreq, new byte[] { 6, 0x00, 0x05, 0x00, 0x01 }));
    }

    [Fact]
    public void IsException_DetectsHighBit()
    {
        var req = ModbusPdu.ReadRequest(ModbusFunction.WORDREAD_F3, 0, 1);
        Assert.True(ModbusPdu.IsException(req, new byte[] { 0x83, 0x02 }));
        Assert.False(ModbusPdu.IsException(req, new byte[] { 0x84, 0x02 }));
    }
}
