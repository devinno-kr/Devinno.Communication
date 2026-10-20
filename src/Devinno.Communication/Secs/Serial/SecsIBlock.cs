using System.Buffers.Binary;
using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Serial;

// SECS-I 블록 헤더 10바이트(빅엔디언):
// 0 R비트 + 장치 ID 상위 7비트, 1 장치 ID 하위, 2 W비트 + Stream, 3 Function, 4 E비트 + 블록 번호 상위, 5 블록 번호 하위, 6~9 System Bytes
internal readonly record struct SecsIHeader(bool RBit, int DeviceID, bool WBit, int Stream, int Function, bool EBit, int BlockNumber, uint SystemBytes)
{
    public const int Length = 10;

    public static SecsIHeader Parse(ReadOnlySpan<byte> h)
    {
        ushort dev = BinaryPrimitives.ReadUInt16BigEndian(h);
        ushort blk = BinaryPrimitives.ReadUInt16BigEndian(h[4..]);
        return new((dev & 0x8000) != 0, dev & 0x7FFF, (h[2] & 0x80) != 0, h[2] & 0x7F, h[3], (blk & 0x8000) != 0, blk & 0x7FFF, BinaryPrimitives.ReadUInt32BigEndian(h[6..]));
    }

    public byte[] ToArray()
    {
        var h = new byte[Length];
        BinaryPrimitives.WriteUInt16BigEndian(h, (ushort)((RBit ? 0x8000 : 0) | DeviceID));
        h[2] = (byte)((WBit ? 0x80 : 0) | Stream);
        h[3] = (byte)Function;
        BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(4), (ushort)((EBit ? 0x8000 : 0) | BlockNumber));
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(6), SystemBytes);
        return h;
    }

    // prev 다음 블록인지: 블록 번호 +1, E비트 말고는 헤더가 같음
    public bool IsNextOf(SecsIHeader prev)
        => this with { EBit = prev.EBit, BlockNumber = prev.BlockNumber } == prev && BlockNumber == prev.BlockNumber + 1;
}

// SECS-I 블록 = 길이 바이트(10~254 = 헤더 + 데이터) + 헤더 10 + 데이터 0~244 + 체크섬 2(헤더 + 데이터의 16비트 합, 빅엔디언)
internal static class SecsIBlock
{
    public const byte ENQ = 0x05, EOT = 0x04, ACK = 0x06, NAK = 0x15;
    public const int MaxData = 244;
    public const int MinLength = SecsIHeader.Length, MaxLength = SecsIHeader.Length + MaxData;
    public const int MaxBlocks = 32767;
    // 메시지 본문 최대 바이트 = 244 × 32767
    public const int MaxMessage = MaxData * MaxBlocks;

    // 헤더 + 데이터의 16비트 산술 합
    public static ushort Checksum(ReadOnlySpan<byte> headerAndData)
    {
        int sum = 0;
        foreach (var b in headerAndData) sum += b;
        return (ushort)sum;
    }

    // 블록 하나 (길이 바이트·체크섬 포함)
    public static byte[] Block(SecsIHeader h, ReadOnlySpan<byte> data)
    {
        var b = new byte[1 + SecsIHeader.Length + data.Length + 2];
        b[0] = (byte)(SecsIHeader.Length + data.Length);
        h.ToArray().CopyTo(b, 1);
        data.CopyTo(b.AsSpan(1 + SecsIHeader.Length));
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(b.Length - 2), Checksum(b.AsSpan(1, b.Length - 3)));
        return b;
    }

    // 본문 길이 → 블록 수 (빈 본문도 블록 1개)
    public static int BlockCount(int bodyLength) => Math.Max(1, (bodyLength + MaxData - 1) / MaxData);

    // 첫 블록 헤더 (S9F9의 SHEAD용). rbit = 보내는 쪽이 Equipment
    public static SecsIHeader FirstHeader(SecsMessage wire, bool rbit, int blockCount)
        => new(rbit, wire.DeviceID, wire.ReplyExpected, wire.Stream, wire.Function, blockCount == 1, 1, wire.SystemBytes);

    // 메시지 → 블록들 (번호 1부터, 마지막 블록 E비트, 빈 본문은 블록 1개). 본문이 너무 크면 ArgumentException
    public static List<byte[]> Split(SecsMessage wire, bool rbit)
    {
        var body = SecsCodec.Encode(wire.Item);
        if (body.Length > MaxMessage) throw new ArgumentException($"SECS-I 메시지 본문은 최대 {MaxMessage}바이트입니다. 현재: {body.Length}", nameof(wire));
        int count = BlockCount(body.Length);
        var first = FirstHeader(wire, rbit, count);
        var blocks = new List<byte[]>(count);
        for (int i = 0; i < count; i++)
        {
            int off = i * MaxData;
            var h = first with { EBit = i == count - 1, BlockNumber = i + 1 };
            blocks.Add(Block(h, body.AsSpan(off, Math.Min(MaxData, body.Length - off))));
        }
        return blocks;
    }
}
