using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

/// <summary>The name information of a single variable (SV or DV) (an S1F12 or S1F22 entry).</summary>
/// <param name="id">The VID (read in the format the equipment sent).</param>
/// <param name="name">The name ("" if the equipment does not know the VID).</param>
/// <param name="units">The units ("" if the equipment does not know the VID).</param>
public sealed class GemVariableInfo(GemId id, string name, string units)
{
    /// <summary>Gets the VID.</summary>
    public GemId Id { get; } = id;
    /// <summary>Gets the name ("" if the equipment does not know the VID).</summary>
    public string Name { get; } = name;
    /// <summary>Gets the units ("" if the equipment does not know the VID).</summary>
    public string Units { get; } = units;
}

/// <summary>The information of a single collection event (CE) (an S1F24 entry).</summary>
/// <param name="ceid">CEID</param>
/// <param name="name">The name ("" if the equipment does not know the CEID).</param>
/// <param name="vids">The VIDs related to this event (informational; the values carried in a report are decided by the Host).</param>
public sealed class GemEventInfo(GemId ceid, string name, IReadOnlyList<GemId> vids)
{
    /// <summary>Gets the CEID.</summary>
    public GemId Ceid { get; } = ceid;
    /// <summary>Gets the name ("" if the equipment does not know the CEID).</summary>
    public string Name { get; } = name;
    /// <summary>Gets the list of related VIDs.</summary>
    public IReadOnlyList<GemId> Vids { get; } = vids;
}

/// <summary>The information of a single equipment constant (EC) (an S2F30 entry).</summary>
/// <param name="id">ECID</param>
/// <param name="name">The name ("" if the equipment does not know the ECID).</param>
/// <param name="min">The minimum value (a zero-length item if none).</param>
/// <param name="max">The maximum value (a zero-length item if none).</param>
/// <param name="defaultValue">The default value.</param>
/// <param name="units">The units.</param>
public sealed class GemConstantInfo(GemId id, string name, SecsItem min, SecsItem max, SecsItem defaultValue, string units)
{
    /// <summary>Gets the ECID.</summary>
    public GemId Id { get; } = id;
    /// <summary>Gets the name ("" if the equipment does not know the ECID).</summary>
    public string Name { get; } = name;
    /// <summary>Gets the minimum value (a zero-length item if none).</summary>
    public SecsItem Min { get; } = min;
    /// <summary>Gets the maximum value (a zero-length item if none).</summary>
    public SecsItem Max { get; } = max;
    /// <summary>Gets the default value.</summary>
    public SecsItem Default { get; } = defaultValue;
    /// <summary>Gets the units.</summary>
    public string Units { get; } = units;
}

// 명령 메서드 S1(통신 수립·온라인·상태·이름)과 S2(EC·시계). 모두 동기 + Async 짝이고 요청 만들기·응답 해석을 함께 쓴다(동기 = 호출 스레드에서 Send, Async = SendAsync)
public partial class GemHost
{
    #region S1F13 / S1F1 / S1F15 / S1F17
    /// <summary>Requests that communications be established (S1F13 &lt;L&gt;). If COMMACK is 0, <see cref="IsCommunicating"/> becomes true and the equipment name is remembered.</summary>
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
        return EstablishReply(Ask(EstablishRequest()), epoch);
    }

    /// <summary>Requests that communications be established (S1F13). The rules and exceptions are the same as for <see cref="EstablishCommunication"/>.</summary>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>COMMACK.</returns>
    public async Task<byte> EstablishCommunicationAsync(CancellationToken cancellationToken = default)
    {
        int epoch = SendEpoch();
        return EstablishReply(await AskAsync(EstablishRequest(), cancellationToken).ConfigureAwait(false), epoch);
    }

    static SecsMessage EstablishRequest() => new(1, 13, true, SecsItem.L());

    // sendEpoch = 보내기 직전에 캡처한 SendEpoch(). 응답 처리 때 그 연결이 아직 열려 있을 때만 장비 이름·통신 수립을 반영한다(H1).
    // GEM이 그 연결의 DeviceOpened를 아직 처리하지 않았으면(sendEpoch == linkEpoch + 1) 보류했다가 DeviceOpened가 반영한다.
    // 끊긴 연결의 늦은 응답이면 COMMACK만 돌려준다
    byte EstablishReply(SecsMessage reply, int sendEpoch)
    {
        const string expected = "<L COMMACK <L MDLN SOFTREV>>";
        if (reply.Item is not { Format: SecsFormat.List, Count: 2 } body) throw Unexpected(reply, expected);
        byte ack = AckOf(body.Items[0], reply, expected);
        if (ack == GemAck.CommackAccepted)
        {
            lock (gate)
            {
                if (running && linkUp && linkEpoch == sendEpoch)
                {
                    StoreModel(body.Items[1]);
                    SetCommunicatingLocked(true);
                }
                else if (running && !linkUp && sendEpoch == linkEpoch + 1) pendingEstablish = (sendEpoch, body.Items[1]);
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
    public (string ModelName, string SoftwareRevision) AreYouThere() => AreYouThereReply(Ask(new SecsMessage(1, 1, true)));

    /// <summary>Asks whether the equipment is responding (S1F1). The rules and exceptions are the same as for <see cref="AreYouThere"/>.</summary>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>(MDLN, SOFTREV).</returns>
    public async Task<(string ModelName, string SoftwareRevision)> AreYouThereAsync(CancellationToken cancellationToken = default)
        => AreYouThereReply(await AskAsync(new SecsMessage(1, 1, true), cancellationToken).ConfigureAwait(false));

    (string ModelName, string SoftwareRevision) AreYouThereReply(SecsMessage reply)
    {
        switch (reply.Item)
        {
            case { Format: SecsFormat.List, Count: 0 }:
                return ("", "");
            case { Format: SecsFormat.List, Count: 2 } body when body.Items[0].Format == SecsFormat.ASCII && body.Items[1].Format == SecsFormat.ASCII:
                StoreModel(body);
                return (body.Items[0].GetString(), body.Items[1].GetString());
            default:
                throw Unexpected(reply, "<L MDLN SOFTREV> 또는 <L>");
        }
    }

    /// <summary>Requests that the equipment go ON-LINE (S1F17).</summary>
    /// <returns>ONLACK (0 = accepted, 1 = not allowed, 2 = already ON-LINE).</returns>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S1F0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body structure differs from what was expected.</exception>
    public byte RequestOnline() => OnlineReply(Ask(OnlineRequest()));

    /// <summary>Requests that the equipment go ON-LINE (S1F17). The rules and exceptions are the same as for <see cref="RequestOnline"/>.</summary>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>ONLACK.</returns>
    public async Task<byte> RequestOnlineAsync(CancellationToken cancellationToken = default)
        => OnlineReply(await AskAsync(OnlineRequest(), cancellationToken).ConfigureAwait(false));

    static SecsMessage OnlineRequest() => new(1, 17, true);
    static byte OnlineReply(SecsMessage reply) => AckOf(reply.Item, reply, "ONLACK");

    /// <summary>Requests that the equipment go OFF-LINE (S1F15).</summary>
    /// <remarks>If the equipment is already OFF-LINE, GEM rules make it reply with S1F0, so this results in a <see cref="SecsAbortException"/>.</remarks>
    /// <returns>OFLACK (0 = accepted).</returns>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S1F0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body structure differs from what was expected.</exception>
    public byte RequestOffline() => OfflineReply(Ask(new SecsMessage(1, 15, true)));

    /// <summary>Requests that the equipment go OFF-LINE (S1F15). The rules and exceptions are the same as for <see cref="RequestOffline"/>.</summary>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>OFLACK.</returns>
    public async Task<byte> RequestOfflineAsync(CancellationToken cancellationToken = default)
        => OfflineReply(await AskAsync(new SecsMessage(1, 15, true), cancellationToken).ConfigureAwait(false));

    static byte OfflineReply(SecsMessage reply) => AckOf(reply.Item, reply, "OFLACK");
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
    public IReadOnlyList<SecsItem> ReadStatus(params GemId[] svids) => Values(Ask(new SecsMessage(1, 3, true, IdList(svids, vidFormat))), svids, "<L SV…>");

    /// <summary>Reads status variable values (S1F3). The rules and exceptions are the same as for <see cref="ReadStatus"/>.</summary>
    /// <param name="svids">The SVIDs to read (null or an empty list means all SVs).</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>The list of values.</returns>
    public async Task<IReadOnlyList<SecsItem>> ReadStatusAsync(IReadOnlyList<GemId>? svids = null, CancellationToken cancellationToken = default)
    {
        var request = new SecsMessage(1, 3, true, IdList(svids, vidFormat));
        return Values(await AskAsync(request, cancellationToken).ConfigureAwait(false), svids, "<L SV…>");
    }

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
    public IReadOnlyList<GemVariableInfo> ReadStatusNames(params GemId[] svids) => NamesReply(Ask(NamesRequest(11, svids)), svids);

    /// <summary>Reads the names and units of status variables (S1F11). The rules and exceptions are the same as for <see cref="ReadStatusNames"/>.</summary>
    /// <param name="svids">The SVIDs to read (null or an empty list means all SVs).</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>The list of name information.</returns>
    public Task<IReadOnlyList<GemVariableInfo>> ReadStatusNamesAsync(IReadOnlyList<GemId>? svids = null, CancellationToken cancellationToken = default)
        => NamesAsync(11, svids, cancellationToken);

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
    public IReadOnlyList<GemVariableInfo> ReadDataVariableNames(params GemId[] vids) => NamesReply(Ask(NamesRequest(21, vids)), vids);

    /// <summary>Reads the names and units of data variables (S1F21). The rules and exceptions are the same as for <see cref="ReadDataVariableNames"/>.</summary>
    /// <param name="vids">The DVIDs to read (null or an empty list means all DVs).</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>The list of name information.</returns>
    public Task<IReadOnlyList<GemVariableInfo>> ReadDataVariableNamesAsync(IReadOnlyList<GemId>? vids = null, CancellationToken cancellationToken = default)
        => NamesAsync(21, vids, cancellationToken);

    // S1F11 / S1F21: <L <L VID NAME UNITS>…>
    async Task<IReadOnlyList<GemVariableInfo>> NamesAsync(int function, IReadOnlyList<GemId>? ids, CancellationToken cancellationToken)
    {
        var request = NamesRequest(function, ids);
        return NamesReply(await AskAsync(request, cancellationToken).ConfigureAwait(false), ids);
    }

    SecsMessage NamesRequest(int function, IReadOnlyList<GemId>? ids) => new(1, function, true, IdList(ids, vidFormat));

    static IReadOnlyList<GemVariableInfo> NamesReply(SecsMessage reply, IReadOnlyList<GemId>? ids)
    {
        const string expected = "<L <L VID NAME UNITS>…>";
        return [.. Entries(reply, ids, expected).Select(e =>
            e is { Format: SecsFormat.List, Count: 3 } && e.Items[1].Format == SecsFormat.ASCII && e.Items[2].Format == SecsFormat.ASCII
                ? new GemVariableInfo(IdOf(e.Items[0], reply, expected), e.Items[1].GetString(), e.Items[2].GetString())
                : throw Unexpected(reply, expected))];
    }

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
    public IReadOnlyList<GemEventInfo> ReadEventNames(params GemId[] ceids) => EventNamesReply(Ask(new SecsMessage(1, 23, true, IdList(ceids, ceidFormat))), ceids);

    /// <summary>Reads the names and related VIDs of collection events (S1F23). The rules and exceptions are the same as for <see cref="ReadEventNames"/>.</summary>
    /// <param name="ceids">The CEIDs to read (null or an empty list means all CEs).</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>The list of event information.</returns>
    public async Task<IReadOnlyList<GemEventInfo>> ReadEventNamesAsync(IReadOnlyList<GemId>? ceids = null, CancellationToken cancellationToken = default)
    {
        var request = new SecsMessage(1, 23, true, IdList(ceids, ceidFormat));
        return EventNamesReply(await AskAsync(request, cancellationToken).ConfigureAwait(false), ceids);
    }

    static IReadOnlyList<GemEventInfo> EventNamesReply(SecsMessage reply, IReadOnlyList<GemId>? ceids)
    {
        const string expected = "<L <L CEID CENAME <L VID…>>…>";
        return [.. Entries(reply, ceids, expected).Select(e =>
            e is { Format: SecsFormat.List, Count: 3 } && e.Items[1].Format == SecsFormat.ASCII && e.Items[2].Format == SecsFormat.List
                ? new GemEventInfo(IdOf(e.Items[0], reply, expected), e.Items[1].GetString(), [.. e.Items[2].Items.Select(v => IdOf(v, reply, expected))])
                : throw Unexpected(reply, expected))];
    }
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
    public IReadOnlyList<SecsItem> ReadConstants(params GemId[] ecids) => Values(Ask(new SecsMessage(2, 13, true, IdList(ecids, vidFormat))), ecids, "<L ECV…>");

    /// <summary>Reads equipment constant values (S2F13). The rules and exceptions are the same as for <see cref="ReadConstants"/>.</summary>
    /// <param name="ecids">The ECIDs to read (null or an empty list means all ECs).</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>The list of values.</returns>
    public async Task<IReadOnlyList<SecsItem>> ReadConstantsAsync(IReadOnlyList<GemId>? ecids = null, CancellationToken cancellationToken = default)
    {
        var request = new SecsMessage(2, 13, true, IdList(ecids, vidFormat));
        return Values(await AskAsync(request, cancellationToken).ConfigureAwait(false), ecids, "<L ECV…>");
    }

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
    public byte WriteConstants(IReadOnlyList<(GemId Ecid, SecsItem Value)> changes) => EacReply(Ask(WriteConstantsRequest(changes)));

    /// <summary>Changes equipment constant values (S2F15). The rules and exceptions are the same as for <see cref="WriteConstants"/>.</summary>
    /// <param name="changes">The list of (ECID, new value) pairs (at least one).</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>EAC.</returns>
    public async Task<byte> WriteConstantsAsync(IReadOnlyList<(GemId Ecid, SecsItem Value)> changes, CancellationToken cancellationToken = default)
    {
        var request = WriteConstantsRequest(changes);
        return EacReply(await AskAsync(request, cancellationToken).ConfigureAwait(false));
    }

    SecsMessage WriteConstantsRequest(IReadOnlyList<(GemId Ecid, SecsItem Value)> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0) throw new ArgumentException("바꿀 EC가 없습니다.", nameof(changes));
        var items = new List<SecsItem>();
        foreach (var (ecid, value) in changes)
        {
            ArgumentNullException.ThrowIfNull(value, nameof(changes));
            items.Add(SecsItem.L(ecid.ToItem(vidFormat), value));
        }
        return new SecsMessage(2, 15, true, SecsItem.L([.. items]));
    }

    static byte EacReply(SecsMessage reply) => AckOf(reply.Item, reply, "EAC");

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
    public IReadOnlyList<GemConstantInfo> ReadConstantNames(params GemId[] ecids) => ConstantNamesReply(Ask(new SecsMessage(2, 29, true, IdList(ecids, vidFormat))), ecids);

    /// <summary>Reads the names, ranges, defaults, and units of equipment constants (S2F29). The rules and exceptions are the same as for <see cref="ReadConstantNames"/>.</summary>
    /// <param name="ecids">The ECIDs to read (null or an empty list means all ECs).</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>The list of constant information.</returns>
    public async Task<IReadOnlyList<GemConstantInfo>> ReadConstantNamesAsync(IReadOnlyList<GemId>? ecids = null, CancellationToken cancellationToken = default)
    {
        var request = new SecsMessage(2, 29, true, IdList(ecids, vidFormat));
        return ConstantNamesReply(await AskAsync(request, cancellationToken).ConfigureAwait(false), ecids);
    }

    static IReadOnlyList<GemConstantInfo> ConstantNamesReply(SecsMessage reply, IReadOnlyList<GemId>? ecids)
    {
        const string expected = "<L <L ECID ECNAME ECMIN ECMAX ECDEF UNITS>…>";
        return [.. Entries(reply, ecids, expected).Select(e =>
            e is { Format: SecsFormat.List, Count: 6 } && e.Items[1].Format == SecsFormat.ASCII && e.Items[5].Format == SecsFormat.ASCII
                ? new GemConstantInfo(IdOf(e.Items[0], reply, expected), e.Items[1].GetString(), e.Items[2], e.Items[3], e.Items[4], e.Items[5].GetString())
                : throw Unexpected(reply, expected))];
    }
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
    public DateTime ReadEquipmentTime() => TimeReply(Ask(new SecsMessage(2, 17, true)));

    /// <summary>Reads the equipment time (S2F17). The rules and exceptions are the same as for <see cref="ReadEquipmentTime"/>.</summary>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>The equipment time (local).</returns>
    public async Task<DateTime> ReadEquipmentTimeAsync(CancellationToken cancellationToken = default)
        => TimeReply(await AskAsync(new SecsMessage(2, 17, true), cancellationToken).ConfigureAwait(false));

    static DateTime TimeReply(SecsMessage reply)
    {
        if (reply.Item is { Format: SecsFormat.ASCII } item && GemTime.TryParse(item.GetString(), out var time)) return time;
        throw Unexpected(reply, "TIME (A, 형식 0·1·2)");
    }

    /// <summary>Sets the equipment time (S2F31). The time is sent in the <see cref="TimeFormat"/> format.</summary>
    /// <param name="time">The time to send (if null, the current Host time; a UTC value is converted to local time).</param>
    /// <returns>TIACK (0 = accepted, 1 = error).</returns>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S2F0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body structure differs from what was expected.</exception>
    public byte SetEquipmentTime(DateTime? time = null) => TiackReply(Ask(SetTimeRequest(time)));

    /// <summary>Sets the equipment time (S2F31). The rules and exceptions are the same as for <see cref="SetEquipmentTime"/>.</summary>
    /// <param name="time">The time to send (if null, the current Host time).</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>TIACK.</returns>
    public async Task<byte> SetEquipmentTimeAsync(DateTime? time = null, CancellationToken cancellationToken = default)
        => TiackReply(await AskAsync(SetTimeRequest(time), cancellationToken).ConfigureAwait(false));

    SecsMessage SetTimeRequest(DateTime? time)
    {
        var t = time ?? DateTime.Now;
        if (t.Kind == DateTimeKind.Utc) t = t.ToLocalTime();
        return new SecsMessage(2, 31, true, SecsItem.A(GemTime.Format(t, timeFormat)));
    }

    static byte TiackReply(SecsMessage reply) => AckOf(reply.Item, reply, "TIACK");
    #endregion

    #region 응답 목록 읽기
    // 응답 <L 항목…>: 요청한 ID가 있으면 항목 수가 같아야 한다(순서로 짝지으므로)
    static IReadOnlyList<SecsItem> Entries(SecsMessage reply, IReadOnlyList<GemId>? requested, string expected)
    {
        if (reply.Item is not { Format: SecsFormat.List } list) throw Unexpected(reply, expected);
        if (requested is { Count: > 0 } && list.Count != requested.Count)
            throw new FormatException($"S{reply.Stream}F{reply.Function} 응답 항목이 {list.Count}개로 요청({requested.Count}개)과 다릅니다: {GemItems.Sml(reply)}");
        return list.Items;
    }

    static IReadOnlyList<SecsItem> Values(SecsMessage reply, IReadOnlyList<GemId>? requested, string expected) => [.. Entries(reply, requested, expected)];
    #endregion
}
