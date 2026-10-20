using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Devinno.Communication.TextComm;

// 프레임 하나를 찾은 결과
internal enum TextCommStatus
{
    // 프레임이 아직 끝나지 않음(더 받아야 함)
    Incomplete,
    // 정상 프레임
    Ok,
    // 형식은 맞지만 합계 바이트가 틀림
    SumError,
    // 깨진 프레임(잘못된 DLE 조합, 너무 짧음, 너무 긺)
    Invalid,
    // 시간 안에 맞는 응답을 받지 못함 (Receive만 돌려준다)
    Timeout,
}

// 찾은 프레임. Consumed = 버퍼 앞에서 버릴 바이트 수. Slave/Command는 Ok·SumError일 때만 의미가 있고, Message는 Ok일 때만 채워진다.
internal readonly record struct TextCommFrame(TextCommStatus Status, int Consumed, byte Slave, byte Command, byte[] Message);

// TextComm 프레임: STX | DLE 이스케이프(slave, command, message…, sum) | ETX.
// sum = slave + command + message 바이트 합의 하위 바이트(이스케이프 전). 0x02/0x03/0x10은 0x10, 값 + 0x10으로 바꾼다.
internal static class TextCommPacket
{
    public const byte STX = 0x02;
    public const byte ETX = 0x03;
    public const byte DLE = 0x10;

    // Slave가 받는 요청 프레임(STX~ETX)의 최대 길이. 넘는 프레임은 버린다.
    public const int MaxRequestLength = 8192;

    // 합계 바이트: 모든 바이트 합의 하위 8비트
    public static byte Sum(ReadOnlySpan<byte> data)
    {
        int sum = 0;
        foreach (var b in data) sum += b;
        return (byte)sum;
    }

    // 프레임을 만든다
    public static byte[] Build(byte slave, byte command, ReadOnlySpan<byte> message)
    {
        var raw = new List<byte>(message.Length + 3) { slave, command };
        foreach (var b in message) raw.Add(b);
        raw.Add(Sum(CollectionsMarshal.AsSpan(raw)));

        var frame = new List<byte>(raw.Count * 2 + 2) { STX };
        foreach (var b in raw)
        {
            if (b is STX or ETX or DLE) { frame.Add(DLE); frame.Add((byte)(b + 0x10)); }
            else frame.Add(b);
        }
        frame.Add(ETX);
        return [.. frame];
    }

    // Master가 보낼 요청 프레임을 만든다. slave·command는 0~255, message는 null 불가(빈 문자열 가능).
    public static byte[] BuildRequest(Encoding encoding, int slave, int command, string message)
    {
        if (slave < 0 || slave > 255) throw new ArgumentOutOfRangeException(nameof(slave), "slave는 0~255 입니다.");
        if (command < 0 || command > 255) throw new ArgumentOutOfRangeException(nameof(command), "command는 0~255 입니다.");
        ArgumentNullException.ThrowIfNull(message);
        return Build((byte)slave, (byte)command, encoding.GetBytes(message));
    }

    // Slave의 응답 프레임을 만든다. response가 null이면 응답하지 않고(null), ""이면 메시지 없는 프레임, 그 밖이면 그 메시지로 만든다.
    // 인코딩이 문자열을 바이트로 바꾸지 못하면 ArgumentException(EncoderFallbackException 포함)을 그대로 던진다.
    public static byte[]? BuildResponse(Encoding encoding, int slave, int command, string? response)
        => response == null ? null : Build((byte)slave, (byte)command, encoding.GetBytes(response));

    // 버퍼 앞에서 프레임 하나를 찾는다.
    // STX 앞의 바이트는 버리고, 프레임 안에서 STX가 다시 나오면 거기서 새로 시작한다(재동기).
    // DLE 다음은 0x12, 0x13, 0x20만 허용한다(그 밖이면 Invalid). STX~ETX 길이가 maxLength를 넘으면 Invalid.
    public static TextCommFrame Find(ReadOnlySpan<byte> buffer, int maxLength)
    {
        int start = buffer.IndexOf(STX);
        if (start < 0) return new(TextCommStatus.Incomplete, buffer.Length, 0, 0, []);

        var payload = new List<byte>();
        bool bad = false;
        for (int i = start + 1; i < buffer.Length; i++)
        {
            // 너무 긴 프레임: STX부터 여기 앞까지 버린다
            if (i - start + 1 > maxLength) return new(TextCommStatus.Invalid, i, 0, 0, []);

            byte b = buffer[i];
            if (b == STX) { start = i; payload.Clear(); bad = false; continue; }
            if (b == ETX) return Decode(payload, bad, i + 1);
            if (b == DLE)
            {
                if (i + 1 >= buffer.Length) break;   // DLE 다음 바이트를 기다린다
                byte next = buffer[i + 1];
                if (next is 0x12 or 0x13 or 0x20) { payload.Add((byte)(next - 0x10)); i++; }
                else bad = true;   // next는 다음 바퀴에서 원래 바이트로 다룬다(STX면 재동기, ETX면 프레임 끝)
                continue;
            }
            payload.Add(b);
        }
        return new(TextCommStatus.Incomplete, start, 0, 0, []);
    }

    static TextCommFrame Decode(List<byte> payload, bool bad, int consumed)
    {
        if (bad || payload.Count < 3) return new(TextCommStatus.Invalid, consumed, 0, 0, []);
        var span = CollectionsMarshal.AsSpan(payload);
        if (Sum(span[..^1]) != span[^1]) return new(TextCommStatus.SumError, consumed, span[0], span[1], []);
        return new(TextCommStatus.Ok, consumed, span[0], span[1], span[2..^1].ToArray());
    }

    // pending 앞에서 끝난 프레임을 모두 꺼낸다(Ok·SumError·Invalid). 쓴 바이트와 버릴 바이트는 pending에서 지운다.
    public static List<TextCommFrame> Extract(List<byte> pending, int maxLength)
    {
        var frames = new List<TextCommFrame>();
        while (true)
        {
            var f = Find(CollectionsMarshal.AsSpan(pending), maxLength);
            if (f.Consumed > 0) pending.RemoveRange(0, f.Consumed);
            if (f.Status == TextCommStatus.Incomplete) return frames;
            frames.Add(f);
        }
    }

    // Master의 응답 받기. read(남은 ms) = 받은 바이트(시간 안에 못 받으면 null).
    // slave·command가 같은 정상 프레임이면 Ok, 합계가 틀린 프레임이면 SumError, 시간이 지나면 Timeout.
    // slave·command가 다른 정상 프레임(늦게 온 이전 응답 등)과 깨진 프레임은 버리고 시간 안에서 계속 읽는다.
    public static TextCommFrame Receive(Func<int, byte[]?> read, byte slave, byte command, int timeout, int maxLength, Func<TextCommFrame, bool>? accept = null)
    {
        var pending = new List<byte>();
        var sw = Stopwatch.StartNew();
        while (true)
        {
            int remain = timeout - (int)sw.ElapsedMilliseconds;
            if (remain <= 0) break;
            var chunk = read(remain);
            if (chunk == null) break;
            pending.AddRange(chunk);
            foreach (var f in Extract(pending, maxLength))
            {
                if (f.Status == TextCommStatus.SumError) return f;
                if (f.Status == TextCommStatus.Ok && f.Slave == slave && f.Command == command && (accept == null || accept(f))) return f;
            }
        }
        return new(TextCommStatus.Timeout, 0, 0, 0, []);
    }
}
