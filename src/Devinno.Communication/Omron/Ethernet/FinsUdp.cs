using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Xml.Linq;
using Devinno.Communication.Internal;
using Devinno.Communication.Omron.Data;

namespace Devinno.Communication.Omron.Ethernet;

/// <summary>
/// Master that exchanges memory area reads/writes (0101/0102/0104) with an Omron PLC over FINS/UDP (default port 9600).
/// Targets the CS1/CJ1 (ETN units), CJ2, the built-in ports of the CP1L-E/CP1E-N□□S, and NJ/NX (CJ memory).
/// </summary>
/// <remarks>
/// <para><b>Node numbers</b>: The destination node (DA1) is <see cref="DestinationNode"/> (default: the last number of <see cref="RemoteIP"/>),
/// and the source node (SA1) is <see cref="SourceNode"/> (default: the last number of the IP of this PC that routes to the PLC). The FINS node number of the PLC is usually set equal to the last number of its IP (automatic generation method).</para>
/// <para><b>Response port</b>: If the IP address conversion of the PLC is "automatic generation (dynamic)", the PLC replies to the port that sent the request, so <see cref="LocalPort"/> is 0 (any port).
/// If it is "automatic generation (static)", the PLC replies to port 9600 of the IP (its own network + SA1), so <see cref="LocalPort"/> must be 9600 and <see cref="SourceNode"/> must be the last number of the IP of this PC.</para>
/// <para>Addresses (case-insensitive): CIO100 or 100 (CIO), W10, H20, A100, D100 (DM100 is also accepted), E0_100 (EM bank 0; the bank is hexadecimal, 0 to 18).
/// For bits, append ".00" to ".15" (CIO100.05, 100.05, W10.01, D100.15). Events return the canonical form (e.g., "CIO100.05", "D100").
/// Word ranges are CIO 0 to 6143, W and H 0 to 511, A 0 to 959, and D and EM 0 to 32767. Timers and counters (T/C) are not supported.
/// On NJ/NX, a variable must be assigned to CJ memory with an AT specification (e.g., %D0) for its value to be visible.</para>
/// <para>Values are <see cref="long"/>. A word read is a raw value of 0 to 65535 and a bit is 0/1. A word to write must be -32768 to 65535 (negative values are sent as two's complement), and a bit accepts only 0/1.</para>
/// <para>A random (individual) read (Read) reads 1 to 167 addresses in one 0104 request. A block read/write (ReadBlock/WriteBlock) covers 1 to 65535 items
/// and is split into requests of <see cref="MaxWordsPerRequest"/> items each (default 499 = the CP1L-E limit; up to 997 for CJ2 and NJ). Bits are split by the same count.
/// The event is raised once only if every chunk succeeds; if any chunk fails, only <see cref="NAKReceived"/> is raised. A block write that was split is not atomic.</para>
/// <para>A response is matched to its request by SID (incremented by 1 for each request), the FINS addresses, and the sender (<see cref="RemoteIP"/>:<see cref="RemotePort"/>); a response that does not match is discarded (such as a late response to an earlier request).
/// Therefore, responses that come from anywhere other than <see cref="RemoteIP"/>:<see cref="RemotePort"/> are not accepted. For a PLC with several network cards or a PLC behind NAT,
/// set <see cref="RemoteIP"/> to the address from which the PLC actually sends its responses.
/// If the PLC sends an error end code, only <see cref="NAKReceived"/> is raised and no retry is made. The CPU error flags in the end code (0x0040 non-fatal, 0x0080 fatal) are not treated as errors (e.g., it is processed normally even during a battery error).</para>
/// <para>UDP is connectionless. <see cref="DeviceOpened"/> is raised when the socket is opened, and if the PLC is powered off, each request times out.</para>
/// <para>Manual jobs are processed before Auto jobs. The communication loop runs on a dedicated background thread, and all events are raised on that thread.
/// To update the UI, marshal asynchronously, as with BeginInvoke (WinForms) or Dispatcher.BeginInvoke (WPF). Exceptions thrown in a handler are ignored and the loop continues.</para>
/// <para>Write safety: with <see cref="DiscardWritesWhileDisconnected"/> true (the default), a Manual write (Write/WriteBlock, including <see cref="NamedWrite(string, long, int?, int?)"/>) requested while there is no connection is not queued, and writes still queued when the connection is lost are discarded right after <see cref="DeviceClosed"/>; both are reported through <see cref="WriteDiscarded"/>, which may also be raised on the calling thread. <see cref="Stop"/> always discards pending Manual writes. Manual reads and Auto jobs are never discarded. Each write ends with exactly one of: <see cref="WriteReceived"/>, <see cref="NAKReceived"/>, a <see cref="TimeoutReceived"/> with IsFinal true, or <see cref="WriteDiscarded"/> (jobs removed with <see cref="ClearManual"/> or <see cref="RemoveManual"/> are not reported). A block write that fails midway reports the items (words or bits) the PLC had already acknowledged in that attempt as WrittenCount (<see cref="NAKEventArgs.WrittenCount"/>, <see cref="AttemptFailedEventArgs.WrittenCount"/>).</para>
/// <para>UDP is connectionless, so for this class "connected" means the socket is open (bound): <see cref="DeviceOpened"/> is the moment writes start to be accepted, and it says nothing about whether the PLC responds.</para>
/// </remarks>
/// <example>
/// <code>
/// using Devinno.Communication.Omron.Data;      // FinsCommand
/// using Devinno.Communication.Omron.Ethernet;
///
/// var plc = new FinsUdp { RemoteIP = "192.168.250.1" };   // DA1 = 1, SA1 = last number of this PC's IP
/// plc.AutoRead(1, ["D100", "CIO0.05", "W10"]);
/// plc.AutoReadBlock(2, "D1000", 800);   // read in two chunks: 499 words + 301 words
/// plc.DataReceived += (s, e) =&gt;
/// {
///     if (e.MessageID == 1) Console.WriteLine($"D100 = {(short)e.Values[0]}, CIO0.05 = {e.Values[1]}");
///     else Console.WriteLine($"{e.Values.Length} words from {e.Addresses[0]}");
/// };
/// plc.NAKReceived += (s, e) =&gt; Console.WriteLine($"End code 0x{e.ErrorCode:X4}");   // e.g., 0x1103 address range, 0x2102 write-protected
/// plc.DeviceOpened += (s, e) =&gt;
/// {
///     // writes are accepted only while the socket is open (otherwise WriteDiscarded, NotConnected); runs again on every (re)connect
///     plc.ManualWrite(10, "D200", -1);
///     plc.ManualWriteBlock(11, "CIO100.00", [1, 0, 1, 1]);   // 4 bits
///     plc.NamedWrite("Speed", 1500);   // calls the XML &lt;Write Name="Speed" Id="20" Address="D200" /&gt; by name
/// };
/// plc.Start();
/// </code>
/// </example>
public class FinsUdp : IDisposable
{
    #region class : Work
    internal sealed class Work(int messageId, FinsCommand command, FinsAddress[] items, int count, long[] values, int? repeatCount, int? timeout)
    {
        public int MessageID { get; } = messageId;
        public FinsCommand Command { get; } = command;
        // Read는 요청한 주소들, 나머지는 시작 주소 1개
        public FinsAddress[] Items { get; } = items;
        // Read는 주소 수, Write는 1, 블록은 워드·비트 수
        public int Count { get; } = count;
        // Write/WriteBlock의 쓸 값 (읽기는 빈 배열)
        public long[] Values { get; } = values;
        public int? RepeatCount { get; } = repeatCount;
        public int? Timeout { get; } = timeout;
        public string[] Addresses => [.. Items.Select(a => a.Name)];
        // 연결이 끊긴 동안 버리는 대상(물리 동작이 있는 쓰기)
        public bool IsWrite => Command is FinsCommand.Write or FinsCommand.WriteBlock;
    }
    #endregion

    #region class : EventArgs
    /// <summary>Common event arguments for request result events. Write response events use this type as-is.</summary>
    /// <param name="messageId">The message ID given at registration.</param>
    /// <param name="command">Request type.</param>
    public class ResultEventArgs(int messageId, FinsCommand command) : EventArgs
    {
        /// <summary>The message ID given at registration.</summary>
        public int MessageID { get; } = messageId;
        /// <summary>Request type.</summary>
        public FinsCommand Command { get; } = command;
    }

    /// <summary>Event arguments for a read (Read/ReadBlock) response.</summary>
    /// <param name="messageId">Message ID.</param>
    /// <param name="command">Request type (Read or ReadBlock).</param>
    /// <param name="addresses">For Read, the requested addresses; for ReadBlock, the single start address (canonical form, e.g., "D100", "CIO100.05").</param>
    /// <param name="values">Values read. Words are raw values of 0 to 65535 and bits are 0/1. For ReadBlock, in order from the start address.</param>
    public class DataEventArgs(int messageId, FinsCommand command, string[] addresses, long[] values)
        : ResultEventArgs(messageId, command)
    {
        /// <summary>For Read, the requested addresses; for ReadBlock, the single start address (canonical form, e.g., "D100", "CIO100.05").</summary>
        public string[] Addresses { get; } = addresses;
        /// <summary>Values read. Words are raw values of 0 to 65535 (convert with (short) if a signed value is needed) and bits are 0/1. For ReadBlock, in order from the start address.</summary>
        public long[] Values { get; } = values;
    }

    /// <summary>Event arguments for when the PLC sends an error end code.</summary>
    /// <param name="messageId">Message ID.</param>
    /// <param name="command">Request type.</param>
    /// <param name="errorCode">FINS end code (MRES x 256 + SRES, with the CPU error flags 0x0040/0x0080 and the relay error bit 0x8000 removed).</param>
    public class NAKEventArgs(int messageId, FinsCommand command, int errorCode)
        : ResultEventArgs(messageId, command)
    {
        /// <summary>FINS end code (with the CPU error flags and relay error bit removed). Examples: 0x0401 unsupported command, 0x1101 area code error, 0x1103 address range error, 0x2101 read-only, 0x2102 write-protected.</summary>
        public int ErrorCode { get; } = errorCode;
        /// <summary>For a block write (WriteBlock) split into chunks, the number of items (words or bits) in the chunks the PLC had acknowledged in this attempt before the error; 0 for other requests and when the first chunk failed.</summary>
        public int WrittenCount { get; init; }
    }

    /// <summary>Event arguments for a failed attempt (<see cref="TimeoutReceived"/>).</summary>
    /// <param name="messageId">The message ID given at registration.</param>
    /// <param name="command">Request type.</param>
    /// <param name="attempt">Attempt number, starting at 1 (1 = first attempt, 2 = first retry, ...).</param>
    /// <param name="isFinal">Whether this job will not be attempted again (retries used up or a socket error occurred).</param>
    /// <param name="requestSent">Whether the request of this attempt was handed to the send function.</param>
    public class AttemptFailedEventArgs(int messageId, FinsCommand command, int attempt, bool isFinal, bool requestSent)
        : ResultEventArgs(messageId, command)
    {
        /// <summary>Attempt number, starting at 1 (1 = first attempt, 2 = first retry, ...).</summary>
        public int Attempt { get; } = attempt;
        /// <summary>Whether this job will not be attempted again (retries used up or a socket error occurred).</summary>
        public bool IsFinal { get; } = isFinal;
        /// <summary>Whether the request of this attempt was handed to the send function. If false, the device did not receive this attempt; if true, the result is unknown and the device may have executed it.</summary>
        public bool RequestSent { get; } = requestSent;
        /// <summary>For a block write (WriteBlock) split into chunks, the number of items (words or bits) in the chunks the PLC had acknowledged in this attempt before the failure; 0 for other requests and when the first chunk failed. A retry starts again from the first chunk.</summary>
        public int WrittenCount { get; init; }
    }
    #endregion

    #region Properties
    int localPort, destinationNetwork, sourceNetwork, maxWords = FinsFrame.DefaultWords;
    int? destinationNode, sourceNode;

    /// <summary>IP address or host name of the PLC (default: "127.0.0.1").</summary>
    public string RemoteIP { get; set; } = "127.0.0.1";
    /// <summary>FINS/UDP port of the PLC (default: 9600).</summary>
    public int RemotePort { get; set; } = 9600;
    /// <summary>UDP port to use on this PC, 0 to 65535 (default: 0 = any port). Set it to 9600 if the PLC uses "automatic generation (static)" conversion.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Outside 0 to 65535.</exception>
    public int LocalPort { get => localPort; set => localPort = Check(value, 0, 65535, nameof(LocalPort)); }
    /// <summary>Destination node DA1, 0 to 254 (default: null = the last number of <see cref="RemoteIP"/>). 0 means "the PLC itself".</summary>
    /// <exception cref="ArgumentOutOfRangeException">Outside 0 to 254.</exception>
    public int? DestinationNode { get => destinationNode; set => destinationNode = value is int v ? Check(v, 0, 254, nameof(DestinationNode)) : null; }
    /// <summary>Source node SA1, 1 to 254 (default: null = the last number of the IP of this PC that routes to the PLC). Must not overlap within the FINS network.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Outside 1 to 254.</exception>
    public int? SourceNode { get => sourceNode; set => sourceNode = value is int v ? Check(v, 1, 254, nameof(SourceNode)) : null; }
    /// <summary>Destination network DNA, 0 to 127 (default: 0 = the same network).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Outside 0 to 127.</exception>
    public int DestinationNetwork { get => destinationNetwork; set => destinationNetwork = Check(value, 0, 127, nameof(DestinationNetwork)); }
    /// <summary>Source network SNA, 0 to 127 (default: 0 = the same network).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Outside 0 to 127.</exception>
    public int SourceNetwork { get => sourceNetwork; set => sourceNetwork = Check(value, 0, 127, nameof(SourceNetwork)); }
    /// <summary>Maximum number of items (words or bits) per request for block read/write, 1 to 997 (default: 499 = the CP1L-E write limit). Can be raised to 997 for the CJ2, NJ, and similar. It is read at execution time.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Outside 1 to 997.</exception>
    public int MaxWordsPerRequest { get => Volatile.Read(ref maxWords); set => Volatile.Write(ref maxWords, Check(value, 1, FinsFrame.MaxWords, nameof(MaxWordsPerRequest))); }

    /// <summary>Response timeout in ms (default: 1000). A per-job timeout, if given, takes precedence. For a split block read/write, it applies to each chunk.</summary>
    public int Timeout { get; set; } = 1000;
    /// <summary>Wait between jobs in ms (default: 10).</summary>
    public int Interval { get; set; } = 10;
    /// <summary>Whether to reopen the socket at 1-second intervals when it could not be opened or a socket error occurred (default: true).</summary>
    public bool AutoReconnect { get; set; } = true;
    /// <summary>Whether to discard writes while there is no connection (default: true). When true, a Manual write or named write requested while not connected raises <see cref="WriteDiscarded"/> (NotConnected) instead of being queued, and writes still queued when the connection is lost are discarded (Disconnected). When false, they stay queued and are sent after the next connection. Manual reads and Auto jobs are not affected, and <see cref="Stop"/> discards pending writes regardless of this setting. For UDP, "connected" means the socket is open.</summary>
    public bool DiscardWritesWhileDisconnected { get; set; } = true;

    /// <summary>Gets whether the UDP socket is open (UDP is connectionless, so this is unrelated to whether the PLC responds).</summary>
    public bool IsOpen => Volatile.Read(ref sock) != null;
    /// <summary>Gets whether the communication loop is running.</summary>
    public bool IsStart { get; private set; }
    /// <summary>Gets whether the object has been disposed.</summary>
    public bool IsDisposed { get; private set; }
    /// <summary>Gets or sets a user-defined tag.</summary>
    public object? Tag { get; set; }

    static int Check(int value, int min, int max, string name)
        => value < min || value > max ? throw new ArgumentOutOfRangeException(name, value, $"{name}는 {min}~{max} 입니다.") : value;
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
    byte sid;
    // 연결이 살아 있는지(workLock 보호). 설정이 true면 쓰기는 이것이 true일 때만 대기열에 들어간다
    bool linkUp;
    // linkUp을 올린 루프 스레드(workLock 보호). 핸들러 안 Stop→Start로 새 루프가 뜬 뒤 옛 루프의 SetLinkDown이 새 연결 상태를 덮지 않게 한다
    Thread? linkThread;
    // 이번 시도의 요청을 송신 함수에 넘겼는지(통신 스레드 전용)
    bool requestSent;
    // 이번 시도에서 PLC가 Ack한 블록 쓰기 조각의 원소 수 합(통신 스레드 전용)
    int writtenCount;
    #endregion

    #region Event
    /// <summary>Raised when a read (Read/ReadBlock) response is received. For ReadBlock, raised once after all chunks have been received.</summary>
    public event EventHandler<DataEventArgs>? DataReceived;
    /// <summary>Raised when a write (Write/WriteBlock) succeeds. A block write raises it once after all chunks succeed.</summary>
    public event EventHandler<ResultEventArgs>? WriteReceived;
    /// <summary>Raised when the PLC sends an error end code. No retry is made.</summary>
    public event EventHandler<NAKEventArgs>? NAKReceived;
    /// <summary>Raised when there is no response, the response does not match the request, or a socket error occurs during a request. Raised on every retry; IsFinal tells whether the job will be attempted again. If RequestSent is true, the device may have executed the request — read it back before resending.</summary>
    public event EventHandler<AttemptFailedEventArgs>? TimeoutReceived;
    /// <summary>Raised when a Manual write is discarded without being sent (see <see cref="DiscardWritesWhileDisconnected"/>). May be raised on the calling thread: NotConnected inside the Manual/NamedWrite call, Stopped inside Stop(); otherwise on the communication thread.</summary>
    public event EventHandler<WriteDiscardedEventArgs>? WriteDiscarded;
    /// <summary>Raised when the UDP socket is opened.</summary>
    public event EventHandler? DeviceOpened;
    /// <summary>Raised when the UDP socket is closed.</summary>
    public event EventHandler? DeviceClosed;
    #endregion

    #region Start / Stop
    /// <summary>Opens the UDP socket and starts the communication loop. Does nothing if it is already running.</summary>
    /// <remarks>If the socket cannot be opened (port in use, host name resolution failure, etc.) and <see cref="AutoReconnect"/> is false, the loop ends and <see cref="IsStart"/> becomes false.
    /// The IP, port, and node numbers are read every time the socket is opened.</remarks>
    public void Start()
    {
        lock (lifeLock)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (IsStart) return;
            cts = new CancellationTokenSource();
            var token = cts.Token;
            IsStart = true;
            thread = new Thread(() => Run(token)) { IsBackground = true, Name = nameof(FinsUdp) };
            thread.Start();
        }
    }

    /// <summary>Stops the communication loop and closes the socket. Waits up to (Timeout + 1000) ms for the loop to end. Pending Manual writes are discarded (WriteDiscarded, Stopped).</summary>
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
        // 설정과 무관하게 남은 쓰기를 비운다(재시작 때 늦게 실행되지 않게)
        // Join이 시간 초과로 끝났어도(핸들러가 루프를 붙잡은 경우) Stop 뒤 쓰기가 대기열에 들어가거나
        // 새 루프가 옛 쓰기를 보내지 않도록 연결 상태를 즉시 내린다
        lock (workLock) { linkUp = false; linkThread = null; }
        DiscardWrites(WriteDiscardReason.Stopped);
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

    static Work ToWork(int id, FinsRequest r, int? repeatCount, int? timeout)
        => new(id, r.Command, r.Items, r.Count, r.Values, repeatCount, timeout);

    static Work ReadWork(int id, IReadOnlyList<string> addresses, int? repeatCount, int? timeout)
        => ToWork(id, FinsRequest.Read(addresses, FinsFrame.MaxItems), repeatCount, timeout);

    static Work ReadBlockWork(int id, string address, int count, int? repeatCount, int? timeout)
        => ToWork(id, FinsRequest.ReadBlock(address, count), repeatCount, timeout);

    static Work WriteWork(int id, string address, long value, int? repeatCount, int? timeout)
        => ToWork(id, FinsRequest.Write(address, value), repeatCount, timeout);

    static Work WriteBlockWork(int id, string address, IReadOnlyList<long> values, int? repeatCount, int? timeout)
        => ToWork(id, FinsRequest.WriteBlock(address, values), repeatCount, timeout);

    void AddAuto(Work w) { lock (workLock) autoWorks.Add(w); }
    void AddManual(Work w)
    {
        bool discard = false;
        lock (workLock)
        {
            if (w.IsWrite && DiscardWritesWhileDisconnected && !linkUp) discard = true;
            else manualWorks.Add(w);
        }
        if (discard) Raise(WriteDiscarded, new WriteDiscardedEventArgs(w.MessageID, WriteDiscardReason.NotConnected));
    }

    // 연결을 얻었다. 소켓이 바인드되었다(UDP라 이것이 "연결"). sock을 공개하기 전에 부른다(IsOpen이 true면 이미 linkUp)
    internal void SetLinkUp()
    {
        lock (workLock)
        {
            linkUp = true;
            linkThread = Thread.CurrentThread;
        }
    }

    // 연결을 잃었다. DeviceClosed 다음에 부른다. stopping이면 Stop으로 닫힌 것
    internal void SetLinkDown(bool stopping)
    {
        bool discard;
        lock (workLock)
        {
            // 다른(새) 루프가 연결을 올렸으면 옛 루프는 건드리지 않는다
            if (linkThread != null && linkThread != Thread.CurrentThread) return;
            linkUp = false;
            linkThread = null;
            discard = DiscardWritesWhileDisconnected;
        }
        if (discard) DiscardWrites(stopping ? WriteDiscardReason.Stopped : WriteDiscardReason.Disconnected);
    }

    // 대기 중인 Manual 쓰기를 순서대로 꺼내(읽기는 남김) lock 밖에서 하나씩 알린다. 꺼내기는 workLock 안이라 쓰기 하나당 알림은 한 번
    internal void DiscardWrites(WriteDiscardReason reason)
    {
        List<Work> dropped;
        lock (workLock)
        {
            dropped = manualWorks.FindAll(x => x.IsWrite);
            if (dropped.Count == 0) return;
            manualWorks.RemoveAll(x => x.IsWrite);
        }
        foreach (var w in dropped) Raise(WriteDiscarded, new WriteDiscardedEventArgs(w.MessageID, reason));
    }

    // 이름 있는 쓰기 표. NamedWrite는 실행 중에도 불리므로 통째로 바꾸는 참조(volatile)로 둔다. ApplyConfig에서만 교체한다.
    volatile NamedWriteTable<FinsWriteConfig> namedWrites = NamedWriteTable<FinsWriteConfig>.Empty;
    #endregion

    #region Auto / Manual
    /// <summary>Registers a random (individual) read (Read, 0104) in the Auto list. The response arrives through <see cref="DataReceived"/>.</summary>
    /// <param name="id">Message ID (the MessageID of the response event).</param>
    /// <param name="addresses">1 to 167 word or bit addresses (e.g., ["D100", "CIO0.05", "W10"]).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    /// <exception cref="ArgumentException">The address format is invalid or the count is outside 1 to 167.</exception>
    public void AutoRead(int id, IReadOnlyList<string> addresses, int? timeout = null)
        => AddAuto(ReadWork(id, addresses, null, timeout));

    /// <summary>Registers a block read (ReadBlock, 0101) in the Auto list. The response arrives through <see cref="DataReceived"/>.</summary>
    /// <param name="id">Message ID (the MessageID of the response event).</param>
    /// <param name="address">Start address. A word address reads words and a bit address reads bits (e.g., "D100", "CIO100.00").</param>
    /// <param name="count">Number of items to read, 1 to 65535 (split into chunks of <see cref="MaxWordsPerRequest"/> items). The last address must be within the memory area.</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>; applies to each chunk).</param>
    /// <exception cref="ArgumentException">The address format is invalid or the count is out of range.</exception>
    public void AutoReadBlock(int id, string address, int count, int? timeout = null)
        => AddAuto(ReadBlockWork(id, address, count, null, timeout));

    /// <summary>Registers a one-shot random (individual) read (Read, 0104). Processed before Auto jobs.</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="addresses">1 to 167 word or bit addresses.</param>
    /// <param name="repeatCount">Retry count when there is no response (if null, no retry).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    /// <exception cref="ArgumentException">The address format is invalid or the count is outside 1 to 167.</exception>
    public void ManualRead(int id, IReadOnlyList<string> addresses, int? repeatCount = null, int? timeout = null)
        => AddManual(ReadWork(id, addresses, repeatCount, timeout));

    /// <summary>Registers a one-shot block read (ReadBlock, 0101). Processed before Auto jobs.</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="address">Start address (a word address reads words, a bit address reads bits).</param>
    /// <param name="count">Number of items to read, 1 to 65535.</param>
    /// <param name="repeatCount">Retry count when there is no response (if null, no retry). A retry starts again from the first chunk.</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>; applies to each chunk).</param>
    /// <exception cref="ArgumentException">The address format is invalid or the count is out of range.</exception>
    public void ManualReadBlock(int id, string address, int count, int? repeatCount = null, int? timeout = null)
        => AddManual(ReadBlockWork(id, address, count, repeatCount, timeout));

    /// <summary>Registers a one-shot write (Write, 0102). <see cref="WriteReceived"/> is raised on success.</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="address">Word or bit address (e.g., "D100", "W10.03").</param>
    /// <param name="value">Value to write. A word is -32768 to 65535 (negative values as two's complement); a bit is 0/1.</param>
    /// <param name="repeatCount">Retry count when there is no response (if null, no retry). A retry resends a write the device may already have executed; do not use it for pulse, counter or incremental writes.</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    /// <exception cref="ArgumentException">The address or value is invalid.</exception>
    public void ManualWrite(int id, string address, long value, int? repeatCount = null, int? timeout = null)
        => AddManual(WriteWork(id, address, value, repeatCount, timeout));

    /// <summary>Registers a one-shot block write (WriteBlock, 0102). <see cref="WriteReceived"/> is raised once if all chunks succeed.</summary>
    /// <remarks>Not atomic: if it fails, the leading chunks may already be written (see WrittenCount, counted in items for that attempt; a retry starts again from the first chunk). Write trigger/handshake words separately after the block succeeds.</remarks>
    /// <param name="id">Message ID.</param>
    /// <param name="address">Start address (a word address writes words, a bit address writes bits).</param>
    /// <param name="values">1 to 65535 values to write (copied at registration). Words are -32768 to 65535 and bits are 0/1. Written in chunks of <see cref="MaxWordsPerRequest"/> items and not atomic.</param>
    /// <param name="repeatCount">Retry count when there is no response (if null, no retry). A retry starts again from the first chunk. A retry resends a write the device may already have executed; do not use it for pulse, counter or incremental writes.</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>; applies to each chunk).</param>
    /// <exception cref="ArgumentException">The address or value is invalid, or the count is out of range.</exception>
    public void ManualWriteBlock(int id, string address, IReadOnlyList<long> values, int? repeatCount = null, int? timeout = null)
        => AddManual(WriteBlockWork(id, address, values, repeatCount, timeout));

    /// <summary>Registers a one-shot write of a single value by the name defined in the XML &lt;Write&gt;. Same as <see cref="ManualWrite"/>.</summary>
    /// <param name="name">Name of the &lt;Write&gt;.</param>
    /// <param name="value">Value to write. A word is -32768 to 65535 (negative values as two's complement); a bit is 0/1.</param>
    /// <param name="repeatCount">Retry count (if null, the RepeatCount in the XML; if that is absent too, no retries). A retry resends a write the device may already have executed; do not use it for pulse, counter or incremental writes.</param>
    /// <param name="timeout">Timeout in ms (if null, the Timeout in the XML; if that is absent too, <see cref="Timeout"/>).</param>
    /// <exception cref="ArgumentException">The name does not exist, or a value is invalid.</exception>
    public void NamedWrite(string name, long value, int? repeatCount = null, int? timeout = null)
    {
        var table = namedWrites;
        var item = table.Get(name);
        var (r, t) = table.Resolve(item, repeatCount, timeout);
        AddManual(WriteWork(item.Id, item.Address, value, r, t));
    }

    /// <summary>Registers a one-shot write of several values to consecutive addresses by the name defined in the XML &lt;Write&gt;. Same as <see cref="ManualWriteBlock"/>.</summary>
    /// <param name="name">Name of the &lt;Write&gt;.</param>
    /// <param name="values">1 to 65535 values to write. Words are -32768 to 65535 and bits are 0/1.</param>
    /// <param name="repeatCount">Retry count (if null, the RepeatCount in the XML; if that is absent too, no retries). A retry resends a write the device may already have executed; do not use it for pulse, counter or incremental writes.</param>
    /// <param name="timeout">Timeout in ms (if null, the Timeout in the XML; if that is absent too, <see cref="Timeout"/>).</param>
    /// <exception cref="ArgumentException">The name does not exist, or a value is invalid.</exception>
    public void NamedWrite(string name, IReadOnlyList<long> values, int? repeatCount = null, int? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        var table = namedWrites;
        var item = table.Get(name);
        var (r, t) = table.Resolve(item, repeatCount, timeout);
        AddManual(WriteBlockWork(item.Id, item.Address, values, r, t));
    }

    /// <summary>Checks whether a named write with the given name was loaded from XML. Names are case-sensitive.</summary>
    /// <param name="name">Name of the &lt;Write&gt;.</param>
    /// <returns>true if it exists.</returns>
    public bool ContainsNamedWrite(string name) => namedWrites.Contains(name);
    #endregion

    #region Config / Xml
    /// <summary>Builds a configuration object from the current communication settings, Auto list, and named write list. Manual jobs are not included.</summary>
    /// <returns>The configuration object.</returns>
    public FinsUdpConfig ToConfig()
    {
        var c = new FinsUdpConfig
        {
            RemoteIP = RemoteIP,
            RemotePort = RemotePort,
            LocalPort = LocalPort,
            DestinationNetwork = DestinationNetwork,
            SourceNetwork = SourceNetwork,
            MaxWordsPerRequest = MaxWordsPerRequest,
            Timeout = Timeout,
            Interval = Interval,
            AutoReconnect = AutoReconnect,
            DiscardWritesWhileDisconnected = DiscardWritesWhileDisconnected,
            Autos = [.. AutoWorksSnapshot().Select(w => FinsAutoConfig.From(w.MessageID, w.Command, w.Items, w.Count, w.Timeout))],
            Writes = namedWrites.Snapshot(),
        };
        if (DestinationNode is int d) c.DestinationNode = d;
        if (SourceNode is int s) c.SourceNode = s;
        return c;
    }

    /// <summary>Applies the communication settings, Auto list, and named write list of a configuration object. The existing Auto list and named writes are replaced by those of the configuration.</summary>
    /// <remarks>Everything is validated first and then applied, so if any value is invalid, nothing is changed.</remarks>
    /// <param name="config">The configuration to apply.</param>
    /// <exception cref="InvalidOperationException">Called while communicating (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">A value is invalid (the message includes the element and attribute names).</exception>
    public void ApplyConfig(FinsUdpConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (IsStart) throw new InvalidOperationException("통신 중에는 설정을 바꿀 수 없습니다. Stop() 후 호출하세요.");
        config.Validate();
        var works = FinsAutoConfig.Parse(config.Autos, FinsFrame.MaxItems).Select(a => ToWork(a.Id, a.Request, null, a.Timeout)).ToList();
        var table = NamedWriteTable<FinsWriteConfig>.Build(config.Writes, (w, label) =>
        {
            try { WriteWork(w.Id, w.Address, 0, null, null); }
            catch (ArgumentException ex)
            {
                throw XmlConfig.Invalid(label, ex, new Dictionary<string, (string, string)> { ["address"] = (nameof(w.Address), w.Address ?? "") });
            }
        });

        RemoteIP = config.RemoteIP;
        RemotePort = config.RemotePort;
        LocalPort = config.LocalPort;
        DestinationNode = config.DestinationNodeSpecified ? config.DestinationNode : null;
        SourceNode = config.SourceNodeSpecified ? config.SourceNode : null;
        DestinationNetwork = config.DestinationNetwork;
        SourceNetwork = config.SourceNetwork;
        MaxWordsPerRequest = config.MaxWordsPerRequest;
        Timeout = config.Timeout;
        Interval = config.Interval;
        AutoReconnect = config.AutoReconnect;
        DiscardWritesWhileDisconnected = config.DiscardWritesWhileDisconnected;
        namedWrites = table;
        ReplaceAutoWorks(works);
    }

    /// <summary>Builds an XML element from the current settings, Auto list, and named write list. Manual jobs are not included.</summary>
    /// <returns>The &lt;FinsUdp&gt; element.</returns>
    public XElement ToXml() => XmlConfig.Serialize(ToConfig());

    /// <summary>Loads the settings, Auto list, and named write list from an XML element. The existing Auto list and named writes are replaced by the XML content.</summary>
    /// <remarks>
    /// <para>Omitted attributes use their default values. An unknown attribute or element is an error (to catch typos).</para>
    /// <para>Everything is validated first and then applied, so if any value is invalid, nothing is changed.</para>
    /// <para>You can check the format by generating the XSD with <see cref="ConfigSchema.Get{T}"/> for <see cref="FinsUdpConfig"/>.</para>
    /// </remarks>
    /// <param name="element">The &lt;FinsUdp&gt; element.</param>
    /// <exception cref="InvalidOperationException">Called while communicating (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">The root name differs, an unknown attribute or element is present, or a value is invalid.</exception>
    public void FromXml(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (IsStart) throw new InvalidOperationException("통신 중에는 설정을 불러올 수 없습니다. Stop() 후 호출하세요.");
        ApplyConfig(XmlConfig.Deserialize<FinsUdpConfig>(element));
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
                var opened = TryOpen();
                if (opened is var (s, remote, nodes))
                {
                    SetLinkUp();   // 소켓이 바인드되었다(UDP라 이것이 "연결"). sock을 공개하기 전에(IsOpen이 true면 이미 linkUp)
                    Volatile.Write(ref sock, s);
                    try
                    {
                        Raise(DeviceOpened);
                        try
                        {
                            while (!token.IsCancellationRequested)
                            {
                                var w = TakeNextWork();
                                if (w != null) ProcessWork(s, remote, nodes, w);
                                if (token.IsCancellationRequested) break;
                                token.WaitHandle.WaitOne(w != null ? Interval : Math.Max(Interval, 10));
                            }
                        }
                        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
                    }
                    finally
                    {
                        CloseSocket(s);
                        Raise(DeviceClosed);
                        SetLinkDown(token.IsCancellationRequested);
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

    // 소켓을 PLC로 가는 로컬 IP(모든 인터페이스가 아님)와 LocalPort에 바인드하고 FINS 주소를 정한다. 실패하면 null.
    (Socket Socket, IPEndPoint Remote, FinsNodes Nodes)? TryOpen()
    {
        Socket? s = null;
        try
        {
            var ip = IPAddress.TryParse(RemoteIP, out var parsed) ? parsed
                : Dns.GetHostAddresses(RemoteIP).First(a => a.AddressFamily == AddressFamily.InterNetwork);
            var remote = new IPEndPoint(ip, RemotePort);
            IPAddress local;
            using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                probe.Connect(remote);   // 경로만 정한다 (UDP라 패킷은 나가지 않음)
                local = ((IPEndPoint)probe.LocalEndPoint!).Address;
            }
            s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Bind(new IPEndPoint(local, LocalPort));
            return (s, remote, Nodes(ip, local, DestinationNetwork, DestinationNode, SourceNetwork, SourceNode));
        }
        catch
        {
            s?.Dispose();
            return null;
        }
    }

    // FINS 주소. DA1을 안 정하면 PLC IP(remote)의 마지막 숫자, SA1을 안 정하면 PLC로 가는 이 PC IP(local)의 마지막 숫자. DA2·SA2는 0(CPU).
    internal static FinsNodes Nodes(IPAddress remote, IPAddress local, int destinationNetwork, int? destinationNode, int sourceNetwork, int? sourceNode)
        => new((byte)destinationNetwork, (byte)(destinationNode ?? remote.GetAddressBytes()[3]), 0,
            (byte)sourceNetwork, (byte)(sourceNode ?? local.GetAddressBytes()[3]), 0);

    void CloseSocket(Socket? s)
    {
        if (s == null) return;
        Interlocked.CompareExchange(ref sock, null, s);
        s.Dispose();
    }

    static ResultEventArgs Result(Work w) => new(w.MessageID, w.Command);

    AttemptFailedEventArgs Failed(Work w, int attempt, bool isFinal)
        => new(w.MessageID, w.Command, attempt, isFinal, requestSent) { WrittenCount = writtenCount };

    void ProcessWork(Socket s, IPEndPoint remote, FinsNodes nodes, Work w)
    {
        int attempt = 0;
        try
        {
            int timeout = w.Timeout ?? Timeout;
            for (; ; attempt++)
            {
                requestSent = false;
                writtenCount = 0;
                var r = FinsFrame.Run(FinsFrame.Chunks(w.Command, w.Items, w.Count, w.Values, MaxWordsPerRequest), c => Exchange(s, remote, nodes, c, timeout), ref writtenCount);
                if (r.Status is FinsStatus.Ack or FinsStatus.Nak)
                {
                    Report(w, r);
                    return;
                }
                bool isFinal = attempt >= (w.RepeatCount ?? 0);
                Raise(TimeoutReceived, Failed(w, attempt + 1, isFinal));
                if (isFinal) return;
            }
        }
        catch
        {
            // 소켓 오류로 작업이 중단되면 조용히 사라지지 않게 알린 뒤(더 시도하지 않으므로 최종), 다시 여는 경로가 돌도록 다시 던진다
            Raise(TimeoutReceived, Failed(w, attempt + 1, true));
            throw;
        }
    }

    void Report(Work w, FinsResponse r)
    {
        if (r.Status == FinsStatus.Nak) Raise(NAKReceived, new NAKEventArgs(w.MessageID, w.Command, r.ErrorCode) { WrittenCount = writtenCount });
        else if (w.Command is FinsCommand.Read or FinsCommand.ReadBlock) Raise(DataReceived, new DataEventArgs(w.MessageID, w.Command, w.Addresses, r.Values));
        else Raise(WriteReceived, Result(w));
    }

    // 조각 하나를 보내고, SID·FINS 주소·보낸 곳이 맞는 응답을 해석한다. 시간 안에 못 받으면 Invalid.
    FinsResponse Exchange(Socket s, IPEndPoint remote, FinsNodes nodes, FinsChunk chunk, int timeout)
    {
        byte id = ++sid;
        Drain(s);
        s.SendTo(FinsFrame.Command(nodes, id, chunk.Body), remote);
        requestSent = true;

        var buf = new byte[4096];
        var sw = Stopwatch.StartNew();
        while (true)
        {
            int remain = timeout - (int)sw.ElapsedMilliseconds;
            if (remain <= 0 || !s.Poll(remain * 1000, SelectMode.SelectRead)) return FinsResponse.Invalid;
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int n;
            try { n = s.ReceiveFrom(buf, ref from); }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize) { continue; }   // ICMP 포트 닫힘, 너무 큰 데이터그램
            var frame = buf.AsSpan(0, n);
            // 다른 곳에서 온 데이터그램, 이전 요청의 늦은 응답(SID 다름)은 버리고 계속 기다린다
            if (!remote.Equals(from) || !FinsFrame.IsResponseTo(frame, nodes, id)) continue;
            return FinsFrame.ParseBody(frame[FinsFrame.HeaderLength..], chunk);
        }
    }

    static void Drain(Socket s)
    {
        var tmp = new byte[4096];
        while (s.Available > 0)
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            try { s.ReceiveFrom(tmp, ref from); }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize) { }
        }
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
