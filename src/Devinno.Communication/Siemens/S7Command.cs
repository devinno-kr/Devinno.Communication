namespace Devinno.Communication.Siemens;

/// <summary>Request type of S7Ethernet.</summary>
public enum S7Command
{
    /// <summary>Random (individual) read: reads addresses in one request (Read Var). At most 19 addresses (the request and response must fit in 240 bytes).</summary>
    Read,
    /// <summary>Block read: reads a given number of bytes from a byte address (split to fit the negotiated PDU size).</summary>
    ReadBlock,
    /// <summary>Random (individual) write: writes addresses and values in one request (Write Var). At most 12 bits, bytes, or words, or at most 11 double words (the request must fit in 240 bytes).</summary>
    Write,
    /// <summary>Block write: writes a byte array from a byte address (split to fit the negotiated PDU size).</summary>
    WriteBlock,
}
