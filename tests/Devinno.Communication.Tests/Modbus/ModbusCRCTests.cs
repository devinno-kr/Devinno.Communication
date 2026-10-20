using Devinno.Communication.Modbus;

namespace Devinno.Communication.Tests.Modbus;

public class ModbusCRCTests
{
    [Fact]
    public void Write_KnownVector_ReadHolding()
    {
        // Modbus 규격 예제: 01 03 00 00 00 0A → CRC C5 CD
        var frame = new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A, 0, 0 };
        ModbusCRC.Write(frame);
        Assert.Equal(0xC5, frame[6]);
        Assert.Equal(0xCD, frame[7]);
    }

    [Fact]
    public void Check_ValidFrame_True()
    {
        Assert.True(ModbusCRC.Check(new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A, 0xC5, 0xCD }));
    }

    [Fact]
    public void Check_CorruptedFrame_False()
    {
        Assert.False(ModbusCRC.Check(new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0B, 0xC5, 0xCD }));
    }

    [Fact]
    public void Check_TooShort_False()
    {
        Assert.False(ModbusCRC.Check(new byte[] { 0x01, 0x03 }));
    }
}
