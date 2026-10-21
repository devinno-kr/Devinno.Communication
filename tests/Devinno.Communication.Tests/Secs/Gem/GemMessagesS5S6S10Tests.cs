using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// GemMessages S5·S6·S10: 골든 SML, 왕복, 받을 때 관대, S6F11 표 열쇠, 오류 규칙, internal(3층용) 모양
public class GemMessagesS5S6S10Tests
{
    static SecsMessage M(string sml) => SecsMessage.ParseSml(sml);
    static void AssertIds(GemId[] expected, IReadOnlyList<GemId> actual) => Assert.Equal(expected, actual.ToArray());

    [Fact]
    public void Build_Golden()
    {
        Assert.Equal("S10F1 W <L [2] <B 0x01> <A \"Operator call\">>", GemMessages.S10F1(1, "Operator call").ToSml());
        Assert.Equal("S10F1 <L [2] <B 0x01> <A \"x\">>", GemMessages.S10F1(1, "x", replyExpected: false).ToSml());
        Assert.Equal("S10F2 <B 0x00>", GemMessages.S10F2(0).ToSml());
        Assert.Equal("S10F3 W <L [2] <B 0x02> <A \"hi\">>", GemMessages.S10F3(2, "hi").ToSml());
        Assert.Equal("S10F4 <B 0x02>", GemMessages.S10F4(2).ToSml());
        Assert.Equal("S5F1 W <L [3] <B 0x82> <U4 1> <A \"Over temperature\">>", GemMessages.S5F1(0x82, 1, "Over temperature").ToSml());
        Assert.Equal("S5F1 <L [3] <B 0x82> <U2 1> <A \"t\">>", GemMessages.S5F1(0x82, 1, "t", new GemFormats { Alid = SecsFormat.U2 }, false).ToSml());
        Assert.Equal("S5F2 <B 0x00>", GemMessages.S5F2(0).ToSml());
        Assert.Equal("S5F3 W <L [2] <B 0x80> <U4 7>>", GemMessages.S5F3(true, 7).ToSml());
        Assert.Equal("S5F3 W <L [2] <B 0x00> <U4>>", GemMessages.S5F3(false, null).ToSml());
        Assert.Equal("S5F4 <B 0x01>", GemMessages.S5F4(1).ToSml());
        Assert.Equal("S5F5 W <U4 1 2>", GemMessages.S5F5([1, 2]).ToSml());
        Assert.Equal("S5F5 W <U4>", GemMessages.S5F5(null).ToSml());
        var a = new GemFormats { Alid = SecsFormat.ASCII };
        Assert.Equal("S5F5 W <A \"\">", GemMessages.S5F5([], a).ToSml());
        Assert.Equal("S5F5 W <A \"7\">", GemMessages.S5F5([7], a).ToSml());
        Assert.Equal("S5F6 <L [1] <L [3] <B 0x86> <U4 2> <A \"Door open\">>>", GemMessages.S5F6([new GemAlarmInfo(2, 0x86, "Door open")]).ToSml());
        Assert.Equal("S5F7 W", GemMessages.S5F7().ToSml());
        Assert.Equal("S5F8 <L [0]>", GemMessages.S5F8([]).ToSml());
        Assert.Equal("S6F12 <B 0x00>", GemMessages.S6F12(0).ToSml());
        Assert.Equal("S6F15 W <U4 5001>", GemMessages.S6F15(5001).ToSml());
        Assert.Equal("S6F19 W <U4 1000>", GemMessages.S6F19(1000).ToSml());
        Assert.Equal("S6F20 <L [2] <F4 25> <A \"LOT-1\">>", GemMessages.S6F20([SecsItem.F4(25), SecsItem.A("LOT-1")]).ToSml());
    }

    [Fact]
    public void S6F11_Golden_SameAsEquipmentBody()
    {
        // GemReportTests.TriggerEvent_GoldenBody_DataValuesOverride_DataIdCounts와 같은 입력
        var m = GemMessages.S6F11(1, 5001, [(1000, [SecsItem.F4(25), SecsItem.A("LOT-1")])]);
        Assert.Equal("S6F11 W <L [3] <U4 1> <U4 5001> <L [1] <L [2] <U4 1000> <L [2] <F4 25> <A \"LOT-1\">>>>>", m.ToSml());
        Assert.Equal("S6F11 <L [3] <U4 1> <U4 5001> <L [0]>>", GemMessages.S6F11(1, 5001, [], null, false).ToSml());
        Assert.Equal("S6F16 <L [3] <U4 1> <U4 5001> <L [1] <L [2] <U4 1000> <L [0]>>>>", GemMessages.S6F16(1, 5001, [(1000, [])]).ToSml());
        var body = GemMessages.S6F11Body(SecsItem.U4(1), SecsItem.U4(5001), [(SecsItem.U4(1000), [SecsItem.F4(25), SecsItem.A("LOT-1")])]);
        Assert.Equal(m.ToSml(), new SecsMessage(6, 11, true, body).ToSml());
    }

    [Fact]
    public void S5F6Entries_UnknownAlid_EchoesBytes()
    {
        // 기존 GemAlarmTests 골든: 모르는 ALID는 ALCD 길이 0 + 받은 ALID 아이템
        Assert.Equal("S5F6 <L [2] <L [3] <B 0x86> <U4 2> <A \"Door open\">> <L [3] <B> <U4 9> <A \"\">>>",
            GemMessages.S5F6Entries([(SecsItem.U4(2), 0x86, "Door open"), (SecsItem.U4(9), null, "")]).ToSml());
        Assert.Equal("S5F6 <L [1] <L [3] <B> <I2 77> <A \"\">>>", GemMessages.S5F6Entries([(SecsItem.I2(77), null, "")]).ToSml());
        Assert.Equal("S5F6 <L [0]>", GemMessages.S5F6Entries([]).ToSml());   // 공개 S5F6과 모호하지 않다
    }

    public static TheoryData<SecsFormat> IdFormats => [SecsFormat.U1, SecsFormat.U2, SecsFormat.U4, SecsFormat.U8,
        SecsFormat.I1, SecsFormat.I2, SecsFormat.I4, SecsFormat.I8, SecsFormat.ASCII];

    [Theory]
    [MemberData(nameof(IdFormats))]
    public void RoundTrip_AllIdFormats(SecsFormat format)
    {
        var fmt = new GemFormats { Vid = format, Ceid = format, Rptid = format, Alid = format, Dataid = format };
        GemId Expected(long n) => format == SecsFormat.ASCII ? new GemId(n.ToString()) : n;

        var alarm = GemMessages.ParseS5F1(GemMessages.S5F1(0x83, 7, "Hot", fmt));
        Assert.Equal((Expected(7), (byte)0x83, "Hot", true, 3), (alarm.Alid, alarm.Alcd, alarm.Text, alarm.IsSet, alarm.Category));

        Assert.Equal((true, (GemId?)Expected(7)), GemMessages.ParseS5F3(GemMessages.S5F3(true, 7, fmt)));
        Assert.Equal((false, (GemId?)null), GemMessages.ParseS5F3(GemMessages.S5F3(false, null, fmt)));

        var ids = GemMessages.ParseS5F5(GemMessages.S5F5(format == SecsFormat.ASCII ? [7] : [7, 8], fmt));
        Assert.Equal(format == SecsFormat.ASCII ? [Expected(7)] : new[] { Expected(7), Expected(8) }, ids);
        Assert.Empty(GemMessages.ParseS5F5(GemMessages.S5F5(null, fmt)));

        var list = GemMessages.ParseS5F6(GemMessages.S5F6([new GemAlarmInfo(7, 0x81, "a"), new GemAlarmInfo(8, 0, "")], fmt));
        Assert.Equal(new[] { (Expected(7), (byte)0x81, "a"), (Expected(8), (byte)0, "") }, list.Select(x => (x.Alid, x.Alcd, x.Text)));
        Assert.Equal(2, GemMessages.ParseS5F8(GemMessages.S5F8([new GemAlarmInfo(7, 0x81, "a"), new GemAlarmInfo(8, 0, "")], fmt)).Count);

        var report = GemMessages.ParseS6F11(GemMessages.S6F11(3, 50, [(100, [SecsItem.U1(1), SecsItem.A("z")])], fmt));
        Assert.Equal((Expected(50), Expected(100), 2), (report.Ceid, report.Reports[0].Rptid, report.Reports[0].Values.Count));
        Assert.Equal(Expected(3), GemId.FromItem(report.DataId));
        Assert.All(report.Reports[0].Values, v => Assert.Null(v.Vid));
        Assert.Equal(Expected(50), GemMessages.ParseS6F15(GemMessages.S6F15(50, fmt)));
        Assert.Equal(Expected(100), GemMessages.ParseS6F19(GemMessages.S6F19(100, fmt)));
        Assert.Equal(Expected(50), GemMessages.ParseS6F16(GemMessages.S6F16(3, 50, [], fmt)).Ceid);
    }

    [Fact]
    public void RoundTrip_Terminal_Acks_Values()
    {
        Assert.Equal(((byte)1, "Operator call"), GemMessages.ParseS10F1(GemMessages.S10F1(1, "Operator call")));
        Assert.Equal(((byte)5, "hi"), GemMessages.ParseS10F3(GemMessages.S10F3(5, "hi")));
        Assert.Equal(((byte)5, "hi"), GemMessages.ParseS10F3(M("S10F3 W <L <U2 5> <A \"hi\">>")));   // TID는 숫자 포맷 무엇이든
        Assert.Equal(2, GemMessages.ParseS10F2(GemMessages.S10F2(2)));
        Assert.Equal(1, GemMessages.ParseS10F4(M("S10F4 <U2 1>")));
        Assert.Equal(0, GemMessages.ParseS5F2(GemMessages.S5F2(0)));
        Assert.Equal(1, GemMessages.ParseS5F4(GemMessages.S5F4(1)));
        Assert.Equal(0, GemMessages.ParseS6F12(GemMessages.S6F12(0)));
        Assert.Equal(["<F4 25>", "<A \"x\">"], GemMessages.ParseS6F20(GemMessages.S6F20([SecsItem.F4(25), SecsItem.A("x")])).Select(v => v.ToSml()));
        Assert.Empty(GemMessages.ParseS6F20(M("S6F20")));

        var (tid, lines) = GemMessages.ParseS10F5(M("S10F5 W <L <B 3> <L <A \"a\"> <A \"b\">>>"));
        Assert.Equal((byte)3, tid);
        Assert.Equal(["a", "b"], lines);
        Assert.Equal("S10F6 <B 0x02>", GemMessages.S10F6(2).ToSml());
        Assert.Throws<FormatException>(() => GemMessages.ParseS10F5(M("S10F5 W <L <B 3> <L <U1 1>>>")));
    }

    [Fact]
    public void ParseS5F5_AcceptsThreeShapes_AndEmpty()
    {
        AssertIds([1, 2], GemMessages.ParseS5F5(M("S5F5 W <U4 1 2>")));
        AssertIds([new GemId("X")], GemMessages.ParseS5F5(M("S5F5 W <A \"X\">")));
        AssertIds([1], GemMessages.ParseS5F5(M("S5F5 W <L <U4 1>>")));
        Assert.Empty(GemMessages.ParseS5F5(M("S5F5 W <U4>")));
        Assert.Empty(GemMessages.ParseS5F5(M("S5F5 W <A \"\">")));
        Assert.Empty(GemMessages.ParseS5F5(M("S5F5 W <L>")));
        Assert.Empty(GemMessages.ParseS5F5(M("S5F5 W")));
    }

    [Fact]
    public void S5F5_AFormat_TwoIds_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => GemMessages.S5F5([1, 2], new GemFormats { Alid = SecsFormat.ASCII }));
        Assert.Equal("alids", ex.ParamName);
    }

    [Fact]
    public void ParseS5F3_Lenient()
    {
        Assert.Equal((true, (GemId?)7), GemMessages.ParseS5F3(M("S5F3 W <L <B 0x80> <U4 7>>")));
        Assert.Equal((true, (GemId?)7), GemMessages.ParseS5F3(M("S5F3 W <L <U1 128> <U2 7>>")));
        Assert.Equal((false, (GemId?)null), GemMessages.ParseS5F3(M("S5F3 W <L <B 0x01> <U4>>")));        // 0x80 비트만 본다
        Assert.Equal((true, (GemId?)null), GemMessages.ParseS5F3(M("S5F3 W <L <BOOLEAN T> <L>>")));
        Assert.Equal((true, (GemId?)new GemId("A1")), GemMessages.ParseS5F3(M("S5F3 W <L <B 0x80> <A \"A1\">>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS5F3(M("S5F3 W <L <U2 128> <U4 7>>")));   // U1·B·BOOLEAN만
        Assert.Throws<FormatException>(() => GemMessages.ParseS5F3(M("S5F3 W <L <B 0x80> <U4 1 2>>")));   // 둘 이상
        Assert.Throws<FormatException>(() => GemMessages.ParseS5F3(M("S5F3 W")));
    }

    [Fact]
    public void ParseS5F6_ZeroLengthAlcd_IsZero_AndHeaderOnlyIsEmpty()
    {
        var l = GemMessages.ParseS5F6(M("S5F6 <L <L <B> <U4 9> <A \"\">> <L <U1> <U4 8> <A \"\">>>"));
        Assert.Equal(new[] { (byte)0, (byte)0 }, l.Select(a => a.Alcd));
        Assert.Empty(GemMessages.ParseS5F6(M("S5F6")));
        Assert.Empty(GemMessages.ParseS5F8(M("S5F8 <L>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS5F6(M("S5F6 <L <L <A \"x\"> <U4 9> <A \"\">>>")));   // ALCD가 A
        Assert.Throws<FormatException>(() => GemMessages.ParseS5F6(M("S5F6 <L <L <B 1> <U4 9> <U1 1>>>")));        // ALTX가 A 아님
    }

    [Fact]
    public void ParseS6F11_TableKey_FollowsRptidFormat()
    {
        IReadOnlyDictionary<GemId, IReadOnlyList<GemId>> Table(GemId key) => new Dictionary<GemId, IReadOnlyList<GemId>> { [key] = new GemId[] { 2001 } };
        var asA = new GemFormats { Rptid = SecsFormat.ASCII };

        // Rptid = A: 표에 숫자 1000(정규화해 "1000"), 받은 A "1000" → 맞음
        var r = GemMessages.ParseS6F11(M("S6F11 W <L <U4 1> <U4 5001> <L <L <A \"1000\"> <L <F4 25>>>>>"), Table(GemItems.Key(1000, SecsFormat.ASCII)), asA);
        Assert.Equal((GemId?)2001, r.Reports[0].Values[0].Vid);
        Assert.Equal(SecsItem.F4(25).ToSml(), r.GetValue(2001)!.ToSml());
        // 표 열쇠가 숫자 1000이어도 A "1000"과 맞는다(10진 정수 문자열일 때). "01000"은 맞지 않는다
        var loose = GemMessages.ParseS6F11(M("S6F11 W <L <U4 1> <U4 5001> <L <L <A \"1000\"> <L <F4 25>>>>>"), Table(1000), asA);
        Assert.Equal((GemId?)2001, loose.Reports[0].Values[0].Vid);
        var padded = GemMessages.ParseS6F11(M("S6F11 W <L <U4 1> <U4 5001> <L <L <A \"01000\"> <L <F4 25>>>>>"), Table(1000), asA);
        Assert.Null(padded.Reports[0].Values[0].Vid);

        // Rptid = U4(기본): 표 {1000:[2001]}, 받은 U2 1000 → 숫자끼리 맞음, 받은 A "1000" → 맞지 않음
        var num = GemMessages.ParseS6F11(M("S6F11 W <L <U4 1> <U4 5001> <L <L <U2 1000> <L <F4 25>>>>>"), Table(1000));
        Assert.Equal((GemId?)2001, num.Reports[0].Values[0].Vid);
        var text = GemMessages.ParseS6F11(M("S6F11 W <L <U4 1> <U4 5001> <L <L <A \"1000\"> <L <F4 25>>>>>"), Table(1000));
        Assert.Null(text.Reports[0].Values[0].Vid);

        // 개수가 다르면 Vid = null(그 리포트만), 표가 없어도 null
        var two = M("S6F11 W <L <U4 1> <U4 5001> <L <L <U4 1000> <L <F4 25> <F4 26>>> <L <U4 1001> <L <F4 1>>>>>");
        var tbl = new Dictionary<GemId, IReadOnlyList<GemId>> { [1000] = new GemId[] { 2001 }, [1001] = new GemId[] { 2002 } };
        var mismatch = GemMessages.ParseS6F11(two, tbl);
        Assert.All(mismatch.Reports[0].Values, v => Assert.Null(v.Vid));
        Assert.Equal((GemId?)2002, mismatch.Reports[1].Values[0].Vid);
        Assert.All(GemMessages.ParseS6F11(two).Reports.SelectMany(x => x.Values), v => Assert.Null(v.Vid));
        // S6F16도 같은 규칙
        var f16 = GemMessages.ParseS6F16(M("S6F16 <L <U4 1> <U4 5001> <L <L <U4 1001> <L <F4 1>>>>>"), tbl);
        Assert.Equal((GemId?)2002, f16.Reports[0].Values[0].Vid);
    }

    [Fact]
    public void ParseS6F11_KeepsDataIdItem_AndCeidFormat()
    {
        var r = GemMessages.ParseS6F11(M("S6F11 W <L <A \"D1\"> <A \"5001\"> <L>>"));
        Assert.Equal("<A \"D1\">", r.DataId.ToSml());
        Assert.Equal(new GemId("5001"), r.Ceid);
        Assert.Empty(r.Reports);
    }

    [Fact]
    public void Errors()
    {
        Assert.Equal("message", Assert.Throws<ArgumentNullException>(() => GemMessages.ParseS6F11(null!)).ParamName);
        var wrong = Assert.Throws<ArgumentException>(() => GemMessages.ParseS5F1(GemMessages.S5F2(0)));
        Assert.Equal("message", wrong.ParamName);
        Assert.Throws<ArgumentException>(() => GemMessages.ParseS10F1(GemMessages.S10F3(1, "x")));

        var ex = Assert.Throws<FormatException>(() => GemMessages.ParseS6F11(M("S6F11 W <L <U4 1>>")));
        Assert.Contains("S6F11", ex.Message);
        Assert.Contains("<U4 1>", ex.Message);
        var ex16 = Assert.Throws<FormatException>(() => GemMessages.ParseS6F16(M("S6F16 <U4 1>")));
        Assert.Contains("응답 ", ex16.Message);
        Assert.Throws<FormatException>(() => GemMessages.ParseS6F11(M("S6F11 W")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS6F11(M("S6F11 W <L <U4 1> <U4 5001> <L <U4 1>>>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS6F11(M("S6F11 W <L <U4 1> <F4 1> <L>>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS6F15(M("S6F15 W")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS6F19(M("S6F19 W <F4 1>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS6F20(M("S6F20 <U4 1>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS5F1(M("S5F1 W <L <B 1> <U4 1>>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS5F1(M("S5F1 W")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS10F1(M("S10F1 W <L <B 1> <U4 1>>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS10F3(M("S10F3 W <L <A \"x\"> <A \"y\">>")));
        Assert.Throws<FormatException>(() => GemMessages.ParseS10F2(M("S10F2 <U4 300>")));

        Assert.Throws<ArgumentNullException>(() => GemMessages.S10F1(1, null!));
        Assert.Throws<ArgumentException>(() => GemMessages.S10F3(1, "한글"));
        Assert.Throws<ArgumentException>(() => GemMessages.S5F1(0x80, 1, "한글"));
        Assert.Throws<ArgumentOutOfRangeException>(() => GemMessages.S5F1(0x80, 300, "x", new GemFormats { Alid = SecsFormat.U1 }));
        Assert.Throws<ArgumentException>(() => GemMessages.S5F3(true, new GemId("X")));
        Assert.Throws<ArgumentNullException>(() => GemMessages.S6F11(1, 2, null!));
        Assert.Throws<ArgumentNullException>(() => GemMessages.S5F6(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => GemMessages.S6F15(70000, new GemFormats { Ceid = SecsFormat.U2 }));
    }
}
