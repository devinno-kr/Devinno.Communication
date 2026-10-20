using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 통신 상태 머신: E30 전이 표(1~10·14·15)의 행마다 하나씩 + S1F13/S1F14 본문
/// (전이 7의 CommDelay 만료는 EstablishCommunicationsTimeout을 1초로 줄일 수 있는 GemConstantTests에서, T3 통신 실패는 GemControlTests에서 확인)
public class GemCommunicationTests
{
    [Fact]
    public async Task T1_InitialEnabled_ConnectSendsS1F13_GoldenBody_T4_T5_T9()
    {
        using var eq = GemKit.Equipment();
        var log = GemKit.Log(eq);
        var host = await GemKit.Connect(eq);
        using (host)
        {
            var s1f13 = host.Next(1, 13);                                                // 연결 직후(10초 CommDelay를 기다리지 않음)
            Assert.Equal("S1F13 W <L [2] <A \"DV-100\"> <A \"1.0.0\">>", s1f13.ToSml());
            await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.Communicating);
            await TestUtil.Until(() => GemKit.Only(log, "Comm").Length == 1);
            Assert.Equal(["Comm NotCommunicating->Communicating"], GemKit.Only(log, "Comm"));
        }
    }

    [Fact]
    public async Task T1_InitialDisabled_NoS1F13_EverythingIsSxF0()
    {
        using var eq = GemKit.Equipment(e => e.InitialCommunicationState = GemEnableState.Disabled);
        using var host = await GemKit.Connect(eq);
        Assert.Equal(GemCommunicationState.Disabled, eq.CommunicationState);
        host.NoMore(500);
        await Assert.ThrowsAsync<SecsAbortException>(() => host.SendAsync("S1F13 W <L>"));
        await Assert.ThrowsAsync<SecsAbortException>(() => host.SendAsync("S7F1 W"));   // DISABLED는 S9F3보다 먼저: 아무것도 해석하지 않음
        Assert.Equal(GemCommunicationState.Disabled, eq.CommunicationState);
    }

    [Fact]
    public async Task T2_EnableCommunication_SendsS1F13_ThenCommunicating()
    {
        using var eq = GemKit.Equipment(e => e.InitialCommunicationState = GemEnableState.Disabled);
        var log = GemKit.Log(eq);
        using var host = await GemKit.Connect(eq);
        Assert.True(eq.EnableCommunication());
        Assert.False(eq.EnableCommunication());
        host.Next(1, 13);
        await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.Communicating);
        await TestUtil.Until(() => GemKit.Only(log, "Comm").Length == 2);
        Assert.Equal(["Comm Disabled->NotCommunicating", "Comm NotCommunicating->Communicating"], GemKit.Only(log, "Comm"));
    }

    [Fact]
    public async Task T3_DisableCommunication_KeepsLink_RepliesSxF0()
    {
        using var eq = GemKit.Equipment();
        var log = GemKit.Log(eq);
        using var host = await GemKit.Communicating(eq);
        Assert.True(eq.DisableCommunication());
        Assert.False(eq.DisableCommunication());
        Assert.Equal(GemCommunicationState.Disabled, eq.CommunicationState);
        await Assert.ThrowsAsync<SecsAbortException>(() => host.SendAsync("S1F13 W <L>"));
        Assert.True(host.Hsms.IsOpen && eq.IsOpen);                                      // 전송 연결은 유지
        await TestUtil.Until(() => GemKit.Only(log, "Comm").Length == 2);
        Assert.Equal("Comm Communicating->Disabled", GemKit.Only(log, "Comm")[1]);
        using var idle = GemKit.Equipment();
        Assert.Throws<InvalidOperationException>(() => idle.EnableCommunication());       // 시작 전
    }

    [Fact]
    public async Task T6_CommackDenied_WaitDelay_NoImmediateRetry()
    {
        using var eq = GemKit.Equipment();
        var log = GemKit.Log(eq);
        using var host = await GemKit.Connect(eq, h => h.Reply(1, 13, "<L <B 1> <L>>"));
        host.Next(1, 13);
        await TestUtil.Until(() => log.Any(l => l.Contains("COMMACK 1", StringComparison.Ordinal)));
        Assert.Equal(GemCommunicationState.NotCommunicating, eq.CommunicationState);
        host.NoMore(700);                                                                 // CommDelay(10초) 동안 다시 보내지 않음
    }

    [Fact]
    public async Task T6_S1F0Reply_IsAlsoFailure()
    {
        using var eq = GemKit.Equipment();
        var log = GemKit.Log(eq);
        using var host = await GemKit.Connect(eq, h => h.Reply(1, 13, null));        // 응답 안 함 → Host 전송이 S1F0
        host.Next(1, 13);
        await TestUtil.Until(() => log.Any(l => l.StartsWith("Error S1F13 통신 수립 실패", StringComparison.Ordinal)));
        Assert.Equal(GemCommunicationState.NotCommunicating, eq.CommunicationState);
    }

    [Fact]
    public async Task T8_WaitDelay_OtherPrimary_ImmediateS1F13()
    {
        using var eq = GemKit.Equipment();
        using var host = await GemKit.Connect(eq, h => h.Reply(1, 13, "<L <B 1> <L>>"));
        host.Next(1, 13);
        await Task.Delay(300);                                                           // WAIT DELAY로 들어갈 시간
        host.Reply(1, 13, "<L <B 0> <L>>");
        await Assert.ThrowsAsync<SecsErrorException>(() => host.SendAsync("S7F1 W"));  // S1F13이 아닌 메시지(여기서는 S9F3로 거절)
        host.NextOf(1, 13, 2000);                                                        // CommDelay를 기다리지 않고 바로
        await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.Communicating);
    }

    [Fact]
    public async Task T10_T15_HostInitiated_S1F14Golden()
    {
        using var eq = GemKit.Equipment(e => e.InitiateCommunication = false);
        var log = GemKit.Log(eq);
        using var host = await GemKit.Connect(eq);
        host.NoMore(500);                                                                 // 장비는 S1F13을 보내지 않음
        Assert.Equal(GemCommunicationState.NotCommunicating, eq.CommunicationState);
        Assert.Equal("S1F14 <L [2] <B 0x00> <L [2] <A \"DV-100\"> <A \"1.0.0\">>>", await host.Ask("S1F13 W <L>"));
        await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.Communicating);
        Assert.Equal("S1F14 <L [2] <B 0x00> <L [2] <A \"DV-100\"> <A \"1.0.0\">>>", await host.Ask("S1F13 W <L>"));   // COMMUNICATING 중에도 0, 상태 유지
        Assert.Equal(GemCommunicationState.Communicating, eq.CommunicationState);
        await TestUtil.Until(() => GemKit.Only(log, "Comm").Length == 1);
    }

    [Fact]
    public async Task T14_LinkLost_NotCommunicating_ThenReconnectEstablishesAgain()
    {
        using var eq = GemKit.Equipment();
        var log = GemKit.Log(eq);
        var host = await GemKit.Communicating(eq);
        host.Dispose();                                                                  // 연결 끊김 = 통신 실패
        await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.NotCommunicating, 5000);
        using var host2 = new Fakes.FakeGemHost(GemKit.Port(eq));
        host2.Start();
        host2.Next(1, 13, 5000);
        await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.Communicating);
        await TestUtil.Until(() => GemKit.Only(log, "Comm").Length == 3);
        Assert.Equal(["Comm NotCommunicating->Communicating", "Comm Communicating->NotCommunicating", "Comm NotCommunicating->Communicating"], GemKit.Only(log, "Comm"));
    }

    [Fact]
    public async Task SimultaneousS1F13_FirstCompletionWins_LateS1F14Ignored()
    {
        using var eq = GemKit.Equipment();
        using var host = await GemKit.Connect(eq, h => h.Hold(1, 13));
        Assert.True(host.Held.TryTake(out var held, 3000));                               // 장비 S1F13을 붙잡아 둠
        Assert.StartsWith("S1F14 <L [2] <B 0x00>", await host.Ask("S1F13 W <L>"));      // Host 쪽이 먼저 끝남 → 전이 15
        await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.Communicating);
        held!.Reply(new SecsMessage(1, 14, false, SecsItem.L(SecsItem.B(1), SecsItem.L())));   // 늦게 온 거절 S1F14
        await Task.Delay(300);
        Assert.Equal(GemCommunicationState.Communicating, eq.CommunicationState);
    }
}
