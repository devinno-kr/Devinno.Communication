using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

/// <summary>Arguments for when the communication state changed.</summary>
/// <param name="previous">State before the change</param>
/// <param name="current">State after the change</param>
public class GemCommunicationStateEventArgs(GemCommunicationState previous, GemCommunicationState current) : EventArgs
{
    /// <summary>State before the change</summary>
    public GemCommunicationState Previous { get; } = previous;
    /// <summary>State after the change</summary>
    public GemCommunicationState Current { get; } = current;
}

/// <summary>Arguments for when the control state changed.</summary>
/// <param name="previous">State before the change</param>
/// <param name="current">State after the change</param>
public class GemControlStateEventArgs(GemControlState previous, GemControlState current) : EventArgs
{
    /// <summary>State before the change</summary>
    public GemControlState Previous { get; } = previous;
    /// <summary>State after the change</summary>
    public GemControlState Current { get; } = current;
}

/// <summary>Arguments for when a message sent by GEM failed or was discarded without being sent.</summary>
/// <param name="message">The primary message that was to be sent</param>
/// <param name="reason">Reason</param>
/// <param name="ack">The ack value if the Host replied with a nonzero ack</param>
/// <param name="exception">Transport exception (T3, SxF0, S9, disconnection, reply parse failure), or null if none</param>
public class GemSendFailedEventArgs(SecsMessage message, GemSendFailure reason, byte? ack, Exception? exception) : EventArgs
{
    /// <summary>The primary message that was to be sent</summary>
    public SecsMessage Message { get; } = message;
    /// <summary>Reason</summary>
    public GemSendFailure Reason { get; } = reason;
    /// <summary>The ack value if the Host replied with a nonzero ack (<see cref="GemSendFailure.NotAcknowledged"/>)</summary>
    public byte? Ack { get; } = ack;
    /// <summary>Transport exception, or null if none</summary>
    public Exception? Exception { get; } = exception;
}

/// <summary>Arguments for a diagnostic notification (S9 sent, structure error, deadline exceeded, overlapping reply, and so on).</summary>
/// <param name="reason">What happened (a diagnostic sentence)</param>
public class GemProtocolErrorEventArgs(string reason) : EventArgs
{
    /// <summary>What happened (a diagnostic sentence)</summary>
    public string Reason { get; } = reason;
}
