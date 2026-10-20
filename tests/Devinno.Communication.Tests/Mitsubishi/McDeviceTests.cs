using Devinno.Communication.Mitsubishi.Data;

namespace Devinno.Communication.Tests.Mitsubishi;

public class McDeviceTests
{
    static McTarget T(string t) => t switch { "1C" => McTarget.Serial1C, "3E" => McTarget.Ethernet3E, _ => McTarget.Ethernet1E };

    [Theory]
    [InlineData("D100", "1C", true, "D", 100, false, 10)]
    [InlineData("x17", "1C", true, "X", 15, true, 8)]      // FX: 8진수 17 = 15
    [InlineData("X17", "3E", false, "X", 0x17, true, 16)] // Q/L: 16진수
    [InlineData("X1F", "3E", false, "X", 0x1F, true, 16)]
    [InlineData("TN123", "1C", true, "TN", 123, false, 10)]
    [InlineData("SM400", "3E", false, "SM", 400, true, 10)]
    [InlineData("ZR100", "3E", false, "ZR", 0x100, false, 16)]
    [InlineData("W1F", "1C", true, "W", 0x1F, false, 16)]
    [InlineData(" m0 ", "1E", true, "M", 0, true, 10)]
    [InlineData("XA0", "3E", false, "X", 0xA0, true, 16)]  // 16진 번호는 A~F로 시작해도 됨
    [InlineData("BA", "1C", true, "B", 0xA, true, 16)]
    [InlineData("SB1F", "3E", false, "SB", 0x1F, true, 16)]
    public void Parse_Valid(string name, string target, bool octal, string code, int number, bool isBit, int radix)
    {
        var d = McDevice.Parse(name, T(target), octal, "x");
        Assert.Equal((name.Trim().ToUpperInvariant(), code, number, isBit, radix), (d.Name, d.Code, d.Number, d.IsBit, d.Radix));
    }

    [Fact]
    public void Parse_WireCodes_PerFrame()
    {
        Assert.Equal(0xA8, McDevice.Parse("D0", McTarget.Ethernet3E, false, "x").WireCode);
        Assert.Equal(0x90, McDevice.Parse("M0", McTarget.Ethernet3E, false, "x").WireCode);
        Assert.Equal(0x9C, McDevice.Parse("X0", McTarget.Ethernet3E, false, "x").WireCode);
        Assert.Equal(0xC2, McDevice.Parse("TN0", McTarget.Ethernet3E, false, "x").WireCode);
        Assert.Equal(0xB0, McDevice.Parse("ZR0", McTarget.Ethernet3E, false, "x").WireCode);
        Assert.Equal(0x4420, McDevice.Parse("D0", McTarget.Ethernet1E, true, "x").WireCode);
        Assert.Equal(0x4D20, McDevice.Parse("M0", McTarget.Ethernet1E, true, "x").WireCode);
        Assert.Equal(0x5920, McDevice.Parse("Y0", McTarget.Ethernet1E, true, "x").WireCode);
        Assert.Equal(0x434E, McDevice.Parse("CN0", McTarget.Ethernet1E, true, "x").WireCode);
    }

    [Theory]
    [InlineData("", "1C", true)]
    [InlineData("100", "1C", true)]          // 코드 없음
    [InlineData("D", "1C", true)]            // 번호 없음
    [InlineData("SM400", "1C", true)]        // 1C에 없는 디바이스
    [InlineData("SM400", "1E", true)]      // 1E에 없는 디바이스
    [InlineData("Q0", "3E", false)]        // 없는 디바이스
    [InlineData("X8", "1C", true)]           // 8진수에 8 없음
    [InlineData("D1A", "1C", true)]          // D는 10진수
    [InlineData("B1G", "1C", true)]          // 16진수에 G 없음
    [InlineData("MX0", "3E", false)]         // 없는 코드
    [InlineData("D10000", "1C", true)]       // 1C 4자리 초과
    [InlineData("TN1000", "1C", true)]       // 1C 2문자 코드는 3자리
    [InlineData("X10000", "1C", true)]       // 8진 4자리(7777) 초과
    [InlineData("D16777216", "3E", false)] // 3E 3바이트 초과
    [InlineData("D1-0", "3E", false)]
    public void Parse_Invalid_Throws(string name, string target, bool octal)
    {
        var ex = Assert.Throws<ArgumentException>(() => McDevice.Parse(name, T(target), octal, "device"));
        Assert.Equal("device", ex.ParamName);
    }

    [Fact]
    public void Parse_FieldLimits_Boundary()
    {
        Assert.Equal(9999, McDevice.Parse("D9999", McTarget.Serial1C, true, "x").Number);
        Assert.Equal(999, McDevice.Parse("TN999", McTarget.Serial1C, true, "x").Number);
        Assert.Equal(4095, McDevice.Parse("X7777", McTarget.Serial1C, true, "x").Number);
        Assert.Equal(0xFFFF, McDevice.Parse("XFFFF", McTarget.Serial1C, false, "x").Number);
        Assert.Equal(0xFFFFFF, McDevice.Parse("D16777215", McTarget.Ethernet3E, false, "x").Number);
    }

    [Theory]
    [InlineData("1C", McCommand.BR, true, 256)]
    [InlineData("1C", McCommand.WR, false, 64)]
    [InlineData("1C", McCommand.WR, true, 32)]
    [InlineData("1C", McCommand.BW, true, 160)]
    [InlineData("1C", McCommand.WW, false, 64)]
    [InlineData("1C", McCommand.WW, true, 10)]
    [InlineData("1E", McCommand.WR, true, 32)]
    [InlineData("1E", McCommand.WW, true, 10)]
    [InlineData("3E", McCommand.BR, true, 3584)]
    [InlineData("3E", McCommand.WW, false, 960)]
    public void MaxCount_Table(string target, McCommand command, bool bitDevice, int expected)
        => Assert.Equal(expected, McDevice.MaxCount(T(target), command, bitDevice));

    [Fact]
    public void ParseFor_ChecksBitCommandAndCount()
    {
        var ex = Assert.Throws<ArgumentException>(() => McDevice.ParseFor(McCommand.BR, "D0", 1, McTarget.Serial1C, true, "device", "count"));
        Assert.Equal("device", ex.ParamName);
        var ex2 = Assert.Throws<ArgumentOutOfRangeException>(() => McDevice.ParseFor(McCommand.WR, "M0", 33, McTarget.Serial1C, true, "device", "count"));
        Assert.Equal("count", ex2.ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => McDevice.ParseFor(McCommand.BR, "M0", 0, McTarget.Serial1C, true, "device", "count"));
        Assert.Equal(256, McDevice.MaxCount(McTarget.Serial1C, McCommand.BR, true));
        McDevice.ParseFor(McCommand.BR, "M0", 256, McTarget.Serial1C, true, "device", "count");
        McDevice.ParseFor(McCommand.WR, "M0", 32, McTarget.Serial1C, true, "device", "count");
    }

    [Fact]
    public void CheckValues_Ranges()
    {
        McDevice.CheckValues(McCommand.BW, [0, 1], "v");
        Assert.Throws<ArgumentOutOfRangeException>(() => McDevice.CheckValues(McCommand.BW, [2], "v"));
        McDevice.CheckValues(McCommand.WW, [-32768, 65535], "v");
        Assert.Throws<ArgumentOutOfRangeException>(() => McDevice.CheckValues(McCommand.WW, [65536], "v"));
        Assert.Throws<ArgumentOutOfRangeException>(() => McDevice.CheckValues(McCommand.WW, [-32769], "v"));
    }

    [Fact]
    public void IsOctal_AutoFollowsFrameDefault()
    {
        Assert.True(McDevice.IsOctal(McXYNotation.Auto, true));
        Assert.False(McDevice.IsOctal(McXYNotation.Auto, false));
        Assert.True(McDevice.IsOctal(McXYNotation.Octal, false));
        Assert.False(McDevice.IsOctal(McXYNotation.Hex, true));
    }

    [Fact]
    public void Binary_BitNibbles_UpperFirst_OddPadded()
    {
        // 공식 예(1E G6): M50~M61 = 0,1,1,1,0,1,0,0,0,0,0,1 → 01 11 01 00 00 01
        Assert.Equal(new byte[] { 0x01, 0x11, 0x01, 0x00, 0x00, 0x01 }, McDevice.EncodeBinary(McCommand.BW, [0, 1, 1, 1, 0, 1, 0, 0, 0, 0, 0, 1]));
        Assert.Equal(new byte[] { 0x11, 0x01, 0x10 }, McDevice.EncodeBinary(McCommand.BW, [1, 1, 0, 1, 1]));   // derived: 홀수 패딩
        Assert.Equal(new byte[] { 0x34, 0x12, 0xFF, 0xFF }, McDevice.EncodeBinary(McCommand.WW, [0x1234, -1]));
        Assert.Empty(McDevice.EncodeBinary(McCommand.WR, null));

        var r = McDevice.DecodeBinary(McCommand.BR, [0x10, 0x10, 0x01, 0x10], 8);   // 공식 예(1E G2)
        Assert.Equal(new long[] { 1, 0, 1, 0, 0, 1, 1, 0 }, r.Values);
        Assert.Equal(new long[] { 0x1234, 0x0002 }, McDevice.DecodeBinary(McCommand.WR, [0x34, 0x12, 0x02, 0x00], 2).Values);
        Assert.Equal(McStatus.Invalid, McDevice.DecodeBinary(McCommand.BR, [0x10], 3).Status);
        Assert.Equal(McStatus.Invalid, McDevice.DecodeBinary(McCommand.BR, [0x20], 1).Status);
        Assert.Equal(McStatus.Invalid, McDevice.DecodeBinary(McCommand.WR, [0x34], 1).Status);
        Assert.Equal(McStatus.Ack, McDevice.DecodeBinary(McCommand.WW, [], 3).Status);
    }
}
