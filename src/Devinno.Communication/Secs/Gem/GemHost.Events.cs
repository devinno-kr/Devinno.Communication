using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

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
    /// <para>An S6F11 received before the equipment's reply (S2F34) to <see cref="DefineReports"/> (also used by <see cref="ConfigureReports"/>) is interpreted with the old definitions, and one received after the method returns, with the new ones; one that arrives in between may be interpreted with either.</para>
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
        var report = DecodeEventReport(e.Message, GemMessages.ParseS6F11, out var warning);
        Respond(e, GemMessages.S6F12(GemAck.Ackc6Accepted));
        if (warning != null) Error(warning);
        Raise(EventReceived, new GemEventReportEventArgs(report, e.Message));
    }

    // 이벤트 리포트 해석(2층 ParseS6F11·ParseS6F16, 구조 오류는 2층 FormatException). 3층 규칙: 해석 표는 받은 순간의 것(gate 안에서 해석, 열쇠는 RptidFormat 기준 GemItems.Key),
    // 표에 있는데 값 개수가 정의와 다른 리포트(2층이 VID 없이 줌)는 모두 모아 알림 문장 하나(없으면 null) — 장비가 그런 리포트를 많이 보내도 앱 이벤트 대기열을 채우지 않게
    GemEventReport DecodeEventReport(SecsMessage m, Func<SecsMessage, IReadOnlyDictionary<GemId, IReadOnlyList<GemId>>?, GemFormats?, GemEventReport> parse, out string? warning)
    {
        warning = null;
        GemEventReport report;
        int mismatches = 0;
        List<string>? listed = null;
        lock (gate)
        {
            report = parse(m, reportTable, Formats);
            foreach (var r in report.Reports)
            {
                var vids = reportTable.GetValueOrDefault(GemItems.Key(r.Rptid, rptidFormat));
                if (vids == null || vids.Count == r.Values.Count) continue;
                if (mismatches++ < MismatchListed) (listed ??= []).Add($"{GemItems.IdText(r.Rptid)}(값 {r.Values.Count}개/정의 {vids.Count}개)");
            }
        }
        if (mismatches > 0)
            warning = $"CEID {GemItems.IdText(report.Ceid)}: 리포트 {mismatches}개의 값 개수가 정의와 달라 VID 없이 줍니다: {string.Join(", ", listed!)}{(mismatches > MismatchListed ? ", …" : "")}";
        return report;
    }

    // S5F1 <L ALCD ALID ALTX> → ACKC5 0 → AlarmReceived
    void OnS5F1(SecsPrimaryEventArgs e)
    {
        var alarm = GemMessages.ParseS5F1(e.Message);
        Respond(e, GemMessages.S5F2(GemAck.Ackc5Accepted));
        Raise(AlarmReceived, new GemAlarmEventArgs(alarm.Alid, alarm.Alcd, alarm.Text));
    }

    // S10F1 <L TID TEXT> → ACKC10 (구독자 없으면 2) → TerminalReceived
    void OnS10F1(SecsPrimaryEventArgs e)
    {
        var (tid, text) = GemMessages.ParseS10F1(e.Message);
        var handler = TerminalReceived;
        Respond(e, GemMessages.S10F2(handler == null ? GemAck.Ackc10TerminalNotAvailable : GemAck.Ackc10Accepted));
        Raise(handler, new GemTerminalEventArgs(tid, [text]));
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
    public GemEventReport RequestEventReport(GemId ceid) => EventReportReply(Ask(GemMessages.S6F15(ceid, Formats)));

    /// <summary>Requests an event report with the current values of a CE (S6F15). The rules and exceptions are the same as for <see cref="RequestEventReport"/>.</summary>
    /// <param name="ceid">CEID</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>The interpreted report.</returns>
    public async Task<GemEventReport> RequestEventReportAsync(GemId ceid, CancellationToken cancellationToken = default)
    {
        var request = GemMessages.S6F15(ceid, Formats);
        return EventReportReply(await AskAsync(request, cancellationToken).ConfigureAwait(false));
    }

    GemEventReport EventReportReply(SecsMessage reply)
    {
        var report = DecodeEventReport(reply, GemMessages.ParseS6F16, out var warning);
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
    public IReadOnlyList<SecsItem> RequestReport(GemId rptid) => ReportValuesReply(Ask(GemMessages.S6F19(rptid, Formats)));

    /// <summary>Requests the current values of a single report (S6F19). The rules and exceptions are the same as for <see cref="RequestReport"/>.</summary>
    /// <param name="rptid">RPTID</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>The list of values.</returns>
    public async Task<IReadOnlyList<SecsItem>> RequestReportAsync(GemId rptid, CancellationToken cancellationToken = default)
    {
        var request = GemMessages.S6F19(rptid, Formats);
        return ReportValuesReply(await AskAsync(request, cancellationToken).ConfigureAwait(false));
    }

    static IReadOnlyList<SecsItem> ReportValuesReply(SecsMessage reply) => [.. Entries(reply, null, "<L V…>", GemMessages.ParseS6F20)];
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
    public byte EnableAlarm(GemId? alid, bool enable) => GemMessages.ParseS5F4(Ask(GemMessages.S5F3(enable, alid, Formats)));

    /// <summary>Enables or disables alarm reporting (S5F3). The rules and exceptions are the same as for <see cref="EnableAlarm"/>.</summary>
    /// <param name="alid">The ALID (if null, all alarms).</param>
    /// <param name="enable">true = enable, false = disable.</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>ACKC5.</returns>
    public async Task<byte> EnableAlarmAsync(GemId? alid, bool enable, CancellationToken cancellationToken = default)
    {
        var request = GemMessages.S5F3(enable, alid, Formats);
        return GemMessages.ParseS5F4(await AskAsync(request, cancellationToken).ConfigureAwait(false));
    }

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
    public IReadOnlyList<GemAlarmInfo> ListAlarms(params GemId[] alids) => AlarmListReply(Ask(GemMessages.S5F5(alids, Formats)), alids);

    /// <summary>Reads the alarm list (S5F5). The rules and exceptions are the same as for <see cref="ListAlarms"/>.</summary>
    /// <param name="alids">The ALIDs to read (null or an empty list means all alarms).</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>The list of alarm information.</returns>
    public async Task<IReadOnlyList<GemAlarmInfo>> ListAlarmsAsync(IReadOnlyList<GemId>? alids = null, CancellationToken cancellationToken = default)
    {
        var request = GemMessages.S5F5(alids, Formats);
        return AlarmListReply(await AskAsync(request, cancellationToken).ConfigureAwait(false), alids);
    }

    static IReadOnlyList<GemAlarmInfo> AlarmListReply(SecsMessage reply, IReadOnlyList<GemId>? alids) => Entries(reply, alids, "<L <L ALCD ALID ALTX>…>", GemMessages.ParseS5F6);

    /// <summary>Reads the list of alarms whose reporting is enabled (S5F7).</summary>
    /// <returns>The list of alarm information.</returns>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S5F0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body structure differs from what was expected.</exception>
    public IReadOnlyList<GemAlarmInfo> ListEnabledAlarms() => EnabledAlarmsReply(Ask(GemMessages.S5F7()));

    /// <summary>Reads the list of alarms whose reporting is enabled (S5F7). The rules and exceptions are the same as for <see cref="ListEnabledAlarms"/>.</summary>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>The list of alarm information.</returns>
    public async Task<IReadOnlyList<GemAlarmInfo>> ListEnabledAlarmsAsync(CancellationToken cancellationToken = default)
    {
        return EnabledAlarmsReply(await AskAsync(GemMessages.S5F7(), cancellationToken).ConfigureAwait(false));
    }

    // 장비가 모르는 ALID는 ALCD가 길이 0(→ 0, 2층 규칙)
    static IReadOnlyList<GemAlarmInfo> EnabledAlarmsReply(SecsMessage reply) => Entries(reply, null, "<L <L ALCD ALID ALTX>…>", GemMessages.ParseS5F8);
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
    public byte SendTerminal(byte tid, string text) => GemMessages.ParseS10F4(Ask(GemMessages.S10F3(tid, text)));

    /// <summary>Sends text to the equipment terminal (S10F3). The rules and exceptions are the same as for <see cref="SendTerminal"/>.</summary>
    /// <param name="tid">The terminal number.</param>
    /// <param name="text">The text (ASCII).</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>ACKC10.</returns>
    public async Task<byte> SendTerminalAsync(byte tid, string text, CancellationToken cancellationToken = default)
    {
        var request = GemMessages.S10F3(tid, text);
        return GemMessages.ParseS10F4(await AskAsync(request, cancellationToken).ConfigureAwait(false));
    }
    #endregion

    #region S2F41
    /// <summary>Sends a remote command (S2F41 &lt;L RCMD &lt;L &lt;L CPNAME CPVAL&gt;…&gt;&gt;). RCMD and CPNAME are sent as A.</summary>
    /// <remarks>If the equipment is not ON-LINE REMOTE, the result is usually HCACK 2 (LOCAL) or S2F0 (OFF-LINE). For a long-running command, the equipment accepts it with HCACK 4 and reports the completion through an event. SecsTimeoutException, SecsDisconnectedException, cancellation, and even HCACK 2 do not prove the command was not executed. Check the equipment state (for example S1F3) before sending it again.</remarks>
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
    public GemCommandResult RemoteCommand(string command, params (string Name, SecsItem Value)[] parameters) => GemMessages.ParseS2F42(Ask(GemMessages.S2F41(command, parameters)));

    /// <summary>Sends a remote command (S2F41). The rules and exceptions are the same as for <see cref="RemoteCommand"/>.</summary>
    /// <remarks>SecsTimeoutException, SecsDisconnectedException, cancellation, and even HCACK 2 do not prove the command was not executed. Check the equipment state (for example S1F3) before sending it again.</remarks>
    /// <param name="command">The command name RCMD.</param>
    /// <param name="parameters">The parameters (CPNAME, CPVAL); null means none.</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>The result.</returns>
    public async Task<GemCommandResult> RemoteCommandAsync(string command, IReadOnlyList<(string Name, SecsItem Value)>? parameters = null, CancellationToken cancellationToken = default)
    {
        var request = GemMessages.S2F41(command, parameters);
        return GemMessages.ParseS2F42(await AskAsync(request, cancellationToken).ConfigureAwait(false));
    }
    #endregion
}
