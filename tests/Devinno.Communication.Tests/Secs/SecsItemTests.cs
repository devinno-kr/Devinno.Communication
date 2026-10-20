using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Tests.Secs;

public class SecsItemTests
{
    [Theory]
    [InlineData(SecsFormat.List, "0")]
    [InlineData(SecsFormat.Binary, "10")]
    [InlineData(SecsFormat.Boolean, "11")]
    [InlineData(SecsFormat.ASCII, "20")]
    [InlineData(SecsFormat.JIS8, "21")]
    [InlineData(SecsFormat.C2, "22")]
    [InlineData(SecsFormat.I8, "30")]
    [InlineData(SecsFormat.I1, "31")]
    [InlineData(SecsFormat.I2, "32")]
    [InlineData(SecsFormat.I4, "34")]
    [InlineData(SecsFormat.F8, "40")]
    [InlineData(SecsFormat.F4, "44")]
    [InlineData(SecsFormat.U8, "50")]
    [InlineData(SecsFormat.U1, "51")]
    [InlineData(SecsFormat.U2, "52")]
    [InlineData(SecsFormat.U4, "54")]
    public void FormatCodes_AreOctalE5Codes(SecsFormat format, string octal)
        => Assert.Equal(octal, Convert.ToString((int)format, 8));

    [Fact]
    public void List_CountAndItems()
    {
        var l = SecsItem.L(SecsItem.A("MDLN"), SecsItem.U4(1, 2));
        Assert.Equal(SecsFormat.List, l.Format);
        Assert.Equal(2, l.Count);
        Assert.Equal("MDLN", l.Items[0].GetString());
        Assert.Equal(new uint[] { 1, 2 }, l.Items[1].GetValues<uint>());
        Assert.Equal(0, SecsItem.L().Count);
        Assert.Throws<InvalidOperationException>(() => SecsItem.U1(1).Items);
    }

    [Fact]
    public void Factories_CopyInputArrays()
    {
        var src = new byte[] { 1, 2 };
        var b = SecsItem.B(src);
        src[0] = 9;
        Assert.Equal(new byte[] { 1, 2 }, b.GetBytes());
        var got = b.GetBytes();
        got[1] = 9;
        Assert.Equal(new byte[] { 1, 2 }, b.GetBytes());

        var children = new[] { SecsItem.U1(1) };
        var l = SecsItem.L(children);
        children[0] = SecsItem.U1(2);
        Assert.Equal(SecsItem.U1(1), l.Items[0]);
    }

    [Fact]
    public void Ascii_IsLatin1_KeepsEightBitBytes()
    {
        var a = SecsItem.A("Aéÿ");
        Assert.Equal(new byte[] { 0x41, 0xE9, 0xFF }, a.GetBytes());
        Assert.Equal("Aéÿ", a.GetString());
        Assert.Equal(3, a.Count);
        var ex = Assert.Throws<ArgumentException>(() => SecsItem.A("가"));
        Assert.Contains("0~255", ex.Message);
    }

    [Fact]
    public void JisAndC2_AreRawBytes()
    {
        Assert.Equal(new byte[] { 0xB1, 0xB2 }, SecsItem.J([0xB1, 0xB2]).GetBytes());
        Assert.Equal(new byte[] { 0x00, 0x0A, 0xB0, 0xA1 }, SecsItem.C2([0x00, 0x0A, 0xB0, 0xA1]).GetBytes());
        Assert.Throws<InvalidOperationException>(() => SecsItem.J([0x41]).GetString());
    }

    [Fact]
    public void GetValues_TypeMustMatchFormat()
    {
        Assert.Equal(new short[] { -1, 2 }, SecsItem.I2(-1, 2).GetValues<short>());
        Assert.Equal(new byte[] { 7 }, SecsItem.U1(7).GetValues<byte>());
        Assert.Equal(new byte[] { 7 }, SecsItem.B(7).GetValues<byte>());
        Assert.Equal(new[] { true, false }, SecsItem.Boolean(true, false).GetValues<bool>());
        Assert.Throws<InvalidOperationException>(() => SecsItem.I2(1).GetValues<int>());
        Assert.Throws<InvalidOperationException>(() => SecsItem.A("x").GetValues<byte>());
        Assert.Throws<InvalidOperationException>(() => SecsItem.L().GetValues<byte>());
    }

    [Fact]
    public void NumericGetters_ReadFirstValueOfAnyNumericFormat()
    {
        Assert.Equal(5L, SecsItem.U1(5).GetInt64());
        Assert.Equal(-5L, SecsItem.I1(-5).GetInt64());
        Assert.Equal(70000L, SecsItem.U4(70000, 1).GetInt64());
        Assert.Equal(3L, SecsItem.F8(3.9).GetInt64());
        Assert.Equal(1L, SecsItem.B(1).GetInt64());
        Assert.Equal(ulong.MaxValue, SecsItem.U8(ulong.MaxValue).GetUInt64());
        Assert.Equal(2.5, SecsItem.F4(2.5f).GetDouble());
        Assert.Equal(-7.0, SecsItem.I8(-7).GetDouble());
        Assert.Throws<OverflowException>(() => SecsItem.U8(ulong.MaxValue).GetInt64());
        Assert.Throws<OverflowException>(() => SecsItem.I4(-1).GetUInt64());
        Assert.Throws<InvalidOperationException>(() => SecsItem.U4().GetInt64());
        Assert.Throws<InvalidOperationException>(() => SecsItem.A("1").GetInt64());
        Assert.Throws<InvalidOperationException>(() => SecsItem.Boolean(true).GetInt64());
        Assert.Throws<InvalidOperationException>(() => SecsItem.L(SecsItem.U1(1)).GetDouble());
    }

    [Fact]
    public void Getters_WrongFormat_Throw()
    {
        Assert.Throws<InvalidOperationException>(() => SecsItem.U1(1).GetString());
        Assert.Throws<InvalidOperationException>(() => SecsItem.U1(1).GetBytes());
        Assert.Throws<InvalidOperationException>(() => SecsItem.U1(1).GetBooleans());
        Assert.Equal(new[] { true }, SecsItem.Boolean(true).GetBooleans());
    }

    [Fact]
    public void Equals_ComparesFormatAndValuesDeep()
    {
        var a = SecsItem.L(SecsItem.A("x"), SecsItem.L(SecsItem.U2(1, 2)));
        var b = SecsItem.L(SecsItem.A("x"), SecsItem.L(SecsItem.U2(1, 2)));
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, SecsItem.L(SecsItem.A("x"), SecsItem.L(SecsItem.U2(1, 3))));
        Assert.NotEqual(SecsItem.U1(1), SecsItem.B(1));            // 값이 같아도 포맷이 다름
        Assert.NotEqual(SecsItem.U4(1), SecsItem.U4(1, 0));
        Assert.Equal(SecsItem.F8(double.NaN), SecsItem.F8(double.NaN));
        Assert.False(SecsItem.U1(1).Equals(null));
    }

    [Fact]
    public void ZeroLengthItems_AreAllowed()
    {
        Assert.Equal(0, SecsItem.A("").Count);
        Assert.Equal(0, SecsItem.U4().Count);
        Assert.Equal(0, SecsItem.B().Count);
        Assert.Empty(SecsItem.F8().GetValues<double>());
    }

    [Fact]
    public void Length_OverMax_Throws()
    {
        Assert.Throws<ArgumentException>(() => SecsItem.B(new byte[SecsItem.MaxLength + 1]));
        Assert.Throws<ArgumentException>(() => SecsItem.U4(new uint[SecsItem.MaxLength / 4 + 1]));
        Assert.Equal(SecsItem.MaxLength / 4, SecsItem.U4(new uint[SecsItem.MaxLength / 4]).Count);
        Assert.Throws<ArgumentNullException>(() => SecsItem.L(SecsItem.U1(1), null!));
        Assert.Throws<ArgumentNullException>(() => SecsItem.A(null!));
    }
}
