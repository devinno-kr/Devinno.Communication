using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// GemMessages S2: 골든 SML, 모든 ID 포맷 왕복, 받을 때 관대, 오류 규칙, internal(3층용) 겹치기
public class GemMessagesS2Tests
{
    static SecsMessage M(string sml) => SecsMessage.ParseSml(sml);
    static GemId[] Ids(params GemId[] ids) => ids;

    [Fact]
    public void Build_Golden()
    {
        Assert.Equal("S2F13 W <L [0]>", GemMessages.S2F13(null).ToSml());
        Assert.Equal("S2F13 W <L [2] <U2 1> <U2 2>>", GemMessages.S2F13([1, 2], new GemFormats { Vid = SecsFormat.U2 }).ToSml());
        Assert.Equal("S2F14 <L [2] <U1 5> <A \"x\">>", GemMessages.S2F14([SecsItem.U1(5), SecsItem.A("x")]).ToSml());
        Assert.Equal("S2F15 W <L [1] <L [2] <U4 7> <F4 1.5>>>", GemMessages.S2F15([(7, SecsItem.F4(1.5f))]).ToSml());
        Assert.Equal("S2F16 <B 0x00>", GemMessages.S2F16(0).ToSml());
        Assert.Equal("S2F17 W", GemMessages.S2F17().ToSml());
        Assert.Equal("S2F29 W <L [1] <U4 7>>", GemMessages.S2F29([7]).ToSml());
        Assert.Equal("S2F30 <L [1] <L [6] <U4 7> <A \"N\"> <U1 0> <U1 9> <U1 5> <A \"mm\">>>",
            GemMessages.S2F30([new GemConstantInfo(7, "N", SecsItem.U1(0), SecsItem.U1(9), SecsItem.U1(5), "mm")]).ToSml());
        Assert.Equal("S2F32 <B 0x01>", GemMessages.S2F32(1).ToSml());
        Assert.Equal("S2F33 W <L [2] <U4 1> <L [1] <L [2] <U4 1000> <L [2] <U4 2001> <U4 2002>>>>>",
            GemMessages.S2F33(1, [(1000, [2001, 2002])]).ToSml());
        Assert.Equal("S2F33 W <L [2] <U4 1> <L [0]>>", GemMessages.S2F33(1, []).ToSml());
        Assert.Equal("S2F34 <B 0x00>", GemMessages.S2F34(0).ToSml());
        Assert.Equal("S2F35 W <L [2] <U4 1> <L [1] <L [2] <U4 5001> <L [1] <U4 1000>>>>>", GemMessages.S2F35(1, [(5001, [1000])]).ToSml());
        Assert.Equal("S2F36 <B 0x04>", GemMessages.S2F36(4).ToSml());
        Assert.Equal("S2F37 W <L [2] <BOOLEAN F> <L [0]>>", GemMessages.S2F37(false, null).ToSml());
        Assert.Equal("S2F37 W <L [2] <BOOLEAN T> <L [1] <U4 5001>>>", GemMessages.S2F37(true, [5001]).ToSml());
        Assert.Equal("S2F38 <B 0x01>", GemMessages.S2F38(1).ToSml());
        Assert.Equal("S2F41 W <L [2] <A \"START\"> <L [1] <L [2] <A \"LOT\"> <A \"L1\">>>>",
            GemMessages.S2F41("START", [("LOT", SecsItem.A("L1"))]).ToSml());
        Assert.Equal("S2F41 W <L [2] <A \"STOP\"> <L [0]>>", GemMessages.S2F41("STOP").ToSml());
        Assert.Equal("S2F42 <L [2] <B 0x03> <L [1] <L [2] <A \"LOT\"> <B 0x01>>>>", GemMessages.S2F42(3, [("LOT", 1)]).ToSml());
        Assert.Equal("S2F42 <L [2] <B 0x00> <L [0]>>", GemMessages.S2F42(0).ToSml());
        Assert.Equal("S2F18 <A \"261021090807\">", GemMessages.S2F18(new DateTime(2026, 10, 21, 9, 8, 7), 0).ToSml());
        Assert.Equal("S2F31 W <A \"2026102109080712\">", GemMessages.S2F31(new DateTime(2026, 10, 21, 9, 8, 7, 120), 1).ToSml());
    }

    public static TheoryData<SecsFormat> IdFormats => [SecsFormat.U1, SecsFormat.U2, SecsFormat.U4, SecsFormat.U8,
        SecsFormat.I1, SecsFormat.I2, SecsFormat.I4, SecsFormat.I8, SecsFormat.ASCII];

    [Theory]
    [MemberData(nameof(IdFormats))]
    public void RoundTrip_AllIdFormats(SecsFormat format)
    {
        var fmt = new GemFormats { Vid = format, Ceid = format, Rptid = format, Dataid = format };
        GemId Expected(long n) => format == SecsFormat.ASCII ? new GemId(n.ToString()) : n;

        Assert.Equal(Ids(Expected(7), Expected(8)), GemMessages.ParseS2F13(GemMessages.S2F13([7, 8], fmt)));
        Assert.Equal(Ids(Expected(7), Expected(8)), GemMessages.ParseS2F29(GemMessages.S2F29([7, 8], fmt)));
        Assert.Equal(new[] { "<F4 25>", "<L [0]>" }, GemMessages.ParseS2F14(GemMessages.S2F14([SecsItem.F4(25), SecsItem.L()])).Select(v => v.ToSml()));

        var change = Assert.Single(GemMessages.ParseS2F15(GemMessages.S2F15([(7, SecsItem.U1(3))], fmt)));
        Assert.Equal((Expected(7), "<U1 3>"), (change.Ecid, change.Value.ToSml()));

        var info = Assert.Single(GemMessages.ParseS2F30(GemMessages.S2F30([new GemConstantInfo(7, "N", SecsItem.U1(0), SecsItem.U1(9), SecsItem.U1(5), "U")], fmt)));
        Assert.Equal((Expected(7), "N", "U"), (info.Id, info.Name, info.Units));
        Assert.Equal("<U1 9>", info.Max.ToSml());

        var s2f33 = GemMessages.ParseS2F33(GemMessages.S2F33(5, [(100, [21, 22]), (101, [])], fmt));
        Assert.Equal(GemId.FromItem(((GemId)5).ToItem(fmt.Dataid)).ToString(), GemId.FromItem(s2f33.DataId).ToString());
        Assert.Equal(((GemId)5).ToItem(fmt.Dataid).ToSml(), s2f33.DataId.ToSml());
        Assert.Equal(2, s2f33.Reports.Count);
        Assert.Equal(Expected(100), s2f33.Reports[0].Rptid);
        Assert.Equal(Ids(Expected(21), Expected(22)), s2f33.Reports[0].Vids);
        Assert.Empty(s2f33.Reports[1].Vids);

        var s2f35 = GemMessages.ParseS2F35(GemMessages.S2F35(5, [(51, [100, 101])], fmt));
        Assert.Equal(((GemId)5).ToItem(fmt.Dataid).ToSml(), s2f35.DataId.ToSml());
        var link = Assert.Single(s2f35.Links);
        Assert.Equal(Expected(51), link.Ceid);
        Assert.Equal(Ids(Expected(100), Expected(101)), link.Rptids);

        var s2f37 = GemMessages.ParseS2F37(GemMessages.S2F37(true, [51, 52], fmt));
        Assert.True(s2f37.Enable);
        Assert.Equal(Ids(Expected(51), Expected(52)), s2f37.Ceids);
        Assert.False(GemMessages.ParseS2F37(GemMessages.S2F37(false, null, fmt)).Enable);

        Assert.Equal((byte)2, GemMessages.ParseS2F16(GemMessages.S2F16(2)));
        Assert.Equal((byte)2, GemMessages.ParseS2F32(GemMessages.S2F32(2)));
        Assert.Equal((byte)2, GemMessages.ParseS2F34(GemMessages.S2F34(2)));
        Assert.Equal((byte)2, GemMessages.ParseS2F36(GemMessages.S2F36(2)));
        Assert.Equal((byte)2, GemMessages.ParseS2F38(GemMessages.S2F38(2)));

        var cmd = GemMessages.ParseS2F41(GemMessages.S2F41("START", [("LOT", SecsItem.A("L1")), ("N", SecsItem.U2(4))]));
        Assert.Equal("START", cmd.Command);
        Assert.Equal(new[] { "LOT=<A \"L1\">", "N=<U2 4>" }, cmd.Parameters.Select(p => $"{p.Name}={p.Value.ToSml()}"));
        var result = GemMessages.ParseS2F42(GemMessages.S2F42(3, [("LOT", 1), ("N", 2)]));
        Assert.Equal((byte)3, result.Hcack);
        Assert.Equal(new[] { ("LOT", (byte)1), ("N", (byte)2) }, result.ParameterAcks);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Time_RoundTrip(int timeFormat)
    {
        var t = new DateTime(2026, 10, 21, 9, 8, 7, 120);
        var expected = timeFormat == 0 ? new DateTime(2026, 10, 21, 9, 8, 7) : t;   // 형식 0은 초 단위, 1·2는 1/100초
        Assert.Equal(expected, GemMessages.ParseS2F18(GemMessages.S2F18(t, timeFormat)));
        Assert.Equal(expected, GemMessages.ParseS2F31(GemMessages.S2F31(t, timeFormat)));
    }

    [Fact]
    public void Time_UtcIsConvertedToLocal()
    {
        var utc = new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(utc.ToLocalTime(), GemMessages.ParseS2F18(GemMessages.S2F18(utc, 0)));
    }

    [Fact]
    public void Lenient()
    {
        Assert.False(GemMessages.ParseS2F37(M("S2F37 W <L <U1 0> <L>>")).Enable);
        Assert.True(GemMessages.ParseS2F37(M("S2F37 W <L <U1 1> <L>>")).Enable);
        Assert.False(GemMessages.ParseS2F37(M("S2F37 W <L <B 0x00> <L>>")).Enable);
        Assert.True(GemMessages.ParseS2F37(M("S2F37 W <L <B 0x01> <L <U4 5>>>")).Enable);
        Assert.Equal(Ids(5, 6), GemMessages.ParseS2F37(M("S2F37 W <L <BOOLEAN T> <U4 5 6>>")).Ceids);   // 정수 배열 목록도 받음

        Assert.Empty(GemMessages.ParseS2F13(M("S2F13 W")));
        Assert.Equal(Ids(1, "X"), GemMessages.ParseS2F29(M("S2F29 W <L <I2 1> <A \"X\">>")));
        Assert.Empty(GemMessages.ParseS2F14(M("S2F14")));
        Assert.Empty(GemMessages.ParseS2F15(M("S2F15 W")));
        Assert.Empty(GemMessages.ParseS2F30(M("S2F30")));

        Assert.Equal((byte)2, GemMessages.ParseS2F34(M("S2F34 <U1 2>")));
        Assert.Equal((byte)2, GemMessages.ParseS2F38(M("S2F38 <I4 2>")));

        var cmd = GemMessages.ParseS2F41(M("S2F41 W <L <U4 5> <L <L <U4 7> <A \"v\">>>>"));
        Assert.Equal("5", cmd.Command);
        Assert.Equal("7", Assert.Single(cmd.Parameters).Name);
        var result = GemMessages.ParseS2F42(M("S2F42 <L <U1 0> <L <L <U4 7> <U1 1>>>>"));
        Assert.Equal(("7", (byte)1), Assert.Single(result.ParameterAcks));

        var s2f33 = GemMessages.ParseS2F33(M("S2F33 W <L <A \"D\"> <L <L <A \"R\"> <U4 1 2>>>>"));   // DataId 그대로, VID는 정수 배열
        Assert.Equal("<A \"D\">", s2f33.DataId.ToSml());
        Assert.Equal((GemId)"R", s2f33.Reports[0].Rptid);
        Assert.Equal(Ids(1, 2), s2f33.Reports[0].Vids);
        Assert.Empty(GemMessages.ParseS2F33(M("S2F33 W <L <U4 1> <L>>")).Reports);
    }

    [Fact]
    public void Errors()
    {
        var wrong = Assert.Throws<ArgumentException>(() => GemMessages.ParseS2F38(GemMessages.S2F36(0)));
        Assert.Equal("message", wrong.ParamName);
        Assert.Equal("message", Assert.Throws<ArgumentNullException>(() => GemMessages.ParseS2F38(null!)).ParamName);

        Assert.Throws<FormatException>(() => GemMessages.ParseS2F31(M("S2F31 W <U4 1>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F31(M("S2F31 W <A \"nope\">")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F31(M("S2F31 W <A \"2026133199999\">")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F18(M("S2F18")));
        var ex42 = Assert.Throws<FormatException>(() => GemMessages.ParseS2F42(M("S2F42 <L <B 0>>")));
        Assert.StartsWith("S2F42 응답 본문이 기대(", ex42.Message);
        Assert.Contains("<B 0x00>", ex42.Message);
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F42(M("S2F42 <L <B 0> <L <L <A \"x\">>>>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F42(M("S2F42 <L <B 0> <L <L <F4 1> <B 1>>>>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F42(M("S2F42 <L <B 0> <L <L <A \"x\"> <A \"y\">>>>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F42(M("S2F42")));

        Assert.Throws<FormatException>(() => GemMessages.ParseS2F37(M("S2F37 W <L <A \"T\"> <L>>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F37(M("S2F37 W <L <U1 1 2> <L>>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F37(M("S2F37 W <L <U2 1> <L>>")));
        var odd = Assert.Throws<FormatException>(() => GemMessages.ParseS2F37(M("S2F37 W")));
        Assert.StartsWith("S2F37 본문이 기대(", odd.Message);
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F33(M("S2F33 W <L <U4 1> <U4 2>>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F33(M("S2F33 W <L <U4 1> <L <U4 2>>>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F35(M("S2F35 W <L <U4 1> <L <L <U4 5>>>>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F15(M("S2F15 W <L <L <U4 1>>>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F15(M("S2F15 W <L <L <F4 1> <U1 1>>>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F30(M("S2F30 <L <L <U4 1> <A \"N\">>>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F14(M("S2F14 <U4 1>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F13(M("S2F13 W <L <F4 1>>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F41(M("S2F41 W <L <A \"X\">>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F41(M("S2F41 W <L <F4 1> <L>>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F41(M("S2F41 W <L <A \"X\"> <L <L <A \"p\">>>>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS2F16(M("S2F16 <U4 300>")));

        Assert.Throws<ArgumentOutOfRangeException>(() => GemMessages.S2F18(DateTime.Now, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => GemMessages.S2F31(DateTime.Now, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => GemMessages.S2F13([300], new GemFormats { Vid = SecsFormat.U1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => GemMessages.S2F33(1, [(300, [])], new GemFormats { Rptid = SecsFormat.U1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => GemMessages.S2F33(300, [], new GemFormats { Dataid = SecsFormat.U1 }));
        Assert.Throws<ArgumentException>(() => GemMessages.S2F37(true, ["X"]));
        Assert.Throws<ArgumentException>(() => GemMessages.S2F41("é€"));
        Assert.Throws<ArgumentException>(() => GemMessages.S2F41("A", [("€", SecsItem.U1(1))]));
        Assert.Throws<ArgumentException>(() => GemMessages.S2F42(0, [("€", 1)]));
        Assert.Throws<ArgumentNullException>(() => GemMessages.S2F41(null!));
        Assert.Throws<ArgumentNullException>(() => GemMessages.S2F41("A", [("p", null!)]));
        Assert.Throws<ArgumentNullException>(() => GemMessages.S2F14(null!));
        Assert.Throws<ArgumentNullException>(() => GemMessages.S2F15(null!));
        Assert.Throws<ArgumentNullException>(() => GemMessages.S2F30(null!));
        Assert.Throws<ArgumentNullException>(() => GemMessages.S2F33(1, null!));
        Assert.Throws<ArgumentNullException>(() => GemMessages.S2F35(1, null!));
    }

    [Fact]
    public void S2F42_EchoOverload_KeepsReceivedNameItem()
    {
        Assert.Equal("S2F42 <L [2] <B 0x03> <L [1] <L [2] <U4 7> <B 0x01>>>>",
            GemMessages.S2F42(3, [(SecsItem.U4(7), (byte)1)]).ToSml());
    }

    [Fact]
    public void Internal_ParseS2F41Raw_KeepsNameItem_AndS2F40()
    {
        var (command, ps) = GemMessages.ParseS2F41Raw(M("S2F41 W <L <A \"GO\"> <L <L <I2 7> <A \"v\">> <L <A \"n\"> <U1 1>>>>"));
        Assert.Equal("GO", command);
        Assert.Equal(new[] { "<I2 7>|7|<A \"v\">", "<A \"n\">|n|<U1 1>" }, ps.Select(p => $"{p.NameItem.ToSml()}|{p.Name}|{p.Value.ToSml()}"));
        Assert.Equal("S2F40 <B 0x00>", GemMessages.S2F40(0).ToSml());
        Assert.Throws<ArgumentException>(() => GemMessages.ParseS2F41Raw(M("S2F42 <L>")));
    }
}
