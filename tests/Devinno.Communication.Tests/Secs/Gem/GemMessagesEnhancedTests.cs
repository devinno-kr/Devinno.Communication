using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// GemMessages 확장 리모트 명령(S2F49/50)과 다중 블록 문의(S6F5/6): 골든 SML, 왕복, 받을 때 관대, 오류 규칙
public class GemMessagesEnhancedTests
{
    static SecsMessage M(string sml) => SecsMessage.ParseSml(sml);

    [Fact]
    public void S2F49_Golden()
    {
        var m = GemMessages.S2F49(7, "CH1", "START", [("LOT", SecsItem.A("L1")), ("STEPS", SecsItem.L(SecsItem.U4(1), SecsItem.U4(2)))]);
        Assert.Equal("S2F49 W <L [4] <U4 7> <A \"CH1\"> <A \"START\"> <L [2] <L [2] <A \"LOT\"> <A \"L1\">> <L [2] <A \"STEPS\"> <L [2] <U4 1> <U4 2>>>>>", m.ToSml());
        var (dataId, objspec, command, ps) = GemMessages.ParseS2F49(m);
        Assert.Equal((GemId)7, dataId);
        Assert.Equal("CH1", objspec);
        Assert.Equal("START", command);
        Assert.Equal(2, ps.Count);
        Assert.Equal(SecsItem.L(SecsItem.U4(1), SecsItem.U4(2)), ps[1].Value);
    }

    [Fact]
    public void S2F49_Empty()
    {
        var m = GemMessages.S2F49(1, "", "STOP");
        Assert.Equal("S2F49 W <L [4] <U4 1> <A \"\"> <A \"STOP\"> <L [0]>>", m.ToSml());
        Assert.Equal("", GemMessages.ParseS2F49(m).ObjectSpec);
    }

    [Fact]
    public void S2F49_ParseLenient()
    {
        Assert.Equal((GemId)7, GemMessages.ParseS2F49(M("S2F49 W <L <U1 7> <A \"\"> <A \"X\"> <L>>")).DataId);
        Assert.Equal(new GemId("D1"), GemMessages.ParseS2F49(M("S2F49 W <L <A \"D1\"> <A \"\"> <A \"X\"> <L>>")).DataId);
        Assert.Equal("5", GemMessages.ParseS2F49(M("S2F49 W <L <U4 1> <A \"\"> <U4 5> <L>>")).Command);
    }

    [Fact]
    public void S2F49_BadStructure_FormatException()
    {
        Assert.Contains("S2F49", Assert.Throws<FormatException>(() => GemMessages.ParseS2F49(M("S2F49 W <L <U4 1> <A \"x\"> <L>>"))).Message);
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F49(M("S2F49 W <L <U4 1> <U4 1> <A \"X\"> <L>>")));
    }

    [Fact]
    public void S2F50_Golden()
    {
        var m = GemMessages.S2F50(3, [("LOT", SecsItem.U1(1)), ("STEPS", SecsItem.L(SecsItem.U1(0), SecsItem.U1(2)))]);
        Assert.Equal("S2F50 <L [2] <B 0x03> <L [2] <L [2] <A \"LOT\"> <U1 1>> <L [2] <A \"STEPS\"> <L [2] <U1 0> <U1 2>>>>>", m.ToSml());
        Assert.Equal("S2F50 <L [2] <B 0x00> <L [0]>>", GemMessages.S2F50(0).ToSml());
        var r = GemMessages.ParseS2F50(m);
        Assert.Equal(3, r.Hcack);
        Assert.Equal(SecsItem.L(SecsItem.U1(0), SecsItem.U1(2)), r.ParameterAcks[1].Cepack);
    }

    [Fact]
    public void ParseS2F50_Lenient()
    {
        var r = GemMessages.ParseS2F50(M("S2F50 <L <U1 4> <L <L <A \"A\"> <B 0x01>> <L <A \"B\"> <I2 1>>>>"));
        Assert.True(r.IsAccepted);
        Assert.Equal(SecsItem.B(1), r.ParameterAcks[0].Cepack);
        Assert.Equal(SecsItem.I2(1), r.ParameterAcks[1].Cepack);
        Assert.False(GemMessages.ParseS2F50(GemMessages.S2F50(3)).IsAccepted);
    }

    [Fact]
    public void S6F5_S6F6_Golden()
    {
        var m = GemMessages.S6F5(9, 300);
        Assert.Equal("S6F5 W <L [2] <U4 9> <U4 300>>", m.ToSml());
        Assert.Equal(((GemId)9, 300UL), GemMessages.ParseS6F5(m));
        Assert.Equal("S6F6 <B 0x01>", GemMessages.S6F6(1).ToSml());
        Assert.Equal(2, GemMessages.ParseS6F6(M("S6F6 <U1 2>")));
    }

    [Fact]
    public void Parse_WrongStreamFunction_ArgumentException()
    {
        Assert.Throws<ArgumentException>(() => GemMessages.ParseS2F49(GemMessages.S2F41("X")));
        Assert.Throws<ArgumentException>(() => GemMessages.ParseS6F6(GemMessages.S6F5(1, 1)));
    }
}
