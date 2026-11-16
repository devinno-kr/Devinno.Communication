using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

/// <summary>A trace the equipment is running (diagnostics, see <see cref="GemEquipment.ActiveTraces"/>).</summary>
/// <param name="trid">TRID</param>
/// <param name="period">The sampling period</param>
/// <param name="svids">The SVIDs (copied and kept read-only)</param>
/// <param name="totalSamples">TOTSMP</param>
/// <param name="groupSize">REPGSZ</param>
/// <param name="remainingSamples">The samples not yet taken</param>
public sealed class GemTraceInfo(GemId trid, TimeSpan period, IReadOnlyList<GemId> svids, ulong totalSamples, ulong groupSize, ulong remainingSamples)
{
    /// <summary>Gets the trace ID (TRID).</summary>
    public GemId Trid { get; } = trid;
    /// <summary>Gets the sampling period.</summary>
    public TimeSpan Period { get; } = period;
    /// <summary>Gets the SVIDs sampled, in request order.</summary>
    public IReadOnlyList<GemId> Svids { get; } = Array.AsReadOnly(svids.ToArray());
    /// <summary>Gets the total number of samples (TOTSMP).</summary>
    public ulong TotalSamples { get; } = totalSamples;
    /// <summary>Gets the number of samples per report (REPGSZ).</summary>
    public ulong GroupSize { get; } = groupSize;
    /// <summary>Gets the number of samples not yet taken.</summary>
    public ulong RemainingSamples { get; } = remainingSamples;
}

// 트레이스 데이터 수집(6J.7): Host의 S2F23으로 정의를 받아 표에 두고, 작업 스레드 틱이 SV를 표본으로 떠 S6F1로 보낸다
public partial class GemEquipment
{
    const int MaxTraceGroupSize = 1000;     // REPGSZ 상한(스펙 밖, 큰 묶음이 메모리에 쌓이지 않게) — 넘으면 TIAACK 5

    // 실행 중인 트레이스 하나(gate)
    sealed class Trace
    {
        public required SecsItem TridItem;  // 받은 TRID 아이템 그대로(S6F1에 돌려줌)
        public required GemId Key;          // 같은 TRID 판단(GemId.FromItem 값)
        public required TimeSpan Period;
        public required long PeriodMs;
        public required List<GemVar> Vars;
        public required ulong Total;
        public required ulong Group;
        public ulong Taken;                 // 뜬 표본 수(= 마지막 SMPLN)
        public long NextDue;                // 다음 표본 시각(TraceClock 기준 ms)
        public readonly List<SecsItem> Buffer = [];
        public string? BufferTime;          // 묶음 첫 표본의 GEM 시각(STIME)
    }

    int maxTraces = 10, maxTraceVariables = 100, minTracePeriod = 100;
    bool traceReplyExpected = true;
    readonly List<Trace> traces = [];       // gate. 시작한 순서
    long traceNextDue = long.MaxValue;      // gate에서 쓰고 Volatile로 읽음(작업 루프 대기 계산). 없으면 long.MaxValue

    // 트레이스 시각 원천(ms, 단조). 테스트가 Start 전에 가짜로 바꾼다
    internal Func<long> TraceClock = () => Environment.TickCount64;

    /// <summary>Gets or sets the number of traces (S2F23) that can run at the same time, 0 to 100 (default: 10). 0 turns traces off: S2F23 is answered with S9F5.</summary>
    /// <remarks>When the Host asks for one more, it gets TIAACK 2. Redefining a running TRID does not count as a new trace.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">Out of range</exception>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public int MaxTraces { get => maxTraces; set => Setting(() => maxTraces = Check(value, 0, 100, nameof(MaxTraces))); }

    /// <summary>Gets or sets the largest number of SVIDs in one trace, 1 to 1000 (default: 100). More gets TIAACK 1.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Out of range</exception>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public int MaxTraceVariables { get => maxTraceVariables; set => Setting(() => maxTraceVariables = Check(value, 1, 1000, nameof(MaxTraceVariables))); }

    /// <summary>Gets or sets the shortest trace period in ms, 10 to 3600000 (default: 100). A shorter DSPER gets TIAACK 3.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Out of range</exception>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public int MinTracePeriod { get => minTracePeriod; set => Setting(() => minTracePeriod = Check(value, 10, 3600000, nameof(MinTracePeriod))); }

    /// <summary>Gets or sets the W-bit of S6F1 trace reports (default: true). When the Host answers with a nonzero ACKC6, <see cref="SendFailed"/> is raised.</summary>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public bool TraceReplyExpected { get => traceReplyExpected; set => Setting(() => traceReplyExpected = value); }

    /// <summary>Gets a copy of the traces now running, in the order they were started (for diagnostics; traces are started and stopped by the Host only).</summary>
    public IReadOnlyList<GemTraceInfo> ActiveTraces
    {
        get
        {
            lock (gate)
                return [.. traces.Select(t => new GemTraceInfo(GemId.FromItem(t.TridItem), t.Period, [.. t.Vars.Select(v => v.Id)], t.Total, t.Group, t.Total - t.Taken))];
        }
    }

    partial void AddTraceHandlers() => handlers[2 * 256 + 23] = OnS2F23;

    partial void ResetTraces() => ClearTracesLocked();

    // 모든 트레이스를 지운다(gate 안): Start·통신 끔
    void ClearTracesLocked()
    {
        traces.Clear();
        UpdateTraceNextDueLocked();
    }

    // 가장 이른 다음 표본 시각(gate 안)
    void UpdateTraceNextDueLocked()
    {
        long due = long.MaxValue;
        foreach (var t in traces) due = Math.Min(due, t.NextDue);
        Volatile.Write(ref traceNextDue, due);
    }

    // 작업 루프 틱 대기(ms): 다음 표본 시각까지(gate 없이, GemLoop sync 안에서 불림)
    int TraceWaitMs()
    {
        long due = Volatile.Read(ref traceNextDue);
        if (due == long.MaxValue) return int.MaxValue;
        return (int)Math.Clamp(due - TraceClock(), 1, int.MaxValue);
    }

    // 작업 스레드 틱(gate 안): 예정 시각이 된 트레이스마다 표본 하나(늦었어도 하나, 놓친 칸은 메우지 않음). 묶음이 차거나 끝이면 S6F1.
    // 한 칸 이상 늦었으면(칸을 건너뜀) 그 전까지 모은 표본을 먼저 따로 보내고, 다음 예정은 이 표본 시각 + 주기로 다시 잡는다
    // → 보고마다 STIME + i·DSPER가 맞는다(Host가 표본 시각을 복원하는 규칙)
    void TraceTickLocked(long now)
    {
        if (traces.Count == 0 || now < Volatile.Read(ref traceNextDue)) return;
        for (int i = 0; i < traces.Count; i++)
        {
            var t = traces[i];
            if (now < t.NextDue) continue;
            bool skipped = now - t.NextDue >= t.PeriodMs;
            if (skipped && t.Buffer.Count > 0) ReportTraceLocked(t);
            if (t.Buffer.Count == 0) t.BufferTime = GemTime.Format(DateTime.Now + clockOffset, TimeFormatLocked);
            foreach (var v in t.Vars) t.Buffer.Add(ValueLocked(v));
            t.Taken++;
            t.NextDue = skipped ? now + t.PeriodMs : t.NextDue + t.PeriodMs;
            if (t.Taken % t.Group == 0 || t.Taken == t.Total) ReportTraceLocked(t);
            if (t.Taken == t.Total) traces.RemoveAt(i--);
        }
        UpdateTraceNextDueLocked();
    }

    // 모은 표본을 S6F1 하나로(gate 안). OFF-LINE이면 버림(알림 없음, 6J.7.2).
    // 송신 대기열은 절반까지만 쓴다: Host가 S6F1에 늦게 답해도 알람·이벤트 자리가 남게(넘으면 이 보고만 SendFailed QueueFull)
    void ReportTraceLocked(Trace t)
    {
        var report = GemMessages.S6F1(t.TridItem, t.Taken, t.BufferTime!, t.Buffer, traceReplyExpected);
        t.Buffer.Clear();
        if (IsOffline(control)) return;
        if (commState == GemCommunicationState.Communicating && !SpoolsLocked(report) && sendQueue.Count >= Math.Max(1, maxPendingSends / 2))
            Raise(SendFailed, new GemSendFailedEventArgs(report, GemSendFailure.QueueFull, null, null));
        else SendOrSpoolLocked(report);
    }

    // S2F23: 검사 순서(T1) = 구조(S9F7) → TOTSMP 0(멈춤) → TIAACK 1 → 2 → 3 → 4 → 5. 실패하면 표는 그대로
    void OnS2F23(SecsPrimaryEventArgs e)
    {
        var m = e.Message;
        var (tridItem, period, total, group, svidList) = GemMessages.ParseS2F23Raw(m);       // 구조 오류 → FormatException → S9F7
        const string expected = "<L TRID <A DSPER> TOTSMP REPGSZ <L SVID…>>";
        var key = GemId.FromItem(tridItem);
        var svids = svidList.Items.Select(i => GemMessages.Id(i, m, expected)).ToList();       // SVID가 ID 아이템이 아니면 S9F7
        byte ack;
        lock (gate)
        {
            ack = DefineTraceLocked(tridItem, key, period, total, group, svids);
        }
        Respond(e, GemMessages.S2F24(ack));
    }

    byte DefineTraceLocked(SecsItem tridItem, GemId key, string period, ulong total, ulong group, List<GemId> svids)
    {
        int at = traces.FindIndex(t => t.Key.Equals(key));
        if (total == 0)
        {
            if (at >= 0) traces.RemoveAt(at);
            UpdateTraceNextDueLocked();
            return GemAck.TiaackOk;
        }
        if (svids.Count > maxTraceVariables) return GemAck.TiaackTooManySvids;
        if (at < 0 && traces.Count >= maxTraces) return GemAck.TiaackNoMoreTraces;
        if (GemTime.ParseTracePeriod(period) is not { } p || p <= TimeSpan.Zero || p.TotalMilliseconds < minTracePeriod) return GemAck.TiaackInvalidPeriod;
        var vars = new List<GemVar>(svids.Count);
        foreach (var id in svids)
        {
            if (!catalog.Vars.TryGetValue(GemItems.Key(id, catalog.VidFormat), out var v) || v.Kind != GemVarKind.Status) return GemAck.TiaackUnknownSvid;
            vars.Add(v);
        }
        if (vars.Count == 0) return GemAck.TiaackUnknownSvid;
        if (group == 0 || group > total || group > MaxTraceGroupSize) return GemAck.TiaackInvalidGroupSize;
        long ms = (long)p.TotalMilliseconds;
        var trace = new Trace { TridItem = tridItem, Key = key, Period = p, PeriodMs = ms, Vars = vars, Total = total, Group = group, NextDue = TraceClock() + ms };
        if (at >= 0) traces[at] = trace;
        else traces.Add(trace);
        UpdateTraceNextDueLocked();
        return GemAck.TiaackOk;
    }
}
