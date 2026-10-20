using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Secs.Hsms;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 제어 상태 머신: E30 전이 표(1~12)의 행마다 하나씩 + OFF-LINE·NOT COMMUNICATING에서의 처리 범위(상태 CE 순서는 GemReportTests)
public class GemControlTests
{
    static GemEquipment Eq(GemControlState initial, Action<GemEquipment>? more = null) => GemKit.Equipment(e =>
    {
        e.InitialControlState = initial;
        more?.Invoke(e);
    });

    [Theory]
    [InlineData(GemControlState.EquipmentOffline, GemLocalRemote.Remote)]
    [InlineData(GemControlState.HostOffline, GemLocalRemote.Remote)]
    [InlineData(GemControlState.OnlineLocal, GemLocalRemote.Local)]
    [InlineData(GemControlState.OnlineRemote, GemLocalRemote.Remote)]
    public void T1_T2_InitialState_AndSwitch_NoEvent(GemControlState initial, GemLocalRemote sw)
    {
        using var eq = Eq(initial);
        var log = GemKit.Log(eq);
        eq.Start();
        Assert.Equal((initial, sw), (eq.ControlState, eq.LocalRemoteSwitch));
        Thread.Sleep(100);
        Assert.Empty(GemKit.Only(log, "Control"));
    }

    [Fact]
    public void Settings_Validation()
    {
        using var eq = GemKit.Equipment();
        Assert.Equal((GemControlState.HostOffline, GemControlState.EquipmentOffline), (eq.InitialControlState, eq.OnlineFailedState));
        Assert.Throws<ArgumentException>(() => eq.InitialControlState = GemControlState.AttemptOnline);
        Assert.Throws<ArgumentException>(() => eq.InitialControlState = (GemControlState)9);
        Assert.Throws<ArgumentException>(() => eq.OnlineFailedState = GemControlState.OnlineRemote);
        eq.OnlineFailedState = GemControlState.HostOffline;
        Assert.Throws<InvalidOperationException>(() => eq.GoOnline());                  // 시작 전
        Assert.Throws<InvalidOperationException>(() => eq.SwitchLocal());
    }

    [Fact]
    public async Task T3_T5_T7_GoOnline_S1F1_S1F2_EntersRemote()
    {
        using var eq = Eq(GemControlState.EquipmentOffline);
        var log = GemKit.Log(eq);
        using var host = await GemKit.Communicating(eq);
        Assert.True(eq.GoOnline());
        Assert.Equal("S1F1 W", host.Next(1, 1).ToSml());
        await TestUtil.Until(() => eq.ControlState == GemControlState.OnlineRemote);
        await TestUtil.Until(() => GemKit.Only(log, "Control").Length == 2);
        Assert.Equal(["Control EquipmentOffline->AttemptOnline", "Control AttemptOnline->OnlineRemote"], GemKit.Only(log, "Control"));
        Assert.False(eq.GoOnline());                                                      // 이미 ON-LINE
    }

    [Fact]
    public async Task T7_SwitchLocalWhileOffline_ThenGoOnline_EntersLocal()
    {
        using var eq = Eq(GemControlState.EquipmentOffline);
        using var host = await GemKit.Communicating(eq);
        Assert.True(eq.SwitchLocal());                                                    // OFF-LINE: 위치만
        Assert.False(eq.SwitchLocal());
        Assert.Equal(GemControlState.EquipmentOffline, eq.ControlState);
        eq.GoOnline();
        host.Next(1, 1);
        await TestUtil.Until(() => eq.ControlState == GemControlState.OnlineLocal);
    }

    [Fact]
    public async Task T4_GoOnline_NotCommunicating_FailsImmediately()
    {
        using var eq = Eq(GemControlState.EquipmentOffline, e => e.InitiateCommunication = false);
        var log = GemKit.Log(eq);
        using var host = await GemKit.Connect(eq);
        Assert.True(eq.GoOnline());
        Assert.Equal(GemControlState.EquipmentOffline, eq.ControlState);
        await TestUtil.Until(() => GemKit.Only(log, "Control").Length == 2);
        Assert.Equal(["Control EquipmentOffline->AttemptOnline", "Control AttemptOnline->EquipmentOffline"], GemKit.Only(log, "Control"));
        host.NoMore();
    }

    [Fact]
    public async Task T4_S1F0_GoesToConfiguredFailedState_SendFailedAborted()
    {
        using var eq = Eq(GemControlState.EquipmentOffline, e => e.OnlineFailedState = GemControlState.HostOffline);
        var log = GemKit.Log(eq);
        using var host = await GemKit.Communicating(eq, h => h.Reply(1, 1, null));
        eq.GoOnline();
        host.Next(1, 1);
        await TestUtil.Until(() => eq.ControlState == GemControlState.HostOffline);
        await TestUtil.Until(() => log.Contains("SendFailed S1F1 Aborted"));
        Assert.Equal(GemCommunicationState.Communicating, eq.CommunicationState);         // SxF0은 통신 실패가 아님
    }

    [Fact]
    public async Task T4_T3_IsCommunicationFailure_T14_ThenReestablishes()
    {
        using var eq = Eq(GemControlState.EquipmentOffline, e => ((SecsHsms)e.Transport).T3 = 1000);
        var log = GemKit.Log(eq);
        using var host = await GemKit.Communicating(eq, h => h.Hold(1, 1));
        eq.GoOnline();
        Assert.True(host.Held.TryTake(out _, 3000));
        await TestUtil.Until(() => eq.ControlState == GemControlState.EquipmentOffline, 5000);
        await TestUtil.Until(() => log.Contains("SendFailed S1F1 Timeout"));
        await TestUtil.Until(() => log.Contains("Comm Communicating->NotCommunicating"));  // 전이 14
        host.NextOf(1, 13, 5000);                                                          // 연결은 살아 있으므로 바로 다시 S1F13
        await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.Communicating);
    }

    [Fact]
    public async Task T4_GoOnline_QueueFull_FailsInsteadOfStuckInAttemptOnline()
    {
        using var eq = Eq(GemControlState.EquipmentOffline);
        var log = GemKit.Log(eq);
        using var host = await GemKit.Communicating(eq);
        // 대기열 상한은 실행 중에 못 바꾸므로, 가득 찬 상황을 필드를 직접 줄여 만든다
        typeof(GemEquipment).GetField("maxPendingSends", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(eq, 0);
        Assert.True(eq.GoOnline());
        Assert.Equal(GemControlState.EquipmentOffline, eq.ControlState);
        await TestUtil.Until(() => log.Contains("SendFailed S1F1 QueueFull"));
        await TestUtil.Until(() => GemKit.Only(log, "Control").Length == 2);
        Assert.Equal(["Control EquipmentOffline->AttemptOnline", "Control AttemptOnline->EquipmentOffline"], GemKit.Only(log, "Control"));
        host.NoMore();
    }

    [Fact]
    public async Task T4_DisableCommunication_DuringAttemptOnline()
    {
        using var eq = Eq(GemControlState.EquipmentOffline);
        var log = GemKit.Log(eq);
        using var host = await GemKit.Communicating(eq, h => h.Hold(1, 1));
        eq.GoOnline();
        Assert.True(host.Held.TryTake(out var held, 3000));
        eq.DisableCommunication();
        Assert.Equal(GemControlState.EquipmentOffline, eq.ControlState);
        await TestUtil.Until(() => log.Contains("SendFailed S1F1 Disabled"));
        held!.Reply(new SecsMessage(1, 2, false, SecsItem.L()));                           // 늦은 S1F2는 무시
        await Task.Delay(200);
        Assert.Equal(GemControlState.EquipmentOffline, eq.ControlState);
    }

    [Fact]
    public async Task AttemptOnline_IgnoresOnOffSwitch_ButKeepsLocalRemotePosition()
    {
        using var eq = Eq(GemControlState.EquipmentOffline);
        using var host = await GemKit.Communicating(eq, h => h.Hold(1, 1));
        eq.GoOnline();
        Assert.True(host.Held.TryTake(out var held, 3000));
        Assert.Equal(GemControlState.AttemptOnline, eq.ControlState);
        Assert.False(eq.GoOnline());
        Assert.False(eq.GoOffline());
        Assert.True(eq.SwitchLocal());
        held!.Reply(new SecsMessage(1, 2, false, SecsItem.L()));
        await TestUtil.Until(() => eq.ControlState == GemControlState.OnlineLocal);
    }

    [Fact]
    public async Task T6_GoOffline_FromOnline()
    {
        using var eq = Eq(GemControlState.OnlineRemote);
        var log = GemKit.Log(eq);
        using var host = await GemKit.Communicating(eq);
        Assert.True(eq.GoOffline());
        Assert.False(eq.GoOffline());
        Assert.Equal(GemControlState.EquipmentOffline, eq.ControlState);
        await TestUtil.Until(() => GemKit.Only(log, "Control").Length == 1);
        Assert.Equal("Control OnlineRemote->EquipmentOffline", GemKit.Only(log, "Control")[0]);
    }

    [Fact]
    public async Task T8_T9_LocalRemoteSwitch_WhileOnline()
    {
        using var eq = Eq(GemControlState.OnlineRemote);
        var log = GemKit.Log(eq);
        using var host = await GemKit.Communicating(eq);
        Assert.True(eq.SwitchLocal());                                                    // 전이 9
        Assert.Equal((GemControlState.OnlineLocal, GemLocalRemote.Local), (eq.ControlState, eq.LocalRemoteSwitch));
        Assert.True(eq.SwitchRemote());                                                   // 전이 8
        Assert.False(eq.SwitchRemote());
        Assert.Equal(GemControlState.OnlineRemote, eq.ControlState);
        await TestUtil.Until(() => GemKit.Only(log, "Control").Length == 2);
        Assert.Equal(["Control OnlineRemote->OnlineLocal", "Control OnlineLocal->OnlineRemote"], GemKit.Only(log, "Control"));
    }

    [Fact]
    public async Task T10_HostS1F15_OflackGolden_HostOffline()
    {
        using var eq = Eq(GemControlState.OnlineLocal);
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S1F16 <B 0x00>", await host.Ask("S1F15 W"));
        await TestUtil.Until(() => eq.ControlState == GemControlState.HostOffline);
        await Assert.ThrowsAsync<SecsAbortException>(() => host.SendAsync("S1F15 W"));   // OFF-LINE: S1F13·S1F17만
    }

    [Fact]
    public async Task T11_HostS1F17_OnlackGolden_AllThreeCodes()
    {
        using var eq = Eq(GemControlState.HostOffline);
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S1F18 <B 0x00>", await host.Ask("S1F17 W"));
        await TestUtil.Until(() => eq.ControlState == GemControlState.OnlineRemote);
        Assert.Equal("S1F18 <B 0x02>", await host.Ask("S1F17 W"));                       // 이미 ON-LINE
        eq.GoOffline();
        Assert.Equal("S1F18 <B 0x01>", await host.Ask("S1F17 W"));                       // EQUIPMENT OFF-LINE: 허락하지 않음
        Assert.Equal(GemControlState.EquipmentOffline, eq.ControlState);
    }

    [Fact]
    public async Task T12_GoOffline_FromHostOffline()
    {
        using var eq = Eq(GemControlState.HostOffline);
        var log = GemKit.Log(eq);
        using var host = await GemKit.Communicating(eq);
        Assert.True(eq.GoOffline());
        Assert.Equal(GemControlState.EquipmentOffline, eq.ControlState);
        await TestUtil.Until(() => GemKit.Only(log, "Control").Length == 1);
        Assert.Equal("Control HostOffline->EquipmentOffline", GemKit.Only(log, "Control")[0]);
    }

    [Fact]
    public async Task Offline_OnlyS1F13AndS1F17_OthersSxF0_UnknownStillS9F5()
    {
        using var eq = Eq(GemControlState.EquipmentOffline);
        using var host = await GemKit.Communicating(eq);
        await Assert.ThrowsAsync<SecsAbortException>(() => host.SendAsync("S1F1 W"));
        Assert.StartsWith("S1F14", await host.Ask("S1F13 W <L>"));
        Assert.Equal("S1F18 <B 0x01>", await host.Ask("S1F17 W"));
        Assert.Equal(5, (await Assert.ThrowsAsync<SecsErrorException>(() => host.SendAsync("S1F99 W"))).Function);
    }

    [Fact]
    public async Task NotCommunicating_OnlyS1F13_OthersSxF0()
    {
        using var eq = Eq(GemControlState.OnlineRemote, e => e.InitiateCommunication = false);
        using var host = await GemKit.Connect(eq);
        await Assert.ThrowsAsync<SecsAbortException>(() => host.SendAsync("S1F1 W"));
        await Assert.ThrowsAsync<SecsAbortException>(() => host.SendAsync("S1F17 W"));
        Assert.Equal(GemCommunicationState.NotCommunicating, eq.CommunicationState);
    }

    [Fact]
    public async Task S1F1_FromHost_GoldenS1F2()
    {
        using var eq = Eq(GemControlState.OnlineRemote);
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S1F2 <L [2] <A \"DV-100\"> <A \"1.0.0\">>", await host.Ask("S1F1 W"));
    }
}
