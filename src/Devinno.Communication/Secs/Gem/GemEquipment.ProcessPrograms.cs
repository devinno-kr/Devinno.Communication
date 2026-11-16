using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

// 레시피 관리(언포맷 Process Program, S7, 6J.5): 켜기 설정, 표준 값 계산, 받기·앱 메서드
public partial class GemEquipment
{
    internal const int MaxProcessProgramLimit = 16_777_215;     // SecsItem.MaxLength

    IGemProcessProgramStore? ppStoreSetting;
    int maxProcessProgramSize = 4_194_304;

    // 아래는 레시피를 켠 실행(Start 때 ProcessProgramStore != null)에서만 쓴다
    bool processPrograms;                       // gate: Start 때 확정
    IGemProcessProgramStore? ppStore;           // 실행 중 저장소(Start 때 ppStoreSetting을 잡아 둠, 바뀌지 않음)
    readonly object ppLock = new();             // 저장소 호출을 하나씩. gate 안에서 잡지 않는다(순서 ppLock → gate)
    string? selectedPpid;                       // gate: 선택된 레시피(PPExecName)

    /// <summary>Where process programs (recipes) are kept (default: null = process programs off). Setting it turns process program management (S7) on at the next <see cref="Start"/>.</summary>
    /// <remarks>
    /// <para>When null, GEM behaves exactly as before: Stream 7 is not a supported stream (S9F3) and the application may handle it with <see cref="RegisterMessage"/>. When set, the standard variables and events PPExecName, PPFormat, PPChangeName, PPChangeStatus, ProcessProgramChange, and ProcessProgramSelected are defined and GEM handles Stream 7, so registering a Stream 7 message makes <see cref="Start"/> fail.</para>
    /// <para>Use <see cref="GemProcessProgramFolder"/> or your own <see cref="IGemProcessProgramStore"/>. A store set in code is not written by <see cref="ToXml"/> and is kept when loaded XML has no <c>ProcessProgramDirectory</c>; that attribute creates a <see cref="GemProcessProgramFolder"/> (and its absence turns a folder store from XML off).</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public IGemProcessProgramStore? ProcessProgramStore { get => ppStoreSetting; set => Setting(() => ppStoreSetting = value); }

    /// <summary>Upper limit of a process program body (the encoded PPBODY) in bytes, 1 to 16777215 (default: 4194304). A larger S7F1 is answered with PPGNT 5 and a larger S7F3 with ACKC7 2.</summary>
    /// <remarks>Over SECS-I a whole message is limited to about 8 MB, so a larger body is refused by the transport.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">Out of range</exception>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public int MaxProcessProgramSize { get => maxProcessProgramSize; set => Setting(() => maxProcessProgramSize = Check(value, 1, MaxProcessProgramLimit, nameof(MaxProcessProgramSize))); }

    const int S7F1 = 7 * 256 + 1, S7F3 = 7 * 256 + 3, S7F5 = 7 * 256 + 5, S7F17 = 7 * 256 + 17, S7F19 = 7 * 256 + 19;

    // gate: 이 연결에서 S7F1로 허락한 길이(PPID → LENGTH). 끊기거나 같은 PPID의 S7F3을 받으면 지운다
    readonly Dictionary<string, ulong> ppGrants = new(StringComparer.Ordinal);

    /// <summary>Raised on the application event thread when the Host asked to change the process program store (S7F1 inquire, S7F3 download, S7F17 delete) and GEM's own checks passed. When the handler returns, the reply uses <see cref="GemProcessProgramChangeEventArgs.Ack"/>; on 0 the store is changed.</summary>
    /// <remarks>
    /// <para>GEM answers first (the application is not called) for: an invalid PPID, a body larger than <see cref="MaxProcessProgramSize"/> or than the length granted by S7F1, overwriting or deleting the selected process program (ACKC7 1), and deleting a PPID that does not exist (ACKC7 4, nothing is deleted). S7F5 and S7F19 are answered from the store without this event.</para>
    /// <para>No subscriber = accept. A handler exception refuses the request (ACKC7 1, PPGNT 4) and is reported through <see cref="ProtocolError"/>. If the handler does not return within <see cref="CommandReplyTimeout"/>, GEM replies with the refusal on its behalf; a result that comes back later is discarded and the store is not changed (reported through <see cref="ProtocolError"/>). The store itself is written only after GEM has taken the right to reply, so a slow store delays the reply instead of being cut off by the deadline: what the Host is told always matches the store. The handler is called outside GEM's store lock, so it may wait for another thread that calls the process program methods.</para>
    /// <para>A change made by the Host does not raise the ProcessProgramChange event (E30 reserves it for the operator); call <see cref="TriggerEvent"/> yourself if your Host wants it.</para>
    /// </remarks>
    public event EventHandler<GemProcessProgramChangeEventArgs>? ProcessProgramChangeRequested;

    partial void AddProcessProgramHandlers()
    {
        standardValues[GemStandard.PPExecName] = () => SecsItem.A(selectedPpid ?? "");
        standardValues[GemStandard.PPFormat] = () => SecsItem.U1(1);
        // Stream 7은 레시피를 켰을 때만 지원 Stream이다(꺼져 있으면 OnPrimary가 S9F3)
        handlers[S7F1] = OnS7F1;
        handlers[S7F3] = OnS7F3;
        handlers[S7F5] = OnS7F5;
        handlers[S7F17] = OnS7F17;
        handlers[S7F19] = e => ServeProcessProgram(e, GemMessages.S7F20([]), () => GemMessages.S7F20(ppStore!.GetIds()));
    }

    partial void ResetProcessPrograms()
    {
        selectedPpid = null;
        ppGrants.Clear();
    }

    // 테스트용
    internal bool IsGateHeldForTest => Monitor.IsEntered(gate);

    #region 앱 메서드 (운전자 변경·선택, 장비 → Host 전송)
    /// <summary>Gets the PPID of the selected process program (PPExecName), or null if none is selected.</summary>
    public string? SelectedProcessProgram { get { lock (gate) return selectedPpid; } }

    /// <summary>The operator created or edited a process program at the equipment: writes it to the store and raises the ProcessProgramChange event (PPChangeName = PPID, PPChangeStatus 1 created or 2 edited).</summary>
    /// <remarks>The event is sent under the same rules as <see cref="TriggerEvent"/> (enabled, ON-LINE, communicating or spooled). The store is called on this thread.</remarks>
    /// <param name="program">The process program.</param>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Not started, or process programs are off (<see cref="ProcessProgramStore"/> is null).</exception>
    public void SaveProcessProgram(GemProcessProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        RequireProcessPrograms();
        lock (ppLock)
        {
            bool existed = ppStore!.Read(program.Ppid) != null;
            ppStore.Write(program);
            lock (gate) TriggerChangeLocked(program.Ppid, existed ? (byte)2 : (byte)1);
        }
    }

    /// <summary>The operator deleted a process program at the equipment: deletes it from the store and raises the ProcessProgramChange event (PPChangeStatus 3). Deleting the selected process program clears the selection (no ProcessProgramSelected event).</summary>
    /// <param name="ppid">PPID</param>
    /// <returns>true if it existed and was deleted (false: nothing happens).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ppid"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Not started, or process programs are off.</exception>
    public bool DeleteProcessProgram(string ppid)
    {
        ArgumentNullException.ThrowIfNull(ppid);
        RequireProcessPrograms();
        lock (ppLock)
        {
            if (!ppStore!.Delete(ppid)) return false;
            lock (gate)
            {
                if (selectedPpid == ppid) selectedPpid = null;
                TriggerChangeLocked(ppid, 3);
            }
            return true;
        }
    }

    /// <summary>Selects a process program for execution: updates PPExecName and raises the ProcessProgramSelected event. null clears the selection (no event).</summary>
    /// <remarks>The Host cannot overwrite or delete the selected process program (ACKC7 1). The Host's PP-SELECT remote command calls this after the application accepts it with HCACK 0.</remarks>
    /// <param name="ppid">PPID of a stored process program, or null.</param>
    /// <exception cref="ArgumentException">The store has no process program with that PPID.</exception>
    /// <exception cref="InvalidOperationException">Not started, or process programs are off.</exception>
    public void SelectProcessProgram(string? ppid)
    {
        RequireProcessPrograms();
        lock (ppLock)
        {
            if (ppid != null && ppStore!.Read(ppid) == null) throw new ArgumentException($"저장소에 없는 PPID {ppid}입니다.", nameof(ppid));
            lock (gate)
            {
                selectedPpid = ppid;
                if (ppid != null) TriggerLocked(catalog.Events[catalog.Std(GemStandard.ProcessProgramSelected)], null);
            }
        }
    }

    /// <summary>Sends a stored process program to the Host (equipment-initiated upload): S7F1 first when <paramref name="inquire"/> is true, then S7F3 if the Host grants it.</summary>
    /// <remarks>
    /// <para>The messages go through the GEM send queue in order with other GEM messages. Nothing is sent and the result says why when the PPID is not stored, the state is OFF-LINE, communication is not established, or the queue is full; process program transfers are never spooled. No <see cref="SendFailed"/> event is raised: the result carries the outcome.</para>
    /// <para>The task completes on the thread pool. <see cref="Stop"/> cancels it (<see cref="OperationCanceledException"/>). The token stops only a step not yet sent.</para>
    /// </remarks>
    /// <param name="ppid">PPID of a stored process program.</param>
    /// <param name="inquire">Send S7F1 before S7F3 (default true, recommended by E30).</param>
    /// <param name="cancellationToken">Cancels a step not yet sent.</param>
    /// <returns>The result (PPGNT, ACKC7, failure).</returns>
    /// <exception cref="ArgumentException"><paramref name="ppid"/> breaks the PPID rules of <see cref="GemProcessProgram"/>.</exception>
    /// <exception cref="InvalidOperationException">Not started, or process programs are off.</exception>
    public Task<GemUploadResult> UploadProcessProgramAsync(string ppid, bool inquire = true, CancellationToken cancellationToken = default)
    {
        try
        {
            GemProcessProgram.CheckPpid(ppid, nameof(ppid));
            RequireProcessPrograms();
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception ex) { return Task.FromException<GemUploadResult>(ex); }
        GemProcessProgram? program;
        lock (ppLock) program = ppStore!.Read(ppid);
        if (program == null) return Task.FromResult(new GemUploadResult(GemUploadFailure.NotFound));
        var tcs = new TaskCompletionSource<GemUploadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<GemUploadFailure?> dropped = f => { if (f is { } x) tcs.TrySetResult(new GemUploadResult(x)); else tcs.TrySetCanceled(); };

        void SendProgram(byte? ppgnt)
        {
            var failure = EnqueueTransferLocked(GemMessages.S7F3(program), (o, t) =>
            {
                if (!t.IsCompletedSuccessfully) { tcs.TrySetResult(new GemUploadResult(TransferFailure(t.Exception!.InnerException!), ppgnt, null, t.Exception.InnerException)); return; }
                try
                {
                    var ack = GemMessages.ParseS7F4(t.Result!);
                    tcs.TrySetResult(new GemUploadResult(ack == GemAck.Ackc7Accepted ? GemUploadFailure.None : GemUploadFailure.Rejected, ppgnt, ack));
                }
                catch (Exception ex) when (ex is FormatException or NullReferenceException or ArgumentException) { tcs.TrySetResult(new GemUploadResult(GemUploadFailure.InvalidReply, ppgnt, null, ex)); }
            }, dropped);
            if (failure is { } f) tcs.TrySetResult(new GemUploadResult(f, ppgnt));
        }

        lock (gate)
        {
            if (!inquire)
            {
                SendProgram(null);
                return tcs.Task;
            }
            var failure = EnqueueTransferLocked(GemMessages.S7F1(ppid, (uint)GemProcessProgram.DataLength(program.Body)), (o, t) =>
            {
                if (!t.IsCompletedSuccessfully) { tcs.TrySetResult(new GemUploadResult(TransferFailure(t.Exception!.InnerException!), null, null, t.Exception.InnerException)); return; }
                byte ppgnt;
                try { ppgnt = GemMessages.ParseS7F2(t.Result!); }
                catch (Exception ex) when (ex is FormatException or NullReferenceException or ArgumentException) { tcs.TrySetResult(new GemUploadResult(GemUploadFailure.InvalidReply, null, null, ex)); return; }
                if (ppgnt != GemAck.PpgntOk) { tcs.TrySetResult(new GemUploadResult(GemUploadFailure.Rejected, ppgnt)); return; }
                if (cancellationToken.IsCancellationRequested) { tcs.TrySetCanceled(cancellationToken); return; }
                lock (gate) SendProgram(ppgnt);
            }, dropped);
            if (failure is { } f) return Task.FromResult(new GemUploadResult(f));
        }
        return tcs.Task;
    }

    /// <summary>Asks the Host for a process program (equipment-initiated download, S7F5). The received process program is returned, not stored; store it with <see cref="SaveProcessProgram"/> if you want it.</summary>
    /// <remarks>The same queue, state, and threading rules as <see cref="UploadProcessProgramAsync"/> apply.</remarks>
    /// <param name="ppid">PPID</param>
    /// <param name="cancellationToken">Cancels the request if it has not been sent yet.</param>
    /// <returns>The process program, or null if the Host denied the request (an empty S7F6).</returns>
    /// <exception cref="ArgumentException"><paramref name="ppid"/> breaks the PPID rules of <see cref="GemProcessProgram"/>.</exception>
    /// <exception cref="InvalidOperationException">Not started, or process programs are off.</exception>
    /// <exception cref="GemProcessProgramRequestException">The request could not be sent or was not answered (see <see cref="GemProcessProgramRequestException.Failure"/>).</exception>
    public Task<GemProcessProgram?> RequestProcessProgramAsync(string ppid, CancellationToken cancellationToken = default)
    {
        try
        {
            GemProcessProgram.CheckPpid(ppid, nameof(ppid));
            RequireProcessPrograms();
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception ex) { return Task.FromException<GemProcessProgram?>(ex); }
        var tcs = new TaskCompletionSource<GemProcessProgram?>(TaskCreationOptions.RunContinuationsAsynchronously);
        GemUploadFailure? failure;
        lock (gate)
        {
            failure = EnqueueTransferLocked(GemMessages.S7F5(ppid), (o, t) =>
            {
                if (!t.IsCompletedSuccessfully) { tcs.TrySetException(new GemProcessProgramRequestException(TransferFailure(t.Exception!.InnerException!), t.Exception.InnerException)); return; }
                try
                {
                    var got = GemMessages.ParseS7F6(t.Result!);
                    tcs.TrySetResult(got is { } g ? new GemProcessProgram(g.Ppid, g.Body) : null);
                }
                catch (Exception ex) when (ex is FormatException or NullReferenceException or ArgumentException) { tcs.TrySetException(new GemProcessProgramRequestException(GemUploadFailure.InvalidReply, ex)); }
            }, f => { if (f is { } x) tcs.TrySetException(new GemProcessProgramRequestException(x)); else tcs.TrySetCanceled(); });
        }
        if (failure is { } fl) return Task.FromException<GemProcessProgram?>(new GemProcessProgramRequestException(fl));
        return tcs.Task;
    }

    // PP-SELECT 존재 검사(앱 이벤트 스레드, ppLock 안). 저장소 예외는 없는 것으로
    bool ProcessProgramExists(string? ppid)
    {
        if (ppid == null || !GemProcessProgram.IsValidPpid(ppid)) return false;
        lock (ppLock)
        {
            try { return ppStore!.Read(ppid) != null; }
            catch (Exception ex)
            {
                Error($"PP-SELECT: 레시피 저장소 예외: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }

    void RequireProcessPrograms()
    {
        Started();
        lock (gate) if (!processPrograms) throw new InvalidOperationException("레시피 기능이 꺼져 있습니다. ProcessProgramStore를 정해야 합니다.");
    }

    // 레시피 전송을 대기열에 넣는다(gate 안). 보낼 수 없으면 그 이유(아무것도 넣지 않음, SendFailed 없음)
    GemUploadFailure? EnqueueTransferLocked(SecsMessage message, Action<Outgoing, Task<SecsMessage?>> done, Action<GemUploadFailure?> dropped)
    {
        if (!running) return GemUploadFailure.NotCommunicating;
        if (commState != GemCommunicationState.Communicating) return GemUploadFailure.NotCommunicating;   // 통신이 먼저(OFF-LINE이어도 연결 전이면 이 이유)
        if (IsOffline(control)) return GemUploadFailure.Offline;
        if (sendQueue.Count >= maxPendingSends) return GemUploadFailure.QueueFull;
        sendQueue.Enqueue(new Outgoing(message, done) { Dropped = dropped });
        worker?.Post(Pump);
        return null;
    }

    static GemUploadFailure TransferFailure(Exception ex) => ex switch
    {
        SecsTimeoutException => GemUploadFailure.Timeout,
        SecsAbortException => GemUploadFailure.Aborted,
        SecsErrorException => GemUploadFailure.Error,
        _ => GemUploadFailure.Disconnected,
    };

    // ProcessProgramChange CE(gate 안): PPChangeName·PPChangeStatus는 이번 보고에만 쓰는 DV 값
    void TriggerChangeLocked(string ppid, byte status)
        => TriggerLocked(catalog.Events[catalog.Std(GemStandard.ProcessProgramChange)], new Dictionary<GemId, SecsItem>
        {
            [catalog.Std(GemStandard.PPChangeName)] = SecsItem.A(ppid),
            [catalog.Std(GemStandard.PPChangeStatus)] = SecsItem.U1(status),
        });
    #endregion

    #region 받기 (디스패처: 해석·미룸만, 저장소·앱 코드는 앱 이벤트 스레드에서 ppLock 안에)
    // S7F1 <L PPID LENGTH> → PPGNT. LENGTH = PPBODY 데이터 바이트 수
    void OnS7F1(SecsPrimaryEventArgs e)
    {
        var (ppid, length) = GemMessages.ParseS7F1(e.Message);
        ServeChange(e, GemAck.PpgntBusy, GemMessages.S7F2,
            check: () =>
            {
                if (!GemProcessProgram.IsValidPpid(ppid)) return GemMessages.S7F2(GemAck.PpgntInvalidPpid);
                if (length > (ulong)maxProcessProgramSize) return GemMessages.S7F2(GemAck.PpgntWillNotAccept);
                return null;
            },
            ask: () => new GemProcessProgramChangeEventArgs(GemProcessProgramChangeKind.Inquire, [ppid], null, length),
            apply: () =>
            {
                lock (gate) ppGrants[ppid] = length;
                return GemMessages.S7F2(GemAck.PpgntOk);
            });
    }

    // S7F3 <L PPID PPBODY> → ACKC7. S7F1 없이 온 것도 받는다
    void OnS7F3(SecsPrimaryEventArgs e)
    {
        var (ppid, body) = GemMessages.ParseS7F3(e.Message);
        GemProcessProgram? program = null;
        ServeChange(e, GemAck.Ackc7PermissionNotGranted, GemMessages.S7F4,
            check: () =>
            {
                ulong? granted;
                string? selected;
                lock (gate)
                {
                    granted = ppGrants.Remove(ppid, out var g) ? g : null;
                    selected = selectedPpid;
                }
                if (!GemProcessProgram.IsValidPpid(ppid)) return GemMessages.S7F4(GemAck.Ackc7PermissionNotGranted);
                if (SecsCodec.Encode(body).Length > maxProcessProgramSize || granted is { } max && (ulong)GemProcessProgram.DataLength(body) > max) return GemMessages.S7F4(GemAck.Ackc7LengthError);
                if (ppid == selected) return GemMessages.S7F4(GemAck.Ackc7PermissionNotGranted);
                program = new GemProcessProgram(ppid, body);
                return null;
            },
            ask: () => new GemProcessProgramChangeEventArgs(GemProcessProgramChangeKind.Download, [ppid], program, 0),
            apply: () =>
            {
                lock (gate) if (ppid == selectedPpid) return GemMessages.S7F4(GemAck.Ackc7PermissionNotGranted);   // 승인하는 사이 선택됨
                ppStore!.Write(program!);
                return GemMessages.S7F4(GemAck.Ackc7Accepted);
            });
    }

    // S7F5 PPID → S7F6 <L PPID PPBODY>, 없거나 잘못된 PPID면 <L>
    void OnS7F5(SecsPrimaryEventArgs e)
    {
        var ppid = GemMessages.ParseS7F5(e.Message);
        ServeProcessProgram(e, GemMessages.S7F6(null), () =>
            GemMessages.S7F6(GemProcessProgram.IsValidPpid(ppid) ? ppStore!.Read(ppid) : null));
    }

    // S7F17 <L PPID…> → ACKC7. 빈 목록 = 전부. 하나라도 문제가 있으면 아무것도 지우지 않는다
    void OnS7F17(SecsPrimaryEventArgs e)
    {
        var requested = GemMessages.ParseS7F17(e.Message);
        IReadOnlyList<string> ppids = [];
        SecsMessage? Check()
        {
            if (ppids.Any(p => !GemProcessProgram.IsValidPpid(p) || ppStore!.Read(p) == null)) return GemMessages.S7F18(GemAck.Ackc7PpidNotFound);
            string? selected;
            lock (gate) selected = selectedPpid;
            return selected != null && ppids.Contains(selected) ? GemMessages.S7F18(GemAck.Ackc7PermissionNotGranted) : null;
        }
        ServeChange(e, GemAck.Ackc7PermissionNotGranted, GemMessages.S7F18,
            check: () =>
            {
                ppids = requested.Count == 0 ? ppStore!.GetIds() : requested;
                return Check();
            },
            ask: () => new GemProcessProgramChangeEventArgs(GemProcessProgramChangeKind.Delete, [.. ppids], null, 0),
            apply: () =>
            {
                if (Check() is { } changed) return changed;                     // 승인하는 사이 바뀜(지워짐·선택됨)
                foreach (var p in ppids) ppStore!.Delete(p);
                return GemMessages.S7F18(GemAck.Ackc7Accepted);
            });
    }

    // 공통: W비트가 없으면 처리하지 않음. 미룬 응답으로 앱 이벤트 스레드에서 ppLock 안에 work를 하고 응답한다.
    // work의 예외(저장소)는 거절 응답(= 기한 응답과 같은 것) + ProtocolError
    void ServeProcessProgram(SecsPrimaryEventArgs e, SecsMessage refusal, Func<SecsMessage> work)
    {
        var m = e.Message;
        string what = $"S7F{m.Function}";
        if (!m.ReplyExpected)
        {
            Error($"W비트가 없는 {what}은(는) 결과를 돌려줄 수 없어 처리하지 않았습니다.");
            return;
        }
        var d = DeferReply(e, refusal, what);
        PostApp(() =>
        {
            SecsMessage reply;
            lock (ppLock)
            {
                try { reply = work(); }
                catch (Exception ex)
                {
                    reply = refusal;
                    ErrorIn(d.Run, $"{what}: 레시피 저장소 예외: {ex.GetType().Name}: {ex.Message}");
                }
            }
            FinishDeferred(d, reply);
        });
    }

    // 앱 승인이 필요한 요청(S7F1·S7F3·S7F17), 앱 이벤트 스레드에서:
    // ① ppLock 안에서 GEM 검사(거절이면 그 응답) ② ppLock 밖에서 앱 승인(핸들러가 다른 스레드의 레시피 메서드를 기다려도 막히지 않게)
    // ③ 응답할 권리를 얻은 뒤에만(기한이 지나 이미 거절했으면 아무것도 바꾸지 않음) ppLock 안에서 다시 검사하고 적용
    void ServeChange(SecsPrimaryEventArgs e, byte refusalAck, Func<byte, SecsMessage> replyOf, Func<SecsMessage?> check,
        Func<GemProcessProgramChangeEventArgs> ask, Func<SecsMessage> apply)
    {
        var m = e.Message;
        string what = $"S7F{m.Function}";
        var refusal = replyOf(refusalAck);
        if (!m.ReplyExpected)
        {
            Error($"W비트가 없는 {what}은(는) 결과를 돌려줄 수 없어 처리하지 않았습니다.");
            return;
        }
        var d = DeferReply(e, refusal, what);
        PostApp(() =>
        {
            SecsMessage? early;
            GemProcessProgramChangeEventArgs? args = null;
            lock (ppLock)
            {
                try
                {
                    early = check();
                    if (early == null) args = ask();
                }
                catch (Exception ex)
                {
                    early = refusal;
                    ErrorIn(d.Run, $"{what}: 레시피 저장소 예외: {ex.GetType().Name}: {ex.Message}");
                }
            }
            if (early != null) { FinishDeferred(d, early); return; }
            AskChange(args!, refusalAck, d.Run, out var ack);
            if (ack != 0) { FinishDeferred(d, replyOf(ack)); return; }
            if (!ClaimDeferred(d)) return;
            SecsMessage reply;
            lock (ppLock)
            {
                try { reply = apply(); }
                catch (Exception ex)
                {
                    reply = refusal;
                    ErrorIn(d.Run, $"{what}: 레시피 저장소 예외: {ex.GetType().Name}: {ex.Message}");
                }
            }
            ReplyNow(d.Args, reply, d.What);
        });
    }

    // 앱 승인(앱 이벤트 스레드, ppLock 밖). 구독자가 없으면 0. 핸들러 예외면 false + refusal
    bool AskChange(GemProcessProgramChangeEventArgs args, byte refusal, int run, out byte ack)
    {
        var handler = ProcessProgramChangeRequested;
        ack = 0;
        if (handler == null) return true;
        try
        {
            handler(this, args);
            ack = args.Ack;
            return true;
        }
        catch (Exception ex)
        {
            ack = refusal;
            ErrorIn(run, $"ProcessProgramChangeRequested 핸들러 예외로 거절합니다: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
    #endregion
}
