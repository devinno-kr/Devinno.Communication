using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

/// <summary>The arguments for when the equipment sends trace data (S6F1).</summary>
/// <param name="trid">TRID</param>
/// <param name="sampleNumber">SMPLN</param>
/// <param name="time">STIME as received</param>
/// <param name="samples">The values split per sample</param>
/// <param name="message">The received S6F1</param>
public class GemTraceDataEventArgs(GemId trid, ulong sampleNumber, string time, IReadOnlyList<IReadOnlyList<SecsItem>> samples, SecsMessage message) : EventArgs
{
    /// <summary>Gets the trace ID (TRID).</summary>
    public GemId Trid { get; } = trid;
    /// <summary>Gets the number of the last sample in this report (SMPLN, from 1).</summary>
    public ulong SampleNumber { get; } = sampleNumber;
    /// <summary>Gets the time of the first sample in this report (STIME) as GEM TIME text, as received. Read it with <see cref="GemTime.TryParse"/>.</summary>
    public string Time { get; } = time;
    /// <summary>Gets the values split per sample, each in SVID order. When this GemHost did not start the trace (or the value count is not a multiple of its SVID count), the whole value list is one entry.</summary>
    public IReadOnlyList<IReadOnlyList<SecsItem>> Samples { get; } = samples;
    /// <summary>Gets the received S6F1 as-is.</summary>
    public SecsMessage Message { get; } = message;
}

// 트레이스(6J.7.2 GemHost): S2F23으로 시작·멈춤, S6F1 받기(구독자가 없으면 지금처럼 MessageReceived)
public partial class GemHost
{
    readonly Dictionary<GemId, int> traceSvidCounts = [];   // gate. 이 Host가 시작한 트레이스의 SVID 수(S6F1을 표본별로 나누려고)

    /// <summary>Raised on the app event thread when the equipment sent trace data (S6F1). GemHost has already answered ACKC6 0.</summary>
    /// <remarks>Without a subscriber, S6F1 goes to <see cref="MessageReceived"/> as before.</remarks>
    public event EventHandler<GemTraceDataEventArgs>? TraceDataReceived;

    /// <summary>Starts a trace on the equipment (S2F23 Trace Initialize Send). The equipment samples the SVIDs every <paramref name="period"/> and sends S6F1 reports of <paramref name="groupSize"/> samples, <paramref name="totalSamples"/> samples in all.</summary>
    /// <remarks>A numeric TRID is sent as U4, a text TRID as A. The period is sent as <c>hhmmsscc</c>; parts below 1/100 s are dropped. Sending the same TRID again replaces that trace. When TIAACK is 0, GemHost remembers the SVID count to split <see cref="GemTraceDataEventArgs.Samples"/>.</remarks>
    /// <param name="trid">The trace ID.</param>
    /// <param name="period">The sampling period, 10 ms to under 100 hours.</param>
    /// <param name="totalSamples">The total number of samples (1 or more; stop with <see cref="StopTrace"/>).</param>
    /// <param name="groupSize">The number of samples per S6F1 (1 or more).</param>
    /// <param name="svids">The SVIDs (sent in <see cref="GemFormats.Vid"/>).</param>
    /// <returns>TIAACK (0 = accepted, 1 too many SVIDs, 2 no more traces, 3 invalid period, 4 unknown SVID, 5 invalid REPGSZ).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="svids"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="period"/>, <paramref name="totalSamples"/>, or <paramref name="groupSize"/> is out of range.</exception>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S2F0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body structure differs from what was expected.</exception>
    public byte InitializeTrace(GemId trid, TimeSpan period, int totalSamples, int groupSize, IReadOnlyList<GemId> svids)
        => TraceStarted(trid, svids, GemMessages.ParseS2F24(Ask(TraceRequest(trid, period, totalSamples, groupSize, svids))));

    /// <summary>Starts a trace on the equipment (S2F23). The rules and exceptions are the same as for <see cref="InitializeTrace"/>.</summary>
    /// <param name="trid">The trace ID.</param>
    /// <param name="period">The sampling period.</param>
    /// <param name="totalSamples">The total number of samples.</param>
    /// <param name="groupSize">The number of samples per S6F1.</param>
    /// <param name="svids">The SVIDs.</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>TIAACK.</returns>
    public async Task<byte> InitializeTraceAsync(GemId trid, TimeSpan period, int totalSamples, int groupSize, IReadOnlyList<GemId> svids, CancellationToken cancellationToken = default)
    {
        var request = TraceRequest(trid, period, totalSamples, groupSize, svids);
        return TraceStarted(trid, svids, GemMessages.ParseS2F24(await AskAsync(request, cancellationToken).ConfigureAwait(false)));
    }

    /// <summary>Stops a trace on the equipment (S2F23 with TOTSMP 0). The equipment answers 0 even when there is no such trace.</summary>
    /// <param name="trid">The trace ID.</param>
    /// <returns>TIAACK.</returns>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S2F0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body structure differs from what was expected.</exception>
    public byte StopTrace(GemId trid)
    {
        Forget(trid);
        return GemMessages.ParseS2F24(Ask(StopRequest(trid)));
    }

    /// <summary>Stops a trace on the equipment (S2F23 with TOTSMP 0). The rules and exceptions are the same as for <see cref="StopTrace"/>.</summary>
    /// <param name="trid">The trace ID.</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>TIAACK.</returns>
    public async Task<byte> StopTraceAsync(GemId trid, CancellationToken cancellationToken = default)
    {
        Forget(trid);
        return GemMessages.ParseS2F24(await AskAsync(StopRequest(trid), cancellationToken).ConfigureAwait(false));
    }

    SecsMessage TraceRequest(GemId trid, TimeSpan period, int totalSamples, int groupSize, IReadOnlyList<GemId> svids)
    {
        ArgumentNullException.ThrowIfNull(svids);
        ArgumentOutOfRangeException.ThrowIfLessThan(totalSamples, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(groupSize, 1);
        var dsper = GemTime.FormatTracePeriod(period);
        return GemMessages.S2F23(trid, dsper, (ulong)totalSamples, (ulong)groupSize, svids, Formats);
    }

    static SecsMessage StopRequest(GemId trid) => GemMessages.S2F23(trid, "000000", 0, 0, []);

    byte TraceStarted(GemId trid, IReadOnlyList<GemId> svids, byte tiaack)
    {
        if (tiaack == GemAck.TiaackOk)
            lock (gate) traceSvidCounts[trid] = svids.Count;
        return tiaack;
    }

    void Forget(GemId trid)
    {
        lock (gate) traceSvidCounts.Remove(trid);
    }

    partial void AddTraceHandlers() => handlers[6 * 256 + 1] = OnS6F1;

    partial void ResetTraces() => traceSvidCounts.Clear();

    // S6F1: 구독자가 없으면 지금처럼 MessageReceived. 있으면 ACKC6 0으로 바로 답하고 표본별로 나눠 알림
    void OnS6F1(SecsPrimaryEventArgs e)
    {
        var handler = TraceDataReceived;
        if (handler == null) { HandleOther(e); return; }
        var data = GemMessages.ParseS6F1(e.Message);                   // 구조 오류 → FormatException → SxF0
        int k;
        lock (gate) k = traceSvidCounts.GetValueOrDefault(data.Trid);
        IReadOnlyList<IReadOnlyList<SecsItem>> samples = k > 0 && data.Values.Count > 0 && data.Values.Count % k == 0
            ? [.. data.Values.Chunk(k).Select(c => (IReadOnlyList<SecsItem>)Array.AsReadOnly(c))]
            : [data.Values];
        if (e.Message.ReplyExpected) Respond(e, GemMessages.S6F2(GemAck.Ackc6Accepted));
        Raise(handler, new GemTraceDataEventArgs(data.Trid, data.SampleNumber, data.Time, samples, e.Message));
    }
}
