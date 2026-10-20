namespace Devinno.Communication.LS.Data;

/// <summary>A device and value pair used for a random (individual) write (WSS).</summary>
/// <param name="Device">Device name (for example %MW100, %MX0).</param>
/// <param name="Value">Value to write. 0 or 1 for a bit (X); otherwise a value within the size range (negative values are sent as two's complement).</param>
public readonly record struct LsValue(string Device, long Value);
