namespace Devinno.Communication.Mitsubishi.Data;

/// <summary>Selects the number base used to interpret X/Y device numbers.</summary>
/// <remarks>
/// The FX series (FX3, FX5) uses octal for X/Y (X0 to X7, X10, ...), while the Q/L series uses hexadecimal (X0 to XF, X10, ...).
/// Even the same "X10" is the 8th point in octal but the 16th point in hexadecimal, so it must be set to match the PLC.
/// </remarks>
public enum McXYNotation
{
    /// <summary>Automatic: octal for McSerial (1C) and McEthernet 1E (FX3), hexadecimal for McEthernet 3E (Q/L). When using an FX5 over 3E, specify Octal explicitly. FX5 over 3E uses octal X/Y: set Octal. With Auto, X/Y numbers are read as hexadecimal and address the wrong points.</summary>
    Auto,
    /// <summary>Hexadecimal (Q/L series, A series).</summary>
    Hex,
    /// <summary>Octal (FX series).</summary>
    Octal,
}
