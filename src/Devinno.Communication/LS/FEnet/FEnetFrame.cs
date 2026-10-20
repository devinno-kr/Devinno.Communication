using System.Buffers.Binary;
using System.Text;
using Devinno.Communication.LS.Data;

namespace Devinno.Communication.LS.FEnet;

// FEnet XGT 전용 프로토콜 (TCP 2004). 헤더 20바이트 + 본문, 모든 다바이트 필드는 리틀엔디언.
// 헤더: "LSIS-XGT"(8) 예약(2) PLC정보(2) CPU정보(1) 출처(1: 요청 0x33, 응답 0x11) InvokeID(2) 본문길이(2) 위치(1) 헤더BCC(1)
internal static class FEnetFrame
{
    public const int HeaderLength = 20;
    public const int MaxBytes = 1400;
    const byte ClientSource = 0x33, ServerSource = 0x11;
    const ushort ReadRequest = 0x54, ReadResponse = 0x55, WriteRequest = 0x58, WriteResponse = 0x59, Continuous = 0x14;
    static ReadOnlySpan<byte> CompanyId => "LSIS-XGT"u8;

    static byte Sum(ReadOnlySpan<byte> s) { int x = 0; foreach (var b in s) x += b; return (byte)x; }
    static ushort Get16(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt16LittleEndian(s[at..]);
    static void Put16(List<byte> b, int v) { b.Add((byte)v); b.Add((byte)(v >> 8)); }

    public static byte[] Request(int invokeId, ReadOnlySpan<byte> body)
    {
        var f = new byte[HeaderLength + body.Length];
        CompanyId.CopyTo(f);
        f[13] = ClientSource;
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(14), (ushort)invokeId);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(16), (ushort)body.Length);
        f[19] = Sum(f.AsSpan(0, 19));
        body.CopyTo(f.AsSpan(HeaderLength));
        return f;
    }

    // 응답 헤더 검사. 회사 ID와 출처(0x11)가 맞으면 (본문 길이, Invoke ID), 아니면 null. 헤더 BCC는 검사하지 않는다.
    public static (int BodyLength, int InvokeId)? ParseHeader(ReadOnlySpan<byte> h)
    {
        if (h.Length < HeaderLength || !h[..8].SequenceEqual(CompanyId) || h[13] != ServerSource) return null;
        return (Get16(h, 16), Get16(h, 14));
    }

    public static ushort DataType(LsDevice d) => d.Size switch { 'X' => 0, 'B' => 1, 'W' => 2, 'D' => 3, _ => 4 };

    static List<byte> Head(ushort cmd, ushort type, int blocks)
    {
        var b = new List<byte>();
        Put16(b, cmd); Put16(b, type); Put16(b, 0); Put16(b, blocks);
        return b;
    }

    static void Name(List<byte> b, LsDevice d) { Put16(b, d.Name.Length); b.AddRange(Encoding.ASCII.GetBytes(d.Name)); }
    static void Value(List<byte> b, long v, int width) { for (int i = 0; i < width; i++) b.Add((byte)(v >> (8 * i))); }

    public static byte[] RSS(IReadOnlyList<LsDevice> devices)
    {
        var b = Head(ReadRequest, DataType(devices[0]), devices.Count);
        foreach (var d in devices) Name(b, d);
        return [.. b];
    }

    public static byte[] RSB(LsDevice byteDevice, int byteCount)
    {
        var b = Head(ReadRequest, Continuous, 1);
        Name(b, byteDevice);
        Put16(b, byteCount);
        return [.. b];
    }

    public static byte[] WSS(IReadOnlyList<(LsDevice Device, long Value)> values)
    {
        var b = Head(WriteRequest, DataType(values[0].Device), values.Count);
        foreach (var (d, _) in values) Name(b, d);
        foreach (var (d, v) in values) { Put16(b, d.Width); Value(b, v, d.Width); }
        return [.. b];
    }

    public static byte[] WSB(LsDevice byteDevice, IReadOnlyList<long> values, int width)
    {
        var b = Head(WriteRequest, Continuous, 1);
        Name(b, byteDevice);
        Put16(b, values.Count * width);
        foreach (var v in values) Value(b, v, width);
        return [.. b];
    }

    static long ReadValue(ReadOnlySpan<byte> s, int width)
    {
        long v = 0;
        for (int i = width - 1; i >= 0; i--) v = (v << 8) | s[i];
        return v;
    }

    // 응답 본문 해석. requestBody: 보낸 요청 본문, width: 값 하나의 바이트 수, count: 개별은 블록 수, 연속은 값 개수.
    // 에러 상태가 0이 아니면 뒤 2바이트(없으면 1바이트)를 에러코드로 읽는다(매뉴얼에 1/2바이트 표기가 엇갈림).
    public static LsResponse Parse(ReadOnlySpan<byte> body, ReadOnlySpan<byte> requestBody, int width, int count)
    {
        if (body.Length < 8) return LsResponse.Invalid;
        ushort reqCmd = Get16(requestBody, 0), type = Get16(requestBody, 2);
        ushort expect = reqCmd == ReadRequest ? ReadResponse : WriteResponse;
        if (Get16(body, 0) != expect || Get16(body, 2) != type) return LsResponse.Invalid;
        if (Get16(body, 6) != 0)
            return new(LsStatus.Nak, [], body.Length >= 10 ? Get16(body, 8) : body.Length == 9 ? body[8] : 0);
        if (body.Length < 10) return LsResponse.Invalid;

        bool continuous = type == Continuous;
        if (reqCmd == WriteRequest)
            return body.Length == 10 && Get16(body, 8) == (continuous ? 1 : count) ? new(LsStatus.Ack, [], 0) : LsResponse.Invalid;
        return continuous ? ParseContinuous(body, width, count) : ParseSingles(body, width, count);
    }

    static LsResponse ParseSingles(ReadOnlySpan<byte> body, int width, int count)
    {
        if (Get16(body, 8) != count) return LsResponse.Invalid;
        var values = new long[count];
        int pos = 10;
        for (int i = 0; i < count; i++)
        {
            if (pos + 2 + width > body.Length || Get16(body, pos) != width) return LsResponse.Invalid;
            values[i] = ReadValue(body.Slice(pos + 2, width), width);
            pos += 2 + width;
        }
        return pos == body.Length ? new(LsStatus.Ack, values, 0) : LsResponse.Invalid;
    }

    static LsResponse ParseContinuous(ReadOnlySpan<byte> body, int width, int count)
    {
        if (body.Length != 12 + width * count || Get16(body, 8) != 1 || Get16(body, 10) != width * count) return LsResponse.Invalid;
        var values = new long[count];
        for (int i = 0; i < count; i++) values[i] = ReadValue(body.Slice(12 + i * width, width), width);
        return new(LsStatus.Ack, values, 0);
    }
}
