using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

// 레시피 관리(언포맷 Process Program, S7, 6J.5): 켜기 설정, 표준 값 계산, 받기·앱 메서드
public partial class GemEquipment
{
    internal const int MaxProcessProgramLimit = 16_777_215;     // SecsItem.MaxLength

    IGemProcessProgramStore? ppStoreSetting;
    int maxProcessProgramSize = 4_194_304;

    // 아래는 레시피를 켠 실행(Start 때 ProcessProgramStore != null)에서만 쓴다
    bool processPrograms;                       // gate: Start 때 확정
    IGemProcessProgramStore? ppStore;           // 실행 중 저장소(Start 때 ppStoreSetting을 잡아 둠, 바뀌지 않음)
    readonly object ppLock = new();             // 저장소 호출을 하나씩. gate 안에서 잡지 않는다(순서 ppLock → gate)
    string? selectedPpid;                       // gate: 선택된 레시피(PPExecName)

    /// <summary>Where process programs (recipes) are kept (default: null = process programs off). Setting it turns process program management (S7) on at the next <see cref="Start"/>.</summary>
    /// <remarks>
    /// <para>When null, GEM behaves exactly as before: Stream 7 is not a supported stream (S9F3) and the application may handle it with <see cref="RegisterMessage"/>. When set, the standard variables and events PPExecName, PPFormat, PPChangeName, PPChangeStatus, ProcessProgramChange, and ProcessProgramSelected are defined and GEM handles Stream 7, so registering a Stream 7 message makes <see cref="Start"/> fail.</para>
    /// <para>Use <see cref="GemProcessProgramFolder"/> or your own <see cref="IGemProcessProgramStore"/>. A store set in code is not written by <see cref="ToXml"/>; the XML attribute <c>ProcessProgramDirectory</c> creates a <see cref="GemProcessProgramFolder"/>.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public IGemProcessProgramStore? ProcessProgramStore { get => ppStoreSetting; set => Setting(() => ppStoreSetting = value); }

    /// <summary>Upper limit of a process program body (the encoded PPBODY) in bytes, 1 to 16777215 (default: 4194304). A larger S7F1 is answered with PPGNT 5 and a larger S7F3 with ACKC7 2.</summary>
    /// <remarks>Over SECS-I a whole message is limited to about 8 MB, so a larger body is refused by the transport.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">Out of range</exception>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public int MaxProcessProgramSize { get => maxProcessProgramSize; set => Setting(() => maxProcessProgramSize = Check(value, 1, MaxProcessProgramLimit, nameof(MaxProcessProgramSize))); }

    const int S7F1 = 7 * 256 + 1, S7F3 = 7 * 256 + 3, S7F5 = 7 * 256 + 5, S7F17 = 7 * 256 + 17, S7F19 = 7 * 256 + 19;

    // gate: 이 연결에서 S7F1로 허락한 길이(PPID → LENGTH). 끊기거나 같은 PPID의 S7F3을 받으면 지운다
    readonly Dictionary<string, ulong> ppGrants = new(StringComparer.Ordinal);

    /// <summary>Raised on the application event thread when the Host asked to change the process program store (S7F1 inquire, S7F3 download, S7F17 delete) and GEM's own checks passed. When the handler returns, the reply uses <see cref="GemProcessProgramChangeEventArgs.Ack"/>; on 0 the store is changed.</summary>
    /// <remarks>
    /// <para>GEM answers first (the application is not called) for: an invalid PPID, a body larger than <see cref="MaxProcessProgramSize"/> or than the length granted by S7F1, overwriting or deleting the selected process program (ACKC7 1), and deleting a PPID that does not exist (ACKC7 4, nothing is deleted). S7F5 and S7F19 are answered from the store without this event.</para>
    /// <para>No subscriber = accept. A handler exception refuses the request (ACKC7 1, PPGNT 4) and is reported through <see cref="ProtocolError"/>. If the handler and the store do not finish within <see cref="CommandReplyTimeout"/>, GEM replies with the refusal on its behalf; a store change that finishes later is still applied (reported through <see cref="ProtocolError"/>), as with remote commands.</para>
    /// <para>A change made by the Host does not raise the ProcessProgramChange event (E30 reserves it for the operator); call <see cref="TriggerEvent"/> yourself if your Host wants it.</para>
    /// </remarks>
    public event EventHandler<GemProcessProgramChangeEventArgs>? ProcessProgramChangeRequested;

    partial void AddProcessProgramHandlers()
    {
        standardValues[GemStandard.PPExecName] = () => SecsItem.A(selectedPpid ?? "");
        standardValues[GemStandard.PPFormat] = () => SecsItem.U1(1);
        // Stream 7은 레시피를 켰을 때만 지원 Stream이다(꺼져 있으면 OnPrimary가 S9F3)
        handlers[S7F1] = OnS7F1;
        handlers[S7F3] = OnS7F3;
        handlers[S7F5] = OnS7F5;
        handlers[S7F17] = OnS7F17;
        handlers[S7F19] = e => ServeProcessProgram(e, GemMessages.S7F20([]), () => GemMessages.S7F20(ppStore!.GetIds()));
    }

    partial void ResetProcessPrograms()
    {
        selectedPpid = null;
        ppGrants.Clear();
    }

    // 테스트용
    internal void SetSelectedForTest(string? ppid) { lock (gate) selectedPpid = ppid; }
    internal bool IsGateHeldForTest => Monitor.IsEntered(gate);

    #region 받기 (디스패처: 해석·미룸만, 저장소·앱 코드는 앱 이벤트 스레드에서 ppLock 안에)
    // S7F1 <L PPID LENGTH> → PPGNT
    void OnS7F1(SecsPrimaryEventArgs e)
    {
        var (ppid, length) = GemMessages.ParseS7F1(e.Message);
        ServeProcessProgram(e, GemMessages.S7F2(GemAck.PpgntBusy), () =>
        {
            if (!GemProcessProgram.IsValidPpid(ppid)) return GemMessages.S7F2(GemAck.PpgntInvalidPpid);
            if (length > (ulong)maxProcessProgramSize) return GemMessages.S7F2(GemAck.PpgntWillNotAccept);
            if (!AskChange(new GemProcessProgramChangeEventArgs(GemProcessProgramChangeKind.Inquire, [ppid], null, length), GemAck.PpgntBusy, out var ack)) return GemMessages.S7F2(ack);
            if (ack == GemAck.PpgntOk) lock (gate) ppGrants[ppid] = length;
            return GemMessages.S7F2(ack);
        });
    }

    // S7F3 <L PPID PPBODY> → ACKC7. S7F1 없이 온 것도 받는다
    void OnS7F3(SecsPrimaryEventArgs e)
    {
        var (ppid, body) = GemMessages.ParseS7F3(e.Message);
        ServeProcessProgram(e, GemMessages.S7F4(GemAck.Ackc7PermissionNotGranted), () =>
        {
            ulong? granted;
            string? selected;
            lock (gate)
            {
                granted = ppGrants.Remove(ppid, out var g) ? g : null;
                selected = selectedPpid;
            }
            if (!GemProcessProgram.IsValidPpid(ppid)) return GemMessages.S7F4(GemAck.Ackc7PermissionNotGranted);
            long size = SecsCodec.Encode(body).Length;
            if (size > maxProcessProgramSize || granted is { } max && (ulong)size > max) return GemMessages.S7F4(GemAck.Ackc7LengthError);
            if (ppid == selected) return GemMessages.S7F4(GemAck.Ackc7PermissionNotGranted);
            var program = new GemProcessProgram(ppid, body);
            if (!AskChange(new GemProcessProgramChangeEventArgs(GemProcessProgramChangeKind.Download, [ppid], program, 0), GemAck.Ackc7PermissionNotGranted, out var ack)) return GemMessages.S7F4(ack);
            if (ack == GemAck.Ackc7Accepted) ppStore!.Write(program);
            return GemMessages.S7F4(ack);
        });
    }

    // S7F5 PPID → S7F6 <L PPID PPBODY>, 없거나 잘못된 PPID면 <L>
    void OnS7F5(SecsPrimaryEventArgs e)
    {
        var ppid = GemMessages.ParseS7F5(e.Message);
        ServeProcessProgram(e, GemMessages.S7F6(null), () =>
            GemMessages.S7F6(GemProcessProgram.IsValidPpid(ppid) ? ppStore!.Read(ppid) : null));
    }

    // S7F17 <L PPID…> → ACKC7. 빈 목록 = 전부. 하나라도 문제가 있으면 아무것도 지우지 않는다
    void OnS7F17(SecsPrimaryEventArgs e)
    {
        var requested = GemMessages.ParseS7F17(e.Message);
        ServeProcessProgram(e, GemMessages.S7F18(GemAck.Ackc7PermissionNotGranted), () =>
        {
            var ppids = requested.Count == 0 ? ppStore!.GetIds() : requested;
            if (ppids.Any(p => !GemProcessProgram.IsValidPpid(p) || ppStore!.Read(p) == null)) return GemMessages.S7F18(GemAck.Ackc7PpidNotFound);
            string? selected;
            lock (gate) selected = selectedPpid;
            if (selected != null && ppids.Contains(selected)) return GemMessages.S7F18(GemAck.Ackc7PermissionNotGranted);
            if (!AskChange(new GemProcessProgramChangeEventArgs(GemProcessProgramChangeKind.Delete, [.. ppids], null, 0), GemAck.Ackc7PermissionNotGranted, out var ack)) return GemMessages.S7F18(ack);
            if (ack == GemAck.Ackc7Accepted) foreach (var p in ppids) ppStore!.Delete(p);
            return GemMessages.S7F18(ack);
        });
    }

    // 공통: W비트가 없으면 처리하지 않음. 미룬 응답으로 앱 이벤트 스레드에서 ppLock 안에 work를 하고 응답한다.
    // work의 예외(저장소)는 거절 응답(= 기한 응답과 같은 것) + ProtocolError
    void ServeProcessProgram(SecsPrimaryEventArgs e, SecsMessage refusal, Func<SecsMessage> work)
    {
        var m = e.Message;
        string what = $"S7F{m.Function}";
        if (!m.ReplyExpected)
        {
            Error($"W비트가 없는 {what}은(는) 결과를 돌려줄 수 없어 처리하지 않았습니다.");
            return;
        }
        var d = DeferReply(e, refusal, what);
        PostApp(() =>
        {
            SecsMessage reply;
            lock (ppLock)
            {
                try { reply = work(); }
                catch (Exception ex)
                {
                    reply = refusal;
                    ErrorIn(d.Run, $"{what}: 레시피 저장소 예외: {ex.GetType().Name}: {ex.Message}");
                }
            }
            FinishDeferred(d, reply);
        });
    }

    // 앱 승인(앱 이벤트 스레드, ppLock 안). 구독자가 없으면 0. 핸들러 예외면 false + refusal
    bool AskChange(GemProcessProgramChangeEventArgs args, byte refusal, out byte ack)
    {
        var handler = ProcessProgramChangeRequested;
        ack = 0;
        if (handler == null) return true;
        try
        {
            handler(this, args);
            ack = args.Ack;
            return true;
        }
        catch (Exception ex)
        {
            ack = refusal;
            Error($"ProcessProgramChangeRequested 핸들러 예외로 거절합니다: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
    #endregion
}
