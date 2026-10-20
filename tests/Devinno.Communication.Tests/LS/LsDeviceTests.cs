using Devinno.Communication.LS.Data;

namespace Devinno.Communication.Tests.LS;

public class LsDeviceTests
{
    [Theory]
    [InlineData("%MW100", 'M', 'W', "100", 2)]
    [InlineData("%mx0", 'M', 'X', "0", 1)]
    [InlineData("%DB5", 'D', 'B', "5", 1)]
    [InlineData("%PD10", 'P', 'D', "10", 4)]
    [InlineData("%ML3", 'M', 'L', "3", 8)]
    [InlineData("%IW0.0.0", 'I', 'W', "0.0.0", 2)]
    public void Parse_Valid(string name, char area, char size, string address, int width)
    {
        var d = LsDevice.Parse(name, "x");
        Assert.Equal((name.ToUpperInvariant(), area, size, address, width), (d.Name, d.Area, d.Size, d.Address, d.Width));
    }

    [Theory]
    [InlineData("")]
    [InlineData("MW100")]          // % 없음
    [InlineData("%MQ100")]         // 크기 문자 오류
    [InlineData("%MW")]            // 주소 없음
    [InlineData("%MWA")]           // 주소가 숫자로 시작 안 함
    [InlineData("%MW12345678901234")] // 17자
    [InlineData("%MW1-0")]         // 허용 안 되는 문자
    [InlineData("%1W100")]         // 영역 문자 오류
    public void Parse_Invalid_Throws(string name)
    {
        var ex = Assert.Throws<ArgumentException>(() => LsDevice.Parse(name, "devices"));
        Assert.Equal("devices", ex.ParamName);
    }

    [Fact]
    public void CheckValue_Ranges()
    {
        LsDevice.Parse("%MX0", "x").CheckValue(1, "v");
        Assert.Throws<ArgumentOutOfRangeException>(() => LsDevice.Parse("%MX0", "x").CheckValue(2, "v"));
        LsDevice.Parse("%MW0", "x").CheckValue(-32768, "v");
        LsDevice.Parse("%MW0", "x").CheckValue(65535, "v");
        Assert.Throws<ArgumentOutOfRangeException>(() => LsDevice.Parse("%MW0", "x").CheckValue(65536, "v"));
        Assert.Throws<ArgumentOutOfRangeException>(() => LsDevice.Parse("%MB0", "x").CheckValue(-129, "v"));
        LsDevice.Parse("%MD0", "x").CheckValue(0xFFFFFFFF, "v");
        LsDevice.Parse("%ML0", "x").CheckValue(long.MinValue, "v");
    }

    [Fact]
    public void ToByteDevice_ConvertsByWidth()
    {
        Assert.Equal("%MB200", LsDevice.Parse("%MW100", "x").ToByteDevice("x").Name);
        Assert.Equal("%DB40", LsDevice.Parse("%DD10", "x").ToByteDevice("x").Name);
        Assert.Equal("%MB7", LsDevice.Parse("%MB7", "x").ToByteDevice("x").Name);
        Assert.Throws<ArgumentException>(() => LsDevice.Parse("%MX0", "x").ToByteDevice("x"));
        Assert.Throws<ArgumentException>(() => LsDevice.Parse("%IW0.0.0", "x").ToByteDevice("x"));
        Assert.Throws<ArgumentException>(() => LsDevice.Parse("%IB0.0.0", "x").ToByteDevice("x"));
    }
}
