namespace Devinno.Communication.LS.Data;

/// <summary>LS XGT dedicated protocol command (the official name in the manual).</summary>
public enum LsCommand
{
    /// <summary>RSS: random (individual) read (up to 16 devices).</summary>
    RSS,
    /// <summary>RSB: block read.</summary>
    RSB,
    /// <summary>WSS: random (individual) write (up to 16 devices).</summary>
    WSS,
    /// <summary>WSB: block write.</summary>
    WSB,
}
