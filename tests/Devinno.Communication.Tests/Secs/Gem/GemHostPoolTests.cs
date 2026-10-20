using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 스레드풀을 일부러 막는 테스트는 다른 테스트와 함께 돌지 않게 따로 둔다(xUnit은 DisableParallelization 컬렉션을 다른 것이 모두 끝난 뒤 혼자 실행)
[CollectionDefinition(nameof(GemHostPoolCollection), DisableParallelization = true)]
public sealed class GemHostPoolCollection { }

/// GemHost 동기 명령은 스레드풀에 기대지 않는다(ASP.NET처럼 풀이 고갈된 곳에서도 호출 스레드에서 끝남)
[Collection(nameof(GemHostPoolCollection))]
public class GemHostPoolTests
{
    [Fact]
    public async Task SyncCommands_CompleteWhileThreadPoolIsStarved()
    {
        var (eq, host) = await GemHostKit.Loopback(e => e.InitialControlState = GemControlState.OnlineRemote, h => h.Subscribe(5001, 2001));
        using var _ = eq;
        using var __ = host;
        Assert.Equal(SecsItem.F4(25), host.ReadStatus(2001).Single());                    // 준비 확인(풀이 정상일 때)

        IReadOnlyList<SecsItem>? values = null;
        GemReportSetupResult? reports = null;
        GemConnectResult? sequence = null;
        Exception? failure = null;
        var caller = new Thread(() =>
        {
            try
            {
                values = host.ReadStatus(2001);                                             // 명령 하나
                reports = host.ConfigureReports();                                          // 여러 메시지(동기 묻기로 돌린 몸통)
                sequence = host.RunConnectSequence();                                       // 접속 시퀀스(리포트 단계 포함)
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true, Name = "PoolTest.Caller" };

        int probe = 0;
        bool finished, probeRan;
        // Dispose하지 않는다(GC가 거둠): 풀이 순서대로(FIFO) 꺼낸다는 보장이 없어, 테스트가 끝난 뒤에야 시작하는 붙잡기 일이 있을 수 있다.
        // 그 일이 Dispose된 이벤트를 기다리면 ObjectDisposedException이 풀 스레드에서 처리되지 않아 테스트 호스트가 죽는다
        var release = new ManualResetEventSlim();
        ThreadPool.GetMinThreads(out int minWorkers, out int minIo);
        try
        {
            // 풀 스레드를 모두 붙잡고 그 뒤에 일을 잔뜩 쌓는다: 새로 늘어나는 풀 스레드도 앞의 일부터 집으므로 그 뒤에 넣은 일(probe)은 풀어 줄 때까지 실행되지 않는다
            for (int i = 0; i < minWorkers * 8 + 1000; i++) ThreadPool.UnsafeQueueUserWorkItem(_ => { if (!release.IsSet) release.Wait(30000); }, null);
            ThreadPool.UnsafeQueueUserWorkItem(_ => Volatile.Write(ref probe, 1), null);
            caller.Start();
            finished = caller.Join(10000);
            probeRan = Volatile.Read(ref probe) == 1;
        }
        finally { release.Set(); }                                                          // 전역 상태(풀) 원래대로

        Assert.True(finished, "스레드풀이 막힌 동안 동기 명령이 10초 안에 끝나지 않았습니다(풀에 기댐).");
        Assert.False(probeRan, "풀이 실제로 막히지 않아 시험이 의미 없습니다.");
        Assert.Null(failure);
        Assert.Equal(SecsItem.F4(25), values!.Single());
        Assert.True(reports!.Success);
        Assert.True(sequence!.Success, $"{sequence.FailedStep} {sequence.Ack} {sequence.Exception}");
        caller.Join();
        await TestUtil.Until(() => Volatile.Read(ref probe) == 1, 10000);                  // 풀이 다시 돈다
    }
}
