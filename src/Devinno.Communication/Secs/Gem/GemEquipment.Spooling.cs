using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

// 스풀링(E30 7.12): 켜기 설정·공개 상태·표준 값 계산, 모으기(활성화·버림 경로·가득 참)와 S2F43, 내보내기·지우기(S6F23)와 재시작 복원
public partial class GemEquipment
{
    string? spoolDirectory;
    int maxSpoolMessages = 1000;

    // 아래 상태는 모두 gate 보호. 스풀링을 켠 실행(Start 때 SpoolDirectory != null)에서만 쓰인다
    GemSpoolStore? spoolStore;
    bool spooling;                                  // Start 때 확정: 이번 실행에서 스풀링이 켜져 있는가
    GemSpoolState spoolState;
    bool spoolFull;
    uint spoolTotal;
    DateTime? spoolStartTime, spoolFullTime;        // GEM 시계 시각(SpoolClockLocked)을 UTC로 저장. GemTime.Format이 로컬로 되돌린다. 값은 반드시 SpoolClockLocked로만 넣는다
    readonly LinkedList<(long Seq, SecsMessage Message)> spool = new();
    long nextSeq;
    // 활성화 알림(SpoolStateChanged)을 아직 내지 않은 동안 true: 그 사이의 가득 참은 활성화 알림 하나에 함께 싣는다
    bool spoolActivating;
    // Host가 고른 스풀 대상(Stream → 함수 집합, null = 그 Stream 전부)
    readonly Dictionary<byte, HashSet<byte>?> spoolSelection = [];
    // 마지막으로 받아들인 S6F23 RSDC 0 뒤에 내보내기를 끝낸(스풀에서 지운) 수. MaxSpoolTransmit와 비교한다
    int transmittedSinceRequest;
    // 보냈지만 결과 처리(SpoolDone)가 아직 끝나지 않은 스풀 머리의 seq. Completed가 inFlight를 비운 뒤 SpoolDone이 지우기 전 사이에
    // 작업 스레드의 Pump가 같은 머리를 다시 보내거나 덮어쓰기가 그것을 지우지 않게 한다. SpoolDone(T3 제외)과 ClearQueueLocked(버린 송신)가 비운다
    long? spoolSending;

    /// <summary>Folder where spooled messages are kept (default: null = spooling off). Setting it turns spooling on at the next <see cref="Start"/>.</summary>
    /// <remarks>
    /// <para>When null, GEM behaves exactly as before. When set, the standard spool variables, constants, and events (SpoolCountActual and so on) are defined, and S2F43 and S6F23 are handled by GEM, so they cannot be registered with <see cref="RegisterMessage"/>.</para>
    /// <para>The folder is created if needed and is locked by one equipment at a time: <see cref="Start"/> throws <see cref="IOException"/> if another equipment (or process) is using it.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">An empty string</exception>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public string? SpoolDirectory
    {
        get => spoolDirectory;
        set => Setting(() => spoolDirectory = value is { Length: 0 } ? throw new ArgumentException("SpoolDirectory는 빈 문자열일 수 없습니다(끄려면 null).", nameof(SpoolDirectory)) : value);
    }

    /// <summary>Upper limit of the number of spooled messages, 1 to 1000000 (default: 1000). It applies only when <see cref="SpoolDirectory"/> is set.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Out of range</exception>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public int MaxSpoolMessages { get => maxSpoolMessages; set => Setting(() => maxSpoolMessages = Check(value, 1, 1000000, nameof(MaxSpoolMessages))); }

    /// <summary>Gets the current spooling state (<see cref="GemSpoolState.Inactive"/> when spooling is off)</summary>
    public GemSpoolState SpoolState { get { lock (gate) return spoolState; } }
    /// <summary>Gets whether the spool is full (false when spooling is off)</summary>
    public bool IsSpoolFull { get { lock (gate) return spoolFull; } }
    /// <summary>Gets the number of messages in the spool (0 when spooling is off)</summary>
    public int SpoolCount { get { lock (gate) return spool.Count; } }

    /// <summary>Raised on the application event thread when the spooling state or the full flag changed (for example, spooling was activated or the spool became full). A change of the message count alone does not raise it.</summary>
    public event EventHandler<GemSpoolStateEventArgs>? SpoolStateChanged;

    // 표준 EC의 현재 값(gate 안)
    bool EnableSpoolingLocked => values[catalog.Std(GemStandard.EnableSpooling)].GetBooleans()[0];
    uint MaxSpoolTransmitLocked => (uint)values[catalog.Std(GemStandard.MaxSpoolTransmit)].GetUInt64();
    bool OverWriteSpoolLocked => values[catalog.Std(GemStandard.OverWriteSpool)].GetBooleans()[0];

    partial void AddSpoolingHandlers()
    {
        standardValues[GemStandard.SpoolCountActual] = () => SecsItem.U4((uint)spool.Count);
        standardValues[GemStandard.SpoolCountTotal] = () => SecsItem.U4(spoolTotal);
        standardValues[GemStandard.SpoolFullTime] = () => SpoolTimeItem(spoolFullTime);
        standardValues[GemStandard.SpoolStartTime] = () => SpoolTimeItem(spoolStartTime);
        handlers[2 * 256 + 43] = OnS2F43;          // 스풀링을 켰을 때만 처리(꺼져 있으면 OnPrimary가 S9F5)
        handlers[6 * 256 + 23] = OnS6F23;
    }

    // GEM 시계(시스템 시각 + S2F31 오프셋)의 현재 시각을 UTC로(gate 안). 재시작 뒤에도 안전하게 저장된다
    DateTime SpoolClockLocked() => (DateTime.Now + clockOffset).ToUniversalTime();
    internal DateTime SpoolClock { get { lock (gate) return SpoolClockLocked(); } }

    SecsItem SpoolTimeItem(DateTime? t) => SecsItem.A(t is { } v ? GemTime.Format(v, TimeFormatLocked) : "");

    // Start 때 상태 초기화(gate 안, ResetClock 뒤). 스풀링을 켰으면 저장소가 Open 때 읽어 둔 상태·메시지로 복원한다(전이 7·8, 메모리만):
    // 메시지가 있으면 상태 파일이 없거나 INACTIVE여도 ACTIVE(메시지가 이김), ACTIVE면 UNLOAD는 늘 NO OUTPUT, SpoolCountTotal = max(저장값, 복원 수),
    // nextSeq = 마지막 seq + 1(새 추가가 복원한 파일을 덮어쓰지 않게). MaxSpoolMessages보다 많으면 오래된 것부터 남기고 넘는 새 것은 지운다(가득 참). 알림 없음
    partial void ResetSpooling()
    {
        spoolState = GemSpoolState.Inactive;
        spoolFull = false;
        spoolTotal = 0;
        spoolStartTime = spoolFullTime = null;
        spool.Clear();
        nextSeq = 1;
        spoolActivating = false;
        spoolSelection.Clear();
        transmittedSinceRequest = 0;
        spoolSending = null;
        if (spoolStore is not { } store) return;

        var snap = store.State;
        var msgs = store.Messages;
        if (snap != null)
        {
            spoolTotal = snap.CountTotal;
            spoolStartTime = snap.StartTime;
            spoolFullTime = snap.FullTime;
            foreach (var (stream, fns) in snap.Selection)
                spoolSelection[stream] = fns.Count == 0 ? null : [.. fns];
        }
        foreach (var (seq, m) in msgs)
        {
            if (spool.Count < maxSpoolMessages) spool.AddLast((seq, m));
            else store.Remove(seq);
        }
        if (msgs.Count > 0) nextSeq = msgs[^1].Seq + 1;
        if (snap?.Active == true || msgs.Count > 0)
        {
            spoolState = GemSpoolState.NoOutput;
            spoolFull = snap?.Full == true;
            if (msgs.Count > maxSpoolMessages && !spoolFull)
            {
                spoolFull = true;
                spoolFullTime = SpoolClockLocked();
            }
            spoolTotal = Math.Max(spoolTotal, (uint)msgs.Count);
            if (snap?.Active != true || spoolFull != snap.Full) SaveSpoolStateLocked();
        }
    }

    // 스풀링 상태를 바꾸고 알린다(gate 안)
    void SetSpoolStateLocked(GemSpoolState next)
    {
        if (spoolState == next) return;
        var prev = spoolState;
        spoolState = next;
        Raise(SpoolStateChanged, new GemSpoolStateEventArgs(prev, next, spoolFull, spool.Count));
    }

    // 스풀 상태를 저장소에 남긴다(gate 안에서는 저장소 대기열에 넣기만)
    void SaveSpoolStateLocked()
        => spoolStore?.SaveState(new GemSpoolSnapshot(spoolState != GemSpoolState.Inactive, spoolFull, spoolTotal, spoolStartTime, spoolFullTime,
            [.. spoolSelection.Select(kv => (kv.Key, (IReadOnlyList<byte>)(kv.Value == null ? [] : [.. kv.Value.Order()])))]));

    // 테스트용: 지금 스풀 대상 표("S5 S6F1,11" 꼴, Stream 순서)
    internal string SpoolTargets
    {
        get
        {
            lock (gate)
                return string.Join(" ", spoolSelection.OrderBy(kv => kv.Key)
                    .Select(kv => kv.Value == null ? $"S{kv.Key}" : $"S{kv.Key}F{string.Join(",", kv.Value.Order())}"));
        }
    }

    // 테스트용: 지금 스풀의 메시지(SML, 오래된 것부터)
    internal string[] SpoolSml { get { lock (gate) return [.. spool.Select(s => s.Message.ToSml())]; } }
    // 테스트용: 이번 실행의 저장소(쓰기 스레드 훅을 걸 때), 지금 스레드가 gate를 잡고 있는지, 전송 디스패처 스레드인지
    internal GemSpoolStore? SpoolStoreForTest { get { lock (gate) return spoolStore; } }
    internal bool GateHeldByCurrentThread => Monitor.IsEntered(gate);
    internal bool OnTransportDispatcherThread => transport.IsDispatcherThread();
    // 테스트용: OnS6F23이 S6F24를 보낸 뒤 두 번째 gate 블록 전에 부른다(디스패처 스레드, gate 밖)
    internal Action? AfterS6F24ForTest;

    #region 모으기 (gate 안)
    // 전이 1의 조건: 스풀링 켬 + INACTIVE + EnableSpooling 참(끄면 새 활성화만 막는다)
    bool CanActivateSpoolLocked => spooling && spoolState == GemSpoolState.Inactive && EnableSpoolingLocked;

    // 전이 1의 상태 부분: ACTIVE(NOT FULL, NO OUTPUT), 카운트 0, 시작 시각. 알림은 FinishSpoolActivationLocked가 낸다
    void ActivateSpoolLocked()
    {
        spoolState = GemSpoolState.NoOutput;
        spoolFull = false;
        spoolTotal = 0;
        spoolStartTime = SpoolClockLocked();
        spoolFullTime = null;
        spoolActivating = true;
        SaveSpoolStateLocked();
    }

    // 전이 1의 끝: SpoolingActivated CE(NOT COMMUNICATING이므로 대상이면 스풀에, 옮긴 메시지들 뒤) → SpoolStateChanged
    void FinishSpoolActivationLocked()
    {
        TriggerStandardEventLocked(GemStandard.SpoolingActivated);
        spoolActivating = false;
        Raise(SpoolStateChanged, new GemSpoolStateEventArgs(GemSpoolState.Inactive, spoolState, spoolFull, spool.Count));
    }

    // 스풀 후보(S5F1·S6F11·S10F1)이고 Host가 정한 대상 표에 있는가
    bool IsSpoolTarget(SecsMessage m)
    {
        if (!(m.Stream == 5 && m.Function == 1 || m.Stream == 6 && m.Function == 11 || m.Stream == 10 && m.Function == 1)) return false;
        return spoolSelection.TryGetValue((byte)m.Stream, out var fns) && (fns == null || fns.Contains((byte)m.Function));
    }

    // 지금 ACTIVE이고 이 메시지가 스풀 대상인가(꺼져 있으면 늘 false)
    bool SpoolsLocked(SecsMessage m) => spooling && spoolState != GemSpoolState.Inactive && IsSpoolTarget(m);

    // 스풀 끝에 넣는다(6I.4). 자리가 없으면 전이 2 뒤 OverWriteSpool에 따라 새 것 또는 가장 오래된 것을 버린다(SendFailed SpoolFull).
    // 넣었으면 true(가장 오래된 것을 지우고 넣은 경우 포함), 새 것을 버렸으면 false(EnqueueLocked의 QueueFull과 같은 모양)
    bool SpoolLocked(SecsMessage message)
    {
        spoolTotal++;
        if (spool.Count >= maxSpoolMessages)
        {
            if (!spoolFull)
            {
                spoolFull = true;                      // 전이 2: INACTIVE를 거쳐서만 풀린다
                spoolFullTime = SpoolClockLocked();
                SaveSpoolStateLocked();
                if (!spoolActivating) Raise(SpoolStateChanged, new GemSpoolStateEventArgs(spoolState, spoolState, true, spool.Count));
            }
            // 덮어쓰기: 가장 오래된 것을 지운다. 단 지금 보내고 있는(결과 처리 전인) 머리는 지우지 않고 그다음을 지운다
            var victim = OverWriteSpoolLocked ? spool.First : null;
            if (victim != null && spoolSending == victim.Value.Seq) victim = victim.Next;
            if (victim == null)
            {
                Raise(SendFailed, new GemSendFailedEventArgs(message, GemSendFailure.SpoolFull, null, null));
                return false;
            }
            spool.Remove(victim);
            spoolStore?.Remove(victim.Value.Seq);
            Raise(SendFailed, new GemSendFailedEventArgs(victim.Value.Message, GemSendFailure.SpoolFull, null, null));
        }
        long seq = nextSeq++;
        spool.AddLast((seq, message));
        spoolStore?.Add(seq, message);
        return true;
    }
    #endregion

    #region 내보내기 (전이 3~6′)
    // Pump가 sendQueue가 빈 뒤에 보낼 스풀 머리(gate 안): TRANSMIT + COMMUNICATING + ON-LINE이고 스풀에 메시지가 있을 때. 저장된 메시지 그대로
    Outgoing? SpoolHeadLocked()
    {
        if (spoolSending != null || spoolState != GemSpoolState.Transmit || commState != GemCommunicationState.Communicating || IsOffline(control) || spool.First is not { } head) return null;
        spoolSending = head.Value.Seq;
        return new Outgoing(head.Value.Message, SpoolDone) { SpoolSeq = head.Value.Seq };
    }

    // 스풀 머리의 송신 완료(Completed 뒤, 보통 디스패처, gate 밖에서 불림). 6I.5 표:
    // 응답(ack 무엇이든·구조 오류)·W비트 없음 송신 완료·SxF0·S9 → 트랜잭션이 끝났으므로 지우고 지금처럼 AckDone으로 알림.
    // T3 → 남김(Completed가 이어서 통신 실패 → CommunicationLostLocked가 전이 6). 끊김은 Completed가 여기까지 오지 않는다(DeviceClosed가 전이 6).
    // 그 밖(SECS-I 재시도 초과 IOException 등) → 남기고 전이 6(CE 없음, 회선은 유지되므로 Host가 다시 S6F23)
    void SpoolDone(Outgoing o, Task<SecsMessage?> t)
    {
        var ex = t.IsCompletedSuccessfully ? null : t.Exception?.InnerException;
        if (ex is SecsTimeoutException) return;                     // spoolSending은 이어지는 통신 실패의 ClearQueueLocked가 비운다
        lock (gate)
        {
            if (!running) return;
            if (spoolSending == o.SpoolSeq) spoolSending = null;
            if (ex != null && ex is not (SecsAbortException or SecsErrorException))
            {
                StopUnloadLocked(commFailure: false);
                return;
            }
            // 머리를 지운다. 없으면(그 사이 PURGE·덮어쓰기로 이미 빠짐) 알리지도 세지도 않는다
            var node = spool.First;
            while (node != null && node.Value.Seq != o.SpoolSeq) node = node.Next;
            if (node == null) return;
            spool.Remove(node);
            spoolStore?.Remove(node.Value.Seq);
            AckDone(o, t);                                          // ack ≠ 0·구조 오류·SxF0·S9는 지금처럼 SendFailed
            if (spoolState != GemSpoolState.Transmit) return;      // 이미 멈춘 내보내기(OFF-LINE 등)의 마지막 트랜잭션
            transmittedSinceRequest++;
            if (spool.Count == 0) DeactivateSpoolLocked();                                                                      // 전이 3
            else if (MaxSpoolTransmitLocked > 0 && transmittedSinceRequest >= MaxSpoolTransmitLocked) StopUnloadLocked(commFailure: false);   // 전이 6(개수 도달, CE 없음)
        }
    }

    // 전이 6·6′(gate 안): TRANSMIT → NO OUTPUT. 보내던 머리는 스풀에 남는다. 통신 실패(전이 14)면 SpoolTransmitFailure CE
    // (NOT COMMUNICATING이므로 S6이 대상이면 스풀 끝에 들어감). 개수 도달·IOException·통신 끔·OFF-LINE은 CE 없음
    void StopUnloadLocked(bool commFailure)
    {
        if (spoolState != GemSpoolState.Transmit) return;
        SetSpoolStateLocked(GemSpoolState.NoOutput);
        if (commFailure) TriggerStandardEventLocked(GemStandard.SpoolTransmitFailure);
    }

    // 전이 3(gate 안): INACTIVE(LOAD·UNLOAD 모두 나감, 가득 참 풀림). 카운트·시각은 남긴다.
    // SpoolingDeactivated CE는 INACTIVE + COMMUNICATING이라 스풀하지 않고 바로 보낸다(대기열)
    void DeactivateSpoolLocked()
    {
        var prev = spoolState;
        spoolState = GemSpoolState.Inactive;
        spoolFull = false;
        SaveSpoolStateLocked();
        TriggerStandardEventLocked(GemStandard.SpoolingDeactivated);
        Raise(SpoolStateChanged, new GemSpoolStateEventArgs(prev, GemSpoolState.Inactive, false, spool.Count));
    }
    #endregion

    #region S6F23 / S6F24
    // 내보내기·지우기 요청(6I.7). S6F24를 먼저 보내고 그다음 gate 안에서 전이한다
    void OnS6F23(SecsPrimaryEventArgs e)
    {
        byte rsdc = GemMessages.ParseS6F23(e.Message);              // 구조 오류 → FormatException → S9F7
        byte rsda;
        lock (gate)
        {
            if (!running) return;
            rsda = rsdc > GemAck.RsdcPurge || spoolState == GemSpoolState.Transmit ? GemAck.RsdaBusy
                : spoolState == GemSpoolState.Inactive || rsdc == GemAck.RsdcTransmit && spool.Count == 0 ? GemAck.RsdaNoData
                : GemAck.RsdaAccepted;
        }
        Respond(e, GemMessages.S6F24(rsda));
        if (rsda == GemAck.RsdaBusy) return;
        AfterS6F24ForTest?.Invoke();
        lock (gate)
        {
            // 응답한 뒤 gate 밖에 있던 사이 앱 스레드의 GoOffline·DisableCommunication이나 끊김·Stop이 끼어들 수 있다. 이들은 NO OUTPUT의
            // spoolState를 바꾸지 않으므로 통신·제어 상태도 다시 본다: COMMUNICATING + ON-LINE이 아니면 아무것도 하지 않는다(보낸 S6F24는 그대로, NO OUTPUT 유지)
            if (!running || spoolState != GemSpoolState.NoOutput || commState != GemCommunicationState.Communicating || IsOffline(control)) return;
            if (rsdc == GemAck.RsdcPurge)
            {
                // 전이 4 → 3: 전부 지움(지운 메시지마다 알리지 않음 — Host가 요청함). 스풀이 비어 있어도 INACTIVE로 끝냄
                spool.Clear();
                spoolStore?.Clear();
                DeactivateSpoolLocked();
            }
            else if (spool.Count == 0) DeactivateSpoolLocked();     // ACTIVE + 빈 스풀 + RSDC 0: RSDA 2와 함께 끝냄(Host가 기다리지 않게)
            else if (rsda == GemAck.RsdaAccepted)
            {
                transmittedSinceRequest = 0;                        // 전이 5
                SetSpoolStateLocked(GemSpoolState.Transmit);
                worker?.Post(Pump);
            }
        }
    }
    #endregion

    #region S2F43 / S2F44
    // 스풀 대상 표를 통째로 바꾼다(6I.6). 오류가 하나라도 있으면 RSPACK 1 + 오류 목록이고 아무것도 바꾸지 않는다. ACTIVE 중에도 받는다
    void OnS2F43(SecsPrimaryEventArgs e)
    {
        var request = GemMessages.ParseS2F43(e.Message);          // 구조 오류 → FormatException → S9F7
        List<(byte Stream, byte Strack, IReadOnlyList<byte> Functions)> errors = [];
        lock (gate)
        {
            if (!running) return;
            foreach (var (stream, fns) in request)
            {
                if (stream == 1 || stream == 9) errors.Add((stream, GemAck.StrackNotAllowed, fns));          // S1은 규격상 금지, S9는 그 링크의 오류라 나중에 보내면 뜻 없음
                else if (!catalog.Streams.Contains(stream)) errors.Add((stream, GemAck.StrackUnknownStream, fns));
                else if (fns.Where(f => f % 2 == 0).ToArray() is { Length: > 0 } even) errors.Add((stream, GemAck.StrackSecondaryFunction, even));
            }
            if (errors.Count == 0)
            {
                spoolSelection.Clear();
                foreach (var (stream, fns) in request)
                {
                    // 같은 Stream이 두 번 오면 합친다(어느 쪽이든 n = 0이면 전부)
                    spoolSelection.TryGetValue(stream, out var have);
                    bool all = fns.Count == 0 || spoolSelection.ContainsKey(stream) && have == null;
                    spoolSelection[stream] = all ? null : [.. have ?? [], .. fns];
                }
                SaveSpoolStateLocked();
            }
        }
        Respond(e, errors.Count == 0 ? GemMessages.S2F44(GemAck.RspackAccepted) : GemMessages.S2F44(GemAck.RspackRejected, errors));
    }
    #endregion
}
