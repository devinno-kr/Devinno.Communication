namespace Devinno.Communication.Mitsubishi.Data;

/// <summary>Mitsubishi MC protocol command (the official names of the 1C frame; 3E sends them as 0401/1401, 1E as 00H to 03H).</summary>
public enum McCommand
{
    /// <summary>BR: batch read in bit units (3E 0401/0001, 1E 00H).</summary>
    BR,
    /// <summary>WR: batch read in word units (3E 0401/0000, 1E 01H). A bit device is read 16 points per word.</summary>
    WR,
    /// <summary>BW: batch write in bit units (3E 1401/0001, 1E 02H).</summary>
    BW,
    /// <summary>WW: batch write in word units (3E 1401/0000, 1E 03H). A bit device is written 16 points per word.</summary>
    WW,
}
