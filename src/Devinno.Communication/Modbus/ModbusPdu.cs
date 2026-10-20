using Devinno.Communication.Internal;

namespace Devinno.Communication.Modbus;

// Modbus PDU(함수 코드부터, 주소·CRC·MBAP 제외) 생성/검증/파싱. RTU·TCP 공용.
internal static partial class ModbusPdu
{
    public const int MaxReadBits = 2000;
    public const int MaxReadWords = 125;
    public const int MaxWriteBits = 1968;
    public const int MaxWriteWords = 123;

    public static int Word(ReadOnlySpan<byte> s, int offset) => s[offset] << 8 | s[offset + 1];
    static byte Hi(int v) => (byte)((v >> 8) & 0xFF);
    static byte Lo(int v) => (byte)(v & 0xFF);

    static void CheckAddress(int address, int count, string paramName)
    {
        if (address < 0 || address + count > 0x10000)
            throw new ArgumentOutOfRangeException(paramName, "주소 범위는 0~65535 입니다.");
    }

    // 워드 값은 -32768~65535 (음수는 2의 보수로 전송). 그 밖은 하위 16비트만 보내 다른 값이 써지므로 예외
    static void CheckWord(int value, string paramName)
    {
        if (value < -32768 || value > 65535)
            throw new ArgumentOutOfRangeException(paramName, value, "워드 값은 -32768~65535여야 합니다.");
    }

    public static byte[] ReadRequest(ModbusFunction fn, int start, int length)
    {
        int max = fn switch
        {
            ModbusFunction.BITREAD_F1 or ModbusFunction.BITREAD_F2 => MaxReadBits,
            ModbusFunction.WORDREAD_F3 or ModbusFunction.WORDREAD_F4 => MaxReadWords,
            _ => throw new ArgumentException("읽기 함수(FC1~FC4)가 아닙니다.", nameof(fn)),
        };
        if (length < 1 || length > max) throw new ArgumentOutOfRangeException(nameof(length), $"길이는 1~{max} 입니다.");
        CheckAddress(start, length, nameof(start));
        return [(byte)fn, Hi(start), Lo(start), Hi(length), Lo(length)];
    }

    public static byte[] WriteSingleCoil(int address, bool value)
    {
        CheckAddress(address, 1, nameof(address));
        return [5, Hi(address), Lo(address), (byte)(value ? 0xFF : 0x00), 0x00];
    }

    public static byte[] WriteSingleRegister(int address, int value)
    {
        CheckAddress(address, 1, nameof(address));
        CheckWord(value, nameof(value));
        return [6, Hi(address), Lo(address), Hi(value), Lo(value)];
    }

    public static byte[] WriteMultipleCoils(int start, bool[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length < 1 || values.Length > MaxWriteBits)
            throw new ArgumentOutOfRangeException(nameof(values), $"개수는 1~{MaxWriteBits} 입니다.");
        CheckAddress(start, values.Length, nameof(start));
        var packed = BitUtil.PackBits(values);
        return [15, Hi(start), Lo(start), Hi(values.Length), Lo(values.Length), (byte)packed.Length, .. packed];
    }

    public static byte[] WriteMultipleRegisters(int start, int[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length < 1 || values.Length > MaxWriteWords)
            throw new ArgumentOutOfRangeException(nameof(values), $"개수는 1~{MaxWriteWords} 입니다.");
        CheckAddress(start, values.Length, nameof(start));
        foreach (var v in values) CheckWord(v, nameof(values));
        var ret = new byte[6 + values.Length * 2];
        ret[0] = 16;
        ret[1] = Hi(start); ret[2] = Lo(start);
        ret[3] = Hi(values.Length); ret[4] = Lo(values.Length);
        ret[5] = (byte)(values.Length * 2);
        for (int i = 0; i < values.Length; i++)
        {
            ret[6 + i * 2] = Hi(values[i]);
            ret[7 + i * 2] = Lo(values[i]);
        }
        return ret;
    }

    public static byte[] WordBitSet(int address, int bitIndex, bool value)
    {
        CheckAddress(address, 1, nameof(address));
        if (bitIndex < 0 || bitIndex > 15) throw new ArgumentOutOfRangeException(nameof(bitIndex), "비트 인덱스는 0~15 입니다.");
        return [26, Hi(address), Lo(address), (byte)bitIndex, (byte)(value ? 0xFF : 0x00), 0x00];
    }

    // 정상 응답 PDU 길이. 쓰기 계열은 모두 5(FC + 주소 2 + 값/개수 2).
    public static int ExpectedResponseLength(ReadOnlySpan<byte> request) => (ModbusFunction)request[0] switch
    {
        ModbusFunction.BITREAD_F1 or ModbusFunction.BITREAD_F2 => 2 + (Word(request, 3) + 7) / 8,
        ModbusFunction.WORDREAD_F3 or ModbusFunction.WORDREAD_F4 => 2 + Word(request, 3) * 2,
        _ => 5,
    };

    public static bool IsException(ReadOnlySpan<byte> request, ReadOnlySpan<byte> response)
        => response.Length == 2 && response[0] == (request[0] | 0x80);

    public static bool IsValidResponse(ReadOnlySpan<byte> request, ReadOnlySpan<byte> response)
    {
        if (response.Length != ExpectedResponseLength(request) || response[0] != request[0]) return false;
        return (ModbusFunction)request[0] switch
        {
            ModbusFunction.BITREAD_F1 or ModbusFunction.BITREAD_F2 or
            ModbusFunction.WORDREAD_F3 or ModbusFunction.WORDREAD_F4 => response[1] == response.Length - 2,
            _ => response[1] == request[1] && response[2] == request[2],
        };
    }

    public static int[] Words(ReadOnlySpan<byte> data)
    {
        var ret = new int[data.Length / 2];
        for (int i = 0; i < ret.Length; i++) ret[i] = Word(data, i * 2);
        return ret;
    }

    public static bool[] ParseBits(ReadOnlySpan<byte> request, ReadOnlySpan<byte> response)
        => BitUtil.UnpackBits(response.Slice(2, response[1]), Word(request, 3));

    public static int[] ParseWords(ReadOnlySpan<byte> response) => Words(response.Slice(2, response[1]));
}
