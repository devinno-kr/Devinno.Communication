using System.Buffers.Binary;

namespace Devinno.Communication.Secs.Data;

// 트랜잭션·디스패처 (HSMS·SECS-I 공유, 상속 없이 각 전송 클래스가 하나씩 가짐).
// - 보낼 때: System Bytes를 매기고, 응답을 기다리는 1차 메시지는 대기 표에 넣고 T3를 잰다(전송이 끝난 뒤부터).
// - 받을 때: 응답(짝수 Function)은 대기 표와 짝짓고, S9Fx(MHEAD)는 해당 트랜잭션을 오류로 끝내고, 나머지 1차 메시지는 디스패처 스레드로 넘긴다.
// - Equipment만: 장치 ID 불일치 S9F1, 해석 실패 S9F7, 자기 T3 초과 S9F9, 크기 초과 S9F11.
// - 1차 메시지 핸들러가 응답하지 않으면 SxF0.
// 전송 계층은 연결되면 Connected(송신 함수), 끊기면 Disconnected()를 부르고, I/O 스레드에서 주기적으로 CheckTimeouts()를 부른다.
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

    sealed class Pending(SecsMessage primary)
    {
        public SecsMessage Primary { get; } = primary;
        public TaskCompletionSource<SecsMessage?> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
    readonly Queue<(SecsMessage? Message, byte[]? Header, int Generation, Action? Action)> queue = new();
    Thread? dispatcher;
    bool running;
    int runGeneration;      // queueLock으로 보호: Start마다 1씩 늘림(지난 실행의 정리가 새 실행을 멈추지 않게)

    // 연결되어 있는지(HSMS는 Selected)
    public bool IsConnected { get { lock (gate) return transmit != null; } }

    // 대기 중인 트랜잭션 수(테스트용)
    internal int PendingCount { get { lock (gate) return pending.Count; } }

    #region Lifecycle
    // 디스패처 스레드를 시작한다(전송 클래스의 Start에서). 이번 실행의 번호를 돌려준다(Stop의 startGen용).
    public int Start()
    {
        Thread t;
        int gen;
        lock (queueLock)
        {
            queue.Clear();
            running = true;
            gen = ++runGeneration;
            t = dispatcher = new Thread(Dispatch) { IsBackground = true, Name = name + ".PrimaryReceived" };
            // 지난 실행의 디스패처가 아직 기다리고 있으면(그 Stop이 아직 세션을 멈추지 못함) 깨워 끝낸다. 안 그러면 다음 Pulse 하나를
            // 그 스레드가 받고 그냥 끝나 버려, 새 디스패처가 그 알림(DeviceOpened 등)을 다음 Post 때까지 처리하지 못한다
            Monitor.PulseAll(queueLock);
        }
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
            queue.Enqueue((null, null, 0, action));
            Monitor.Pulse(queueLock);
        }
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
            p.Tcs.TrySetException(new SecsDisconnectedException($"S{p.Primary.Stream}F{p.Primary.Function}의 응답을 기다리는 중에 연결이 끊겼습니다."));
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

    // 보내기 시작: System Bytes를 매기고(응답을 기다리면 대기 표에 넣고) 송신 함수를 부른다
    (Pending? P, Task Sent) Begin(SecsMessage primary, CancellationToken token)
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
            if (primary.ReplyExpected)
            {
                p = new Pending(wire);
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
            if (pending.ContainsKey(p.Primary.SystemBytes)) p.Deadline = Environment.TickCount64 + t3();
        }
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
                if (pending.TryGetValue(msg.SystemBytes, out p) && p.Primary.Stream == msg.Stream && (msg.Function == 0 || msg.Function == p.Primary.Function + 1))
                    pending.Remove(msg.SystemBytes);
                else p = null;
            }
            if (p == null)
            {
                onError($"짝이 없는 응답을 버렸습니다: S{msg.Stream}F{msg.Function} System Bytes 0x{msg.SystemBytes:X8}");
                return;
            }
            if (msg.Function == 0) p.Tcs.TrySetException(new SecsAbortException(p.Primary, msg));
            else p.Tcs.TrySetResult(msg);
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
                if (pending.TryGetValue(sys, out p) && p.Primary.Stream == (h[2] & 0x7F) && p.Primary.Function == h[3]) pending.Remove(sys);
                else p = null;
            }
            if (p != null)
            {
                p.Tcs.TrySetException(new SecsErrorException(p.Primary, msg));
                return;
            }
        }

        int gen;
        lock (gate) gen = generation;
        lock (queueLock)
        {
            if (!running) return;
            queue.Enqueue((msg, header, gen, null));
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
            if (function % 2 != 0 || !pending.TryGetValue(systemBytes, out var p) || p.Primary.Stream != stream
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
        p.Tcs.TrySetException(new SecsTimeoutException(p.Primary, $"S{p.Primary.Stream}F{p.Primary.Function}: {reason}."));
        if (role() == SecsRole.Equipment) SendS9(9, headerOf(p.Primary));
    }
    #endregion

    #region Dispatch
    void Dispatch()
    {
        var me = Thread.CurrentThread;
        while (true)
        {
            (SecsMessage? Message, byte[]? Header, int Generation, Action? Action) x;
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
