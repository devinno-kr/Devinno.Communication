namespace Devinno.Communication.Secs.Data;

/// <summary>
/// Thrown by <c>Send</c>/<c>SendAsync</c> when the reply to a sent primary message is not received within T3. The connection is kept.
/// </summary>
/// <remarks>If the role is Equipment, S9F9 (transaction timer timeout) is automatically sent to the peer at this point.</remarks>
/// <param name="primary">The primary message for which no reply was received.</param>
/// <param name="message">Error description.</param>
public class SecsTimeoutException(SecsMessage primary, string message) : TimeoutException(message)
{
    /// <summary>The primary message for which no reply was received (including the system bytes sent).</summary>
    public SecsMessage Primary { get; } = primary;
}

/// <summary>
/// Thrown by <c>Send</c>/<c>SendAsync</c> when the peer replies to a primary message with SxF0 (abort).
/// </summary>
/// <param name="primary">The primary message that was sent.</param>
/// <param name="reply">The SxF0 message that was received.</param>
public class SecsAbortException(SecsMessage primary, SecsMessage reply)
    : Exception($"S{primary.Stream}F{primary.Function}에 상대가 S{reply.Stream}F0(중단)으로 응답했습니다.")
{
    /// <summary>The primary message that was sent.</summary>
    public SecsMessage Primary { get; } = primary;
    /// <summary>The SxF0 message that was received.</summary>
    public SecsMessage Reply { get; } = reply;
}

/// <summary>
/// Thrown by <c>Send</c>/<c>SendAsync</c> when the peer (equipment) sends an S9Fx error message for the primary message that was sent.
/// </summary>
/// <remarks>S9F1 unrecognized device ID, S9F3 unrecognized stream type, S9F5 unrecognized function type, S9F7 illegal data, S9F11 data too long. It is matched by the system bytes contained in the MHEAD (10 bytes) of the S9.</remarks>
/// <param name="primary">The primary message that was sent.</param>
/// <param name="error">The S9Fx message that was received.</param>
public class SecsErrorException(SecsMessage primary, SecsMessage error)
    : Exception($"S{primary.Stream}F{primary.Function}에 상대가 S9F{error.Function}({Describe(error.Function)})를 보냈습니다.")
{
    /// <summary>The primary message that was sent.</summary>
    public SecsMessage Primary { get; } = primary;
    /// <summary>Function of the S9 message received (1, 3, 5, 7, 11).</summary>
    public int Function { get; } = error.Function;
    /// <summary>The S9Fx message that was received (the body is &lt;B [10] MHEAD&gt;).</summary>
    public SecsMessage ErrorMessage { get; } = error;

    static string Describe(int function) => function switch
    {
        1 => "장치 ID 불일치",
        3 => "모르는 Stream",
        5 => "모르는 Function",
        7 => "잘못된 데이터",
        11 => "데이터가 너무 큼",
        _ => "오류",
    };
}

/// <summary>
/// Thrown when the connection is lost while waiting for a reply (including <c>Stop</c>), or when a message is sent over a connection that has been lost.
/// </summary>
public class SecsDisconnectedException : IOException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Error description.</param>
    public SecsDisconnectedException(string message) : base(message) { }

    /// <summary>Creates the exception with the causing exception.</summary>
    /// <param name="message">Error description.</param>
    /// <param name="innerException">The causing exception (e.g., the <see cref="InvalidOperationException"/> received when trying to send right after the connection was lost).</param>
    public SecsDisconnectedException(string message, Exception? innerException) : base(message, innerException) { }
}
