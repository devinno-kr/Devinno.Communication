using System.Net;
using System.Net.Sockets;
using Devinno.Communication.Mitsubishi.Data;
using Devinno.Communication.Mitsubishi.Ethernet;

namespace Devinno.Communication.Tests.Fakes;

/// 가상 미쓰비시 PLC의 이더넷 부분: 3E/1E 바이너리 요청 처리 + 127.0.0.1 TCP 서버.
/// 디바이스 코드는 라이브러리 표를 쓰지 않고 여기 표로 엄격하게 해석한다(모르는 코드·서브커맨드는 오류 응답).
internal sealed partial class FakeMcPlc
{
    static readonly Dictionary<byte, string> Codes3E = new()
    {
        [0x91] = "SM", [0xA9] = "SD", [0x9C] = "X", [0x9D] = "Y", [0x90] = "M", [0x92] = "L", [0x93] = "F", [0x94] = "V",
        [0xA0] = "B", [0xA8] = "D", [0xB4] = "W", [0xC1] = "TS", [0xC0] = "TC", [0xC2] = "TN", [0xC7] = "SS", [0xC6] = "SC",
        [0xC8] = "SN", [0xC4] = "CS", [0xC3] = "CC", [0xC5] = "CN", [0xA1] = "SB", [0xB5] = "SW", [0x98] = "S", [0xCC] = "Z",
        [0xAF] = "R", [0xB0] = "ZR",
    };

    static readonly Dictionary<int, string> Codes1E = new()
    {
        [0x5820] = "X", [0x5920] = "Y", [0x4D20] = "M", [0x5320] = "S", [0x4C20] = "L", [0x4620] = "F", [0x4220] = "B",
        [0x4420] = "D", [0x5220] = "R", [0x5720] = "W", [0x544E] = "TN", [0x5453] = "TS", [0x5443] = "TC", [0x434E] = "CN",
        [0x4353] = "CS", [0x4343] = "CC",
    };

    static readonly byte[] Route3E = [0x00, 0xFF, 0xFF, 0x03, 0x00];

    TcpListener? listener;
    CancellationTokenSource? cts;

    /// 1E 오류 5BH 뒤에 00H를 붙일지 (FX3 형식 4바이트, false면 A/FX5 형식 3바이트)
    public bool AbnormalTrailingZero { get; set; } = true;
    /// 0보다 크면 1E 5BH 오류의 뒤따르는 00H를 본 응답과 따로, 이 시간(ms) 뒤에 보낸다 (AbnormalTrailingZero가 true일 때)
    public int TrailingZeroDelayMs { get; set; }
    public int Port => ((IPEndPoint)listener!.LocalEndpoint).Port;

    static int Le16(byte[] b, int at) => b[at] | b[at + 1] << 8;

    /// 쓰기 데이터를 값으로 푼다. 길이가 점수와 안 맞거나, 비트 니블이 0/1이 아니거나, 홀수 점수의 패드 니블이 0이 아니면 null.
    static long[]? WriteData(McCommand command, byte[] data, int count)
    {
        if (command == McCommand.BW)
        {
            if (data.Length != (count + 1) / 2) return null;
            if (count % 2 == 1 && (data[^1] & 0x0F) != 0) return null;
            var nibbles = Enumerable.Range(0, count).Select(i => (long)((i % 2 == 0 ? data[i / 2] >> 4 : data[i / 2]) & 0x0F)).ToArray();
            return nibbles.Any(n => n > 1) ? null : nibbles;
        }
        if (data.Length != count * 2) return null;
        return Enumerable.Range(0, count).Select(i => (long)Le16(data, i * 2)).ToArray();
    }

    static byte[] ReadData(McCommand command, long[] values)
    {
        if (command == McCommand.BR)
        {
            var b = new byte[(values.Length + 1) / 2];
            for (int i = 0; i < values.Length; i++) if (values[i] != 0) b[i / 2] |= (byte)(i % 2 == 0 ? 0x10 : 0x01);
            return b;
        }
        return values.SelectMany(v => new[] { (byte)v, (byte)(v >> 8) }).ToArray();
    }

    #region 3E
    static byte[] Response3E(int endCode, byte[] data)
    {
        int len = 2 + data.Length;
        return [0xD0, 0x00, .. Route3E, (byte)len, (byte)(len >> 8), (byte)endCode, (byte)(endCode >> 8), .. data];
    }

    /// 3E 요청 프레임 전체 → 응답 프레임 전체. 오류는 종료코드(C059 명령/서브커맨드, C05C 그 밖의 요청 내용)와 오류 정보(경로 + 명령 + 서브커맨드).
    public byte[] Handle3E(byte[] req)
    {
        Interlocked.Increment(ref requests);
        byte[] Error(int code) => Response3E(code, [.. Route3E, .. (req.Length >= 15 ? req[11..15] : new byte[4])]);
        if (req.Length < 21 || req[0] != 0x50 || req[1] != 0x00 || !req.AsSpan(2, 5).SequenceEqual(Route3E) || Le16(req, 7) != req.Length - 9)
            return Error(0xC05C);
        if (ForceError is int fe) return Error(fe);
        int cmd = Le16(req, 11), sub = Le16(req, 13);
        if (cmd is not (0x0401 or 0x1401) || sub is not (0x0000 or 0x0001)) return Error(0xC059);
        if (!Codes3E.TryGetValue(req[18], out var code)) return Error(0xC05C);
        var command = (cmd, sub) switch
        {
            (0x0401, 0) => McCommand.WR,
            (0x0401, _) => McCommand.BR,
            (_, 0) => McCommand.WW,
            _ => McCommand.BW,
        };
        int head = req[15] | req[16] << 8 | req[17] << 16;
        int count = Le16(req, 19);
        if (count < 1 || count > (command is McCommand.BR or McCommand.BW ? 3584 : 960)) return Error(0xC05C);
        try
        {
            var data = req[21..];
            long[]? values = null;
            if (command is McCommand.BW or McCommand.WW)
            {
                values = WriteData(command, data, count);
                if (values == null) return Error(0xC05C);
            }
            else if (data.Length != 0) return Error(0xC05C);
            var r = Execute(command, code, head, count, values);
            return Response3E(0, values == null ? ReadData(command, r) : new byte[0]);
        }
        catch (InvalidDataException) { return Error(0xC05C); }
    }
    #endregion

    #region 1E
    /// 1E 요청 헤더(12바이트) 뒤에 따라오는 쓰기 데이터 바이트 수
    static int DataLength1E(byte[] head)
    {
        int n = head[10] == 0 ? 256 : head[10];
        return head[0] switch { 0x02 => (n + 1) / 2, 0x03 => n * 2, _ => 0 };
    }

    /// 1E 요청 프레임 전체 → 응답 프레임 전체. 오류: 50H 명령, 56H 디바이스, 57H 길이·점수, 58H 비트 명령에 워드 디바이스, 5BH+10H PC 번호. 점수 한계·음수 번호·잘못된 비트 데이터도 거부한다.
    public byte[] Handle1E(byte[] req)
    {
        Interlocked.Increment(ref requests);
        byte sub = req.Length > 0 ? req[0] : (byte)0xFF;
        byte resp = (byte)(sub | 0x80);
        byte[] Error(int code) => code > 0xFF
            ? (AbnormalTrailingZero ? [resp, (byte)(code >> 8), (byte)code, 0x00] : [resp, (byte)(code >> 8), (byte)code])
            : [resp, (byte)code];
        if (req.Length < 12 || sub > 0x03 || req[11] != 0x00) return Error(0x50);
        if (req[1] != 0xFF) return Error(0x5B10);
        if (ForceError is int fe) return Error(fe);
        if (!Codes1E.TryGetValue(Le16(req, 8), out var code)) return Error(0x56);
        var command = sub switch { 0x00 => McCommand.BR, 0x01 => McCommand.WR, 0x02 => McCommand.BW, _ => McCommand.WW };
        int head = req[4] | req[5] << 8 | req[6] << 16 | req[7] << 24;
        int count = req[10] == 0 ? 256 : req[10];
        if (head < 0) return Error(0x56);
        bool wordDevice = WordCodes.Contains(code);
        int max = command switch
        {
            McCommand.BR => 256,
            McCommand.BW => 160,
            McCommand.WR => wordDevice ? 64 : 32,
            _ => wordDevice ? 64 : 10,
        };
        if (count > max || req.Length != 12 + DataLength1E(req)) return Error(0x57);
        try
        {
            long[]? values = null;
            if (command is McCommand.BW or McCommand.WW)
            {
                values = WriteData(command, req[12..], count);
                if (values == null) return Error(0x57);
            }
            var r = Execute(command, code, head, count, values);
            return [resp, 0x00, .. (values == null ? ReadData(command, r) : new byte[0])];
        }
        catch (InvalidDataException) { return Error(0x58); }
    }
    #endregion

    #region TCP
    /// 127.0.0.1 임의 포트에 TCP 서버를 연다. frame에 따라 3E 또는 1E로 요청을 자른다.
    public void Start(McFrame frame)
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        cts = new CancellationTokenSource();
        _ = AcceptLoop(listener, frame, cts.Token);
    }

    async Task AcceptLoop(TcpListener l, McFrame frame, CancellationToken token)
    {
        try
        {
            while (true)
            {
                var c = await l.AcceptTcpClientAsync(token);
                _ = Serve(c, frame, token);
            }
        }
        catch { }
    }

    async Task Serve(TcpClient c, McFrame frame, CancellationToken token)
    {
        using (c)
        {
            try
            {
                var s = c.GetStream();
                while (true)
                {
                    byte[] req;
                    if (frame == McFrame.E3)
                    {
                        var head = new byte[9];
                        await s.ReadExactlyAsync(head, token);
                        if (head[0] != 0x50 || head[1] != 0x00) return;   // 서브헤더가 틀리면 연결을 끊는다
                        var body = new byte[Le16(head, 7)];
                        await s.ReadExactlyAsync(body, token);
                        req = [.. head, .. body];
                    }
                    else
                    {
                        var head = new byte[12];
                        await s.ReadExactlyAsync(head, token);
                        if (head[0] > 0x03) return;
                        var data = new byte[DataLength1E(head)];
                        await s.ReadExactlyAsync(data, token);
                        req = [.. head, .. data];
                    }
                    if (Silent) { Interlocked.Increment(ref requests); continue; }
                    var res = frame == McFrame.E3 ? Handle3E(req) : Handle1E(req);
                    if (frame == McFrame.E1 && TrailingZeroDelayMs > 0 && res.Length == 4 && res[1] == 0x5B)
                    {
                        await s.WriteAsync(res.AsMemory(0, 3), token);
                        await Task.Delay(TrailingZeroDelayMs, token);
                        await s.WriteAsync(res.AsMemory(3, 1), token);
                        continue;
                    }
                    await s.WriteAsync(res, token);
                }
            }
            catch { }
        }
    }

    partial void StopServer()
    {
        cts?.Cancel();
        listener?.Stop();
    }
    #endregion
}
