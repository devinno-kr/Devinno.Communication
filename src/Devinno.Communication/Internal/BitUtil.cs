namespace Devinno.Communication.Internal;

// 비트 배열 ↔ 바이트 변환. 비트 i는 바이트 i/8의 (i%8)번 비트(LSB 먼저).
internal static class BitUtil
{
    public static byte[] PackBits(ReadOnlySpan<bool> bits)
    {
        var ret = new byte[(bits.Length + 7) / 8];
        for (int i = 0; i < bits.Length; i++)
            if (bits[i]) ret[i / 8] |= (byte)(1 << (i % 8));
        return ret;
    }

    public static bool[] UnpackBits(ReadOnlySpan<byte> bytes, int count)
    {
        var ret = new bool[count];
        for (int i = 0; i < count && i / 8 < bytes.Length; i++)
            ret[i] = (bytes[i / 8] & (1 << (i % 8))) != 0;
        return ret;
    }
}
