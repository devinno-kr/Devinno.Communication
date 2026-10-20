using Devinno.Communication.Siemens;

namespace Devinno.Communication.Tests.Siemens;

public class S7AddressTests
{
    [Theory]
    [InlineData("DB1.DBX0.1", 0x84, 'X', 1, 0, 1)]
    [InlineData("db10.dbb5", 0x84, 'B', 10, 5, 0)]
    [InlineData("DB65535.DBW65535", 0x84, 'W', 65535, 65535, 0)]
    [InlineData("DB2.DBD4", 0x84, 'D', 2, 4, 0)]
    [InlineData("M10.7", 0x83, 'X', 0, 10, 7)]
    [InlineData("MB3", 0x83, 'B', 0, 3, 0)]
    [InlineData("mw12", 0x83, 'W', 0, 12, 0)]
    [InlineData("MD0", 0x83, 'D', 0, 0, 0)]
    [InlineData("I0.0", 0x81, 'X', 0, 0, 0)]
    [InlineData("E1.2", 0x81, 'X', 0, 1, 2)]
    [InlineData("IB2", 0x81, 'B', 0, 2, 0)]
    [InlineData("EW4", 0x81, 'W', 0, 4, 0)]
    [InlineData("ID8", 0x81, 'D', 0, 8, 0)]
    [InlineData("Q0.5", 0x82, 'X', 0, 0, 5)]
    [InlineData("A1.0", 0x82, 'X', 0, 1, 0)]
    [InlineData("QB1", 0x82, 'B', 0, 1, 0)]
    [InlineData("AW2", 0x82, 'W', 0, 2, 0)]
    [InlineData("AD4", 0x82, 'D', 0, 4, 0)]
    public void Parse_Valid(string text, int area, char size, int db, int b, int bit)
    {
        var a = S7Address.Parse(text, "address");
        Assert.Equal((text.ToUpperInvariant(), (byte)area, size, db, b, bit), (a.Name, a.Area, a.Size, a.Db, a.Byte, a.Bit));
    }

    [Theory]
    [InlineData("", "비어")]
    [InlineData("DB0.DBW0", "DB 번호")]
    [InlineData("DB65536.DBW0", "DB 번호")]
    [InlineData("MW65536", "바이트 주소")]
    [InlineData("M0.8", "비트 번호")]
    [InlineData("M10", "비트 번호가 필요")]
    [InlineData("DB1.DBX0", "비트 번호가 필요")]
    [InlineData("MW10.1", "붙일 수 없")]
    [InlineData("DB1.DBW0.0", "붙일 수 없")]
    [InlineData("T5", "타이머")]
    [InlineData("C5", "카운터")]
    [InlineData("Z5", "카운터")]
    [InlineData("PIW256", "형식")]
    [InlineData("MX0.0", "형식")]
    [InlineData(" MW0", "형식")]
    [InlineData("DB1.DW0", "형식")]
    [InlineData("MW-1", "형식")]
    [InlineData("MW١", "형식")]
    [InlineData("MW10\n", "형식")]
    [InlineData("M0.0\n", "형식")]
    public void Parse_Invalid_Throws(string text, string mustContain)
    {
        var ex = Assert.Throws<ArgumentException>(() => S7Address.Parse(text, "address"));
        Assert.Equal("address", ex.ParamName);
        Assert.Contains(mustContain, ex.Message);
    }

    [Fact]
    public void Parse_Null_Throws()
        => Assert.Throws<ArgumentException>(() => S7Address.Parse(null!, "address"));

    [Theory]
    [InlineData("DB1.DBB0")]
    [InlineData("MB0")]
    [InlineData("eb1")]
    [InlineData("AB2")]
    public void BlockStart_ByteAddress_Ok(string text) => Assert.Equal('B', S7Address.ParseBlockStart(text, "address").Size);

    [Theory]
    [InlineData("DB1.DBW0")]
    [InlineData("MD0")]
    [InlineData("M0.0")]
    public void BlockStart_NotByte_Throws(string text)
    {
        var ex = Assert.Throws<ArgumentException>(() => S7Address.ParseBlockStart(text, "address"));
        Assert.Contains("바이트 주소", ex.Message);
    }

    // 실장비 캡처(WinCC ↔ S7-300)의 항목 주소: DB1.DBX2.0 → 00 00 10, DB1.DBD8 → 00 00 40, M1.0 → 00 00 08, M1.1 → 00 00 09
    // gmiru 예: DBX40.3 → 0x000143. 바이트 65535는 0x07FFF8 (S7.Net은 0x08FFF8로 잘못 만듦)
    [Theory]
    [InlineData("DB1.DBX2.0", 0x10, 0x01, 1)]
    [InlineData("DB1.DBD8", 0x40, 0x02, 4)]
    [InlineData("M1.0", 0x08, 0x01, 1)]
    [InlineData("M1.1", 0x09, 0x01, 1)]
    [InlineData("DB1.DBX40.3", 0x143, 0x01, 1)]
    [InlineData("DB1.DBB65535", 0x07FFF8, 0x02, 1)]
    [InlineData("MW10", 0x50, 0x02, 2)]
    public void ToItem_BitAddressAndTransportSize(string text, int bitAddress, int transportSize, int count)
    {
        var a = S7Address.Parse(text, "address");
        var it = a.ToItem();
        Assert.Equal(((byte)transportSize, count, a.Db, a.Area, bitAddress), (it.TransportSize, it.Count, it.Db, it.Area, it.BitAddress));
    }

    [Fact]
    public void CheckValue_Ranges()
    {
        var bit = S7Address.Parse("M0.0", "a");
        bit.CheckValue(0, "v"); bit.CheckValue(1, "v");
        Assert.Throws<ArgumentOutOfRangeException>(() => bit.CheckValue(2, "v"));
        var w = S7Address.Parse("MW0", "a");
        w.CheckValue(-32768, "v"); w.CheckValue(65535, "v");
        Assert.Throws<ArgumentOutOfRangeException>(() => w.CheckValue(65536, "v"));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.CheckValue(-32769, "v"));
        var d = S7Address.Parse("MD0", "a");
        d.CheckValue(int.MinValue, "v"); d.CheckValue(uint.MaxValue, "v");
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => d.CheckValue(1L << 32, "v"));
        Assert.Equal("v", ex.ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => S7Address.Parse("MB0", "a").CheckValue(256, "v"));
    }

    [Fact]
    public void EncodeDecode_BigEndian_Truncated()
    {
        Assert.Equal(new byte[] { 0x12, 0x34 }, S7Address.Parse("MW0", "a").Encode(0x1234));
        Assert.Equal(new byte[] { 0xFF, 0xFF }, S7Address.Parse("MW0", "a").Encode(-1));
        Assert.Equal(new byte[] { 0x3F, 0xC0, 0x00, 0x00 }, S7Address.Parse("DB1.DBD4", "a").Encode(BitConverter.SingleToInt32Bits(1.5f)));
        Assert.Equal(new byte[] { 0x01 }, S7Address.Parse("Q0.0", "a").Encode(1));
        Assert.Equal(0xFEADBEEFL, S7Address.Parse("MD0", "a").Decode([0xFE, 0xAD, 0xBE, 0xEF]));
        Assert.Equal(0xBABEL, S7Address.Parse("MW0", "a").Decode([0xBA, 0xBE]));
        Assert.Equal(1L, S7Address.Parse("M0.0", "a").Decode([0x01]));
        Assert.Equal(0xA9L, S7Address.Parse("MB0", "a").Decode([0xA9]));
    }

    [Fact]
    public void SplitNames_TrimsSpaces() => Assert.Equal(new[] { "DB1.DBW0", "m0.0" }, S7Address.SplitNames(" DB1.DBW0 , m0.0"));

    [Fact]
    public void PublicTypes()
    {
        Assert.Equal((1, 2, 3), ((int)S7ConnectionType.PG, (int)S7ConnectionType.OP, (int)S7ConnectionType.Basic));
        Assert.Equal(new[] { "Read", "ReadBlock", "Write", "WriteBlock" }, Enum.GetNames<S7Command>());
        var v = new S7Value("MW0", -1);
        Assert.Equal(("MW0", -1L), (v.Address, v.Value));
    }
}
