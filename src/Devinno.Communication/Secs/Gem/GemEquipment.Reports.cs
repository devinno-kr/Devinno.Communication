using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

// 동적 리포트 설정 S2F33/35/37/39, 이벤트 리포트 S6F11(TriggerEvent), 요청 S6F15/S6F19
public partial class GemEquipment
{
    const int S2F33 = 2 * 256 + 33, S2F35 = 2 * 256 + 35, S2F37 = 2 * 256 + 37, S2F39 = 2 * 256 + 39, S6F15 = 6 * 256 + 15, S6F19 = 6 * 256 + 19;

    bool eventsEnabledByDefault, eventReplyExpected = true;
    // gate: Host가 정한 표(Start 때 비움, 연결이 끊겨도 유지)
    readonly Dictionary<GemId, List<GemId>> reports = [];     // RPTID → VID 열쇠 목록(순서 보존)
    readonly Dictionary<GemId, List<GemId>> links = [];       // CEID 열쇠 → RPTID 목록(링크 순서)
    readonly HashSet<GemId> enabledEvents = [];              // 활성 CEID 열쇠
    long dataId;                                              // 마지막으로 쓴 DATAID

    /// <summary>Gets or sets whether all CEs are enabled at Start (default: false). If false, the Host turns them on with S2F37.</summary>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public bool EventsEnabledByDefault { get => eventsEnabledByDefault; set => Setting(() => eventsEnabledByDefault = value); }

    /// <summary>Gets or sets whether S6F11 carries the W-bit (default: true). If true, ACKC6 other than 0 is reported through <see cref="SendFailed"/>.</summary>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public bool EventReplyExpected { get => eventReplyExpected; set => Setting(() => eventReplyExpected = value); }

    partial void AddReportHandlers()
    {
        handlers[S2F33] = e => Respond(e, GemItems.Ack(DefineReports(e.Message.Item)));
        handlers[S2F35] = e => Respond(e, GemItems.Ack(LinkReports(e.Message.Item)));
        handlers[S2F37] = e => Respond(e, GemItems.Ack(EnableEvents(e.Message.Item)));
        handlers[S2F39] = e => Respond(e, GemItems.Ack(GemAck.GrantGranted));    // 한도를 두지 않으므로 항상 허락
        handlers[S6F15] = OnS6F15;
        handlers[S6F19] = OnS6F19;
        standardValues[GemStandard.EventsEnabled] = () => GemItems.IdList(catalog.EventList.Where(e => enabledEvents.Contains(e.Key)).Select(e => e.Key), catalog.CeidFormat);
    }

    partial void ResetReports()
    {
        reports.Clear();
        links.Clear();
        enabledEvents.Clear();
        if (eventsEnabledByDefault) foreach (var e in catalog.EventList) enabledEvents.Add(e.Key);
        dataId = 0;
    }

    #region TriggerEvent
    /// <summary>Reports that a collection event occurred. If the Host has enabled it, S6F11 is sent with the linked reports.</summary>
    /// <remarks>
    /// <para>Values are captured at the moment of the call (the body is built on this thread and put in the queue). Even if the send happens later, the values are those at the time of the event.</para>
    /// <para>Cases where nothing is sent (false): CE disabled, communication DISABLED, control OFF-LINE. If ON-LINE but communication is not established, the result is false plus <see cref="SendFailed"/> (NotCommunicating).</para>
    /// <para>S6F11 messages are sent one at a time in the order they were queued, and if ACKC6 is not 0, <see cref="SendFailed"/> (NotAcknowledged) is raised. They are not resent.</para>
    /// </remarks>
    /// <param name="ceid">CEID</param>
    /// <param name="dataValues">DV values used only for this report (defined DVs only, with the same format as the definition). If omitted, the stored value of the DV</param>
    /// <returns>true if it was put in the send queue</returns>
    /// <exception cref="ArgumentException">The CEID is not defined, or dataValues contains a VID that is not a DV or a value with a different format</exception>
    /// <exception cref="InvalidOperationException">Not started</exception>
    public bool TriggerEvent(GemId ceid, IReadOnlyDictionary<GemId, SecsItem>? dataValues = null)
    {
        Started();
        lock (gate)
        {
            if (!catalog.Events.TryGetValue(GemItems.Key(ceid, catalog.CeidFormat), out var ce))
                throw new ArgumentException($"정의되지 않은 CEID {ceid}입니다.", nameof(ceid));
            Dictionary<GemId, SecsItem>? overrides = null;
            if (dataValues != null)
            {
                foreach (var (vid, value) in dataValues)
                {
                    if (!catalog.Vars.TryGetValue(GemItems.Key(vid, catalog.VidFormat), out var v) || v.Kind != GemVarKind.Data)
                        throw new ArgumentException($"dataValues의 {vid}는 정의된 DV가 아닙니다.", nameof(dataValues));
                    ArgumentNullException.ThrowIfNull(value, nameof(dataValues));
                    if (!GemItems.Matches(value, v)) throw new ArgumentException($"DV {vid}의 포맷은 {GemItems.FormatName(v.Format)}입니다. 받은 값: {value.ToSml()}", nameof(dataValues));
                    (overrides ??= [])[v.Key] = value;
                }
            }
            return TriggerLocked(ce, overrides);
        }
    }

    // 이벤트를 보낼지 판단하고 본문을 지금 값으로 만든다(gate 안). 제어 OFF-LINE이면 보내지 않는다(OFF-LINE으로 가는 전이 6·10의 CE는 상태를 바꾸기 전에 부름)
    bool TriggerLocked(GemEventDef ce, IReadOnlyDictionary<GemId, SecsItem>? overrides)
    {
        if (!running || commState == GemCommunicationState.Disabled || IsOffline(control)) return false;
        if (!enabledEvents.Contains(ce.Key)) return false;
        var message = new SecsMessage(6, 11, eventReplyExpected, EventBodyLocked(ce.Key.ToItem(catalog.CeidFormat), ce, overrides));
        if (commState != GemCommunicationState.Communicating)
        {
            DiscardLocked(message, GemSendFailure.NotCommunicating);
            return false;
        }
        return EnqueueLocked(message);
    }

    partial void TriggerStandardEventLocked(GemStandard ce)
        => TriggerLocked(catalog.Events[catalog.Std(ce)], null);

    // S6F11/S6F16 본문: <L DATAID CEID <L <L RPTID <L V…>>…>> (리포트는 링크 순서, 링크가 없으면 빈 L)
    SecsItem EventBodyLocked(SecsItem ceidItem, GemEventDef? ce, IReadOnlyDictionary<GemId, SecsItem>? overrides)
    {
        var rpts = new List<SecsItem>();
        if (ce != null && links.TryGetValue(ce.Key, out var linked))
            foreach (var rptid in linked) rpts.Add(SecsItem.L(rptid.ToItem(catalog.RptidFormat), ReportValuesLocked(rptid, overrides)));
        return SecsItem.L(NextDataIdLocked(), ceidItem, SecsItem.L([.. rpts]));
    }

    SecsItem ReportValuesLocked(GemId rptid, IReadOnlyDictionary<GemId, SecsItem>? overrides)
        => SecsItem.L([.. reports[rptid].Select(k => overrides != null && overrides.TryGetValue(k, out var o) ? o : ValueLocked(catalog.Vars[k]))]);

    // DATAID: Start마다 1부터, DataidFormat의 최댓값을 넘으면 1로
    SecsItem NextDataIdLocked()
    {
        long max = catalog.DataidFormat == SecsFormat.ASCII ? long.MaxValue : GemItems.IntegerRange(catalog.DataidFormat).Max;
        dataId = dataId >= max ? 1 : dataId + 1;
        return new GemId(dataId).ToItem(catalog.DataidFormat);
    }
    #endregion

    #region S2F33/35/37 (전부 검사한 뒤 한꺼번에 적용, 첫 오류의 코드)
    // S2F33 <L DATAID <L <L RPTID <L VID…>>…>> → DRACK
    byte DefineReports(SecsItem? body)
    {
        if (body is not { Format: SecsFormat.List, Count: 2 } || body.Items[1].Format != SecsFormat.List) return GemAck.DrackInvalidFormat;
        var defs = new List<(GemId Rptid, List<GemId> Vids)>();
        foreach (var r in body.Items[1].Items)
        {
            if (r is not { Format: SecsFormat.List, Count: 2 }) return GemAck.DrackInvalidFormat;
            try
            {
                var rptid = GemItems.ReadId(r.Items[0], catalog.RptidFormat);
                if (!GemItems.Fits(rptid, catalog.RptidFormat)) return GemAck.DrackInvalidFormat;      // 설정 포맷으로 되돌려 보낼 수 없음
                defs.Add((rptid, GemItems.ReadIdList(r.Items[1], catalog.VidFormat)));
            }
            catch (FormatException) { return GemAck.DrackInvalidFormat; }
        }
        lock (gate)
        {
            if (defs.Count == 0)
            {
                reports.Clear();                      // 모든 리포트와 링크 삭제
                links.Clear();
                return GemAck.DrackAccepted;
            }
            var seen = new HashSet<GemId>();
            foreach (var (rptid, vids) in defs)
            {
                if (!seen.Add(rptid)) return GemAck.DrackAlreadyDefined;                            // 이번 메시지 안 중복
                if (vids.Count == 0) continue;                                                     // 삭제(없는 RPTID도 0)
                if (reports.ContainsKey(rptid)) return GemAck.DrackAlreadyDefined;
                if (vids.Any(v => !catalog.Vars.ContainsKey(v))) return GemAck.DrackUnknownVariable;
            }
            foreach (var (rptid, vids) in defs)
            {
                if (vids.Count > 0)
                {
                    reports[rptid] = vids;
                    continue;
                }
                reports.Remove(rptid);
                foreach (var ce in links.Keys.ToList())
                {
                    links[ce].Remove(rptid);
                    if (links[ce].Count == 0) links.Remove(ce);
                }
            }
            return GemAck.DrackAccepted;
        }
    }

    // S2F35 <L DATAID <L <L CEID <L RPTID…>>…>> → LRACK
    byte LinkReports(SecsItem? body)
    {
        if (body is not { Format: SecsFormat.List, Count: 2 } || body.Items[1].Format != SecsFormat.List) return GemAck.LrackInvalidFormat;
        var reqs = new List<(GemId Ceid, List<GemId> Rptids)>();
        foreach (var l in body.Items[1].Items)
        {
            if (l is not { Format: SecsFormat.List, Count: 2 }) return GemAck.LrackInvalidFormat;
            try { reqs.Add((GemItems.ReadId(l.Items[0], catalog.CeidFormat), GemItems.ReadIdList(l.Items[1], catalog.RptidFormat))); }
            catch (FormatException) { return GemAck.LrackInvalidFormat; }
        }
        lock (gate)
        {
            var seen = new HashSet<GemId>();
            foreach (var (ceid, rptids) in reqs)
            {
                if (!seen.Add(ceid)) return GemAck.LrackAlreadyLinked;
                if (!catalog.Events.ContainsKey(ceid)) return GemAck.LrackUnknownEvent;
                if (rptids.Count == 0) continue;                                                   // 링크 해제
                if (links.ContainsKey(ceid)) return GemAck.LrackAlreadyLinked;                    // 덧붙이지 않음
                if (rptids.Any(r => !reports.ContainsKey(r))) return GemAck.LrackUnknownReport;
            }
            foreach (var (ceid, rptids) in reqs)
            {
                if (rptids.Count == 0) links.Remove(ceid);
                else links[ceid] = rptids;                                                         // 활성화 상태는 바꾸지 않음
            }
            return GemAck.LrackAccepted;
        }
    }

    // S2F37 <L CEED <L CEID…>> → ERACK. CEED는 BOOLEAN(관대하게 B·U1: 0이 아니면 켬). 빈 목록 = 모든 CE. 구조 오류는 S9F7(ERACK에 형식 오류 코드가 없음)
    byte EnableEvents(SecsItem? body)
    {
        if (body is not { Format: SecsFormat.List, Count: 2 }) throw new FormatException("S2F37 본문은 <L CEED <L CEID…>>입니다.");
        var ceed = body.Items[0];
        bool enable = ceed switch
        {
            { Format: SecsFormat.Boolean, Count: 1 } => ceed.GetBooleans()[0],
            { Format: SecsFormat.Binary or SecsFormat.U1, Count: 1 } => ceed.GetInt64() != 0,
            _ => throw new FormatException($"CEED는 BOOLEAN이어야 합니다: {GemItems.Sml(ceed)}"),
        };
        var ceids = GemItems.ReadIdList(body.Items[1], catalog.CeidFormat);
        lock (gate)
        {
            if (ceids.Any(c => !catalog.Events.ContainsKey(c))) return GemAck.ErackUnknownEvent;
            foreach (var c in ceids.Count == 0 ? catalog.EventList.Select(e => e.Key) : ceids)
            {
                if (enable) enabledEvents.Add(c);
                else enabledEvents.Remove(c);
            }
            return GemAck.ErackAccepted;
        }
    }
    #endregion

    #region S6F15/S6F19
    // S6F15 CEID → S6F16 = S6F11과 같은 본문(지금 값, 활성화와 무관). 모르는 CEID는 <L DATAID CEID <L>>
    void OnS6F15(SecsPrimaryEventArgs e)
    {
        var item = e.Message.Item ?? throw new FormatException("S6F15에 CEID가 없습니다.");
        var ceid = GemItems.ReadId(item, catalog.CeidFormat);
        SecsItem body;
        lock (gate)
        {
            var ce = catalog.Events.GetValueOrDefault(ceid);
            body = EventBodyLocked(ce != null ? ce.Key.ToItem(catalog.CeidFormat) : item, ce, null);
        }
        Respond(e, body);
    }

    // S6F19 RPTID → S6F20 <L V…>. 모르는 RPTID는 <L>
    void OnS6F19(SecsPrimaryEventArgs e)
    {
        var rptid = GemItems.ReadId(e.Message.Item ?? throw new FormatException("S6F19에 RPTID가 없습니다."), catalog.RptidFormat);
        SecsItem body;
        lock (gate) body = reports.ContainsKey(rptid) ? ReportValuesLocked(rptid, null) : SecsItem.L();
        Respond(e, body);
    }
    #endregion
}
