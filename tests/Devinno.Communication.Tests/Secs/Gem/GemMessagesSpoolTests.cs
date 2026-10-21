using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// GemMessages 스풀 메시지(S2F43/44, S6F23/24): 골든 SML, 왕복, 받을 때 관대, 오류 규칙
public class GemMessagesSpoolTests
{
    static SecsMessage M(string sml) => SecsMessage.ParseSml(sml);

    [Fact]
    public void S2F43_Golden()
    {
        var m = GemMessages.S2F43([(6, new byte[] { 11 }), (5, Array.Empty<byte>())]);
        Assert.Equal("S2F43 W <L [2] <L [2] <U1 6> <L [1] <U1 11>>> <L [2] <U1 5> <L [0]>>>", m.ToSml());
        Assert.True(m.ReplyExpected);
    }

    [Fact]
    public void S2F43_Empty_IsEmptyList()
    {
        var m = GemMessages.S2F43([]);
        Assert.Equal("S2F43 W <L [0]>", m.ToSml());
        Assert.Empty(GemMessages.ParseS2F43(m));
        Assert.Empty(GemMessages.ParseS2F43(M("S2F43 W")));
    }

    [Fact]
    public void S2F43_RoundTrip()
    {
        var parsed = GemMessages.ParseS2F43(GemMessages.S2F43([(6, new byte[] { 11, 13 }), (5, Array.Empty<byte>())]));
        Assert.Equal(2, parsed.Count);
        Assert.Equal((byte)6, parsed[0].Stream);
        Assert.Equal(new byte[] { 11, 13 }, parsed[0].Functions);
        Assert.Equal((byte)5, parsed[1].Stream);
        Assert.Empty(parsed[1].Functions);
    }

    [Fact]
    public void S2F43_ParseLenient()
    {
        var parsed = GemMessages.ParseS2F43(M("S2F43 W <L <L <I4 6> <L <U2 11>>>>"));
        Assert.Equal((byte)6, parsed[0].Stream);
        Assert.Equal(new byte[] { 11 }, parsed[0].Functions);
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F43(M("S2F43 W <L <L <U2 256> <L>>>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F43(M("S2F43 W <L <L <U1 6> <L <U2 256>>>>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F43(M("S2F43 W <L <L <I1 -1> <L>>>")));
    }

    [Fact]
    public void S2F44_Golden_Accepted()
    {
        var m = GemMessages.S2F44(0);
        Assert.Equal("S2F44 <L [2] <B 0x00> <L [0]>>", m.ToSml());
        Assert.False(m.ReplyExpected);
        var (rspack, errors) = GemMessages.ParseS2F44(m);
        Assert.Equal((byte)0, rspack);
        Assert.Empty(errors);
    }

    [Fact]
    public void S2F44_Golden_Errors()
    {
        var m = GemMessages.S2F44(1, [(1, 1, Array.Empty<byte>()), (6, 4, new byte[] { 12 })]);
        Assert.Equal("S2F44 <L [2] <B 0x01> <L [2] <L [3] <U1 1> <B 0x01> <L [0]>> <L [3] <U1 6> <B 0x04> <L [1] <U1 12>>>>>", m.ToSml());
        var (rspack, errors) = GemMessages.ParseS2F44(m);
        Assert.Equal((byte)1, rspack);
        Assert.Equal(2, errors.Count);
        Assert.Equal(((byte)1, (byte)1), (errors[0].Stream, errors[0].Strack));
        Assert.Empty(errors[0].Functions);
        Assert.Equal(((byte)6, (byte)4), (errors[1].Stream, errors[1].Strack));
        Assert.Equal(new byte[] { 12 }, errors[1].Functions);
    }

    [Fact]
    public void S2F44_ParseLenient()
    {
        var (rspack, errors) = GemMessages.ParseS2F44(M("S2F44 <L <U1 1> <L <L <I2 6> <U2 3> <L <I4 12>>>>>"));
        Assert.Equal((byte)1, rspack);
        Assert.Equal(((byte)6, (byte)3), (errors[0].Stream, errors[0].Strack));
        Assert.Equal(new byte[] { 12 }, errors[0].Functions);
    }

    [Fact]
    public void S6F23_S6F24_Golden()
    {
        var q = GemMessages.S6F23(0);
        Assert.Equal("S6F23 W <U1 0>", q.ToSml());
        Assert.True(q.ReplyExpected);
        Assert.Equal((byte)0, GemMessages.ParseS6F23(q));
        Assert.Equal((byte)1, GemMessages.ParseS6F23(GemMessages.S6F23(1)));

        var a = GemMessages.S6F24(2);
        Assert.Equal("S6F24 <B 0x02>", a.ToSml());
        Assert.False(a.ReplyExpected);
        Assert.Equal((byte)2, GemMessages.ParseS6F24(a));
    }

    [Fact]
    public void S6F23_ParseLenient()
    {
        Assert.Equal((byte)1, GemMessages.ParseS6F23(M("S6F23 W <B 0x01>")));
        Assert.Equal((byte)1, GemMessages.ParseS6F23(M("S6F23 W <I2 1>")));
        Assert.Equal((byte)1, GemMessages.ParseS6F24(M("S6F24 <U4 1>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS6F23(M("S6F23 W <U2 256>")));
    }

    [Fact]
    public void GemAck_Constants()
    {
        Assert.Equal(0, GemAck.RspackAccepted); Assert.Equal(1, GemAck.RspackRejected);
        Assert.Equal(1, GemAck.StrackNotAllowed); Assert.Equal(2, GemAck.StrackUnknownStream);
        Assert.Equal(3, GemAck.StrackUnknownFunction); Assert.Equal(4, GemAck.StrackSecondaryFunction);
        Assert.Equal(0, GemAck.RsdcTransmit); Assert.Equal(1, GemAck.RsdcPurge);
        Assert.Equal(0, GemAck.RsdaAccepted); Assert.Equal(1, GemAck.RsdaBusy); Assert.Equal(2, GemAck.RsdaNoData);
    }

    [Fact]
    public void Parse_WrongStreamFunction_ArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => GemMessages.ParseS2F43(M("S2F41 W <L [0]>")));
        Assert.Equal("message", ex.ParamName);
        Assert.Throws<ArgumentException>(() => GemMessages.ParseS2F44(M("S2F43 W <L [0]>")));
        Assert.Throws<ArgumentException>(() => GemMessages.ParseS6F23(M("S6F24 <B 0>")));
        Assert.Throws<ArgumentException>(() => GemMessages.ParseS6F24(M("S6F23 W <U1 0>")));
        Assert.Throws<ArgumentNullException>(() => GemMessages.ParseS2F43(null!));
    }

    [Fact]
    public void Parse_BadStructure_FormatException()
    {
        var ex = Assert.Throws<FormatException>(() => GemMessages.ParseS2F43(M("S2F43 W <A \"x\">")));
        Assert.Contains("S2F43", ex.Message);
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F43(M("S2F43 W <L <L <U1 6>>>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F43(M("S2F43 W <L <U1 6>>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F43(M("S2F43 W <L <L <U1 6> <U1 11>>>")));
        Assert.Contains("S2F44", Assert.Throws<FormatException>(() => GemMessages.ParseS2F44(M("S2F44 <B 0>"))).Message);
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F44(M("S2F44 <L <B 0> <L <L <U1 1> <B 1>>>>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F44(M("S2F44 <L <B 0>>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS6F23(M("S6F23 W")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS6F23(M("S6F23 W <A \"x\">")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS6F24(M("S6F24 <L <B 0>>")));
    }
}
