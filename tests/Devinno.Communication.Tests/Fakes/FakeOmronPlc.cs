using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Devinno.Communication.Omron.Data;

namespace Devinno.Communication.Tests.Fakes;

/// 테스트 전용 가상 옴론 PLC. 라이브러리에는 들어가지 않는다. 메모리 하나를 세 가지로 제공한다:
/// FINS/UDP 서버(127.0.0.1 임의 포트, 동적 변환처럼 요청을 보낸 곳으로 답함), FINS/TCP 서버(노드 주소 교환, 자동 할당 239부터),
/// Host Link FA 처리기(요청 프레임 → 응답 프레임, 시리얼 포트 없이).
/// 요청은 엄격하게 검사한다: ICF/GCT/주소/명령/길이가 하나라도 어긋나면 답하지 않고 Violations를 올린다(TCP는 연결도 끊음).
/// 메모리(워드 영역 코드별): CIO 6144, W·H 512, A 960, D 32768, EM 뱅크마다 32768워드. 비트 영역 코드는 같은 워드의 비트를 본다.
/// 없는 영역 코드는 종료 코드 0x1101, 범위 밖은 0x1103으로 답한다.
internal sealed class FakeOmronPlc : IDisposable
{
    readonly object sync = new();
    readonly Dictionary<byte, ushort[]> memory = [];
    readonly List<(byte Src, byte Area, int Word, int Bit, int Count)> requests = [];
    readonly List<TcpClient> clients = [];
    Socket? udp, foreign;
    TcpListener? listener;
    CancellationTokenSource? cts;
    int violations, connections, nextClient = 239;
    byte[] lastHeader = [];

    T Locked<T>(ref T field) { lock (sync) return field; }
    void Store<T>(ref T field, T value) { lock (sync) field = value; }

    /// 이 PLC의 FINS 노드 (UDP의 DA1, TCP의 서버 노드). DA1 = 0(자기 PLC)도 받고, 그때는 이 번호로 답한다.
    int _node = 1;
    public int Node { get => Locked(ref _node); set => Store(ref _node, value); }
    /// 메모리 명령에 이 종료 코드(MRES × 256 + SRES)로 답함 (데이터 없음)
    int? _endCode;
    public int? EndCode { get => Locked(ref _endCode); set => Store(ref _endCode, value); }
    /// EndCode를 쓰기 전에 정상으로 답할 메모리 명령 수 (연속 읽기/쓰기의 둘째 조각 실패 흉내)
    int _endCodeAfter;
    public int EndCodeAfter { get => Locked(ref _endCodeAfter); set => Store(ref _endCodeAfter, value); }
    /// 정상 응답의 종료 코드에 더하는 CPU 오류 플래그 (예: 0x0040 비치명 오류, 0x0080 치명 오류)
    int _flagBits;
    public int FlagBits { get => Locked(ref _flagBits); set => Store(ref _flagBits, value); }
    /// 쓰기(0102)에 0x2102(쓰기 보호)로 답함
    bool _writeProtect;
    public bool WriteProtect { get => Locked(ref _writeProtect); set => Store(ref _writeProtect, value); }
    /// 메모리 명령에 답하지 않음
    bool _silent;
    public bool Silent { get => Locked(ref _silent); set => Store(ref _silent, value); }
    /// UDP/TCP: 진짜 응답 앞에 SID가 하나 작고 데이터를 뒤집은 응답(늦게 온 이전 응답)을 먼저 보냄
    bool _staleReply;
    public bool StaleReply { get => Locked(ref _staleReply); set => Store(ref _staleReply, value); }
    /// UDP: 진짜 응답 앞에 다른 포트(127.0.0.1)에서 SID까지 같고 데이터를 뒤집은 응답을 먼저 보냄
    bool _foreignReply;
    public bool ForeignReply { get => Locked(ref _foreignReply); set => Store(ref _foreignReply, value); }
    /// TCP: FINS 프레임에 오류 통지(명령 3, 오류 코드 3)로 답하고 연결을 끊음
    bool _errorNotice;
    public bool ErrorNotice { get => Locked(ref _errorNotice); set => Store(ref _errorNotice, value); }
    /// TCP: 응답 앞에 연결 확인(명령 6)을 보냄
    bool _confirmation;
    public bool Confirmation { get => Locked(ref _confirmation); set => Store(ref _confirmation, value); }
    /// TCP: 응답을 이 바이트 수씩 나눠 보냄
    int? _fragmentSize;
    public int? FragmentSize { get => Locked(ref _fragmentSize); set => Store(ref _fragmentSize, value); }
    /// TCP: 노드 주소 응답에 이 오류 코드를 넣고 연결을 끊음 (예: 0x21 같은 노드 접속 중)
    int? _handshakeError;
    public int? HandshakeError { get => Locked(ref _handshakeError); set => Store(ref _handshakeError, value); }
    /// Host Link: 응답에서 ICF 앞에 Host Link 종료 코드 "00"을 넣음 (true) / 넣지 않음 (false)
    bool _hostLinkEndCode = true;
    public bool HostLinkEndCode { get => Locked(ref _hostLinkEndCode); set => Store(ref _hostLinkEndCode, value); }
    /// Host Link: 다음 응답 n개의 FCS를 틀리게 보냄
    int _corruptFcs;
    public int CorruptFcs { get => Locked(ref _corruptFcs); set => Store(ref _corruptFcs, value); }
    /// Host Link 국번. 다르면 답하지 않음 (실장비처럼)
    int _unitNumber;
    public int UnitNumber { get => Locked(ref _unitNumber); set => Store(ref _unitNumber, value); }

    public int UdpPort => ((IPEndPoint)udp!.LocalEndPoint!).Port;
    public int TcpPort => ((IPEndPoint)listener!.LocalEndpoint).Port;
    /// 형식이 어긋나 답하지 않은 요청 수
    public int Violations => Volatile.Read(ref violations);
    /// TCP 접속 수
    public int Connections => Volatile.Read(ref connections);
    /// 마지막으로 받은 Ethernet 요청의 FINS 헤더 10바이트
    public byte[] LastHeader { get { lock (sync) return [.. lastHeader]; } }
    /// 마지막 UDP 요청을 보낸 곳
    IPEndPoint? _lastUdpSource;
    public IPEndPoint? LastUdpSource { get => Locked(ref _lastUdpSource); private set => Store(ref _lastUdpSource, value); }
    /// 마지막 TCP 노드 주소 요청의 클라이언트 노드 (0 = 자동)
    int _lastRequestedClient = -1;
    public int LastRequestedClient { get => Locked(ref _lastRequestedClient); private set => Store(ref _lastRequestedClient, value); }

    /// 처리한 메모리 명령: (SRC 01/02/04, 영역 코드, 워드, 비트, 개수). 0104는 (4, 0, 0, 0, 주소 수)
    public (byte Src, byte Area, int Word, int Bit, int Count)[] Requests { get { lock (sync) return [.. requests]; } }
    public void ClearRequests() { lock (sync) requests.Clear(); }

    #region Memory
    /// 비트 영역 코드 → 같은 메모리의 워드 영역 코드. 모르는 코드면 0
    static byte WordArea(byte area) => area switch
    {
        0xB0 or 0xB1 or 0xB2 or 0xB3 or 0x82 => area,
        >= 0xA0 and <= 0xAF => area,
        >= 0x60 and <= 0x68 => area,
        0x30 => 0xB0, 0x31 => 0xB1, 0x32 => 0xB2, 0x33 => 0xB3, 0x02 => 0x82,
        >= 0x20 and <= 0x2F => (byte)(area + 0x80),
        >= 0xE0 and <= 0xE8 => (byte)(area - 0x80),
        _ => 0,
    };

    static bool IsBitArea(byte area) => area is 0x30 or 0x31 or 0x32 or 0x33 or 0x02 or (>= 0x20 and <= 0x2F) or (>= 0xE0 and <= 0xE8);

    static int Size(byte wordArea) => wordArea switch { 0xB0 => 6144, 0xB1 or 0xB2 => 512, 0xB3 => 960, _ => 32768 };

    // 반드시 sync 안에서 호출한다
    ushort[] Words(byte wordArea)
    {
        if (!memory.TryGetValue(wordArea, out var m)) memory[wordArea] = m = new ushort[Size(wordArea)];
        return m;
    }

    /// 값 읽기 (워드 0~65535, 비트 0/1). 라이브러리의 값 변환은 쓰지 않는다.
    public long Get(string address)
    {
        var a = FinsAddress.Parse(address, nameof(address));
        lock (sync)
        {
            var w = Words(WordArea(a.Area))[a.Word];
            return a.IsBit ? (w >> a.Bit) & 1 : w;
        }
    }

    public void Set(string address, long value)
    {
        var a = FinsAddress.Parse(address, nameof(address));
        lock (sync)
        {
            var m = Words(WordArea(a.Area));
            if (!a.IsBit) m[a.Word] = (ushort)value;
            else if (value != 0) m[a.Word] |= (ushort)(1 << a.Bit);
            else m[a.Word] &= (ushort)~(1 << a.Bit);
        }
    }

    /// 워드 주소부터 연속 워드
    public ushort[] GetWords(string start, int count)
    {
        var a = FinsAddress.Parse(start, nameof(start));
        lock (sync) return Words(WordArea(a.Area)).AsSpan(a.Word, count).ToArray();
    }

    public void SetWords(string start, params ushort[] values)
    {
        var a = FinsAddress.Parse(start, nameof(start));
        lock (sync) values.CopyTo(Words(WordArea(a.Area)).AsSpan(a.Word));
    }
    #endregion

    #region FINS 본문 처리 (MRC부터)
    void Violation() => Interlocked.Increment(ref violations);

    /// 메모리 명령 처리. 응답 본문(MRC SRC MRES SRES 데이터)을 돌려준다. 형식 위반이면 null(답하지 않음).
    byte[]? HandleBody(ReadOnlySpan<byte> body)
    {
        if (body.Length < 2) return null;
        byte mrc = body[0], src = body[1];
        if (mrc != 0x01 || src is not (0x01 or 0x02 or 0x04)) return [mrc, src, 0x04, 0x01];   // 지원하지 않는 명령

        var items = new List<(byte Area, int Word, int Bit)>();
        int count;
        if (src == 0x04)
        {
            if ((body.Length - 2) % 4 != 0 || body.Length == 2 || (body.Length - 2) / 4 > 167) return null;
            for (int p = 2; p < body.Length; p += 4) items.Add((body[p], body[p + 1] << 8 | body[p + 2], body[p + 3]));
            count = items.Count;
        }
        else
        {
            if (body.Length < 8) return null;
            items.Add((body[2], body[3] << 8 | body[4], body[5]));
            count = body[6] << 8 | body[7];
            if (count < 1 || count > (src == 0x01 ? 999 : 997)) return null;
            int width = IsBitArea(items[0].Area) ? 1 : 2;
            if (src == 0x01 ? body.Length != 8 : body.Length != 8 + count * width) return null;
            if (src == 0x02 && width == 1)
                foreach (var b in body[8..]) if (b > 1) return null;
        }
        foreach (var (area, _, bit) in items)
            if (bit > 15 || (!IsBitArea(area) && bit != 0)) return null;

        lock (sync)
        {
            requests.Add(src == 0x04 ? (src, (byte)0, 0, 0, count) : (src, items[0].Area, items[0].Word, items[0].Bit, count));
            if (_silent) return [];
            if (_endCode is int ec)
            {
                if (_endCodeAfter > 0) _endCodeAfter--;
                else return [mrc, src, (byte)(ec >> 8), (byte)ec];
            }

            int code = 0;
            foreach (var (area, word, bit) in items)
            {
                byte wa = WordArea(area);
                if (wa == 0) { code = 0x1101; break; }
                long last = IsBitArea(area) ? (word * 16L + bit + (src == 0x04 ? 1 : count) - 1) / 16 : word + (long)(src == 0x04 ? 1 : count) - 1;
                if (last >= Size(wa)) { code = 0x1103; break; }
            }
            if (code == 0 && src == 0x02 && _writeProtect) code = 0x2102;
            if (code != 0) return [mrc, src, (byte)(code >> 8), (byte)code];

            var outData = new List<byte> { mrc, src, (byte)(_flagBits >> 8), (byte)_flagBits };
            if (src == 0x04)
            {
                foreach (var (area, word, bit) in items)
                {
                    outData.Add(area);
                    ushort w = Words(WordArea(area))[word];
                    if (IsBitArea(area)) outData.Add((byte)((w >> bit) & 1));
                    else { outData.Add((byte)(w >> 8)); outData.Add((byte)w); }
                }
                return [.. outData];
            }
            var (a0, w0, b0) = items[0];
            var m = Words(WordArea(a0));
            bool isBit = IsBitArea(a0);
            for (int i = 0; i < count; i++)
            {
                int index = isBit ? w0 * 16 + b0 + i : w0 + i;
                if (src == 0x01)
                {
                    if (isBit) outData.Add((byte)((m[index / 16] >> (index % 16)) & 1));
                    else { outData.Add((byte)(m[index] >> 8)); outData.Add((byte)m[index]); }
                }
                else if (isBit)
                {
                    if (body[8 + i] != 0) m[index / 16] |= (ushort)(1 << (index % 16));
                    else m[index / 16] &= (ushort)~(1 << (index % 16));
                }
                else m[index] = (ushort)(body[8 + 2 * i] << 8 | body[9 + 2 * i]);
            }
            return [.. outData];
        }
    }

    /// Ethernet FINS 프레임 처리. 헤더 검사: ICF 80, RSV 00, GCT 02, DNA 00, DA1 = Node 또는 00, DA2 00, SA1 1~254, SA2 00.
    /// 응답 프레임을 돌려준다(Silent면 빈 배열). 형식 위반이면 null.
    byte[]? HandleFrame(ReadOnlySpan<byte> f, int? expectedClient)
    {
        int node = Node;
        if (f.Length < 12 || f[0] != 0x80 || f[1] != 0 || f[2] != 0x02 || f[3] != 0 || (f[4] != node && f[4] != 0) || f[5] != 0
            || f[7] is 0 or 255 || f[8] != 0 || (expectedClient is int c && f[7] != c)) { Violation(); return null; }
        lock (sync) lastHeader = f[..10].ToArray();
        var body = HandleBody(f[10..]);
        if (body == null) { Violation(); return null; }
        if (body.Length == 0) return [];
        return [0xC0, 0x00, 0x02, f[6], f[7], f[8], f[3], (byte)(f[4] == 0 ? node : f[4]), f[5], f[9], .. body];
    }

    /// 응답의 SID를 하나 줄이거나(stale) 데이터 바이트를 뒤집는다 (가짜 응답)
    static byte[] Tamper(byte[] response, bool previousSid)
    {
        var r = response.ToArray();
        if (previousSid) r[9]--;
        for (int i = 14; i < r.Length; i++) r[i] ^= 0xFF;
        return r;
    }
    #endregion

    #region UDP / TCP 서버
    public void Start()
    {
        cts = new CancellationTokenSource();
        udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        foreign = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        foreign.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = UdpLoop(udp, cts.Token);
        _ = AcceptLoop(listener, cts.Token);
    }

    async Task UdpLoop(Socket s, CancellationToken token)
    {
        var buf = new byte[4096];
        try
        {
            while (true)
            {
                var r = await s.ReceiveFromAsync(buf, new IPEndPoint(IPAddress.Any, 0), token);
                var from = (IPEndPoint)r.RemoteEndPoint;
                LastUdpSource = from;
                var reply = HandleFrame(buf.AsSpan(0, r.ReceivedBytes), null);
                if (reply == null || reply.Length == 0) continue;
                if (ForeignReply) await foreign!.SendToAsync(Tamper(reply, false), from, token);
                if (StaleReply) await s.SendToAsync(Tamper(reply, true), from, token);
                await s.SendToAsync(reply, from, token);
            }
        }
        catch { }
    }

    /// 지금 열린 TCP 연결을 모두 끊는다 (재접속 테스트용)
    public void Kick()
    {
        lock (sync)
        {
            foreach (var c in clients) c.Dispose();
            clients.Clear();
        }
    }

    async Task AcceptLoop(TcpListener l, CancellationToken token)
    {
        try
        {
            while (true)
            {
                var c = await l.AcceptTcpClientAsync(token);
                Interlocked.Increment(ref connections);
                lock (sync) clients.Add(c);
                _ = Task.Run(() => Serve(c, token), token);
            }
        }
        catch { }
    }

    static byte[] TcpHeader(int length, uint command, uint error)
    {
        var h = new byte[16];
        "FINS"u8.CopyTo(h);
        BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(4), length);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(8), command);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(12), error);
        return h;
    }

    async Task Serve(TcpClient c, CancellationToken token)
    {
        using (c)
        {
            try
            {
                var s = c.GetStream();
                var head = new byte[16];
                await s.ReadExactlyAsync(head, token);
                if (!head.AsSpan(0, 4).SequenceEqual("FINS"u8) || BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4)) != 12
                    || BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(8)) != 0) { Violation(); return; }
                var nodeField = new byte[4];
                await s.ReadExactlyAsync(nodeField, token);
                int requested = (int)BinaryPrimitives.ReadUInt32BigEndian(nodeField);
                LastRequestedClient = requested;
                if (HandshakeError is int he)
                {
                    byte[] refused = [.. TcpHeader(16, 1, (uint)he), 0, 0, 0, 0, 0, 0, 0, 0];
                    await s.WriteAsync(refused, token);
                    return;
                }
                int client;
                lock (sync) client = requested != 0 ? requested : nextClient++;
                int server = Node;
                byte[] accepted = [.. TcpHeader(16, 1, 0), 0, 0, 0, (byte)client, 0, 0, 0, (byte)server];
                await s.WriteAsync(accepted, token);

                while (true)
                {
                    await s.ReadExactlyAsync(head, token);
                    int length = (int)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4));
                    if (!head.AsSpan(0, 4).SequenceEqual("FINS"u8) || BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(8)) != 2
                        || length < 8 + 12 || length > 8 + 2012) { Violation(); return; }
                    var frame = new byte[length - 8];
                    await s.ReadExactlyAsync(frame, token);
                    var reply = HandleFrame(frame, client);
                    if (reply == null) return;
                    if (ErrorNotice)
                    {
                        await s.WriteAsync(TcpHeader(8, 3, 3), token);
                        return;
                    }
                    if (reply.Length == 0) continue;
                    var outBytes = new List<byte>();
                    if (Confirmation) outBytes.AddRange(TcpHeader(8, 6, 0));
                    if (StaleReply) { var st = Tamper(reply, true); outBytes.AddRange(TcpHeader(8 + st.Length, 2, 0)); outBytes.AddRange(st); }
                    outBytes.AddRange(TcpHeader(8 + reply.Length, 2, 0));
                    outBytes.AddRange(reply);
                    var all = outBytes.ToArray();
                    if (FragmentSize is int n)
                    {
                        for (int p = 0; p < all.Length; p += n)
                        {
                            await s.WriteAsync(all.AsMemory(p, Math.Min(n, all.Length - p)), token);
                            await s.FlushAsync(token);
                        }
                    }
                    else await s.WriteAsync(all, token);
                }
            }
            catch { }
        }
    }
    #endregion

    #region Host Link
    static string Fcs(string s)
    {
        int x = 0;
        foreach (char ch in s) x ^= ch;
        return x.ToString("X2", CultureInfo.InvariantCulture);
    }

    /// Host Link FA 요청 프레임 하나를 처리해 응답 프레임을 돌려준다. 국번이 다르거나 Silent면 null(무응답).
    /// 형식 위반(헤더 "00000000" 아님, FCS 틀림, 16진수 아님, 끝 "*" CR 아님)이면 Violations를 올리고 null.
    public byte[]? HandleHostLink(byte[] request)
    {
        var s = Encoding.ASCII.GetString(request);
        if (s.Length < 1 + 2 + 2 + 1 + 8 + 4 + 2 + 2 || s[0] != '@' || !s.EndsWith("*\r", StringComparison.Ordinal)) { Violation(); return null; }
        var text = s[..^4];
        if (!string.Equals(s[^4..^2], Fcs(text), StringComparison.Ordinal)) { Violation(); return null; }
        if (s[1..3] != UnitNumber.ToString("D2", CultureInfo.InvariantCulture)) return null;
        if (s[3..5] != "FA" || !Uri.IsHexDigit(s[5]) || s[6..14] != "00000000" || (text.Length - 14) % 2 != 0) { Violation(); return null; }
        byte[] body;
        try { body = Convert.FromHexString(text[14..]); }
        catch (FormatException) { Violation(); return null; }
        var reply = HandleBody(body);
        if (reply == null) { Violation(); return null; }
        if (reply.Length == 0) return null;
        var r = "@" + s[1..3] + "FA" + (HostLinkEndCode ? "00" : "") + "40000000" + Convert.ToHexString(reply);
        var fcs = Fcs(r);
        lock (sync)
        {
            if (_corruptFcs > 0)
            {
                _corruptFcs--;
                fcs = (Convert.ToInt32(fcs, 16) ^ 0x01).ToString("X2", CultureInfo.InvariantCulture);
            }
        }
        return Encoding.ASCII.GetBytes(r + fcs + "*\r");
    }
    #endregion

    public void Dispose()
    {
        cts?.Cancel();
        listener?.Stop();
        udp?.Dispose();
        foreign?.Dispose();
        Kick();
    }
}
