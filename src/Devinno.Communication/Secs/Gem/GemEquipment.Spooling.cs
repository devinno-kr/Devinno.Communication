using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

// 스풀링(E30 7.12). 이 파일은 켜기 설정·공개 상태·표준 값 계산만 둔다(스풀 동작은 뒤 태스크).
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
#pragma warning disable CS0414   // 스풀 동작을 구현하는 뒤 태스크에서 읽는다
    long nextSeq;
#pragma warning restore CS0414
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

    /// <summary>Raised on the application event thread when the spooling state, the full flag, or the message count changed.</summary>
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

    // 스풀 상태를 저장소에 남긴다(gate 안에서는 저장소 대기열에 넣기만). 저장 내용은 뒤 태스크에서 채운다
    void SaveSpoolStateLocked() { }
}
