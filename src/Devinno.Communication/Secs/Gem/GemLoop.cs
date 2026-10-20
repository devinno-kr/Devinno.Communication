namespace Devinno.Communication.Secs.Gem;

// 전용 스레드 하나가 넣은 순서대로 일을 실행한다(GEM 작업 스레드·앱 이벤트 스레드). 스레드풀을 쓰지 않는다(lock + Monitor).
// tick이 있으면 일이 없어도 tickMs마다, 그리고 일을 하나 끝낼 때마다 부른다(타이머·기한 검사). 일과 tick의 예외는 삼킨다.
// maxPending이 0보다 크면 대기열 상한: 가득 찬 때 넣으면 가장 오래된 일을 버리고 Dropped를 늘린다(핸들러가 멈춰도 메모리가 끝없이 늘지 않게).
internal sealed class GemLoop(string name, Action? tick = null, int tickMs = 20, int maxPending = 0)
{
    readonly object sync = new();
    readonly Queue<Action> queue = new();
    Thread? thread;
    bool running;
    long dropped;

    // 지금 스레드가 이 루프의 스레드인지
    public bool IsCurrentThread { get { lock (sync) return thread == Thread.CurrentThread; } }

    // 상한 때문에 버린 일의 수
    public long Dropped => Interlocked.Read(ref dropped);

    public void Start()
    {
        Thread t;
        lock (sync)
        {
            queue.Clear();
            running = true;
            t = thread = new Thread(Run) { IsBackground = true, Name = name };
        }
        t.Start();
    }

    // 일을 넣는다. 멈춰 있으면 버리고 false. 상한이 차 있으면 가장 오래된 일을 버리고 넣는다(true)
    public bool Post(Action action)
    {
        lock (sync)
        {
            if (!running) return false;
            if (maxPending > 0 && queue.Count >= maxPending)
            {
                queue.Dequeue();
                Interlocked.Increment(ref dropped);
            }
            queue.Enqueue(action);
            Monitor.Pulse(sync);
            return true;
        }
    }

    // 멈춘다. 남은 일은 버린다. 이 루프의 스레드 안에서 부르면 기다리지 않는다(지금 일이 끝나면 멈춤).
    public void Stop(int waitMs)
    {
        Thread? t;
        lock (sync)
        {
            running = false;
            queue.Clear();
            t = thread;
            thread = null;
            Monitor.PulseAll(sync);
        }
        if (t != null && t != Thread.CurrentThread) t.Join(waitMs);
    }

    void Run()
    {
        var me = Thread.CurrentThread;
        while (true)
        {
            Action? a = null;
            lock (sync)
            {
                if (running && thread == me && queue.Count == 0)
                {
                    if (tick == null) { while (running && thread == me && queue.Count == 0) Monitor.Wait(sync); }
                    else Monitor.Wait(sync, tickMs);
                }
                if (!running || thread != me) return;
                if (queue.Count > 0) a = queue.Dequeue();
            }
            if (a != null) { try { a(); } catch (Exception) { } }
            if (tick != null) { try { tick(); } catch (Exception) { } }
        }
    }
}
