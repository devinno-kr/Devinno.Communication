using System.Diagnostics;
using System.Net.Sockets;
using System.Xml.Linq;
using Devinno.Communication.Internal;
using Devinno.Communication.Mitsubishi.Data;

namespace Devinno.Communication.Mitsubishi.Ethernet;

/// <summary>
/// Mitsubishi PLC master that exchanges MC protocol binary frames (3E or 1E) over TCP.
/// </summary>
/// <remarks>
/// <para>The frame is chosen with <see cref="Frame"/>. E3 (default) is the Ethernet built into Q/L/FX5 CPUs (commands 0401/1401, subcommands 0000/0001), and E1 is FX3 Ethernet such as FX3U-ENET(-ADP/-L) (commands 00H to 03H). FX3 Ethernet does not support 3E.
/// The iQ-R-specific subcommands (0002/0003), 4E, ASCII code, and UDP are not supported.</para>
/// <para>The official 1C command names are used as well: BR (bit read), WR (word read), BW (bit write), WW (word write).</para>
/// <para>Devices are specified as code + number, such as "D100", "M0", "X1F", "SM400", "ZR100" (case-insensitive).
/// 3E codes: SM SD X Y M L F V B D W TS TC TN SS SC SN CS CC CN SB SW S Z R ZR. 1E codes: X Y M S L F B D R W TN TS TC CN CS CC.
/// X/Y numbers follow <see cref="XYNotation"/> (Auto = hexadecimal for E3, octal for E1), B/W/SB/SW/ZR are hexadecimal, and the rest are decimal. When using an FX5 over 3E, X/Y are octal, so specify <see cref="McXYNotation.Octal"/>.</para>
/// <para>Number of points handled at a time: E3 is 960 words or 3584 bits (480 words for QnA). E1 (FX3) is BR 256, WR 64 words for a word device and 32 words for a bit device, BW 160, WW 64 words for a word device and 10 words for a bit device.
/// When WR/WW is used on a bit device, 16 points form one word (bit0 = the start number). The library does not check the start number alignment (a multiple of 16 for 1E), and the PLC rejects a mismatch with an error. The 32-bit counters of FX3 (C200 to C255) are not handled specially.</para>
/// <para>Values are long. A word read is a raw value of 0 to 65535 and a bit is 0/1. A word to write accepts -32768 to 65535 and is truncated to 16 bits when sent (negative values as two's complement); a bit accepts only 0/1.</para>
/// <para><see cref="Frame"/> and <see cref="XYNotation"/> are applied when a job is registered. Jobs that are already registered do not change.
/// The monitoring timer (the time the PLC waits for a response) is sent as the job's timeout rounded up to a multiple of 250 ms.
/// Auto jobs that are already registered do not change when <see cref="XYNotation"/> or <see cref="Frame"/> is changed afterwards, so calling ToConfig/SaveXml in that state can save values whose names are interpreted differently when loaded again. Configure these before registering.</para>
/// <para>3E/1E have no number to match a request with its response. Therefore the bytes left in the socket are discarded just before a request is sent, and if the response header does not match, the connection is dropped and reconnected.
/// If a previous response arrives late after the retry request following a timeout has been sent, it cannot be distinguished, so set the timeout generously relative to the PLC response time.</para>
/// <para>When the PLC sends an error response, only <see cref="NAKReceived"/> is raised and no retry is made. ErrorCode is the end code for 3E (for example 0xC051) and the end code for 1E (for example 0x56); for a 5BH error of 1E it is 0x5B00 | the abnormal code (for example 0x5B10).</para>
/// <para>The protocol defines no fixed port number. Specify <see cref="RemotePort"/> to match the PLC's open settings.</para>
/// <para>Manual jobs are processed before Auto jobs. The communication loop runs on a dedicated background thread, and all events are raised on that thread. To update the UI, marshal asynchronously, as with BeginInvoke (WinForms) or Dispatcher.BeginInvoke (WPF). Exceptions thrown in a handler are ignored and the loop continues.</para>
/// <para>Write safety: with <see cref="DiscardWritesWhileDisconnected"/> true (the default), a Manual write (BW/WW, including <see cref="NamedWrite(string, long, int?, int?)"/>) requested while there is no connection is not queued, and writes still queued when the connection is lost are discarded right after <see cref="DeviceClosed"/>; both are reported through <see cref="WriteDiscarded"/>, which may also be raised on the calling thread. <see cref="Stop"/> always discards pending Manual writes. Manual reads and Auto jobs are never discarded. Each write ends with exactly one of: <see cref="WriteReceived"/>, <see cref="NAKReceived"/>, a <see cref="TimeoutReceived"/> with IsFinal true, or <see cref="WriteDiscarded"/> (jobs removed with <see cref="ClearManual"/> or <see cref="RemoveManual"/> are not reported).</para>
/// </remarks>
/// <example>
/// <code>
/// using Devinno.Communication.Mitsubishi.Ethernet;
/// using Devinno.Communication.Mitsubishi.Data;   // McCommand, McXYNotation
///
/// // Q/L/FX5 (3E)
/// var mc = new McEthernet { RemoteIP = "192.168.0.30", RemotePort = 5000, Frame = McFrame.E3 };
/// mc.AutoWR(1, "D100", 10);
/// mc.AutoBR(2, "M0", 32);
/// mc.DataReceived += (s, e) =&gt; Console.WriteLine($"{e.MessageID} {e.Device}: {string.Join(",", e.Values)}");
/// mc.NAKReceived += (s, e) =&gt; Console.WriteLine($"Error {e.ErrorCode:X4}");
/// mc.DeviceOpened += (s, e) =&gt;
/// {
///     // writes are accepted only while connected (otherwise WriteDiscarded, NotConnected)
///     mc.ManualWW(10, "D200", [1234, -1]);
///     mc.NamedWrite("Speed", 1500);   // calls the XML &lt;Write Name="Speed" Id="20" Device="D200" /&gt; by name
/// };
/// mc.Start();
///
/// // FX3U-ENET-ADP (1E, X/Y octal)
/// var fx = new McEthernet { RemoteIP = "192.168.0.31", RemotePort = 5002, Frame = McFrame.E1 };
/// fx.AutoBR(1, "X0", 16);
/// fx.Start();
/// </code>
/// </example>
public class McEthernet : IDisposable
{
    #region class : Work
    internal sealed class Work(int messageId, McCommand command, McDevice device, int count, long[]? values, McFrame frame, int? repeatCount, int? timeout)
    {
        public int MessageID { get; } = messageId;
        public McCommand Command { get; } = command;
        public McDevice Device { get; } = device;
        public int Count { get; } = count;
        public long[]? Values { get; } = values;
        public McFrame Frame { get; } = frame;
        public int? RepeatCount { get; } = repeatCount;
        public int? Timeout { get; } = timeout;
        // 연결이 끊긴 동안 버리는 대상(물리 동작이 있는 쓰기)
        public bool IsWrite => Command is McCommand.BW or McCommand.WW;
    }
    #endregion

    #region class : EventArgs
    /// <summary>Common event arguments for request result events. Write response events use this type as-is.</summary>
    /// <param name="messageId">The message ID given at registration.</param>
    /// <param name="command">Command.</param>
    public class ResultEventArgs(int messageId, McCommand command) : EventArgs
    {
        /// <summary>The message ID given at registration.</summary>
        public int MessageID { get; } = messageId;
        /// <summary>Command.</summary>
        public McCommand Command { get; } = command;
    }

    /// <summary>Event arguments for a read (BR/WR) response.</summary>
    /// <param name="messageId">Message ID.</param>
    /// <param name="command">Command (BR or WR).</param>
    /// <param name="device">Start device (uppercased name, for example "D100").</param>
    /// <param name="values">Values read. 0/1 for BR, a raw value of 0 to 65535 for WR.</param>
    public class DataEventArgs(int messageId, McCommand command, string device, long[] values)
        : ResultEventArgs(messageId, command)
    {
        /// <summary>Start device (uppercased name, for example "D100").</summary>
        public string Device { get; } = device;
        /// <summary>Values read. 0/1 for BR, a raw value of 0 to 65535 for WR (convert with (short) if a sign is needed).</summary>
        public long[] Values { get; } = values;
    }

    /// <summary>Event arguments for when the PLC sends an error response.</summary>
    /// <param name="messageId">Message ID.</param>
    /// <param name="command">Command.</param>
    /// <param name="errorCode">The end code for 3E (for example 0xC051), the end code for 1E (for example 0x56); for a 1E 5BH error, 0x5B00 | the abnormal code (for example 0x5B10).</param>
    public class NAKEventArgs(int messageId, McCommand command, int errorCode)
        : ResultEventArgs(messageId, command)
    {
        /// <summary>The end code for 3E (for example 0xC051), the end code for 1E (for example 0x56); for a 1E 5BH error, 0x5B00 | the abnormal code (for example 0x5B10).</summary>
        public int ErrorCode { get; } = errorCode;
    }

    /// <summary>Event arguments for a failed attempt (<see cref="TimeoutReceived"/>).</summary>
    /// <param name="messageId">The message ID given at registration.</param>
    /// <param name="command">Command.</param>
    /// <param name="attempt">Attempt number, starting at 1 (1 = first attempt, 2 = first retry, ...).</param>
    /// <param name="isFinal">Whether this job will not be attempted again (retries used up or the connection was lost).</param>
    /// <param name="requestSent">Whether the request of this attempt was handed to the send function.</param>
    public class AttemptFailedEventArgs(int messageId, McCommand command, int attempt, bool isFinal, bool requestSent)
        : ResultEventArgs(messageId, command)
    {
        /// <summary>Attempt number, starting at 1 (1 = first attempt, 2 = first retry, ...).</summary>
        public int Attempt { get; } = attempt;
        /// <summary>Whether this job will not be attempted again (retries used up or the connection was lost).</summary>
        public bool IsFinal { get; } = isFinal;
        /// <summary>Whether the request of this attempt was handed to the send function. If false, the device did not receive this attempt; if true, the result is unknown and the device may have executed it.</summary>
        public bool RequestSent { get; } = requestSent;
    }
    #endregion

    #region Properties
    /// <summary>IP address of the PLC to connect to (default: "127.0.0.1").</summary>
    public string RemoteIP { get; set; } = "127.0.0.1";
    /// <summary>Port to connect to (default: 5000). The protocol defines no fixed port, so match the PLC's open settings.</summary>
    public int RemotePort { get; set; } = 5000;
    /// <summary>Frame (default: E3). FX3 Ethernet uses E1. Applied at registration.</summary>
    public McFrame Frame { get; set; } = McFrame.E3;
    /// <summary>Number base of X/Y numbers (default: Auto = hexadecimal for E3, octal for E1). Applied at registration. FX5 over 3E uses octal X/Y: set Octal. With Auto, X/Y numbers are read as hexadecimal and address the wrong points.</summary>
    public McXYNotation XYNotation { get; set; } = McXYNotation.Auto;

    /// <summary>Response timeout in ms (default: 1000). A per-job timeout, if given, takes precedence. It is also used for the PLC monitoring timer.</summary>
    public int Timeout { get; set; } = 1000;
    /// <summary>Wait between jobs in ms (default: 10).</summary>
    public int Interval { get; set; } = 10;
    /// <summary>Whether to reconnect at 1-second intervals when the connection could not be made or was lost (default: true).</summary>
    public bool AutoReconnect { get; set; } = true;
    /// <summary>Whether to discard writes while there is no connection (default: true). When true, a Manual write or named write requested while not connected raises <see cref="WriteDiscarded"/> (NotConnected) instead of being queued, and writes still queued when the connection is lost are discarded (Disconnected). When false, they stay queued and are sent after the next connection. Manual reads and Auto jobs are not affected, and <see cref="Stop"/> discards pending writes regardless of this setting.</summary>
    public bool DiscardWritesWhileDisconnected { get; set; } = true;

    /// <summary>Gets whether the TCP connection is established.</summary>
    public bool IsOpen => Volatile.Read(ref sock)?.Connected ?? false;
    /// <summary>Gets whether the communication loop is running.</summary>
    public bool IsStart { get; private set; }
    /// <summary>Gets whether the object has been disposed.</summary>
    public bool IsDisposed { get; private set; }
    /// <summary>Gets or sets a user-defined tag.</summary>
    public object? Tag { get; set; }
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
    // 연결이 살아 있는지(workLock 보호). 설정이 true면 쓰기는 이것이 true일 때만 대기열에 들어간다
    bool linkUp;
    // linkUp을 올린 루프 스레드(workLock 보호). 핸들러 안 Stop→Start로 새 루프가 뜬 뒤 옛 루프의 SetLinkDown이 새 연결 상태를 덮지 않게 한다
    Thread? linkThread;
    // 이번 시도의 요청을 송신 함수에 넘겼는지(통신 스레드 전용)
    bool requestSent;
    #endregion

    #region Event
    /// <summary>Raised when a read (BR/WR) response is received.</summary>
    public event EventHandler<DataEventArgs>? DataReceived;
    /// <summary>Raised when a write (BW/WW) response is received.</summary>
    public event EventHandler<ResultEventArgs>? WriteReceived;
    /// <summary>Raised when the PLC sends an error response (end code other than 0). No retry is made.</summary>
    public event EventHandler<NAKEventArgs>? NAKReceived;
    /// <summary>Raised when there is no response, the response is malformed, or the connection is lost during a request. Raised on every retry; IsFinal tells whether the job will be attempted again. If RequestSent is true, the device may have executed the request — read it back before resending.</summary>
    public event EventHandler<AttemptFailedEventArgs>? TimeoutReceived;
    /// <summary>Raised when a Manual write is discarded without being sent (see <see cref="DiscardWritesWhileDisconnected"/>). May be raised on the calling thread: NotConnected inside the Manual/NamedWrite call, Stopped inside Stop(); otherwise on the communication thread.</summary>
    public event EventHandler<WriteDiscardedEventArgs>? WriteDiscarded;
    /// <summary>Raised when the TCP connection is established.</summary>
    public event EventHandler? DeviceOpened;
    /// <summary>Raised when the TCP connection is lost.</summary>
    public event EventHandler? DeviceClosed;
    #endregion

    #region Start / Stop
    /// <summary>Connects to the PLC and starts the communication loop. Does nothing if it is already running.</summary>
    /// <remarks>If the connection cannot be made and <see cref="AutoReconnect"/> is false, the loop ends and <see cref="IsStart"/> becomes false.</remarks>
    public void Start()
    {
        lock (lifeLock)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (IsStart) return;
            cts = new CancellationTokenSource();
            var token = cts.Token;
            IsStart = true;
            thread = new Thread(() => Run(token)) { IsBackground = true, Name = nameof(McEthernet) };
            thread.Start();
        }
    }

    /// <summary>Stops the communication loop and disconnects. Waits up to (Timeout + 1000) ms for the loop to end. Pending Manual writes are discarded (WriteDiscarded, Stopped).</summary>
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

    // 작업 하나를 만든다. 프레임 설정(frame, notation)은 인자로 받는다(ApplyConfig가 적용 전에 검사할 수 있도록).
    internal static Work NewWork(int id, McCommand command, string device, int count, IReadOnlyList<long>? values,
        int? repeatCount, int? timeout, McFrame frame, McXYNotation notation)
    {
        var target = frame == McFrame.E3 ? McTarget.Ethernet3E : McTarget.Ethernet1E;
        var d = McDevice.ParseFor(command, device, count, target, McDevice.IsOctal(notation, frame == McFrame.E1), nameof(device), values == null ? nameof(count) : nameof(values));
        if (values != null) McDevice.CheckValues(command, values, nameof(values));
        return new Work(id, command, d, count, values == null ? null : [.. values], frame, repeatCount, timeout);
    }

    Work Current(int id, McCommand command, string device, int count, IReadOnlyList<long>? values, int? repeatCount, int? timeout)
        => NewWork(id, command, device, count, values, repeatCount, timeout, Frame, XYNotation);

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

    // 연결을 얻었다. sock을 공개하기 전에 부른다(IsOpen이 true면 이미 linkUp)
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
    volatile NamedWriteTable<McEthernetWriteConfig> namedWrites = NamedWriteTable<McEthernetWriteConfig>.Empty;

    // 디바이스가 비트면 BW, 워드면 WW (프레임 설정은 인자로 받는다)
    static McCommand WriteCommand(string device, McFrame frame, McXYNotation notation)
        => McDevice.Parse(device, frame == McFrame.E3 ? McTarget.Ethernet3E : McTarget.Ethernet1E,
            McDevice.IsOctal(notation, frame == McFrame.E1), nameof(device)).IsBit ? McCommand.BW : McCommand.WW;
    #endregion

    #region Auto / Manual
    /// <summary>Registers a BR (bit batch read) in the Auto list.</summary>
    /// <param name="id">Message ID (the MessageID of the response event).</param>
    /// <param name="device">Start bit device (for example "M0", "X1F").</param>
    /// <param name="count">Number of points to read. E3 1 to 3584, E1 1 to 256.</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void AutoBR(int id, string device, int count, int? timeout = null)
        => AddAuto(Current(id, McCommand.BR, device, count, null, null, timeout));

    /// <summary>Registers a WR (word batch read) in the Auto list.</summary>
    /// <param name="id">Message ID (the MessageID of the response event).</param>
    /// <param name="device">Start device (for example "D100"; for a bit device, 16 points = 1 word).</param>
    /// <param name="count">Number of words to read. E3 1 to 960, E1 1 to 64 for a word device and 1 to 32 for a bit device.</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void AutoWR(int id, string device, int count, int? timeout = null)
        => AddAuto(Current(id, McCommand.WR, device, count, null, null, timeout));

    /// <summary>Registers a one-shot BR (bit batch read).</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="device">Start bit device.</param>
    /// <param name="count">Number of points to read. E3 1 to 3584, E1 1 to 256.</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualBR(int id, string device, int count, int? repeatCount = null, int? timeout = null)
        => AddManual(Current(id, McCommand.BR, device, count, null, repeatCount, timeout));

    /// <summary>Registers a one-shot WR (word batch read).</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="device">Start device.</param>
    /// <param name="count">Number of words to read. E3 1 to 960, E1 1 to 64 for a word device and 1 to 32 for a bit device.</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualWR(int id, string device, int count, int? repeatCount = null, int? timeout = null)
        => AddManual(Current(id, McCommand.WR, device, count, null, repeatCount, timeout));

    /// <summary>Registers a one-shot BW (bit batch write).</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="device">Start bit device.</param>
    /// <param name="values">The values to write, each 0 or 1. E3 1 to 3584 values, E1 1 to 160 values.</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries). A retry resends a write the device may already have executed; do not use it for pulse, counter or incremental writes.</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualBW(int id, string device, IReadOnlyList<long> values, int? repeatCount = null, int? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        AddManual(Current(id, McCommand.BW, device, values.Count, values, repeatCount, timeout));
    }

    /// <summary>Registers a one-shot WW (word batch write).</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="device">Start device (for a bit device, 16 points = 1 word).</param>
    /// <param name="values">The words to write, each -32768 to 65535. E3 1 to 960 values, E1 1 to 64 for a word device and 1 to 10 for a bit device.</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries). A retry resends a write the device may already have executed; do not use it for pulse, counter or incremental writes.</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualWW(int id, string device, IReadOnlyList<long> values, int? repeatCount = null, int? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        AddManual(Current(id, McCommand.WW, device, values.Count, values, repeatCount, timeout));
    }

    /// <summary>Registers a write of one value by the name defined in the XML &lt;Write&gt; element. BW for a bit device, WW for a word device.</summary>
    /// <remarks>The device is interpreted with the current settings at call time, such as Frame/XYNotation (same as Manual). The check at load time used the settings that came with the XML, so changing these settings after loading can change the result.</remarks>
    /// <param name="name">The Name of the &lt;Write&gt; element.</param>
    /// <param name="value">The value to write. 0 or 1 for a bit device, -32768 to 65535 for a word device.</param>
    /// <param name="repeatCount">Retry count (if null, the RepeatCount in the XML; if that is absent too, no retries). A retry resends a write the device may already have executed; do not use it for pulse, counter or incremental writes.</param>
    /// <param name="timeout">Timeout in ms (if null, the Timeout in the XML; if that is absent too, <see cref="Timeout"/>).</param>
    /// <exception cref="ArgumentException">The name does not exist, or a value is invalid.</exception>
    public void NamedWrite(string name, long value, int? repeatCount = null, int? timeout = null)
        => NamedWrite(name, [value], repeatCount, timeout);

    /// <summary>Registers a write of several values to consecutive devices by the name defined in the XML &lt;Write&gt; element. BW for a bit device, WW for a word device.</summary>
    /// <remarks>The device is interpreted with the current settings at call time, such as Frame/XYNotation (same as Manual). The check at load time used the settings that came with the XML, so changing these settings after loading can change the result.</remarks>
    /// <param name="name">The Name of the &lt;Write&gt; element.</param>
    /// <param name="values">The values to write (same rules as <see cref="ManualBW"/> and <see cref="ManualWW"/>).</param>
    /// <param name="repeatCount">Retry count (if null, the RepeatCount in the XML; if that is absent too, no retries). A retry resends a write the device may already have executed; do not use it for pulse, counter or incremental writes.</param>
    /// <param name="timeout">Timeout in ms (if null, the Timeout in the XML; if that is absent too, <see cref="Timeout"/>).</param>
    /// <exception cref="ArgumentException">The name does not exist, or a value is invalid.</exception>
    public void NamedWrite(string name, IReadOnlyList<long> values, int? repeatCount = null, int? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        var table = namedWrites;
        var item = table.Get(name);
        var (r, t) = table.Resolve(item, repeatCount, timeout);
        AddManual(Current(item.Id, WriteCommand(item.Device, Frame, XYNotation), item.Device, values.Count, values, r, t));
    }

    /// <summary>Checks whether a named write with the given name was loaded from the XML. Names are case-sensitive.</summary>
    /// <param name="name">The Name of the &lt;Write&gt; element.</param>
    /// <returns>true if it exists.</returns>
    public bool ContainsNamedWrite(string name) => namedWrites.Contains(name);
    #endregion

    #region Config / Xml
    /// <summary>Builds a configuration object from the current communication settings, Auto list, and named write list. Manual jobs are not included.</summary>
    /// <remarks>Auto jobs were built with the Frame/XYNotation in effect at registration. If Frame or similar was changed after registration, loading the saved XML rebuilds them with the settings at save time.</remarks>
    /// <returns>The configuration object.</returns>
    public McEthernetConfig ToConfig() => new()
    {
        RemoteIP = RemoteIP,
        RemotePort = RemotePort,
        Frame = Frame,
        XYNotation = XYNotation,
        Timeout = Timeout,
        Interval = Interval,
        AutoReconnect = AutoReconnect,
        DiscardWritesWhileDisconnected = DiscardWritesWhileDisconnected,
        Autos = [.. AutoWorksSnapshot().Select(w =>
        {
            McEthernetAutoConfig a = w.Command == McCommand.BR ? new McEthernetBrConfig() : new McEthernetWrConfig();
            a.Id = w.MessageID;
            a.Device = w.Device.Name;
            a.Count = w.Count;
            if (w.Timeout is int t) a.Timeout = t;
            return a;
        })],
        Writes = namedWrites.Snapshot(),
    };

    /// <summary>Applies the communication settings, Auto list, and named write list of the configuration object. The existing Auto list and named writes are replaced by those of the configuration.</summary>
    /// <remarks>Auto items are built with the Frame and XYNotation of the configuration object. The whole input is validated before it is applied, so if any single value is invalid, nothing is changed.</remarks>
    /// <param name="config">The configuration to apply.</param>
    /// <exception cref="InvalidOperationException">Called while communicating (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">A value is invalid (the message includes the element and attribute names).</exception>
    public void ApplyConfig(McEthernetConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (IsStart) throw new InvalidOperationException("통신 중에는 설정을 바꿀 수 없습니다. Stop() 후 호출하세요.");
        config.Validate();
        var works = new List<Work>();
        foreach (var a in config.Autos ?? [])
        {
            var command = a is McEthernetBrConfig ? McCommand.BR : McCommand.WR;
            var label = $"{command} Id=\"{a.Id}\"";
            if (a.TimeoutSpecified) XmlConfig.Range(label, nameof(a.Timeout), a.Timeout, 1, 60000);
            int? t = a.TimeoutSpecified ? a.Timeout : null;
            try
            {
                works.Add(NewWork(a.Id, command, a.Device, a.Count, null, null, t, config.Frame, config.XYNotation));
            }
            catch (ArgumentException ex)
            {
                throw XmlConfig.Invalid(label, ex, new Dictionary<string, (string, string)>
                {
                    ["device"] = (nameof(a.Device), a.Device ?? ""),
                    ["count"] = (nameof(a.Count), a.Count.ToString()),
                });
            }
        }

        var table = NamedWriteTable<McEthernetWriteConfig>.Build(config.Writes, (w, label) =>
        {
            try
            {
                var command = WriteCommand(w.Device, config.Frame, config.XYNotation);
                NewWork(w.Id, command, w.Device, 1, [0], null, null, config.Frame, config.XYNotation);
            }
            catch (ArgumentException ex)
            {
                throw XmlConfig.Invalid(label, ex, new Dictionary<string, (string, string)> { ["device"] = (nameof(w.Device), w.Device ?? "") });
            }
        });

        RemoteIP = config.RemoteIP;
        RemotePort = config.RemotePort;
        Frame = config.Frame;
        XYNotation = config.XYNotation;
        Timeout = config.Timeout;
        Interval = config.Interval;
        AutoReconnect = config.AutoReconnect;
        DiscardWritesWhileDisconnected = config.DiscardWritesWhileDisconnected;
        namedWrites = table;
        ReplaceAutoWorks(works);
    }

    /// <summary>Builds an XML element from the current settings, Auto list, and named write list. Manual jobs are not included.</summary>
    /// <returns>The &lt;McEthernet&gt; element.</returns>
    public XElement ToXml() => XmlConfig.Serialize(ToConfig());

    /// <summary>Loads the settings, Auto list, and named write list from an XML element. The existing Auto list and named writes are replaced by the XML content.</summary>
    /// <remarks>
    /// <para>Omitted attributes use their default values. An unknown attribute or element is an error (to catch typos).</para>
    /// <para>The whole input is validated before it is applied, so if any single value is invalid, nothing is changed.</para>
    /// <para>You can check the format as an XSD by passing <see cref="McEthernetConfig"/> to <see cref="ConfigSchema.Get{T}"/>.</para>
    /// </remarks>
    /// <param name="element">The &lt;McEthernet&gt; element.</param>
    /// <exception cref="InvalidOperationException">Called while communicating (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">The root name differs, an unknown attribute or element is present, or a value is invalid.</exception>
    public void FromXml(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (IsStart) throw new InvalidOperationException("통신 중에는 설정을 불러올 수 없습니다. Stop() 후 호출하세요.");
        ApplyConfig(XmlConfig.Deserialize<McEthernetConfig>(element));
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
                    SetLinkUp();
                    Volatile.Write(ref sock, s);
                    try
                    {
                        Raise(DeviceOpened);
                        try
                        {
                            while (!token.IsCancellationRequested)
                            {
                                var w = TakeNextWork();
                                if (w != null) ProcessWork(s, w);
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

    Socket? TryConnect(CancellationToken token)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
            linked.CancelAfter(Math.Max(Timeout, 1000));
            s.ConnectAsync(RemoteIP, RemotePort, linked.Token).AsTask().GetAwaiter().GetResult();
            return s;
        }
        catch
        {
            s.Dispose();
            return null;
        }
    }

    void CloseSocket(Socket? s)
    {
        if (s == null) return;
        Interlocked.CompareExchange(ref sock, null, s);
        try { s.Shutdown(SocketShutdown.Both); } catch { }
        s.Dispose();
    }

    static ResultEventArgs Result(Work w) => new(w.MessageID, w.Command);

    // PLC 감시 타이머 값: 타임아웃을 250ms 단위로 올림, 1~0xFFFF
    internal static int MonitoringTimer(int timeoutMs) => Math.Clamp((timeoutMs + 249) / 250, 1, 0xFFFF);

    internal byte[] BuildRequest(Work w)
    {
        int timer = MonitoringTimer(w.Timeout ?? Timeout);
        return w.Frame == McFrame.E3
            ? Mc3EFrame.Request(w.Command, w.Device, w.Count, w.Values, timer)
            : Mc1EFrame.Request(w.Command, w.Device, w.Count, w.Values, timer);
    }

    AttemptFailedEventArgs Failed(Work w, int attempt, bool isFinal)
        => new(w.MessageID, w.Command, attempt, isFinal, requestSent);

    void ProcessWork(Socket s, Work w)
    {
        int attempt = 0;
        try
        {
            int timeout = w.Timeout ?? Timeout;
            for (; ; attempt++)
            {
                requestSent = false;
                var r = Exchange(s, w, timeout);
                if (r.Status is McStatus.Ack or McStatus.Nak)
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
            // 연결 오류로 작업이 중단되면 조용히 사라지지 않게 알린 뒤(더 시도하지 않으므로 최종), 재접속 경로가 돌도록 다시 던진다
            Raise(TimeoutReceived, Failed(w, attempt + 1, true));
            throw;
        }
    }

    void Report(Work w, McResponse r)
    {
        if (r.Status == McStatus.Nak)
            Raise(NAKReceived, new NAKEventArgs(w.MessageID, w.Command, r.ErrorCode));
        else if (w.Command is McCommand.BR or McCommand.WR)
            Raise(DataReceived, new DataEventArgs(w.MessageID, w.Command, w.Device.Name, [.. r.Values]));
        else
            Raise(WriteReceived, Result(w));
    }

    // 요청을 보내고 응답 하나를 받아 해석한다. 시간 안에 못 받으면 Invalid. 응답 머리가 어긋나면 IOException(재접속).
    McResponse Exchange(Socket s, Work w, int timeout)
    {
        var request = BuildRequest(w);
        Drain(s);   // 짝지을 번호가 없으므로 이전 요청의 늦은 응답을 먼저 버린다
        s.Send(request);
        requestSent = true;
        var sw = Stopwatch.StartNew();
        return w.Frame == McFrame.E3 ? Receive3E(s, w, timeout, sw) : Receive1E(s, w, timeout, sw);
    }

    static McResponse Receive3E(Socket s, Work w, int timeout, Stopwatch sw)
    {
        var header = ReceiveExact(s, Mc3EFrame.ResponseHeaderLength, timeout, sw);
        if (header == null) return McResponse.Invalid;
        int len = Mc3EFrame.BodyLength(header);
        if (len < 0) throw new IOException("3E 응답 머리가 올바르지 않습니다.");
        var body = ReceiveExact(s, len, timeout, sw);
        return body == null ? McResponse.Invalid : Mc3EFrame.Parse(body, w.Command, w.Count);
    }

    static McResponse Receive1E(Socket s, Work w, int timeout, Stopwatch sw)
    {
        var first = ReceiveExact(s, 1, timeout, sw);
        // 이전 5BH 오류 응답의 뒤따르는 00H가 늦게 도착해 맨 앞에 남아 있을 수 있다. 1E 응답 서브헤더는 80H~83H라서 00H는 건너뛴다
        if (first != null && first[0] == 0x00) first = ReceiveExact(s, 1, timeout, sw);
        if (first == null) return McResponse.Invalid;
        var second = ReceiveExact(s, 1, timeout, sw);
        if (second == null) return McResponse.Invalid;
        byte[] head = [first[0], second[0]];
        if (head[0] != (Mc1EFrame.Subheader(w.Command) | 0x80)) throw new IOException("1E 응답 서브헤더가 올바르지 않습니다.");
        byte[]? rest;
        if (head[1] == 0x00) rest = ReceiveExact(s, Mc1EFrame.DataLength(w.Command, w.Count), timeout, sw);
        else if (head[1] == Mc1EFrame.AbnormalEndCode)
        {
            rest = ReceiveExact(s, 1, timeout, sw);
            // FX3는 이상 코드 뒤에 00H를 하나 더 보낸다. 이미 와 있으면 같이 읽는다(늦게 오면 다음 요청 전 Drain이 버린다)
            if (rest != null && s.Available > 0)
            {
                var peek = new byte[1];
                if (s.Receive(peek, 0, 1, SocketFlags.Peek) == 1 && peek[0] == 0x00) { s.Receive(peek, 0, 1, SocketFlags.None); rest = [rest[0], 0x00]; }
            }
        }
        else rest = [];
        return rest == null ? McResponse.Invalid : Mc1EFrame.Parse([.. head, .. rest], w.Command, w.Count);
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
