namespace Devinno.Communication.Modbus.RTU;

// RTU 프레임: [Slave][PDU...][CRC Lo][CRC Hi]
internal static class ModbusRtuFrame
{
    public static byte[] Build(int slave, ReadOnlySpan<byte> pdu)
    {
        var f = new byte[pdu.Length + 3];
        f[0] = (byte)slave;
        pdu.CopyTo(f.AsSpan(1));
        ModbusCRC.Write(f);
        return f;
    }

    // 요청 프레임 전체 길이. 바이트가 부족하거나 모르는 FC면 null(유휴 시간으로 판단).
    public static int? RequestLength(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 2) return null;
        switch (buffer[1])
        {
            case >= 1 and <= 6: return 8;
            case 15 or 16: return buffer.Length < 7 ? null : 9 + buffer[6];
            case 26: return 9;
            default: return null;
        }
    }
}
