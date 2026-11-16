using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// GemMessages 트레이스(S2F23/24·S6F1/2)와 DSPER 변환: 골든 SML, 왕복, 받을 때 관대, 오류 규칙
public class GemMessagesTraceTests
{
    static SecsMessage M(string sml) => SecsMessage.ParseSml(sml);

    [Fact]
    public void S2F23_Golden()
    {
        var m = GemMessages.S2F23(7, "00000100", 6, 2, [2001, 2002]);
        Assert.Equal("S2F23 W <L [5] <U4 7> <A \"00000100\"> <U4 6> <U4 2> <L [2] <U4 2001> <U4 2002>>>", m.ToSml());
        Assert.Equal(SecsCodec.Encode(M(m.ToSml()).Item!), SecsCodec.Encode(m.Item!));
    }

    [Fact]
    public void S2F23_TextTrid_A()
    {
        var m = GemMessages.S2F23("T1", "000001", 1, 1, [2001]);
        Assert.StartsWith("S2F23 W <L [5] <A \"T1\">", m.ToSml());
    }

    [Fact]
    public void S2F23_TooLarge_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GemMessages.S2F23(1, "000001", (ulong)uint.MaxValue + 1, 1, [2001]));
    }

    [Fact]
    public void ParseS2F23_LenientIntegers()
    {
        var r = GemMessages.ParseS2F23(M("S2F23 W <L <U1 7> <A \"00000100\"> <U2 6> <I4 2> <L <U4 2001> <U4 2002>>>"));
        Assert.Equal((GemId)7, r.Trid);
        Assert.Equal("00000100", r.Period);
        Assert.Equal(6UL, r.TotalSamples);
        Assert.Equal(2UL, r.GroupSize);
        Assert.Equal([(GemId)2001, (GemId)2002], r.Svids);
    }

    [Fact]
    public void ParseS2F23Raw_KeepsTridItem()
    {
        var r = GemMessages.ParseS2F23Raw(M("S2F23 W <L <I2 7> <A \"000001\"> <U4 6> <U4 2> <L <U4 2001>>>"));
        Assert.Equal(SecsFormat.I2, r.Trid.Format);
        Assert.Equal(SecsFormat.List, r.Svids.Format);
    }

    [Theory]
    [InlineData("<L <U4 1> <A \"000001\"> <U4 6> <U4 2>>")]
    [InlineData("<L <U4 1> <U4 1> <U4 6> <U4 2> <L>>")]
    [InlineData("<L <U4 1> <A \"000001\"> <F4 6> <U4 2> <L>>")]
    [InlineData("<L <U4 1> <A \"000001\"> <U4 [2] 1 2> <U4 2> <L>>")]
    [InlineData("<L <U4 1> <A \"000001\"> <U4 6> <U4 2> <U4 1>>")]
    [InlineData("<L <B 1> <A \"000001\"> <U4 6> <U4 2> <L>>")]
    [InlineData("<L <U4 1> <A \"000001\"> <I4 -1> <U4 2> <L>>")]
    public void ParseS2F23_Malformed_Throws(string body)
    {
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F23(M("S2F23 W " + body)));
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F23Raw(M("S2F23 W " + body)));
    }

    [Fact]
    public void S2F24_RoundTrip()
    {
        var m = GemMessages.S2F24(5);
        Assert.Equal("S2F24 <B 0x05>", m.ToSml());
        Assert.Equal(5, GemMessages.ParseS2F24(m));
    }

    [Fact]
    public void S6F1_Golden()
    {
        var m = GemMessages.S6F1(7, 4, "2026111712000000", [SecsItem.F4(25), SecsItem.U4(3), SecsItem.F4(26), SecsItem.U4(4)]);
        Assert.Equal("S6F1 W <L [4] <U4 7> <U4 4> <A \"2026111712000000\"> <L [4] <F4 25> <U4 3> <F4 26> <U4 4>>>", m.ToSml());
        Assert.False(GemMessages.S6F1(7, 4, "x", [], replyExpected: false).ReplyExpected);
    }

    [Fact]
    public void ParseS6F1_Lenient()
    {
        var d = GemMessages.ParseS6F1(M("S6F1 W <L <A \"T1\"> <U2 4> <A \"t\"> <L <F4 25> <L <U1 1>>>>"));
        Assert.Equal(new GemId("T1"), d.Trid);
        Assert.Equal(4UL, d.SampleNumber);
        Assert.Equal("t", d.Time);
        Assert.Equal(2, d.Values.Count);
        Assert.Equal(SecsItem.L(SecsItem.U1(1)), d.Values[1]);
        Assert.Throws<FormatException>(() => GemMessages.ParseS6F1(M("S6F1 W <L <U4 1> <U4 4> <U4 1> <L>>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS6F1(M("S6F1 W <L <U4 1> <U4 4> <A \"t\">>")));
    }

    [Fact]
    public void S6F2_RoundTrip()
    {
        Assert.Equal(1, GemMessages.ParseS6F2(GemMessages.S6F2(1)));
        Assert.Equal("S6F2 <B 0x00>", GemMessages.S6F2(0).ToSml());
    }

    [Fact]
    public void WrongSf_Throws()
    {
        Assert.Throws<ArgumentException>(() => GemMessages.ParseS2F23(M("S2F25 W <L>")));
        Assert.Throws<ArgumentException>(() => GemMessages.ParseS6F1(M("S6F11 W <L>")));
        Assert.Throws<ArgumentException>(() => GemMessages.ParseS6F2(GemMessages.S6F12(0)));
    }

    [Theory]
    [InlineData("000001", 1000)]
    [InlineData("00000150", 1500)]
    [InlineData("010000", 3600000)]
    [InlineData("00000001", 10)]
    [InlineData("000000", 0)]
    public void TracePeriod_Parse(string text, int ms)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(ms), GemTime.ParseTracePeriod(text));
    }

    [Theory]
    [InlineData("000060")]
    [InlineData("006000")]
    [InlineData("0000001")]
    [InlineData("00a001")]
    [InlineData("")]
    [InlineData("0000000100")]
    public void TracePeriod_Parse_Invalid(string text)
    {
        Assert.Null(GemTime.ParseTracePeriod(text));
    }

    [Fact]
    public void TracePeriod_Format()
    {
        Assert.Equal("00000150", GemTime.FormatTracePeriod(TimeSpan.FromMilliseconds(1509)));
        Assert.Equal("01020304", GemTime.FormatTracePeriod(new TimeSpan(0, 1, 2, 3, 40)));
        Assert.Throws<ArgumentOutOfRangeException>(() => GemTime.FormatTracePeriod(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => GemTime.FormatTracePeriod(TimeSpan.FromMilliseconds(5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => GemTime.FormatTracePeriod(TimeSpan.FromHours(100)));
    }

    [Fact]
    public void TiaackConstants()
    {
        Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 5 }, new[] { GemAck.TiaackOk, GemAck.TiaackTooManySvids, GemAck.TiaackNoMoreTraces, GemAck.TiaackInvalidPeriod, GemAck.TiaackUnknownSvid, GemAck.TiaackInvalidGroupSize });
    }
}
