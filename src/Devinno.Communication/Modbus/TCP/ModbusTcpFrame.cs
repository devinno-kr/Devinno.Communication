namespace Devinno.Communication.Modbus.TCP;

// Modbus TCP 프레임: MBAP[TID 2][Protocol 2 = 0][Length 2][Unit 1] + PDU. Length = Unit 1 + PDU 길이.
internal static class ModbusTcpFrame
{
    public static byte[] Build(int transactionId, int unitId, ReadOnlySpan<byte> pdu)
    {
        var f = new byte[7 + pdu.Length];
        f[0] = (byte)((transactionId >> 8) & 0xFF);
        f[1] = (byte)(transactionId & 0xFF);
        int len = pdu.Length + 1;
        f[4] = (byte)(len >> 8);
        f[5] = (byte)(len & 0xFF);
        f[6] = (byte)unitId;
        pdu.CopyTo(f.AsSpan(7));
        return f;
    }

    // 프레임 전체 길이. 7바이트 미만이면 null, 헤더가 깨졌으면 -1.
    public static int? FrameLength(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 7) return null;
        int protocol = buffer[2] << 8 | buffer[3];
        int len = buffer[4] << 8 | buffer[5];
        if (protocol != 0 || len < 2 || len > 254) return -1;
        return 6 + len;
    }
}
