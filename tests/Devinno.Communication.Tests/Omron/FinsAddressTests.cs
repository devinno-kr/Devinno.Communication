using Devinno.Communication.Omron.Data;

namespace Devinno.Communication.Tests.Omron;

public class FinsAddressTests
{
    [Theory]
    [InlineData("D100", "D100", 0x82, 100, 0, false)]
    [InlineData("dm100", "D100", 0x82, 100, 0, false)]
    [InlineData("D0100", "D100", 0x82, 100, 0, false)]
    [InlineData("D32767", "D32767", 0x82, 32767, 0, false)]
    [InlineData("D100.15", "D100.15", 0x02, 100, 15, true)]
    [InlineData("CIO100", "CIO100", 0xB0, 100, 0, false)]
    [InlineData("100", "CIO100", 0xB0, 100, 0, false)]
    [InlineData("cio100.5", "CIO100.05", 0x30, 100, 5, true)]
    [InlineData("100.05", "CIO100.05", 0x30, 100, 5, true)]
    [InlineData("0.00", "CIO0.00", 0x30, 0, 0, true)]
    [InlineData("CIO6143.15", "CIO6143.15", 0x30, 6143, 15, true)]
    [InlineData("W10", "W10", 0xB1, 10, 0, false)]
    [InlineData("wr10.01", "W10.01", 0x31, 10, 1, true)]
    [InlineData("H20.15", "H20.15", 0x32, 20, 15, true)]
    [InlineData("HR511", "H511", 0xB2, 511, 0, false)]
    [InlineData("A959", "A959", 0xB3, 959, 0, false)]
    [InlineData("ar448.00", "A448.00", 0x33, 448, 0, true)]
    [InlineData("E0_100", "E0_100", 0xA0, 100, 0, false)]
    [InlineData("em3_10", "E3_10", 0xA3, 10, 0, false)]
    [InlineData("EC_0", "EC_0", 0xAC, 0, 0, false)]
    [InlineData("EF_1.02", "EF_1.02", 0x2F, 1, 2, true)]
    [InlineData("E10_5", "E10_5", 0x60, 5, 0, false)]
    [InlineData("E18_32767", "E18_32767", 0x68, 32767, 0, false)]
    [InlineData("e18_0.15", "E18_0.15", 0xE8, 0, 15, true)]
    [InlineData("e11_0.0", "E11_0.00", 0xE1, 0, 0, true)]
    public void Parse_NormalizesName_AndPicksWordOrBitAreaCode(string text, string name, int area, int word, int bit, bool isBit)
    {
        var a = FinsAddress.Parse(text, "p");
        Assert.Equal((name, (byte)area, word, bit, isBit), (a.Name, a.Area, a.Word, a.Bit, a.IsBit));
        Assert.Equal(isBit ? 1 : 2, a.Width);
    }

    /// W342 5-2-2 예 (공식): 영역 코드 + 워드(빅엔디언 2바이트) + 비트
    [Theory]
    [InlineData("CIO10", "B0000A00")]
    [InlineData("CIO10.13", "30000A0D")]
    [InlineData("W10", "B1000A00")]
    [InlineData("W10.13", "31000A0D")]
    [InlineData("H10", "B2000A00")]
    [InlineData("H10.13", "32000A0D")]
    [InlineData("D10", "82000A00")]
    [InlineData("E3_10", "A3000A00")]
    public void Encode_MatchesW342Examples(string text, string hex)
        => Assert.Equal(Convert.FromHexString(hex), FinsAddress.Parse(text, "p").Encode());

    [Theory]
    [InlineData("", "비어")]
    [InlineData("C100", "카운터")]
    [InlineData("T5", "타이머")]
    [InlineData("TIM5", "타이머")]
    [InlineData("cnt3", "카운터")]
    [InlineData("CIO6144", "0~6143")]
    [InlineData("W512", "0~511")]
    [InlineData("H512", "0~511")]
    [InlineData("A960", "0~959")]
    [InlineData("D32768", "0~32767")]
    [InlineData("E0_32768", "0~32767")]
    [InlineData("E19_0", "뱅크")]
    [InlineData("D100.16", "00~15")]
    [InlineData("D100.", "형식")]
    [InlineData("D100.123", "형식")]
    [InlineData("D 100", "형식")]
    [InlineData("X100", "형식")]
    [InlineData("E0100", "형식")]
    [InlineData("D-1", "형식")]
    [InlineData("CIO", "형식")]
    [InlineData("100.5.1", "형식")]
    [InlineData("D123456", "형식")]
    public void Parse_Invalid_Throws(string text, string message)
    {
        var ex = Assert.Throws<ArgumentException>(() => FinsAddress.Parse(text, "addr"));
        Assert.Contains(message, ex.Message);
        Assert.Equal("addr", ex.ParamName);
    }

    [Fact]
    public void Parse_Null_Throws() => Assert.Throws<ArgumentException>(() => FinsAddress.Parse(null!, "p"));

    [Theory]
    [InlineData("D100", 499, 599, 0)]
    [InlineData("CIO100.15", 1, 101, 0)]
    [InlineData("CIO100.05", 27, 102, 0)]
    [InlineData("W0.10", 16, 1, 10)]
    public void Offset_WordsAddWords_BitsCarryIntoNextWord(string text, int count, int word, int bit)
    {
        var a = FinsAddress.Parse(text, "p").Offset(count);
        Assert.Equal((word, bit), (a.Word, a.Bit));
        Assert.Equal(FinsAddress.Parse(text, "p").Area, a.Area);
    }

    [Theory]
    [InlineData("D32767", 1, true)]
    [InlineData("D32767", 2, false)]
    [InlineData("D0", 32768, true)]
    [InlineData("CIO6143.15", 1, true)]
    [InlineData("CIO6143.15", 2, false)]
    [InlineData("CIO6143.00", 16, true)]
    [InlineData("CIO6143.00", 17, false)]
    [InlineData("D0", 0, false)]
    [InlineData("CIO0.00", 65535, true)]
    [InlineData("CIO0.00", 65536, false)]
    public void ParseBlock_CountMustFitArea(string text, int count, bool ok)
    {
        if (ok) Assert.Equal(FinsAddress.Parse(text, "p"), FinsAddress.ParseBlock(text, count, "address", "count"));
        else Assert.Equal("count", Assert.Throws<ArgumentOutOfRangeException>(() => FinsAddress.ParseBlock(text, count, "address", "count")).ParamName);
    }

    [Fact]
    public void ParseItems_CountLimits()
    {
        Assert.Throws<ArgumentNullException>(() => FinsAddress.ParseItems(null!, 167, "addresses"));
        Assert.Contains("1~167", Assert.Throws<ArgumentException>(() => FinsAddress.ParseItems([], 167, "addresses")).Message);
        var many = Enumerable.Range(0, 168).Select(i => $"D{i}").ToArray();
        Assert.Contains("1~167", Assert.Throws<ArgumentException>(() => FinsAddress.ParseItems(many, 167, "addresses")).Message);
        Assert.Equal(167, FinsAddress.ParseItems(many[..167], 167, "addresses").Length);
        Assert.Throws<ArgumentException>(() => FinsAddress.ParseItems(["D0", "T1"], 167, "addresses"));
    }

    [Theory]
    [InlineData("D0", -32768, true)]
    [InlineData("D0", 65535, true)]
    [InlineData("D0", -32769, false)]
    [InlineData("D0", 65536, false)]
    [InlineData("D0.01", 0, true)]
    [InlineData("D0.01", 1, true)]
    [InlineData("D0.01", 2, false)]
    [InlineData("D0.01", -1, false)]
    public void CheckValue_WordRangeOrBit(string text, long value, bool ok)
    {
        var a = FinsAddress.Parse(text, "p");
        if (ok) a.CheckValue(value, "v");
        else Assert.Equal("v", Assert.Throws<ArgumentOutOfRangeException>(() => a.CheckValue(value, "v")).ParamName);
    }

    [Fact]
    public void SplitNames_TrimsSpaces() => Assert.Equal(new[] { "D100", "w10", "" }, FinsAddress.SplitNames(" D100, w10 ,"));
}
