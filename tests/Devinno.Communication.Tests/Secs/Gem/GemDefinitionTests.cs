using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 정의 클래스 7종의 생성자 검사, 등록 때 겹침 검사, Start 때 만드는 정의 표(GemCatalog)와 표준 변수·이벤트
public class GemDefinitionTests
{
    static GemCatalog Build(GemDefinitionSet d, GemStandardIds? ids = null, SecsFormat vid = SecsFormat.U4, SecsFormat ceid = SecsFormat.U4, SecsFormat alid = SecsFormat.U4)
        => GemCatalog.Build(d, ids ?? new GemStandardIds(), vid, ceid, SecsFormat.U4, alid, SecsFormat.U4);

    #region 생성자
    [Fact]
    public void StatusVariable_InitialValue_DefaultsToEmptyOfFormat_AndMustMatchFormat()
    {
        var sv = new GemStatusVariable(2001, "Temperature", SecsFormat.F4, "C");
        Assert.Equal(("Temperature", SecsFormat.F4, "C", "<F4>"), (sv.Name, sv.Format, sv.Units, sv.InitialValue.ToSml()));
        Assert.Equal(SecsItem.F4(25), new GemStatusVariable(2001, "T", SecsFormat.F4, initialValue: SecsItem.F4(25)).InitialValue);
        Assert.Equal("initialValue", Assert.Throws<ArgumentException>(() => new GemStatusVariable(1, "T", SecsFormat.F4, initialValue: SecsItem.F8(1))).ParamName);
        Assert.Equal("name", Assert.Throws<ArgumentException>(() => new GemStatusVariable(1, "온도", SecsFormat.F4)).ParamName);
        Assert.Throws<ArgumentNullException>(() => new GemStatusVariable(1, null!, SecsFormat.F4));
        Assert.Equal(SecsItem.L(SecsItem.U1(1)), new GemDataVariable(3001, "Pair", SecsFormat.List, initialValue: SecsItem.L(SecsItem.U1(1))).InitialValue);
        Assert.Throws<ArgumentException>(() => new GemDataVariable(1, "X", SecsFormat.List, initialValue: SecsItem.U1(1)));
    }

    [Fact]
    public void EquipmentConstant_MinMaxRules()
    {
        var ec = new GemEquipmentConstant(4001, "MaxTemp", SecsItem.F4(80), SecsItem.F4(0), SecsItem.F4(200), "C");
        Assert.Equal((SecsFormat.F4, "C"), (ec.Format, ec.Units));
        Assert.Throws<ArgumentException>(() => new GemEquipmentConstant(1, "X", SecsItem.F4(80), SecsItem.F8(0)));                  // 포맷 다름
        Assert.Throws<ArgumentException>(() => new GemEquipmentConstant(1, "X", SecsItem.A("a"), SecsItem.A("a")));                // 숫자 아님
        Assert.Throws<ArgumentException>(() => new GemEquipmentConstant(1, "X", SecsItem.U1(1), SecsItem.U1(0, 1)));               // 값 둘
        Assert.Throws<ArgumentOutOfRangeException>(() => new GemEquipmentConstant(1, "X", SecsItem.U1(5), SecsItem.U1(6), SecsItem.U1(4)));   // min > max
        Assert.Throws<ArgumentOutOfRangeException>(() => new GemEquipmentConstant(1, "X", SecsItem.U1(9), null, SecsItem.U1(8)));  // 기본값 밖
        Assert.Throws<ArgumentNullException>(() => new GemEquipmentConstant(1, "X", null!));
        Assert.Equal(SecsFormat.List, new GemEquipmentConstant(1, "L", SecsItem.L()).Format);
    }

    [Fact]
    public void Alarm_TextCategoryAndCeids()
    {
        var a = new GemAlarm(1, "Over temperature", 2, 9001, 9002);
        Assert.Equal((2, (GemId)9001, (GemId)9002), (a.Category, a.SetCeid, a.ClearCeid));
        Assert.Throws<ArgumentException>(() => new GemAlarm(1, "", 2, 1, 2));
        Assert.Throws<ArgumentException>(() => new GemAlarm(1, new string('x', 121), 2, 1, 2));
        new GemAlarm(1, new string('x', 120), 8, 1, 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => new GemAlarm(1, "t", 0, 1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GemAlarm(1, "t", 9, 1, 2));
        Assert.Throws<ArgumentException>(() => new GemAlarm(1, "t", 1, 5, 5));
    }

    [Fact]
    public void RemoteCommand_NameAndParameters()
    {
        var c = new GemRemoteCommand("START", [new GemCommandParameter("LOTID", SecsFormat.ASCII), new GemCommandParameter("QTY")]);
        Assert.Equal(2, c.Parameters.Count);
        Assert.Null(c.Parameters[1].Format);
        Assert.Throws<ArgumentException>(() => new GemRemoteCommand(""));
        Assert.Throws<ArgumentException>(() => new GemRemoteCommand(new string('C', 41)));
        Assert.Throws<ArgumentException>(() => new GemRemoteCommand("X", [new GemCommandParameter("A"), new GemCommandParameter("A")]));
        Assert.Throws<ArgumentException>(() => new GemCommandParameter(""));
        Assert.Empty(new GemCollectionEvent(5001, "Started").DataVariables);
    }
    #endregion

    #region 등록
    [Fact]
    public void DefinitionSet_SameIdTwice_IsArgumentException_VidSpaceShared()
    {
        var d = new GemDefinitionSet();
        d.Add(new GemStatusVariable(2001, "A", SecsFormat.U4));
        Assert.Throws<ArgumentException>(() => d.Add(new GemDataVariable(2001, "B", SecsFormat.U4)));
        Assert.Throws<ArgumentException>(() => d.Add(new GemEquipmentConstant(2001, "C", SecsItem.U4(1))));
        d.Add(new GemCollectionEvent(2001, "CE can reuse number"));                    // CEID 공간은 따로
        Assert.Throws<ArgumentException>(() => d.Add(new GemCollectionEvent(2001, "again")));
        d.Add(new GemAlarm(1, "a", 1, 9001, 9002));
        Assert.Throws<ArgumentException>(() => d.Add(new GemAlarm(1, "b", 1, 9003, 9004)));
        d.Add(new GemRemoteCommand("START"));
        Assert.Throws<ArgumentException>(() => d.Add(new GemRemoteCommand("START")));
        Assert.Throws<ArgumentNullException>(() => d.Add((GemStatusVariable)null!));
    }

    [Fact]
    public void RegisterMessage_Rules()
    {
        var d = new GemDefinitionSet();
        d.AddMessage(7, 1);
        d.AddMessage(1, 65);                                                          // GEM이 처리하지 않는 S1 메시지는 됨
        Assert.Throws<ArgumentException>(() => d.AddMessage(7, 1));                   // 두 번
        Assert.Throws<ArgumentException>(() => d.AddMessage(7, 2));                   // 짝수
        Assert.Throws<ArgumentException>(() => d.AddMessage(1, 3));                   // GEM 메시지
        Assert.Throws<ArgumentException>(() => d.AddMessage(2, 41));
        Assert.Throws<ArgumentException>(() => d.AddMessage(9, 1));                   // S9
        Assert.Throws<ArgumentOutOfRangeException>(() => d.AddMessage(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.AddMessage(128, 1));
        Assert.True(GemCatalog.IsGemMessage(6, 15));
        Assert.False(GemCatalog.IsGemMessage(6, 11));                                 // 장비가 보내는 쪽
    }
    #endregion

    #region 정의 표
    [Fact]
    public void Catalog_DefaultStandardIds_AndOrder()
    {
        var d = new GemDefinitionSet();
        d.Add(new GemStatusVariable(2001, "Temperature", SecsFormat.F4));
        d.Add(new GemDataVariable(3001, "LotID", SecsFormat.ASCII));
        d.Add(new GemEquipmentConstant(4001, "MaxTemp", SecsItem.F4(80)));
        d.Add(new GemCollectionEvent(5001, "ProcessStarted", [2001, 3001]));
        d.Add(new GemAlarm(1, "Over temperature", 2, 9001, 9002));
        var c = Build(d);
        Assert.Equal(new GemId[] { 1001, 1002, 1003, 1004, 1005, 1006, 1007, 1008, 2001 }, c.StatusVars.Select(v => v.Key));
        Assert.Equal(new GemId[] { 1201, 3001 }, c.DataVars.Select(v => v.Key));
        Assert.Equal(new GemId[] { 1101, 1102, 4001 }, c.Constants.Select(v => v.Key));
        Assert.Equal(new GemId[] { 1301, 1302, 1303, 1304, 1305, 1306, 5001, 9001, 9002 }, c.EventList.Select(e => e.Key));
        Assert.Equal("Alarm 1 Set", c.Events[9001].Name);
        Assert.Equal([(GemId)1201], c.Events[9002].VidKeys);
        Assert.Equal([(GemId)1002, 1001], c.Events[1302].VidKeys);
        Assert.Equal((GemId)1102, c.Std(GemStandard.TimeFormat));
        Assert.Equal("<U2 10>", c.Vars[1101].Initial.ToSml());
        Assert.Equal(("<U1 0>", "<U1 2>"), (c.Vars[1102].Min!.ToSml(), c.Vars[1102].Max!.ToSml()));
        Assert.Equal(SecsFormat.U4, c.Vars[1004].Format);                                   // EventsEnabled = CeidFormat 배열
        Assert.Contains(10, c.Streams);
    }

    [Fact]
    public void Catalog_AsciiFormats_StandardIdsAreNames_NumbersBecomeText()
    {
        var d = new GemDefinitionSet();
        d.Add(new GemStatusVariable(2001, "Temperature", SecsFormat.F4));
        d.Add(new GemCollectionEvent("LOT_START", "LotStart", [2001]));
        var c = Build(d, vid: SecsFormat.ASCII, ceid: SecsFormat.ASCII, alid: SecsFormat.ASCII);
        Assert.True(c.Vars.ContainsKey("Clock"));
        Assert.True(c.Vars.ContainsKey("2001"));                                           // 숫자 ID는 10진 문자열 열쇠
        Assert.Equal([(GemId)"2001"], c.Events["LOT_START"].VidKeys);
        Assert.True(c.Events.ContainsKey("EquipmentOffline"));
        Assert.Equal(SecsFormat.List, c.Vars["EventsEnabled"].Format);                     // A 목록은 <L <A>…>
        Assert.Equal(SecsFormat.ASCII, c.Vars["AlarmID"].Format);
    }

    [Fact]
    public void Catalog_StandardIdsOverride()
    {
        var d = new GemDefinitionSet();
        d.Add(new GemStatusVariable(1001, "MyClockLikeSv", SecsFormat.U4));
        Assert.Contains("겹칩니다", Assert.Throws<InvalidOperationException>(() => Build(d)).Message);
        var c = Build(d, new GemStandardIds { Clock = 9001 });
        Assert.Equal(GemStandard.Clock, c.Vars[9001].Standard);
        Assert.Null(c.Vars[1001].Standard);
    }

    [Fact]
    public void Catalog_Errors_AreInvalidOperation()
    {
        void Fails(Action<GemDefinitionSet> setup, string contains, SecsFormat vid = SecsFormat.U4, SecsFormat ceid = SecsFormat.U4)
        {
            var d = new GemDefinitionSet();
            setup(d);
            Assert.Contains(contains, Assert.Throws<InvalidOperationException>(() => Build(d, vid: vid, ceid: ceid)).Message);
        }
        Fails(d => d.Add(new GemStatusVariable("TEMP", "T", SecsFormat.F4)), "VidFormat");                       // 문자열 ID + 숫자 포맷
        Fails(d => d.Add(new GemStatusVariable(300, "T", SecsFormat.F4)), "VidFormat", vid: SecsFormat.U1);      // 표준 1001도 U1 밖
        Fails(d => d.Add(new GemCollectionEvent(5001, "E", [7777])), "7777");                                    // 모르는 VID
        Fails(d => { d.Add(new GemCollectionEvent(9001, "E")); d.Add(new GemAlarm(1, "a", 1, 9001, 9002)); }, "겹칩니다");   // 알람 CE와 앱 CE
        Fails(d => d.Add(new GemCollectionEvent(1301, "E")), "겹칩니다");                                         // 표준 CE와
        Fails(d => { d.Add(new GemStatusVariable(2001, "A", SecsFormat.U4)); d.Add(new GemDataVariable("2001", "B", SecsFormat.U4)); }, "겹칩니다", vid: SecsFormat.ASCII);
        Fails(d => d.Add(new GemAlarm(-1, "a", 1, 9001, 9002)), "AlidFormat");
    }
    #endregion
}
