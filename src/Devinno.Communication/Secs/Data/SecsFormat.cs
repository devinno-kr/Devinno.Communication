namespace Devinno.Communication.Secs.Data;

/// <summary>
/// SECS-II item format. The values are the format codes of SEMI E5 (written in octal).
/// </summary>
/// <remarks>
/// The format byte is (code &lt;&lt; 2) | number of length bytes. Example: U4 = octal 54 = 0x2C, so with 1 length byte it is 0xB1.
/// </remarks>
public enum SecsFormat
{
    /// <summary>List (octal 00). The length is the number of child items.</summary>
    List = 0x00,
    /// <summary>Binary (octal 10). A byte array.</summary>
    Binary = 0x08,
    /// <summary>Boolean (octal 11). 1 byte per element; 0 = false.</summary>
    Boolean = 0x09,
    /// <summary>ASCII string (octal 20). 8-bit bytes are preserved as they are (Latin-1).</summary>
    ASCII = 0x10,
    /// <summary>JIS-8 string (octal 21). Preserved only as raw bytes.</summary>
    JIS8 = 0x11,
    /// <summary>2-byte character (octal 22). Preserved only as raw bytes.</summary>
    C2 = 0x12,
    /// <summary>8-byte signed integer (octal 30).</summary>
    I8 = 0x18,
    /// <summary>1-byte signed integer (octal 31).</summary>
    I1 = 0x19,
    /// <summary>2-byte signed integer (octal 32).</summary>
    I2 = 0x1A,
    /// <summary>4-byte signed integer (octal 34).</summary>
    I4 = 0x1C,
    /// <summary>8-byte floating point (octal 40).</summary>
    F8 = 0x20,
    /// <summary>4-byte floating point (octal 44).</summary>
    F4 = 0x24,
    /// <summary>8-byte unsigned integer (octal 50).</summary>
    U8 = 0x28,
    /// <summary>1-byte unsigned integer (octal 51).</summary>
    U1 = 0x29,
    /// <summary>2-byte unsigned integer (octal 52).</summary>
    U2 = 0x2A,
    /// <summary>4-byte unsigned integer (octal 54).</summary>
    U4 = 0x2C,
}
