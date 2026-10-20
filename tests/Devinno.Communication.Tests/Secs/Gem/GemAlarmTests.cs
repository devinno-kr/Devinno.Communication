using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 알람: S5F1(SET/CLEAR ALCD 골든)과 알람 CE 짝, 상태 갱신 규칙, S5F3/S5F5/S5F7
public class GemAlarmTests
{
    static GemEquipment Eq(Action<GemEquipment>? more = null) => GemKit.Equipment(e =>
    {
        GemKit.Sample(e);
        e.AddAlarm(new GemAlarm(2, "Door open", 6, 9003, 9004));
        e.InitialControlState = GemControlState.OnlineRemote;
        more?.Invoke(e);
    });

    [Fact]
    public async Task SetClear_S5F1Golden_ThenAlarmCe_WithAlarmIdDv()
    {
        using var eq = Eq(e => { e.AlarmsEnabledByDefault = true; e.EventsEnabledByDefault = true; });
        using var host = await GemKit.Communicating(eq);
        await host.Ask("S2F33 W <L <U4 0> <L <L <U4 1000> <L <U4 1201> <U4 1006>>>>>");
        await host.Ask("S2F35 W <L <U4 0> <L <L <U4 9001> <L <U4 1000>>> <L <U4 9002> <L <U4 1000>>>>>");
        Assert.True(eq.SetAlarm(1));
        Assert.Equal("S5F1 W <L [3] <B 0x82> <U4 1> <A \"Over temperature\">>", host.Next(5, 1).ToSml());          // S5F1이 CE보다 먼저
        Assert.Equal("<L [3] <U4 1> <U4 9001> <L [1] <L [2] <U4 1000> <L [2] <U4 1> <U4 1>>>>>", host.Next(6, 11).Item!.ToSml());
        Assert.False(eq.SetAlarm(1));                                                                             // 이미 SET: 아무것도 안 보냄
        Assert.True(eq.ClearAlarm(1));
        Assert.Equal("S5F1 W <L [3] <B 0x02> <U4 1> <A \"Over temperature\">>", host.Next(5, 1).ToSml());
        Assert.Equal("<L [3] <U4 2> <U4 9002> <L [1] <L [2] <U4 1000> <L [2] <U4 1> <U4>>>>>", host.Next(6, 11).Item!.ToSml());
        Assert.False(eq.ClearAlarm(1));
        host.NoMore();
        Assert.Equal(SecsItem.U4(), eq.GetValue(1201));                                                           // AlarmID DV는 그 리포트에만
    }

    [Fact]
    public async Task NotEnabled_NoS5F1_ButCeStillFollowsItsOwnEnable()
    {
        using var eq = Eq(e => e.EventsEnabledByDefault = true);
        using var host = await GemKit.Communicating(eq);
        eq.SetAlarm(2);
        Assert.Equal(9003, host.Next(6, 11).Item!.Items[1].GetInt64());
        host.NoMore();
        Assert.Equal(SecsItem.U4(2), eq.GetValue(1006));                                                          // AlarmsSet
    }

    [Fact]
    public async Task Offline_StateChanges_NothingSent()
    {
        using var eq = Eq(e => { e.AlarmsEnabledByDefault = true; e.EventsEnabledByDefault = true; e.InitialControlState = GemControlState.HostOffline; });
        var log = GemKit.Log(eq);
        using var host = await GemKit.Communicating(eq);
        Assert.True(eq.SetAlarm(1));
        Assert.True(eq.SetAlarm(2));
        host.NoMore();
        Assert.Equal(SecsItem.U4(1, 2), eq.GetValue(1006));
        Assert.DoesNotContain(log, l => l.StartsWith("SendFailed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnlineNotCommunicating_DiscardPath()
    {
        using var eq = Eq(e => { e.AlarmsEnabledByDefault = true; e.EventsEnabledByDefault = true; e.InitiateCommunication = false; });
        var log = GemKit.Log(eq);
        using var host = await GemKit.Connect(eq);
        Assert.True(eq.SetAlarm(1));
        await TestUtil.Until(() => log.Contains("SendFailed S5F1 NotCommunicating") && log.Contains("SendFailed S6F11 NotCommunicating"));
        Assert.Equal(SecsItem.U4(1), eq.GetValue(1006));
    }

    [Fact]
    public async Task S5F2NonZero_SendFailed_AlarmReplyExpectedFalse_NoWBit()
    {
        using var eq = Eq(e => e.AlarmsEnabledByDefault = true);
        var log = GemKit.Log(eq);
        using var host = await GemKit.Communicating(eq, h => h.Reply(5, 1, "<B 1>"));
        eq.SetAlarm(1);
        host.Next(5, 1);
        await TestUtil.Until(() => log.Contains("SendFailed S5F1 NotAcknowledged 1"));

        using var eq2 = Eq(e => { e.AlarmsEnabledByDefault = true; e.AlarmReplyExpected = false; });
        using var host2 = await GemKit.Communicating(eq2);
        eq2.SetAlarm(1);
        Assert.False(host2.Next(5, 1).ReplyExpected);
    }

    [Fact]
    public async Task S5F3_EnableOne_All_Disable_Unknown_Lenient()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S5F4 <B 0x00>", await host.Ask("S5F3 W <L <B 0x80> <U4 2>>"));
        Assert.Equal(SecsItem.U4(2), eq.GetValue(1005));
        Assert.Equal("S5F4 <B 0x00>", await host.Ask("S5F3 W <L <B 0x80> <U4>>"));       // 길이 0 = 모든 알람
        Assert.Equal(SecsItem.U4(1, 2), eq.GetValue(1005));
        Assert.Equal("S5F4 <B 0x00>", await host.Ask("S5F3 W <L <B 0x00> <U1 1>>"));     // 끔, ALID는 아무 정수 포맷
        Assert.Equal(SecsItem.U4(2), eq.GetValue(1005));
        Assert.Equal("S5F4 <B 0x01>", await host.Ask("S5F3 W <L <B 0x80> <U4 77>>"));    // 모르는 ALID
        Assert.Equal("S5F4 <B 0x00>", await host.Ask("S5F3 W <L <BOOLEAN T> <U4 1>>"));  // BOOLEAN도 받음
        Assert.Equal(SecsItem.U4(1, 2), eq.GetValue(1005));
        Assert.Equal(7, (await Assert.ThrowsAsync<SecsErrorException>(() => host.SendAsync("S5F3 W <L <A \"on\"> <U4 1>>"))).Function);
        Assert.Equal(7, (await Assert.ThrowsAsync<SecsErrorException>(() => host.SendAsync("S5F3 W <L <B 0x80>>"))).Function);
    }

    [Fact]
    public async Task S5F5_ListAlarms_Golden_SecsgemStyleList_Unknown()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        eq.SetAlarm(2);
        const string all = "S5F6 <L [2] <L [3] <B 0x02> <U4 1> <A \"Over temperature\">> <L [3] <B 0x86> <U4 2> <A \"Door open\">>>";
        Assert.Equal(all, await host.Ask("S5F5 W <U4>"));
        Assert.Equal(all, await host.Ask("S5F5 W <L>"));
        Assert.Equal("S5F6 <L [2] <L [3] <B 0x86> <U4 2> <A \"Door open\">> <L [3] <B> <U4 9> <A \"\">>>", await host.Ask("S5F5 W <U4 2 9>"));
        Assert.Equal("S5F6 <L [1] <L [3] <B 0x02> <U4 1> <A \"Over temperature\">>>", await host.Ask("S5F5 W <L <U1 1>>"));
    }

    [Fact]
    public async Task S5F7_OnlyEnabledAlarms()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S5F8 <L [0]>", await host.Ask("S5F7 W"));
        await host.Ask("S5F3 W <L <B 0x80> <U4 2>>");
        Assert.Equal("S5F8 <L [1] <L [3] <B 0x06> <U4 2> <A \"Door open\">>>", await host.Ask("S5F7 W"));
    }

    [Fact]
    public void ArgumentChecks()
    {
        using var eq = Eq();
        Assert.Throws<InvalidOperationException>(() => eq.SetAlarm(1));
        eq.Start();
        Assert.Equal("alid", Assert.Throws<ArgumentException>(() => eq.SetAlarm(99)).ParamName);
        Assert.Equal("alid", Assert.Throws<ArgumentException>(() => eq.ClearAlarm("1")).ParamName);
    }
}
