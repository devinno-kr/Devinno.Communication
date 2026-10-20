using System.Collections.Concurrent;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 앱 이벤트 대기열 상한(GemLoop maxPending, MaxPendingEvents)과 Stop 뒤 늦은 등록 메시지 응답
public class GemEventQueueTests
{
    [Fact]
    public async Task GemLoop_Bounded_DropsOldest_CountsDropped()
    {
        var loop = new GemLoop("test.Bounded", maxPending: 2);
        loop.Start();
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var ran = new ConcurrentQueue<int>();
        loop.Post(() => { entered.Set(); release.Wait(5000); });
        Assert.True(entered.Wait(3000));                                   // 스레드가 첫 일에서 멈춤
        for (int i = 1; i <= 5; i++)
        {
            int n = i;
            Assert.True(loop.Post(() => ran.Enqueue(n)));
        }
        Assert.Equal(3, loop.Dropped);                                     // 2개만 남고 오래된 1·2·3은 버림
        release.Set();
        await TestUtil.Until(() => ran.Count == 2);
        Assert.Equal([4, 5], ran);
        loop.Stop(1000);
        Assert.False(loop.Post(() => { }));
    }

    [Fact]
    public async Task GemLoop_Unbounded_ByDefault()
    {
        var loop = new GemLoop("test.Unbounded");
        loop.Start();
        using var release = new ManualResetEventSlim();
        var ran = new ConcurrentQueue<int>();
        loop.Post(() => release.Wait(5000));
        for (int i = 0; i < 100; i++) loop.Post(() => ran.Enqueue(0));
        release.Set();
        await TestUtil.Until(() => ran.Count == 100);
        Assert.Equal(0, loop.Dropped);
        loop.Stop(1000);
    }

    [Fact]
    public async Task Equipment_StuckHandler_EventQueueBounded_DropsOldest_ResetOnStart()
    {
        using var eq = GemKit.Equipment(e =>
        {
            GemKit.Sample(e);
            e.InitialControlState = GemControlState.OnlineRemote;
            e.MaxPendingEvents = 3;
        });
        Assert.Throws<ArgumentOutOfRangeException>(() => eq.MaxPendingEvents = 0);
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var seen = new ConcurrentQueue<GemControlState>();
        eq.ControlStateChanged += (s, e) => { seen.Enqueue(e.Current); entered.Set(); release.Wait(10000); };
        eq.Start();
        Assert.Throws<InvalidOperationException>(() => eq.MaxPendingEvents = 5);
        eq.SwitchLocal();                                                   // 첫 이벤트: 핸들러가 앱 이벤트 스레드를 붙잡음
        Assert.True(entered.Wait(3000));
        for (int i = 0; i < 5; i++) { eq.SwitchRemote(); eq.SwitchLocal(); }   // 10개 더 → 3개만 남음
        Assert.Equal(7, eq.DroppedEventCount);
        release.Set();
        await TestUtil.Until(() => seen.Count == 4);
        Assert.Equal([GemControlState.OnlineLocal, GemControlState.OnlineLocal, GemControlState.OnlineRemote, GemControlState.OnlineLocal], seen);
        eq.Stop();
        Assert.Equal(7, eq.DroppedEventCount);                              // Stop 뒤에도 읽음
        eq.Start();
        Assert.Equal(0, eq.DroppedEventCount);                              // Start마다 0부터
    }

    [Fact]
    public async Task Equipment_RegisteredReply_AfterStop_Rejected_NotSent()
    {
        using var eq = GemKit.Equipment(e =>
        {
            GemKit.Sample(e);
            e.InitialControlState = GemControlState.OnlineRemote;
            e.RegisterMessage(7, 19);
            e.CommandReplyTimeout = 10000;
        });
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var late = new ConcurrentQueue<string>();
        eq.MessageReceived += (s, e) =>
        {
            entered.Set();
            release.Wait(10000);
            try { e.Reply(new SecsMessage(7, 20, false)); }
            catch (Exception ex) { late.Enqueue($"{ex.GetType().Name}: {ex.Message}"); }
        };
        using var host = await GemKit.Communicating(eq);
        var ask = host.SendAsync("S7F19 W");
        Assert.True(entered.Wait(3000));
        var stopping = Task.Run(eq.Stop);                                   // 앱 이벤트 스레드가 붙잡혀 있어도 Stop은 끝남(최대 3초)
        await TestUtil.Until(() => !eq.IsStart);
        release.Set();
        await stopping;
        await Assert.ThrowsAnyAsync<Exception>(() => ask);                 // 연결이 끊겨 Host 쪽 요청은 실패
        await TestUtil.Until(() => late.Count == 1);
        Assert.StartsWith("InvalidOperationException", late.Single());
        Assert.Contains("다시 시작", late.Single());
    }
}
