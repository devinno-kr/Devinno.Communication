using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 변수 값과 S1F3/S1F11/S1F21/S1F23(골든 SML), 본문 구조 오류의 S9F7, 받을 때 관대하게(정수 포맷 8종)
public class GemDataTests
{
    static GemEquipment Eq(Action<GemEquipment>? more = null) => GemKit.Equipment(e =>
    {
        GemKit.Sample(e);
        e.InitialControlState = GemControlState.OnlineRemote;
        more?.Invoke(e);
    });

    [Fact]
    public async Task S1F3_SelectedSvids_AnyIntegerFormat_UnknownIsEmptyList()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S1F4 <L [4] <F4 25> <U1 5> <U4> <L [0]>>", await host.Ask("S1F3 W <L <U4 2001> <U2 1002> <I8 2002> <U4 9999>>"));
        eq.SetValue(2001, SecsItem.F4(31.5f));
        eq.SetValue(2002, SecsItem.U4(7));
        Assert.Equal("S1F4 <L [2] <F4 31.5> <U4 7>>", await host.Ask("S1F3 W <L <U2 2001> <U2 2002>>"));
        Assert.Equal("S1F4 <L [1] <L [0]>>", await host.Ask("S1F3 W <L <U4 4001>>"));      // EC는 SV가 아님
    }

    [Fact]
    public async Task S1F3_EmptyList_AllSvs_StandardFirst_SameOrderAsS1F11()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        var all = (await host.SendAsync("S1F3 W <L>"))!.Item!;
        Assert.Equal(10, all.Count);                                                      // 표준 8 + 앱 2
        Assert.Equal("<F4 25>", all.Items[8].ToSml());
        Assert.Equal("S1F12 <L [10] <L [3] <U4 1001> <A \"Clock\"> <A \"\">> <L [3] <U4 1002> <A \"ControlState\"> <A \"\">> "
            + "<L [3] <U4 1003> <A \"PreviousControlState\"> <A \"\">> <L [3] <U4 1004> <A \"EventsEnabled\"> <A \"\">> "
            + "<L [3] <U4 1005> <A \"AlarmsEnabled\"> <A \"\">> <L [3] <U4 1006> <A \"AlarmsSet\"> <A \"\">> "
            + "<L [3] <U4 1007> <A \"ProcessState\"> <A \"\">> <L [3] <U4 1008> <A \"PreviousProcessState\"> <A \"\">> "
            + "<L [3] <U4 2001> <A \"Temperature\"> <A \"C\">> <L [3] <U4 2002> <A \"Count\"> <A \"\">>>", await host.Ask("S1F11 W <L>"));
        Assert.StartsWith("S1F4 <L [10] ", await host.Ask("S1F3 W"));                  // 본문 없음 = 빈 목록으로 봄
    }

    [Fact]
    public async Task S1F11_Selected_UnknownEchoesIdWithEmptyText()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S1F12 <L [2] <L [3] <U4 2001> <A \"Temperature\"> <A \"C\">> <L [3] <I2 77> <A \"\"> <A \"\">>>",
            await host.Ask("S1F11 W <L <U2 2001> <I2 77>>"));
    }

    [Fact]
    public async Task S1F21_DataVariables_StandardFirst()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S1F22 <L [2] <L [3] <U4 1201> <A \"AlarmID\"> <A \"\">> <L [3] <U4 3001> <A \"LotID\"> <A \"\">>>", await host.Ask("S1F21 W <L>"));
        Assert.Equal("S1F22 <L [1] <L [3] <U4 2001> <A \"\"> <A \"\">>>", await host.Ask("S1F21 W <L <U4 2001>>"));   // SV는 DV가 아님
    }

    [Fact]
    public async Task S1F23_EventNames_WithVids_UnknownEmpty()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S1F24 <L [2] <L [3] <U4 5001> <A \"ProcessStarted\"> <L [2] <U4 2001> <U4 3001>>> <L [3] <U4 7777> <A \"\"> <L [0]>>>",
            await host.Ask("S1F23 W <L <U4 5001> <U4 7777>>"));
        var all = (await host.SendAsync("S1F23 W <L>"))!.Item!;
        Assert.Equal(10, all.Count);                                                      // 표준 6 + 앱 2 + 알람 2
        Assert.Equal("<L [3] <U4 9001> <A \"Alarm 1 Set\"> <L [1] <U4 1201>>>", all.Items[8].ToSml());
        Assert.Equal("<L [3] <U4 1302> <A \"ControlStateLocal\"> <L [2] <U4 1002> <U4 1001>>>", all.Items[1].ToSml());
    }

    [Theory]
    [InlineData("S1F3 W <U4 2001>")]                                                      // 리스트가 아님
    [InlineData("S1F3 W <L <F4 2001>>")]                                                  // ID가 아님
    [InlineData("S1F3 W <L <A \"2001\">>")]                                               // 숫자 포맷 설정에 A
    [InlineData("S1F23 W <L <L>>")]
    public async Task MalformedBody_S9F7_NoReply(string sml)
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        var ex = await Assert.ThrowsAsync<SecsErrorException>(() => host.SendAsync(sml));
        Assert.Equal(7, ex.Function);
    }

    [Fact]
    public async Task AsciiVidFormat_IdsAreText()
    {
        using var eq = Eq(e => e.VidFormat = SecsFormat.ASCII);
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S1F4 <L [2] <F4 25> <U1 5>>", await host.Ask("S1F3 W <L <A \"2001\"> <A \"ControlState\">>"));
        Assert.Equal("S1F12 <L [1] <L [3] <A \"2001\"> <A \"Temperature\"> <A \"C\">>>", await host.Ask("S1F11 W <L <A \"2001\">>"));
        Assert.Equal(7, (await Assert.ThrowsAsync<SecsErrorException>(() => host.SendAsync("S1F3 W <L <U4 2001>>"))).Function);
    }

    [Fact]
    public async Task Offline_S1F3_IsSxF0()
    {
        using var eq = Eq(e => e.InitialControlState = GemControlState.HostOffline);
        using var host = await GemKit.Communicating(eq);
        await Assert.ThrowsAsync<SecsAbortException>(() => host.SendAsync("S1F3 W <L>"));
    }

    [Fact]
    public void SetValue_GetValue_Rules()
    {
        using var eq = Eq();
        Assert.Throws<InvalidOperationException>(() => eq.GetValue(2001));                // 시작 전
        eq.Start();
        Assert.Equal(SecsItem.F4(25), eq.GetValue(2001));
        Assert.Equal(SecsItem.A(""), eq.GetValue(3001));
        Assert.Equal(SecsItem.F4(80), eq.GetValue(4001));                                  // EC
        Assert.Equal(SecsItem.U2(10), eq.GetValue(1101));                                  // 표준 EC
        Assert.Equal(SecsItem.U1(5), eq.GetValue(1002));                                   // 표준 SV(계산)
        eq.SetValue(3001, SecsItem.A("LOT-1"));
        Assert.Equal(SecsItem.A("LOT-1"), eq.GetValue(3001));
        Assert.Equal("value", Assert.Throws<ArgumentException>(() => eq.SetValue(2001, SecsItem.F8(1))).ParamName);
        Assert.Equal("vid", Assert.Throws<ArgumentException>(() => eq.SetValue(1002, SecsItem.U1(1))).ParamName);
        Assert.Equal("vid", Assert.Throws<ArgumentException>(() => eq.SetValue(4001, SecsItem.F4(1))).ParamName);
        Assert.Equal("vid", Assert.Throws<ArgumentException>(() => eq.SetValue(7777, SecsItem.U4(1))).ParamName);
        Assert.Throws<ArgumentException>(() => eq.GetValue("2001"));                       // 숫자 포맷에서 문자열 ID는 다른 ID
        Assert.Throws<ArgumentNullException>(() => eq.SetValue(2001, null!));
        eq.Stop();
        eq.Start();
        Assert.Equal(SecsItem.A(""), eq.GetValue(3001));                                   // Start마다 처음 값
    }

    [Fact]
    public async Task StandardValueProviderThrows_ZeroLengthValue_ProtocolError_StateStillChanges()
    {
        using var eq = Eq(e => e.EventsEnabledByDefault = true);
        // 표준 SV 계산이 던지는 상황을 이음새로 만든다(실제로는 값 검사로 막히지만 본문 만들기는 상태를 바꾼 뒤에도 던지면 안 됨)
        var field = typeof(GemEquipment).GetField("standardValues", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var providers = (Dictionary<GemStandard, Func<SecsItem>>)field.GetValue(eq)!;
        providers[GemStandard.ProcessState] = () => throw new InvalidOperationException("계산 실패");
        var log = GemKit.Log(eq);
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S1F4 <L [2] <U1> <U1 0>>", await host.Ask("S1F3 W <L <U4 1007> <U4 1008>>"));
        Assert.Equal("S2F34 <B 0x00>", await host.Ask("S2F33 W <L <U4 1> <L <L <U4 100> <L <U4 1007> <U4 1008>>>>>"));
        Assert.Equal("S2F36 <B 0x00>", await host.Ask("S2F35 W <L <U4 1> <L <L <U4 1304> <L <U4 100>>>>>"));
        eq.SetProcessState(3);
        Assert.Equal("<L [3] <U4 1> <U4 1304> <L [1] <L [2] <U4 100> <L [2] <U1> <U1 0>>>>>", host.Next(6, 11).Item!.ToSml());
        Assert.Equal(SecsItem.U1(0), eq.GetValue(1008));
        Assert.Equal(SecsItem.U1(), eq.GetValue(1007));
        await TestUtil.Until(() => log.Count(l => l.StartsWith("Error", StringComparison.Ordinal) && l.Contains("계산 실패", StringComparison.Ordinal)) >= 3);
    }
}
