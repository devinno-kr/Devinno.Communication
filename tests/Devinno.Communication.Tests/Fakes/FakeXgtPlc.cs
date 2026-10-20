using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Devinno.Communication.LS.Data;
using Devinno.Communication.LS.CNet;
using Devinno.Communication.LS.FEnet;

namespace Devinno.Communication.Tests.Fakes;

/// 테스트 전용 가상 LS PLC. 라이브러리에는 들어가지 않는다.
/// 메모리: 영역 문자마다 64KB. 비트 X는 비트 번호, 나머지는 주소 × 크기 바이트 위치에 리틀엔디언 저장.
internal sealed class FakeXgtPlc : IDisposable
{
    readonly Dictionary<char, byte[]> memory = [];
    readonly object sync = new();
    TcpListener? listener;
    CancellationTokenSource? cts;

    public int Station { get; set; } = 1;
    public int? ForceNak { get; set; }
    public bool Silent { get; set; }
    public int Port => ((IPEndPoint)listener!.LocalEndpoint).Port;

    #region Memory
    // 반드시 sync 안에서 호출한다 (딕셔너리 접근 보호)
    byte[] Area(char a)
    {
        if (!memory.TryGetValue(a, out var m)) memory[a] = m = new byte[65536];
        return m;
    }

    static (char Area, char Size, int Offset, int Width) Locate(string name)
    {
        var d = LsDevice.Parse(name, nameof(name));
        if (d.Address.Contains('.')) throw new NotSupportedException("가상 PLC는 점 주소를 지원하지 않습니다.");
        int a = int.Parse(d.Address, CultureInfo.InvariantCulture);
        return (d.Area, d.Size, d.IsBit ? a : a * d.Width, d.Width);
    }

    static string Name(char area, char size, int address) => $"%{area}{size}{address}";

    public long Get(string device)
    {
        lock (sync)
        {
            var (area, size, off, w) = Locate(device);
            var m = Area(area);
            if (size == 'X') return (m[off / 8] >> (off % 8)) & 1;
            long v = 0;
            for (int i = w - 1; i >= 0; i--) v = (v << 8) | m[off + i];
            return v;
        }
    }

    public void Set(string device, long value)
    {
        lock (sync)
        {
            var (area, size, off, w) = Locate(device);
            var m = Area(area);
            if (size == 'X')
            {
                if (value != 0) m[off / 8] |= (byte)(1 << (off % 8));
                else m[off / 8] &= (byte)~(1 << (off % 8));
                return;
            }
            for (int i = 0; i < w; i++) m[off + i] = (byte)(value >> (8 * i));
        }
    }
    #endregion

    #region Cnet
    static int Hex(string s) => int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    static string HexOf(long v, int w) => w == 8 ? ((ulong)v).ToString("X16") : ((ulong)v & ((1UL << (w * 8)) - 1)).ToString("X" + w * 2);
    static int Sum(ReadOnlySpan<byte> s) { int x = 0; foreach (var b in s) x += b; return x & 0xFF; }

    static byte[] Reply(char first, string head, string body)
    {
        var b = Encoding.ASCII.GetBytes($"{first}{head}{body}\x03");
        return [.. b, .. Encoding.ASCII.GetBytes(Sum(b).ToString("X2"))];
    }

    /// Cnet 요청(ENQ … EOT BCC)에 대한 응답. BCC 틀림·국번 다름·Silent면 null.
    public byte[]? HandleCnet(byte[] request)
    {
        if (Silent) return null;
        var s = Encoding.ASCII.GetString(request);
        int eot = s.IndexOf('\x04');
        if (s.Length < 9 || s[0] != '\x05' || eot < 0 || eot + 3 != s.Length) return null;
        if (Hex(s[(eot + 1)..]) != Sum(request.AsSpan(0, eot + 1))) return null;
        string st = s.Substring(1, 2), cmd = s.Substring(3, 1), type = s.Substring(4, 2), body = s[6..eot];
        if (Hex(st) != Station) return null;
        string head = st + cmd + type;
        if (ForceNak is int code) return Reply('\x15', head, code.ToString("X4"));

        try
        {
            var sb = new StringBuilder();
            int p = 0;
            string Take(int n) { var r = body.Substring(p, n); p += n; return r; }
            switch (cmd + type)
            {
                case "rSS":
                    {
                        int n = Hex(Take(2));
                        sb.Append(n.ToString("X2"));
                        for (int i = 0; i < n; i++)
                        {
                            var name = Take(Hex(Take(2)));
                            int w = Locate(name).Width;
                            sb.Append(w.ToString("X2")).Append(HexOf(Get(name), w));
                        }
                        break;
                    }
                case "rSB":
                    {
                        var (area, size, off, w) = Locate(Take(Hex(Take(2))));
                        int cnt = Hex(Take(2));
                        sb.Append("01").Append((cnt * w).ToString("X2"));
                        for (int i = 0; i < cnt; i++) sb.Append(HexOf(Get(Name(area, size, off / w + i)), w));
                        break;
                    }
                case "wSS":
                    {
                        int n = Hex(Take(2));
                        for (int i = 0; i < n; i++)
                        {
                            var name = Take(Hex(Take(2)));
                            int w = Locate(name).Width;
                            Set(name, Convert.ToInt64(Take(w * 2), 16));
                        }
                        break;
                    }
                case "wSB":
                    {
                        var (area, size, off, w) = Locate(Take(Hex(Take(2))));
                        int cnt = Hex(Take(2));
                        for (int i = 0; i < cnt; i++) Set(Name(area, size, off / w + i), Convert.ToInt64(Take(w * 2), 16));
                        break;
                    }
                default:
                    return Reply('\x15', head, "0004");
            }
            return p == body.Length ? Reply('\x06', head, sb.ToString()) : Reply('\x15', head, "1234");
        }
        catch (Exception) { return Reply('\x15', head, "1132"); }
    }
    #endregion

    #region FEnet
    static ushort Get16(byte[] s, int at) => BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(at));
    static void Put16(List<byte> b, int v) { b.Add((byte)v); b.Add((byte)(v >> 8)); }

    /// FEnet 요청 본문에 대한 응답 본문
    public byte[] HandleFEnetBody(byte[] req)
    {
        ushort cmd = Get16(req, 0), type = Get16(req, 2);
        ushort resCmd = (ushort)(cmd == 0x54 ? 0x55 : 0x59);
        var b = new List<byte>();
        Put16(b, resCmd); Put16(b, type); Put16(b, 0);
        if (ForceNak is int code) { Put16(b, 0xFFFF); Put16(b, code); return [.. b]; }
        try
        {
            int blocks = Get16(req, 6), p = 8;
            string ReadName() { int len = Get16(req, p); var n = Encoding.ASCII.GetString(req, p + 2, len); p += 2 + len; return n; }

            if (type == 0x14)
            {
                var (area, size, off, _) = Locate(ReadName());
                if (size != 'B') throw new FormatException("연속 명령은 바이트 변수만");
                int count = Get16(req, p); p += 2;
                Put16(b, 0); Put16(b, 1);
                lock (sync)
                {
                    var mem = Area(area);
                    if (cmd == 0x54) { Put16(b, count); b.AddRange(mem.AsSpan(off, count).ToArray()); }
                    else { req.AsSpan(p, count).CopyTo(mem.AsSpan(off)); p += count; }
                }
            }
            else
            {
                var names = new List<string>();
                for (int i = 0; i < blocks; i++) names.Add(ReadName());
                Put16(b, 0); Put16(b, blocks);
                foreach (var name in names)
                {
                    int w = Locate(name).Width;
                    if (cmd == 0x54)
                    {
                        Put16(b, w);
                        long v = Get(name);
                        for (int i = 0; i < w; i++) b.Add((byte)(v >> (8 * i)));
                    }
                    else
                    {
                        int size = Get16(req, p);
                        long v = 0;
                        for (int i = size - 1; i >= 0; i--) v = (v << 8) | req[p + 2 + i];
                        Set(name, v);
                        p += 2 + size;
                    }
                }
            }
            if (p != req.Length) throw new FormatException("요청 길이가 맞지 않습니다.");
            return [.. b];
        }
        catch (Exception)
        {
            var e = new List<byte>();
            Put16(e, resCmd); Put16(e, type); Put16(e, 0); Put16(e, 0xFFFF); Put16(e, 0x0004);
            return [.. e];
        }
    }

    /// 127.0.0.1 임의 포트에 FEnet TCP 서버를 연다
    public void Start()
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        cts = new CancellationTokenSource();
        _ = AcceptLoop(listener, cts.Token);
    }

    async Task AcceptLoop(TcpListener l, CancellationToken token)
    {
        try
        {
            while (true)
            {
                var c = await l.AcceptTcpClientAsync(token);
                _ = Serve(c, token);
            }
        }
        catch { }
    }

    async Task Serve(TcpClient c, CancellationToken token)
    {
        using (c)
        {
            try
            {
                var s = c.GetStream();
                var head = new byte[20];
                while (true)
                {
                    await s.ReadExactlyAsync(head, token);
                    var body = new byte[Get16(head, 16)];
                    await s.ReadExactlyAsync(body, token);
                    if (Silent) continue;
                    var res = HandleFEnetBody(body);
                    var h = new byte[20];
                    "LSIS-XGT"u8.CopyTo(h);
                    h[13] = 0x11;
                    h[14] = head[14]; h[15] = head[15];
                    BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(16), (ushort)res.Length);
                    h[19] = (byte)Sum(h.AsSpan(0, 19));
                    await s.WriteAsync(h.Concat(res).ToArray(), token);
                }
            }
            catch { }
        }
    }
    #endregion

    public void Dispose()
    {
        cts?.Cancel();
        listener?.Stop();
    }
}
