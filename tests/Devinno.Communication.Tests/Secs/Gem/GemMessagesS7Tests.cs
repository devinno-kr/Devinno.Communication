using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// GemMessages 레시피 메시지(S7F1~F6·F17~F20): 골든 SML, 왕복, 받을 때 관대, 오류 규칙, GemProcessProgram 검사
public class GemMessagesS7Tests
{
    static SecsMessage M(string sml) => SecsMessage.ParseSml(sml);

    static readonly GemProcessProgram R1 = new("RCP-1", SecsItem.B(1, 2, 3));

    [Fact]
    public void S7F1_Golden()
    {
        var m = GemMessages.S7F1("RCP-1", 300);
        Assert.Equal("S7F1 W <L [2] <A \"RCP-1\"> <U4 300>>", m.ToSml());
        Assert.Equal(("RCP-1", 300UL), GemMessages.ParseS7F1(m));
        Assert.Equal(("RCP-1", 300UL), GemMessages.ParseS7F1(M("S7F1 W <L <A \"RCP-1\"> <U2 300>>")));
        Assert.Equal(("RCP-1", 300UL), GemMessages.ParseS7F1(M("S7F1 W <L <A \"RCP-1\"> <I4 300>>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS7F1(M("S7F1 W <L <A \"RCP-1\"> <I4 -1>>")));
    }

    [Fact]
    public void S7F2_S7F4_S7F18_Golden()
    {
        Assert.Equal("S7F2 <B 0x00>", GemMessages.S7F2(0).ToSml());
        Assert.Equal("S7F4 <B 0x02>", GemMessages.S7F4(2).ToSml());
        Assert.Equal("S7F18 <B 0x04>", GemMessages.S7F18(4).ToSml());
        Assert.False(GemMessages.S7F2(0).ReplyExpected);
        Assert.False(GemMessages.S7F4(0).ReplyExpected);
        Assert.False(GemMessages.S7F18(0).ReplyExpected);
        Assert.Equal(5, GemMessages.ParseS7F2(GemMessages.S7F2(5)));
        Assert.Equal(3, GemMessages.ParseS7F4(GemMessages.S7F4(3)));
        Assert.Equal(1, GemMessages.ParseS7F18(M("S7F18 <U1 1>")));
    }

    [Fact]
    public void S7F3_Golden_KeepsBodyFormat()
    {
        var m = GemMessages.S7F3(R1);
        Assert.Equal("S7F3 W <L [2] <A \"RCP-1\"> <B 0x01 0x02 0x03>>", m.ToSml());
        Assert.True(m.ReplyExpected);
        foreach (var body in new[] { SecsItem.A("x=1"), SecsItem.U2(7), SecsItem.B(1, 2, 3) })
        {
            var (ppid, got) = GemMessages.ParseS7F3(GemMessages.S7F3(new GemProcessProgram("R", body)));
            Assert.Equal("R", ppid);
            Assert.Equal(body, got);
        }
    }

    [Fact]
    public void S7F3_ListBody_FormatException()
    {
        var ex = Assert.Throws<FormatException>(() => GemMessages.ParseS7F3(M("S7F3 W <L <A \"R\"> <L>>")));
        Assert.Contains("S7F3", ex.Message);
        Assert.Throws<FormatException>(() => GemMessages.ParseS7F3(M("S7F3 W <L <A \"R\">>")));
    }

    [Fact]
    public void S7F5_S7F6_Golden()
    {
        Assert.Equal("S7F5 W <A \"RCP-1\">", GemMessages.S7F5("RCP-1").ToSml());
        Assert.Equal("RCP-1", GemMessages.ParseS7F5(GemMessages.S7F5("RCP-1")));
        var denied = GemMessages.S7F6(null);
        Assert.Equal("S7F6 <L [0]>", denied.ToSml());
        Assert.Null(GemMessages.ParseS7F6(denied));
        var got = GemMessages.ParseS7F6(GemMessages.S7F6(R1));
        Assert.Equal(("RCP-1", SecsItem.B(1, 2, 3)), got);
    }

    [Fact]
    public void S7F17_S7F20_Golden()
    {
        Assert.Equal("S7F17 W <L [2] <A \"A\"> <A \"B\">>", GemMessages.S7F17(["A", "B"]).ToSml());
        Assert.Equal("S7F17 W <L [0]>", GemMessages.S7F17([]).ToSml());
        Assert.Equal("S7F20 <L [0]>", GemMessages.S7F20([]).ToSml());
        Assert.Equal(new[] { "A", "B" }, GemMessages.ParseS7F17(GemMessages.S7F17(["A", "B"])));
        Assert.Equal(new[] { "X" }, GemMessages.ParseS7F20(GemMessages.S7F20(["X"])));
        Assert.Empty(GemMessages.ParseS7F17(M("S7F17 W")));
    }

    [Fact]
    public void S7F19_NoBody()
    {
        var m = GemMessages.S7F19();
        Assert.Null(m.Item);
        Assert.True(m.ReplyExpected);
        Assert.Equal(7, m.Stream);
        Assert.Equal(19, m.Function);
    }

    [Fact]
    public void GemProcessProgram_Validation()
    {
        Assert.Equal("ppid", Assert.Throws<ArgumentException>(() => new GemProcessProgram("", SecsItem.B())).ParamName);
        Assert.Throws<ArgumentException>(() => new GemProcessProgram(new string('x', 121), SecsItem.B()));
        Assert.Throws<ArgumentException>(() => new GemProcessProgram("가", SecsItem.B()));
        Assert.Equal(120, new GemProcessProgram(new string('x', 120), SecsItem.B()).Ppid.Length);
        Assert.Equal("body", Assert.Throws<ArgumentException>(() => new GemProcessProgram("R", SecsItem.L())).ParamName);
        Assert.Throws<ArgumentNullException>(() => new GemProcessProgram(null!, SecsItem.B()));
        Assert.Throws<ArgumentNullException>(() => new GemProcessProgram("R", null!));
        Assert.Throws<ArgumentException>(() => GemMessages.S7F5(""));
        Assert.Throws<ArgumentException>(() => GemMessages.S7F17(["ok", ""]));
        Assert.Equal(120, GemProcessProgram.MaxPpidLength);
    }

    [Fact]
    public void Parse_WrongStreamFunction_ArgumentException()
    {
        Assert.Equal("message", Assert.Throws<ArgumentException>(() => GemMessages.ParseS7F3(GemMessages.S7F5("x"))).ParamName);
    }

    [Fact]
    public void Parse_PpidNotAscii_FormatException()
    {
        Assert.Throws<FormatException>(() => GemMessages.ParseS7F5(new SecsMessage(7, 5, true, SecsItem.U4(1))));
        Assert.Throws<FormatException>(() => GemMessages.ParseS7F17(M("S7F17 W <L <U4 1>>")));
    }
}
