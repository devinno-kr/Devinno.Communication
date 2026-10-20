using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Tests.Secs;

public class SecsSmlTests
{
    [Fact]
    public void Message_Constructor_ChecksRanges()
    {
        var m = new SecsMessage(1, 13, true, SecsItem.L());
        Assert.Equal((1, 13, true, 0u, 0), (m.Stream, m.Function, m.ReplyExpected, m.SystemBytes, m.DeviceID));
        Assert.Null(new SecsMessage(1, 1, true).Item);
        Assert.Equal("stream", Assert.Throws<ArgumentOutOfRangeException>(() => new SecsMessage(128, 1, false)).ParamName);
        Assert.Equal("function", Assert.Throws<ArgumentOutOfRangeException>(() => new SecsMessage(1, 256, false)).ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SecsMessage(-1, 1, false));
    }

    [Fact]
    public void WithHeader_ReturnsCopy_OriginalUnchanged()
    {
        var m = new SecsMessage(6, 11, true, SecsItem.U4(1));
        var c = m.WithHeader(0x1E3A3253, 7);
        Assert.Equal((0x1E3A3253u, 7), (c.SystemBytes, c.DeviceID));
        Assert.Equal((0u, 0), (m.SystemBytes, m.DeviceID));
        Assert.Same(m.Item, c.Item);
        Assert.Equal((6, 11, true), (c.Stream, c.Function, c.ReplyExpected));
    }

    [Fact]
    public void ToSml_SpecExample()
    {
        var m = new SecsMessage(1, 13, true, SecsItem.L(SecsItem.A("MDLN"), SecsItem.U4(1, 2)));
        Assert.Equal("S1F13 W <L [2] <A \"MDLN\"> <U4 1 2>>", m.ToSml());
        Assert.Equal(m.ToSml(), m.ToString());
        Assert.Equal("S1F1 W", new SecsMessage(1, 1, true).ToSml());
        Assert.Equal("S1F0", new SecsMessage(1, 0, false).ToSml());
    }

    [Fact]
    public void ToSml_AllFormats()
    {
        var item = SecsItem.L(
            SecsItem.B(0x00, 0xAB), SecsItem.Boolean(true, false), SecsItem.A(""), SecsItem.J([0xB1]), SecsItem.C2([0x00, 0x0A]),
            SecsItem.I1(-128), SecsItem.I2(-1, 2), SecsItem.I4(int.MinValue), SecsItem.I8(long.MaxValue),
            SecsItem.U1(255), SecsItem.U2(65535), SecsItem.U4(uint.MaxValue), SecsItem.U8(ulong.MaxValue),
            SecsItem.F4(1.123f), SecsItem.F8(-0.1, double.NaN), SecsItem.U4(), SecsItem.L());
        Assert.Equal("<L [17] <B 0x00 0xAB> <BOOLEAN T F> <A \"\"> <J 0xB1> <C2 0x00 0x0A> <I1 -128> <I2 -1 2> <I4 -2147483648> "
            + "<I8 9223372036854775807> <U1 255> <U2 65535> <U4 4294967295> <U8 18446744073709551615> <F4 1.123> <F8 -0.1 NaN> <U4> <L [0]>>",
            item.ToSml());
        Assert.Equal(item, SecsItem.ParseSml(item.ToSml()));
    }

    [Fact]
    public void ToSml_AsciiNonPrintable_WrittenAsHexOutsideQuotes()
    {
        var a = SecsItem.A("ab\r\n\"q\"é");
        Assert.Equal("<A \"ab\" 0x0D 0x0A 0x22 \"q\" 0x22 0xE9>", a.ToSml());
        Assert.Equal(a, SecsItem.ParseSml(a.ToSml()));
        Assert.Equal("<A 0x01>", SecsItem.A("\u0001").ToSml());
    }

    [Fact]
    public void ParseSml_Lenient_CountOptional_QuotesBothStyles_CaseInsensitive()
    {
        var expected = new SecsMessage(1, 13, true, SecsItem.L(SecsItem.A("MDLN"), SecsItem.U4(1, 2)));
        foreach (var text in new[]
        {
            "S1F13 W <L [2] <A \"MDLN\"> <U4 1 2>>",
            "s1f13 w <l <a 'MDLN'> <u4 0x1 0x02>>.",
            "  S1F13W\r\n  <L[2]\n    <A [4] \"MD\" 'LN'>\n    <U4 [2] 1 2>\n  >\n.  ",
        })
        {
            var m = SecsMessage.ParseSml(text);
            Assert.Equal((expected.Stream, expected.Function, expected.ReplyExpected), (m.Stream, m.Function, m.ReplyExpected));
            Assert.Equal(expected.Item, m.Item);
        }
        var noW = SecsMessage.ParseSml("S6F12 <B 0>");
        Assert.False(noW.ReplyExpected);
        Assert.Equal(SecsItem.B(0), noW.Item);
        Assert.Null(SecsMessage.ParseSml("S1F1 W.").Item);
        Assert.Null(SecsMessage.ParseSml("S1F0").Item);
    }

    [Fact]
    public void ParseSml_ValuesForEachFormat()
    {
        Assert.Equal(SecsItem.Boolean(true, false, true, false), SecsItem.ParseSml("<BOOLEAN T f TRUE false>"));
        Assert.Equal(SecsItem.B(1, 255, 16), SecsItem.ParseSml("<B 1 0xFF 0x10>"));
        Assert.Equal(SecsItem.I1(-128, 127), SecsItem.ParseSml("<I1 -128 0x7F>"));
        Assert.Equal(SecsItem.F8(1e-3, double.PositiveInfinity), SecsItem.ParseSml("<F8 1E-3 Infinity>"));
        Assert.Equal(SecsItem.F4(-2.5f), SecsItem.ParseSml("<F4 -2.5>"));
        Assert.Equal(SecsItem.A("AB"), SecsItem.ParseSml("<A 65 0x42>"));
        Assert.Equal(SecsItem.A(""), SecsItem.ParseSml("<A>"));
        Assert.Equal(SecsItem.U8(ulong.MaxValue), SecsItem.ParseSml("<U8 0xFFFFFFFFFFFFFFFF>"));
    }

    [Theory]
    [InlineData("<U1 256>", 4, "범위")]
    [InlineData("<I1 0x80>", 4, "범위")]
    [InlineData("<U2 -1>", 4, "범위")]
    [InlineData("<U4 1.5>", 4, "정수가 아닙니다")]
    [InlineData("<X 1>", 1, "모르는 포맷")]
    [InlineData("<L [2] <U1 1>>", 3, "개수")]
    [InlineData("<A \"abc>", 3, "닫는 따옴표")]
    [InlineData("<BOOLEAN yes>", 9, "BOOLEAN")]
    [InlineData("<F4 x>", 4, "실수가 아닙니다")]
    [InlineData("<U1 1", 5, "'>'")]
    [InlineData("<U1 1> <U1 2>", 7, "뒤에")]
    [InlineData("U1 1", 0, "'<'")]
    public void ParseSmlItem_Errors_HavePosition(string text, int pos, string detail)
    {
        var ex = Assert.Throws<FormatException>(() => SecsItem.ParseSml(text));
        Assert.Contains($"위치 {pos}", ex.Message);
        Assert.Contains(detail, ex.Message);
    }

    [Theory]
    [InlineData("1F1", "S{Stream}")]
    [InlineData("S1 F1", "F{Function}")]
    [InlineData("S128F1", "0~127")]
    [InlineData("S1F256", "0~255")]
    [InlineData("S1F1 W <U1 1> extra", "뒤에")]
    [InlineData("S1F1 X", "뒤에")]
    public void ParseSmlMessage_Errors(string text, string detail)
        => Assert.Contains(detail, Assert.Throws<FormatException>(() => SecsMessage.ParseSml(text)).Message);

    [Fact]
    public void ParseSml_DeepNesting_IsFormatException()
    {
        var text = string.Concat(Enumerable.Repeat("<L ", 100)) + string.Concat(Enumerable.Repeat(">", 100));
        Assert.Contains("중첩", Assert.Throws<FormatException>(() => SecsItem.ParseSml(text)).Message);
    }
}
