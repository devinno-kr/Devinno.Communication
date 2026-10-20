using System.Buffers.Binary;

namespace Devinno.Communication.Omron.Data;

// FINS 헤더의 주소 6바이트 (명령 기준: 목적지 네트워크·노드·유닛, 출발지 네트워크·노드·유닛)
internal readonly record struct FinsNodes(byte Dna, byte Da1, byte Da2, byte Sna, byte Sa1, byte Sa2);

// FINS 명령 프레임 생성·응답 해석·조각 나누기. FinsUdp·FinsTcp·FinsSerial이 함께 쓴다.
// 본문(body)은 MRC부터다: 요청 = MRC SRC 파라미터, 응답 = MRC SRC MRES SRES 데이터. 숫자는 모두 빅엔디언.
internal static class FinsFrame
{
    public const int HeaderLength = 10;
    // ICF 0x80 = 명령, 응답 필요. GCT 0x02 (W421 7-2)
    public const byte IcfCommand = 0x80, Gct = 0x02;
    public const byte MrcMemory = 0x01, SrcRead = 0x01, SrcWrite = 0x02, SrcMultiRead = 0x04;

    // Ethernet: 0104 한 요청의 최대 주소 수 (W342), 0101/0102 한 요청의 최대 워드 수(쓰기 997, 읽기 999 중 작은 쪽)와 기본값(CP1L-E 쓰기 499)
    public const int MaxItems = 167, MaxWords = 997, DefaultWords = 499;
    // Host Link FINS(FA): MRC부터 요청 540바이트·응답 538바이트 안에 들어가는 최대 주소 수와 워드 수
    public const int SerialMaxItems = 134, SerialMaxWords = 266;

    public static byte[] Header(FinsNodes n, byte sid) => [IcfCommand, 0x00, Gct, n.Dna, n.Da1, n.Da2, n.Sna, n.Sa1, n.Sa2, sid];

    // Ethernet FINS 프레임 = 헤더 10바이트 + 본문
    public static byte[] Command(FinsNodes n, byte sid, ReadOnlySpan<byte> body) => [.. Header(n, sid), .. body];

    // 0101 MEMORY AREA READ: 01 01 | 주소 4 | 개수 2
    public static byte[] ReadBody(FinsAddress start, int count) => [MrcMemory, SrcRead, .. start.Encode(), (byte)(count >> 8), (byte)count];

    // 0102 MEMORY AREA WRITE: 01 02 | 주소 4 | 개수 2 | 데이터 (워드 2바이트, 비트 1바이트 00/01)
    public static byte[] WriteBody(FinsAddress start, ReadOnlySpan<long> values)
    {
        var b = new List<byte>(8 + values.Length * 2) { MrcMemory, SrcWrite };
        b.AddRange(start.Encode());
        b.Add((byte)(values.Length >> 8));
        b.Add((byte)values.Length);
        foreach (var v in values)
        {
            if (start.IsBit) b.Add((byte)v);
            else { b.Add((byte)(v >> 8)); b.Add((byte)v); }
        }
        return [.. b];
    }

    // 0104 MULTIPLE MEMORY AREA READ: 01 04 | 주소 4 × n
    public static byte[] MultiReadBody(IReadOnlyList<FinsAddress> items) => [MrcMemory, SrcMultiRead, .. items.SelectMany(a => a.Encode())];

    // 종료 코드에서 중계 오류 비트(MRES bit7)와 CPU 오류 플래그(SRES bit6 비치명, bit7 치명)를 뺀 값. 0이면 정상(0x0040 배터리 오류 등도 정상).
    public static int EndCode(byte mres, byte sres) => (mres & 0x7F) << 8 | (sres & 0x3F);

    // Ethernet FINS 프레임이 이 요청의 응답인지: ICF 응답 비트, SID, 응답의 출발지(SNA/SA1/SA2) = 요청의 목적지(DNA/DA1/DA2).
    // 요청 DA1이 0(자기 PLC)이면 PLC가 자기 노드 번호로 답하므로 SA1은 보지 않는다(CP1L 캡처).
    public static bool IsResponseTo(ReadOnlySpan<byte> frame, FinsNodes n, byte sid)
        => frame.Length >= HeaderLength + 2 && (frame[0] & 0x40) != 0 && frame[9] == sid
           && frame[6] == n.Dna && (n.Da1 == 0 || frame[7] == n.Da1) && frame[8] == n.Da2;

    // 작업 하나를 요청 조각들로 나눈다. Read = 0104 하나, Write = 0102 하나, ReadBlock/WriteBlock = maxPerRequest개씩 0101/0102.
    public static FinsChunk[] Chunks(FinsCommand command, FinsAddress[] items, int count, long[] values, int maxPerRequest)
    {
        switch (command)
        {
            case FinsCommand.Read:
                return [new FinsChunk(MultiReadBody(items), items, items.Length)];
            case FinsCommand.Write:
                return [new FinsChunk(WriteBody(items[0], values), [items[0]], 1)];
            default:
                var list = new List<FinsChunk>();
                for (int off = 0; off < count; off += maxPerRequest)
                {
                    int n = Math.Min(maxPerRequest, count - off);
                    var a = items[0].Offset(off);
                    var body = command == FinsCommand.ReadBlock ? ReadBody(a, n) : WriteBody(a, values.AsSpan(off, n));
                    list.Add(new FinsChunk(body, [a], n));
                }
                return [.. list];
        }
    }

    // 응답 본문(MRC부터) 해석. MRC/SRC가 요청과 다르거나 데이터 길이·영역 코드가 맞지 않으면 Invalid.
    public static FinsResponse ParseBody(ReadOnlySpan<byte> body, FinsChunk chunk)
    {
        if (body.Length < 4 || body[0] != chunk.Body[0] || body[1] != chunk.Body[1]) return FinsResponse.Invalid;
        int code = EndCode(body[2], body[3]);
        if (code != 0) return FinsResponse.Nak(code);
        int at = (body[2] & 0x80) != 0 ? 6 : 4;   // 중계 오류 비트가 있으면 오류 네트워크·노드 2바이트가 더 붙는다
        if (body.Length < at) return FinsResponse.Invalid;
        var data = body[at..];
        switch (chunk.Body[1])
        {
            case SrcWrite:
                return data.Length == 0 ? FinsResponse.Ack([]) : FinsResponse.Invalid;
            case SrcRead:
                {
                    int width = chunk.Items[0].Width;
                    if (data.Length != chunk.Count * width) return FinsResponse.Invalid;
                    var v = new long[chunk.Count];
                    for (int i = 0; i < v.Length; i++) v[i] = Decode(data.Slice(i * width, width));
                    return FinsResponse.Ack(v);
                }
            default:
                {
                    var v = new long[chunk.Items.Length];
                    int p = 0;
                    for (int i = 0; i < v.Length; i++)
                    {
                        var a = chunk.Items[i];
                        if (data.Length - p < 1 + a.Width || data[p] != a.Area) return FinsResponse.Invalid;
                        v[i] = Decode(data.Slice(p + 1, a.Width));
                        p += 1 + a.Width;
                    }
                    return p == data.Length ? FinsResponse.Ack(v) : FinsResponse.Invalid;
                }
        }
    }

    // 워드는 빅엔디언 0~65535, 비트는 bit0
    static long Decode(ReadOnlySpan<byte> d) => d.Length == 2 ? BinaryPrimitives.ReadUInt16BigEndian(d) : d[0] & 1;

    // 조각을 차례로 주고받는다. 모두 Ack면 값을 이어 붙인 Ack, 아니면 처음 실패한 결과(이후 조각은 보내지 않음).
    public static FinsResponse Run(IReadOnlyList<FinsChunk> chunks, Func<FinsChunk, FinsResponse> exchange)
    {
        var values = new List<long>();
        foreach (var c in chunks)
        {
            var r = exchange(c);
            if (r.Status != FinsStatus.Ack) return r;
            values.AddRange(r.Values);
        }
        return FinsResponse.Ack([.. values]);
    }
}
