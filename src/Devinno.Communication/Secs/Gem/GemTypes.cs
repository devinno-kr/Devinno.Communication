namespace Devinno.Communication.Secs.Gem;

/// <summary>
/// The GEM communication state (E30 Communications State Model).
/// </summary>
/// <remarks>The substates inside NOT COMMUNICATING (WAIT CRA, WAIT DELAY, WAIT CR FROM HOST) are not exposed (for diagnostics, see <c>ProtocolError</c>).</remarks>
public enum GemCommunicationState
{
    /// <summary>Communication is off (DISABLED). Received messages are not interpreted; if the W-bit is set, the reply is SxF0.</summary>
    Disabled,
    /// <summary>Communication is on but not yet established (NOT COMMUNICATING). Only S1F13 is exchanged.</summary>
    NotCommunicating,
    /// <summary>Communication is established (COMMUNICATING). The S1F13/S1F14 exchange has completed with COMMACK 0.</summary>
    Communicating,
}

/// <summary>
/// Whether communication is enabled at startup (<c>GemEquipment.InitialCommunicationState</c>).
/// </summary>
public enum GemEnableState
{
    /// <summary>Starts disabled (DISABLED). Enable it with <c>EnableCommunication()</c>.</summary>
    Disabled,
    /// <summary>Starts enabled (ENABLED → NOT COMMUNICATING).</summary>
    Enabled,
}

/// <summary>
/// The GEM control state (E30 Control State Model). The values match the ControlState SV values.
/// </summary>
public enum GemControlState
{
    /// <summary>The operator put the equipment OFF-LINE (EQUIPMENT OFF-LINE).</summary>
    EquipmentOffline = 1,
    /// <summary>Attempting to go ON-LINE (ATTEMPT ON-LINE): sends S1F1 and waits for S1F2.</summary>
    AttemptOnline = 2,
    /// <summary>The Host put the equipment OFF-LINE (HOST OFF-LINE): the equipment goes ON-LINE on the Host S1F17.</summary>
    HostOffline = 3,
    /// <summary>ON-LINE LOCAL: the Host can only read and set; remote commands are rejected with HCACK 2.</summary>
    OnlineLocal = 4,
    /// <summary>ON-LINE REMOTE: the Host can also send remote commands.</summary>
    OnlineRemote = 5,
}

/// <summary>
/// The position of the operator LOCAL/REMOTE switch. When the equipment goes ON-LINE, it becomes LOCAL or REMOTE according to this position.
/// </summary>
public enum GemLocalRemote
{
    /// <summary>LOCAL</summary>
    Local,
    /// <summary>REMOTE</summary>
    Remote,
}

/// <summary>
/// The reason a message sent by GEM failed or was dropped without being sent (the <c>SendFailed</c> event).
/// </summary>
public enum GemSendFailure
{
    /// <summary>No reply within T3 (treated as a communication failure; the state goes to NOT COMMUNICATING).</summary>
    Timeout,
    /// <summary>The Host replied with SxF0.</summary>
    Aborted,
    /// <summary>The Host replied with S9Fx.</summary>
    Error,
    /// <summary>There is no connection, or the connection dropped while sending.</summary>
    Disconnected,
    /// <summary>The Host replied with a nonzero ack (the value is in <c>Ack</c>).</summary>
    NotAcknowledged,
    /// <summary>The structure of the reply body differs from what was expected.</summary>
    InvalidReply,
    /// <summary>Dropped because the send queue was full (<c>MaxPendingSends</c>).</summary>
    QueueFull,
    /// <summary>Dropped because the equipment is ON-LINE but communication is not established, or the queue was flushed after a communication failure. When spooling is on and active and the Host selected the message for spooling (S2F43), it is put in the spool instead and this is not reported.</summary>
    NotCommunicating,
    /// <summary>Dropped because the queue was flushed when communication was turned off (DISABLED).</summary>
    Disabled,
    /// <summary>Dropped because the spool was full (a new message when OverWriteSpool is false, or the oldest one when it is true).</summary>
    SpoolFull,
}

/// <summary>
/// The spooling state (E30 spooling state model, equipment side).
/// </summary>
public enum GemSpoolState
{
    /// <summary>Spooling is off (<c>SpoolDirectory</c> is not set), or it is on but no message is being spooled and none is waiting.</summary>
    Inactive,
    /// <summary>Spooling is active and messages are being stored without being sent (SPOOL ACTIVE, NO OUTPUT).</summary>
    NoOutput,
    /// <summary>Spooled messages are being sent to the Host (SPOOL ACTIVE, TRANSMIT).</summary>
    Transmit,
}
