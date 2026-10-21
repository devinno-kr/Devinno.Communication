using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

// 스풀링(E30 7.12): 켜기 설정·공개 상태·표준 값 계산, 모으기(활성화·버림 경로·가득 참)와 S2F43. 내보내기(S6F23)는 뒤 태스크
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
    }

    // GEM 시계(시스템 시각 + S2F31 오프셋)의 현재 시각을 UTC로(gate 안). 재시작 뒤에도 안전하게 저장된다
    DateTime SpoolClockLocked() => (DateTime.Now + clockOffset).ToUniversalTime();
    internal DateTime SpoolClock { get { lock (gate) return SpoolClockLocked(); } }

    SecsItem SpoolTimeItem(DateTime? t) => SecsItem.A(t is { } v ? GemTime.Format(v, TimeFormatLocked) : "");

    // Start 때 상태 초기화(gate 안). 이전 실행의 값은 남기지 않는다(저장된 상태 복원은 뒤 태스크)
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

    // 스풀 끝에 넣는다(6I.4). 자리가 없으면 전이 2 뒤 OverWriteSpool에 따라 새 것 또는 가장 오래된 것을 버린다(SendFailed SpoolFull)
    void SpoolLocked(SecsMessage message)
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
            // 덮어쓰기: 가장 오래된 것을 지운다. 단 지금 보내고 있는 머리는 지우지 않고 그다음을 지운다
            var victim = OverWriteSpoolLocked ? spool.First : null;
            if (victim != null && inFlight?.SpoolSeq == victim.Value.Seq) victim = victim.Next;
            if (victim == null)
            {
                Raise(SendFailed, new GemSendFailedEventArgs(message, GemSendFailure.SpoolFull, null, null));
                return;
            }
            spool.Remove(victim);
            spoolStore?.Remove(victim.Value.Seq);
            Raise(SendFailed, new GemSendFailedEventArgs(victim.Value.Message, GemSendFailure.SpoolFull, null, null));
        }
        long seq = nextSeq++;
        spool.AddLast((seq, message));
        spoolStore?.Add(seq, message);
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
