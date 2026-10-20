using System.Globalization;
using System.Text;
using Devinno.Communication.LS.Data;

namespace Devinno.Communication.LS.CNet;

// Cnet XGT 전용 프로토콜 프레임 (ASCII, 소문자 명령 + BCC).
// 요청: ENQ 국번(2) 명령(r/w) 타입(SS/SB) 본문 EOT BCC(2)
// 응답: ACK|NAK 국번 명령 타입 본문 ETX BCC(2). BCC = 첫 바이트부터 EOT/ETX까지 합의 하위 바이트(16진 2자).
internal static class CNetFrame
{
    public const byte ENQ = 0x05, EOT = 0x04, ACK = 0x06, NAK = 0x15, ETX = 0x03;
    public const int MaxBlocks = LsDevice.MaxSingles;
    public const int MaxBlockBytes = 120;

    public static byte Bcc(ReadOnlySpan<byte> s)
    {
        int sum = 0;
        foreach (var b in s) sum += b;
        return (byte)sum;
    }

    static StringBuilder Start(int station, char cmd, string type)
        => new StringBuilder().Append((char)ENQ).Append(station.ToString("X2")).Append(cmd).Append(type);

    static StringBuilder Name(StringBuilder sb, LsDevice d) => sb.Append(d.Name.Length.ToString("X2")).Append(d.Name);

    static string Hex(long value, int width)
        => width == 8 ? ((ulong)value).ToString("X16") : ((ulong)value & ((1UL << (width * 8)) - 1)).ToString("X" + width * 2);

    static byte[] Finish(StringBuilder sb)
    {
        sb.Append((char)EOT);
        var body = Encoding.ASCII.GetBytes(sb.ToString());
        return [.. body, .. Encoding.ASCII.GetBytes(Bcc(body).ToString("X2"))];
    }

    public static byte[] RSS(int station, IReadOnlyList<LsDevice> devices)
    {
        var sb = Start(station, 'r', "SS").Append(devices.Count.ToString("X2"));
        foreach (var d in devices) Name(sb, d);
        return Finish(sb);
    }

    public static byte[] RSB(int station, LsDevice device, int count)
        => Finish(Name(Start(station, 'r', "SB"), device).Append(count.ToString("X2")));

    public static byte[] WSS(int station, IReadOnlyList<(LsDevice Device, long Value)> values)
    {
        var sb = Start(station, 'w', "SS").Append(values.Count.ToString("X2"));
        foreach (var (d, v) in values) Name(sb, d).Append(Hex(v, d.Width));
        return Finish(sb);
    }

    public static byte[] WSB(int station, LsDevice device, IReadOnlyList<long> values)
    {
        var sb = Name(Start(station, 'w', "SB"), device).Append(values.Count.ToString("X2"));
        foreach (var v in values) sb.Append(Hex(v, device.Width));
        return Finish(sb);
    }

    // 수신 버퍼에서 응답 프레임 전체 길이(ETX + BCC 2). 아직 끝나지 않았으면 -1.
    // ASCII 본문에는 0x03이 나올 수 없으므로 첫 ETX가 프레임 끝이다.
    public static int FrameLength(ReadOnlySpan<byte> buffer)
    {
        int i = buffer.IndexOf(ETX);
        return i >= 0 && buffer.Length >= i + 3 ? i + 3 : -1;
    }

    // 응답 해석. request: 보낸 요청(국번·명령·타입 대조), width: 값 하나의 바이트 수, count: RSS는 블록 수, RSB는 디바이스 수.
    public static LsResponse Parse(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> request, int width, int count)
    {
        int n = frame.Length;
        if (n < 9 || frame[n - 3] != ETX) return LsResponse.Invalid;
        try
        {
            var bcc = byte.Parse(Encoding.ASCII.GetString(frame[(n - 2)..]), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (bcc != Bcc(frame[..(n - 2)])) return new(LsStatus.BccError, [], 0);

            // 국번(2) + 명령(1) + 타입(2)이 요청과 같아야 한다
            if (!frame.Slice(1, 5).SequenceEqual(request.Slice(1, 5))) return LsResponse.Invalid;
            var body = Encoding.ASCII.GetString(frame[6..(n - 3)]);

            if (frame[0] == NAK)
                return body.Length == 4 ? new(LsStatus.Nak, [], int.Parse(body, NumberStyles.HexNumber, CultureInfo.InvariantCulture)) : LsResponse.Invalid;
            if (frame[0] != ACK) return LsResponse.Invalid;

            if (request[3] == 'w') return body.Length == 0 ? new(LsStatus.Ack, [], 0) : LsResponse.Invalid;
            return request[5] == 'B' ? ParseBlock(body, width, count) : ParseSingles(body, width, count);
        }
        catch (FormatException) { return LsResponse.Invalid; }
        catch (OverflowException) { return LsResponse.Invalid; }
    }

    static int Hex2(string s, int at) => int.Parse(s.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    static long HexValue(string s, int at, int chars) => long.Parse(s.AsSpan(at, chars), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    static LsResponse ParseSingles(string body, int width, int count)
    {
        if (body.Length < 2 || Hex2(body, 0) != count) return LsResponse.Invalid;
        var values = new long[count];
        int pos = 2;
        for (int i = 0; i < count; i++)
        {
            if (pos + 2 + width * 2 > body.Length || Hex2(body, pos) != width) return LsResponse.Invalid;
            values[i] = HexValue(body, pos + 2, width * 2);
            pos += 2 + width * 2;
        }
        return pos == body.Length ? new(LsStatus.Ack, values, 0) : LsResponse.Invalid;
    }

    static LsResponse ParseBlock(string body, int width, int count)
    {
        if (body.Length != 4 + width * count * 2 || Hex2(body, 0) != 1 || Hex2(body, 2) != width * count) return LsResponse.Invalid;
        var values = new long[count];
        for (int i = 0; i < count; i++) values[i] = HexValue(body, 4 + i * width * 2, width * 2);
        return new(LsStatus.Ack, values, 0);
    }
}
