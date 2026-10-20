using System.Diagnostics;
using System.IO.Ports;
using System.Xml.Linq;
using Devinno.Communication.Internal;
using Devinno.Communication.Mitsubishi.Data;

namespace Devinno.Communication.Mitsubishi.Serial;

/// <summary>
/// Mitsubishi PLC master that exchanges MC protocol 1C frames (computer link formats 1 and 4) over a serial line (RS-232/485).
/// </summary>
/// <remarks>
/// <para>Command names follow the official 1C names: BR (bit read), WR (word read), BW (bit write), WW (word write). They are common ACPU commands, so they are used with the computer link of FX3, FX5, and A series.</para>
/// <para>Devices are specified as code + number, such as "D100", "M0", "X17", "TN5" (case-insensitive). Usable codes: X Y M L S B F D W R TN TS TC CN CS CC (FX has no TC/CC).
/// X/Y numbers follow <see cref="XYNotation"/> (default Auto = octal), B/W are hexadecimal, and the rest are decimal. Because the frame field holds 4 digits for a 1-character code and 3 digits for a 2-character code, larger numbers (such as D10000) cannot be used.</para>
/// <para>Number of points handled at a time (FX3): BR 256, WR 64 words for a word device and 32 words for a bit device, BW 160, WW 64 words for a word device and 10 words for a bit device.
/// When WR/WW is used on a bit device (M, X, ...), 16 points form one word (bit0 = the start number). The start number must then be a multiple of 8 (FX), but the library does not check this, and the PLC rejects a mismatch with a NAK.
/// The 32-bit counters of FX3 (C200 to C255) are not handled specially.</para>
/// <para>Values are long. A word read is a raw value of 0 to 65535 and a bit is 0/1. A word to write accepts -32768 to 65535 and is truncated to 16 bits when sent (negative values as two's complement); a bit accepts only 0/1.</para>
/// <para><see cref="Format"/>, <see cref="SumCheck"/>, <see cref="MessageWait"/>, and <see cref="XYNotation"/> are applied to the frame when a job is registered. Jobs that are already registered do not change, so configure these first and then register. In particular, changing XYNotation after registering Auto items does not affect the items already registered, so the names written by ToConfig/SaveXml may be read in the new notation when loaded again. Decide XYNotation before registering.</para>
/// <para>When a read response is received normally, an ACK (station number + "FF") is returned to the PLC (the transmission sequence of the official manual). When the PLC sends a NAK, only <see cref="NAKReceived"/> is raised and no retry is made. If the sum check is wrong, <see cref="SumCheckErrorReceived"/> is raised and the job is retried repeatCount times.
/// If a sum check or format error occurs in a read response, no NAK is sent to the PLC and the request is sent again (retry).</para>
/// <para>The station number is 0 to 31 (00 to 1F for A series C24), and 0 to 15 is recommended for FX, but the library does not check this. The PC number is fixed to "FF" (own station). The communication settings (Baudrate, Parity, ...) must match the PLC's computer link settings.</para>
/// <para>Manual jobs are processed before Auto jobs. Responses arrive through the Received events, and the id given at registration is returned as <c>MessageID</c>.</para>
/// <para>The communication loop runs on a dedicated background thread, and all events are raised on that thread. To update the UI, marshal asynchronously, as with BeginInvoke (WinForms) or Dispatcher.BeginInvoke (WPF). Exceptions thrown in a handler are ignored and the loop continues.</para>
/// <para>Write safety: with <see cref="DiscardWritesWhileDisconnected"/> true (the default), a Manual write (BW/WW, including <see cref="NamedWrite(string, long, int?, int?)"/>) requested while the port is not open is not queued, and writes still queued when the port is lost are discarded right after <see cref="DeviceClosed"/>; both are reported through <see cref="WriteDiscarded"/>, which may also be raised on the calling thread. <see cref="Stop"/> always discards pending Manual writes. Manual reads and Auto jobs are never discarded. Each write ends with exactly one of: <see cref="WriteReceived"/>, <see cref="NAKReceived"/>, a <see cref="TimeoutReceived"/> or <see cref="SumCheckErrorReceived"/> with IsFinal true, or <see cref="WriteDiscarded"/> (jobs removed with <see cref="ClearManual"/> or <see cref="RemoveManual"/> are not reported).</para>
/// </remarks>
/// <example>
/// <code>
/// using Devinno.Communication.Mitsubishi.Serial;
/// using Devinno.Communication.Mitsubishi.Data;   // McCommand, McXYNotation
///
/// var mc = new McSerial { Port = "COM3", Baudrate = 9600, Format = McSerialFormat.Format1, SumCheck = true };
/// mc.AutoWR(1, 0, "D100", 10);
/// mc.AutoBR(2, 0, "X0", 16);
/// mc.DataReceived += (s, e) =&gt; Console.WriteLine($"{e.MessageID} {e.Device}: {string.Join(",", e.Values)}");
/// mc.NAKReceived += (s, e) =&gt; Console.WriteLine($"NAK {e.ErrorCode:X2}");
/// mc.DeviceOpened += (s, e) =&gt;
/// {
///     // writes are accepted only while the port is open (otherwise WriteDiscarded, NotConnected)
///     mc.ManualWW(10, 0, "D200", [1234, -1]);
///     mc.NamedWrite("Speed", 1500);   // calls the XML &lt;Write Name="Speed" Id="20" Slave="0" Device="D200" /&gt; by name
/// };
/// mc.Start();
/// </code>
/// </example>
public class McSerial : IDisposable
{
    // 국번 최댓값 (A 시리즈 C24: 00~1F, FX: 00~0F)
    internal const int MaxStation = 31;

    #region class : Work
    internal sealed class Work(int messageId, int slave, McCommand command, string device, int count, byte[] request, byte[]? hostAck,
        McSerialFormat format, bool sumCheck, int? repeatCount, int? timeout)
    {
        public int MessageID { get; } = messageId;
        public int Slave { get; } = slave;
        public McCommand Command { get; } = command;
        public string Device { get; } = device;
        public int Count { get; } = count;
        public byte[] Request { get; } = request;
        // 읽기 응답을 정상으로 받은 뒤 보낼 ACK (쓰기는 null)
        public byte[]? HostAck { get; } = hostAck;
        public McSerialFormat Format { get; } = format;
        public bool SumCheck { get; } = sumCheck;
        public int? RepeatCount { get; } = repeatCount;
        public int? Timeout { get; } = timeout;
        // 포트가 끊긴 동안 버리는 대상(물리 동작이 있는 쓰기)
        public bool IsWrite => Command is McCommand.BW or McCommand.WW;
    }
    #endregion

    #region class : EventArgs
    /// <summary>Common event arguments for request result events. Write response events use this type as-is.</summary>
    /// <param name="messageId">The message ID given at registration.</param>
    /// <param name="slave">Station number.</param>
    /// <param name="command">Command.</param>
    public class ResultEventArgs(int messageId, int slave, McCommand command) : EventArgs
    {
        /// <summary>The message ID given at registration.</summary>
        public int MessageID { get; } = messageId;
        /// <summary>Station number.</summary>
        public int Slave { get; } = slave;
        /// <summary>Command.</summary>
        public McCommand Command { get; } = command;
    }

    /// <summary>Event arguments for a read (BR/WR) response.</summary>
    /// <param name="messageId">Message ID.</param>
    /// <param name="slave">Station number.</param>
    /// <param name="command">Command (BR or WR).</param>
    /// <param name="device">Start device (uppercased name, for example "D100").</param>
    /// <param name="values">Values read. 0/1 for BR, a raw value of 0 to 65535 for WR.</param>
    public class DataEventArgs(int messageId, int slave, McCommand command, string device, long[] values)
        : ResultEventArgs(messageId, slave, command)
    {
        /// <summary>Start device (uppercased name, for example "D100").</summary>
        public string Device { get; } = device;
        /// <summary>Values read. 0/1 for BR, a raw value of 0 to 65535 for WR (convert with (short) if a sign is needed).</summary>
        public long[] Values { get; } = values;
    }

    /// <summary>Event arguments for when the PLC sends a NAK.</summary>
    /// <param name="messageId">Message ID.</param>
    /// <param name="slave">Station number.</param>
    /// <param name="command">Command.</param>
    /// <param name="errorCode">NAK error code (for example 0x02 sum check error, 0x06 device specification error, 0x10 PC number error).</param>
    public class NAKEventArgs(int messageId, int slave, McCommand command, int errorCode)
        : ResultEventArgs(messageId, slave, command)
    {
        /// <summary>NAK error code (for example 0x02 sum check error, 0x06 device specification error, 0x10 PC number error).</summary>
        public int ErrorCode { get; } = errorCode;
    }

    /// <summary>Event arguments for a failed attempt (<see cref="TimeoutReceived"/>, <see cref="SumCheckErrorReceived"/>).</summary>
    /// <param name="messageId">The message ID given at registration.</param>
    /// <param name="slave">Station number.</param>
    /// <param name="command">Command.</param>
    /// <param name="attempt">Attempt number, starting at 1 (1 = first attempt, 2 = first retry, ...).</param>
    /// <param name="isFinal">Whether this job will not be attempted again (retries used up or the port was lost).</param>
    /// <param name="requestSent">Whether the request of this attempt was handed to the send function.</param>
    public class AttemptFailedEventArgs(int messageId, int slave, McCommand command, int attempt, bool isFinal, bool requestSent)
        : ResultEventArgs(messageId, slave, command)
    {
        /// <summary>Attempt number, starting at 1 (1 = first attempt, 2 = first retry, ...).</summary>
        public int Attempt { get; } = attempt;
        /// <summary>Whether this job will not be attempted again (retries used up or the port was lost).</summary>
        public bool IsFinal { get; } = isFinal;
        /// <summary>Whether the request of this attempt was handed to the send function. If false, the device did not receive this attempt; if true, the result is unknown and the device may have executed it.</summary>
        public bool RequestSent { get; } = requestSent;
    }
    #endregion

    #region Properties
    /// <summary>Serial port name (default: "COM1").</summary>
    public string Port { get; set; } = "COM1";
    /// <summary>Baud rate in bps (default: 9600).</summary>
    public int Baudrate { get; set; } = 9600;
    /// <summary>Parity (default: None). Must match the PLC computer link setting.</summary>
    public Parity Parity { get; set; } = Parity.None;
    /// <summary>Data bits (default: 8). Must match the PLC computer link setting (7 or 8 bits).</summary>
    public int DataBits { get; set; } = 8;
    /// <summary>Stop bits (default: One).</summary>
    public StopBits StopBits { get; set; } = StopBits.One;

    /// <summary>Frame format (default: Format1). Format4 appends CR LF to the end of every frame. Applied at registration.</summary>
    public McSerialFormat Format { get; set; } = McSerialFormat.Format1;
    /// <summary>Whether to use the sum check (default: true). Must match the PLC setting. Applied at registration.</summary>
    public bool SumCheck { get; set; } = true;
    /// <summary>Message wait time, 0 to 15 (x10 ms, default: 0). The minimum wait before the PLC responds. Applied at registration.</summary>
    public int MessageWait { get; set; } = 0;
    /// <summary>Number base of X/Y numbers (default: Auto = octal). Applied at registration.</summary>
    public McXYNotation XYNotation { get; set; } = McXYNotation.Auto;

    /// <summary>Response timeout in ms (default: 1000). A per-job timeout, if given, takes precedence.</summary>
    public int Timeout { get; set; } = 1000;
    /// <summary>Wait between jobs in ms (default: 10).</summary>
    public int Interval { get; set; } = 10;
    /// <summary>Whether to reopen the port at 1-second intervals when it could not be opened or was lost (default: false).</summary>
    public bool AutoReconnect { get; set; } = false;
    /// <summary>Whether to discard writes while the port is not open (default: true). When true, a Manual write or named write requested while the port is not open raises <see cref="WriteDiscarded"/> (NotConnected) instead of being queued, and writes still queued when the port is lost are discarded (Disconnected). When false, they stay queued and are sent after the next open. Manual reads and Auto jobs are not affected, and <see cref="Stop"/> discards pending writes regardless of this setting.</summary>
    public bool DiscardWritesWhileDisconnected { get; set; } = true;

    /// <summary>Gets whether the serial port is open.</summary>
    public bool IsOpen => Volatile.Read(ref port)?.IsOpen ?? false;
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
    SerialPort? port;
    Thread? thread;
    CancellationTokenSource? cts;
    // 포트가 열려 있는지(workLock 보호). 설정이 true면 쓰기는 이것이 true일 때만 대기열에 들어간다
    bool linkUp;
    // linkUp을 올린 루프 스레드(workLock 보호). 핸들러 안 Stop→Start로 새 루프가 뜬 뒤 옛 루프의 SetLinkDown이 새 연결 상태를 덮지 않게 한다
    Thread? linkThread;
    // 이번 시도의 요청을 송신 함수에 넘겼는지(통신 스레드 전용)
    bool requestSent;
    #endregion

    #region Event
    /// <summary>Raised when a read (BR/WR) response is received.</summary>
    public event EventHandler<DataEventArgs>? DataReceived;
    /// <summary>Raised when a write (BW/WW) response (ACK) is received.</summary>
    public event EventHandler<ResultEventArgs>? WriteReceived;
    /// <summary>Raised when the PLC sends a NAK. No retry is made.</summary>
    public event EventHandler<NAKEventArgs>? NAKReceived;
    /// <summary>Raised when there is no response, the response is malformed, or the port is lost during a request. Raised on every retry; IsFinal tells whether the job will be attempted again. If RequestSent is true, the device may have executed the request — read it back before resending.</summary>
    public event EventHandler<AttemptFailedEventArgs>? TimeoutReceived;
    /// <summary>Raised when the sum check of a response is incorrect. Raised on every retry; IsFinal tells whether the job will be attempted again.</summary>
    public event EventHandler<AttemptFailedEventArgs>? SumCheckErrorReceived;
    /// <summary>Raised when a Manual write is discarded without being sent (see <see cref="DiscardWritesWhileDisconnected"/>). May be raised on the calling thread: NotConnected inside the Manual/NamedWrite call, Stopped inside Stop(); otherwise on the communication thread.</summary>
    public event EventHandler<WriteDiscardedEventArgs>? WriteDiscarded;
    /// <summary>Raised when the serial port is opened.</summary>
    public event EventHandler? DeviceOpened;
    /// <summary>Raised when the serial port is closed.</summary>
    public event EventHandler? DeviceClosed;
    #endregion

    #region Start / Stop
    /// <summary>Opens the serial port and starts the communication loop. Does nothing if it is already running.</summary>
    /// <remarks>If the port cannot be opened and <see cref="AutoReconnect"/> is false, the loop ends and <see cref="IsStart"/> becomes false.</remarks>
    public void Start()
    {
        lock (lifeLock)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (IsStart) return;
            cts = new CancellationTokenSource();
            var token = cts.Token;
            IsStart = true;
            thread = new Thread(() => Run(token)) { IsBackground = true, Name = nameof(McSerial) };
            thread.Start();
        }
    }

    /// <summary>Stops the communication loop and closes the port. Waits up to (Timeout + 1000) ms for the loop to end. Pending Manual writes are discarded (WriteDiscarded, Stopped).</summary>
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
        ClosePort(Volatile.Read(ref port));
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

    // 작업 하나를 만든다. 프레임 설정(format, sumCheck, messageWait, notation)은 인자로 받는다(ApplyConfig가 적용 전에 검사할 수 있도록).
    internal static Work NewWork(int id, int slave, McCommand command, string device, int count, IReadOnlyList<long>? values,
        int? repeatCount, int? timeout, McSerialFormat format, bool sumCheck, int messageWait, McXYNotation notation)
    {
        if (slave < 0 || slave > MaxStation) throw new ArgumentOutOfRangeException(nameof(slave), $"국번은 0~{MaxStation} 입니다 (FX는 0~15).");
        if (messageWait < 0 || messageWait > 15) throw new ArgumentOutOfRangeException(nameof(MessageWait), "메시지 대기는 0~15 (×10ms) 입니다.");
        var d = McDevice.ParseFor(command, device, count, McTarget.Serial1C, McDevice.IsOctal(notation, true), nameof(device), values == null ? nameof(count) : nameof(values));
        if (values != null) McDevice.CheckValues(command, values, nameof(values));
        var request = McSerialFrame.Request(slave, command, messageWait, d, count, values, format, sumCheck);
        var hostAck = command is McCommand.BR or McCommand.WR ? McSerialFrame.HostAck(slave, format) : null;
        return new Work(id, slave, command, d.Name, count, request, hostAck, format, sumCheck, repeatCount, timeout);
    }

    Work Current(int id, int slave, McCommand command, string device, int count, IReadOnlyList<long>? values, int? repeatCount, int? timeout)
        => NewWork(id, slave, command, device, count, values, repeatCount, timeout, Format, SumCheck, MessageWait, XYNotation);

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

    // 포트를 열었다. port를 공개하기 전에 부른다(IsOpen이 true면 이미 linkUp)
    internal void SetLinkUp()
    {
        lock (workLock)
        {
            linkUp = true;
            linkThread = Thread.CurrentThread;
        }
    }

    // 포트를 잃었다. DeviceClosed 다음에 부른다. stopping이면 Stop으로 닫힌 것
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
    volatile NamedWriteTable<McSerialWriteConfig> namedWrites = NamedWriteTable<McSerialWriteConfig>.Empty;

    // 디바이스가 비트면 BW, 워드면 WW (X/Y 표기는 인자로 받는다)
    static McCommand WriteCommand(string device, McXYNotation notation)
        => McDevice.Parse(device, McTarget.Serial1C, McDevice.IsOctal(notation, true), nameof(device)).IsBit ? McCommand.BW : McCommand.WW;
    #endregion

    #region Auto / Manual
    /// <summary>Registers a BR (bit batch read) in the Auto list.</summary>
    /// <param name="id">Message ID (the MessageID of the response event).</param>
    /// <param name="slave">Station number, 0 to 31 (0 to 15 for FX).</param>
    /// <param name="device">Start bit device (for example "M0", "X10").</param>
    /// <param name="count">Number of points to read, 1 to 256.</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void AutoBR(int id, int slave, string device, int count, int? timeout = null)
        => AddAuto(Current(id, slave, McCommand.BR, device, count, null, null, timeout));

    /// <summary>Registers a WR (word batch read) in the Auto list.</summary>
    /// <param name="id">Message ID (the MessageID of the response event).</param>
    /// <param name="slave">Station number, 0 to 31 (0 to 15 for FX).</param>
    /// <param name="device">Start device (for example "D100"; for a bit device, 16 points = 1 word).</param>
    /// <param name="count">Number of words to read. 1 to 64 for a word device, 1 to 32 for a bit device.</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void AutoWR(int id, int slave, string device, int count, int? timeout = null)
        => AddAuto(Current(id, slave, McCommand.WR, device, count, null, null, timeout));

    /// <summary>Registers a one-shot BR (bit batch read).</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="slave">Station number, 0 to 31 (0 to 15 for FX).</param>
    /// <param name="device">Start bit device.</param>
    /// <param name="count">Number of points to read, 1 to 256.</param>
    /// <param name="repeatCount">Retry count when there is no response or the sum check is wrong (null for no retries).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualBR(int id, int slave, string device, int count, int? repeatCount = null, int? timeout = null)
        => AddManual(Current(id, slave, McCommand.BR, device, count, null, repeatCount, timeout));

    /// <summary>Registers a one-shot WR (word batch read).</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="slave">Station number, 0 to 31 (0 to 15 for FX).</param>
    /// <param name="device">Start device.</param>
    /// <param name="count">Number of words to read. 1 to 64 for a word device, 1 to 32 for a bit device.</param>
    /// <param name="repeatCount">Retry count when there is no response or the sum check is wrong (null for no retries).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualWR(int id, int slave, string device, int count, int? repeatCount = null, int? timeout = null)
        => AddManual(Current(id, slave, McCommand.WR, device, count, null, repeatCount, timeout));

    /// <summary>Registers a one-shot BW (bit batch write).</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="slave">Station number, 0 to 31 (0 to 15 for FX).</param>
    /// <param name="device">Start bit device.</param>
    /// <param name="values">1 to 160 values to write, each 0 or 1.</param>
    /// <param name="repeatCount">Retry count when there is no response or the sum check is wrong (null for no retries). A retry resends a write the device may already have executed; do not use it for pulse, counter or incremental writes.</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualBW(int id, int slave, string device, IReadOnlyList<long> values, int? repeatCount = null, int? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        AddManual(Current(id, slave, McCommand.BW, device, values.Count, values, repeatCount, timeout));
    }

    /// <summary>Registers a one-shot WW (word batch write).</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="slave">Station number, 0 to 31 (0 to 15 for FX).</param>
    /// <param name="device">Start device (for a bit device, 16 points = 1 word).</param>
    /// <param name="values">Words to write. 1 to 64 for a word device, 1 to 10 for a bit device. Each -32768 to 65535.</param>
    /// <param name="repeatCount">Retry count when there is no response or the sum check is wrong (null for no retries). A retry resends a write the device may already have executed; do not use it for pulse, counter or incremental writes.</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualWW(int id, int slave, string device, IReadOnlyList<long> values, int? repeatCount = null, int? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        AddManual(Current(id, slave, McCommand.WW, device, values.Count, values, repeatCount, timeout));
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
        AddManual(Current(item.Id, item.Slave, WriteCommand(item.Device, XYNotation), item.Device, values.Count, values, r, t));
    }

    /// <summary>Checks whether a named write with the given name was loaded from the XML. Names are case-sensitive.</summary>
    /// <param name="name">The Name of the &lt;Write&gt; element.</param>
    /// <returns>true if it exists.</returns>
    public bool ContainsNamedWrite(string name) => namedWrites.Contains(name);
    #endregion

    #region Config / Xml
    /// <summary>Builds a configuration object from the current communication settings, Auto list, and named write list. Manual jobs are not included.</summary>
    /// <remarks>Auto jobs were built with the frame settings in effect at registration. If Format or similar was changed after registration, loading the saved XML rebuilds them with the settings at save time.</remarks>
    /// <returns>The configuration object.</returns>
    public McSerialConfig ToConfig() => new()
    {
        Port = Port,
        Baudrate = Baudrate,
        Parity = Parity,
        DataBits = DataBits,
        StopBits = StopBits,
        Format = Format,
        SumCheck = SumCheck,
        MessageWait = MessageWait,
        XYNotation = XYNotation,
        Timeout = Timeout,
        Interval = Interval,
        AutoReconnect = AutoReconnect,
        DiscardWritesWhileDisconnected = DiscardWritesWhileDisconnected,
        Autos = [.. AutoWorksSnapshot().Select(w =>
        {
            McSerialAutoConfig a = w.Command == McCommand.BR ? new McSerialBrConfig() : new McSerialWrConfig();
            a.Id = w.MessageID;
            a.Slave = w.Slave;
            a.Device = w.Device;
            a.Count = w.Count;
            if (w.Timeout is int t) a.Timeout = t;
            return a;
        })],
        Writes = namedWrites.Snapshot(),
    };

    /// <summary>Applies the communication settings, Auto list, and named write list of the configuration object. The existing Auto list and named writes are replaced by those of the configuration.</summary>
    /// <remarks>Auto items are built with the Format, SumCheck, MessageWait, and XYNotation of the configuration object. The whole input is validated before it is applied, so if any single value is invalid, nothing is changed.</remarks>
    /// <param name="config">The configuration to apply.</param>
    /// <exception cref="InvalidOperationException">Called while communicating (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">A value is invalid (the message includes the element and attribute names).</exception>
    public void ApplyConfig(McSerialConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (IsStart) throw new InvalidOperationException("통신 중에는 설정을 바꿀 수 없습니다. Stop() 후 호출하세요.");
        config.Validate();
        var works = new List<Work>();
        foreach (var a in config.Autos ?? [])
        {
            var command = a is McSerialBrConfig ? McCommand.BR : McCommand.WR;
            var label = $"{command} Id=\"{a.Id}\"";
            if (a.TimeoutSpecified) XmlConfig.Range(label, nameof(a.Timeout), a.Timeout, 1, 60000);
            int? t = a.TimeoutSpecified ? a.Timeout : null;
            try
            {
                works.Add(NewWork(a.Id, a.Slave, command, a.Device, a.Count, null, null, t, config.Format, config.SumCheck, config.MessageWait, config.XYNotation));
            }
            catch (ArgumentException ex)
            {
                throw XmlConfig.Invalid(label, ex, new Dictionary<string, (string, string)>
                {
                    ["slave"] = (nameof(a.Slave), a.Slave.ToString()),
                    ["device"] = (nameof(a.Device), a.Device ?? ""),
                    ["count"] = (nameof(a.Count), a.Count.ToString()),
                });
            }
        }

        var table = NamedWriteTable<McSerialWriteConfig>.Build(config.Writes, (w, label) =>
        {
            try
            {
                var command = WriteCommand(w.Device, config.XYNotation);
                NewWork(w.Id, w.Slave, command, w.Device, 1, [0], null, null, config.Format, config.SumCheck, config.MessageWait, config.XYNotation);
            }
            catch (ArgumentException ex)
            {
                throw XmlConfig.Invalid(label, ex, new Dictionary<string, (string, string)>
                {
                    ["slave"] = (nameof(w.Slave), w.Slave.ToString()),
                    ["device"] = (nameof(w.Device), w.Device ?? ""),
                });
            }
        });

        Port = config.Port;
        Baudrate = config.Baudrate;
        Parity = config.Parity;
        DataBits = config.DataBits;
        StopBits = config.StopBits;
        Format = config.Format;
        SumCheck = config.SumCheck;
        MessageWait = config.MessageWait;
        XYNotation = config.XYNotation;
        Timeout = config.Timeout;
        Interval = config.Interval;
        AutoReconnect = config.AutoReconnect;
        DiscardWritesWhileDisconnected = config.DiscardWritesWhileDisconnected;
        namedWrites = table;
        ReplaceAutoWorks(works);
    }

    /// <summary>Builds an XML element from the current settings, Auto list, and named write list. Manual jobs are not included.</summary>
    /// <returns>The &lt;McSerial&gt; element.</returns>
    public XElement ToXml() => XmlConfig.Serialize(ToConfig());

    /// <summary>Loads the settings, Auto list, and named write list from an XML element. The existing Auto list and named writes are replaced by the XML content.</summary>
    /// <remarks>
    /// <para>Omitted attributes use their default values. An unknown attribute or element is an error (to catch typos).</para>
    /// <para>The whole input is validated before it is applied, so if any single value is invalid, nothing is changed.</para>
    /// <para>You can check the format as an XSD by passing <see cref="McSerialConfig"/> to <see cref="ConfigSchema.Get{T}"/>.</para>
    /// </remarks>
    /// <param name="element">The &lt;McSerial&gt; element.</param>
    /// <exception cref="InvalidOperationException">Called while communicating (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">The root name differs, an unknown attribute or element is present, or a value is invalid.</exception>
    public void FromXml(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (IsStart) throw new InvalidOperationException("통신 중에는 설정을 불러올 수 없습니다. Stop() 후 호출하세요.");
        ApplyConfig(XmlConfig.Deserialize<McSerialConfig>(element));
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
                var sp = TryOpen();
                if (sp != null)
                {
                    SetLinkUp();
                    Volatile.Write(ref port, sp);
                    try
                    {
                        Raise(DeviceOpened);
                        try
                        {
                            while (!token.IsCancellationRequested)
                            {
                                var w = TakeNextWork();
                                if (w != null) ProcessWork(sp, w);
                                if (token.IsCancellationRequested) break;
                                token.WaitHandle.WaitOne(w != null ? Interval : Math.Max(Interval, 10));
                            }
                        }
                        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException) { }
                    }
                    finally
                    {
                        ClosePort(sp);
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

    SerialPort? TryOpen()
    {
        SerialPort? sp = null;
        try
        {
            sp = new SerialPort(Port, Baudrate, Parity, DataBits, StopBits);
            sp.Open();
            return sp;
        }
        catch
        {
            sp?.Dispose();
            return null;
        }
    }

    void ClosePort(SerialPort? sp)
    {
        if (sp == null) return;
        Interlocked.CompareExchange(ref port, null, sp);
        try { sp.Close(); } catch { }
        sp.Dispose();
    }

    static ResultEventArgs Result(Work w) => new(w.MessageID, w.Slave, w.Command);

    void ProcessWork(SerialPort sp, Work w)
        => Process(w,
            (request, timeout) =>
            {
                sp.DiscardInBuffer();
                sp.Write(request, 0, request.Length);
                requestSent = true;
                return ReadFrame(sp, timeout, w.Format, w.SumCheck);
            },
            ack => sp.Write(ack, 0, ack.Length));

    AttemptFailedEventArgs Failed(Work w, int attempt, bool isFinal)
        => new(w.MessageID, w.Slave, w.Command, attempt, isFinal, requestSent);

    // 작업 하나의 송수신·재시도·이벤트. exchange(요청, 타임아웃) = 요청을 보내고 응답 프레임(없으면 null), send = 컴퓨터 ACK 송신.
    // 정상 읽기 응답이면 이벤트보다 먼저 ACK를 보낸다. 마지막 상태를 돌려준다.
    // requestSent는 실제 송신 직후 exchange 안에서 세우고, exchange가 예외 없이 돌아오면 여기서도 세운다(시험용 exchange)
    internal McStatus Process(Work w, Func<byte[], int, byte[]?> exchange, Action<byte[]> send)
    {
        int attempt = 0;
        try
        {
            int timeout = w.Timeout ?? Timeout;
            for (; ; attempt++)
            {
                requestSent = false;
                var frame = exchange(w.Request, timeout);
                requestSent = true;
                var r = frame == null ? McResponse.Invalid : McSerialFrame.Parse(frame, w.Command, w.Slave, w.Count, w.Format, w.SumCheck);
                if (r.Status == McStatus.Ack && w.HostAck != null) send(w.HostAck);
                if (r.Status is McStatus.Ack or McStatus.Nak)
                {
                    Report(w, r);
                    return r.Status;
                }
                bool isFinal = attempt >= (w.RepeatCount ?? 0);
                Raise(r.Status == McStatus.SumError ? SumCheckErrorReceived : TimeoutReceived, Failed(w, attempt + 1, isFinal));
                if (isFinal) return r.Status;
            }
        }
        catch
        {
            // 포트 오류로 작업이 중단되면 조용히 사라지지 않게 알린 뒤(더 시도하지 않으므로 최종), 재접속 경로가 돌도록 다시 던진다
            Raise(TimeoutReceived, Failed(w, attempt + 1, true));
            throw;
        }
    }

    void Report(Work w, McResponse r)
    {
        if (r.Status == McStatus.Nak)
            Raise(NAKReceived, new NAKEventArgs(w.MessageID, w.Slave, w.Command, r.ErrorCode));
        else if (w.Command is McCommand.BR or McCommand.WR)
            Raise(DataReceived, new DataEventArgs(w.MessageID, w.Slave, w.Command, w.Device, [.. r.Values]));
        else
            Raise(WriteReceived, Result(w));
    }

    // 응답 프레임 하나를 받을 때까지 읽는다. 시간 안에 못 받으면 null.
    static byte[]? ReadFrame(SerialPort sp, int timeout, McSerialFormat format, bool sumCheck)
    {
        var buf = new byte[1024];
        int n = 0;
        var sw = Stopwatch.StartNew();
        while (true)
        {
            int remain = timeout - (int)sw.ElapsedMilliseconds;
            if (remain <= 0 || n == buf.Length) return null;
            sp.ReadTimeout = remain;
            try { n += sp.Read(buf, n, buf.Length - n); }
            catch (TimeoutException) { return null; }
            var (start, len) = McSerialFrame.Find(buf.AsSpan(0, n), format, sumCheck);
            if (len > 0) return buf.AsSpan(start, len).ToArray();
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
