using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

/// <summary>A single value inside an event report.</summary>
/// <param name="vid">The VID of this value (the VID if the report was defined by the Host; null if the report is unknown or the number of values differs from the definition).</param>
/// <param name="value">The value.</param>
public sealed class GemReportValue(GemId? vid, SecsItem value)
{
    /// <summary>Gets the VID of this value (null if unknown).</summary>
    public GemId? Vid { get; } = vid;
    /// <summary>Gets the value.</summary>
    public SecsItem Value { get; } = value;
}

/// <summary>A single report inside an event report.</summary>
/// <param name="rptid">RPTID</param>
/// <param name="values">The list of values (in the VID order of the report definition).</param>
public sealed class GemReportData(GemId rptid, IReadOnlyList<GemReportValue> values)
{
    /// <summary>Gets the RPTID.</summary>
    public GemId Rptid { get; } = rptid;
    /// <summary>Gets the list of values (in the VID order of the report definition).</summary>
    public IReadOnlyList<GemReportValue> Values { get; } = values;
}

/// <summary>An interpreted event report (S6F11, S6F16).</summary>
/// <param name="dataId">The DATAID (the item exactly as the equipment sent it).</param>
/// <param name="ceid">CEID</param>
/// <param name="reports">The list of reports (in the order the equipment sent them).</param>
public sealed class GemEventReport(SecsItem dataId, GemId ceid, IReadOnlyList<GemReportData> reports)
{
    /// <summary>Gets the DATAID (the item exactly as the equipment sent it; the format differs between equipment).</summary>
    public SecsItem DataId { get; } = dataId;
    /// <summary>Gets the CEID (read in the format the equipment sent).</summary>
    /// <remarks>If the equipment sent it as A (usually when <c>CeidFormat</c> is A), it is a text ID even if it looks like a number. For example, <c>Ceid == new GemId("5001")</c> is true and <c>Ceid == 5001</c> is false.</remarks>
    public GemId Ceid { get; } = ceid;
    /// <summary>Gets the list of reports (in the order the equipment sent them).</summary>
    public IReadOnlyList<GemReportData> Reports { get; } = reports;

    /// <summary>Finds a value by VID (the first one if it appears in several reports).</summary>
    /// <param name="vid">VID</param>
    /// <returns>The value, or null if there is none (or the report does not know its VIDs).</returns>
    public SecsItem? GetValue(GemId vid)
    {
        foreach (var r in Reports)
            foreach (var v in r.Values)
                if (v.Vid == vid) return v.Value;
        return null;
    }
}

/// <summary>The arguments for when the equipment sends an event report (S6F11).</summary>
/// <param name="report">The interpreted report.</param>
/// <param name="message">The received S6F11 as-is.</param>
public class GemEventReportEventArgs(GemEventReport report, SecsMessage message) : EventArgs
{
    /// <summary>Gets the interpreted report.</summary>
    public GemEventReport Report { get; } = report;
    /// <summary>Gets the received S6F11 as-is.</summary>
    public SecsMessage Message { get; } = message;
}

/// <summary>The arguments for when the equipment sends an alarm (S5F1).</summary>
/// <param name="alid">ALID</param>
/// <param name="alcd">The ALCD (0x80 = set, lower 7 bits = category).</param>
/// <param name="text">The alarm text ALTX.</param>
public class GemAlarmEventArgs(GemId alid, byte alcd, string text) : EventArgs
{
    /// <summary>ALID</summary>
    public GemId Alid { get; } = alid;
    /// <summary>Gets the ALCD (0x80 = set, lower 7 bits = category).</summary>
    public byte Alcd { get; } = alcd;
    /// <summary>Gets a value indicating whether the alarm is set (SET) rather than cleared (CLEAR).</summary>
    public bool IsSet => (Alcd & GemAck.AlcdSet) != 0;
    /// <summary>Gets the alarm category (the lower 7 bits of ALCD).</summary>
    public int Category => Alcd & 0x7F;
    /// <summary>Gets the alarm text ALTX.</summary>
    public string Text { get; } = text;
}

/// <summary>The information of a single alarm (an S5F6 or S5F8 entry).</summary>
/// <param name="alid">ALID</param>
/// <param name="alcd">The ALCD (0x80 = currently set, lower 7 bits = category, 0 if the equipment does not know the ALID).</param>
/// <param name="text">The alarm text ("" if the equipment does not know the ALID).</param>
public sealed class GemAlarmInfo(GemId alid, byte alcd, string text)
{
    /// <summary>ALID</summary>
    public GemId Alid { get; } = alid;
    /// <summary>Gets the ALCD (0x80 = currently set, lower 7 bits = category, 0 if the equipment does not know the ALID).</summary>
    public byte Alcd { get; } = alcd;
    /// <summary>Gets a value indicating whether the alarm is currently set.</summary>
    public bool IsSet => (Alcd & GemAck.AlcdSet) != 0;
    /// <summary>Gets the alarm category (the lower 7 bits of ALCD).</summary>
    public int Category => Alcd & 0x7F;
    /// <summary>Gets the alarm text ("" if the equipment does not know the ALID).</summary>
    public string Text { get; } = text;
}

/// <summary>The result of a remote command (S2F41). A nonzero HCACK is also a value, not an exception.</summary>
/// <param name="hcack">HCACK</param>
/// <param name="parameterAcks">The list of parameter errors (CPNAME, CPACK) (copied and kept read-only).</param>
public sealed class GemCommandResult(byte hcack, IReadOnlyList<(string Name, byte Cpack)> parameterAcks)
{
    /// <summary>Gets the HCACK (0 = executed, 1 = unknown command, 2 = cannot be done now, 3 = parameter error, 4 = accepted, will finish later).</summary>
    public byte Hcack { get; } = hcack;
    /// <summary>Gets the list of parameter errors (CPNAME, CPACK) (CPACK 1 = unknown name, 2 = invalid value, 3 = invalid format).</summary>
    public IReadOnlyList<(string Name, byte Cpack)> ParameterAcks { get; } = Array.AsReadOnly(parameterAcks.ToArray());
    /// <summary>Gets a value indicating whether the equipment accepted the command (HCACK 0 or 4).</summary>
    public bool IsAccepted => Hcack is GemAck.HcackAccepted or GemAck.HcackAcceptedLater;
}

// 장비 보고 받기(S6F11·S5F1·S10F1 자동 응답 + 해석 이벤트), 리포트 요청 S6F15/S6F19, 알람 S5F3/5/7, 터미널 S10F3, 리모트 명령 S2F41
public partial class GemHost
{
    const int S5F1 = 5 * 256 + 1, S6F11 = 6 * 256 + 11, S10F1 = 10 * 256 + 1;
    // 값 개수가 정의와 다른 리포트를 알릴 때 RPTID를 몇 개까지 적을지(메시지 하나에 모아 알림)
    const int MismatchListed = 5;

    /// <summary>Raised on the app event thread when an event report (S6F11) is received from the equipment (already answered with ACKC6 0).</summary>
    /// <remarks>
    /// <para>CEID, RPTID, and VID are accepted in whatever format the equipment sends. For reports the Host defined (<see cref="ConfigureReports"/>, <see cref="DefineReports"/>), each value carries its VID;
    /// for unknown reports, only the values are delivered, without VIDs. If the number of values differs from the definition, that report is delivered without VIDs, and even if there are several such reports, one <see cref="ProtocolError"/> per message reports them (the count and the first few RPTIDs).</para>
    /// <para>If <see cref="RptidFormat"/> is A, the interpretation table also matches numeric RPTIDs as decimal text (definition 1000 = received A "1000"). <see cref="GemEventReport.Ceid"/> is in the format the equipment sent, so if it arrived as A, it is a text ID.</para>
    /// <para>If the body structure is wrong or the CEID or RPTID cannot be read as an ID, no reply is sent (S6F0) so that the equipment learns of the failure, and it is reported through <see cref="ProtocolError"/> (no event is raised).</para>
    /// <para>VIDs are attached with the interpretation table at the point this S6F11 was received: an S6F11 received before the equipment's reply (S2F34) to <see cref="DefineReports"/> (also used by <see cref="ConfigureReports"/>) is interpreted with the old definitions, and one received after the method returns, with the new ones.</para>
    /// </remarks>
    public event EventHandler<GemEventReportEventArgs>? EventReceived;

    /// <summary>Raised on the app event thread when an alarm (S5F1) is received from the equipment (already answered with ACKC5 0).</summary>
    public event EventHandler<GemAlarmEventArgs>? AlarmReceived;

    /// <summary>Raised on the app event thread when a terminal message (S10F1) is received from the equipment (already answered with ACKC10 0).</summary>
    /// <remarks>If there are no subscribers, the reply is ACKC10 2 (no terminal).</remarks>
    public event EventHandler<GemTerminalEventArgs>? TerminalReceived;

    partial void AddEventHandlers()
    {
        handlers[S6F11] = OnS6F11;
        handlers[S5F1] = OnS5F1;
        handlers[S10F1] = OnS10F1;
    }

    #region 받기 (디스패처)
    // S6F11 <L DATAID CEID <L <L RPTID <L V…>>…>> → 해석이 되면 ACKC6 0 → EventReceived. 해석이 안 되면 응답하지 않음(SxF0)
    void OnS6F11(SecsPrimaryEventArgs e)
    {
        var report = DecodeEventReport(e.Message.Item, out var warning);
        Respond(e, GemItems.Ack(GemAck.Ackc6Accepted));
        if (warning != null) Error(warning);
        Raise(EventReceived, new GemEventReportEventArgs(report, e.Message));
    }

    // 이벤트 리포트 본문 해석(S6F11·S6F16). 해석 표는 받은 순간의 것을 쓴다. 구조 오류는 FormatException(SML은 앞부분만),
    // 값 개수가 정의와 다른 리포트는 모두 모아 알림 문장 하나(없으면 null) — 장비가 그런 리포트를 많이 보내도 앱 이벤트 대기열을 채우지 않게
    GemEventReport DecodeEventReport(SecsItem? body, out string? warning)
    {
        warning = null;
        if (body is not { Format: SecsFormat.List, Count: 3 } || body.Items[2].Format != SecsFormat.List)
            throw new FormatException($"이벤트 리포트 본문은 <L DATAID CEID <L <L RPTID <L V…>>…>>입니다: {GemItems.Sml(body)}");
        var ceid = GemId.FromItem(body.Items[1]);
        var reports = new List<GemReportData>();
        int mismatches = 0;
        List<string>? listed = null;
        foreach (var r in body.Items[2].Items)
        {
            if (r is not { Format: SecsFormat.List, Count: 2 } || r.Items[1].Format != SecsFormat.List)
                throw new FormatException($"리포트는 <L RPTID <L V…>>입니다: {GemItems.Sml(r)}");
            var rptid = GemId.FromItem(r.Items[0]);
            var values = r.Items[1].Items;
            var vids = KnownReport(rptid);
            if (vids != null && vids.Count != values.Count)
            {
                if (mismatches++ < MismatchListed) (listed ??= []).Add($"{GemItems.IdText(rptid)}(값 {values.Count}개/정의 {vids.Count}개)");
                vids = null;
            }
            reports.Add(new GemReportData(rptid, [.. values.Select((v, i) => new GemReportValue(vids?[i], v))]));
        }
        if (mismatches > 0)
            warning = $"CEID {GemItems.IdText(ceid)}: 리포트 {mismatches}개의 값 개수가 정의와 달라 VID 없이 줍니다: {string.Join(", ", listed!)}{(mismatches > MismatchListed ? ", …" : "")}";
        return new GemEventReport(body.Items[0], ceid, reports);
    }

    // S5F1 <L ALCD ALID ALTX> → ACKC5 0 → AlarmReceived
    void OnS5F1(SecsPrimaryEventArgs e)
    {
        if (e.Message.Item is not { Format: SecsFormat.List, Count: 3 } body || body.Items[2].Format != SecsFormat.ASCII)
            throw new FormatException("S5F1 본문은 <L ALCD ALID ALTX>입니다.");
        byte alcd = GemItems.ReadAck(body.Items[0]);
        var alid = GemId.FromItem(body.Items[1]);
        Respond(e, GemItems.Ack(GemAck.Ackc5Accepted));
        Raise(AlarmReceived, new GemAlarmEventArgs(alid, alcd, body.Items[2].GetString()));
    }

    // S10F1 <L TID TEXT> → ACKC10 (구독자 없으면 2) → TerminalReceived
    void OnS10F1(SecsPrimaryEventArgs e)
    {
        if (e.Message.Item is not { Format: SecsFormat.List, Count: 2 } body || body.Items[1].Format != SecsFormat.ASCII)
            throw new FormatException("S10F1 본문은 <L TID TEXT>입니다.");
        byte tid = GemItems.ReadAck(body.Items[0]);
        var handler = TerminalReceived;
        Respond(e, GemItems.Ack(handler == null ? GemAck.Ackc10TerminalNotAvailable : GemAck.Ackc10Accepted));
        Raise(handler, new GemTerminalEventArgs(tid, [body.Items[1].GetString()]));
    }
    #endregion

    #region S6F15 / S6F19
    /// <summary>Requests an event report with the current values of a CE (S6F15). The interpretation rules are the same as for <see cref="EventReceived"/>.</summary>
    /// <param name="ceid">The CEID (sent in <see cref="CeidFormat"/>).</param>
    /// <returns>The interpreted report.</returns>
    /// <exception cref="ArgumentException">The CEID cannot be represented in <see cref="CeidFormat"/>.</exception>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S6F0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body cannot be interpreted.</exception>
    public GemEventReport RequestEventReport(GemId ceid) => EventReportReply(Ask(new SecsMessage(6, 15, true, ceid.ToItem(ceidFormat))));

    /// <summary>Requests an event report with the current values of a CE (S6F15). The rules and exceptions are the same as for <see cref="RequestEventReport"/>.</summary>
    /// <param name="ceid">CEID</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>The interpreted report.</returns>
    public async Task<GemEventReport> RequestEventReportAsync(GemId ceid, CancellationToken cancellationToken = default)
    {
        var request = new SecsMessage(6, 15, true, ceid.ToItem(ceidFormat));
        return EventReportReply(await AskAsync(request, cancellationToken).ConfigureAwait(false));
    }

    GemEventReport EventReportReply(SecsMessage reply)
    {
        GemEventReport report;
        string? warning;
        try { report = DecodeEventReport(reply.Item, out warning); }
        catch (FormatException) { throw Unexpected(reply, "<L DATAID CEID <L <L RPTID <L V…>>…>>"); }
        if (warning != null) Error(warning);
        return report;
    }

    /// <summary>Requests the current values of a single report (S6F19).</summary>
    /// <param name="rptid">The RPTID (sent in <see cref="RptidFormat"/>).</param>
    /// <returns>The list of values (an empty list if the equipment does not know the RPTID).</returns>
    /// <exception cref="ArgumentException">The RPTID cannot be represented in <see cref="RptidFormat"/>.</exception>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S6F0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body is not a list.</exception>
    public IReadOnlyList<SecsItem> RequestReport(GemId rptid) => ReportValuesReply(Ask(new SecsMessage(6, 19, true, rptid.ToItem(rptidFormat))));

    /// <summary>Requests the current values of a single report (S6F19). The rules and exceptions are the same as for <see cref="RequestReport"/>.</summary>
    /// <param name="rptid">RPTID</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>The list of values.</returns>
    public async Task<IReadOnlyList<SecsItem>> RequestReportAsync(GemId rptid, CancellationToken cancellationToken = default)
    {
        var request = new SecsMessage(6, 19, true, rptid.ToItem(rptidFormat));
        return ReportValuesReply(await AskAsync(request, cancellationToken).ConfigureAwait(false));
    }

    static IReadOnlyList<SecsItem> ReportValuesReply(SecsMessage reply)
        => reply.Item is { Format: SecsFormat.List } list ? [.. list.Items] : throw Unexpected(reply, "<L V…>");
    #endregion

    #region S5F3 / S5F5 / S5F7
    /// <summary>Enables or disables alarm reporting (S5F3 &lt;L ALED ALID&gt;; ALED = 0x80 enables).</summary>
    /// <param name="alid">The ALID (if null, all alarms: a zero-length ALID).</param>
    /// <param name="enable">true = enable, false = disable.</param>
    /// <returns>ACKC5 (0 = accepted, 1 = error, such as an unknown ALID).</returns>
    /// <exception cref="ArgumentException">The ALID cannot be represented in <see cref="AlidFormat"/>.</exception>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S5F0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body structure differs from what was expected.</exception>
    public byte EnableAlarm(GemId? alid, bool enable) => Ackc5Reply(Ask(EnableAlarmRequest(alid, enable)));

    /// <summary>Enables or disables alarm reporting (S5F3). The rules and exceptions are the same as for <see cref="EnableAlarm"/>.</summary>
    /// <param name="alid">The ALID (if null, all alarms).</param>
    /// <param name="enable">true = enable, false = disable.</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>ACKC5.</returns>
    public async Task<byte> EnableAlarmAsync(GemId? alid, bool enable, CancellationToken cancellationToken = default)
    {
        var request = EnableAlarmRequest(alid, enable);
        return Ackc5Reply(await AskAsync(request, cancellationToken).ConfigureAwait(false));
    }

    SecsMessage EnableAlarmRequest(GemId? alid, bool enable)
    {
        var id = alid is { } a ? a.ToItem(alidFormat) : GemItems.Empty(alidFormat);
        return new SecsMessage(5, 3, true, SecsItem.L(SecsItem.B(enable ? GemAck.AledEnable : (byte)0), id));
    }

    static byte Ackc5Reply(SecsMessage reply) => AckOf(reply.Item, reply, "ACKC5");

    /// <summary>Reads the alarm list (S5F5).</summary>
    /// <param name="alids">The ALIDs to read (if none, all alarms). If <see cref="AlidFormat"/> is numeric, they are sent as a single array; if A, only zero or one is allowed.</param>
    /// <returns>The list of alarm information.</returns>
    /// <exception cref="ArgumentException">An ALID cannot be represented in <see cref="AlidFormat"/>, or the format is A and there are two or more.</exception>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S5F0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body structure differs from what was expected, or the number of entries differs from the request.</exception>
    public IReadOnlyList<GemAlarmInfo> ListAlarms(params GemId[] alids) => AlarmList(Ask(ListAlarmsRequest(alids ??= [])), alids);

    /// <summary>Reads the alarm list (S5F5). The rules and exceptions are the same as for <see cref="ListAlarms"/>.</summary>
    /// <param name="alids">The ALIDs to read (null or an empty list means all alarms).</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>The list of alarm information.</returns>
    public async Task<IReadOnlyList<GemAlarmInfo>> ListAlarmsAsync(IReadOnlyList<GemId>? alids = null, CancellationToken cancellationToken = default)
    {
        alids ??= [];
        var request = ListAlarmsRequest(alids);
        return AlarmList(await AskAsync(request, cancellationToken).ConfigureAwait(false), alids);
    }

    SecsMessage ListAlarmsRequest(IReadOnlyList<GemId> alids)
    {
        SecsItem request;
        if (alidFormat != SecsFormat.ASCII) request = GemItems.IdList(alids, alidFormat);
        else if (alids.Count <= 1) request = alids.Count == 0 ? SecsItem.A("") : alids[0].ToItem(SecsFormat.ASCII);
        else throw new ArgumentException("AlidFormat이 A면 ALID는 0개 또는 1개만 보낼 수 있습니다(A 배열 없음).", nameof(alids));
        return new SecsMessage(5, 5, true, request);
    }

    /// <summary>Reads the list of alarms whose reporting is enabled (S5F7).</summary>
    /// <returns>The list of alarm information.</returns>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S5F0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body structure differs from what was expected.</exception>
    public IReadOnlyList<GemAlarmInfo> ListEnabledAlarms() => AlarmList(Ask(new SecsMessage(5, 7, true)), null);

    /// <summary>Reads the list of alarms whose reporting is enabled (S5F7). The rules and exceptions are the same as for <see cref="ListEnabledAlarms"/>.</summary>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>The list of alarm information.</returns>
    public async Task<IReadOnlyList<GemAlarmInfo>> ListEnabledAlarmsAsync(CancellationToken cancellationToken = default)
    {
        return AlarmList(await AskAsync(new SecsMessage(5, 7, true), cancellationToken).ConfigureAwait(false), null);
    }

    // <L <L ALCD ALID ALTX>…>. 장비가 모르는 ALID는 ALCD가 길이 0(→ 0)
    static IReadOnlyList<GemAlarmInfo> AlarmList(SecsMessage reply, IReadOnlyList<GemId>? requested)
    {
        const string expected = "<L <L ALCD ALID ALTX>…>";
        return [.. Entries(reply, requested, expected).Select(e =>
        {
            if (e is not { Format: SecsFormat.List, Count: 3 } || e.Items[2].Format != SecsFormat.ASCII) throw Unexpected(reply, expected);
            byte alcd = e.Items[0] is { Count: 0, Format: not (SecsFormat.List or SecsFormat.ASCII) } ? (byte)0 : AckOf(e.Items[0], reply, expected);
            return new GemAlarmInfo(IdOf(e.Items[1], reply, expected), alcd, e.Items[2].GetString());
        })];
    }
    #endregion

    #region S10F3
    /// <summary>Sends text to the equipment terminal (S10F3 &lt;L TID TEXT&gt;).</summary>
    /// <param name="tid">The terminal number.</param>
    /// <param name="text">The text (ASCII).</param>
    /// <returns>ACKC10 (0 = displayed, 1 = not displayed, 2 = no terminal).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="text"/> contains characters outside ASCII (Latin-1).</exception>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S10F0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body structure differs from what was expected.</exception>
    public byte SendTerminal(byte tid, string text) => Ackc10Reply(Ask(TerminalRequest(tid, text)));

    /// <summary>Sends text to the equipment terminal (S10F3). The rules and exceptions are the same as for <see cref="SendTerminal"/>.</summary>
    /// <param name="tid">The terminal number.</param>
    /// <param name="text">The text (ASCII).</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>ACKC10.</returns>
    public async Task<byte> SendTerminalAsync(byte tid, string text, CancellationToken cancellationToken = default)
    {
        var request = TerminalRequest(tid, text);
        return Ackc10Reply(await AskAsync(request, cancellationToken).ConfigureAwait(false));
    }

    static SecsMessage TerminalRequest(byte tid, string text)
    {
        GemDefinitionCheck.Ascii(text, nameof(text));
        return new SecsMessage(10, 3, true, SecsItem.L(SecsItem.B(tid), SecsItem.A(text)));
    }

    static byte Ackc10Reply(SecsMessage reply) => AckOf(reply.Item, reply, "ACKC10");
    #endregion

    #region S2F41
    /// <summary>Sends a remote command (S2F41 &lt;L RCMD &lt;L &lt;L CPNAME CPVAL&gt;…&gt;&gt;). RCMD and CPNAME are sent as A.</summary>
    /// <remarks>If the equipment is not ON-LINE REMOTE, the result is usually HCACK 2 (LOCAL) or S2F0 (OFF-LINE). For a long-running command, the equipment accepts it with HCACK 4 and reports the completion through an event.</remarks>
    /// <param name="command">The command name RCMD (ASCII).</param>
    /// <param name="parameters">The parameters (CPNAME, CPVAL).</param>
    /// <returns>The result (HCACK and the CPACK list).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/>, or a parameter name or value, is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> or a name contains characters outside ASCII (Latin-1).</exception>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S2F0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body structure differs from what was expected.</exception>
    public GemCommandResult RemoteCommand(string command, params (string Name, SecsItem Value)[] parameters) => CommandReply(Ask(CommandRequest(command, parameters)));

    /// <summary>Sends a remote command (S2F41). The rules and exceptions are the same as for <see cref="RemoteCommand"/>.</summary>
    /// <param name="command">The command name RCMD.</param>
    /// <param name="parameters">The parameters (CPNAME, CPVAL); null means none.</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>The result.</returns>
    public async Task<GemCommandResult> RemoteCommandAsync(string command, IReadOnlyList<(string Name, SecsItem Value)>? parameters = null, CancellationToken cancellationToken = default)
    {
        var request = CommandRequest(command, parameters);
        return CommandReply(await AskAsync(request, cancellationToken).ConfigureAwait(false));
    }

    static SecsMessage CommandRequest(string command, IReadOnlyList<(string Name, SecsItem Value)>? parameters)
    {
        GemDefinitionCheck.Ascii(command, nameof(command));
        var ps = new List<SecsItem>();
        foreach (var (name, value) in parameters ?? [])
        {
            GemDefinitionCheck.Ascii(name, nameof(parameters));
            ArgumentNullException.ThrowIfNull(value, nameof(parameters));
            ps.Add(SecsItem.L(SecsItem.A(name), value));
        }
        return new SecsMessage(2, 41, true, SecsItem.L(SecsItem.A(command), SecsItem.L([.. ps])));
    }

    static GemCommandResult CommandReply(SecsMessage reply)
    {
        const string expected = "<L HCACK <L <L CPNAME CPACK>…>>";
        if (reply.Item is not { Format: SecsFormat.List, Count: 2 } body || body.Items[1].Format != SecsFormat.List) throw Unexpected(reply, expected);
        byte hcack = AckOf(body.Items[0], reply, expected);
        var acks = body.Items[1].Items.Select(p => p is { Format: SecsFormat.List, Count: 2 }
            ? (Name: ParameterName(p.Items[0], reply, expected), Cpack: AckOf(p.Items[1], reply, expected))
            : throw Unexpected(reply, expected)).ToList();
        return new GemCommandResult(hcack, acks);
    }

    // CPNAME: A(관대하게 정수 하나도 10진 문자열)
    static string ParameterName(SecsItem item, SecsMessage reply, string expected)
        => item.Format == SecsFormat.ASCII ? item.GetString() : IdOf(item, reply, expected).ToString();
    #endregion
}
