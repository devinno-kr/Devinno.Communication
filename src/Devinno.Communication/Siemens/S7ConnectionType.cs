namespace Devinno.Communication.Siemens;

/// <summary>Connection type placed in the first byte of the remote TSAP. The PLC allocates connection resources according to this value.</summary>
public enum S7ConnectionType
{
    /// <summary>PG (programming device) connection = 0x01 (default of Snap7 and libnodave; confirmed from a capture of a real S7-300).</summary>
    PG = 1,
    /// <summary>OP (operator panel) connection = 0x02.</summary>
    OP = 2,
    /// <summary>S7 Basic (other) connection = 0x03 (default of S7.Net).</summary>
    Basic = 3,
}
