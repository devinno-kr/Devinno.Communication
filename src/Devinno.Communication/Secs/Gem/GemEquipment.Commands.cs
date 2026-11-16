using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

/// <summary>Arguments for when the Host sent a remote command (S2F41, or the enhanced S2F49). If the handler sets <see cref="Hcack"/>, the reply uses that value when the handler returns.</summary>
/// <remarks>
/// <para>GEM has already checked the command name, parameter names, and formats against the definition. A missing parameter is judged by the application and reported with <see cref="SetParameterAck(string, byte)"/>.</para>
/// <para>S2F49 uses the same command definitions as S2F41 and is answered with S2F50; <see cref="ObjectSpec"/> tells which object is meant (decide yourself, for example HCACK 6 when there is no such object), and a list-valued parameter may get a list CEPACK through <see cref="SetParameterAck(string, SecsItem)"/>.</para>
/// <para>Reply to a long-running job with <see cref="GemAck.HcackAcceptedLater"/> (4), do the job separately, and report its completion with <c>TriggerEvent</c>. If the handler does not return within <c>CommandReplyTimeout</c>, GEM replies with HCACK 2 on its behalf.</para>
/// </remarks>
public class GemRemoteCommandEventArgs : EventArgs
{
    readonly List<(string Name, SecsItem Ack)> acks = [];

    internal GemRemoteCommandEventArgs(string command, IReadOnlyList<(string Name, SecsItem Value)> parameters, string? objectSpec = null)
    {
        Command = command;
        Parameters = parameters;
        ObjectSpec = objectSpec;
    }

    /// <summary>Command name (RCMD, the name in the definition)</summary>
    public string Command { get; }
    /// <summary>List of received parameters (CPNAME, CPVAL), in the order received</summary>
    public IReadOnlyList<(string Name, SecsItem Value)> Parameters { get; }
    /// <summary>HCACK to reply with (default: 0 = accepted and executed). Use the Hcack... values of <see cref="GemAck"/>.</summary>
    public byte Hcack { get; set; }
    /// <summary>OBJSPEC of an S2F49 (the target object, as received; "" usually means the equipment itself), or null for S2F41</summary>
    public string? ObjectSpec { get; }
    /// <summary>true for S2F49 (enhanced remote command, answered with S2F50), false for S2F41</summary>
    public bool IsEnhanced => ObjectSpec != null;

    /// <summary>Puts an error (CPACK, or CEPACK for S2F49) for one parameter in the reply. If at least one is put and <see cref="Hcack"/> is 0, HCACK 3 is sent.</summary>
    /// <param name="name">Parameter name (a name that was not received is also allowed: reports a missing parameter)</param>
    /// <param name="cpack">CPACK, 1 to 255 (1 unknown name, 2 invalid value, 3 invalid format)</param>
    /// <exception cref="ArgumentNullException">name is null</exception>
    /// <exception cref="ArgumentOutOfRangeException">cpack is 0</exception>
    public void SetParameterAck(string name, byte cpack)
    {
        GemDefinitionCheck.Ascii(name, nameof(name));
        if (cpack == 0) throw new ArgumentOutOfRangeException(nameof(cpack), cpack, "CPACK은 1 이상입니다(문제가 없는 파라미터는 넣지 않음).");
        acks.Add((name, SecsItem.U1(cpack)));
    }

    /// <summary>Puts a CEPACK item for one parameter of an S2F49 in the reply, for example a list shaped like a list-valued CEPVAL. Acks are sent in the order they were put.</summary>
    /// <param name="name">Parameter name</param>
    /// <param name="cepack">CEPACK item (a number such as <c>SecsItem.U1(2)</c>, or a list)</param>
    /// <exception cref="ArgumentNullException">name or cepack is null</exception>
    /// <exception cref="InvalidOperationException">The command is an S2F41, whose CPACK is a single number (use <see cref="SetParameterAck(string, byte)"/>)</exception>
    public void SetParameterAck(string name, SecsItem cepack)
    {
        GemDefinitionCheck.Ascii(name, nameof(name));
        ArgumentNullException.ThrowIfNull(cepack);
        if (!IsEnhanced) throw new InvalidOperationException("S2F41의 CPACK은 숫자 하나입니다. SetParameterAck(name, byte)를 쓰세요.");
        acks.Add((name, cepack));
    }

    internal IReadOnlyList<(string Name, SecsItem Ack)> ParameterAcks => acks;
}

/// <summary>Arguments for when a message registered by the application (<c>RegisterMessage</c>) was received. Reply once with <see cref="Reply"/> inside the handler.</summary>
/// <remarks>If the handler returns without replying, SxF0 is sent (the same for an exception, which is also reported through <c>ProtocolError</c>). If the handler does not return within <c>CommandReplyTimeout</c>, GEM also replies with SxF0 on its behalf.</remarks>
public class GemMessageEventArgs : EventArgs
{
    readonly Action<SecsMessage> validate;
    readonly Func<SecsMessage, bool> send;
    readonly object gate = new();
    int state;   // 0 응답 가능, 1 응답함, 2 핸들러 끝남

    internal GemMessageEventArgs(SecsMessage message, Action<SecsMessage> validate, Func<SecsMessage, bool> send)
    {
        Message = message;
        this.validate = validate;
        this.send = send;
    }

    /// <summary>The received primary message</summary>
    public SecsMessage Message { get; }

    /// <summary>Sends a reply once from inside the handler (Function = primary + 1, or 0; no W-bit). Waits until it has been sent.</summary>
    /// <param name="reply">Reply message</param>
    /// <exception cref="ArgumentNullException">reply is null</exception>
    /// <exception cref="ArgumentException">The Stream differs, the Function is neither primary + 1 nor 0, or the W-bit is set</exception>
    /// <exception cref="InvalidOperationException">The message has no W-bit, a reply was already sent, the handler has finished, the deadline passed and GEM already replied, or GEM was stopped (Stop) in the meantime</exception>
    /// <exception cref="SecsDisconnectedException">The connection on which the message was received was lost</exception>
    public void Reply(SecsMessage reply)
    {
        validate(reply);
        lock (gate)
        {
            if (state != 0) throw new InvalidOperationException(state == 1 ? "이미 응답했습니다." : "핸들러가 끝난 뒤에는 응답할 수 없습니다.");
            state = 1;
        }
        if (!send(reply)) throw new InvalidOperationException("CommandReplyTimeout이 지나 GEM이 이미 SxF0으로 응답했습니다.");
    }

    // 핸들러가 끝남. 응답하지 않았으면 true
    internal bool Close()
    {
        lock (gate)
        {
            if (state != 0) return false;
            state = 2;
            return true;
        }
    }
}

// 리모트 명령 S2F41/F42(앱 승인, 미룬 응답)와 등록 메시지
public partial class GemEquipment
{
    const int S2F41 = 2 * 256 + 41;

    /// <summary>Raised on the application event thread when the Host's remote command (S2F41, or the enhanced S2F49) passed the checks. When the handler returns, the reply uses <see cref="GemRemoteCommandEventArgs.Hcack"/>.</summary>
    /// <remarks>
    /// <para>Cases where GEM replies first (the application is not called): a command not in the definitions HCACK 1, ON-LINE LOCAL HCACK 2, unknown parameter (CPACK 1) or format mismatch (CPACK 3) HCACK 3, no subscriber HCACK 2.</para>
    /// <para><b>S2F49</b> is checked against the same definitions and answered with S2F50 (CEPACK as U1, or the items the handler set); <see cref="GemRemoteCommandEventArgs.ObjectSpec"/> carries OBJSPEC. If the application registered S2F49 with <see cref="RegisterMessage"/>, GEM leaves it to the application. S2F21 is not handled.</para>
    /// <para><b>PP-SELECT</b> (when <see cref="ProcessProgramStore"/> is set): GEM defines it with an A parameter PPID unless you define it yourself, accepts it also in ON-LINE LOCAL, answers HCACK 3 with CPACK 2 when PPID is missing or not stored (the handler is not called), and after the handler accepts with HCACK 0 calls <see cref="SelectProcessProgram"/>. Without a PPID parameter in your own definition, only the LOCAL rule applies.</para>
    /// <para>If the handler does not return within <see cref="CommandReplyTimeout"/>, GEM replies with HCACK 2 on its behalf (<see cref="ProtocolError"/>) and discards the result that returns later. The handler keeps running, so the action may still be performed while the host believes it was not. Keep handlers short; for long actions reply HCACK 4 and report completion with an event. If the handler throws, GEM replies with HCACK 2 (cannot be executed) regardless of the Hcack and CPACK set so far, and reports it through <see cref="ProtocolError"/>.</para>
    /// </remarks>
    public event EventHandler<GemRemoteCommandEventArgs>? RemoteCommandReceived;

    /// <summary>Raised on the application event thread when a registered message (<see cref="RegisterMessage"/>) was received. Reply with <see cref="GemMessageEventArgs.Reply"/> inside the handler.</summary>
    /// <remarks>While OFF-LINE or before communication is established, GEM replies with SxF0 and no event is raised. If there is no subscriber or the handler does not reply (including deadline exceeded or an exception), the reply is SxF0. A handler exception is reported through <see cref="ProtocolError"/>.</remarks>
    public event EventHandler<GemMessageEventArgs>? MessageReceived;

    const int S2F49 = 2 * 256 + 49;

    partial void AddCommandHandlers()
    {
        handlers[S2F41] = e => OnRemoteCommand(e, false);
        handlers[S2F49] = e => OnRemoteCommand(e, true);
    }

    // S2F41 <L RCMD <L <L CPNAME CPVAL>…>> → <L HCACK <L <L CPNAME CPACK>…>>,
    // S2F49 <L DATAID OBJSPEC RCMD <L <L CPNAME CEPVAL>…>> → S2F50 <L HCACK <L <L CPNAME CEPACK>…>>. 같은 명령 표·같은 검사·같은 앱 이벤트
    void OnRemoteCommand(SecsPrimaryEventArgs e, bool enhanced)
    {
        // 구조 오류·이름이 A(또는 정수 하나)가 아님 = FormatException(S9F7). CPNAME 아이템은 CPACK에 그대로 되돌린다(D3)
        string rcmd;
        IReadOnlyList<(SecsItem NameItem, string Name, SecsItem Value)> ps;
        string? objspec = null;
        if (enhanced) (rcmd, ps, objspec) = GemMessages.ParseS2F49Raw(e.Message);
        else (rcmd, ps) = GemMessages.ParseS2F41Raw(e.Message);
        // 응답 빌더: S2F42의 CPACK은 B 숫자, S2F50의 CEPACK은 U1 숫자 또는 앱이 준 아이템
        SecsMessage Reply(byte hcack, IReadOnlyList<(SecsItem Name, SecsItem Ack)>? acks = null) => enhanced
            ? GemMessages.S2F50(hcack, acks ?? [])
            : GemMessages.S2F42(hcack, [.. (acks ?? []).Select(a => (a.Name, (byte)a.Ack.GetUInt64()))]);
        static SecsItem Code(byte b) => SecsItem.U1(b);

        if (!catalog.Commands.TryGetValue(rcmd, out var def)) { Respond(e, Reply(GemAck.HcackInvalidCommand)); return; }
        bool local, pp;
        lock (gate)
        {
            local = control == GemControlState.OnlineLocal;
            pp = processPrograms;
        }
        // PP-SELECT(레시피를 켰을 때)는 LOCAL에서도 받는다(E30: LOCAL에서 레시피 선택 허용)
        bool ppSelect = pp && def.Name == GemCatalog.PpSelect;
        if (local && !ppSelect) { Respond(e, Reply(GemAck.HcackCannotPerform)); return; }
        var bad = new List<(SecsItem Name, SecsItem Ack)>();
        foreach (var (item, name, value) in ps)
        {
            var pd = def.Parameters.FirstOrDefault(x => x.Name == name);
            if (pd == null) bad.Add((item, Code(GemAck.CpackUnknownName)));
            else if (pd.Format != null && value.Format != pd.Format) bad.Add((item, Code(GemAck.CpackIllegalFormat)));
        }
        if (bad.Count > 0) { Respond(e, Reply(GemAck.HcackParameterInvalid, bad)); return; }
        var handler = RemoteCommandReceived;
        if (handler == null) { Respond(e, Reply(GemAck.HcackCannotPerform)); return; }

        var args = new GemRemoteCommandEventArgs(def.Name, [.. ps.Select(p => (p.Name, p.Value))], objspec);
        string what = $"S2F{(enhanced ? 49 : 41)} {def.Name}";
        if (!e.Message.ReplyExpected)
        {
            int r = CurrentRun;
            PostApp(() =>                                                              // W비트 없음: 응답 없이 알리기만
            {
                try { handler(this, args); }
                catch (Exception ex) { ErrorIn(r, $"{what}: RemoteCommandReceived 핸들러 예외: {ex.GetType().Name}: {ex.Message}"); }
            });
            return;
        }
        // PP-SELECT의 PPID(정의에 PPID 파라미터가 있을 때만 존재 검사와 선택 반영)
        bool checkPpid = ppSelect && def.Parameters.Any(x => x.Name == GemCatalog.PpSelectPpid);
        var ppidParam = ps.FirstOrDefault(p => p.Name == GemCatalog.PpSelectPpid);
        string? ppid = ppidParam.Value is { Format: SecsFormat.ASCII } v ? v.GetString() : null;
        var d = DeferReply(e, Reply(GemAck.HcackCannotPerform), what);
        PostApp(() =>
        {
            if (checkPpid && !ProcessProgramExists(ppid))
            {
                FinishDeferred(d, Reply(GemAck.HcackParameterInvalid, [(ppidParam.NameItem ?? SecsItem.A(GemCatalog.PpSelectPpid), Code(GemAck.CpackIllegalValue))]));
                return;
            }
            SecsMessage reply;
            byte hcack = GemAck.HcackCannotPerform;
            try
            {
                handler(this, args);
                hcack = args.Hcack;
                if (args.ParameterAcks.Count > 0 && hcack == GemAck.HcackAccepted) hcack = GemAck.HcackParameterInvalid;
                reply = Reply(hcack, [.. args.ParameterAcks.Select(a => (SecsItem.A(a.Name), a.Ack))]);
            }
            catch (Exception ex)
            {
                reply = Reply(GemAck.HcackCannotPerform);                                  // 실패 쪽으로: 실행하지 않은 것으로 답함
                hcack = GemAck.HcackCannotPerform;
                ErrorIn(d.Run, $"{what}: RemoteCommandReceived 핸들러 예외로 HCACK 2로 응답합니다: {ex.GetType().Name}: {ex.Message}");
            }
            // 앱이 HCACK 0으로 받아들인 PP-SELECT는 응답 뒤 선택을 반영한다(PPExecName + ProcessProgramSelected)
            if (FinishDeferred(d, reply) && checkPpid && hcack == GemAck.HcackAccepted)
            {
                try { SelectProcessProgram(ppid); }
                catch (Exception ex) { ErrorIn(d.Run, $"{what}: 선택을 반영하지 못했습니다: {ex.GetType().Name}: {ex.Message}"); }
            }
        });
    }

    partial void HandleRegistered(SecsPrimaryEventArgs e)
    {
        var handler = MessageReceived;
        if (handler == null) return;                                       // W면 전송 계층이 SxF0
        var m = e.Message;
        var abort = new SecsMessage(m.Stream, 0, false);
        string what = $"S{m.Stream}F{m.Function}(등록 메시지)";
        int r = CurrentRun;
        Deferred? d = m.ReplyExpected ? DeferReply(e, abort, what) : null;
        var args = new GemMessageEventArgs(m, e.Validate, reply =>
        {
            if (d == null) return false;
            lock (gate)
            {
                // Stop(→ Start)을 넘겨 늦게 부른 응답: 이전 실행의 메시지이므로 보내지 않는다
                if (!running || run != d.Run) throw new InvalidOperationException("GEM이 멈췄거나 다시 시작되어 이 메시지에는 응답할 수 없습니다.");
            }
            if (!d.TryClaim()) return false;
            lock (gate) deferreds.Remove(d);
            e.Reply(reply);
            return true;
        });
        PostApp(() =>
        {
            try { handler(this, args); }
            catch (Exception ex) { ErrorIn(r, $"{what}: MessageReceived 핸들러 예외: {ex.GetType().Name}: {ex.Message}"); }
            if (args.Close() && d != null) FinishDeferred(d, abort);
        });
    }
}
