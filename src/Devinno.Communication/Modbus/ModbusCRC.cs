namespace Devinno.Communication.Modbus;

/// <summary>CRC-16/MODBUS calculation utility used by Modbus RTU.</summary>
public static class ModbusCRC
{
    /// <summary>Calculates the CRC-16/MODBUS value of the data.</summary>
    /// <param name="data">The bytes to calculate over.</param>
    /// <returns>The CRC value. In a frame, the low byte comes first.</returns>
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }

    /// <summary>Writes the CRC of the preceding bytes into the last 2 bytes of the frame.</summary>
    /// <param name="frame">The whole frame, including the 2 bytes reserved for the CRC.</param>
    public static void Write(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Length < 3) throw new ArgumentException("프레임은 3바이트 이상이어야 합니다.", nameof(frame));
        var crc = Compute(frame.AsSpan(0, frame.Length - 2));
        frame[^2] = (byte)(crc & 0xFF);
        frame[^1] = (byte)(crc >> 8);
    }

    /// <summary>Checks whether the CRC in the last 2 bytes of the frame is correct.</summary>
    /// <param name="frame">The whole frame, including the CRC.</param>
    /// <returns>true if the CRC matches.</returns>
    public static bool Check(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 3) return false;
        var crc = Compute(frame[..^2]);
        return frame[^2] == (byte)(crc & 0xFF) && frame[^1] == (byte)(crc >> 8);
    }
}
