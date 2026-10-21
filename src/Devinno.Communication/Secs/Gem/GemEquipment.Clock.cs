using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

/// <summary>Source that set the GEM clock.</summary>
public enum GemClockSource
{
    /// <summary>The Host decided the time with S2F31</summary>
    S2F31,
    /// <summary>The equipment asked with S2F17 and set the time from the Host's S2F18 reply (<c>RequestHostTime</c>)</summary>
    S2F18,
}

/// <summary>Arguments for when the GEM clock was set.</summary>
/// <remarks>The library does not change the OS clock (administrator rights are required). To set the OS clock too, the application does it in this event.</remarks>
/// <param name="time">Time that was set (local)</param>
/// <param name="source">Source</param>
public class GemClockEventArgs(DateTime time, GemClockSource source) : EventArgs
{
    /// <summary>Time that was set (local)</summary>
    public DateTime Time { get; } = time;
    /// <summary>Source</summary>
    public GemClockSource Source { get; } = source;
}

/// <summary>Arguments for when a terminal message was received (one line for S10F3, several lines for S10F5).</summary>
/// <param name="tid">Terminal number TID</param>
/// <param name="lines">Text (one line for S10F3)</param>
public class GemTerminalEventArgs(byte tid, IReadOnlyList<string> lines) : EventArgs
{
    /// <summary>Terminal number TID</summary>
    public byte Tid { get; } = tid;
    /// <summary>Text (one line for S10F3)</summary>
    public IReadOnlyList<string> Lines { get; } = lines;
}

// 시계 S2F17/18·S2F31/32, 터미널 S10F1~6, 처리 상태(ProcessState)
public partial class GemEquipment
{
    const int S2F17 = 2 * 256 + 17, S2F31 = 2 * 256 + 31, S10F3 = 10 * 256 + 3, S10F5 = 10 * 256 + 5;

    bool terminalReplyExpected = true;
    TimeSpan clockOffset;                   // gate: GEM 시계 = 시스템 시각 + 오프셋
    byte processState, previousProcessState;   // gate

    /// <summary>Gets or sets whether S10F1 carries the W-bit (default: true). If true, ACKC10 other than 0 is reported through <see cref="SendFailed"/>.</summary>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public bool TerminalReplyExpected { get => terminalReplyExpected; set => Setting(() => terminalReplyExpected = value); }

    /// <summary>Gets the GEM clock (local time) = system time + the offset set by the Host. The Clock SV and the S2F18 reply use this value. The offset is 0 at Start.</summary>
    public DateTime Now { get { lock (gate) return DateTime.Now + clockOffset; } }

    /// <summary>Raised on the application event thread when the GEM clock was set (S2F31, S2F18).</summary>
    /// <remarks>If the time sent by the Host is outside local years 1970 to 2999, the clock is not set (TIACK 1 for S2F31).</remarks>
    public event EventHandler<GemClockEventArgs>? ClockSet;

    /// <summary>Raised on the application event thread when a terminal message from the Host (S10F3/S10F5) was received (already replied to with ACKC10 0).</summary>
    /// <remarks>If there is no subscriber, it replies with ACKC10 2 (no terminal).</remarks>
    public event EventHandler<GemTerminalEventArgs>? TerminalReceived;

    partial void AddClockHandlers()
    {
        handlers[S2F17] = e => Respond(e, ClockReply());
        handlers[S2F31] = OnS2F31;
        handlers[S10F3] = OnS10F3;
        handlers[S10F5] = OnS10F5;
        standardValues[GemStandard.Clock] = () => SecsItem.A(GemTime.Format(DateTime.Now + clockOffset, TimeFormatLocked));
        standardValues[GemStandard.ProcessState] = () => SecsItem.U1(processState);
        standardValues[GemStandard.PreviousProcessState] = () => SecsItem.U1(previousProcessState);
    }

    partial void ResetClock()
    {
        clockOffset = TimeSpan.Zero;
        processState = 0;
        previousProcessState = 0;
    }

    int TimeFormatLocked => (int)values[catalog.Std(GemStandard.TimeFormat)].GetInt64();

    // S2F18 <A TIME>: GEM 시계(TimeFormat 형식)
    SecsMessage ClockReply()
    {
        DateTime now;
        int timeFormat;
        lock (gate)
        {
            now = DateTime.Now + clockOffset;
            timeFormat = TimeFormatLocked;
        }
        return GemMessages.S2F18(now, timeFormat);
    }

    #region 시계
    // S2F31 TIME → TIACK. TimeFormat과 무관하게 세 형식 모두 받는다. 로컬 연도 1970~2999 밖이면 TIACK 1(시계 계산이 넘치지 않게)
    void OnS2F31(SecsPrimaryEventArgs e)
    {
        DateTime set = default;
        bool ok = true;
        try { set = GemMessages.ParseS2F31(e.Message); }
        catch (FormatException) { ok = false; }                  // A가 아니거나 해석 불가(연도 범위 밖 포함) = TIACK 1
        if (ok)
        {
            lock (gate) clockOffset = set - DateTime.Now;
        }
        Respond(e, GemMessages.S2F32(ok ? GemAck.TiackAccepted : GemAck.TiackError));
        if (ok) Raise(ClockSet, new GemClockEventArgs(set, GemClockSource.S2F31));
    }

    /// <summary>Asks the Host for the time (S2F17). When S2F18 is received, the GEM clock is set and <see cref="ClockSet"/> (S2F18) is raised.</summary>
    /// <remarks>Cases where nothing is sent (false): communication DISABLED, control OFF-LINE, or communication not yet established. If the reply cannot be parsed or the time is outside local years 1970 to 2999, <see cref="SendFailed"/> (InvalidReply) is raised and the clock is unchanged.</remarks>
    /// <returns>true if it was put in the send queue</returns>
    /// <exception cref="InvalidOperationException">Not started</exception>
    public bool RequestHostTime()
    {
        Started();
        lock (gate)
        {
            if (!running || commState != GemCommunicationState.Communicating || IsOffline(control)) return false;
            return EnqueueLocked(GemMessages.S2F17(), S2F17Done);
        }
    }

    void S2F17Done(Outgoing o, Task<SecsMessage?> t)
    {
        if (!t.IsCompletedSuccessfully) { ReportFailure(o.Message, t.Exception!.InnerException!); return; }
        DateTime time = default;
        bool ok = t.Result != null;
        if (ok)
        {
            try { time = GemMessages.ParseS2F18(t.Result!); }
            catch (FormatException) { ok = false; }
        }
        if (!ok)
        {
            Raise(SendFailed, new GemSendFailedEventArgs(o.Message, GemSendFailure.InvalidReply, null, new FormatException($"S2F18의 TIME을 해석하지 못했습니다: {(t.Result == null ? "(응답 없음)" : GemItems.Sml(t.Result))}")));
            return;
        }
        lock (gate)
        {
            if (!running) return;
            clockOffset = time - DateTime.Now;
            Raise(ClockSet, new GemClockEventArgs(time, GemClockSource.S2F18));
        }
    }
    #endregion

    #region 처리 상태
    /// <summary>Changes the equipment process state (ProcessState SV). If the value changed, PreviousProcessState is updated and the ProcessStateChange CE is sent.</summary>
    /// <remarks>The meaning of the state values (for example, 0 INIT, 1 IDLE, 2 EXECUTING ...) is decided by the equipment and written in its GEM manual. The initial value is 0.</remarks>
    /// <param name="state">New state value</param>
    /// <exception cref="InvalidOperationException">Not started</exception>
    public void SetProcessState(byte state)
    {
        Started();
        lock (gate)
        {
            if (state == processState) return;
            previousProcessState = processState;
            processState = state;
            TriggerStandardEventLocked(GemStandard.ProcessStateChange);
        }
    }
    #endregion

    #region 터미널
    // S10F3 <L TID TEXT> → ACKC10 (구독자 없으면 2) → TerminalReceived
    void OnS10F3(SecsPrimaryEventArgs e)
    {
        var (tid, text) = GemMessages.ParseS10F3(e.Message);        // 구조 오류·TID 해석 실패 = FormatException(S9F7)
        Terminal(e, tid, [text], GemMessages.S10F4);
    }

    // S10F5 <L TID <L TEXT…>> → ACKC10 → TerminalReceived
    void OnS10F5(SecsPrimaryEventArgs e)
    {
        var (tid, lines) = GemMessages.ParseS10F5(e.Message);
        Terminal(e, tid, lines, GemMessages.S10F6);
    }

    void Terminal(SecsPrimaryEventArgs e, byte tid, IReadOnlyList<string> lines, Func<byte, SecsMessage> reply)
    {
        var handler = TerminalReceived;
        Respond(e, reply(handler == null ? GemAck.Ackc10TerminalNotAvailable : GemAck.Ackc10Accepted));
        Raise(handler, new GemTerminalEventArgs(tid, lines));
    }

    /// <summary>The operator sends a terminal message to the Host (S10F1 &lt;L TID TEXT&gt;).</summary>
    /// <remarks>The conditions under which nothing is sent are the same as for <see cref="TriggerEvent"/> (false for DISABLED or OFF-LINE; false plus <see cref="SendFailed"/> for ON-LINE without communication). If ACKC10 is not 0, <see cref="SendFailed"/> (NotAcknowledged) is raised.</remarks>
    /// <param name="tid">Terminal number</param>
    /// <param name="text">Text (ASCII)</param>
    /// <returns>true if it was put in the send queue or the spool</returns>
    /// <exception cref="ArgumentNullException">text is null</exception>
    /// <exception cref="ArgumentException">text contains characters outside ASCII (Latin-1)</exception>
    /// <exception cref="InvalidOperationException">Not started</exception>
    public bool SendTerminalMessage(byte tid, string text)
    {
        GemDefinitionCheck.Ascii(text, nameof(text));
        Started();
        lock (gate)
        {
            if (!running || commState == GemCommunicationState.Disabled || IsOffline(control)) return false;
            var s10f1 = GemMessages.S10F1(tid, text, terminalReplyExpected);
            return SendOrSpoolLocked(s10f1);
        }
    }

    /// <summary>The operator reports that the Host's terminal message was acknowledged (MessageRecognition CE).</summary>
    /// <returns>true if the CE was put in the send queue or the spool (the conditions under which nothing is sent are the same as for <see cref="TriggerEvent"/>)</returns>
    /// <exception cref="InvalidOperationException">Not started</exception>
    public bool RecognizeTerminalMessage()
    {
        Started();
        lock (gate) return TriggerLocked(catalog.Events[catalog.Std(GemStandard.MessageRecognition)], null);
    }
    #endregion
}
