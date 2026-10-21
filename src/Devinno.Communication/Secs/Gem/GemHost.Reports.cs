using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

/// <summary>A single report subscription: when this CE occurs, the values of these VIDs are received.</summary>
/// <param name="ceid">CEID</param>
/// <param name="vids">The VIDs to receive (an empty list means the event only, without values).</param>
public sealed class GemSubscription(GemId ceid, IReadOnlyList<GemId> vids)
{
    /// <summary>CEID</summary>
    public GemId Ceid { get; } = ceid;
    /// <summary>Gets the VIDs to receive (an empty list means the event only, without values).</summary>
    public IReadOnlyList<GemId> Vids { get; } = vids;
}

/// <summary>A step of report setup (<c>ConfigureReports</c>).</summary>
public enum GemReportSetupStep
{
    /// <summary>No step failed (success).</summary>
    None,
    /// <summary>Delete all reports on the equipment (S2F33 with an empty list).</summary>
    DeleteReports,
    /// <summary>Define reports (S2F33).</summary>
    DefineReports,
    /// <summary>Link reports to CEs (S2F35).</summary>
    LinkEvents,
    /// <summary>Enable CEs (S2F37).</summary>
    EnableEvents,
}

/// <summary>The result of report setup (<c>ConfigureReports</c>). It tells which step the equipment rejected with a nonzero ack.</summary>
/// <param name="failedStep">The step that failed (None on success).</param>
/// <param name="ack">The ack of that step (DRACK, LRACK, or ERACK; null on success).</param>
public sealed class GemReportSetupResult(GemReportSetupStep failedStep, byte? ack)
{
    /// <summary>Gets a value indicating whether all steps completed with ack 0.</summary>
    public bool Success => FailedStep == GemReportSetupStep.None;
    /// <summary>Gets the step that failed (None on success).</summary>
    public GemReportSetupStep FailedStep { get; } = failedStep;
    /// <summary>Gets the ack of the failed step (null on success).</summary>
    public byte? Ack { get; } = ack;
}

// 리포트 구독(설정)과 동적 리포트 설정 S2F33/35/37, 해석 표(RPTID → VID)
public partial class GemHost
{
    readonly List<GemSubscription> subscriptions = [];                 // gate: 설정(실행 중에도 바꿀 수 있음)
    readonly Dictionary<GemId, IReadOnlyList<GemId>> reportTable = []; // gate: 장비에 정의되어 있다고 아는 리포트(성공한 S2F33). 열쇠는 GemItems.Key(RPTID, RptidFormat)
    readonly SemaphoreSlim reportLock = new(1, 1);                     // ConfigureReports를 한 번에 하나씩
    long dataId;                                                       // gate: 마지막으로 쓴 DATAID
    bool deleteReportsFirst = true;
    long reportIdStart = 1000;

    /// <summary>Gets or sets whether, when configuring reports, all reports on the equipment are deleted first and all CEs are disabled (default: true).</summary>
    /// <remarks>If false, RPTIDs and CEIDs may collide with the reports and links remaining on the equipment, resulting in DRACK 3 or LRACK 3; and because the VIDs of reports that already existed on the equipment are unknown, their values are delivered without VIDs.</remarks>
    /// <exception cref="InvalidOperationException">Changed while running.</exception>
    public bool DeleteReportsFirst { get => deleteReportsFirst; set => Setting(() => deleteReportsFirst = value); }

    /// <summary>Gets or sets the starting RPTID number assigned to each subscription when configuring reports (default: 1000). It increments by 1 for each subscription that has VIDs, in order. If <see cref="RptidFormat"/> is A, the prefix R is added, as in "R1000".</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 0.</exception>
    /// <exception cref="InvalidOperationException">Changed while running.</exception>
    public long ReportIdStart
    {
        get => reportIdStart;
        set => Setting(() => reportIdStart = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(ReportIdStart), value, "ReportIdStart는 0 이상입니다."));
    }

    #region 구독
    /// <summary>Subscribes to a CE (configuration). If the same CEID exists, it is replaced in place. It is applied to the equipment at <see cref="ConfigureReports"/> (including the connect sequence).</summary>
    /// <remarks>It can be changed while running (it takes effect from the next <see cref="ConfigureReports"/>).</remarks>
    /// <param name="ceid">CEID</param>
    /// <param name="vids">The VIDs to receive (if none, the event only, without values).</param>
    /// <exception cref="ArgumentException">The CEID cannot be represented in <see cref="CeidFormat"/>, or a VID cannot be represented in <see cref="VidFormat"/>.</exception>
    public void Subscribe(GemId ceid, params GemId[] vids)
    {
        vids ??= [];
        ceid.ToItem(ceidFormat);
        foreach (var v in vids) v.ToItem(vidFormat);
        var s = new GemSubscription(ceid, [.. vids]);
        lock (gate)
        {
            int i = subscriptions.FindIndex(x => x.Ceid == ceid);
            if (i >= 0) subscriptions[i] = s;
            else subscriptions.Add(s);
        }
    }

    /// <summary>Removes a CE subscription (configuration).</summary>
    /// <param name="ceid">CEID</param>
    /// <returns>true if it existed and was removed.</returns>
    public bool Unsubscribe(GemId ceid)
    {
        lock (gate) return subscriptions.RemoveAll(x => x.Ceid == ceid) > 0;
    }

    /// <summary>Removes all subscriptions (configuration).</summary>
    public void ClearSubscriptions()
    {
        lock (gate) subscriptions.Clear();
    }

    /// <summary>Gets the list of subscriptions (a copy, in the order added).</summary>
    public IReadOnlyList<GemSubscription> Subscriptions { get { lock (gate) return [.. subscriptions]; } }
    #endregion

    #region 해석 표
    partial void ResetReports()
    {
        reportTable.Clear();
        dataId = 0;
    }

    // 알고 있는 리포트의 VID 목록(없으면 null). 이벤트 해석과 테스트가 쓴다. RptidFormat이 A면 숫자 RPTID도 10진 텍스트로 맞춰 찾는다
    internal IReadOnlyList<GemId>? KnownReport(GemId rptid)
    {
        lock (gate) return reportTable.GetValueOrDefault(GemItems.Key(rptid, rptidFormat));
    }

    // DATAID: Start마다 1부터, DataidFormat의 최댓값을 넘으면 1로(빌더가 formats.Dataid로 만듦, A면 10진 문자열).
    // build가 예외를 던지면(인자 오류) 번호를 쓰지 않는다. 번호 매기기와 만들기를 gate 안에서 함께 해 동시에 불러도 번호가 겹치지 않는다(gate 안: 메시지 만들기만, 전송 없음)
    SecsMessage WithNextDataId(Func<GemId, SecsMessage> build)
    {
        lock (gate)
        {
            long max = dataidFormat == SecsFormat.ASCII ? long.MaxValue : GemItems.IntegerRange(dataidFormat).Max;
            long next = dataId >= max ? 1 : dataId + 1;
            var message = build(new GemId(next));
            dataId = next;
            return message;
        }
    }
    #endregion

    #region S2F33 / S2F35 / S2F37
    /// <summary>Defines reports (S2F33 &lt;L DATAID &lt;L &lt;L RPTID &lt;L VID…&gt;&gt;…&gt;&gt;). If DRACK is 0, they are added to the interpretation table.</summary>
    /// <remarks>An empty list = delete all reports and links on the equipment. An entry with an empty VID list = delete that report. The equipment accepts all of them or rejects all of them.
    /// If <see cref="RptidFormat"/> is A, a numeric RPTID (for example, 1000) is sent as A "1000" and entered in the interpretation table as the text "1000", so that it matches the RPTID the equipment returns as A.</remarks>
    /// <param name="reports">The list of (RPTID, VID list) pairs.</param>
    /// <returns>DRACK (0 = accepted, 1 = no space, 2 = malformed, 3 = RPTID already exists, 4 = unknown VID).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reports"/> or a VID list is null.</exception>
    /// <exception cref="ArgumentException">An ID cannot be represented in the configured format.</exception>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S2F0 (for example, when OFF-LINE).</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body structure differs from what was expected.</exception>
    public byte DefineReports(IReadOnlyList<(GemId Rptid, IReadOnlyList<GemId> Vids)> reports) => DefineReportsReply(Ask(DefineReportsRequest(reports)), reports);

    /// <summary>Defines reports (S2F33). The rules and exceptions are the same as for <see cref="DefineReports"/>.</summary>
    /// <param name="reports">The list of (RPTID, VID list) pairs.</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>DRACK</returns>
    public async Task<byte> DefineReportsAsync(IReadOnlyList<(GemId Rptid, IReadOnlyList<GemId> Vids)> reports, CancellationToken cancellationToken = default)
    {
        var request = DefineReportsRequest(reports);
        return DefineReportsReply(await AskAsync(request, cancellationToken).ConfigureAwait(false), reports);
    }

    SecsMessage DefineReportsRequest(IReadOnlyList<(GemId Rptid, IReadOnlyList<GemId> Vids)> reports)
    {
        var formats = Formats;
        return WithNextDataId(id => GemMessages.S2F33(id, reports, formats));
    }

    // DRACK 0이면 해석 표 갱신을 디스패처 대기열에 넣는다(기다리지 않음, H3): S2F34보다 먼저 받은 S6F11은 옛 표로, 메서드가 돌아온 뒤에 받은 것은 새 표로 해석된다(그 사이에 받은 것은 어느 쪽일 수도 있음).
    // 디스패처가 멈춰 있으면 버려진다(Start가 표를 비움). transport.Post는 GEM gate 밖에서
    byte DefineReportsReply(SecsMessage reply, IReadOnlyList<(GemId Rptid, IReadOnlyList<GemId> Vids)> reports)
    {
        byte ack = GemMessages.ParseS2F34(reply);
        if (ack != GemAck.DrackAccepted) return ack;
        int r;
        lock (gate) r = run;
        transport.Post(() => ApplyReportTable(reports, r));
        return ack;
    }

    // 해석 표에 반영(디스패처, 또는 전송 Stop 스레드). r = 응답을 받은 실행 번호(Stop → Start 뒤의 늦은 갱신은 버림). 열쇠는 보낸 포맷 기준: A면 숫자 RPTID도 텍스트
    void ApplyReportTable(IReadOnlyList<(GemId Rptid, IReadOnlyList<GemId> Vids)> reports, int r)
    {
        lock (gate)
        {
            if (!running || run != r) return;
            if (reports.Count == 0) reportTable.Clear();
            foreach (var (rptid, vids) in reports)
            {
                var key = GemItems.Key(rptid, rptidFormat);
                if (vids.Count == 0) reportTable.Remove(key);
                else reportTable[key] = [.. vids];
            }
        }
    }

    /// <summary>Links reports to CEs (S2F35 &lt;L DATAID &lt;L &lt;L CEID &lt;L RPTID…&gt;&gt;…&gt;&gt;). An entry with an empty RPTID list unlinks everything from that CE.</summary>
    /// <param name="links">The list of (CEID, RPTID list) pairs.</param>
    /// <returns>LRACK (0 = accepted, 1 = no space, 2 = malformed, 3 = already linked, 4 = unknown CEID, 5 = unknown RPTID).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="links"/> or an RPTID list is null.</exception>
    /// <exception cref="ArgumentException">An ID cannot be represented in the configured format.</exception>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S2F0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body structure differs from what was expected.</exception>
    public byte LinkEvents(IReadOnlyList<(GemId Ceid, IReadOnlyList<GemId> Rptids)> links) => GemMessages.ParseS2F36(Ask(LinkEventsRequest(links)));

    /// <summary>Links reports to CEs (S2F35). The rules and exceptions are the same as for <see cref="LinkEvents"/>.</summary>
    /// <param name="links">The list of (CEID, RPTID list) pairs.</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>LRACK</returns>
    public async Task<byte> LinkEventsAsync(IReadOnlyList<(GemId Ceid, IReadOnlyList<GemId> Rptids)> links, CancellationToken cancellationToken = default)
    {
        var request = LinkEventsRequest(links);
        return GemMessages.ParseS2F36(await AskAsync(request, cancellationToken).ConfigureAwait(false));
    }

    SecsMessage LinkEventsRequest(IReadOnlyList<(GemId Ceid, IReadOnlyList<GemId> Rptids)> links)
    {
        var formats = Formats;
        return WithNextDataId(id => GemMessages.S2F35(id, links, formats));
    }

    /// <summary>Enables or disables CE reporting (S2F37 &lt;L CEED &lt;L CEID…&gt;&gt;).</summary>
    /// <param name="enable">true = enable, false = disable.</param>
    /// <param name="ceids">The CEIDs (if none, all CEs of the equipment).</param>
    /// <returns>ERACK (0 = accepted, 1 = an unknown CEID is present, so nothing was changed).</returns>
    /// <exception cref="ArgumentException">A CEID cannot be represented in <see cref="CeidFormat"/>.</exception>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S2F0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body structure differs from what was expected.</exception>
    public byte EnableEvents(bool enable, params GemId[] ceids) => GemMessages.ParseS2F38(Ask(GemMessages.S2F37(enable, ceids, Formats)));

    /// <summary>Enables or disables CE reporting (S2F37). The rules and exceptions are the same as for <see cref="EnableEvents"/>.</summary>
    /// <param name="enable">true = enable, false = disable.</param>
    /// <param name="ceids">The CEIDs (null or an empty list means all CEs).</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>ERACK</returns>
    public async Task<byte> EnableEventsAsync(bool enable, IReadOnlyList<GemId>? ceids = null, CancellationToken cancellationToken = default)
    {
        var request = GemMessages.S2F37(enable, ceids, Formats);
        return GemMessages.ParseS2F38(await AskAsync(request, cancellationToken).ConfigureAwait(false));
    }
    #endregion

    #region ConfigureReports
    /// <summary>Configures the reports on the equipment according to the subscription list (<see cref="Subscriptions"/>) (the same as connect sequence step 4).</summary>
    /// <remarks>
    /// <para>Order: (if <see cref="DeleteReportsFirst"/>) delete everything with an empty S2F33 list → disable all CEs with S2F37 (the ack is not checked) →
    /// assign RPTIDs starting from <see cref="ReportIdStart"/> to each subscription that has VIDs and define them with a single S2F33 → link them to the CEs with a single S2F35 → enable all subscribed CEs with S2F37 (including subscriptions without VIDs).</para>
    /// <para>If the equipment rejects with a nonzero ack, it stops at that step and reports it in the result (not as an exception). Communication exceptions (T3, SxF0, S9, disconnection) are thrown as they are. If called concurrently, the calls run one after another.</para>
    /// </remarks>
    /// <returns>The result (the failed step and the ack).</returns>
    /// <exception cref="ArgumentException">An ID in a subscription, or an RPTID to be assigned, cannot be represented in the configured format (nothing is sent).</exception>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S2F0 (for example, when OFF-LINE).</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body structure differs from what was expected.</exception>
    public GemReportSetupResult ConfigureReports() => Done(ConfigureReportsCore(SyncAsker, sync: true, CancellationToken.None));

    /// <summary>Configures the reports on the equipment according to the subscription list. The rules and exceptions are the same as for <see cref="ConfigureReports"/>.</summary>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>The result.</returns>
    public Task<GemReportSetupResult> ConfigureReportsAsync(CancellationToken cancellationToken = default)
        => ConfigureReportsCore(AsyncAsker(cancellationToken), sync: false, cancellationToken);

    // 몸통 하나: sync면 SyncAsker와 reportLock.Wait로 호출 스레드에서 끝까지 실행(이미 끝난 Task를 돌려줌), 아니면 SendAsync로 이어 감
    async Task<GemReportSetupResult> ConfigureReportsCore(Asker ask, bool sync, CancellationToken cancellationToken)
    {
        if (sync) reportLock.Wait(cancellationToken);
        else await reportLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<GemSubscription> subs;
            lock (gate) subs = [.. subscriptions];
            // 보내기 전에 모든 ID를 검사하고 RPTID를 매긴다(틀리면 아무것도 보내지 않음)
            var defs = new List<(GemId Rptid, IReadOnlyList<GemId> Vids)>();
            var links = new List<(GemId Ceid, IReadOnlyList<GemId> Rptids)>();
            long next = reportIdStart;
            foreach (var s in subs)
            {
                s.Ceid.ToItem(ceidFormat);
                foreach (var v in s.Vids) v.ToItem(vidFormat);
                if (s.Vids.Count == 0) continue;
                var rptid = rptidFormat == SecsFormat.ASCII ? new GemId("R" + next) : new GemId(next);
                rptid.ToItem(rptidFormat);
                next++;
                defs.Add((rptid, s.Vids));
                links.Add((s.Ceid, [rptid]));
            }

            if (deleteReportsFirst)
            {
                byte del = DefineReportsReply(await ask(DefineReportsRequest([])).ConfigureAwait(false), []);
                if (del != GemAck.DrackAccepted) return new GemReportSetupResult(GemReportSetupStep.DeleteReports, del);
                GemMessages.ParseS2F38(await ask(GemMessages.S2F37(false, null, Formats)).ConfigureAwait(false));     // 남은 CE 잡음 방지(ack는 보지 않음)
            }
            if (defs.Count > 0)
            {
                byte drack = DefineReportsReply(await ask(DefineReportsRequest(defs)).ConfigureAwait(false), defs);
                if (drack != GemAck.DrackAccepted) return new GemReportSetupResult(GemReportSetupStep.DefineReports, drack);
                byte lrack = GemMessages.ParseS2F36(await ask(LinkEventsRequest(links)).ConfigureAwait(false));
                if (lrack != GemAck.LrackAccepted) return new GemReportSetupResult(GemReportSetupStep.LinkEvents, lrack);
            }
            if (subs.Count > 0)
            {
                byte erack = GemMessages.ParseS2F38(await ask(GemMessages.S2F37(true, [.. subs.Select(s => s.Ceid)], Formats)).ConfigureAwait(false));
                if (erack != GemAck.ErackAccepted) return new GemReportSetupResult(GemReportSetupStep.EnableEvents, erack);
            }
            return new GemReportSetupResult(GemReportSetupStep.None, null);
        }
        finally { reportLock.Release(); }
    }
    #endregion
}
