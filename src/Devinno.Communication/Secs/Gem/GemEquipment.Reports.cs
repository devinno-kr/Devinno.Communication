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
        handlers[S2F33] = e => Respond(e, GemMessages.S2F34(DefineReports(e.Message)));
        handlers[S2F35] = e => Respond(e, GemMessages.S2F36(LinkReports(e.Message)));
        handlers[S2F37] = e => Respond(e, GemMessages.S2F38(EnableEvents(e.Message)));
        handlers[S2F39] = e => Respond(e, GemMessages.S2F40(GemAck.GrantGranted));    // 한도를 두지 않으므로 항상 허락
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
    /// <para>Cases where nothing is sent (false): CE disabled, communication DISABLED, control OFF-LINE. If ON-LINE but communication is not established, the result is false plus <see cref="SendFailed"/> (NotCommunicating), unless the message is put in the spool (see <see cref="SpoolDirectory"/>).</para>
    /// <para>While spooling is active, an S6F11 that the Host selected for spooling (S2F43) is put at the end of the spool even when communication is established, and it is not sent until the Host requests the spool with S6F23.</para>
    /// <para>S6F11 messages are sent one at a time in the order they were queued, and if ACKC6 is not 0, <see cref="SendFailed"/> (NotAcknowledged) is raised. They are not resent.</para>
    /// </remarks>
    /// <param name="ceid">CEID</param>
    /// <param name="dataValues">DV values used only for this report (defined DVs only, with the same format as the definition). If omitted, the stored value of the DV</param>
    /// <returns>true if it was put in the send queue or the spool</returns>
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
        var reps = ReportsLocked(ce, overrides);                 // 값을 먼저, DATAID는 그 뒤(지금 순서)
        var message = GemMessages.S6F11(NextDataIdLocked(), ce.Key, reps, formats, eventReplyExpected);
        return SendOrSpoolLocked(message);
    }

    partial void TriggerStandardEventLocked(GemStandard ce)
        => TriggerLocked(catalog.Events[catalog.Std(ce)], null);

    // S6F11/S6F16의 리포트: 링크 순서, 각 리포트의 지금 값(링크가 없으면 빈 목록)
    List<(GemId Rptid, IReadOnlyList<SecsItem> Values)> ReportsLocked(GemEventDef? ce, IReadOnlyDictionary<GemId, SecsItem>? overrides)
    {
        var rpts = new List<(GemId Rptid, IReadOnlyList<SecsItem> Values)>();
        if (ce != null && links.TryGetValue(ce.Key, out var linked))
            foreach (var rptid in linked) rpts.Add((rptid, ReportValuesLocked(rptid, overrides)));
        return rpts;
    }

    List<SecsItem> ReportValuesLocked(GemId rptid, IReadOnlyDictionary<GemId, SecsItem>? overrides)
        => [.. reports[rptid].Select(k => overrides != null && overrides.TryGetValue(k, out var o) ? o : ValueLocked(catalog.Vars[k]))];

    // DATAID: Start마다 1부터, DataidFormat의 최댓값을 넘으면 1로 (보낼 때 formats.Dataid로)
    GemId NextDataIdLocked()
    {
        long max = catalog.DataidFormat == SecsFormat.ASCII ? long.MaxValue : GemItems.IntegerRange(catalog.DataidFormat).Max;
        dataId = dataId >= max ? 1 : dataId + 1;
        return new GemId(dataId);
    }
    #endregion

    #region S2F33/35/37 (전부 검사한 뒤 한꺼번에 적용, 첫 오류의 코드)
    // S2F33 <L DATAID <L <L RPTID <L VID…>>…>> → DRACK. 2층 해석 실패(구조·ID 모양) = DRACK 1,
    // 그 뒤 3층 규칙(D2): RPTID는 설정 포맷이고 그 포맷으로 되돌려 보낼 수 있어야, VID 목록은 <L VID…>만, VID는 설정 포맷(아니면 DRACK 1)
    byte DefineReports(SecsMessage m)
    {
        IReadOnlyList<(GemId Rptid, IReadOnlyList<GemId> Vids)> parsed;
        try { parsed = GemMessages.ParseS2F33(m).Reports; }
        catch (FormatException) { return GemAck.DrackInvalidFormat; }
        var entries = m.Item!.Items[1].Items;
        var defs = new List<(GemId Rptid, List<GemId> Vids)>();
        for (int i = 0; i < parsed.Count; i++)
        {
            var r = entries[i];
            try
            {
                var rptid = Expect(parsed[i].Rptid, r.Items[0].Format, catalog.RptidFormat);
                if (!GemItems.Fits(rptid, catalog.RptidFormat)) return GemAck.DrackInvalidFormat;      // 설정 포맷으로 되돌려 보낼 수 없음
                defs.Add((rptid, ExpectList(r.Items[1], parsed[i].Vids, catalog.VidFormat)));
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

    // S2F35 <L DATAID <L <L CEID <L RPTID…>>…>> → LRACK. 2층 해석 실패 = LRACK 1, 그 뒤 CEID·RPTID 설정 포맷과 RPTID 목록은 <L …>만(D2, 아니면 LRACK 1)
    byte LinkReports(SecsMessage m)
    {
        IReadOnlyList<(GemId Ceid, IReadOnlyList<GemId> Rptids)> parsed;
        try { parsed = GemMessages.ParseS2F35(m).Links; }
        catch (FormatException) { return GemAck.LrackInvalidFormat; }
        var entries = m.Item!.Items[1].Items;
        var reqs = new List<(GemId Ceid, List<GemId> Rptids)>();
        for (int i = 0; i < parsed.Count; i++)
        {
            var l = entries[i];
            try { reqs.Add((Expect(parsed[i].Ceid, l.Items[0].Format, catalog.CeidFormat), ExpectList(l.Items[1], parsed[i].Rptids, catalog.RptidFormat))); }
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
    byte EnableEvents(SecsMessage m)
    {
        var (enable, ids) = GemMessages.ParseS2F37(m);
        var ceids = ExpectList(m.Item!.Items[1], ids, catalog.CeidFormat);     // <L CEID…>만, 설정 포맷(D2)
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
        var ceid = GemMessages.ParseS6F15(e.Message);                       // 본문 없음·ID 아님 = FormatException
        var item = e.Message.Item!;
        Expect(ceid, item.Format, catalog.CeidFormat);
        SecsMessage reply;
        lock (gate)
        {
            var ce = catalog.Events.GetValueOrDefault(ceid);
            var reps = ReportsLocked(ce, null);
            reply = GemMessages.S6F16(NextDataIdLocked().ToItem(catalog.DataidFormat), ce != null ? ce.Key.ToItem(catalog.CeidFormat) : item,
                [.. reps.Select(r => (r.Rptid.ToItem(catalog.RptidFormat), r.Values))]);    // 모르는 CEID는 받은 아이템 그대로(D3 겹치기)
        }
        Respond(e, reply);
    }

    // S6F19 RPTID → S6F20 <L V…>. 모르는 RPTID는 <L>
    void OnS6F19(SecsPrimaryEventArgs e)
    {
        var rptid = Expect(GemMessages.ParseS6F19(e.Message), e.Message.Item!.Format, catalog.RptidFormat);
        List<SecsItem> values;
        lock (gate) values = reports.ContainsKey(rptid) ? ReportValuesLocked(rptid, null) : [];
        Respond(e, GemMessages.S6F20(values));
    }
    #endregion
}
