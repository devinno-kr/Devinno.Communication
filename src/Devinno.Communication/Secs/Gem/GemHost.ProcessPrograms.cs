using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

/// <summary>Arguments for when the equipment sent a process program load inquire (S7F1) or a process program (S7F3) to the Host. The reply uses <see cref="Ack"/> when the handler returns.</summary>
/// <remarks>Store the process program yourself (for example in your MES database); GemHost keeps no store. If the handler does not return within <c>CommandReplyTimeout</c>, GemHost replies with PPGNT 4 or ACKC7 1 on its behalf.</remarks>
public class GemProcessProgramReceivedEventArgs : EventArgs
{
    internal GemProcessProgramReceivedEventArgs(bool isInquire, string ppid, ulong length, GemProcessProgram? program)
    {
        IsInquire = isInquire;
        Ppid = ppid;
        Length = length;
        Program = program;
    }

    /// <summary>true for S7F1 (the equipment asks whether it may send; reply PPGNT), false for S7F3 (the process program; reply ACKC7)</summary>
    public bool IsInquire { get; }
    /// <summary>PPID</summary>
    public string Ppid { get; }
    /// <summary>The announced length in bytes (S7F1 only, otherwise 0)</summary>
    public ulong Length { get; }
    /// <summary>The process program (S7F3 only, otherwise null)</summary>
    public GemProcessProgram? Program { get; }
    /// <summary>The reply (default: 0 = grant or accept). PPGNT for S7F1, ACKC7 for S7F3; see <see cref="GemAck"/>.</summary>
    public byte Ack { get; set; }
}

/// <summary>Arguments for when the equipment requested a process program from the Host (S7F5). Set <see cref="Program"/> to send it; leave it null to deny (an empty S7F6).</summary>
/// <remarks>If the handler does not return within <c>CommandReplyTimeout</c>, GemHost denies on its behalf.</remarks>
public class GemProcessProgramRequestedEventArgs : EventArgs
{
    internal GemProcessProgramRequestedEventArgs(string ppid) => Ppid = ppid;

    /// <summary>The requested PPID</summary>
    public string Ppid { get; }
    /// <summary>The process program to send (default: null = deny)</summary>
    public GemProcessProgram? Program { get; set; }
}

// 레시피(S7): Host 명령(다운로드·업로드·삭제·목록·선택)과 장비가 보낸 S7F1·S7F3·S7F5 받기
public partial class GemHost
{
    const int S7F1 = 7 * 256 + 1, S7F3 = 7 * 256 + 3, S7F5 = 7 * 256 + 5;

    /// <summary>Raised on the app event thread when the equipment sent S7F1 (inquire) or S7F3 (a process program). When the handler returns, the reply uses <see cref="GemProcessProgramReceivedEventArgs.Ack"/>.</summary>
    /// <remarks>Without a subscriber, these messages go to <see cref="MessageReceived"/> as before. A handler exception refuses (PPGNT 4, ACKC7 1) and is reported through <see cref="ProtocolError"/>.</remarks>
    public event EventHandler<GemProcessProgramReceivedEventArgs>? ProcessProgramReceived;

    /// <summary>Raised on the app event thread when the equipment requested a process program (S7F5). When the handler returns, <see cref="GemProcessProgramRequestedEventArgs.Program"/> is sent (null = deny).</summary>
    /// <remarks>Without a subscriber, S7F5 goes to <see cref="MessageReceived"/> as before. A handler exception denies and is reported through <see cref="ProtocolError"/>.</remarks>
    public event EventHandler<GemProcessProgramRequestedEventArgs>? ProcessProgramRequested;

    partial void AddProcessProgramHandlers()
    {
        handlers[S7F1] = OnProgramReceived;
        handlers[S7F3] = OnProgramReceived;
        handlers[S7F5] = OnProgramRequested;
    }

    #region 명령
    /// <summary>Sends a process program to the equipment (host-initiated download): S7F1 first when <paramref name="inquire"/> is true, then S7F3 if the equipment grants it.</summary>
    /// <param name="program">The process program.</param>
    /// <param name="inquire">Send S7F1 before S7F3 (default true, recommended by E30).</param>
    /// <returns>PPGNT (null if no S7F1 was sent) and ACKC7 (null if the inquire was refused).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Not started or not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment answered SxF0 (usually OFF-LINE).</exception>
    /// <exception cref="SecsErrorException">The equipment answered S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The connection was lost.</exception>
    /// <exception cref="FormatException">The reply body is not as expected.</exception>
    public GemDownloadResult DownloadProcessProgram(GemProcessProgram program, bool inquire = true)
    {
        ArgumentNullException.ThrowIfNull(program);
        byte? ppgnt = null;
        if (inquire)
        {
            ppgnt = GemMessages.ParseS7F2(Ask(GemMessages.S7F1(program.Ppid, (uint)GemProcessProgram.DataLength(program.Body))));
            if (ppgnt != GemAck.PpgntOk) return new GemDownloadResult(ppgnt, null);
        }
        return new GemDownloadResult(ppgnt, GemMessages.ParseS7F4(Ask(GemMessages.S7F3(program))));
    }

    /// <summary>Sends a process program to the equipment. The rules and exceptions are the same as for <see cref="DownloadProcessProgram"/>.</summary>
    /// <param name="program">The process program.</param>
    /// <param name="inquire">Send S7F1 before S7F3 (default true).</param>
    /// <param name="cancellationToken">Stops waiting for the reply.</param>
    /// <returns>PPGNT and ACKC7.</returns>
    public async Task<GemDownloadResult> DownloadProcessProgramAsync(GemProcessProgram program, bool inquire = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        byte? ppgnt = null;
        if (inquire)
        {
            ppgnt = GemMessages.ParseS7F2(await AskAsync(GemMessages.S7F1(program.Ppid, (uint)GemProcessProgram.DataLength(program.Body)), cancellationToken).ConfigureAwait(false));
            if (ppgnt != GemAck.PpgntOk) return new GemDownloadResult(ppgnt, null);
        }
        return new GemDownloadResult(ppgnt, GemMessages.ParseS7F4(await AskAsync(GemMessages.S7F3(program), cancellationToken).ConfigureAwait(false)));
    }

    /// <summary>Requests a process program from the equipment (S7F5 → S7F6). The exceptions are the same as for <see cref="DownloadProcessProgram"/>.</summary>
    /// <param name="ppid">PPID</param>
    /// <returns>The process program, or null if the equipment denied the request or does not have it.</returns>
    /// <exception cref="ArgumentException"><paramref name="ppid"/> breaks the PPID rules of <see cref="GemProcessProgram"/>.</exception>
    /// <exception cref="FormatException">The reply body is not as expected, or the PPID or body received breaks the rules of <see cref="GemProcessProgram"/>.</exception>
    public GemProcessProgram? UploadProcessProgram(string ppid) => ProgramOf(Ask(GemMessages.S7F5(ppid)));

    /// <summary>Requests a process program from the equipment. The rules and exceptions are the same as for <see cref="UploadProcessProgram"/>.</summary>
    /// <param name="ppid">PPID</param>
    /// <param name="cancellationToken">Stops waiting for the reply.</param>
    /// <returns>The process program, or null if denied.</returns>
    public async Task<GemProcessProgram?> UploadProcessProgramAsync(string ppid, CancellationToken cancellationToken = default)
    {
        var request = GemMessages.S7F5(ppid);
        return ProgramOf(await AskAsync(request, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Deletes process programs on the equipment (S7F17). An empty list deletes all. The exceptions are the same as for <see cref="DownloadProcessProgram"/>.</summary>
    /// <param name="ppids">The PPIDs (empty = all).</param>
    /// <returns>ACKC7 (0 = deleted; the equipment deletes none if one fails).</returns>
    public byte DeleteProcessPrograms(IReadOnlyList<string> ppids) => GemMessages.ParseS7F18(Ask(GemMessages.S7F17(ppids)));

    /// <summary>Deletes process programs on the equipment. The rules are the same as for <see cref="DeleteProcessPrograms"/>.</summary>
    /// <param name="ppids">The PPIDs (empty = all).</param>
    /// <param name="cancellationToken">Stops waiting for the reply.</param>
    /// <returns>ACKC7.</returns>
    public async Task<byte> DeleteProcessProgramsAsync(IReadOnlyList<string> ppids, CancellationToken cancellationToken = default)
    {
        var request = GemMessages.S7F17(ppids);
        return GemMessages.ParseS7F18(await AskAsync(request, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Lists the process programs on the equipment (S7F19 → S7F20). The exceptions are the same as for <see cref="DownloadProcessProgram"/>.</summary>
    /// <returns>The PPIDs.</returns>
    public IReadOnlyList<string> ListProcessPrograms() => GemMessages.ParseS7F20(Ask(GemMessages.S7F19()));

    /// <summary>Lists the process programs on the equipment. The rules are the same as for <see cref="ListProcessPrograms"/>.</summary>
    /// <param name="cancellationToken">Stops waiting for the reply.</param>
    /// <returns>The PPIDs.</returns>
    public async Task<IReadOnlyList<string>> ListProcessProgramsAsync(CancellationToken cancellationToken = default)
        => GemMessages.ParseS7F20(await AskAsync(GemMessages.S7F19(), cancellationToken).ConfigureAwait(false));

    /// <summary>Selects a process program on the equipment with the standard remote command PP-SELECT (S2F41, parameter PPID). The rules are the same as for <see cref="RemoteCommand"/>.</summary>
    /// <param name="ppid">PPID</param>
    /// <returns>The remote command result (HCACK 0 = selected).</returns>
    /// <exception cref="ArgumentException"><paramref name="ppid"/> breaks the PPID rules of <see cref="GemProcessProgram"/>.</exception>
    public GemCommandResult SelectProcessProgram(string ppid) => RemoteCommand(GemCatalog.PpSelect, (GemCatalog.PpSelectPpid, SecsItem.A(GemProcessProgram.CheckPpid(ppid, nameof(ppid)))));

    /// <summary>Selects a process program on the equipment. The rules are the same as for <see cref="SelectProcessProgram"/>.</summary>
    /// <param name="ppid">PPID</param>
    /// <param name="cancellationToken">Stops waiting for the reply.</param>
    /// <returns>The remote command result.</returns>
    public Task<GemCommandResult> SelectProcessProgramAsync(string ppid, CancellationToken cancellationToken = default)
        => RemoteCommandAsync(GemCatalog.PpSelect, [(GemCatalog.PpSelectPpid, SecsItem.A(GemProcessProgram.CheckPpid(ppid, nameof(ppid))))], cancellationToken);

    static GemProcessProgram? ProgramOf(SecsMessage reply)
    {
        if (GemMessages.ParseS7F6(reply) is not { } p) return null;
        try { return new GemProcessProgram(p.Ppid, p.Body); }
        catch (ArgumentException ex) { throw new FormatException(GemMessages.Fail(reply, "<L <A PPID(1~120)> PPBODY>").Message, ex); }
    }
    #endregion

    #region 받기 (장비 → Host)
    // S7F1 / S7F3: 구독자가 없으면 지금처럼 MessageReceived
    void OnProgramReceived(SecsPrimaryEventArgs e)
    {
        var handler = ProcessProgramReceived;
        if (handler == null) { HandleOther(e); return; }
        var m = e.Message;
        bool inquire = m.Function == 1;
        GemProcessProgramReceivedEventArgs args;
        SecsMessage refusal;
        if (inquire)
        {
            var (ppid, length) = GemMessages.ParseS7F1(m);
            args = new(true, ppid, length, null);
            refusal = GemMessages.S7F2(GemAck.PpgntBusy);
        }
        else
        {
            var (ppid, body) = GemMessages.ParseS7F3(m);
            refusal = GemMessages.S7F4(GemAck.Ackc7PermissionNotGranted);
            if (!GemProcessProgram.IsValidPpid(ppid)) { Respond(e, refusal); return; }
            args = new(false, ppid, 0, new GemProcessProgram(ppid, body));
        }
        Serve(e, refusal, () =>
        {
            handler(this, args);
            return inquire ? GemMessages.S7F2(args.Ack) : GemMessages.S7F4(args.Ack);
        });
    }

    // S7F5: 구독자가 없으면 지금처럼 MessageReceived
    void OnProgramRequested(SecsPrimaryEventArgs e)
    {
        var handler = ProcessProgramRequested;
        if (handler == null) { HandleOther(e); return; }
        var args = new GemProcessProgramRequestedEventArgs(GemMessages.ParseS7F5(e.Message));
        Serve(e, GemMessages.S7F6(null), () =>
        {
            handler(this, args);
            return GemMessages.S7F6(args.Program);
        });
    }

    // 앱 이벤트 스레드에서 핸들러를 부르고 응답한다(미룬 응답, 기한이 지나면 refusal). 핸들러 예외 = refusal + ProtocolError. W비트가 없으면 응답 없이 알리기만
    void Serve(SecsPrimaryEventArgs e, SecsMessage refusal, Func<SecsMessage> run)
    {
        var m = e.Message;
        string what = $"S{m.Stream}F{m.Function}";
        Deferred? d = null;
        int r;
        if (m.ReplyExpected) e.Defer();
        lock (gate)
        {
            r = this.run;
            if (m.ReplyExpected)
            {
                d = new Deferred(e, Environment.TickCount64 + commandReplyTimeout, r, what) { TimeoutReply = refusal };
                deferreds.Add(d);
            }
        }
        PostApp(() =>
        {
            SecsMessage reply;
            try { reply = run(); }
            catch (Exception ex)
            {
                reply = refusal;
                ErrorIn(r, $"{what}: 핸들러 예외로 거절합니다: {ex.GetType().Name}: {ex.Message}");
            }
            if (d != null) FinishDeferred(d, reply);
        });
    }
    #endregion
}
