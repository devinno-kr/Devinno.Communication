using System.Diagnostics;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 실행 모델: 앱 핸들러가 멈춰도 자동 응답·이벤트 송신은 막히지 않고, 디스패처는 자기 송신을 기다리지 않는다
public class GemThreadingTests
{
    static GemEquipment Eq(Action<GemEquipment>? more = null) => GemKit.Equipment(e =>
    {
        GemKit.Sample(e);
        e.InitialControlState = GemControlState.OnlineRemote;
        e.EventsEnabledByDefault = true;
        more?.Invoke(e);
    });

    /// 응답이 기한 안에 오는지 잰다(T3 3초보다 충분히 짧게: 부하가 있어도 1.5초 안)
    static async Task<string> Quick(Fakes.FakeGemHost host, string sml)
    {
        var sw = Stopwatch.StartNew();
        var reply = await host.Ask(sml);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"{sml} 응답이 {sw.ElapsedMilliseconds}ms 걸렸습니다.");
        return reply;
    }

    [Fact]
    public async Task StuckRemoteCommandHandler_DoesNotBlockAutoReplies_NorEventSend_DeadlineHcack2()
    {
        using var eq = Eq(e => e.CommandReplyTimeout = 1000);
        var log = GemKit.Log(eq);
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        eq.RemoteCommandReceived += (s, e) => { entered.Set(); release.Wait(10000); e.Hcack = GemAck.HcackAccepted; };
        using var host = await GemKit.Communicating(eq);
        var command = host.Ask("S2F41 W <L <A \"STOP\"> <L>>");
        Assert.True(entered.Wait(3000));                                                   // 앱 이벤트 스레드가 핸들러에서 멈춤
        Assert.StartsWith("S1F4 <L [1] <F4 25>>", await Quick(host, "S1F3 W <L <U4 2001>>"));
        Assert.Equal("S2F14 <L [1] <F4 80>>", await Quick(host, "S2F13 W <L <U4 4001>>"));
        Assert.True(eq.TriggerEvent(5002));
        Assert.Equal(5002, host.Next(6, 11).Item!.Items[1].GetInt64());                  // 작업 스레드의 송신도 막히지 않음
        Assert.Equal("S2F42 <L [2] <B 0x02> <L [0]>>", await command);                    // 기한(1초)에 작업 스레드가 대신 응답
        release.Set();
        await TestUtil.Until(() => log.Any(l => l.Contains("결과를 버렸습니다", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task StuckPublicEventHandler_DoesNotBlockAutoReplies()
    {
        using var eq = Eq();
        using var release = new ManualResetEventSlim();
        eq.ControlStateChanged += (s, e) => release.Wait(10000);
        using var host = await GemKit.Communicating(eq);
        eq.SwitchLocal();                                                                   // 핸들러가 앱 이벤트 스레드를 붙잡음
        Assert.Equal("S1F18 <B 0x02>", await Quick(host, "S1F17 W"));
        Assert.Equal(1302, host.Next(6, 11).Item!.Items[1].GetInt64());
        release.Set();
    }

    [Fact]
    public async Task HostDelaysItsS1F14_DispatcherNotBlocked_OtherRepliesImmediate()
    {
        using var eq = Eq();
        using var host = await GemKit.Connect(eq, h => h.Hold(1, 13));
        Assert.True(host.Held.TryTake(out var held, 3000));                                 // 장비 S1F13의 응답을 주지 않음(T3까지 대기 가능)
        Assert.StartsWith("S1F14 <L [2] <B 0x00>", await Quick(host, "S1F13 W <L>"));
        await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.Communicating);
        Assert.StartsWith("S1F4", await Quick(host, "S1F3 W <L <U4 2001>>"));
        var sw = Stopwatch.StartNew();
        Assert.True(eq.TriggerEvent(5002));
        Assert.Equal(5002, host.NextOf(6, 11).Item!.Items[1].GetInt64());                  // 장비 S1F13이 아직 응답 대기여도 대기열은 계속 감
        Assert.True(sw.ElapsedMilliseconds < 1500, $"S6F11이 {sw.ElapsedMilliseconds}ms 뒤에 왔습니다(장비 S1F13의 T3까지 막힘).");
        held!.Reply(new SecsMessage(1, 14, false, SecsItem.L(SecsItem.B(0), SecsItem.L())));
    }
}
