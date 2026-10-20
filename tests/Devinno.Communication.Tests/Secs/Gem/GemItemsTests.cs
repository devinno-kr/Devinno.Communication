using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// ID 포맷 검사, 받을 때 관대하게 읽기, ID 목록 값, ack, 숫자 변환, XML 값 텍스트
public class GemItemsTests
{
    [Fact]
    public void IdFormats_OnlyIntegersAndAscii()
    {
        foreach (var f in Enum.GetValues<SecsFormat>())
        {
            bool ok = f is SecsFormat.U1 or SecsFormat.U2 or SecsFormat.U4 or SecsFormat.U8 or SecsFormat.I1 or SecsFormat.I2 or SecsFormat.I4 or SecsFormat.I8 or SecsFormat.ASCII;
            if (ok) Assert.Equal(f, GemItems.CheckIdFormat(f, "f"));
            else Assert.Equal("f", Assert.Throws<ArgumentException>(() => GemItems.CheckIdFormat(f, "f")).ParamName);
        }
    }

    [Fact]
    public void ReadId_NumericSetting_AcceptsAnyIntegerFormat_RejectsAscii()
    {
        Assert.Equal((GemId)1001, GemItems.ReadId(SecsItem.U2(1001), SecsFormat.U4));
        Assert.Equal((GemId)1001, GemItems.ReadId(SecsItem.I8(1001), SecsFormat.U1));   // 값으로 찾으므로 범위는 보지 않음
        Assert.Throws<FormatException>(() => GemItems.ReadId(SecsItem.A("1001"), SecsFormat.U4));
        Assert.Throws<FormatException>(() => GemItems.ReadId(null, SecsFormat.U4));
    }

    [Fact]
    public void ReadId_AsciiSetting_AcceptsOnlyAscii()
    {
        Assert.Equal((GemId)"1001", GemItems.ReadId(SecsItem.A("1001"), SecsFormat.ASCII));
        Assert.Throws<FormatException>(() => GemItems.ReadId(SecsItem.U4(1001), SecsFormat.ASCII));
    }

    [Fact]
    public void Key_AsciiSetting_NumberBecomesDecimalText()
    {
        Assert.Equal((GemId)"2001", GemItems.Key(2001, SecsFormat.ASCII));
        Assert.Equal((GemId)2001, GemItems.Key(2001, SecsFormat.U4));
        Assert.Equal((GemId)"X", GemItems.Key("X", SecsFormat.U4));
    }

    [Fact]
    public void ReadIdList_And_ReadIdVector()
    {
        Assert.Equal([(GemId)1, 2], GemItems.ReadIdList(SecsItem.ParseSml("<L <U1 1> <U4 2>>"), SecsFormat.U4));
        Assert.Throws<FormatException>(() => GemItems.ReadIdList(SecsItem.U4(1), SecsFormat.U4));
        Assert.Equal([(GemId)1, 2, 3], GemItems.ReadIdVector(SecsItem.U4(1, 2, 3), SecsFormat.U4));
        Assert.Equal([(GemId)1, 2], GemItems.ReadIdVector(SecsItem.ParseSml("<L <U1 1> <U1 2>>"), SecsFormat.U4));   // secsgem 방식
        Assert.Empty(GemItems.ReadIdVector(SecsItem.U4(), SecsFormat.U4));
        Assert.Empty(GemItems.ReadIdVector(SecsItem.A(""), SecsFormat.U4));
        Assert.Empty(GemItems.ReadIdVector(SecsItem.L(), SecsFormat.ASCII));
        Assert.Equal([(GemId)"AL1"], GemItems.ReadIdVector(SecsItem.A("AL1"), SecsFormat.ASCII));
        Assert.Throws<FormatException>(() => GemItems.ReadIdVector(SecsItem.F4(1), SecsFormat.U4));
    }

    [Fact]
    public void IdList_NumericIsOneArray_AsciiIsListOfA()
    {
        Assert.Equal("<U4 1 2>", GemItems.IdList([1, 2], SecsFormat.U4).ToSml());
        Assert.Equal("<I2>", GemItems.IdList([], SecsFormat.I2).ToSml());
        Assert.Equal("<L [2] <A \"1\"> <A \"X\">>", GemItems.IdList([1, "X"], SecsFormat.ASCII).ToSml());
    }

    [Theory]
    [InlineData(SecsFormat.List, "<L [0]>")]
    [InlineData(SecsFormat.ASCII, "<A \"\">")]
    [InlineData(SecsFormat.U4, "<U4>")]
    [InlineData(SecsFormat.Binary, "<B>")]
    [InlineData(SecsFormat.Boolean, "<BOOLEAN>")]
    [InlineData(SecsFormat.F8, "<F8>")]
    public void Empty_IsLengthZeroItem(SecsFormat f, string sml) => Assert.Equal(sml, GemItems.Empty(f).ToSml());

    [Fact]
    public void ReadAck_AnyNumericFormat_ZeroTo255()
    {
        Assert.Equal(0, GemItems.ReadAck(SecsItem.B(0)));
        Assert.Equal(4, GemItems.ReadAck(SecsItem.U1(4)));
        Assert.Equal(200, GemItems.ReadAck(SecsItem.I4(200)));
        Assert.Throws<FormatException>(() => GemItems.ReadAck(SecsItem.I4(256)));
        Assert.Throws<FormatException>(() => GemItems.ReadAck(SecsItem.I1(-1)));
        Assert.Throws<FormatException>(() => GemItems.ReadAck(SecsItem.B()));
        Assert.Throws<FormatException>(() => GemItems.ReadAck(SecsItem.B(0, 1)));
        Assert.Throws<FormatException>(() => GemItems.ReadAck(SecsItem.A("0")));
        Assert.Throws<FormatException>(() => GemItems.ReadAck(null));
    }

    [Fact]
    public void ConvertNumeric_Rules()
    {
        Assert.Equal(SecsItem.F4(80), GemItems.ConvertNumeric(SecsItem.U1(80), SecsFormat.F4));
        Assert.Equal(SecsItem.U2(5), GemItems.ConvertNumeric(SecsItem.F8(5.0), SecsFormat.U2));
        Assert.Null(GemItems.ConvertNumeric(SecsItem.F8(5.5), SecsFormat.U2));        // 소수
        Assert.Null(GemItems.ConvertNumeric(SecsItem.I4(-1), SecsFormat.U4));         // 범위
        Assert.Null(GemItems.ConvertNumeric(SecsItem.U4(256), SecsFormat.U1));
        Assert.Null(GemItems.ConvertNumeric(SecsItem.A("1"), SecsFormat.U1));         // 숫자 아님
        Assert.Equal(SecsItem.U8(ulong.MaxValue), GemItems.ConvertNumeric(SecsItem.U8(ulong.MaxValue), SecsFormat.U8));
        Assert.Equal(SecsItem.I8(1, 2), GemItems.ConvertNumeric(SecsItem.U1(1, 2), SecsFormat.I8));
        var same = SecsItem.A("x");
        Assert.Same(same, GemItems.ConvertNumeric(same, SecsFormat.ASCII));
    }

    [Fact]
    public void InRange_UsesFirstValueOfMinMax()
    {
        Assert.True(GemItems.InRange(SecsItem.F4(80), SecsItem.F4(0), SecsItem.F4(200)));
        Assert.False(GemItems.InRange(SecsItem.F4(201), SecsItem.F4(0), SecsItem.F4(200)));
        Assert.False(GemItems.InRange(SecsItem.U8(ulong.MaxValue), null, SecsItem.U8(ulong.MaxValue - 1)));   // 64비트도 정확히
        Assert.True(GemItems.InRange(SecsItem.I2(-5), null, null));
        Assert.False(GemItems.InRange(SecsItem.F4(float.NaN), null, SecsItem.F4(10)));       // Min이나 Max가 있으면 NaN은 범위 밖
        Assert.False(GemItems.InRange(SecsItem.F8(double.NaN), SecsItem.F8(0), null));
        Assert.False(GemItems.InRange(SecsItem.F4(1, float.NaN), SecsItem.F4(0), SecsItem.F4(10)));
        Assert.True(GemItems.InRange(SecsItem.F4(float.NaN), null, null));
    }

    [Theory]
    [InlineData(SecsFormat.F4, "80", "<F4 80>")]
    [InlineData(SecsFormat.U4, "4294967295", "<U4 4294967295>")]
    [InlineData(SecsFormat.I1, "-3", "<I1 -3>")]
    [InlineData(SecsFormat.Boolean, "true", "<BOOLEAN T>")]
    [InlineData(SecsFormat.Binary, "0x01 0xFF", "<B 0x01 0xFF>")]
    [InlineData(SecsFormat.ASCII, "a <b>", "<A \"a <b>\">")]
    [InlineData(SecsFormat.U2, "", "<U2>")]
    [InlineData(SecsFormat.List, "", "<L [0]>")]
    public void ParseValue_AndBack(SecsFormat f, string text, string sml)
    {
        var item = GemItems.ParseValue(text, f);
        Assert.Equal(sml, item.ToSml());
        Assert.Equal(text, GemItems.ValueText(item));
    }

    [Theory]
    [InlineData(SecsFormat.U4, "1 2")]
    [InlineData(SecsFormat.U1, "256")]
    [InlineData(SecsFormat.F4, "x")]
    [InlineData(SecsFormat.U4, "1> <U4 2")]
    [InlineData(SecsFormat.List, "1")]
    [InlineData(SecsFormat.Boolean, "yes")]
    public void ParseValue_Invalid_IsFormatException(SecsFormat f, string text)
        => Assert.Throws<FormatException>(() => GemItems.ParseValue(text, f));

    [Fact]
    public void ValueText_ArraysAndLists_AreNotWritable()
    {
        Assert.Null(GemItems.ValueText(SecsItem.U4(1, 2)));
        Assert.Null(GemItems.ValueText(SecsItem.L(SecsItem.U1(1))));
        Assert.Equal("false", GemItems.ValueText(SecsItem.Boolean(false)));
    }

    [Fact]
    public void FormatNames_RoundTrip()
    {
        foreach (var f in Enum.GetValues<SecsFormat>())
        {
            Assert.True(GemItems.TryParseFormatName(GemItems.FormatName(f), out var back));
            Assert.Equal(f, back);
        }
        Assert.Equal("A", GemItems.FormatName(SecsFormat.ASCII));
        Assert.False(GemItems.TryParseFormatName("ASCII", out _));
        Assert.False(GemItems.TryParseFormatName("u4", out _));
    }
}
