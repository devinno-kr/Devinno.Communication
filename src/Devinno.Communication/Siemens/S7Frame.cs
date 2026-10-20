using System.Buffers.Binary;

namespace Devinno.Communication.Siemens;

// S7 헤더 해석 결과. Error = 오류 등급 × 256 + 오류 코드 (Ack/Ack_Data만, Job은 0)
internal readonly record struct S7Header(byte Rosctr, ushort PduRef, int Error, int ParamOffset, int ParamLength, int DataOffset, int DataLength);

// ISO-on-TCP(RFC 1006) 프레임 = TPKT(4) + COTP + S7 PDU. 생성과 해석은 모두 여기서 한다.
internal static class S7Frame
{
    public const int JobHeader = 10, AckHeader = 12;
    // 모든 CPU가 받아들이는 최소 PDU 크기. 개별 읽기/쓰기는 등록할 때 이 크기에 맞는지 검사한다.
    public const int MinPdu = 240;
    public const byte RosctrJob = 0x01, RosctrAck = 0x02, RosctrAckData = 0x03;
    public const byte FuncSetup = 0xF0, FuncRead = 0x04, FuncWrite = 0x05;
    // 데이터 항목의 전송 크기: 03 BIT·04 BYTE/WORD/DWORD·05 INT는 길이가 비트 수, 07 REAL·09 OCTET STRING은 바이트 수
    public const byte DataBit = 0x03, DataByte = 0x04, DataInt = 0x05, DataReal = 0x07, DataOctet = 0x09;

    // 연속 읽기 한 요청의 최대 바이트 = PDU − 18 (응답 헤더 12 + 파라미터 2 + 데이터 항목 머리 4)
    public static int ReadChunk(int pdu) => pdu - 18;
    // 연속 쓰기 한 요청의 최대 바이트 = PDU − 28 (요청 헤더 10 + 파라미터 2 + 요청 항목 12 + 데이터 항목 머리 4)
    public static int WriteChunk(int pdu) => pdu - 28;

    static int BE16(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt16BigEndian(s[at..]);

    #region COTP 연결
    // COTP CR(연결 요청). 원본 참조 0x0001, 클래스 0, 호출(로컬) TSAP, 피호출(PLC) TSAP, TPDU 크기 1024.
    public static byte[] ConnectionRequest(int localTsap, int remoteTsap) =>
    [
        0x03, 0x00, 0x00, 0x16, 0x11, 0xE0, 0x00, 0x00, 0x00, 0x01, 0x00,
        0xC1, 0x02, (byte)(localTsap >> 8), (byte)localTsap,
        0xC2, 0x02, (byte)(remoteTsap >> 8), (byte)remoteTsap,
        0xC0, 0x01, 0x0A,
    ];

    // TPKT 뒤의 COTP가 CC(연결 확인)이고 파라미터가 올바른 TLV인지. 파라미터 순서는 따지지 않는다(실장비는 C0을 먼저 보냄).
    public static bool IsConnectionConfirm(ReadOnlySpan<byte> cotp)
    {
        if (cotp.Length < 7 || cotp[0] != cotp.Length - 1 || (cotp[1] & 0xF0) != 0xD0) return false;
        int p = 7;
        while (p < cotp.Length)
        {
            if (cotp.Length - p < 2 || cotp.Length - p - 2 < cotp[p + 1]) return false;
            p += 2 + cotp[p + 1];
        }
        return true;
    }
    #endregion

    #region TPKT / COTP DT
    // TPKT 프레임 하나의 내용(COTP부터)을 읽는다. readExact가 null(시간 초과)이면 null.
    public static byte[]? ReadTpkt(Func<int, byte[]?> readExact)
    {
        var head = readExact(4);
        if (head == null) return null;
        int len = BE16(head, 2);
        if (head[0] != 0x03 || head[1] != 0x00 || len < 7) throw new IOException("TPKT 헤더가 올바르지 않습니다.");
        return readExact(len - 4);
    }

    // COTP DT 조각을 EOT(마지막 조각 표시)까지 이어 붙인 S7 PDU를 읽는다. 빈 조각도 받는다. 시간 초과면 null.
    // DT가 아니거나 TPKT가 깨졌으면 IOException (스트림 정렬이 깨짐 → 재접속).
    public static byte[]? ReadPdu(Func<int, byte[]?> readExact)
    {
        var pdu = new List<byte>();
        while (true)
        {
            var tpdu = ReadTpkt(readExact);
            if (tpdu == null) return null;
            if (tpdu.Length < 3 || tpdu[0] != 0x02 || tpdu[1] != 0xF0) throw new IOException("COTP 데이터(DT) TPDU가 아닙니다.");
            pdu.AddRange(tpdu.AsSpan(3));
            if (pdu.Count > ushort.MaxValue) throw new IOException("S7 PDU가 너무 큽니다.");
            if ((tpdu[2] & 0x80) != 0) return [.. pdu];
        }
    }
    #endregion

    #region S7 요청
    // S7 Job 프레임 전체 (TPKT + COTP DT(EOT) + 헤더 10바이트 + 파라미터 + 데이터)
    public static byte[] Job(ushort pduRef, ReadOnlySpan<byte> param, ReadOnlySpan<byte> data)
    {
        int len = 4 + 3 + JobHeader + param.Length + data.Length;
        var f = new byte[len];
        f[0] = 0x03;
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(2), (ushort)len);
        f[4] = 0x02; f[5] = 0xF0; f[6] = 0x80;
        f[7] = 0x32; f[8] = RosctrJob;
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(11), pduRef);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(13), (ushort)param.Length);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(15), (ushort)data.Length);
        param.CopyTo(f.AsSpan(17));
        data.CopyTo(f.AsSpan(17 + param.Length));
        return f;
    }

    // Setup Communication 파라미터: 동시 작업 수(AmQ) 1/1, 요청 PDU 크기
    public static byte[] SetupParam(int pduSize) => [FuncSetup, 0x00, 0x00, 0x01, 0x00, 0x01, (byte)(pduSize >> 8), (byte)pduSize];

    static void WriteItem(Span<byte> d, S7Item it)
    {
        d[0] = 0x12; d[1] = 0x0A; d[2] = 0x10; d[3] = it.TransportSize;
        BinaryPrimitives.WriteUInt16BigEndian(d[4..], (ushort)it.Count);
        BinaryPrimitives.WriteUInt16BigEndian(d[6..], (ushort)it.Db);
        d[8] = it.Area;
        d[9] = (byte)(it.BitAddress >> 16); d[10] = (byte)(it.BitAddress >> 8); d[11] = (byte)it.BitAddress;
    }

    // Read Var 파라미터: 04 개수 + 항목 12바이트씩
    public static byte[] ReadParam(IReadOnlyList<S7Item> items)
    {
        var p = new byte[2 + 12 * items.Count];
        p[0] = FuncRead; p[1] = (byte)items.Count;
        for (int i = 0; i < items.Count; i++) WriteItem(p.AsSpan(2 + 12 * i), items[i]);
        return p;
    }

    // Write Var 파라미터와 데이터. 데이터 항목 = 00 + 전송 크기(비트 03 / 바이트 04) + 길이(비트 수) + 값,
    // 값이 홀수 바이트면 마지막 항목만 빼고 채움 바이트 00.
    public static (byte[] Param, byte[] Data) WriteParam(IReadOnlyList<(S7Item Item, byte[] Data)> items)
    {
        var param = ReadParam([.. items.Select(x => x.Item)]);
        param[0] = FuncWrite;
        var data = new List<byte>();
        for (int i = 0; i < items.Count; i++)
        {
            var (it, d) = items[i];
            bool bit = it.TransportSize == S7Item.TsBit;
            int bits = bit ? 1 : d.Length * 8;
            data.AddRange([0x00, bit ? DataBit : DataByte, (byte)(bits >> 8), (byte)bits]);
            data.AddRange(d);
            if (d.Length % 2 == 1 && i < items.Count - 1) data.Add(0x00);
        }
        return (param, [.. data]);
    }

    static int DataSize(IReadOnlyList<int> lengths)
    {
        int s = 0;
        for (int i = 0; i < lengths.Count; i++) s += 4 + lengths[i] + (lengths[i] % 2 == 1 && i < lengths.Count - 1 ? 1 : 0);
        return s;
    }

    // 개별 읽기 요청과 응답이 최소 PDU(240)에 들어가는지. lengths = 항목별 데이터 바이트 수(비트는 1).
    public static bool ReadFits(IReadOnlyList<int> lengths)
        => JobHeader + 2 + 12 * lengths.Count <= MinPdu && AckHeader + 2 + DataSize(lengths) <= MinPdu;

    // 개별 쓰기 요청이 최소 PDU(240)에 들어가는지 (응답은 항목마다 1바이트라 항상 들어감)
    public static bool WriteFits(IReadOnlyList<int> lengths)
        => JobHeader + 2 + 12 * lengths.Count + DataSize(lengths) <= MinPdu;
    #endregion

    #region S7 응답
    // S7 PDU 헤더. 프로토콜 ID가 0x32가 아니거나 길이 필드가 PDU 길이와 맞지 않으면 null.
    public static S7Header? ParseHeader(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length < JobHeader || pdu[0] != 0x32) return null;
        byte rosctr = pdu[1];
        int hl = rosctr is RosctrAck or RosctrAckData ? AckHeader : JobHeader;
        if (pdu.Length < hl) return null;
        int pl = BE16(pdu, 6), dl = BE16(pdu, 8);
        if (hl + pl + dl != pdu.Length) return null;
        int error = hl == AckHeader ? pdu[10] << 8 | pdu[11] : 0;
        return new S7Header(rosctr, (ushort)BE16(pdu, 4), error, hl, pl, hl + pl, dl);
    }

    // Setup Communication 응답의 PDU 크기. 오류 응답이거나 형식이 맞지 않으면 null.
    public static int? ParseSetup(ReadOnlySpan<byte> pdu)
    {
        if (ParseHeader(pdu) is not { } h || h.Rosctr != RosctrAckData || h.Error != 0 || h.ParamLength != 8 || pdu[h.ParamOffset] != FuncSetup) return null;
        return BE16(pdu, h.ParamOffset + 6);
    }

    // Ack/Ack_Data 공통 검사. 헤더 오류(등급·코드가 0이 아님)면 Nak(코드, -1), 기능·항목 수가 다르면 Invalid, 정상이면 null.
    static S7Response? CheckAck(ReadOnlySpan<byte> pdu, byte func, int count, out S7Header header)
    {
        header = default;
        if (ParseHeader(pdu) is not { } h || h.Rosctr is not (RosctrAck or RosctrAckData)) return S7Response.Invalid;
        header = h;
        if (h.Error != 0) return S7Response.Nak(h.Error, -1);
        if (h.Rosctr != RosctrAckData || h.ParamLength != 2 || pdu[h.ParamOffset] != func || pdu[h.ParamOffset + 1] != count) return S7Response.Invalid;
        return null;
    }

    // Read Var 응답. lengths = 항목마다 기대하는 데이터 바이트 수(비트는 1).
    // 실패 항목(반환 코드 ≠ FF)은 길이 필드와 상관없이 4바이트(실장비 rc 00 00 00, Snap7 rc 00 00 04)이고, 첫 실패 항목으로 Nak.
    public static S7Response ParseRead(ReadOnlySpan<byte> pdu, IReadOnlyList<int> lengths)
    {
        if (CheckAck(pdu, FuncRead, lengths.Count, out var h) is { } early) return early;
        var items = new byte[lengths.Count][];
        S7Response? nak = null;
        int p = h.DataOffset, end = h.DataOffset + h.DataLength;
        for (int i = 0; i < lengths.Count; i++)
        {
            if (end - p < 4) return S7Response.Invalid;
            if (pdu[p] != 0xFF)
            {
                nak ??= S7Response.Nak(pdu[p], i);
                p += 4;
                continue;
            }
            int len = BE16(pdu, p + 2);
            int n = pdu[p + 1] switch
            {
                DataBit or DataByte or DataInt => (len + 7) / 8,
                DataReal or DataOctet => len,
                _ => -1,
            };
            if (n != lengths[i] || end - p - 4 < n) return S7Response.Invalid;
            items[i] = pdu.Slice(p + 4, n).ToArray();
            p += 4 + n;
            if (n % 2 == 1 && i < lengths.Count - 1) p++;   // 채움 바이트 (마지막 항목 뒤에는 없음)
        }
        if (end - p is not (0 or 1)) return S7Response.Invalid;   // 마지막 항목 뒤 채움 바이트 1개까지는 허용
        return nak ?? S7Response.Ack(items);
    }

    // Write Var 응답: 항목마다 반환 코드 1바이트. 첫 실패 항목으로 Nak.
    public static S7Response ParseWrite(ReadOnlySpan<byte> pdu, int count)
    {
        if (CheckAck(pdu, FuncWrite, count, out var h) is { } early) return early;
        if (h.DataLength != count) return S7Response.Invalid;
        for (int i = 0; i < count; i++)
            if (pdu[h.DataOffset + i] != 0xFF) return S7Response.Nak(pdu[h.DataOffset + i], i);
        return S7Response.Ack([]);
    }
    #endregion
}
