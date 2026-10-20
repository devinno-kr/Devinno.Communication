namespace Devinno.Communication.Tests.Fakes;

/// 스레드풀을 일부러 막는다(GemHostPoolTests와 같은 방식). using 블록 안에서는 풀 일이 돌지 않는다.
/// 쓰는 테스트 클래스는 [Collection(nameof(GemHostPoolCollection))]에 두고, 막힌 구간 안에서는 await하지 않는다(sync [Fact] 또는 전용 스레드).
internal sealed class PoolStarver : IDisposable
{
    // Dispose하지 않는다(GC가 거둠): 풀이 순서대로(FIFO) 꺼낸다는 보장이 없어, 풀어 준 뒤에야 시작하는 붙잡기 일이 있을 수 있다.
    // 그 일이 Dispose된 이벤트를 기다리면 ObjectDisposedException이 풀 스레드에서 처리되지 않아 테스트 호스트가 죽는다
    readonly ManualResetEventSlim release = new();
    int probe;

    PoolStarver() { }

    /// 풀 뒤에 넣은 probe 일이 돌았는지(막혀 있었다면 false)
    public bool ProbeRan => Volatile.Read(ref probe) == 1;

    public static PoolStarver Start()
    {
        var s = new PoolStarver();
        ThreadPool.GetMinThreads(out int minWorkers, out _);
        // 풀 스레드를 모두 붙잡고 그 뒤에 일을 잔뜩 쌓는다: 새로 늘어나는 풀 스레드도 앞의 일부터 집으므로 그 뒤에 넣은 일(probe)은 풀어 줄 때까지 실행되지 않는다
        for (int i = 0; i < minWorkers * 8 + 1000; i++)
            ThreadPool.UnsafeQueueUserWorkItem(static st => { var r = (ManualResetEventSlim)st!; if (!r.IsSet) r.Wait(30000); }, s.release);
        ThreadPool.UnsafeQueueUserWorkItem(static st => Volatile.Write(ref ((PoolStarver)st!).probe, 1), s);
        return s;
    }

    /// 풀어 준다(전역 상태(풀) 원래대로)
    public void Dispose() => release.Set();
}
