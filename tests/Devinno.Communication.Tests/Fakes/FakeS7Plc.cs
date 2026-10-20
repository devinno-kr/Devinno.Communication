using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Devinno.Communication.Siemens;

namespace Devinno.Communication.Tests.Fakes;

/// 테스트 전용 가상 S7 PLC (ISO-on-TCP 서버, 127.0.0.1 임의 포트). 라이브러리에는 들어가지 않는다.
/// 응답은 실장비(S7-300 캡처) 형식을 따른다: CC 파라미터를 요청과 다른 순서로 보냄, 비트 항목은 03 + 길이 1비트,
/// 바이트 항목은 04 + 길이(비트 수), 홀수 길이 항목 뒤 채움 바이트(마지막 제외), 실패 항목은 rc 00 00 00.
/// 요청은 엄격하게 검사한다: TPKT/COTP/S7 형식이 하나라도 어긋나면 연결을 끊는다(Master 회귀가 루프백에서 우연히 통과하지 않게).
/// 메모리: I·Q·M은 65536바이트씩, DB는 AddDb로 만든 것만 있다(없는 DB → 0A, 범위 밖 → 05, 쓰기 금지 DB → 03).
internal sealed class FakeS7Plc : IDisposable
{
    readonly object sync = new();
    readonly Dictionary<(byte Area, int Db), byte[]> memory = [];
    readonly HashSet<int> protectedDbs = [];
    readonly List<(byte Function, int Bytes)> jobs = [];
    readonly List<TcpClient> clients = [];
    TcpListener? listener;
    CancellationTokenSource? cts;
    int connections, lastLocalTsap, lastRemoteTsap, lastRequestedPdu, jobRequests;

    /// 협상에서 줄 수 있는 최대 PDU (240/480/960)
    int _pduSize = 960;
    public int PduSize { get { lock (sync) return _pduSize; } set { lock (sync) _pduSize = value; } }
    /// 받아들이는 상대(PLC) TSAP. 다르면 CC 없이 연결을 끊는다. (기본 0x0101 = PG, 랙 0, 슬롯 1)
    int _expectedRemoteTsap = 0x0101;
    public int ExpectedRemoteTsap { get { lock (sync) return _expectedRemoteTsap; } set { lock (sync) _expectedRemoteTsap = value; } }
    /// 실패 항목을 Snap7 서버처럼 rc 00 00 04로 보냄
    bool _snapStyleErrors;
    public bool SnapStyleErrors { get { lock (sync) return _snapStyleErrors; } set { lock (sync) _snapStyleErrors = value; } }
    /// 읽기/쓰기 요청에 이 헤더 오류(등급 × 256 + 코드)로 응답 (Ack, 파라미터·데이터 없음)
    int? _headerError;
    public int? HeaderError { get { lock (sync) return _headerError; } set { lock (sync) _headerError = value; } }
    /// 응답 S7 PDU를 이 바이트 수씩 COTP DT 조각으로 나눠 보냄 (맨 앞에 빈 DT 하나 추가)
    int? _fragmentSize;
    public int? FragmentSize
    {
        get { lock (sync) return _fragmentSize; }
        set { if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value)); lock (sync) _fragmentSize = value; }
    }
    /// 읽기/쓰기 응답 앞에 PDU 참조가 다른 오류 응답(헤더 오류 8104)을 먼저 보냄
    bool _staleReply;
    public bool StaleReply { get { lock (sync) return _staleReply; } set { lock (sync) _staleReply = value; } }
    /// 읽기/쓰기 요청에 응답하지 않음 (연결·Setup은 응답)
    bool _silent;
    public bool Silent { get { lock (sync) return _silent; } set { lock (sync) _silent = value; } }
    /// Setup 응답에 요청한 크기와 상관없이 PduSize를 그대로 보냄 (Master가 스스로 작은 쪽을 고르는지 확인용)
    bool _ignoreRequestedPdu;
    public bool IgnoreRequestedPdu { get { lock (sync) return _ignoreRequestedPdu; } set { lock (sync) _ignoreRequestedPdu = value; } }

    public int Port => ((IPEndPoint)listener!.LocalEndpoint).Port;
    public int Connections => Volatile.Read(ref connections);
    public int LastLocalTsap => Volatile.Read(ref lastLocalTsap);
    public int LastRemoteTsap => Volatile.Read(ref lastRemoteTsap);
    public int LastRequestedPdu => Volatile.Read(ref lastRequestedPdu);
    /// 형식이 올바른 읽기/쓰기 요청을 받은 횟수 (헤더 오류·무응답으로 답한 요청 포함, Setup 제외)
    public int JobRequests => Volatile.Read(ref jobRequests);

    #region Memory
    public void AddDb(int number, int size) { lock (sync) memory[(S7Address.AreaDB, number)] = new byte[size]; }
    public void WriteProtectDb(int number) { lock (sync) protectedDbs.Add(number); }

    // 반드시 sync 안에서 호출한다. 없는 DB면 null
    byte[]? Area(byte area, int db)
    {
        if (area == S7Address.AreaDB) return memory.GetValueOrDefault((area, db));
        if (!memory.TryGetValue((area, 0), out var m)) memory[(area, 0)] = m = new byte[65536];
        return m;
    }

    public byte[] GetBytes(string byteAddress, int count)
    {
        var a = S7Address.Parse(byteAddress, nameof(byteAddress));
        lock (sync) return Area(a.Area, a.Db)!.AsSpan(a.Byte, count).ToArray();
    }

    public void SetBytes(string byteAddress, params byte[] data)
    {
        var a = S7Address.Parse(byteAddress, nameof(byteAddress));
        lock (sync) data.CopyTo(Area(a.Area, a.Db)!.AsSpan(a.Byte));
    }

    /// 값 읽기 (빅엔디언, 부호 없음, 비트는 0/1). 라이브러리의 Encode/Decode를 쓰지 않고 따로 계산한다.
    public long Get(string address)
    {
        var a = S7Address.Parse(address, nameof(address));
        lock (sync)
        {
            var m = Area(a.Area, a.Db)!;
            if (a.IsBit) return (m[a.Byte] >> a.Bit) & 1;
            long v = 0;
            for (int i = 0; i < a.Width; i++) v = (v << 8) | m[a.Byte + i];
            return v;
        }
    }

    public void Set(string address, long value)
    {
        var a = S7Address.Parse(address, nameof(address));
        lock (sync)
        {
            var m = Area(a.Area, a.Db)!;
            if (a.IsBit)
            {
                if (value != 0) m[a.Byte] |= (byte)(1 << a.Bit);
                else m[a.Byte] &= (byte)~(1 << a.Bit);
                return;
            }
            for (int i = 0; i < a.Width; i++) m[a.Byte + i] = (byte)(value >> (8 * (a.Width - 1 - i)));
        }
    }

    /// 처리한 읽기(04)/쓰기(05) 요청: (기능, 항목 데이터 바이트 합)
    public (byte Function, int Bytes)[] Jobs { get { lock (sync) return [.. jobs]; } }
    public void ClearJobs() { lock (sync) jobs.Clear(); }
    #endregion

    #region Server
    public void Start()
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        cts = new CancellationTokenSource();
        _ = AcceptLoop(listener, cts.Token);
    }

    /// 지금 열린 연결을 모두 끊는다 (재접속 테스트용)
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

    async Task Serve(TcpClient c, CancellationToken token)
    {
        using (c)
        {
            try
            {
                var s = c.GetStream();
                var cr = await ReadTpkt(s, token);
                var cc = cr == null ? null : ConnectionConfirm(cr);
                if (cc == null) return;   // 잘못된 CR이나 다른 TSAP → CC 없이 끊음
                await s.WriteAsync(cc, token);

                int pdu = 0;
                while (true)
                {
                    var req = await ReadJob(s, token);
                    if (req == null) return;
                    ushort pduRef = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(4));
                    byte[]? reply;
                    if (pdu == 0)
                    {
                        reply = Setup(req, pduRef, out pdu);
                        if (reply == null) return;
                    }
                    else
                    {
                        reply = HandleJob(req, pduRef, pdu, out bool violation);
                        if (violation) return;
                        if (Silent) continue;
                        if (StaleReply) await Send(s, Ack(S7Frame.RosctrAck, (ushort)(pduRef + 1), [], [], 0x8104), token);
                    }
                    await Send(s, reply!, token);
                }
            }
            catch { }
        }
    }

    static async Task<byte[]?> ReadTpkt(NetworkStream s, CancellationToken token)
    {
        var head = new byte[4];
        await s.ReadExactlyAsync(head, token);
        int len = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(2));
        if (head[0] != 0x03 || head[1] != 0x00 || len < 7) return null;
        var body = new byte[len - 4];
        await s.ReadExactlyAsync(body, token);
        return body;
    }

    /// COTP DT 하나(EOT, 조각 번호 0)에 담긴 S7 Job. 형식이 어긋나면 null
    static async Task<byte[]?> ReadJob(NetworkStream s, CancellationToken token)
    {
        var tpdu = await ReadTpkt(s, token);
        if (tpdu == null || tpdu.Length < 3 + 10 || tpdu[0] != 0x02 || tpdu[1] != 0xF0 || tpdu[2] != 0x80) return null;
        var p = tpdu[3..];
        if (p[0] != 0x32 || p[1] != S7Frame.RosctrJob || p[2] != 0 || p[3] != 0) return null;
        int pl = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(6)), dl = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(8));
        return 10 + pl + dl == p.Length ? p : null;
    }

    /// CR 검사 후 CC. LI·코드 E0·목적지 참조 0·클래스 0, 파라미터는 C1(2)·C2(2)·C0(1) 각각 한 번. 응답 파라미터는 C0, C1, C2 순서(실장비처럼).
    byte[]? ConnectionConfirm(byte[] cr)
    {
        if (cr.Length < 7 || cr[0] != cr.Length - 1 || cr[1] != 0xE0 || cr[2] != 0 || cr[3] != 0 || cr[6] != 0) return null;
        var prm = new Dictionary<byte, byte[]>();
        for (int p = 7; p < cr.Length;)
        {
            if (cr.Length - p < 2 || cr.Length - p - 2 < cr[p + 1] || !prm.TryAdd(cr[p], cr[(p + 2)..(p + 2 + cr[p + 1])])) return null;
            p += 2 + cr[p + 1];
        }
        if (prm.Count != 3 || !prm.TryGetValue(0xC1, out var local) || local.Length != 2 || !prm.TryGetValue(0xC2, out var remote) || remote.Length != 2
            || !prm.TryGetValue(0xC0, out var tpdu) || tpdu.Length != 1 || tpdu[0] is < 0x07 or > 0x0D) return null;
        Volatile.Write(ref lastLocalTsap, local[0] << 8 | local[1]);
        Volatile.Write(ref lastRemoteTsap, remote[0] << 8 | remote[1]);
        if ((remote[0] << 8 | remote[1]) != ExpectedRemoteTsap) return null;
        return [0x03, 0x00, 0x00, 0x16, 0x11, 0xD0, cr[4], cr[5], 0x00, 0x03, 0x00, 0xC0, 0x01, tpdu[0], 0xC1, 0x02, local[0], local[1], 0xC2, 0x02, remote[0], remote[1]];
    }

    byte[]? Setup(byte[] req, ushort pduRef, out int pdu)
    {
        pdu = 0;
        int pl = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(6)), dl = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(8));
        if (pl != 8 || dl != 0 || req[10] != S7Frame.FuncSetup || req[11] != 0) return null;
        if (req[12] != 0x00 || req[13] != 0x01 || req[14] != 0x00 || req[15] != 0x01) return null;
        int requested = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(16));
        Volatile.Write(ref lastRequestedPdu, requested);
        pdu = IgnoreRequestedPdu ? PduSize : Math.Min(requested, PduSize);
        return Ack(S7Frame.RosctrAckData, pduRef, [S7Frame.FuncSetup, 0x00, 0x00, 0x01, 0x00, 0x01, (byte)(pdu >> 8), (byte)pdu], [], 0);
    }

    static byte[] Ack(byte rosctr, ushort pduRef, byte[] param, byte[] data, int error)
    {
        var p = new byte[12 + param.Length + data.Length];
        p[0] = 0x32; p[1] = rosctr;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(4), pduRef);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(6), (ushort)param.Length);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(8), (ushort)data.Length);
        p[10] = (byte)(error >> 8); p[11] = (byte)error;
        param.CopyTo(p, 12);
        data.CopyTo(p, 12 + param.Length);
        return p;
    }

    async Task Send(NetworkStream s, byte[] pdu, CancellationToken token)
    {
        static byte[] Dt(ReadOnlySpan<byte> part, bool eot) => [0x03, 0x00, (byte)((part.Length + 7) >> 8), (byte)(part.Length + 7), 0x02, 0xF0, (byte)(eot ? 0x80 : 0x00), .. part];
        var frames = new List<byte>();
        if (FragmentSize is int n)
        {
            frames.AddRange(Dt([], false));
            for (int p = 0; p < pdu.Length; p += n) frames.AddRange(Dt(pdu.AsSpan(p, Math.Min(n, pdu.Length - p)), p + n >= pdu.Length));
        }
        else frames.AddRange(Dt(pdu, true));
        await s.WriteAsync(frames.ToArray(), token);
    }
    #endregion

    #region Read / Write Var
    readonly record struct Item(byte Ts, int Count, int Db, byte Area, int BitAddress)
    {
        public int Length => Ts == S7Item.TsBit ? 1 : Count;
    }

    /// 읽기/쓰기 Job. 형식 위반이면 violation = true (연결을 끊음)
    byte[]? HandleJob(byte[] req, ushort pduRef, int pdu, out bool violation)
    {
        violation = true;
        int pl = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(6)), dl = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(8));
        if (pl < 2) return null;
        byte func = req[10];
        int n = req[11];
        if (func is not (S7Frame.FuncRead or S7Frame.FuncWrite) || n is < 1 or > 20 || pl != 2 + 12 * n) return null;
        if (func == S7Frame.FuncRead && dl != 0) return null;
        var items = new Item[n];
        for (int i = 0; i < n; i++)
        {
            var d = req.AsSpan(12 + 12 * i, 12);
            if (d[0] != 0x12 || d[1] != 0x0A || d[2] != 0x10) return null;
            var it = new Item(d[3], BinaryPrimitives.ReadUInt16BigEndian(d[4..]), BinaryPrimitives.ReadUInt16BigEndian(d[6..]), d[8], d[9] << 16 | d[10] << 8 | d[11]);
            bool areaOk = it.Area is S7Address.AreaI or S7Address.AreaQ or S7Address.AreaM ? it.Db == 0 : it.Area == S7Address.AreaDB && it.Db >= 1;
            bool tsOk = it.Ts == S7Item.TsBit ? it.Count == 1 : it.Ts == S7Item.TsByte && it.Count >= 1 && (it.BitAddress & 7) == 0;
            if (!areaOk || !tsOk) return null;
            items[i] = it;
        }

        byte[][] values = new byte[n][];
        if (func == S7Frame.FuncWrite)
        {
            int p = 10 + pl, end = req.Length;
            for (int i = 0; i < n; i++)
            {
                var it = items[i];
                if (end - p < 4 || req[p] != 0x00) return null;
                int len = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(p + 2));
                bool ok = it.Ts == S7Item.TsBit ? req[p + 1] == S7Frame.DataBit && len == 1 : req[p + 1] == S7Frame.DataByte && len == it.Count * 8;
                if (!ok || end - p - 4 < it.Length) return null;
                values[i] = req[(p + 4)..(p + 4 + it.Length)];
                if (it.Ts == S7Item.TsBit && values[i][0] > 1) return null;
                p += 4 + it.Length;
                if (it.Length % 2 == 1 && i < n - 1)
                {
                    if (p >= end || req[p] != 0x00) return null;
                    p++;
                }
            }
            if (p != end) return null;
        }
        violation = false;
        Interlocked.Increment(ref jobRequests);

        if (HeaderError is int he) return Ack(S7Frame.RosctrAck, pduRef, [], [], he);
        int dataOut = 0;
        for (int i = 0; i < n; i++) dataOut += 4 + items[i].Length + (items[i].Length % 2 == 1 && i < n - 1 ? 1 : 0);
        int responseLength = func == S7Frame.FuncRead ? 14 + dataOut : 14 + n;
        if (req.Length > pdu || responseLength > pdu) return Ack(S7Frame.RosctrAck, pduRef, [], [], 0x8500);   // Snap7 서버처럼: PDU를 넘음

        var outData = new List<byte>();
        lock (sync)
        {
            jobs.Add((func, items.Sum(x => x.Length)));
            for (int i = 0; i < n; i++)
            {
                var it = items[i];
                var m = Area(it.Area, it.Db);
                int off = it.BitAddress >> 3;
                byte rc = m == null ? (byte)0x0A
                        : off + it.Length > m.Length ? (byte)0x05
                        : func == S7Frame.FuncWrite && it.Area == S7Address.AreaDB && protectedDbs.Contains(it.Db) ? (byte)0x03
                        : (byte)0xFF;
                if (func == S7Frame.FuncWrite)
                {
                    if (rc == 0xFF)
                    {
                        if (it.Ts == S7Item.TsBit)
                        {
                            int bit = it.BitAddress & 7;
                            if (values[i][0] != 0) m![off] |= (byte)(1 << bit); else m![off] &= (byte)~(1 << bit);
                        }
                        else values[i].CopyTo(m!, off);
                    }
                    outData.Add(rc);
                    continue;
                }
                if (rc != 0xFF) { outData.AddRange([rc, 0x00, 0x00, (byte)(SnapStyleErrors ? 0x04 : 0x00)]); continue; }
                if (it.Ts == S7Item.TsBit) outData.AddRange([0xFF, S7Frame.DataBit, 0x00, 0x01, (byte)((m![off] >> (it.BitAddress & 7)) & 1)]);
                else
                {
                    int bits = it.Count * 8;
                    outData.AddRange([0xFF, S7Frame.DataByte, (byte)(bits >> 8), (byte)bits]);
                    outData.AddRange(m!.AsSpan(off, it.Count).ToArray());
                }
                if (it.Length % 2 == 1 && i < n - 1) outData.Add(0x00);
            }
        }
        return Ack(S7Frame.RosctrAckData, pduRef, [func, (byte)n], [.. outData], 0);
    }
    #endregion

    public void Dispose()
    {
        cts?.Cancel();
        listener?.Stop();
        Kick();
    }
}
