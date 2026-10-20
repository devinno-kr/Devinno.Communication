using System.Buffers.Binary;
using System.Diagnostics;

namespace Devinno.Communication.Secs.Data;

// 트랜잭션·디스패처 (HSMS·SECS-I 공유, 상속 없이 각 전송 클래스가 하나씩 가짐).
// - 보낼 때: System Bytes를 매기고, 응답을 기다리는 1차 메시지는 대기 표에 넣고 T3를 잰다(전송이 끝난 뒤부터).
// - 받을 때: 응답(짝수 Function)은 대기 표와 짝짓고, S9Fx(MHEAD)는 해당 트랜잭션을 오류로 끝내고, 나머지 1차 메시지는 디스패처 스레드로 넘긴다.
// - Equipment만: 장치 ID 불일치 S9F1, 해석 실패 S9F7, 자기 T3 초과 S9F9, 크기 초과 S9F11.
// - 1차 메시지 핸들러가 응답하지 않으면 SxF0.
// 전송 계층은 연결되면 Connected(송신 함수), 끊기면 Disconnected()를 부르고, I/O 스레드에서 주기적으로 CheckTimeouts()를 부른다.
// 송신 Task를 비동기로 끝내는 전송 계층(SECS-I)은 보내기를 끝낸 I/O 스레드에서 TransmitCompleted(wire)를 불러 T3를 그 순간부터 재게 한다.
// 순서 있는 완료(SendOrdered, GEM 내부용): 결과를 TCS 대신 디스패처 대기열(1차 메시지와 같은 대기열)에 넣어 와이어 순서로 알린다.
// 잠금 순서는 queueLock → gate 하나뿐: gate 안에서는 대기 표에서 꺼내기만 하고, 대기열에 넣기(queueLock)는 언제나 gate를 놓은 뒤에 한다.
internal sealed class SecsSession(
    Func<SecsRole> role,
    Func<int> deviceId,
    Func<int> t3,
    Func<SecsMessage, byte[]> headerOf,
    Action<SecsPrimaryEventArgs> onPrimary,
    Action<string> onError,
    string name)
{
    // 송신 함수: 메시지(System Bytes·장치 ID가 채워진 것)를 보내고, 보내기가 끝나면 완료되는 Task
    public delegate Task Transmit(SecsMessage wire, CancellationToken token);

    sealed class Pending(SecsMessage primary, Action<Task<SecsMessage?>>? ordered)
    {
        public SecsMessage Primary { get; } = primary;
        public TaskCompletionSource<SecsMessage?> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // 순서 있는 완료(SendOrdered)면 결과를 받을 콜백(TCS는 쓰지 않음). W비트가 없어도 대기 표에 들어간다(송신 완료·실패로 끝남)
        public readonly Action<Task<SecsMessage?>>? Ordered = ordered;
        // T3 기한(Environment.TickCount64). 보내기가 끝나기 전에는 MaxValue
        public long Deadline = long.MaxValue;
        // SECS-I: 응답 첫 블록을 받았음(이후는 T4가 맡음)
        public bool Started;
    }

    readonly object gate = new();
    readonly Dictionary<uint, Pending> pending = [];
    Transmit? transmit;
    int generation;
    uint systemBytes;
    // 디스패처 대기열: 스레드풀에 기대지 않도록 lock + Monitor로 깨운다(스레드풀이 바빠도 1차 메시지 전달이 늦어지지 않게)
    readonly object queueLock = new();
    // Ordered = 순서 있는 완료의 Action(Start가 비울 때도 버리지 않음)
    readonly Queue<(SecsMessage? Message, byte[]? Header, int Generation, Action? Action, bool Ordered)> queue = new();
    Thread? dispatcher;
    bool running;
    int runGeneration;      // queueLock으로 보호: Start마다 1씩 늘림(지난 실행의 정리가 새 실행을 멈추지 않게)

    // 연결되어 있는지(HSMS는 Selected)
    public bool IsConnected { get { lock (gate) return transmit != null; } }

    // 대기 중인 트랜잭션 수(테스트용)
    internal int PendingCount { get { lock (gate) return pending.Count; } }

    // 지금 스레드가 이 세션의 디스패처 스레드인지
    public bool IsDispatcherThread { get { lock (queueLock) return dispatcher == Thread.CurrentThread; } }

    #region Lifecycle
    // 디스패처 스레드를 시작한다(전송 클래스의 Start에서). 이번 실행의 번호를 돌려준다(Stop의 startGen용).
    public int Start()
    {
        Thread t;
        int gen;
        List<Action> ordered;
        lock (queueLock)
        {
            // 지난 실행이 남긴 순서 있는 완료는 버리지 않는다(지난 Stop이 번호 불일치로 건너뛰었을 수 있음). 이벤트 Action·1차 메시지는 지금처럼 버린다
            ordered = [.. queue.Where(q => q.Ordered).Select(q => q.Action!)];
            queue.Clear();
            running = true;
            gen = ++runGeneration;
            t = dispatcher = new Thread(Dispatch) { IsBackground = true, Name = name + ".PrimaryReceived" };
            // 지난 실행의 디스패처가 아직 기다리고 있으면(그 Stop이 아직 세션을 멈추지 못함) 깨워 끝낸다. 안 그러면 다음 Pulse 하나를
            // 그 스레드가 받고 그냥 끝나 버려, 새 디스패처가 그 알림(DeviceOpened 등)을 다음 Post 때까지 처리하지 못한다
            Monitor.PulseAll(queueLock);
        }
        // lock을 놓은 뒤 Start 스레드에서 순서대로(새 디스패처가 돌기 전에: 새 실행의 일보다 먼저)
        foreach (var a in ordered) { try { a(); } catch (Exception) { } }
        t.Start();
        return gen;
    }

    // 연결을 끊은 것으로 처리하고 디스패처를 멈춘다. 처리하지 못한 1차 메시지는 버린다. 최대 waitMs 기다린다.
    // startGen을 주면 그 번호의 실행일 때만 멈춘다(그 뒤에 다시 Start됐으면 아무것도 하지 않음).
    public void Stop(int waitMs, int? startGen = null)
    {
        Thread? t;
        List<Action> leftover;
        lock (queueLock)
        {
            if (startGen != null && startGen != runGeneration) return;
            Disconnected();   // 번호 검사와 같은 lock 안에서: 새 실행의 연결을 끊지 않게 (lock 순서는 queueLock → gate뿐이고 반대로 잡는 곳은 없음)
            t = dispatcher;
            dispatcher = null;
            running = false;
            leftover = [.. queue.Where(q => q.Action != null).Select(q => q.Action!)];
            queue.Clear();
            Monitor.PulseAll(queueLock);
        }
        if (t != null && t != Thread.CurrentThread) t.Join(waitMs);
        // Post로 맡긴 알림(DeviceClosed·ProtocolError 등)은 버리지 않고 멈추는 쪽 스레드에서 순서대로 마저 실행한다
        foreach (var a in leftover) { try { a(); } catch (Exception) { } }
    }

    // 디스패처 스레드에서 action을 실행한다(1차 메시지와 같은 대기열·순서, 연결 세대와 무관). 예외는 무시한다. 디스패처가 멈춰 있으면 버린다.
    internal void Post(Action action)
    {
        lock (queueLock)
        {
            if (!running) return;
            queue.Enqueue((null, null, 0, action, false));
            Monitor.Pulse(queueLock);
        }
    }

    // 순서 있는 완료를 디스패처 대기열에 넣는다. 디스패처가 멈춰 있으면 그 자리(완료시킨 스레드)에서 바로 실행한다. 예외는 무시한다.
    // Stop은 queueLock을 쥔 채 Disconnected를 부르므로 그때는 running이 아직 true → 대기열에 들어가 Stop의 leftover로 Stop 스레드에서 실행된다
    void PostCompletion(Action action)
    {
        Debug.Assert(!Monitor.IsEntered(gate));   // gate → queueLock 순서를 만들지 않는다
        lock (queueLock)
        {
            if (running)
            {
                queue.Enqueue((null, null, 0, action, true));
                Monitor.Pulse(queueLock);
                return;
            }
        }
        try { action(); } catch (Exception) { }
    }

    // 트랜잭션 하나를 끝낸다(완료 지점은 모두 여기로). 대기 표에서 꺼낸 쪽만, gate 밖에서 부른다(정확히 한 번)
    void Complete(Pending p, SecsMessage? reply, Exception? error)
    {
        if (p.Ordered == null)
        {
            if (error != null) p.Tcs.TrySetException(error);
            else p.Tcs.TrySetResult(reply);
            return;
        }
        var result = error != null ? Task.FromException<SecsMessage?>(error) : Task.FromResult(reply);
        var completed = p.Ordered;
        PostCompletion(() => completed(result));
    }

    // 새 연결(HSMS Selected, SECS-I 포트 열림). System Bytes 카운터를 처음부터 센다.
    public void Connected(Transmit tx)
    {
        lock (gate)
        {
            generation++;
            transmit = tx;
            systemBytes = 0;
        }
    }

    // 연결이 끊김. 대기 중인 트랜잭션은 모두 SecsDisconnectedException으로 끝낸다.
    public void Disconnected()
    {
        List<Pending> failed;
        lock (gate)
        {
            if (transmit == null) return;
            transmit = null;
            generation++;
            failed = [.. pending.Values];
            pending.Clear();
        }
        foreach (var p in failed)
            Complete(p, null, new SecsDisconnectedException(p.Primary.ReplyExpected
                ? $"S{p.Primary.Stream}F{p.Primary.Function}의 응답을 기다리는 중에 연결이 끊겼습니다."
                : $"S{p.Primary.Stream}F{p.Primary.Function}을(를) 보내는 중에 연결이 끊겼습니다."));
    }

    // 다음 System Bytes (0은 건너뜀)
    public uint NextSystemBytes()
    {
        lock (gate) return NextSystemBytesLocked();
    }

    uint NextSystemBytesLocked()
    {
        if (++systemBytes == 0) systemBytes = 1;
        return systemBytes;
    }
    #endregion

    #region Send
    // 연결 전·끊긴 뒤에 보내려 할 때의 InvalidOperationException 문장. 공개 형식은 그대로 두고, 안에서 이 문장으로 "연결 없음"을 구별한다(GemHost 자동 접속 시퀀스)
    internal const string NotConnectedMessage = "연결되어 있지 않습니다. HSMS는 Selected, SECS-I는 포트가 열린 뒤에 보낼 수 있습니다.";

    // 연결이 없어 보내지 못한 예외인지
    internal static bool IsNotConnected(Exception ex) => ex.GetType() == typeof(InvalidOperationException) && ex.Message == NotConnectedMessage;

    // 보내기 시작: System Bytes를 매기고(응답을 기다리거나 ordered면 대기 표에 넣고) 송신 함수를 부른다
    (Pending? P, Task Sent) Begin(SecsMessage primary, CancellationToken token, Action<Task<SecsMessage?>>? ordered = null)
    {
        ArgumentNullException.ThrowIfNull(primary);
        if (primary.Function % 2 == 0)
            throw new ArgumentException($"1차 메시지는 홀수 Function이어야 합니다. 현재: S{primary.Stream}F{primary.Function}", nameof(primary));
        token.ThrowIfCancellationRequested();

        Transmit tx;
        SecsMessage wire;
        Pending? p = null;
        lock (gate)
        {
            tx = transmit ?? throw new InvalidOperationException(NotConnectedMessage);
            wire = primary.WithHeader(NextSystemBytesLocked(), deviceId());
            if (primary.ReplyExpected || ordered != null)
            {
                p = new Pending(wire, ordered);
                pending[wire.SystemBytes] = p;
            }
        }
        try { return (p, tx(wire, token)); }
        catch
        {
            if (p != null) Take(p);
            throw;
        }
    }

    // 보내기가 끝난 뒤: 실패면 대기 표에서 빼고, 성공이면 T3를 시작한다
    void Sent(Pending? p, Task sent)
    {
        if (!sent.IsCompletedSuccessfully)
        {
            if (p != null) Take(p);
            sent.GetAwaiter().GetResult();   // 실패 예외를 그대로 던진다
        }
        if (p == null) return;
        lock (gate)
        {
            // 전송 계층이 TransmitCompleted로 이미 T3를 시작했으면 그대로 둔다
            if (pending.ContainsKey(p.Primary.SystemBytes) && p.Deadline == long.MaxValue) p.Deadline = Environment.TickCount64 + t3();
        }
    }

    // 전송 계층(I/O 스레드)이 보내기를 끝낸 순간에 T3를 시작한다(SECS-I: 마지막 블록의 ACK).
    // 송신 Task가 비동기로 끝나면 Sent는 스레드풀에서 늦게 돌 수 있어(스레드풀이 바쁘면 응답보다 늦게) 그때부터 재면 T3가 늦게 시작되거나 아예 시작되지 않는다.
    // wire는 송신 함수가 받은 바로 그 객체(응답·S9는 대기 표의 1차 메시지와 System Bytes가 겹칠 수 있어 참조로 확인)
    // W비트 없는 ordered 송신이면 여기서 null로 끝낸다
    public void TransmitCompleted(SecsMessage wire)
    {
        Pending? done = null;
        lock (gate)
        {
            if (pending.TryGetValue(wire.SystemBytes, out var p) && ReferenceEquals(p.Primary, wire))
            {
                if (p.Primary.ReplyExpected) { if (p.Deadline == long.MaxValue) p.Deadline = Environment.TickCount64 + t3(); }
                else if (p.Ordered != null) { pending.Remove(wire.SystemBytes); done = p; }
            }
        }
        if (done != null) Complete(done, null, null);
    }

    // 전송 계층(I/O 스레드)이 송신 실패를 알린다. 그 wire(참조)의 ordered 트랜잭션이면 error로 끝낸다. ordered가 아니면 아무것도 하지 않는다(송신 Task 쪽이 맡음)
    public void TransmitFailed(SecsMessage wire, Exception error)
    {
        Pending? p;
        lock (gate)
        {
            if (!pending.TryGetValue(wire.SystemBytes, out p) || !ReferenceEquals(p.Primary, wire) || p.Ordered == null) return;
            pending.Remove(wire.SystemBytes);
        }
        Complete(p, null, error);
    }

    // 동기 보내기. 스레드풀을 거치지 않고 송신 완료·응답 TaskCompletionSource를 직접 기다린다.
    public SecsMessage? Send(SecsMessage primary)
    {
        var (p, sent) = Begin(primary, CancellationToken.None);
        try { sent.Wait(); } catch (AggregateException) { }
        Sent(p, sent);
        return p?.Tcs.Task.GetAwaiter().GetResult();
    }

    public async Task<SecsMessage?> SendAsync(SecsMessage primary, CancellationToken token)
    {
        var (p, sent) = Begin(primary, token);
        try { await sent.ConfigureAwait(false); } catch (Exception) { }
        Sent(p, sent);
        if (p == null) return null;
        using (token.Register(() => { if (Take(p)) p.Tcs.TrySetCanceled(token); }))
            return await p.Tcs.Task.ConfigureAwait(false);
    }

    // 순서 있는 완료로 보낸다(GEM 내부용). 검사·동기 예외는 SendAsync와 같고, 동기 예외면 completed는 불리지 않는다.
    // 그 밖에는 completed가 정확히 한 번, 끝난 Task로 불린다(응답 / W 없으면 송신 성공 때 null / 예외). 디스패처가 돌면 디스패처에서(와이어 순서)
    public void SendOrdered(SecsMessage primary, Action<Task<SecsMessage?>> completed)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(completed);
        var (p, sent) = Begin(primary, CancellationToken.None, completed);
        if (!sent.IsCompleted)
        {
            // SECS-I: 완료는 I/O 스레드의 TransmitCompleted/TransmitFailed가 한다(스레드풀을 기다리지 않음). 예외는 관찰만
            sent.ContinueWith(static x => _ = x.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return;
        }
        if (!sent.IsCompletedSuccessfully)
        {
            if (Take(p!)) Complete(p!, null, sent.IsFaulted ? sent.Exception!.InnerException! : new TaskCanceledException(sent));
            return;
        }
        if (primary.ReplyExpected)
        {
            lock (gate)
            {
                // 이미 끝났거나(응답이 먼저 옴) TransmitCompleted로 T3를 시작했으면 그대로 둔다
                if (pending.TryGetValue(p!.Primary.SystemBytes, out var cur) && cur == p && p.Deadline == long.MaxValue) p.Deadline = Environment.TickCount64 + t3();
            }
        }
        else if (Take(p!)) Complete(p!, null, null);
    }

    // 대기 표에서 뺀다. 이미 빠졌으면 false
    bool Take(Pending p)
    {
        lock (gate)
        {
            if (!pending.TryGetValue(p.Primary.SystemBytes, out var cur) || cur != p) return false;
            pending.Remove(p.Primary.SystemBytes);
            return true;
        }
    }

    // S9Fx 자동 송신 (보내기 결과는 기다리지 않음)
    void SendS9(int function, byte[] header)
    {
        Transmit? tx;
        SecsMessage wire;
        lock (gate)
        {
            tx = transmit;
            if (tx == null) return;
            wire = new SecsMessage(9, function, false, SecsItem.B(header)).WithHeader(NextSystemBytesLocked(), deviceId());
        }
        Forget(tx, wire);
    }

    static void Forget(Transmit tx, SecsMessage wire)
    {
        try
        {
            var t = tx(wire, CancellationToken.None);
            if (!t.IsCompleted) t.ContinueWith(static x => _ = x.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            else _ = t.Exception;
        }
        catch (Exception) { }
    }
    #endregion

    #region Receive
    // 받은 메시지 하나(완전히 해석된 것). header = 받은 10바이트 헤더(S9 MHEAD용)
    public void Received(SecsMessage msg, byte[] header)
    {
        if (role() == SecsRole.Equipment && msg.DeviceID != deviceId())
        {
            SendS9(1, header);
            onError($"장치 ID가 다른 메시지를 받아 S9F1을 보냈습니다: S{msg.Stream}F{msg.Function} 장치 ID {msg.DeviceID} (내 장치 ID {deviceId()})");
            if (msg.Function % 2 == 0) ReplyAbandoned(msg.SystemBytes, "응답의 장치 ID가 다릅니다");   // SECS-I: 이미 첫 블록을 받은 응답이면 T3로 끝낼 수 없으므로 여기서 끝낸다
            return;
        }

        if (msg.Function % 2 == 0)
        {
            Pending? p;
            lock (gate)
            {
                // W 없는 ordered 항목(송신 완료 대기)은 응답과 짝짓지 않는다
                if (pending.TryGetValue(msg.SystemBytes, out p) && p.Primary.ReplyExpected && p.Primary.Stream == msg.Stream && (msg.Function == 0 || msg.Function == p.Primary.Function + 1))
                    pending.Remove(msg.SystemBytes);
                else p = null;
            }
            if (p == null)
            {
                onError($"짝이 없는 응답을 버렸습니다: S{msg.Stream}F{msg.Function} System Bytes 0x{msg.SystemBytes:X8}");
                return;
            }
            if (msg.Function == 0) Complete(p, null, new SecsAbortException(p.Primary, msg));
            else Complete(p, msg, null);
            return;
        }

        // S9F1/3/5/7/11의 MHEAD는 상대가 받은 "내" 메시지 헤더 → 그 트랜잭션을 오류로 끝낸다(S9F9의 SHEAD는 상대 자신의 트랜잭션이라 제외)
        if (msg.Stream == 9 && msg.Function is 1 or 3 or 5 or 7 or 11 && msg.Item is { Format: SecsFormat.Binary, Count: 10 } mhead)
        {
            var h = mhead.GetBytes();
            uint sys = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(6));
            Pending? p;
            lock (gate)
            {
                if (pending.TryGetValue(sys, out p) && p.Primary.ReplyExpected && p.Primary.Stream == (h[2] & 0x7F) && p.Primary.Function == h[3]) pending.Remove(sys);
                else p = null;
            }
            if (p != null)
            {
                Complete(p, null, new SecsErrorException(p.Primary, msg));
                return;
            }
        }

        int gen;
        lock (gate) gen = generation;
        lock (queueLock)
        {
            if (!running) return;
            queue.Enqueue((msg, header, gen, null, false));
            Monitor.Pulse(queueLock);
        }
    }

    // 본문을 해석하지 못함 (Equipment면 S9F7)
    public void DecodeFailed(byte[] header, string reason)
    {
        if (role() == SecsRole.Equipment) SendS9(7, header);
        onError($"메시지 본문을 해석하지 못했습니다{(role() == SecsRole.Equipment ? "(S9F7 보냄)" : "")}: {reason}");
    }

    // 최대 크기를 넘는 메시지를 버림 (Equipment면 S9F11)
    public void TooLong(byte[] header, long length)
    {
        if (role() == SecsRole.Equipment) SendS9(11, header);
        onError($"최대 크기를 넘는 메시지({length}바이트)를 버렸습니다{(role() == SecsRole.Equipment ? "(S9F11 보냄)" : "")}.");
    }

    // SECS-I: 응답의 첫 블록이 왔음 → T3를 멈춘다. 대기 중인 트랜잭션의 응답이면 true
    public bool ReplyStarted(uint systemBytes, int stream, int function)
    {
        lock (gate)
        {
            if (function % 2 != 0 || !pending.TryGetValue(systemBytes, out var p) || !p.Primary.ReplyExpected || p.Primary.Stream != stream
                || (function != 0 && function != p.Primary.Function + 1)) return false;
            p.Started = true;
            return true;
        }
    }

    // SECS-I: 조립 중인 응답을 버렸음(블록 사이 T4 초과, 다른 메시지 블록) → 트랜잭션을 타임아웃으로 끝낸다
    public void ReplyAbandoned(uint systemBytes, string reason)
    {
        Pending? p;
        lock (gate)
        {
            if (!pending.TryGetValue(systemBytes, out p) || !p.Started) return;
            pending.Remove(systemBytes);
        }
        Expire(p, reason);
    }

    // T3가 지난 트랜잭션을 끝낸다(I/O 스레드에서 주기적으로)
    public void CheckTimeouts()
    {
        List<Pending>? expired = null;
        long now = Environment.TickCount64;
        lock (gate)
        {
            foreach (var p in pending.Values)
                if (!p.Started && p.Deadline <= now) (expired ??= []).Add(p);
            if (expired != null) foreach (var p in expired) pending.Remove(p.Primary.SystemBytes);
        }
        if (expired != null) foreach (var p in expired) Expire(p, $"응답을 T3({t3()}ms) 안에 받지 못했습니다");
    }

    void Expire(Pending p, string reason)
    {
        Complete(p, null, new SecsTimeoutException(p.Primary, $"S{p.Primary.Stream}F{p.Primary.Function}: {reason}."));
        if (role() == SecsRole.Equipment) SendS9(9, headerOf(p.Primary));
    }
    #endregion

    #region Dispatch
    void Dispatch()
    {
        var me = Thread.CurrentThread;
        while (true)
        {
            (SecsMessage? Message, byte[]? Header, int Generation, Action? Action, bool Ordered) x;
            lock (queueLock)
            {
                while (running && dispatcher == me && queue.Count == 0) Monitor.Wait(queueLock);
                if (!running || dispatcher != me) return;
                x = queue.Dequeue();
            }
            if (x.Action != null) { try { x.Action(); } catch (Exception) { } }
            else Deliver(x.Message!, x.Header!, x.Generation);
        }
    }

    void Deliver(SecsMessage msg, byte[] header, int gen)
    {
        var args = new SecsPrimaryEventArgs(msg, header, reply => SendReply(msg, reply, gen));
        try { onPrimary(args); } catch (Exception) { }
        if (args.Close() && msg.ReplyExpected)
        {
            try { SendReply(msg, new SecsMessage(msg.Stream, 0, false), gen); } catch (Exception) { }
        }
    }

    // 응답: 1차 메시지와 같은 System Bytes·장치 ID. 1차 메시지를 받은 연결이 아니면 보내지 않는다.
    void SendReply(SecsMessage primary, SecsMessage reply, int gen)
    {
        Transmit tx;
        lock (gate)
        {
            if (transmit == null || gen != generation)
                throw new SecsDisconnectedException($"S{primary.Stream}F{primary.Function}을(를) 받은 연결이 끊겨 응답을 보낼 수 없습니다.");
            tx = transmit;
        }
        tx(reply.WithHeader(primary.SystemBytes, primary.DeviceID), CancellationToken.None).GetAwaiter().GetResult();
    }
    #endregion
}
