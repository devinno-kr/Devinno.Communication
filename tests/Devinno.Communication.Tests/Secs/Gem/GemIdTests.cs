using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

public class GemIdTests
{
    [Fact]
    public void ImplicitConversions_AndProperties()
    {
        GemId a = 5, b = 5L, c = "LOT";
        Assert.False(a.IsText);
        Assert.Equal(5, a.Number);
        Assert.Null(a.Text);
        Assert.True(c.IsText);
        Assert.Equal("LOT", c.Text);
        Assert.Equal("5", a.ToString());
        Assert.Equal("LOT", c.ToString());
        Assert.Equal(a, b);
        Assert.Throws<InvalidOperationException>(() => c.Number);
        Assert.Throws<ArgumentException>(() => new GemId(""));
        Assert.Throws<ArgumentNullException>(() => new GemId(null!));
    }

    [Fact]
    public void Equality_NumbersByValue_TextCaseSensitive_NumberNeverEqualsText()
    {
        Assert.True((GemId)1001 == new GemId(1001L));
        Assert.True((GemId)"abc" == "abc");
        Assert.True((GemId)"abc" != "ABC");
        Assert.True((GemId)5 != "5");
        Assert.Equal(new GemId("x").GetHashCode(), new GemId("x").GetHashCode());
        var set = new HashSet<GemId> { 1, 1L, "1", "A", "a" };
        Assert.Equal(4, set.Count);
    }

    [Theory]
    [InlineData(SecsFormat.U1, 255, "<U1 255>")]
    [InlineData(SecsFormat.U2, 65535, "<U2 65535>")]
    [InlineData(SecsFormat.U4, 4294967295, "<U4 4294967295>")]
    [InlineData(SecsFormat.U8, 9223372036854775807, "<U8 9223372036854775807>")]
    [InlineData(SecsFormat.I1, -128, "<I1 -128>")]
    [InlineData(SecsFormat.I2, -32768, "<I2 -32768>")]
    [InlineData(SecsFormat.I4, -2147483648, "<I4 -2147483648>")]
    [InlineData(SecsFormat.I8, -9223372036854775808, "<I8 -9223372036854775808>")]
    [InlineData(SecsFormat.ASCII, 1001, "<A \"1001\">")]
    public void ToItem_AllNineIdFormats(SecsFormat format, long value, string sml)
        => Assert.Equal(sml, new GemId(value).ToItem(format).ToSml());

    [Theory]
    [InlineData(SecsFormat.U1, 256)]
    [InlineData(SecsFormat.U1, -1)]
    [InlineData(SecsFormat.U2, 65536)]
    [InlineData(SecsFormat.U4, 4294967296)]
    [InlineData(SecsFormat.U8, -1)]
    [InlineData(SecsFormat.I1, 128)]
    [InlineData(SecsFormat.I2, -32769)]
    [InlineData(SecsFormat.I4, 2147483648)]
    public void ToItem_OutOfRange_IsArgumentOutOfRange(SecsFormat format, long value)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new GemId(value).ToItem(format));

    [Fact]
    public void ToItem_TextIdWithNumericFormat_OrNonIdFormat_IsArgumentException()
    {
        Assert.Equal("<A \"LOT\">", new GemId("LOT").ToItem(SecsFormat.ASCII).ToSml());
        Assert.Throws<ArgumentException>(() => new GemId("LOT").ToItem(SecsFormat.U4));
        Assert.Throws<ArgumentException>(() => new GemId(1).ToItem(SecsFormat.F4));
        Assert.Throws<ArgumentException>(() => new GemId(1).ToItem(SecsFormat.Binary));
    }

    [Theory]
    [InlineData("<U1 7>")]
    [InlineData("<U2 7>")]
    [InlineData("<U4 7>")]
    [InlineData("<U8 7>")]
    [InlineData("<I1 7>")]
    [InlineData("<I2 7>")]
    [InlineData("<I4 7>")]
    [InlineData("<I8 7>")]
    public void FromItem_AnyIntegerFormat_SameId(string sml) => Assert.Equal((GemId)7, GemId.FromItem(SecsItem.ParseSml(sml)));

    [Theory]
    [InlineData("<B 1>")]
    [InlineData("<BOOLEAN T>")]
    [InlineData("<F4 1>")]
    [InlineData("<F8 1>")]
    [InlineData("<L>")]
    [InlineData("<U4>")]
    [InlineData("<U4 1 2>")]
    [InlineData("<A \"\">")]
    [InlineData("<U8 9223372036854775808>")]
    [InlineData("<J 0x41>")]
    public void FromItem_NotAnId_IsFormatException(string sml) => Assert.Throws<FormatException>(() => GemId.FromItem(SecsItem.ParseSml(sml)));

    [Fact]
    public void FromItem_Ascii_IsTextId() => Assert.Equal((GemId)"5001", GemId.FromItem(SecsItem.A("5001")));
}
