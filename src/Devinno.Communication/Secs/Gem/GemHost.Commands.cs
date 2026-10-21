using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

// 명령 메서드 S1(통신 수립·온라인·상태·이름)과 S2(EC·시계). 모두 동기 + Async 짝이고 요청 만들기·응답 해석을 함께 쓴다(동기 = 호출 스레드에서 Send, Async = SendAsync)
public partial class GemHost
{
    #region S1F13 / S1F1 / S1F15 / S1F17
    /// <summary>Requests that communications be established (S1F13 &lt;L&gt;). If COMMACK is 0, <see cref="IsCommunicating"/> becomes true and the equipment name is remembered, unless GemHost has already handled the transport <c>DeviceClosed</c> of the connection it was sent on (see remarks).</summary>
    /// <remarks>If GemHost has already handled that connection's <c>DeviceClosed</c> by the time the reply is handled, the COMMACK is still returned but <see cref="IsCommunicating"/> does not become true.
    /// If the reply arrives before GemHost has handled the transport <c>DeviceOpened</c> of that connection, the establishment is applied when it handles it.
    /// This assumes no application handler of the transport events (<c>DeviceOpened</c>, <c>DeviceClosed</c>, <c>ProtocolError</c>) holds the transport dispatcher across the reconnection: otherwise a request sent on the new connection can be counted for the old one, and <see cref="IsCommunicating"/> stays false after that connection's <c>DeviceClosed</c> is handled.</remarks>
    /// <returns>COMMACK (0 = accepted, 1 = denied).</returns>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S1F0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body structure differs from what was expected.</exception>
    public byte EstablishCommunication()
    {
        int epoch = SendEpoch();
        return EstablishReply(Ask(GemMessages.S1F13()), epoch);
    }

    /// <summary>Requests that communications be established (S1F13). The rules and exceptions are the same as for <see cref="EstablishCommunication"/>.</summary>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>COMMACK.</returns>
    public async Task<byte> EstablishCommunicationAsync(CancellationToken cancellationToken = default)
    {
        int epoch = SendEpoch();
        return EstablishReply(await AskAsync(GemMessages.S1F13(), cancellationToken).ConfigureAwait(false), epoch);
    }

    // sendEpoch = 보내기 직전에 캡처한 SendEpoch(). 응답 처리 때 그 연결이 아직 열려 있을 때만 장비 이름·통신 수립을 반영한다(H1).
    // GEM이 그 연결의 DeviceOpened를 아직 처리하지 않았으면(sendEpoch == linkEpoch + 1) 보류했다가 DeviceOpened가 반영한다.
    // 끊긴 연결의 늦은 응답이면 COMMACK만 돌려준다
    byte EstablishReply(SecsMessage reply, int sendEpoch)
    {
        var (ack, model, revision) = GemMessages.ParseS1F14(reply);
        if (ack == GemAck.CommackAccepted)
        {
            lock (gate)
            {
                if (running && linkUp && linkEpoch == sendEpoch)
                {
                    StoreModel(model, revision);
                    SetCommunicatingLocked(true);
                }
                else if (running && !linkUp && sendEpoch == linkEpoch + 1) pendingEstablish = (sendEpoch, model, revision);
            }
        }
        return ack;
    }

    /// <summary>Asks whether the equipment is responding (S1F1). The received names are also remembered in <see cref="ModelName"/> and <see cref="SoftwareRevision"/>.</summary>
    /// <returns>(MDLN, SOFTREV). Both are "" if the equipment replies with an empty &lt;L&gt;.</returns>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S1F0 (for example, when OFF-LINE).</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body structure differs from what was expected.</exception>
    public (string ModelName, string SoftwareRevision) AreYouThere() => AreYouThereReply(Ask(GemMessages.S1F1()));

    /// <summary>Asks whether the equipment is responding (S1F1). The rules and exceptions are the same as for <see cref="AreYouThere"/>.</summary>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>(MDLN, SOFTREV).</returns>
    public async Task<(string ModelName, string SoftwareRevision)> AreYouThereAsync(CancellationToken cancellationToken = default)
        => AreYouThereReply(await AskAsync(GemMessages.S1F1(), cancellationToken).ConfigureAwait(false));

    // 받은 이름을 기억한다(<L>은 이름을 바꾸지 않음)
    (string ModelName, string SoftwareRevision) AreYouThereReply(SecsMessage reply)
    {
        var names = GemMessages.ParseS1F2(reply);
        if (reply.Item is { Count: > 0 }) StoreModel(names.ModelName, names.SoftwareRevision);
        return names;
    }

    /// <summary>Requests that the equipment go ON-LINE (S1F17).</summary>
    /// <returns>ONLACK (0 = accepted, 1 = not allowed, 2 = already ON-LINE).</returns>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S1F0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body structure differs from what was expected.</exception>
    public byte RequestOnline() => GemMessages.ParseS1F18(Ask(GemMessages.S1F17()));

    /// <summary>Requests that the equipment go ON-LINE (S1F17). The rules and exceptions are the same as for <see cref="RequestOnline"/>.</summary>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>ONLACK.</returns>
    public async Task<byte> RequestOnlineAsync(CancellationToken cancellationToken = default)
        => GemMessages.ParseS1F18(await AskAsync(GemMessages.S1F17(), cancellationToken).ConfigureAwait(false));

    /// <summary>Requests that the equipment go OFF-LINE (S1F15).</summary>
    /// <remarks>If the equipment is already OFF-LINE, GEM rules make it reply with S1F0, so this results in a <see cref="SecsAbortException"/>.</remarks>
    /// <returns>OFLACK (0 = accepted).</returns>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S1F0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body structure differs from what was expected.</exception>
    public byte RequestOffline() => GemMessages.ParseS1F16(Ask(GemMessages.S1F15()));

    /// <summary>Requests that the equipment go OFF-LINE (S1F15). The rules and exceptions are the same as for <see cref="RequestOffline"/>.</summary>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>OFLACK.</returns>
    public async Task<byte> RequestOfflineAsync(CancellationToken cancellationToken = default)
        => GemMessages.ParseS1F16(await AskAsync(GemMessages.S1F15(), cancellationToken).ConfigureAwait(false));
    #endregion

    #region S1F3 / S1F11 / S1F21 / S1F23
    /// <summary>Reads status variable values (S1F3).</summary>
    /// <remarks>VIDs are sent in <see cref="VidFormat"/>. The value of an SVID the equipment does not know is usually a zero-length item (&lt;L&gt;).</remarks>
    /// <param name="svids">The SVIDs to read (if none, all SVs of the equipment, in the order the equipment decides).</param>
    /// <returns>The list of values (in the requested order).</returns>
    /// <exception cref="ArgumentException">An SVID cannot be represented in <see cref="VidFormat"/>.</exception>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S1F0 (for example, when OFF-LINE).</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body structure differs from what was expected, or the number of values differs from the request.</exception>
    public IReadOnlyList<SecsItem> ReadStatus(params GemId[] svids) => StatusReply(Ask(GemMessages.S1F3(svids, Formats)), svids);

    /// <summary>Reads status variable values (S1F3). The rules and exceptions are the same as for <see cref="ReadStatus"/>.</summary>
    /// <param name="svids">The SVIDs to read (null or an empty list means all SVs).</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>The list of values.</returns>
    public async Task<IReadOnlyList<SecsItem>> ReadStatusAsync(IReadOnlyList<GemId>? svids = null, CancellationToken cancellationToken = default)
    {
        var request = GemMessages.S1F3(svids, Formats);
        return StatusReply(await AskAsync(request, cancellationToken).ConfigureAwait(false), svids);
    }

    static IReadOnlyList<SecsItem> StatusReply(SecsMessage reply, IReadOnlyList<GemId>? svids) => [.. Entries(reply, svids, "<L SV…>", GemMessages.ParseS1F4)];

    /// <summary>Reads the names and units of status variables (S1F11).</summary>
    /// <param name="svids">The SVIDs to read (if none, all SVs).</param>
    /// <returns>The list of name information.</returns>
    /// <exception cref="ArgumentException">An SVID cannot be represented in <see cref="VidFormat"/>.</exception>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S1F0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body structure differs from what was expected, or the number of entries differs from the request.</exception>
    public IReadOnlyList<GemVariableInfo> ReadStatusNames(params GemId[] svids) => StatusNamesReply(Ask(GemMessages.S1F11(svids, Formats)), svids);

    /// <summary>Reads the names and units of status variables (S1F11). The rules and exceptions are the same as for <see cref="ReadStatusNames"/>.</summary>
    /// <param name="svids">The SVIDs to read (null or an empty list means all SVs).</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>The list of name information.</returns>
    public async Task<IReadOnlyList<GemVariableInfo>> ReadStatusNamesAsync(IReadOnlyList<GemId>? svids = null, CancellationToken cancellationToken = default)
    {
        var request = GemMessages.S1F11(svids, Formats);
        return StatusNamesReply(await AskAsync(request, cancellationToken).ConfigureAwait(false), svids);
    }

    static IReadOnlyList<GemVariableInfo> StatusNamesReply(SecsMessage reply, IReadOnlyList<GemId>? svids)
        => Entries(reply, svids, "<L <L VID NAME UNITS>…>", GemMessages.ParseS1F12);

    /// <summary>Reads the names and units of data variables (S1F21).</summary>
    /// <param name="vids">The DVIDs to read (if none, all DVs).</param>
    /// <returns>The list of name information.</returns>
    /// <exception cref="ArgumentException">A VID cannot be represented in <see cref="VidFormat"/>.</exception>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S1F0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx (S9F5 from an equipment that does not support S1F21).</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body structure differs from what was expected, or the number of entries differs from the request.</exception>
    public IReadOnlyList<GemVariableInfo> ReadDataVariableNames(params GemId[] vids) => DataVariableNamesReply(Ask(GemMessages.S1F21(vids, Formats)), vids);

    /// <summary>Reads the names and units of data variables (S1F21). The rules and exceptions are the same as for <see cref="ReadDataVariableNames"/>.</summary>
    /// <param name="vids">The DVIDs to read (null or an empty list means all DVs).</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>The list of name information.</returns>
    public async Task<IReadOnlyList<GemVariableInfo>> ReadDataVariableNamesAsync(IReadOnlyList<GemId>? vids = null, CancellationToken cancellationToken = default)
    {
        var request = GemMessages.S1F21(vids, Formats);
        return DataVariableNamesReply(await AskAsync(request, cancellationToken).ConfigureAwait(false), vids);
    }

    static IReadOnlyList<GemVariableInfo> DataVariableNamesReply(SecsMessage reply, IReadOnlyList<GemId>? vids)
        => Entries(reply, vids, "<L <L VID NAME UNITS>…>", GemMessages.ParseS1F22);

    /// <summary>Reads the names and related VIDs of collection events (S1F23).</summary>
    /// <param name="ceids">The CEIDs to read (if none, all CEs). Sent in <see cref="CeidFormat"/>.</param>
    /// <returns>The list of event information.</returns>
    /// <exception cref="ArgumentException">A CEID cannot be represented in <see cref="CeidFormat"/>.</exception>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S1F0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx (S9F5 from an equipment that does not support S1F23).</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body structure differs from what was expected, or the number of entries differs from the request.</exception>
    public IReadOnlyList<GemEventInfo> ReadEventNames(params GemId[] ceids) => EventNamesReply(Ask(GemMessages.S1F23(ceids, Formats)), ceids);

    /// <summary>Reads the names and related VIDs of collection events (S1F23). The rules and exceptions are the same as for <see cref="ReadEventNames"/>.</summary>
    /// <param name="ceids">The CEIDs to read (null or an empty list means all CEs).</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>The list of event information.</returns>
    public async Task<IReadOnlyList<GemEventInfo>> ReadEventNamesAsync(IReadOnlyList<GemId>? ceids = null, CancellationToken cancellationToken = default)
    {
        var request = GemMessages.S1F23(ceids, Formats);
        return EventNamesReply(await AskAsync(request, cancellationToken).ConfigureAwait(false), ceids);
    }

    static IReadOnlyList<GemEventInfo> EventNamesReply(SecsMessage reply, IReadOnlyList<GemId>? ceids)
        => Entries(reply, ceids, "<L <L CEID CENAME <L VID…>>…>", GemMessages.ParseS1F24);
    #endregion

    #region S2F13 / S2F15 / S2F29
    /// <summary>Reads equipment constant values (S2F13).</summary>
    /// <param name="ecids">The ECIDs to read (if none, all ECs). Sent in <see cref="VidFormat"/>.</param>
    /// <returns>The list of values (in the requested order; an unknown ECID is usually a zero-length item).</returns>
    /// <exception cref="ArgumentException">An ECID cannot be represented in <see cref="VidFormat"/>.</exception>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S2F0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body structure differs from what was expected, or the number of values differs from the request.</exception>
    public IReadOnlyList<SecsItem> ReadConstants(params GemId[] ecids) => ConstantsReply(Ask(GemMessages.S2F13(ecids, Formats)), ecids);

    /// <summary>Reads equipment constant values (S2F13). The rules and exceptions are the same as for <see cref="ReadConstants"/>.</summary>
    /// <param name="ecids">The ECIDs to read (null or an empty list means all ECs).</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>The list of values.</returns>
    public async Task<IReadOnlyList<SecsItem>> ReadConstantsAsync(IReadOnlyList<GemId>? ecids = null, CancellationToken cancellationToken = default)
    {
        var request = GemMessages.S2F13(ecids, Formats);
        return ConstantsReply(await AskAsync(request, cancellationToken).ConfigureAwait(false), ecids);
    }

    static IReadOnlyList<SecsItem> ConstantsReply(SecsMessage reply, IReadOnlyList<GemId>? ecids) => [.. Entries(reply, ecids, "<L ECV…>", GemMessages.ParseS2F14)];

    /// <summary>Changes equipment constant values (S2F15 &lt;L &lt;L ECID ECV&gt;…&gt;). The equipment accepts all of them or rejects all of them.</summary>
    /// <remarks>Send each value in the format the equipment defines (the Default format from <see cref="ReadConstantNames"/>). A nonzero EAC is returned as a value, not thrown as an exception.</remarks>
    /// <param name="changes">The list of (ECID, new value) pairs (at least one).</param>
    /// <returns>EAC (0 = accepted, 1 = unknown ECID, 2 = cannot be changed now, 3 = out of range or malformed).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="changes"/> or a value is null.</exception>
    /// <exception cref="ArgumentException">The list is empty, or an ECID cannot be represented in <see cref="VidFormat"/>.</exception>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S2F0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body structure differs from what was expected.</exception>
    public byte WriteConstants(IReadOnlyList<(GemId Ecid, SecsItem Value)> changes) => GemMessages.ParseS2F16(Ask(WriteConstantsRequest(changes)));

    /// <summary>Changes equipment constant values (S2F15). The rules and exceptions are the same as for <see cref="WriteConstants"/>.</summary>
    /// <param name="changes">The list of (ECID, new value) pairs (at least one).</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>EAC.</returns>
    public async Task<byte> WriteConstantsAsync(IReadOnlyList<(GemId Ecid, SecsItem Value)> changes, CancellationToken cancellationToken = default)
    {
        var request = WriteConstantsRequest(changes);
        return GemMessages.ParseS2F16(await AskAsync(request, cancellationToken).ConfigureAwait(false));
    }

    // 3층 규칙: 바꿀 EC가 하나 이상(2층 빌더는 빈 목록도 만든다)
    SecsMessage WriteConstantsRequest(IReadOnlyList<(GemId Ecid, SecsItem Value)> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0) throw new ArgumentException("바꿀 EC가 없습니다.", nameof(changes));
        return GemMessages.S2F15(changes, Formats);
    }

    /// <summary>Reads the names, ranges, defaults, and units of equipment constants (S2F29).</summary>
    /// <param name="ecids">The ECIDs to read (if none, all ECs).</param>
    /// <returns>The list of constant information.</returns>
    /// <exception cref="ArgumentException">An ECID cannot be represented in <see cref="VidFormat"/>.</exception>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S2F0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body structure differs from what was expected, or the number of entries differs from the request.</exception>
    public IReadOnlyList<GemConstantInfo> ReadConstantNames(params GemId[] ecids) => ConstantNamesReply(Ask(GemMessages.S2F29(ecids, Formats)), ecids);

    /// <summary>Reads the names, ranges, defaults, and units of equipment constants (S2F29). The rules and exceptions are the same as for <see cref="ReadConstantNames"/>.</summary>
    /// <param name="ecids">The ECIDs to read (null or an empty list means all ECs).</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>The list of constant information.</returns>
    public async Task<IReadOnlyList<GemConstantInfo>> ReadConstantNamesAsync(IReadOnlyList<GemId>? ecids = null, CancellationToken cancellationToken = default)
    {
        var request = GemMessages.S2F29(ecids, Formats);
        return ConstantNamesReply(await AskAsync(request, cancellationToken).ConfigureAwait(false), ecids);
    }

    static IReadOnlyList<GemConstantInfo> ConstantNamesReply(SecsMessage reply, IReadOnlyList<GemId>? ecids)
        => Entries(reply, ecids, "<L <L ECID ECNAME ECMIN ECMAX ECDEF UNITS>…>", GemMessages.ParseS2F30);
    #endregion

    #region S2F17 / S2F31
    /// <summary>Reads the equipment time (S2F17).</summary>
    /// <remarks>The TIME sent by the equipment is accepted in format 0 (12 characters), 1 (16 characters), or 2 (ISO 8601), and is returned as local time.</remarks>
    /// <returns>The equipment time (local).</returns>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S2F0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply is not an A item or cannot be interpreted as a time (only years 1970 to 2999 are accepted).</exception>
    public DateTime ReadEquipmentTime() => GemMessages.ParseS2F18(Ask(GemMessages.S2F17()));

    /// <summary>Reads the equipment time (S2F17). The rules and exceptions are the same as for <see cref="ReadEquipmentTime"/>.</summary>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>The equipment time (local).</returns>
    public async Task<DateTime> ReadEquipmentTimeAsync(CancellationToken cancellationToken = default)
        => GemMessages.ParseS2F18(await AskAsync(GemMessages.S2F17(), cancellationToken).ConfigureAwait(false));

    /// <summary>Sets the equipment time (S2F31). The time is sent in the <see cref="TimeFormat"/> format.</summary>
    /// <param name="time">The time to send (if null, the current Host time; a UTC value is converted to local time).</param>
    /// <returns>TIACK (0 = accepted, 1 = error).</returns>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S2F0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body structure differs from what was expected.</exception>
    public byte SetEquipmentTime(DateTime? time = null) => GemMessages.ParseS2F32(Ask(GemMessages.S2F31(time ?? DateTime.Now, timeFormat)));

    /// <summary>Sets the equipment time (S2F31). The rules and exceptions are the same as for <see cref="SetEquipmentTime"/>.</summary>
    /// <param name="time">The time to send (if null, the current Host time).</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>TIACK.</returns>
    public async Task<byte> SetEquipmentTimeAsync(DateTime? time = null, CancellationToken cancellationToken = default)
        => GemMessages.ParseS2F32(await AskAsync(GemMessages.S2F31(time ?? DateTime.Now, timeFormat), cancellationToken).ConfigureAwait(false));
    #endregion

    #region S2F43 / S6F23
    /// <summary>Sets which streams and functions the equipment spools (S2F43, Reset Spooling Streams and Functions).</summary>
    /// <param name="streams">The streams to spool and, for each, the functions (an empty list means all functions of the stream). An empty list of streams means no stream is spooled.</param>
    /// <returns>RSPACK (0 = accepted, 1 = some streams or functions are not allowed) and, for each rejected entry, its stream, STRACK, and functions.</returns>
    /// <exception cref="ArgumentException">A stream or function number cannot be represented in the message.</exception>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S2F0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx (for example, spooling is not enabled on the equipment).</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body structure differs from what was expected.</exception>
    public (byte Rspack, IReadOnlyList<(byte Stream, byte Strack, IReadOnlyList<byte> Functions)> Errors) SetSpoolStreams(IReadOnlyList<(byte Stream, IReadOnlyList<byte> Functions)> streams)
        => GemMessages.ParseS2F44(Ask(GemMessages.S2F43(streams)));

    /// <summary>Sets which streams and functions the equipment spools (S2F43). The rules and exceptions are the same as for <see cref="SetSpoolStreams"/>.</summary>
    /// <param name="streams">The streams to spool and, for each, the functions (an empty list means all functions of the stream).</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>RSPACK and the rejected entries.</returns>
    public async Task<(byte Rspack, IReadOnlyList<(byte Stream, byte Strack, IReadOnlyList<byte> Functions)> Errors)> SetSpoolStreamsAsync(IReadOnlyList<(byte Stream, IReadOnlyList<byte> Functions)> streams, CancellationToken cancellationToken = default)
        => GemMessages.ParseS2F44(await AskAsync(GemMessages.S2F43(streams), cancellationToken).ConfigureAwait(false));

    /// <summary>Asks the equipment to transmit its spooled messages (S6F23 with RSDC 0).</summary>
    /// <returns>RSDA (0 = accepted, the equipment starts transmitting; 1 = busy; 2 = there is no data to transmit).</returns>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S6F0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx (for example, spooling is not enabled on the equipment).</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body structure differs from what was expected.</exception>
    public byte TransmitSpool() => GemMessages.ParseS6F24(Ask(GemMessages.S6F23(GemAck.RsdcTransmit)));

    /// <summary>Asks the equipment to transmit its spooled messages (S6F23 with RSDC 0). The rules and exceptions are the same as for <see cref="TransmitSpool"/>.</summary>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>RSDA.</returns>
    public async Task<byte> TransmitSpoolAsync(CancellationToken cancellationToken = default)
        => GemMessages.ParseS6F24(await AskAsync(GemMessages.S6F23(GemAck.RsdcTransmit), cancellationToken).ConfigureAwait(false));

    /// <summary>Asks the equipment to discard its spooled messages (S6F23 with RSDC 1).</summary>
    /// <returns>RSDA (0 = accepted; 1 = busy; 2 = there is no data to discard).</returns>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S6F0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx (for example, spooling is not enabled on the equipment).</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body structure differs from what was expected.</exception>
    public byte PurgeSpool() => GemMessages.ParseS6F24(Ask(GemMessages.S6F23(GemAck.RsdcPurge)));

    /// <summary>Asks the equipment to discard its spooled messages (S6F23 with RSDC 1). The rules and exceptions are the same as for <see cref="PurgeSpool"/>.</summary>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>RSDA.</returns>
    public async Task<byte> PurgeSpoolAsync(CancellationToken cancellationToken = default)
        => GemMessages.ParseS6F24(await AskAsync(GemMessages.S6F23(GemAck.RsdcPurge), cancellationToken).ConfigureAwait(false));
    #endregion
}
