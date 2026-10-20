using System.Text;
using Devinno.Communication.Mitsubishi.Data;

namespace Devinno.Communication.Mitsubishi.Serial;

// MC 프로토콜 1C 프레임 (ASCII, ACPU 공통 명령 BR/WR/BW/WW, 형식 1·4).
// 요청: ENQ 국번(2) PC번호 "FF" 명령(2) 메시지대기(1) 디바이스(5) 점수(2) [데이터] [섬체크(2)] [CR LF]
// 응답: 읽기 STX 국번 PC 데이터 ETX [섬체크] [CR LF] / 쓰기 ACK 국번 PC [CR LF] / 오류 NAK 국번 PC 에러코드(2) [CR LF]
// 섬체크 = ENQ/STX 다음 바이트부터 마지막 데이터(요청) 또는 ETX(응답)까지 합의 하위 바이트, 대문자 16진 2자.
internal static class McSerialFrame
{
    public const byte ENQ = 0x05, STX = 0x02, ETX = 0x03, ACK = 0x06, NAK = 0x15, CR = 0x0D, LF = 0x0A;

    public static byte Sum(ReadOnlySpan<byte> s)
    {
        int sum = 0;
        foreach (var b in s) sum += b;
        return (byte)sum;
    }

    // 디바이스 필드 5자: 1문자 코드 + 4자리, 2문자 코드 + 3자리. 번호는 디바이스의 진수로 쓴다.
    public static string DeviceField(McDevice d)
        => d.Code + Convert.ToString(d.Number, d.Radix).ToUpperInvariant().PadLeft(d.Code.Length == 1 ? 4 : 3, '0');

    static byte[] Finish(string body, McSerialFormat format, bool sumCheck, byte first)
    {
        var b = new List<byte> { first };
        var text = Encoding.ASCII.GetBytes(body);
        b.AddRange(text);
        if (sumCheck) b.AddRange(Encoding.ASCII.GetBytes(Sum(text).ToString("X2")));
        if (format == McSerialFormat.Format4) { b.Add(CR); b.Add(LF); }
        return [.. b];
    }

    // 요청 프레임. count: 점수(256이면 "00"), values: BW는 0/1, WW는 -32768~65535(16비트로 자름), 읽기는 null.
    public static byte[] Request(int station, McCommand command, int messageWait, McDevice device, int count, IReadOnlyList<long>? values, McSerialFormat format, bool sumCheck)
    {
        var sb = new StringBuilder()
            .Append(station.ToString("X2")).Append("FF").Append(command.ToString()).Append(messageWait.ToString("X1"))
            .Append(DeviceField(device)).Append((count & 0xFF).ToString("X2"));
        if (values != null)
            foreach (var v in values)
                sb.Append(command == McCommand.BW ? (v != 0 ? "1" : "0") : (v & 0xFFFF).ToString("X4"));
        return Finish(sb.ToString(), format, sumCheck, ENQ);
    }

    // 읽기 응답을 정상으로 받은 뒤 컴퓨터가 보내는 ACK (ACK 국번 "FF" [CR LF]). 섬체크 없음.
    public static byte[] HostAck(int station, McSerialFormat format)
        => Finish(station.ToString("X2") + "FF", format, false, ACK);

    // 수신 버퍼에서 응답 프레임을 찾는다. STX/ACK/NAK 앞의 바이트(잡음)는 건너뛴다.
    // 프레임이 다 오면 (시작 위치, 길이), 아직이면 길이 -1. ACK·NAK는 고정 길이(5·7, 형식 4는 +2), STX는 ETX [+섬체크] [+CR LF]까지.
    public static (int Start, int Length) Find(ReadOnlySpan<byte> buffer, McSerialFormat format, bool sumCheck)
    {
        int start = buffer.IndexOfAny(STX, ACK, NAK);
        if (start < 0) return (-1, -1);
        var b = buffer[start..];
        int tail = format == McSerialFormat.Format4 ? 2 : 0;
        int len;
        if (b[0] == ACK) len = 5 + tail;
        else if (b[0] == NAK) len = 7 + tail;
        else
        {
            int etx = b.IndexOf(ETX);
            if (etx < 0) return (start, -1);
            len = etx + 1 + (sumCheck ? 2 : 0) + tail;
        }
        return b.Length >= len ? (start, len) : (start, -1);
    }

    // 16진 ASCII를 값으로 (대소문자 무시). 16진 문자가 아니면 -1.
    static int Hex(ReadOnlySpan<byte> s)
    {
        int v = 0;
        foreach (var c in s)
        {
            int d = c >= '0' && c <= '9' ? c - '0' : c >= 'A' && c <= 'F' ? c - 'A' + 10 : c >= 'a' && c <= 'f' ? c - 'a' + 10 : -1;
            if (d < 0) return -1;
            v = v * 16 + d;
        }
        return v;
    }

    static bool Echo(ReadOnlySpan<byte> f, int station) => Hex(f.Slice(1, 2)) == station && Hex(f.Slice(3, 2)) == 0xFF;

    // 응답 해석. frame은 Find로 자른 한 프레임. 국번·PC번호 대조, 데이터 길이 검사, 섬체크 틀리면 SumError.
    public static McResponse Parse(ReadOnlySpan<byte> frame, McCommand command, int station, int count, McSerialFormat format, bool sumCheck)
    {
        int n = frame.Length;
        if (format == McSerialFormat.Format4)
        {
            if (n < 2 || frame[n - 2] != CR || frame[n - 1] != LF) return McResponse.Invalid;
            n -= 2;
        }
        var f = frame[..n];
        if (n < 5) return McResponse.Invalid;
        bool read = command is McCommand.BR or McCommand.WR;
        switch (f[0])
        {
            case NAK:
                {
                    int code = n == 7 ? Hex(f.Slice(5, 2)) : -1;
                    return code >= 0 && Echo(f, station) ? new(McStatus.Nak, [], code) : McResponse.Invalid;
                }
            case ACK:
                return n == 5 && !read && Echo(f, station) ? new(McStatus.Ack, [], 0) : McResponse.Invalid;
            case STX:
                {
                    int etx = sumCheck ? n - 3 : n - 1;
                    if (!read || etx < 5 || f[etx] != ETX) return McResponse.Invalid;
                    if (sumCheck && Hex(f.Slice(n - 2, 2)) != Sum(f[1..(etx + 1)])) return new(McStatus.SumError, [], 0);
                    if (!Echo(f, station)) return McResponse.Invalid;
                    var data = f[5..etx];
                    return command == McCommand.BR ? Bits(data, count) : Words(data, count);
                }
            default:
                return McResponse.Invalid;
        }
    }

    static McResponse Bits(ReadOnlySpan<byte> data, int count)
    {
        if (data.Length != count) return McResponse.Invalid;
        var r = new long[count];
        for (int i = 0; i < count; i++)
        {
            if (data[i] is not ((byte)'0' or (byte)'1')) return McResponse.Invalid;
            r[i] = data[i] - '0';
        }
        return new(McStatus.Ack, r, 0);
    }

    static McResponse Words(ReadOnlySpan<byte> data, int count)
    {
        if (data.Length != count * 4) return McResponse.Invalid;
        var r = new long[count];
        for (int i = 0; i < count; i++)
        {
            int v = Hex(data.Slice(i * 4, 4));
            if (v < 0) return McResponse.Invalid;
            r[i] = v;
        }
        return new(McStatus.Ack, r, 0);
    }
}
