using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// GemMessages 리밋(S2F45~48): 골든 SML, 왕복, 받을 때 관대, 오류 규칙, ack 상수
public class GemMessagesLimitTests
{
    static SecsMessage M(string sml) => SecsMessage.ParseSml(sml);

    [Fact]
    public void S2F45_Golden()
    {
        var m = GemMessages.S2F45(1, [((GemId)2001, [(1, SecsItem.F4(30), SecsItem.F4(20)), (2, null, null)]), ((GemId)2002, null)]);
        Assert.Equal("S2F45 W <L [2] <U4 1> <L [2] <L [2] <U4 2001> <L [2] <L [2] <B 0x01> <L [2] <F4 30> <F4 20>>> <L [2] <B 0x02> <L [0]>>>> <L [2] <U4 2002> <L [0]>>>>", m.ToSml());
        var (dataId, defs) = GemMessages.ParseS2F45(m);
        Assert.Equal(SecsItem.U4(1), dataId);
        Assert.Equal(2, defs.Count);
        Assert.Equal((GemId)2001, defs[0].Vid);
        Assert.Equal(2, defs[0].Limits.Count);
        Assert.Equal(SecsItem.F4(30), defs[0].Limits[0].Upper);
        Assert.Null(defs[0].Limits[1].Upper);
        Assert.Null(defs[0].Limits[1].Lower);
        Assert.Empty(defs[1].Limits);
    }

    [Fact]
    public void S2F45_Empty_ReleaseAll()
    {
        Assert.Equal("S2F45 W <L [2] <U4 1> <L [0]>>", GemMessages.S2F45(1, []).ToSml());
    }

    [Fact]
    public void S2F45_HalfNull_Throws()
    {
        Assert.Throws<ArgumentException>(() => GemMessages.S2F45(1, [((GemId)2001, [(1, null, SecsItem.F4(1))])]));
        Assert.Throws<ArgumentException>(() => GemMessages.S2F45(1, [((GemId)2001, [(1, SecsItem.F4(1), null)])]));
    }

    [Fact]
    public void ParseS2F45_Lenient()
    {
        var (_, defs) = GemMessages.ParseS2F45(M("S2F45 W <L <U4 1> <L <L <A \"T\"> <L <L <U1 1> <L <A \"30\"> <A \"20\">>>>>>>"));
        Assert.Equal(new GemId("T"), defs[0].Vid);
        Assert.Equal(1, defs[0].Limits[0].LimitId);
        Assert.Equal(SecsItem.A("30"), defs[0].Limits[0].Upper);
        var raw = GemMessages.ParseS2F45Raw(M("S2F45 W <L <U4 1> <L <L <I2 2001> <L>>>>"));
        Assert.Equal(SecsFormat.I2, raw.Definitions[0].Vid.Format);
        Assert.Empty(raw.Definitions[0].Limits);
    }

    [Theory]
    [InlineData("<L <U4 1> <L <L <U4 2001> <L <L <B 1> <L <F4 1>>>>>>>")]
    [InlineData("<L <U4 1> <L <L <U4 2001> <L> <L>>>>")]
    [InlineData("<L <U4 1> <L <L <U4 2001> <L <L <A \"1\"> <L>>>>>>")]
    [InlineData("<L <U4 1> <L <L <U4 2001> <L <L <U2 300> <L>>>>>>")]
    [InlineData("<L <U4 1> <L <L <U4 2001> <L <L <B 1>>>>>>")]
    [InlineData("<L <U4 1> <L <L <B 1> <L>>>>")]
    [InlineData("<L <U4 1> <U4 2>>")]
    public void ParseS2F45_Malformed_Throws(string body)
    {
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F45(M("S2F45 W " + body)));
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F45Raw(M("S2F45 W " + body)));
    }

    [Fact]
    public void S2F46_Golden()
    {
        var m = GemMessages.S2F46(1, [new GemLimitError(2001, 4, 1, 2), new GemLimitError(2002, 1, null, null)]);
        Assert.Equal("S2F46 <L [2] <B 0x01> <L [2] <L [3] <U4 2001> <B 0x04> <L [2] <B 0x01> <B 0x02>>> <L [3] <U4 2002> <B 0x01> <L [0]>>>>", m.ToSml());
        var (vlaack, errors) = GemMessages.ParseS2F46(m);
        Assert.Equal(1, vlaack);
        Assert.Equal((GemId)2001, errors[0].Vid);
        Assert.Equal((byte)4, errors[0].Lvack);
        Assert.Equal((byte)1, errors[0].LimitId);
        Assert.Equal((byte)2, errors[0].Limitack);
        Assert.Null(errors[1].LimitId);
        Assert.Null(errors[1].Limitack);
    }

    [Fact]
    public void S2F46_Ok()
    {
        Assert.Equal("S2F46 <L [2] <B 0x00> <L [0]>>", GemMessages.S2F46(0).ToSml());
        Assert.Empty(GemMessages.ParseS2F46(GemMessages.S2F46(0)).Errors);
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F46(M("S2F46 <L <B 1> <L <L <U4 1> <B 1> <L <B 1>>>>>")));
    }

    [Fact]
    public void ParseS2F46_EmptyItemPair_IsNoLimit()
    {
        // 다른 구현은 "LIMITID 없음"을 <L> 대신 길이 0 아이템 둘(<L [2] <B> <B>>)로 쓰기도 한다(secsgem 교차 확인에서 발견)
        var (vlaack, errors) = GemMessages.ParseS2F46(M("S2F46 <L <B 1> <L <L <U2 2002> <B 1> <L <B> <B>>>>>"));
        Assert.Equal(1, vlaack);
        var e = Assert.Single(errors);
        Assert.Equal(((GemId)2002, (byte)1, (byte?)null, (byte?)null), (e.Vid, e.Lvack, e.LimitId, e.Limitack));
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F46(M("S2F46 <L <B 1> <L <L <U4 1> <B 4> <L <B 1> <B>>>>>")));   // 하나만 비면 구조 오류
    }

    [Fact]
    public void S2F47_RoundTrip()
    {
        var m = GemMessages.S2F47([2001]);
        Assert.Equal("S2F47 W <L [1] <U4 2001>>", m.ToSml());
        Assert.Equal([(GemId)2001], GemMessages.ParseS2F47(m));
        Assert.Equal("S2F47 W <L [0]>", GemMessages.S2F47().ToSml());
        Assert.Empty(GemMessages.ParseS2F47(M("S2F47 W <L>")));
        Assert.Equal(SecsFormat.I1, GemMessages.ParseS2F47Raw(M("S2F47 W <L <I1 5>>"))[0].Format);
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F47(M("S2F47 W <U4 1>")));
    }

    [Fact]
    public void S2F48_Golden()
    {
        var m = GemMessages.S2F48([
            new GemLimitInfo(2001, "C", SecsItem.F4(0), SecsItem.F4(100), [new GemLimit(1, SecsItem.F4(30), SecsItem.F4(20))]),
            new GemLimitInfo(2002, null, null, null, []),
        ]);
        Assert.Equal("S2F48 <L [2] <L [2] <U4 2001> <L [4] <A \"C\"> <F4 0> <F4 100> <L [1] <L [3] <B 0x01> <F4 30> <F4 20>>>>> <L [2] <U4 2002> <L [0]>>>", m.ToSml());
        var infos = GemMessages.ParseS2F48(m);
        Assert.True(infos[0].IsSupported);
        Assert.Equal("C", infos[0].Units);
        Assert.Equal(SecsItem.F4(100), infos[0].LimitMax);
        var l = Assert.Single(infos[0].Limits);
        Assert.Equal((1, SecsItem.F4(30), SecsItem.F4(20)), (l.LimitId, l.Upper, l.Lower));
        Assert.False(infos[1].IsSupported);
        Assert.Null(infos[1].Units);
        Assert.Empty(infos[1].Limits);
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F48(M("S2F48 <L <L <U4 1> <L <A \"C\">>>>")));
    }

    [Fact]
    public void WrongSf_Throws()
    {
        Assert.Throws<ArgumentException>(() => GemMessages.ParseS2F45(M("S2F47 W <L>")));
        Assert.Throws<ArgumentException>(() => GemMessages.ParseS2F46(M("S2F48 <L>")));
        Assert.Throws<ArgumentException>(() => GemMessages.ParseS2F47(M("S2F45 W <L>")));
        Assert.Throws<ArgumentException>(() => GemMessages.ParseS2F48(M("S2F46 <L>")));
    }

    [Fact]
    public void AckConstants()
    {
        Assert.Equal(new byte[] { 0, 1, 2 }, new[] { GemAck.VlaackOk, GemAck.VlaackLimitError, GemAck.VlaackCannotPerform });
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, new[] { GemAck.LvackUnknownVid, GemAck.LvackNoLimits, GemAck.LvackRepeatedVid, GemAck.LvackLimitValueError });
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7 }, new[] { GemAck.LimitackUnknownLimitId, GemAck.LimitackUpperAboveMax, GemAck.LimitackLowerBelowMin, GemAck.LimitackUpperBelowLower, GemAck.LimitackIllegalFormat, GemAck.LimitackNotNumeric, GemAck.LimitackDuplicateLimitId });
    }

    [Fact]
    public void LimitZone_Values()
    {
        Assert.Equal([GemLimitZone.NoZone, GemLimitZone.Below, GemLimitZone.Above], Enum.GetValues<GemLimitZone>());
    }
}
