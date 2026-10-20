namespace Devinno.Communication.Omron.Data;

/// <summary>Request types of an Omron FINS master (FinsUdp, FinsTcp, FinsSerial).</summary>
public enum FinsCommand
{
    /// <summary>Random (individual) read: reads several addresses in one request (0104 MULTIPLE MEMORY AREA READ).</summary>
    Read,
    /// <summary>Block read: reads the given number of words or bits from the start address (0101 MEMORY AREA READ; split into several requests when it exceeds the per-request limit).</summary>
    ReadBlock,
    /// <summary>Write: writes one value to one address (0102 MEMORY AREA WRITE).</summary>
    Write,
    /// <summary>Block write: writes word or bit values in order from the start address (0102 MEMORY AREA WRITE; split into several requests when it exceeds the per-request limit).</summary>
    WriteBlock,
}
