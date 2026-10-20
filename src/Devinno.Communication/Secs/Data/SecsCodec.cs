using System.Buffers.Binary;

namespace Devinno.Communication.Secs.Data;

// SECS-II 아이템 ↔ 바이트 (HSMS·SECS-I 공유). 숫자는 빅엔디언.
// 아이템 = 포맷 바이트((코드 &lt;&lt; 2) | 길이 바이트 수 1~3) + 길이(빅엔디언, 리스트는 자식 수·그 밖은 바이트 수) + 데이터.
internal static class SecsCodec
{
    // 리스트 중첩 한계. 악의적이거나 깨진 본문의 깊은 중첩으로 스택이 넘치지 않게 한다.
    public const int MaxDepth = 64;

    #region Encode
    // 본문 바이트. item이 null이면 빈 배열(헤더만 있는 메시지).
    public static byte[] Encode(SecsItem? item)
    {
        if (item == null) return [];
        var buf = new byte[EncodedLength(item)];
        int n = Write(item, buf);
        return n == buf.Length ? buf : throw new InvalidOperationException("인코딩 길이 계산 오류");
    }

    // 인코딩한 바이트 수(인코딩하지 않고 셈. 진단 SML의 "본문 M바이트"도 씀)
    internal static long EncodedLength(SecsItem item)
    {
        long len = item.Format == SecsFormat.List ? item.Count : (long)item.Count * SecsItem.ElementSize(item.Format);
        long total = 1 + LengthBytes(len) + (item.Format == SecsFormat.List ? 0 : len);
        if (item.Format == SecsFormat.List)
        {
            var children = item.Items;
            for (int i = 0; i < children.Count; i++) total += EncodedLength(children[i]);
        }
        return total;
    }

    static int LengthBytes(long length) => length > 0xFFFF ? 3 : length > 0xFF ? 2 : 1;

    static int Write(SecsItem item, Span<byte> dest)
    {
        int len = item.Format == SecsFormat.List ? item.Count : item.Count * SecsItem.ElementSize(item.Format);
        int nb = LengthBytes(len);
        dest[0] = (byte)(((int)item.Format << 2) | nb);
        for (int i = 0; i < nb; i++) dest[1 + i] = (byte)(len >> (8 * (nb - 1 - i)));
        int p = 1 + nb;
        if (item.Format == SecsFormat.List)
        {
            foreach (var c in item.Items) p += Write(c, dest[p..]);
            return p;
        }
        var d = dest.Slice(p, len);
        switch (item.RawValues)
        {
            case byte[] b: b.CopyTo(d); break;
            case bool[] b: for (int i = 0; i < b.Length; i++) d[i] = b[i] ? (byte)1 : (byte)0; break;
            case sbyte[] v: for (int i = 0; i < v.Length; i++) d[i] = (byte)v[i]; break;
            case short[] v: for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteInt16BigEndian(d[(i * 2)..], v[i]); break;
            case int[] v: for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteInt32BigEndian(d[(i * 4)..], v[i]); break;
            case long[] v: for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteInt64BigEndian(d[(i * 8)..], v[i]); break;
            case ushort[] v: for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(d[(i * 2)..], v[i]); break;
            case uint[] v: for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(d[(i * 4)..], v[i]); break;
            case ulong[] v: for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt64BigEndian(d[(i * 8)..], v[i]); break;
            case float[] v: for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteSingleBigEndian(d[(i * 4)..], v[i]); break;
            case double[] v: for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteDoubleBigEndian(d[(i * 8)..], v[i]); break;
        }
        return p + len;
    }
    #endregion

    #region Decode
    // 본문 전체를 아이템 하나로 해석한다. 빈 본문이면 null. 형식이 틀리거나 뒤에 남는 바이트가 있으면 FormatException.
    public static SecsItem? Decode(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty) return null;
        int p = 0;
        var item = Read(body, ref p, 0);
        if (p != body.Length) throw new FormatException($"아이템 뒤에 바이트 {body.Length - p}개가 남았습니다 (위치 {p}).");
        return item;
    }

    static SecsItem Read(ReadOnlySpan<byte> s, ref int p, int depth)
    {
        int start = p;
        if (p >= s.Length) throw new FormatException($"아이템 머리가 없습니다 (위치 {p}).");
        byte fb = s[p++];
        int nb = fb & 0x03;
        if (nb == 0) throw new FormatException($"길이 바이트 수가 0입니다 (위치 {start}, 포맷 바이트 0x{fb:X2}).");
        if (!Enum.IsDefined(typeof(SecsFormat), fb >> 2)) throw new FormatException($"모르는 포맷 코드 8진수 {Convert.ToString(fb >> 2, 8)} (위치 {start}).");
        var format = (SecsFormat)(fb >> 2);
        if (p + nb > s.Length) throw new FormatException($"길이 바이트가 모자랍니다 (위치 {start}).");
        int len = 0;
        for (int i = 0; i < nb; i++) len = (len << 8) | s[p++];

        if (format == SecsFormat.List)
        {
            if (depth >= MaxDepth) throw new FormatException($"리스트 중첩이 {MaxDepth}단계를 넘습니다 (위치 {start}).");
            // 자식 하나는 최소 2바이트이므로 남은 바이트로 불가능한 개수는 바로 거부한다(큰 배열 할당 방지)
            if (len > (s.Length - p) / 2) throw new FormatException($"리스트 자식 {len}개가 남은 바이트보다 많습니다 (위치 {start}).");
            var items = new SecsItem[len];
            for (int i = 0; i < len; i++) items[i] = Read(s, ref p, depth + 1);
            return SecsItem.RawList(items);
        }

        if (p + len > s.Length) throw new FormatException($"{format} 데이터 {len}바이트가 모자랍니다 (위치 {start}).");
        int size = SecsItem.ElementSize(format);
        if (len % size != 0) throw new FormatException($"{format} 데이터 길이 {len}이(가) {size}의 배수가 아닙니다 (위치 {start}).");
        var d = s.Slice(p, len);
        p += len;
        int n = len / size;
        Array values = format switch
        {
            SecsFormat.Boolean => Map(d, n, static (x, i) => x[i] != 0),
            SecsFormat.I1 => Map(d, n, static (x, i) => (sbyte)x[i]),
            SecsFormat.I2 => Map(d, n, static (x, i) => BinaryPrimitives.ReadInt16BigEndian(x[(i * 2)..])),
            SecsFormat.I4 => Map(d, n, static (x, i) => BinaryPrimitives.ReadInt32BigEndian(x[(i * 4)..])),
            SecsFormat.I8 => Map(d, n, static (x, i) => BinaryPrimitives.ReadInt64BigEndian(x[(i * 8)..])),
            SecsFormat.U2 => Map(d, n, static (x, i) => BinaryPrimitives.ReadUInt16BigEndian(x[(i * 2)..])),
            SecsFormat.U4 => Map(d, n, static (x, i) => BinaryPrimitives.ReadUInt32BigEndian(x[(i * 4)..])),
            SecsFormat.U8 => Map(d, n, static (x, i) => BinaryPrimitives.ReadUInt64BigEndian(x[(i * 8)..])),
            SecsFormat.F4 => Map(d, n, static (x, i) => BinaryPrimitives.ReadSingleBigEndian(x[(i * 4)..])),
            SecsFormat.F8 => Map(d, n, static (x, i) => BinaryPrimitives.ReadDoubleBigEndian(x[(i * 8)..])),
            _ => d.ToArray(),   // B, A, J, C2, U1
        };
        return SecsItem.Raw(format, values);
    }

    delegate T SpanReader<T>(ReadOnlySpan<byte> s, int index);

    static T[] Map<T>(ReadOnlySpan<byte> d, int count, SpanReader<T> f)
    {
        var a = new T[count];
        for (int i = 0; i < count; i++) a[i] = f(d, i);
        return a;
    }
    #endregion
}
