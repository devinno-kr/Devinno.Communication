namespace Devinno.Communication;

/// <summary>Why a Manual write was discarded without being sent.</summary>
public enum WriteDiscardReason
{
    /// <summary>The write was requested while there was no connection (before Start, while connecting or waiting to reconnect, or after Stop), so it was not queued.</summary>
    NotConnected,
    /// <summary>The write was waiting in the queue when the connection was lost.</summary>
    Disconnected,
    /// <summary>The write was waiting in the queue when Stop() (or Dispose()) was called.</summary>
    Stopped,
}

/// <summary>Event arguments for a Manual write that was discarded without being sent.</summary>
/// <param name="messageId">The message ID given at registration (for a named write, the Id in the XML).</param>
/// <param name="reason">Why the write was discarded.</param>
public class WriteDiscardedEventArgs(int messageId, WriteDiscardReason reason) : EventArgs
{
    /// <summary>The message ID given at registration (for a named write, the Id in the XML).</summary>
    public int MessageID { get; } = messageId;
    /// <summary>Why the write was discarded.</summary>
    public WriteDiscardReason Reason { get; } = reason;
}
