namespace Devinno.Communication.Secs.Hsms;

/// <summary>
/// HSMS connection mode. It is decided separately from the Host/Equipment role (usually Equipment = Passive, Host = Active).
/// </summary>
public enum HsmsMode
{
    /// <summary>TCP client. Connects to the peer (<c>RemoteIP</c>:<c>RemotePort</c>) and requests Select.</summary>
    Active,
    /// <summary>TCP server. Waits for a connection on <c>LocalIP</c>:<c>LocalPort</c> and answers the peer's Select request. Accepts only one connection at a time.</summary>
    Passive,
}

/// <summary>
/// HSMS connection state.
/// </summary>
public enum HsmsState
{
    /// <summary>No TCP connection.</summary>
    NotConnected,
    /// <summary>TCP is connected but not yet Selected (data messages cannot be exchanged).</summary>
    NotSelected,
    /// <summary>Select is complete. Data messages can be exchanged.</summary>
    Selected,
}
