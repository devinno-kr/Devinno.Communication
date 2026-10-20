namespace Devinno.Communication.Mitsubishi.Serial;

/// <summary>Transmission format of the 1C frame (must match the PLC's computer link setting).</summary>
public enum McSerialFormat
{
    /// <summary>Format 1: no CR LF at the end of the frame.</summary>
    Format1,
    /// <summary>Format 4: CR LF is appended to the end of every frame (request, response, ACK).</summary>
    Format4,
}
