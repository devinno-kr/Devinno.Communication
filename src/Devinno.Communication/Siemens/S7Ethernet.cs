using System.Diagnostics;
using System.Net.Sockets;
using System.Xml.Linq;
using Devinno.Communication.Internal;

namespace Devinno.Communication.Siemens;

/// <summary>
/// Siemens S7 PLC master that exchanges Read Var / Write Var of S7 communication (S7comm) over ISO-on-TCP (RFC 1006, default port 102).
/// Targets the S7-300/400/1200/1500 and the S7-200 SMART and LOGO! (with the TSAP specified directly).
/// </summary>
/// <remarks>
/// <para><b>The S7-1200/1500 require PLC settings.</b> In TIA Portal, under CPU properties, Protection &amp; Security, Connection mechanisms,
/// "Permit access with PUT/GET communication from remote partner" must be enabled,
/// and "Optimized block access" must be turned off in the block properties of the DBs to read and write (changing it initializes the DB values).
/// Symbolic access, optimized DBs, S7CommPlus, and timers/counters (T/C/Z) are not supported.</para>
/// <para>Rack/slot: S7-1200/1500 = 0/1 (default), S7-300 = 0/2, S7-400 = 0/3 (depends on the hardware configuration).
/// The remote TSAP = <see cref="ConnectionType"/> x 256 + rack x 32 + slot. For devices that define the TSAP separately, such as the S7-200 SMART and LOGO!,
/// specify <see cref="RemoteTsap"/> and <see cref="LocalTsap"/> directly (the V memory of LOGO! is DB1).</para>
/// <para>Addresses are absolute addresses (case-insensitive): DB1.DBX0.1, DB1.DBB0, DB1.DBW0, DB1.DBD0, M10.0, MB10, MW10, MD10,
/// I0.0/IB0/IW0/ID0, Q0.0/QB0/QW0/QD0. The German E (input) and A (output) are also accepted (EB0 = IB0, AW0 = QW0).
/// The bit number is 0 to 7, the byte address is 0 to 65535, and the DB number is 1 to 65535.</para>
/// <para>Values are exchanged as <see cref="long"/>. A value read is a big-endian unsigned raw value (bit 0/1, B 0 to 255, W 0 to 65535, D 0 to 4294967295),
/// and a value to write is truncated to the size (negative values as two's complement; a bit accepts only 0/1). INT, DINT, and REAL conversion is done by the application
/// (for example, reading a REAL: <c>BitConverter.Int32BitsToSingle((int)value)</c>, writing a REAL: <c>BitConverter.SingleToInt32Bits(1.5f)</c>).</para>
/// <para>Random read/write (Read/Write): the request and response must fit in 240 bytes, the minimum PDU of all CPUs (checked at registration).
/// A read takes at most 19 addresses of any size (20 is rejected), and a write takes at most 12 bits, bytes, or words, or at most 11 double words. Block read/write (ReadBlock/WriteBlock) covers 1 to 65535 bytes from a byte address, and
/// according to the PDU size negotiated at connection (<see cref="NegotiatedPduSize"/>), a read is split into pieces of PDU - 18 bytes and a write into pieces of PDU - 28 bytes
/// (for a PDU of 240, 222/212 bytes = the official GET/PUT limit of the S7-1200). The event is raised once only if all the pieces succeed, and if any one fails,
/// only <see cref="NAKReceived"/> is raised. A block write that was split is not atomic (the earlier pieces may already have been written).</para>
/// <para>Manual jobs are processed before Auto jobs. Responses are matched to requests by the PDU reference number in the S7 header; a response that does not match is discarded.
/// When the PLC sends an error (a header error or an item return code), only <see cref="NAKReceived"/> is raised and no retry is made.</para>
/// <para>The communication loop runs on a dedicated background thread, and all events are raised on that thread. To update the UI, marshal asynchronously, as with BeginInvoke (WinForms) or Dispatcher.BeginInvoke (WPF). Exceptions thrown in a handler are ignored and the loop continues.</para>
/// </remarks>
/// <example>
/// <code>
/// using Devinno.Communication.Siemens;
///
/// var plc = new S7Ethernet { RemoteIP = "192.168.0.10", Rack = 0, Slot = 1 };   // S7-1200/1500 (Slot = 2 for S7-300)
/// plc.AutoRead(1, ["DB1.DBW0", "DB1.DBD2", "M10.0"]);
/// plc.AutoReadBlock(2, "DB1.DBB100", 500);   // 500 bytes, read in pieces that fit the PDU size
/// plc.DataReceived += (s, e) =&gt;
/// {
///     if (e.MessageID != 1) return;
///     long word = e.Values[0];
///     float real = BitConverter.Int32BitsToSingle((int)e.Values[1]);   // when DB1.DBD2 is a REAL
///     bool flag = e.Values[2] == 1;
/// };
/// plc.BlockReceived += (s, e) =&gt; Console.WriteLine($"{e.Address}: {e.Data.Length} bytes");
/// plc.NAKReceived += (s, e) =&gt; Console.WriteLine($"Error 0x{e.ErrorCode:X2} (item {e.ItemIndex})");
/// plc.Start();
/// plc.ManualWrite(10, [new S7Value("DB1.DBW0", 1234), new S7Value("M10.0", 1)]);
/// plc.ManualWrite(11, [new S7Value("DB1.DBD4", BitConverter.SingleToInt32Bits(1.5f))]);   // writing a REAL
/// plc.ManualWriteBlock(12, "DB2.DBB0", new byte[300]);
/// plc.NamedWrite("Speed", 1500);   // calls the XML &lt;Write Name="Speed" Id="20" Address="DB1.DBW10" /&gt; by name
/// </code>
/// </example>
public class S7Ethernet : IDisposable
{
    #region class : Work
    internal sealed class Work(int messageId, S7Command command, S7Address[] items, int count, byte[] param, byte[] data, int? repeatCount, int? timeout)
    {
        public int MessageID { get; } = messageId;
        public S7Command Command { get; } = command;
        // Read/Write는 요청한 주소들, 블록은 시작 주소 1개
        public S7Address[] Items { get; } = items;
        // 블록 바이트 수 (Read/Write는 0)
        public int Count { get; } = count;
        // Read/Write의 미리 만든 파라미터 (블록은 빈 배열)
        public byte[] Param { get; } = param;
        // Write의 미리 만든 데이터, WriteBlock의 쓸 바이트 (읽기는 빈 배열)
        public byte[] Data { get; } = data;
        public int? RepeatCount { get; } = repeatCount;
        public int? Timeout { get; } = timeout;
        public string[] Addresses => [.. Items.Select(a => a.Name)];
    }
    #endregion

    #region class : EventArgs
    /// <summary>Common event arguments for request result events. Write response and timeout events use this type as-is.</summary>
    /// <param name="messageId">The message ID given at registration.</param>
    /// <param name="command">Request type.</param>
    public class ResultEventArgs(int messageId, S7Command command) : EventArgs
    {
        /// <summary>The message ID given at registration.</summary>
        public int MessageID { get; } = messageId;
        /// <summary>Request type.</summary>
        public S7Command Command { get; } = command;
    }

    /// <summary>Event arguments for a random read (Read) response.</summary>
    /// <param name="messageId">Message ID.</param>
    /// <param name="command">Request type (Read).</param>
    /// <param name="addresses">The requested addresses (as registered, uppercase).</param>
    /// <param name="values">Values read. Big-endian unsigned raw values in address order (bit 0/1).</param>
    public class DataEventArgs(int messageId, S7Command command, string[] addresses, long[] values)
        : ResultEventArgs(messageId, command)
    {
        /// <summary>The requested addresses (as registered, uppercase).</summary>
        public string[] Addresses { get; } = addresses;
        /// <summary>Values read. Big-endian unsigned raw values in address order (bit 0/1, B 0 to 255, W 0 to 65535, D 0 to 4294967295).</summary>
        public long[] Values { get; } = values;
    }

    /// <summary>Event arguments for a block read (ReadBlock) response.</summary>
    /// <param name="messageId">Message ID.</param>
    /// <param name="command">Request type (ReadBlock).</param>
    /// <param name="address">Start byte address (as registered, uppercase).</param>
    /// <param name="data">Bytes read (in PLC memory order).</param>
    public class BlockEventArgs(int messageId, S7Command command, string address, byte[] data)
        : ResultEventArgs(messageId, command)
    {
        /// <summary>Start byte address (as registered, uppercase).</summary>
        public string Address { get; } = address;
        /// <summary>Bytes read (in PLC memory order). Words and double words are big-endian.</summary>
        public byte[] Data { get; } = data;
    }

    /// <summary>Event arguments for when the PLC sends an error response.</summary>
    /// <param name="messageId">Message ID.</param>
    /// <param name="command">Request type.</param>
    /// <param name="errorCode">For a header error, error class x 256 + error code (for example 0x8500 PDU size exceeded, 0x8104 not supported); for an item error, the return code (0x03 access denied, 0x05 address out of range, 0x0A object does not exist (DB)).</param>
    /// <param name="itemIndex">Number of the first failed item (0-based; 0 for block read/write). -1 for a header error.</param>
    public class NAKEventArgs(int messageId, S7Command command, int errorCode, int itemIndex)
        : ResultEventArgs(messageId, command)
    {
        /// <summary>For a header error, error class x 256 + error code (for example 0x8500 PDU size exceeded, 0x8104 not supported); for an item error, the return code (0x03 access denied, 0x05 address out of range, 0x0A object does not exist (DB)).</summary>
        public int ErrorCode { get; } = errorCode;
        /// <summary>Number of the first failed item (0-based; 0 for block read/write). -1 for a header error.</summary>
        public int ItemIndex { get; } = itemIndex;
    }
    #endregion

    #region Properties
    int rack, slot = 1, localTsap = 0x0100, pduSize = 960, negotiated;
    int? remoteTsap;
    S7ConnectionType connectionType = S7ConnectionType.PG;

    /// <summary>IP address of the PLC to connect to (default: "127.0.0.1").</summary>
    public string RemoteIP { get; set; } = "127.0.0.1";
    /// <summary>Port to connect to (default: 102, ISO-on-TCP).</summary>
    public int RemotePort { get; set; } = 102;

    /// <summary>CPU rack number, 0 to 7 (default: 0). Not used if <see cref="RemoteTsap"/> is specified.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Outside 0 to 7.</exception>
    public int Rack { get => rack; set => rack = Check(value, 0, 7, nameof(Rack)); }
    /// <summary>CPU slot number, 0 to 31 (default: 1 = S7-1200/1500; 2 for S7-300, 3 for S7-400). Not used if <see cref="RemoteTsap"/> is specified.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Outside 0 to 31.</exception>
    public int Slot { get => slot; set => slot = Check(value, 0, 31, nameof(Slot)); }
    /// <summary>Connection type (default: PG). It becomes the first byte of the remote TSAP.</summary>
    /// <exception cref="ArgumentOutOfRangeException">An undefined value.</exception>
    public S7ConnectionType ConnectionType
    {
        get => connectionType;
        set => connectionType = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(ConnectionType), value, "PG, OP, Basic 중 하나여야 합니다.");
    }
    /// <summary>Specifies the remote (PLC) TSAP directly, 0 to 0xFFFF (default: null = <see cref="ConnectionType"/> x 256 + <see cref="Rack"/> x 32 + <see cref="Slot"/>).
    /// Use it for devices that define the TSAP separately, such as the S7-200 SMART and LOGO!.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Outside 0 to 0xFFFF.</exception>
    public int? RemoteTsap { get => remoteTsap; set => remoteTsap = value is int v ? Check(v, 0, 0xFFFF, nameof(RemoteTsap)) : null; }
    /// <summary>Own (local) TSAP, 0 to 0xFFFF (default: 0x0100). The S7-300/400/1200/1500 usually accept any value.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Outside 0 to 0xFFFF.</exception>
    public int LocalTsap { get => localTsap; set => localTsap = Check(value, 0, 0xFFFF, nameof(LocalTsap)); }
    /// <summary>PDU size requested at connection, 240 to 960 bytes (default: 960). The PLC decides the actual size; check it with <see cref="NegotiatedPduSize"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Outside 240 to 960.</exception>
    public int PduSize { get => pduSize; set => pduSize = Check(value, S7Frame.MinPdu, 960, nameof(PduSize)); }
    /// <summary>PDU size negotiated with the PLC on the current connection (0 if not connected). It is the basis for splitting block read/write (for example 240 for S7-300/1200, 960 for S7-1500).</summary>
    public int NegotiatedPduSize => Volatile.Read(ref negotiated);

    /// <summary>Response timeout in ms (default: 1000). A per-job timeout, if given, takes precedence. For a split block read/write, it applies to each piece.</summary>
    public int Timeout { get; set; } = 1000;
    /// <summary>Wait between jobs in ms (default: 10).</summary>
    public int Interval { get; set; } = 10;
    /// <summary>Whether to reconnect at 1-second intervals when the connection could not be made or was lost (default: true).</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>Gets whether the connection to the PLC (TCP, COTP, Setup Communication) is established.</summary>
    public bool IsOpen => NegotiatedPduSize > 0 && (Volatile.Read(ref sock)?.Connected ?? false);
    /// <summary>Gets whether the communication loop is running.</summary>
    public bool IsStart { get; private set; }
    /// <summary>Gets whether the object has been disposed.</summary>
    public bool IsDisposed { get; private set; }
    /// <summary>Gets or sets a user-defined tag.</summary>
    public object? Tag { get; set; }

    static int Check(int value, int min, int max, string name)
        => value < min || value > max ? throw new ArgumentOutOfRangeException(name, value, $"{name}는 {min}~{max} 입니다.") : value;

    // 연결 요청에 쓸 상대 TSAP
    internal int EffectiveRemoteTsap => RemoteTsap ?? ((int)ConnectionType << 8 | Rack * 0x20 + Slot);
    #endregion

    #region Member Variable
    readonly object lifeLock = new();
    readonly object workLock = new();
    readonly List<Work> autoWorks = [];
    readonly List<Work> manualWorks = [];
    readonly Queue<Work> workQueue = new();
    Socket? sock;
    Thread? thread;
    CancellationTokenSource? cts;
    ushort pduRef;
    #endregion

    #region Event
    /// <summary>Raised when a random read (Read) response is received.</summary>
    public event EventHandler<DataEventArgs>? DataReceived;
    /// <summary>Raised once when all pieces of a block read (ReadBlock) have been received.</summary>
    public event EventHandler<BlockEventArgs>? BlockReceived;
    /// <summary>Raised when a write (Write/WriteBlock) succeeds. A block write raises it once after all pieces succeed.</summary>
    public event EventHandler<ResultEventArgs>? WriteReceived;
    /// <summary>Raised when the PLC sends an error response (a header error or an item return code). No retry is made.</summary>
    public event EventHandler<NAKEventArgs>? NAKReceived;
    /// <summary>Raised when there is no response, the content does not match the request, or the connection is lost during a request. Raised on every retry.</summary>
    public event EventHandler<ResultEventArgs>? TimeoutReceived;
    /// <summary>Raised when the connection to the PLC (TCP, COTP, Setup Communication) is established.</summary>
    public event EventHandler? DeviceOpened;
    /// <summary>Raised when the connection is lost.</summary>
    public event EventHandler? DeviceClosed;
    #endregion

    #region Start / Stop
    /// <summary>Connects to the PLC and starts the communication loop. Does nothing if it is already running.</summary>
    /// <remarks>If the connection cannot be made and <see cref="AutoReconnect"/> is false, the loop ends and <see cref="IsStart"/> becomes false.
    /// The connection settings (IP, port, rack, slot, TSAP, PDU size) are read on every connection.</remarks>
    public void Start()
    {
        lock (lifeLock)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (IsStart) return;
            cts = new CancellationTokenSource();
            var token = cts.Token;
            IsStart = true;
            thread = new Thread(() => Run(token)) { IsBackground = true, Name = nameof(S7Ethernet) };
            thread.Start();
        }
    }

    /// <summary>Stops the communication loop and disconnects. Waits up to (Timeout + 1000) ms for the loop to end.</summary>
    /// <remarks>May be called from inside an event handler; in that case it does not wait and returns immediately. Events that were already in progress may still be raised briefly right after Stop() returns.</remarks>
    public void Stop()
    {
        Thread? t;
        CancellationTokenSource? c;
        lock (lifeLock)
        {
            c = cts; cts = null;
            t = thread; thread = null;
            IsStart = false;
            c?.Cancel();
        }
        CloseSocket(Volatile.Read(ref sock));
        if (t != null && t != Thread.CurrentThread) t.Join(Timeout + 1000);
        c?.Dispose();
    }

    /// <summary>Stops communication and releases its resources.</summary>
    public void Dispose()
    {
        if (IsDisposed) return;
        Stop();
        IsDisposed = true;
        GC.SuppressFinalize(this);
    }
    #endregion

    #region Schedule
    internal Work? TakeNextWork()
    {
        lock (workLock)
        {
            if (manualWorks.Count > 0)
            {
                var w = manualWorks[0];
                manualWorks.RemoveAt(0);
                return w;
            }
            if (workQueue.Count == 0)
                foreach (var a in autoWorks) workQueue.Enqueue(a);
            return workQueue.Count > 0 ? workQueue.Dequeue() : null;
        }
    }

    internal List<Work> AutoWorksSnapshot()
    {
        lock (workLock) return [.. autoWorks];
    }

    internal void ReplaceAutoWorks(List<Work> works)
    {
        lock (workLock)
        {
            autoWorks.Clear();
            autoWorks.AddRange(works);
            workQueue.Clear();
        }
    }

    /// <summary>Checks whether the Auto list contains the given message ID.</summary>
    /// <param name="id">Message ID.</param>
    /// <returns>true if it exists.</returns>
    public bool ContainAutoID(int id) { lock (workLock) return autoWorks.Exists(x => x.MessageID == id); }

    /// <summary>Removes all jobs with the given message ID from the Auto list.</summary>
    /// <param name="id">Message ID.</param>
    /// <returns>true if any job was removed.</returns>
    public bool RemoveAuto(int id) { lock (workLock) return autoWorks.RemoveAll(x => x.MessageID == id) > 0; }

    /// <summary>Removes all not-yet-executed Manual jobs with the given message ID.</summary>
    /// <param name="id">Message ID.</param>
    /// <returns>true if any job was removed.</returns>
    public bool RemoveManual(int id) { lock (workLock) return manualWorks.RemoveAll(x => x.MessageID == id) > 0; }

    /// <summary>Clears the Auto list.</summary>
    public void ClearAuto() { lock (workLock) autoWorks.Clear(); }
    /// <summary>Clears the pending Manual jobs.</summary>
    public void ClearManual() { lock (workLock) manualWorks.Clear(); }
    /// <summary>Clears the Auto job queue remaining in the current pass. It is refilled on the next pass.</summary>
    public void ClearWorkSchedule() { lock (workLock) workQueue.Clear(); }

    static S7Address[] ParseItems(IReadOnlyList<string> addresses, string paramName)
    {
        ArgumentNullException.ThrowIfNull(addresses, paramName);
        if (addresses.Count < 1 || addresses.Count > S7Address.MaxItems)
            throw new ArgumentException($"주소는 1~{S7Address.MaxItems}개입니다: {addresses.Count}개", paramName);
        return [.. addresses.Select(a => S7Address.Parse(a, paramName))];
    }

    internal Work ReadWork(int id, IReadOnlyList<string> addresses, int? repeatCount, int? timeout)
    {
        var items = ParseItems(addresses, nameof(addresses));
        if (!S7Frame.ReadFits([.. items.Select(a => a.Width)]))
            throw new ArgumentException($"요청이나 응답이 최소 PDU {S7Frame.MinPdu}바이트를 넘습니다. 주소를 나눠서 등록하세요(예: 워드는 19개까지).", nameof(addresses));
        return new Work(id, S7Command.Read, items, 0, S7Frame.ReadParam([.. items.Select(a => a.ToItem())]), [], repeatCount, timeout);
    }

    Work WriteWork(int id, IReadOnlyList<S7Value> values, int? repeatCount, int? timeout)
    {
        ArgumentNullException.ThrowIfNull(values);
        var items = ParseItems([.. values.Select(v => v.Address)], nameof(values));
        for (int i = 0; i < items.Length; i++) items[i].CheckValue(values[i].Value, nameof(values));
        if (!S7Frame.WriteFits([.. items.Select(a => a.Width)]))
            throw new ArgumentException($"요청이 최소 PDU {S7Frame.MinPdu}바이트를 넘습니다. 값을 나눠서 쓰세요(예: 비트는 12개까지).", nameof(values));
        var (param, data) = S7Frame.WriteParam([.. items.Select((a, i) => (a.ToItem(), a.Encode(values[i].Value)))]);
        return new Work(id, S7Command.Write, items, 0, param, data, repeatCount, timeout);
    }

    internal Work ReadBlockWork(int id, string address, int count, int? repeatCount, int? timeout)
    {
        var a = S7Address.ParseBlockStart(address, nameof(address));
        if (count < 1 || count > S7Address.MaxBlock) throw new ArgumentOutOfRangeException(nameof(count), $"바이트 수는 1~{S7Address.MaxBlock} 입니다: {count}");
        return new Work(id, S7Command.ReadBlock, [a], count, [], [], repeatCount, timeout);
    }

    Work WriteBlockWork(int id, string address, IReadOnlyList<byte> data, int? repeatCount, int? timeout)
    {
        ArgumentNullException.ThrowIfNull(data);
        var a = S7Address.ParseBlockStart(address, nameof(address));
        if (data.Count < 1 || data.Count > S7Address.MaxBlock) throw new ArgumentOutOfRangeException(nameof(data), $"바이트 수는 1~{S7Address.MaxBlock} 입니다: {data.Count}");
        return new Work(id, S7Command.WriteBlock, [a], data.Count, [], [.. data], repeatCount, timeout);
    }

    // 이름 있는 쓰기 표. NamedWrite는 실행 중에도 불리므로 통째로 바꾸는 참조(volatile)로 둔다. ApplyConfig에서만 교체한다.
    volatile NamedWriteTable<S7WriteConfig> namedWrites = NamedWriteTable<S7WriteConfig>.Empty;

    void AddAuto(Work w) { lock (workLock) autoWorks.Add(w); }
    void AddManual(Work w) { lock (workLock) manualWorks.Add(w); }
    #endregion

    #region Auto / Manual
    /// <summary>Registers a random read (Read) in the Auto list. The response arrives through <see cref="DataReceived"/>.</summary>
    /// <param name="id">Message ID (the MessageID of the response event).</param>
    /// <param name="addresses">At most 19 addresses of any size (for example ["DB1.DBW0", "M10.0", "IB0"]); the request and response must fit in 240 bytes, so 20 is rejected.</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    /// <exception cref="ArgumentException">An address format is invalid, or the count or size limit is exceeded.</exception>
    public void AutoRead(int id, IReadOnlyList<string> addresses, int? timeout = null)
        => AddAuto(ReadWork(id, addresses, null, timeout));

    /// <summary>Registers a block read (ReadBlock) in the Auto list. The response arrives through <see cref="BlockReceived"/>.</summary>
    /// <param name="id">Message ID (the MessageID of the response event).</param>
    /// <param name="address">Start byte address (DB1.DBB0, MB0, IB0/EB0, QB0/AB0).</param>
    /// <param name="count">Number of bytes to read, 1 to 65535 (read in pieces that fit the negotiated PDU).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>; applied to each piece).</param>
    /// <exception cref="ArgumentException">The address is not a byte address, or the byte count is out of range.</exception>
    public void AutoReadBlock(int id, string address, int count, int? timeout = null)
        => AddAuto(ReadBlockWork(id, address, count, null, timeout));

    /// <summary>Registers a one-shot random read (Read). It is processed before Auto jobs.</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="addresses">At most 19 addresses of any size; the request and response must fit in 240 bytes, so 20 is rejected.</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    /// <exception cref="ArgumentException">An address format is invalid, or the count or size limit is exceeded.</exception>
    public void ManualRead(int id, IReadOnlyList<string> addresses, int? repeatCount = null, int? timeout = null)
        => AddManual(ReadWork(id, addresses, repeatCount, timeout));

    /// <summary>Registers a one-shot block read (ReadBlock). It is processed before Auto jobs.</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="address">Start byte address (DB1.DBB0, MB0, IB0/EB0, QB0/AB0).</param>
    /// <param name="count">Number of bytes to read, 1 to 65535.</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries). A retry starts again from the first piece.</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>; applied to each piece).</param>
    /// <exception cref="ArgumentException">The address is not a byte address, or the byte count is out of range.</exception>
    public void ManualReadBlock(int id, string address, int count, int? repeatCount = null, int? timeout = null)
        => AddManual(ReadBlockWork(id, address, count, repeatCount, timeout));

    /// <summary>Registers a one-shot random write (Write). On success, <see cref="WriteReceived"/> is raised.</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="values">Address and value pairs. A bit accepts only 0/1; otherwise a value within the size range (negative values as two's complement). The request must fit in 240 bytes: at most 12 bits, bytes, or words, or at most 11 double words.</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    /// <exception cref="ArgumentException">An address or value is invalid, or the count or size limit is exceeded.</exception>
    public void ManualWrite(int id, IReadOnlyList<S7Value> values, int? repeatCount = null, int? timeout = null)
        => AddManual(WriteWork(id, values, repeatCount, timeout));

    /// <summary>Registers a one-shot block write (WriteBlock). When all pieces succeed, <see cref="WriteReceived"/> is raised once.</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="address">Start byte address (DB1.DBB0, MB0, IB0/EB0, QB0/AB0).</param>
    /// <param name="data">1 to 65535 bytes to write (copied at registration). Written in pieces that fit the negotiated PDU; not atomic.</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries). A retry starts again from the first piece.</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>; applied to each piece).</param>
    /// <exception cref="ArgumentException">The address is not a byte address, or the byte count is out of range.</exception>
    public void ManualWriteBlock(int id, string address, IReadOnlyList<byte> data, int? repeatCount = null, int? timeout = null)
        => AddManual(WriteBlockWork(id, address, data, repeatCount, timeout));

    /// <summary>Registers a write of one value by the name defined in the XML &lt;Write&gt; element. It is sent as a random write (Write), the same as <see cref="ManualWrite"/>.</summary>
    /// <param name="name">The Name of the &lt;Write&gt; element.</param>
    /// <param name="value">The value to write. A bit accepts only 0/1; otherwise a value within the size range (negative values as two's complement).</param>
    /// <param name="repeatCount">Retry count (if null, the RepeatCount in the XML; if that is absent too, no retries).</param>
    /// <param name="timeout">Timeout in ms (if null, the Timeout in the XML; if that is absent too, <see cref="Timeout"/>).</param>
    /// <exception cref="ArgumentException">The name does not exist, or a value is invalid.</exception>
    public void NamedWrite(string name, long value, int? repeatCount = null, int? timeout = null)
    {
        var table = namedWrites;
        var item = table.Get(name);
        var (r, t) = table.Resolve(item, repeatCount, timeout);
        AddManual(WriteWork(item.Id, [new S7Value(item.Address, value)], r, t));
    }

    /// <summary>Registers a block write of several values by the name defined in the XML &lt;Write&gt; element.
    /// The values are concatenated as big-endian bytes according to the address size (B 1 byte, W 2 bytes, D 4 bytes) and sent as a block write (WriteBlock) to the byte address of the same area and start byte.</summary>
    /// <remarks>A bit (X) address cannot be used to write several values. A block write is split to fit the PDU size and is not atomic (see <see cref="ManualWriteBlock"/>).</remarks>
    /// <param name="name">The Name of the &lt;Write&gt; element.</param>
    /// <param name="values">The values to write. B is 0 to 255, W is -32768 to 65535, D is -2147483648 to 4294967295.</param>
    /// <param name="repeatCount">Retry count (if null, the RepeatCount in the XML; if that is absent too, no retries).</param>
    /// <param name="timeout">Timeout in ms (if null, the Timeout in the XML; if that is absent too, <see cref="Timeout"/>).</param>
    /// <exception cref="ArgumentException">The name does not exist, the address is a bit address, a value is out of range, or the byte count is outside 1 to 65535.</exception>
    public void NamedWrite(string name, IReadOnlyList<long> values, int? repeatCount = null, int? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        var table = namedWrites;
        var item = table.Get(name);
        var (r, t) = table.Resolve(item, repeatCount, timeout);
        var a = S7Address.Parse(item.Address, nameof(item.Address));
        if (a.IsBit) throw new ArgumentException($"비트 주소는 값 여러 개를 쓸 수 없습니다: \"{name}\" ({item.Address})", nameof(values));
        var bytes = new List<byte>(values.Count * a.Width);
        foreach (var v in values)
        {
            a.CheckValue(v, nameof(values));
            if (a.Size == 'B' && v < 0) throw new ArgumentOutOfRangeException(nameof(values), $"{a.Name} 값 범위는 0~255 입니다: {v}");
            bytes.AddRange(a.Encode(v));
        }
        AddManual(WriteBlockWork(item.Id, a.ByteStartName(), bytes, r, t));
    }

    /// <summary>Checks whether a named write with the given name was loaded from the XML. Names are case-sensitive.</summary>
    /// <param name="name">The Name of the &lt;Write&gt; element.</param>
    /// <returns>true if it exists.</returns>
    public bool ContainsNamedWrite(string name) => namedWrites.Contains(name);
    #endregion

    #region Config / Xml
    /// <summary>Builds a configuration object from the current communication settings, Auto list, and named write list. Manual jobs are not included.</summary>
    /// <returns>The configuration object.</returns>
    public S7EthernetConfig ToConfig()
    {
        var c = new S7EthernetConfig
        {
            RemoteIP = RemoteIP,
            RemotePort = RemotePort,
            Rack = Rack,
            Slot = Slot,
            ConnectionType = ConnectionType,
            LocalTsap = LocalTsap,
            PduSize = PduSize,
            Timeout = Timeout,
            Interval = Interval,
            AutoReconnect = AutoReconnect,
            Autos = [.. AutoWorksSnapshot().Select(w =>
            {
                S7EthernetAutoConfig a = w.Command == S7Command.Read
                    ? new S7ReadConfig { Id = w.MessageID, Addresses = string.Join(",", w.Addresses) }
                    : new S7ReadBlockConfig { Id = w.MessageID, Address = w.Items[0].Name, Count = w.Count };
                if (w.Timeout is int t) a.Timeout = t;
                return a;
            })],
            Writes = namedWrites.Snapshot(),
        };
        if (RemoteTsap is int tsap) c.RemoteTsap = tsap;
        return c;
    }

    /// <summary>Applies the communication settings, Auto list, and named write list of the configuration object. The existing Auto list and named writes are replaced by those of the configuration.</summary>
    /// <remarks>The whole input is validated before it is applied, so if any single value is invalid, nothing is changed.</remarks>
    /// <param name="config">The configuration to apply.</param>
    /// <exception cref="InvalidOperationException">Called while communicating (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">A value is invalid (the message includes the element and attribute names).</exception>
    public void ApplyConfig(S7EthernetConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (IsStart) throw new InvalidOperationException("통신 중에는 설정을 바꿀 수 없습니다. Stop() 후 호출하세요.");
        config.Validate();
        var works = new List<Work>();
        foreach (var a in config.Autos ?? [])
        {
            var label = $"{(a is S7ReadConfig ? "Read" : "ReadBlock")} Id=\"{a.Id}\"";
            if (a.TimeoutSpecified) XmlConfig.Range(label, nameof(a.Timeout), a.Timeout, 1, 60000);
            int? t = a.TimeoutSpecified ? a.Timeout : null;
            try
            {
                works.Add(a switch
                {
                    S7ReadConfig r => ReadWork(a.Id, S7Address.SplitNames(r.Addresses), null, t),
                    S7ReadBlockConfig b => ReadBlockWork(a.Id, b.Address, b.Count, null, t),
                    _ => throw new FormatException($"알 수 없는 Auto 항목: {a.GetType().Name}"),
                });
            }
            catch (ArgumentException ex)
            {
                throw XmlConfig.Invalid(label, ex, new Dictionary<string, (string, string)>
                {
                    ["addresses"] = ("Addresses", (a as S7ReadConfig)?.Addresses ?? ""),
                    ["address"] = ("Address", (a as S7ReadBlockConfig)?.Address ?? ""),
                    ["count"] = ("Count", (a as S7ReadBlockConfig)?.Count.ToString() ?? ""),
                });
            }
        }

        var table = NamedWriteTable<S7WriteConfig>.Build(config.Writes, (w, label) =>
        {
            try
            {
                WriteWork(w.Id, [new S7Value(w.Address, 0)], null, null);
            }
            catch (ArgumentException ex)
            {
                throw XmlConfig.Invalid(label, ex, new Dictionary<string, (string, string)> { ["values"] = (nameof(w.Address), w.Address ?? "") });
            }
        });

        RemoteIP = config.RemoteIP;
        RemotePort = config.RemotePort;
        Rack = config.Rack;
        Slot = config.Slot;
        ConnectionType = config.ConnectionType;
        RemoteTsap = config.RemoteTsapSpecified ? config.RemoteTsap : null;
        LocalTsap = config.LocalTsap;
        PduSize = config.PduSize;
        Timeout = config.Timeout;
        Interval = config.Interval;
        AutoReconnect = config.AutoReconnect;
        namedWrites = table;
        ReplaceAutoWorks(works);
    }

    /// <summary>Builds an XML element from the current settings, Auto list, and named write list. Manual jobs are not included.</summary>
    /// <returns>The &lt;S7Ethernet&gt; element.</returns>
    public XElement ToXml() => XmlConfig.Serialize(ToConfig());

    /// <summary>Loads the settings, Auto list, and named write list from an XML element. The existing Auto list and named writes are replaced by the XML content.</summary>
    /// <remarks>
    /// <para>Omitted attributes use their default values. An unknown attribute or element is an error (to catch typos).</para>
    /// <para>The whole input is validated before it is applied, so if any single value is invalid, nothing is changed.</para>
    /// <para>You can check the format as an XSD by passing <see cref="S7EthernetConfig"/> to <see cref="ConfigSchema.Get{T}"/>.</para>
    /// </remarks>
    /// <param name="element">The &lt;S7Ethernet&gt; element.</param>
    /// <exception cref="InvalidOperationException">Called while communicating (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">The root name differs, an unknown attribute or element is present, or a value is invalid.</exception>
    public void FromXml(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (IsStart) throw new InvalidOperationException("통신 중에는 설정을 불러올 수 없습니다. Stop() 후 호출하세요.");
        ApplyConfig(XmlConfig.Deserialize<S7EthernetConfig>(element));
    }

    /// <summary>Saves the settings, Auto list, and named write list to an XML file.</summary>
    /// <param name="path">File path.</param>
    public void SaveXml(string path) => ToXml().Save(path);

    /// <summary>Loads the settings, Auto list, and named write list from an XML file. The rules are the same as <see cref="FromXml"/>.</summary>
    /// <param name="path">File path.</param>
    public void LoadXml(string path) => FromXml(XElement.Load(path));
    #endregion

    #region Loop
    void Run(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var s = TryConnect(token);
                if (s != null)
                {
                    int pdu = NegotiatedPduSize;   // 이 연결에서 연속 읽기/쓰기를 나누는 기준
                    try
                    {
                        Raise(DeviceOpened);
                        try
                        {
                            while (!token.IsCancellationRequested)
                            {
                                var w = TakeNextWork();
                                if (w != null) ProcessWork(s, w, pdu);
                                if (token.IsCancellationRequested) break;
                                token.WaitHandle.WaitOne(w != null ? Interval : Math.Max(Interval, 10));
                            }
                        }
                        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException) { }
                    }
                    finally
                    {
                        CloseSocket(s);
                        Raise(DeviceClosed);
                    }
                }
                if (!AutoReconnect || token.IsCancellationRequested) break;
                token.WaitHandle.WaitOne(1000);
            }
        }
        catch (Exception) { }
        finally
        {
            lock (lifeLock)
            {
                if (thread == Thread.CurrentThread) { IsStart = false; thread = null; }
            }
        }
    }

    // TCP 접속 → COTP CR/CC → Setup Communication. 하나라도 실패하면 소켓을 닫고 null.
    Socket? TryConnect(CancellationToken token)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            int timeout = Math.Max(Timeout, 1000);
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                linked.CancelAfter(timeout);
                s.ConnectAsync(RemoteIP, RemotePort, linked.Token).AsTask().GetAwaiter().GetResult();
            }
            Volatile.Write(ref sock, s);   // 연결 설정 중에도 Stop()이 소켓을 닫아 바로 끝낼 수 있게
            var pdu = Handshake(s, timeout) ?? throw new IOException("S7 연결 설정(COTP 또는 Setup Communication)에 실패했습니다.");
            Volatile.Write(ref negotiated, pdu);
            return s;
        }
        catch
        {
            CloseSocket(s);
            return null;
        }
    }

    // 협상한 PDU 크기. CC가 아니거나, Setup 응답이 오류이거나 240보다 작으면 null.
    int? Handshake(Socket s, int timeout)
    {
        var sw = Stopwatch.StartNew();
        byte[]? Read(int n) => ReceiveExact(s, n, timeout, sw);
        s.Send(S7Frame.ConnectionRequest(LocalTsap, EffectiveRemoteTsap));
        var cc = S7Frame.ReadTpkt(Read);
        if (cc == null || !S7Frame.IsConnectionConfirm(cc)) return null;

        int requested = PduSize;
        ushort r = ++pduRef;
        s.Send(S7Frame.Job(r, S7Frame.SetupParam(requested), []));
        while (true)
        {
            var pdu = S7Frame.ReadPdu(Read);
            if (pdu == null) return null;
            if (S7Frame.ParseHeader(pdu) is not { } h) return null;
            if (h.PduRef != r) continue;
            return S7Frame.ParseSetup(pdu) is int v && v >= S7Frame.MinPdu ? Math.Min(requested, v) : null;
        }
    }

    void CloseSocket(Socket? s)
    {
        if (s == null) return;
        if (Interlocked.CompareExchange(ref sock, null, s) == s) Volatile.Write(ref negotiated, 0);
        try { s.Shutdown(SocketShutdown.Both); } catch { }
        s.Dispose();
    }

    static ResultEventArgs Result(Work w) => new(w.MessageID, w.Command);

    void ProcessWork(Socket s, Work w, int pdu)
    {
        try
        {
            int timeout = w.Timeout ?? Timeout;
            for (int attempt = 0; ; attempt++)
            {
                if (Execute(s, w, timeout, pdu) is S7Status.Ack or S7Status.Nak) return;
                Raise(TimeoutReceived, Result(w));
                if (attempt >= (w.RepeatCount ?? 0)) return;
            }
        }
        catch
        {
            // 연결 오류로 작업이 중단되면 조용히 사라지지 않게 알린 뒤, 재접속 경로가 돌도록 다시 던진다
            Raise(TimeoutReceived, Result(w));
            throw;
        }
    }

    // 작업 한 번 실행. Ack/Nak이면 이벤트까지 발생시킨다. 응답이 없거나 내용이 맞지 않으면 Invalid(타임아웃 경로).
    S7Status Execute(Socket s, Work w, int timeout, int pdu)
    {
        switch (w.Command)
        {
            case S7Command.Read:
                {
                    var r = Parse(Exchange(s, w.Param, [], timeout), res => S7Frame.ParseRead(res, [.. w.Items.Select(a => a.Width)]));
                    if (r.Status == S7Status.Ack)
                        Raise(DataReceived, new DataEventArgs(w.MessageID, w.Command, w.Addresses, [.. w.Items.Select((a, i) => a.Decode(r.Items[i]))]));
                    return Finish(w, r);
                }
            case S7Command.Write:
                {
                    var r = Parse(Exchange(s, w.Param, w.Data, timeout), res => S7Frame.ParseWrite(res, w.Items.Length));
                    if (r.Status == S7Status.Ack) Raise(WriteReceived, Result(w));
                    return Finish(w, r);
                }
            default:
                {
                    var start = w.Items[0];
                    bool read = w.Command == S7Command.ReadBlock;
                    int chunk = read ? S7Frame.ReadChunk(pdu) : S7Frame.WriteChunk(pdu);
                    var buf = new byte[w.Count];
                    for (int off = 0; off < w.Count; off += chunk)
                    {
                        int n = Math.Min(chunk, w.Count - off);
                        var item = S7Item.Bytes(start.Area, start.Db, start.Byte + off, n);
                        S7Response r;
                        if (read)
                        {
                            r = Parse(Exchange(s, S7Frame.ReadParam([item]), [], timeout), res => S7Frame.ParseRead(res, [n]));
                            if (r.Status == S7Status.Ack) r.Items[0].CopyTo(buf, off);
                        }
                        else
                        {
                            var (param, data) = S7Frame.WriteParam([(item, w.Data[off..(off + n)])]);
                            r = Parse(Exchange(s, param, data, timeout), res => S7Frame.ParseWrite(res, 1));
                        }
                        if (r.Status != S7Status.Ack) return Finish(w, r);
                    }
                    if (read) Raise(BlockReceived, new BlockEventArgs(w.MessageID, w.Command, start.Name, buf));
                    else Raise(WriteReceived, Result(w));
                    return S7Status.Ack;
                }
        }
    }

    static S7Response Parse(byte[]? pdu, Func<byte[], S7Response> parse) => pdu == null ? S7Response.Invalid : parse(pdu);

    S7Status Finish(Work w, S7Response r)
    {
        if (r.Status == S7Status.Nak) Raise(NAKReceived, new NAKEventArgs(w.MessageID, w.Command, r.ErrorCode, r.ItemIndex));
        return r.Status;
    }

    // 요청을 보내고 PDU 참조가 맞는 응답 S7 PDU를 돌려준다. 시간 안에 못 받으면 null.
    byte[]? Exchange(Socket s, byte[] param, byte[] data, int timeout)
    {
        ushort r = ++pduRef;
        var request = S7Frame.Job(r, param, data);
        Drain(s);
        s.Send(request);

        var sw = Stopwatch.StartNew();
        while (true)
        {
            var pdu = S7Frame.ReadPdu(n => ReceiveExact(s, n, timeout, sw));
            if (pdu == null) return null;
            var h = S7Frame.ParseHeader(pdu) ?? throw new IOException("S7 응답 헤더가 올바르지 않습니다.");   // 스트림이 깨짐 → 재접속
            if (h.PduRef == r) return pdu;
            // 이전 요청의 늦은 응답 등 짝이 맞지 않으면 버리고 계속 기다린다
        }
    }

    static byte[]? ReceiveExact(Socket s, int count, int timeout, Stopwatch sw)
    {
        var buf = new byte[count];
        int n = 0;
        while (n < count)
        {
            int remain = timeout - (int)sw.ElapsedMilliseconds;
            if (remain <= 0 || !s.Poll(remain * 1000, SelectMode.SelectRead)) return null;
            int r = s.Receive(buf, n, count - n, SocketFlags.None);
            if (r == 0) throw new IOException("상대가 연결을 끊었습니다.");
            n += r;
        }
        return buf;
    }

    static void Drain(Socket s)
    {
        if (s.Available == 0) return;
        var tmp = new byte[s.Available];
        while (s.Available > 0) s.Receive(tmp, 0, Math.Min(tmp.Length, s.Available), SocketFlags.None);
    }
    #endregion

    #region Raise
    void Raise(EventHandler? handler)
    {
        if (handler == null) return;
        try { handler(this, EventArgs.Empty); } catch { }
    }

    void Raise<T>(EventHandler<T>? handler, T args)
    {
        if (handler == null) return;
        try { handler(this, args); } catch { }
    }
    #endregion
}
