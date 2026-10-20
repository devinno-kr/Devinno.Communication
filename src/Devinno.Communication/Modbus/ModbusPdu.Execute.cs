using Devinno.Communication.Internal;

namespace Devinno.Communication.Modbus;

// Slave가 배열에 반영한 쓰기 정보
internal readonly record struct ModbusWrite(ModbusFunction Function, int StartAddress, int Length, bool IsBit);

internal static partial class ModbusPdu
{
    const byte IllegalFunction = 1;
    const byte IllegalDataAddress = 2;
    const byte IllegalDataValue = 3;

    static byte[] Error(byte fc, byte code) => [(byte)(fc | 0x80), code];
    static bool InRange(int? areaLength, int start, int length) => areaLength is int n && start + length <= n;

    // 요청 PDU를 배열에 대해 실행하고 응답 PDU를 돌려준다. 호출자가 lock을 잡는다.
    public static byte[] Execute(ReadOnlySpan<byte> req, bool[]? coils, bool[]? contacts, int[]? holding, int[]? input, out ModbusWrite? written)
    {
        written = null;
        if (req.Length < 1) return Error(0, IllegalDataValue);
        byte fc = req[0];

        switch ((ModbusFunction)fc)
        {
            case ModbusFunction.BITREAD_F1:
            case ModbusFunction.BITREAD_F2:
                {
                    if (req.Length != 5) return Error(fc, IllegalDataValue);
                    int start = Word(req, 1), len = Word(req, 3);
                    if (len < 1 || len > MaxReadBits) return Error(fc, IllegalDataValue);
                    var area = fc == 1 ? coils : contacts;
                    if (!InRange(area?.Length, start, len)) return Error(fc, IllegalDataAddress);
                    var packed = BitUtil.PackBits(area!.AsSpan(start, len));
                    return [fc, (byte)packed.Length, .. packed];
                }
            case ModbusFunction.WORDREAD_F3:
            case ModbusFunction.WORDREAD_F4:
                {
                    if (req.Length != 5) return Error(fc, IllegalDataValue);
                    int start = Word(req, 1), len = Word(req, 3);
                    if (len < 1 || len > MaxReadWords) return Error(fc, IllegalDataValue);
                    var area = fc == 3 ? holding : input;
                    if (!InRange(area?.Length, start, len)) return Error(fc, IllegalDataAddress);
                    var ret = new byte[2 + len * 2];
                    ret[0] = fc; ret[1] = (byte)(len * 2);
                    for (int i = 0; i < len; i++)
                    {
                        ret[2 + i * 2] = Hi(area![start + i]);
                        ret[3 + i * 2] = Lo(area[start + i]);
                    }
                    return ret;
                }
            case ModbusFunction.BITWRITE_F5:
                {
                    if (req.Length != 5) return Error(fc, IllegalDataValue);
                    int addr = Word(req, 1), val = Word(req, 3);
                    if (val != 0xFF00 && val != 0x0000) return Error(fc, IllegalDataValue);
                    if (!InRange(coils?.Length, addr, 1)) return Error(fc, IllegalDataAddress);
                    coils![addr] = val == 0xFF00;
                    written = new ModbusWrite(ModbusFunction.BITWRITE_F5, addr, 1, true);
                    return req.ToArray();
                }
            case ModbusFunction.WORDWRITE_F6:
                {
                    if (req.Length != 5) return Error(fc, IllegalDataValue);
                    int addr = Word(req, 1);
                    if (!InRange(holding?.Length, addr, 1)) return Error(fc, IllegalDataAddress);
                    holding![addr] = Word(req, 3);
                    written = new ModbusWrite(ModbusFunction.WORDWRITE_F6, addr, 1, false);
                    return req.ToArray();
                }
            case ModbusFunction.MULTIBITWRITE_F15:
                {
                    if (req.Length < 6) return Error(fc, IllegalDataValue);
                    int start = Word(req, 1), count = Word(req, 3), byteCount = req[5];
                    if (count < 1 || count > MaxWriteBits || byteCount != (count + 7) / 8 || req.Length != 6 + byteCount)
                        return Error(fc, IllegalDataValue);
                    if (!InRange(coils?.Length, start, count)) return Error(fc, IllegalDataAddress);
                    var bits = BitUtil.UnpackBits(req.Slice(6, byteCount), count);
                    Array.Copy(bits, 0, coils!, start, count);
                    written = new ModbusWrite(ModbusFunction.MULTIBITWRITE_F15, start, count, true);
                    return [fc, req[1], req[2], req[3], req[4]];
                }
            case ModbusFunction.MULTIWORDWRITE_F16:
                {
                    if (req.Length < 6) return Error(fc, IllegalDataValue);
                    int start = Word(req, 1), count = Word(req, 3), byteCount = req[5];
                    if (count < 1 || count > MaxWriteWords || byteCount != count * 2 || req.Length != 6 + byteCount)
                        return Error(fc, IllegalDataValue);
                    if (!InRange(holding?.Length, start, count)) return Error(fc, IllegalDataAddress);
                    for (int i = 0; i < count; i++) holding![start + i] = Word(req, 6 + i * 2);
                    written = new ModbusWrite(ModbusFunction.MULTIWORDWRITE_F16, start, count, false);
                    return [fc, req[1], req[2], req[3], req[4]];
                }
            case ModbusFunction.WORDBITSET_F26:
                {
                    if (req.Length != 6) return Error(fc, IllegalDataValue);
                    int addr = Word(req, 1), bit = req[3], val = Word(req, 4);
                    if (bit > 15 || (val != 0xFF00 && val != 0x0000)) return Error(fc, IllegalDataValue);
                    if (!InRange(holding?.Length, addr, 1)) return Error(fc, IllegalDataAddress);
                    int v = holding![addr] & 0xFFFF;
                    holding[addr] = val == 0xFF00 ? v | (1 << bit) : v & ~(1 << bit);
                    written = new ModbusWrite(ModbusFunction.WORDBITSET_F26, addr, 1, false);
                    return [fc, req[1], req[2], req[4], req[5]];
                }
            default:
                return Error(fc, IllegalFunction);
        }
    }
}
