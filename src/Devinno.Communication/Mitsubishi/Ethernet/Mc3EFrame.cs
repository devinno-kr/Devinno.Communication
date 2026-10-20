using Devinno.Communication.Mitsubishi.Data;

namespace Devinno.Communication.Mitsubishi.Ethernet;

// MC 프로토콜 3E 프레임 (바이너리, Q/L 서브커맨드 0000 워드 / 0001 비트). 다바이트 값은 리틀엔디언.
// 요청: 50 00 | 네트워크 00 | PC FF | I/O FF 03 | 국번 00 | 길이(2) | 감시타이머(2) | 명령(2) | 서브커맨드(2) | 번호(3) 코드(1) | 점수(2) | [데이터]
// 응답: D0 00 | 같은 경로(5) | 길이(2) | 종료코드(2) | [데이터 또는 오류 정보]. 길이 = 감시타이머(요청) / 종료코드(응답)부터 끝까지.
internal static class Mc3EFrame
{
    // 응답 헤더(서브헤더 + 경로 + 길이) 바이트 수
    public const int ResponseHeaderLength = 9;
    static readonly byte[] Route = [0x00, 0xFF, 0xFF, 0x03, 0x00];

    public static byte[] Request(McCommand command, McDevice device, int count, IReadOnlyList<long>? values, int timer)
    {
        var data = new List<byte>();
        Put16(data, timer);
        Put16(data, command is McCommand.BR or McCommand.WR ? 0x0401 : 0x1401);
        Put16(data, command is McCommand.BR or McCommand.BW ? 0x0001 : 0x0000);
        data.Add((byte)device.Number);
        data.Add((byte)(device.Number >> 8));
        data.Add((byte)(device.Number >> 16));
        data.Add((byte)device.WireCode);
        Put16(data, count);
        data.AddRange(McDevice.EncodeBinary(command, values));

        var r = new List<byte> { 0x50, 0x00 };
        r.AddRange(Route);
        Put16(r, data.Count);
        r.AddRange(data);
        return [.. r];
    }

    static void Put16(List<byte> b, int v) { b.Add((byte)v); b.Add((byte)(v >> 8)); }

    // 응답 헤더 9바이트에서 뒤따르는 바이트 수. 서브헤더(D0 00)나 경로가 다르면 -1 (스트림이 어긋남).
    public static int BodyLength(ReadOnlySpan<byte> header)
    {
        if (header.Length != ResponseHeaderLength || header[0] != 0xD0 || header[1] != 0x00 || !header.Slice(2, 5).SequenceEqual(Route)) return -1;
        return header[7] | header[8] << 8;
    }

    // 응답 본문(종료코드 + 데이터) 해석. 종료코드가 0이 아니면 Nak(ErrorCode = 종료코드).
    public static McResponse Parse(ReadOnlySpan<byte> body, McCommand command, int count)
    {
        if (body.Length < 2) return McResponse.Invalid;
        int end = body[0] | body[1] << 8;
        if (end != 0) return new(McStatus.Nak, [], end);
        return McDevice.DecodeBinary(command, body[2..], count);
    }
}
