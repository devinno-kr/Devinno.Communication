namespace Devinno.Communication.Siemens;

/// <summary>An address and value pair used for an S7Ethernet random (individual) write (ManualWrite).</summary>
/// <param name="Address">S7 absolute address (for example DB1.DBW0, DB1.DBX0.1, M10.0, MW10, QB0).</param>
/// <param name="Value">Value to write. 0 or 1 for a bit; otherwise a value within the size range (negative values are sent as two's complement).</param>
public readonly record struct S7Value(string Address, long Value);
