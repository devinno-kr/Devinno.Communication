namespace Devinno.Communication.Secs.Data;

/// <summary>
/// The role of this side in SECS communication.
/// </summary>
/// <remarks>
/// Only Equipment checks received messages and automatically sends S9Fx error messages (unrecognized device ID S9F1, parse failure S9F7, T3 exceeded S9F9, size exceeded S9F11).
/// Host does not check the device ID and does not send S9. The role is decided separately from HSMS Active/Passive.
/// </remarks>
public enum SecsRole
{
    /// <summary>Host (MES or other upper-level system).</summary>
    Host,
    /// <summary>Equipment.</summary>
    Equipment,
}
