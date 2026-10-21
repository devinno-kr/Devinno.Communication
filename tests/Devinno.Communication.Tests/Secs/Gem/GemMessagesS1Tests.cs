using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// GemMessages S1: 골든 SML, 모든 ID 포맷 왕복, 받을 때 관대, 오류 규칙, internal(3층용) 겹치기
public class GemMessagesS1Tests
{
    static SecsMessage M(string sml) => SecsMessage.ParseSml(sml);
    static GemId[] Ids(params GemId[] ids) => ids;

    [Fact]
    public void Build_Golden()
    {
        Assert.Equal("S1F13 W <L [2] <A \"DV-100\"> <A \"1.0.0\">>", GemMessages.S1F13("DV-100", "1.0.0").ToSml());
        Assert.Equal("S1F13 W <L [0]>", GemMessages.S1F13().ToSml());
        Assert.Equal("S1F14 <L [2] <B 0x00> <L [0]>>", GemMessages.S1F14(0).ToSml());
        Assert.Equal("S1F14 <L [2] <B 0x00> <L [2] <A \"DV-100\"> <A \"1.0.0\">>>", GemMessages.S1F14(0, "DV-100", "1.0.0").ToSml());
        Assert.Equal("S1F1 W", GemMessages.S1F1().ToSml());
        Assert.Equal("S1F2 <L [0]>", GemMessages.S1F2().ToSml());
        Assert.Equal("S1F2 <L [2] <A \"DV-100\"> <A \"1.0.0\">>", GemMessages.S1F2("DV-100", "1.0.0").ToSml());
        Assert.Equal("S1F15 W", GemMessages.S1F15().ToSml());
        Assert.Equal("S1F17 W", GemMessages.S1F17().ToSml());
        Assert.Equal("S1F18 <B 0x01>", GemMessages.S1F18(1).ToSml());
        Assert.Equal("S1F16 <B 0x00>", GemMessages.S1F16(0).ToSml());
        Assert.Equal("S1F3 W <L [2] <U2 1> <U2 2>>", GemMessages.S1F3([1, 2], new GemFormats { Vid = SecsFormat.U2 }).ToSml());
        Assert.Equal("S1F3 W <L [0]>", GemMessages.S1F3(null).ToSml());
        Assert.Equal("S1F4 <L [2] <F4 25> <U1 5>>", GemMessages.S1F4([SecsItem.F4(25), SecsItem.U1(5)]).ToSml());
        Assert.Equal("S1F11 W <L [1] <U4 1001>>", GemMessages.S1F11([1001]).ToSml());
        Assert.Equal("S1F12 <L [1] <L [3] <U4 1001> <A \"Temp\"> <A \"C\">>>",
            GemMessages.S1F12([new GemVariableInfo(1001, "Temp", "C")]).ToSml());
    }

    public static TheoryData<SecsFormat> IdFormats => [SecsFormat.U1, SecsFormat.U2, SecsFormat.U4, SecsFormat.U8,
        SecsFormat.I1, SecsFormat.I2, SecsFormat.I4, SecsFormat.I8, SecsFormat.ASCII];

    [Theory]
    [MemberData(nameof(IdFormats))]
    public void RoundTrip_AllIdFormats(SecsFormat format)
    {
        var fmt = new GemFormats { Vid = format };
        GemId Expected(long n) => format == SecsFormat.ASCII ? new GemId(n.ToString()) : n;

        Assert.Equal(Ids(Expected(7), Expected(8)), GemMessages.ParseS1F3(GemMessages.S1F3([7, 8], fmt)));
        Assert.Equal(Ids(Expected(7), Expected(8)), GemMessages.ParseS1F11(GemMessages.S1F11([7, 8], fmt)));

        var info = Assert.Single(GemMessages.ParseS1F12(GemMessages.S1F12([new GemVariableInfo(7, "N", "U")], fmt)));
        Assert.Equal((Expected(7), "N", "U"), (info.Id, info.Name, info.Units));

        Assert.Equal(((byte)1, "M", "S"), GemMessages.ParseS1F14(GemMessages.S1F14(1, "M", "S")));
        Assert.Equal(((byte)0, (string?)null, (string?)null), GemMessages.ParseS1F14(GemMessages.S1F14(0)));
        Assert.Equal(((string?)null, (string?)null), GemMessages.ParseS1F13(GemMessages.S1F13()));
        Assert.Equal(("M", "S"), GemMessages.ParseS1F13(GemMessages.S1F13("M", "S")));
        Assert.Equal(("M", "S"), GemMessages.ParseS1F2(GemMessages.S1F2("M", "S")));
        Assert.Equal((byte)2, GemMessages.ParseS1F16(GemMessages.S1F16(2)));
        Assert.Equal((byte)2, GemMessages.ParseS1F18(GemMessages.S1F18(2)));

        var values = GemMessages.ParseS1F4(GemMessages.S1F4([SecsItem.F4(25), SecsItem.L()]));
        Assert.Equal(new[] { "<F4 25>", "<L [0]>" }, values.Select(v => v.ToSml()));
    }

    [Fact]
    public void Lenient()
    {
        Assert.Equal((byte)2, GemMessages.ParseS1F18(M("S1F18 <U1 2>")));
        Assert.Equal((byte)2, GemMessages.ParseS1F18(M("S1F18 <I4 2>")));
        Assert.Equal((byte)2, GemMessages.ParseS1F18(M("S1F18 <B 0x02>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS1F18(M("S1F18 <U4 300>")));

        Assert.Empty(GemMessages.ParseS1F3(M("S1F3 W")));
        Assert.Empty(GemMessages.ParseS1F3(M("S1F3 W <U4>")));
        Assert.Equal(Ids(1, 2), GemMessages.ParseS1F3(M("S1F3 W <U4 1 2>")));
        Assert.Equal(Ids("X"), GemMessages.ParseS1F3(M("S1F3 W <A \"X\">")));
        Assert.Equal(Ids(1, "X"), GemMessages.ParseS1F3(M("S1F3 W <L <I2 1> <A \"X\">>")));   // 포맷 섞임도 2층은 받음(판단은 3층)

        Assert.Equal(((byte)0, (string?)null, (string?)null), GemMessages.ParseS1F14(M("S1F14 <L <B 0> <A \"x\">>")));
        Assert.Equal(("", ""), GemMessages.ParseS1F2(M("S1F2 <L>")));
        Assert.Equal(((string?)null, (string?)null), GemMessages.ParseS1F13(M("S1F13 W <L <U4 1>>")));

        Assert.Empty(GemMessages.ParseS1F4(M("S1F4")));
        Assert.Empty(GemMessages.ParseS1F12(M("S1F12")));
        var info = Assert.Single(GemMessages.ParseS1F12(M("S1F12 <L <L <I2 77> <A> <A>>>")));
        Assert.Equal(((GemId)77, "", ""), (info.Id, info.Name, info.Units));
    }

    [Fact]
    public void Errors()
    {
        var wrong = Assert.Throws<ArgumentException>(() => GemMessages.ParseS1F18(GemMessages.S1F16(0)));
        Assert.Equal("message", wrong.ParamName);
        Assert.Equal("message", Assert.Throws<ArgumentNullException>(() => GemMessages.ParseS1F18(null!)).ParamName);

        var ex = Assert.Throws<FormatException>(() => GemMessages.ParseS1F2(M("S1F2 <L <U4 1>>")));
        Assert.Contains("S1F2 응답 본문이 기대(", ex.Message);
        Assert.Contains("<U4 1>", ex.Message);

        var odd = Assert.Throws<FormatException>(() => GemMessages.ParseS1F3(M("S1F3 W <L <F4 1>>")));
        Assert.StartsWith("S1F3 본문이 기대(", odd.Message);

        var big = new SecsMessage(1, 2, false, SecsItem.L([.. Enumerable.Range(0, 10).Select(_ => SecsItem.A(new string('x', 20000)))]));
        var bigEx = Assert.Throws<FormatException>(() => GemMessages.ParseS1F2(big));
        Assert.True(bigEx.Message.Length < GemItems.SmlLimit + 300, $"길이 {bigEx.Message.Length}");
        Assert.Contains("…(생략: ", bigEx.Message);

        Assert.Throws<FormatException>(() => GemMessages.ParseS1F2(M("S1F2")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS1F14(M("S1F14 <L <A \"0\"> <L>>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS1F14(M("S1F14 <B 0>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS1F12(M("S1F12 <L <L <U4 1> <A \"N\">>>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS1F4(M("S1F4 <U4 1>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS1F16(M("S1F16")));

        Assert.Throws<ArgumentOutOfRangeException>(() => GemMessages.S1F3([300], new GemFormats { Vid = SecsFormat.U1 }));
        Assert.Throws<ArgumentException>(() => GemMessages.S1F3(["X"], GemFormats.Default));
        Assert.Throws<ArgumentException>(() => GemMessages.S1F13("a", null));
        Assert.Throws<ArgumentException>(() => GemMessages.S1F2(null, "b"));
        Assert.Throws<ArgumentException>(() => GemMessages.S1F14(0, "a", null));
        Assert.Throws<ArgumentNullException>(() => GemMessages.S1F4(null!));
        Assert.Throws<ArgumentNullException>(() => GemMessages.S1F12((IReadOnlyList<GemVariableInfo>)null!));
    }

    [Fact]
    public void Internal_EchoAndNameOverloads()
    {
        // 받은 ID 아이템 그대로 되돌리기(D3): 포맷이 설정과 달라도 바이트 그대로
        Assert.Equal("S1F12 <L [1] <L [3] <I2 77> <A \"\"> <A \"\">>>",
            GemMessages.S1F12([(SecsItem.I2(77), "", "")]).ToSml());
        Assert.Equal("S1F22 <L [1] <L [3] <I2 77> <A \"\"> <A \"\">>>",
            GemMessages.S1F22([(SecsItem.I2(77), "", "")]).ToSml());
        Assert.Equal("S1F22 <L [1] <L [3] <U2 5> <A \"D\"> <A \"mm\">>>",
            GemMessages.S1F22([new GemVariableInfo(5, "D", "mm")], new GemFormats { Vid = SecsFormat.U2 }).ToSml());
        Assert.Equal("S1F21 W <L [1] <A \"5\">>", GemMessages.S1F21([5], new GemFormats { Vid = SecsFormat.ASCII }).ToSml());
        Assert.Equal("S1F23 W <L [1] <I4 5001>>", GemMessages.S1F23([5001], new GemFormats { Ceid = SecsFormat.I4 }).ToSml());

        var s1f24 = GemMessages.S1F24([(SecsItem.I2(9), "", []), (SecsItem.U4(5001), "Start", [1, 2])], new GemFormats { Vid = SecsFormat.U2 });
        Assert.Equal("S1F24 <L [2] <L [3] <I2 9> <A \"\"> <L [0]>> <L [3] <U4 5001> <A \"Start\"> <L [2] <U2 1> <U2 2>>>>", s1f24.ToSml());

        var events = GemMessages.ParseS1F24(s1f24);
        Assert.Equal(2, events.Count);
        Assert.Equal(((GemId)5001, "Start"), (events[1].Ceid, events[1].Name));
        Assert.Equal(Ids(1, 2), events[1].Vids);
        Assert.Throws<FormatException>(() => GemMessages.ParseS1F24(M("S1F24 <L <L <U4 1> <A \"x\"> <U4 1 2>>>")));

        Assert.Equal(Ids(5), GemMessages.ParseS1F21(M("S1F21 W <L <U4 5>>")));
        Assert.Equal(Ids(5001), GemMessages.ParseS1F23(M("S1F23 W <L <U4 5001>>")));
        var dv = Assert.Single(GemMessages.ParseS1F22(M("S1F22 <L <L <U4 5> <A \"D\"> <A \"mm\">>>")));
        Assert.Equal(((GemId)5, "D", "mm"), (dv.Id, dv.Name, dv.Units));
        Assert.Throws<ArgumentException>(() => GemMessages.ParseS1F22(M("S1F12 <L>")));
    }
}
