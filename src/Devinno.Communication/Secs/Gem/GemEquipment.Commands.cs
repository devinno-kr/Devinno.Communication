using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

/// <summary>Arguments for when the Host sent a remote command (S2F41). If the handler sets <see cref="Hcack"/>, the reply uses that value when the handler returns.</summary>
/// <remarks>
/// <para>GEM has already checked the command name, parameter names, and formats against the definition. A missing parameter is judged by the application and reported with <see cref="SetParameterAck"/>.</para>
/// <para>Reply to a long-running job with <see cref="GemAck.HcackAcceptedLater"/> (4), do the job separately, and report its completion with <c>TriggerEvent</c>. If the handler does not return within <c>CommandReplyTimeout</c>, GEM replies with HCACK 2 on its behalf.</para>
/// </remarks>
public class GemRemoteCommandEventArgs : EventArgs
{
    readonly List<(string Name, byte Cpack)> acks = [];

    internal GemRemoteCommandEventArgs(string command, IReadOnlyList<(string Name, SecsItem Value)> parameters)
    {
        Command = command;
        Parameters = parameters;
    }

    /// <summary>Command name (RCMD, the name in the definition)</summary>
    public string Command { get; }
    /// <summary>List of received parameters (CPNAME, CPVAL), in the order received</summary>
    public IReadOnlyList<(string Name, SecsItem Value)> Parameters { get; }
    /// <summary>HCACK to reply with (default: 0 = accepted and executed). Use the Hcack... values of <see cref="GemAck"/>.</summary>
    public byte Hcack { get; set; }

    /// <summary>Puts an error (CPACK) for one parameter in the reply. If at least one is put and <see cref="Hcack"/> is 0, HCACK 3 is sent.</summary>
    /// <param name="name">Parameter name (a name that was not received is also allowed: reports a missing parameter)</param>
    /// <param name="cpack">CPACK, 1 to 255 (1 unknown name, 2 invalid value, 3 invalid format)</param>
    /// <exception cref="ArgumentNullException">name is null</exception>
    /// <exception cref="ArgumentOutOfRangeException">cpack is 0</exception>
    public void SetParameterAck(string name, byte cpack)
    {
        GemDefinitionCheck.Ascii(name, nameof(name));
        if (cpack == 0) throw new ArgumentOutOfRangeException(nameof(cpack), cpack, "CPACK은 1 이상입니다(문제가 없는 파라미터는 넣지 않음).");
        acks.Add((name, cpack));
    }

    internal IReadOnlyList<(string Name, byte Cpack)> ParameterAcks => acks;
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

    /// <summary>Raised on the application event thread when the Host's remote command (S2F41) passed the checks. When the handler returns, the reply uses <see cref="GemRemoteCommandEventArgs.Hcack"/>.</summary>
    /// <remarks>
    /// <para>Cases where GEM replies first (the application is not called): a command not in the definitions HCACK 1, ON-LINE LOCAL HCACK 2, unknown parameter (CPACK 1) or format mismatch (CPACK 3) HCACK 3, no subscriber HCACK 2.</para>
    /// <para>If the handler does not return within <see cref="CommandReplyTimeout"/>, GEM replies with HCACK 2 on its behalf (<see cref="ProtocolError"/>) and discards the result that returns later. The handler keeps running, so the action may still be performed while the host believes it was not. Keep handlers short; for long actions reply HCACK 4 and report completion with an event. If the handler throws, GEM replies with HCACK 2 (cannot be executed) regardless of the Hcack and CPACK set so far, and reports it through <see cref="ProtocolError"/>.</para>
    /// </remarks>
    public event EventHandler<GemRemoteCommandEventArgs>? RemoteCommandReceived;

    /// <summary>Raised on the application event thread when a registered message (<see cref="RegisterMessage"/>) was received. Reply with <see cref="GemMessageEventArgs.Reply"/> inside the handler.</summary>
    /// <remarks>While OFF-LINE or before communication is established, GEM replies with SxF0 and no event is raised. If there is no subscriber or the handler does not reply (including deadline exceeded or an exception), the reply is SxF0. A handler exception is reported through <see cref="ProtocolError"/>.</remarks>
    public event EventHandler<GemMessageEventArgs>? MessageReceived;

    partial void AddCommandHandlers() => handlers[S2F41] = OnS2F41;

    // S2F41 <L RCMD <L <L CPNAME CPVAL>…>> → <L HCACK <L <L CPNAME CPACK>…>>
    void OnS2F41(SecsPrimaryEventArgs e)
    {
        // 구조 오류·이름이 A(또는 정수 하나)가 아님 = FormatException(S9F7). CPNAME 아이템은 CPACK에 그대로 되돌린다(D3)
        var (rcmd, ps) = GemMessages.ParseS2F41Raw(e.Message);

        if (!catalog.Commands.TryGetValue(rcmd, out var def)) { Respond(e, GemMessages.S2F42(GemAck.HcackInvalidCommand)); return; }
        bool local;
        lock (gate) local = control == GemControlState.OnlineLocal;
        if (local) { Respond(e, GemMessages.S2F42(GemAck.HcackCannotPerform)); return; }
        var bad = new List<(SecsItem Name, byte Cpack)>();
        foreach (var (item, name, value) in ps)
        {
            var pd = def.Parameters.FirstOrDefault(x => x.Name == name);
            if (pd == null) bad.Add((item, GemAck.CpackUnknownName));
            else if (pd.Format != null && value.Format != pd.Format) bad.Add((item, GemAck.CpackIllegalFormat));
        }
        if (bad.Count > 0) { Respond(e, GemMessages.S2F42(GemAck.HcackParameterInvalid, bad)); return; }
        var handler = RemoteCommandReceived;
        if (handler == null) { Respond(e, GemMessages.S2F42(GemAck.HcackCannotPerform)); return; }

        var args = new GemRemoteCommandEventArgs(def.Name, [.. ps.Select(p => (p.Name, p.Value))]);
        string what = $"S2F41 {def.Name}";
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
        var d = DeferReply(e, GemMessages.S2F42(GemAck.HcackCannotPerform), what);
        PostApp(() =>
        {
            SecsMessage reply;
            try
            {
                handler(this, args);
                byte hcack = args.Hcack;
                if (args.ParameterAcks.Count > 0 && hcack == GemAck.HcackAccepted) hcack = GemAck.HcackParameterInvalid;
                reply = GemMessages.S2F42(hcack, args.ParameterAcks);
            }
            catch (Exception ex)
            {
                reply = GemMessages.S2F42(GemAck.HcackCannotPerform);                      // 실패 쪽으로: 실행하지 않은 것으로 답함
                ErrorIn(d.Run, $"{what}: RemoteCommandReceived 핸들러 예외로 HCACK 2로 응답합니다: {ex.GetType().Name}: {ex.Message}");
            }
            FinishDeferred(d, reply);
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
