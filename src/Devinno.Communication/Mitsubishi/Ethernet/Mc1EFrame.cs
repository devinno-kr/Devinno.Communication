using Devinno.Communication.Mitsubishi.Data;

namespace Devinno.Communication.Mitsubishi.Ethernet;

// MC 프로토콜 1E 프레임 (바이너리, A 호환 / FX3 이더넷). 다바이트 값은 리틀엔디언.
// 요청: 서브헤더(00 BR, 01 WR, 02 BW, 03 WW) | PC FF | 감시타이머(2) | 시작 번호(4) | 코드(2) | 점수(1, 00 = 256) | 00 | [데이터]
// 응답: (서브헤더 | 80H) | 종료코드 | [데이터]. 길이 필드가 없어 명령과 점수로 길이를 계산한다.
// 종료코드 5BH면 이상 코드 1바이트가 붙고, FX3는 그 뒤에 00H가 하나 더 온다(3바이트·4바이트 모두 허용).
internal static class Mc1EFrame
{
    public const byte AbnormalEndCode = 0x5B;

    public static byte Subheader(McCommand command) => command switch
    {
        McCommand.BR => 0x00,
        McCommand.WR => 0x01,
        McCommand.BW => 0x02,
        _ => 0x03,
    };

    public static byte[] Request(McCommand command, McDevice device, int count, IReadOnlyList<long>? values, int timer)
    {
        var r = new List<byte>
        {
            Subheader(command), 0xFF, (byte)timer, (byte)(timer >> 8),
            (byte)device.Number, (byte)(device.Number >> 8), (byte)(device.Number >> 16), (byte)(device.Number >> 24),
            (byte)device.WireCode, (byte)(device.WireCode >> 8),
            (byte)(count == 256 ? 0 : count), 0x00,
        };
        r.AddRange(McDevice.EncodeBinary(command, values));
        return [.. r];
    }

    // 정상 응답(종료코드 00)에서 헤더 2바이트 뒤 데이터 바이트 수
    public static int DataLength(McCommand command, int count) => command switch
    {
        McCommand.BR => (count + 1) / 2,
        McCommand.WR => count * 2,
        _ => 0,
    };

    // 응답 프레임 전체 해석. 5BH 오류는 ErrorCode = 0x5B00 | 이상 코드, 그 밖의 오류는 ErrorCode = 종료코드.
    public static McResponse Parse(ReadOnlySpan<byte> frame, McCommand command, int count)
    {
        if (frame.Length < 2 || frame[0] != (Subheader(command) | 0x80)) return McResponse.Invalid;
        int end = frame[1];
        if (end == AbnormalEndCode)
            return frame.Length == 3 || (frame.Length == 4 && frame[3] == 0) ? new(McStatus.Nak, [], end << 8 | frame[2]) : McResponse.Invalid;
        if (end != 0) return frame.Length == 2 ? new(McStatus.Nak, [], end) : McResponse.Invalid;
        return McDevice.DecodeBinary(command, frame[2..], count);
    }
}
