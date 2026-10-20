using System.Diagnostics;
using System.IO.Ports;
using System.Xml.Linq;
using Devinno.Communication.Internal;
using Devinno.Communication.Omron.Data;

namespace Devinno.Communication.Omron.Serial;

/// <summary>
/// Master that exchanges FINS commands (header code FA) with an Omron PLC over serial (RS-232C/422/485) Host Link.
/// Targets the CS/CJ/CP series (the built-in and optional serial ports of the CP1E-N/NA, CP1L, CP1H, CJ1/CJ2, and CS1, and the SCU/SCB). The older C series (C-mode only) is not supported.
/// </summary>
/// <remarks>
/// <para>The PLC serial port must be set to Host Link (SYSWAY) mode. The communication settings must match the PLC; the defaults are the Omron Host Link defaults (9600 bps, 7 data bits, Even, 2 stop bits).
/// <see cref="UnitNumber"/> is the Host Link unit number of the PLC (0 to 31); it is a single property on the assumption of one PLC per serial line.</para>
/// <para>Frame: '@' unit number "FA" response wait (<see cref="ResponseWait"/>) "00000000" (ICF, DA2, SA2, SID) FINS command (hexadecimal ASCII) FCS '*' CR.
/// The FCS is the XOR of all characters from '@' up to just before the FCS. If the FCS of a response is wrong, <see cref="FcsErrorReceived"/> is raised and the request is retried up to repeatCount times.</para>
/// <para>Addresses (case-insensitive): CIO100 or 100 (CIO), W10, H20, A100, D100 (DM100 is also accepted), E0_100 (EM bank 0; the bank is hexadecimal, 0 to 18).
/// For bits, append ".00" to ".15" (CIO100.05, 100.05, W10.01, D100.15). Events return the canonical form (e.g., "CIO100.05", "D100").
/// Word ranges are CIO 0 to 6143, W and H 0 to 511, A 0 to 959, and D and EM 0 to 32767. Timers and counters (T/C) are not supported.</para>
/// <para>Values are <see cref="long"/>. A word read is a raw value of 0 to 65535 and a bit is 0/1. A word to write must be -32768 to 65535 (negative values are sent as two's complement), and a bit accepts only 0/1.</para>
/// <para>A Host Link frame can carry only up to 540 bytes of FINS command and 538 bytes of response, so a random (individual) read (Read) covers 1 to 134 addresses and
/// a block read/write (ReadBlock/WriteBlock) is split into chunks of <see cref="MaxWordsPerRequest"/> items (default and maximum 266). Bits are split by the same count.
/// The event is raised once only if every chunk succeeds; if any chunk fails, only <see cref="NAKReceived"/> is raised. A block write that was split is not atomic.</para>
/// <para>If the PLC sends an error end code, only <see cref="NAKReceived"/> is raised and no retry is made. The CPU error flags in the end code (0x0040 non-fatal, 0x0080 fatal) are not treated as errors.</para>
/// <para>Manual jobs are processed before Auto jobs. The communication loop runs on a dedicated background thread, and all events are raised on that thread.
/// To update the UI, marshal asynchronously, as with BeginInvoke (WinForms) or Dispatcher.BeginInvoke (WPF). Exceptions thrown in a handler are ignored and the loop continues.</para>
/// </remarks>
/// <example>
/// <code>
/// using Devinno.Communication.Omron.Data;      // FinsCommand
/// using Devinno.Communication.Omron.Serial;
///
/// var plc = new FinsSerial { Port = "COM3", UnitNumber = 0 };   // 9600, 7, Even, 2 (Host Link defaults)
/// plc.AutoRead(1, ["D100", "CIO0.00"]);
/// plc.AutoReadBlock(2, "D0", 100);
/// plc.DataReceived += (s, e) =&gt; Console.WriteLine($"{e.MessageID}: {string.Join(",", e.Values)}");
/// plc.FcsErrorReceived += (s, e) =&gt; Console.WriteLine("FCS error, retrying");
/// plc.NAKReceived += (s, e) =&gt; Console.WriteLine($"End code 0x{e.ErrorCode:X4}");
/// plc.Start();
/// plc.ManualWrite(10, "D200", 1234, repeatCount: 2);
/// plc.NamedWrite("Speed", 1500);   // calls the XML &lt;Write Name="Speed" Id="20" Address="D200" /&gt; by name
/// </code>
/// </example>
public class FinsSerial : IDisposable
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
    }
    #endregion


    #region class : EventArgs
    /// <summary>Common event arguments for request result events. Write response and timeout events use this type as-is.</summary>
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
    }
    #endregion


    #region Properties
    int unitNumber, responseWait, maxWords = FinsFrame.SerialMaxWords;

    /// <summary>Serial port name (default: "COM1").</summary>
    public string Port { get; set; } = "COM1";
    /// <summary>Baud rate in bps (default: 9600). Must match the PLC Host Link setting.</summary>
    public int Baudrate { get; set; } = 9600;
    /// <summary>Parity (default: Even). Must match the PLC Host Link setting.</summary>
    public Parity Parity { get; set; } = Parity.Even;
    /// <summary>Data bits (default: 7). Must match the PLC Host Link setting (7 or 8 bits).</summary>
    public int DataBits { get; set; } = 7;
    /// <summary>Stop bits (default: Two). Must match the PLC Host Link setting.</summary>
    public StopBits StopBits { get; set; } = StopBits.Two;

    /// <summary>Host Link unit number of the PLC, 0 to 31 (default: 0). It is read at execution time.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Outside 0 to 31.</exception>
    public int UnitNumber { get => Volatile.Read(ref unitNumber); set => Volatile.Write(ref unitNumber, Check(value, 0, 31, nameof(UnitNumber))); }
    /// <summary>PLC response wait time, 0 to 15 (x10 ms, default: 0). The minimum wait before the PLC responds. It is read at execution time.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Outside 0 to 15.</exception>
    public int ResponseWait { get => Volatile.Read(ref responseWait); set => Volatile.Write(ref responseWait, Check(value, 0, 15, nameof(ResponseWait))); }
    /// <summary>Maximum number of items (words or bits) per request for block read/write, 1 to 266 (default: 266 = the Host Link frame limit). It is read at execution time.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Outside 1 to 266.</exception>
    public int MaxWordsPerRequest { get => Volatile.Read(ref maxWords); set => Volatile.Write(ref maxWords, Check(value, 1, FinsFrame.SerialMaxWords, nameof(MaxWordsPerRequest))); }

    /// <summary>Response timeout in ms (default: 1000). A per-job timeout, if given, takes precedence. For a split block read/write, it applies to each chunk.</summary>
    public int Timeout { get; set; } = 1000;
    /// <summary>Wait between jobs in ms (default: 10).</summary>
    public int Interval { get; set; } = 10;
    /// <summary>Whether to reopen the port at 1-second intervals when it could not be opened or was lost (default: false).</summary>
    public bool AutoReconnect { get; set; } = false;

    /// <summary>Gets whether the serial port is open.</summary>
    public bool IsOpen => Volatile.Read(ref port)?.IsOpen ?? false;
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
    SerialPort? port;
    Thread? thread;
    CancellationTokenSource? cts;
    #endregion

    #region Event
    /// <summary>Raised when a read (Read/ReadBlock) response is received. For ReadBlock, raised once after all chunks have been received.</summary>
    public event EventHandler<DataEventArgs>? DataReceived;
    /// <summary>Raised when a write (Write/WriteBlock) succeeds. A block write raises it once after all chunks succeed.</summary>
    public event EventHandler<ResultEventArgs>? WriteReceived;
    /// <summary>Raised when the PLC sends an error end code. No retry is made.</summary>
    public event EventHandler<NAKEventArgs>? NAKReceived;
    /// <summary>Raised when there is no response, the response is malformed, or the port is lost during a request. Raised on every retry.</summary>
    public event EventHandler<ResultEventArgs>? TimeoutReceived;
    /// <summary>Raised when the FCS of a response does not match. Raised on every retry.</summary>
    public event EventHandler<ResultEventArgs>? FcsErrorReceived;
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
            thread = new Thread(() => Run(token)) { IsBackground = true, Name = nameof(FinsSerial) };
            thread.Start();
        }
    }

    /// <summary>Stops the communication loop and closes the port. Waits up to (Timeout + 1000) ms for the loop to end.</summary>
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

    static Work ToWork(int id, FinsRequest r, int? repeatCount, int? timeout) => new(id, r.Command, r.Items, r.Count, r.Values, repeatCount, timeout);

    static Work ReadWork(int id, IReadOnlyList<string> addresses, int? repeatCount, int? timeout)
        => ToWork(id, FinsRequest.Read(addresses, FinsFrame.SerialMaxItems), repeatCount, timeout);

    static Work ReadBlockWork(int id, string address, int count, int? repeatCount, int? timeout)
        => ToWork(id, FinsRequest.ReadBlock(address, count), repeatCount, timeout);

    static Work WriteWork(int id, string address, long value, int? repeatCount, int? timeout)
        => ToWork(id, FinsRequest.Write(address, value), repeatCount, timeout);

    static Work WriteBlockWork(int id, string address, IReadOnlyList<long> values, int? repeatCount, int? timeout)
        => ToWork(id, FinsRequest.WriteBlock(address, values), repeatCount, timeout);

    void AddAuto(Work w) { lock (workLock) autoWorks.Add(w); }
    void AddManual(Work w) { lock (workLock) manualWorks.Add(w); }

    // 이름 있는 쓰기 표. NamedWrite는 실행 중에도 불리므로 통째로 바꾸는 참조(volatile)로 둔다. ApplyConfig에서만 교체한다.
    volatile NamedWriteTable<FinsWriteConfig> namedWrites = NamedWriteTable<FinsWriteConfig>.Empty;
    #endregion


    #region Auto / Manual
    /// <summary>Registers a random (individual) read (Read, 0104) in the Auto list. The response arrives through <see cref="DataReceived"/>.</summary>
    /// <param name="id">Message ID (the MessageID of the response event).</param>
    /// <param name="addresses">1 to 134 word or bit addresses (e.g., ["D100", "CIO0.05", "W10"]).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    /// <exception cref="ArgumentException">The address format is invalid or the count is outside 1 to 134.</exception>
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
    /// <param name="addresses">1 to 134 word or bit addresses.</param>
    /// <param name="repeatCount">Retry count when there is no response or the FCS is wrong (if null, no retry).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    /// <exception cref="ArgumentException">The address format is invalid or the count is outside 1 to 134.</exception>
    public void ManualRead(int id, IReadOnlyList<string> addresses, int? repeatCount = null, int? timeout = null)
        => AddManual(ReadWork(id, addresses, repeatCount, timeout));

    /// <summary>Registers a one-shot block read (ReadBlock, 0101). Processed before Auto jobs.</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="address">Start address (a word address reads words, a bit address reads bits).</param>
    /// <param name="count">Number of items to read, 1 to 65535.</param>
    /// <param name="repeatCount">Retry count when there is no response or the FCS is wrong (if null, no retry). A retry starts again from the first chunk.</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>; applies to each chunk).</param>
    /// <exception cref="ArgumentException">The address format is invalid or the count is out of range.</exception>
    public void ManualReadBlock(int id, string address, int count, int? repeatCount = null, int? timeout = null)
        => AddManual(ReadBlockWork(id, address, count, repeatCount, timeout));

    /// <summary>Registers a one-shot write (Write, 0102). <see cref="WriteReceived"/> is raised on success.</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="address">Word or bit address (e.g., "D100", "W10.03").</param>
    /// <param name="value">Value to write. A word is -32768 to 65535 (negative values as two's complement); a bit is 0/1.</param>
    /// <param name="repeatCount">Retry count when there is no response or the FCS is wrong (if null, no retry).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    /// <exception cref="ArgumentException">The address or value is invalid.</exception>
    public void ManualWrite(int id, string address, long value, int? repeatCount = null, int? timeout = null)
        => AddManual(WriteWork(id, address, value, repeatCount, timeout));

    /// <summary>Registers a one-shot block write (WriteBlock, 0102). <see cref="WriteReceived"/> is raised once if all chunks succeed.</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="address">Start address (a word address writes words, a bit address writes bits).</param>
    /// <param name="values">1 to 65535 values to write (copied at registration). Words are -32768 to 65535 and bits are 0/1. Written in chunks of <see cref="MaxWordsPerRequest"/> items and not atomic.</param>
    /// <param name="repeatCount">Retry count when there is no response or the FCS is wrong (if null, no retry). A retry starts again from the first chunk.</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>; applies to each chunk).</param>
    /// <exception cref="ArgumentException">The address or value is invalid, or the count is out of range.</exception>
    public void ManualWriteBlock(int id, string address, IReadOnlyList<long> values, int? repeatCount = null, int? timeout = null)
        => AddManual(WriteBlockWork(id, address, values, repeatCount, timeout));

    /// <summary>Registers a one-shot write of a single value by the name defined in the XML &lt;Write&gt;. Same as <see cref="ManualWrite"/>.</summary>
    /// <param name="name">Name of the &lt;Write&gt;.</param>
    /// <param name="value">Value to write. A word is -32768 to 65535 (negative values as two's complement); a bit is 0/1.</param>
    /// <param name="repeatCount">Retry count (if null, the RepeatCount in the XML; if that is absent too, no retries).</param>
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
    /// <param name="repeatCount">Retry count (if null, the RepeatCount in the XML; if that is absent too, no retries).</param>
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
    public FinsSerialConfig ToConfig() => new()
    {
        Port = Port,
        Baudrate = Baudrate,
        Parity = Parity,
        DataBits = DataBits,
        StopBits = StopBits,
        UnitNumber = UnitNumber,
        ResponseWait = ResponseWait,
        MaxWordsPerRequest = MaxWordsPerRequest,
        Timeout = Timeout,
        Interval = Interval,
        AutoReconnect = AutoReconnect,
        Autos = [.. AutoWorksSnapshot().Select(w => FinsAutoConfig.From(w.MessageID, w.Command, w.Items, w.Count, w.Timeout))],
        Writes = namedWrites.Snapshot(),
    };

    /// <summary>Applies the communication settings, Auto list, and named write list of a configuration object. The existing Auto list and named writes are replaced by those of the configuration.</summary>
    /// <remarks>Everything is validated first and then applied, so if any value is invalid, nothing is changed.</remarks>
    /// <param name="config">The configuration to apply.</param>
    /// <exception cref="InvalidOperationException">Called while communicating (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">A value is invalid (the message includes the element and attribute names).</exception>
    public void ApplyConfig(FinsSerialConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (IsStart) throw new InvalidOperationException("통신 중에는 설정을 바꿀 수 없습니다. Stop() 후 호출하세요.");
        config.Validate();
        var works = FinsAutoConfig.Parse(config.Autos, FinsFrame.SerialMaxItems).Select(a => ToWork(a.Id, a.Request, null, a.Timeout)).ToList();
        var table = NamedWriteTable<FinsWriteConfig>.Build(config.Writes, (w, label) =>
        {
            try { WriteWork(w.Id, w.Address, 0, null, null); }
            catch (ArgumentException ex)
            {
                throw XmlConfig.Invalid(label, ex, new Dictionary<string, (string, string)> { ["address"] = (nameof(w.Address), w.Address ?? "") });
            }
        });

        Port = config.Port;
        Baudrate = config.Baudrate;
        Parity = config.Parity;
        DataBits = config.DataBits;
        StopBits = config.StopBits;
        UnitNumber = config.UnitNumber;
        ResponseWait = config.ResponseWait;
        MaxWordsPerRequest = config.MaxWordsPerRequest;
        Timeout = config.Timeout;
        Interval = config.Interval;
        AutoReconnect = config.AutoReconnect;
        namedWrites = table;
        ReplaceAutoWorks(works);
    }

    /// <summary>Builds an XML element from the current settings, Auto list, and named write list. Manual jobs are not included.</summary>
    /// <returns>The &lt;FinsSerial&gt; element.</returns>
    public XElement ToXml() => XmlConfig.Serialize(ToConfig());

    /// <summary>Loads the settings, Auto list, and named write list from an XML element. The existing Auto list and named writes are replaced by the XML content.</summary>
    /// <remarks>
    /// <para>Omitted attributes use their default values. An unknown attribute or element is an error (to catch typos).</para>
    /// <para>Everything is validated first and then applied, so if any value is invalid, nothing is changed.</para>
    /// <para>You can check the format by generating the XSD with <see cref="ConfigSchema.Get{T}"/> for <see cref="FinsSerialConfig"/>.</para>
    /// </remarks>
    /// <param name="element">The &lt;FinsSerial&gt; element.</param>
    /// <exception cref="InvalidOperationException">Called while communicating (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">The root name differs, an unknown attribute or element is present, or a value is invalid.</exception>
    public void FromXml(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (IsStart) throw new InvalidOperationException("통신 중에는 설정을 불러올 수 없습니다. Stop() 후 호출하세요.");
        ApplyConfig(XmlConfig.Deserialize<FinsSerialConfig>(element));
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

    static ResultEventArgs Result(Work w) => new(w.MessageID, w.Command);

    void ProcessWork(SerialPort sp, Work w)
    {
        try
        {
            Process(w, (request, timeout) =>
            {
                sp.DiscardInBuffer();
                sp.Write(request, 0, request.Length);
                return ReadFrame(sp, timeout);
            });
        }
        catch
        {
            // 포트 오류로 작업이 중단되면 조용히 사라지지 않게 알린 뒤, 다시 여는 경로가 돌도록 다시 던진다
            Raise(TimeoutReceived, Result(w));
            throw;
        }
    }

    // 작업 하나의 송수신·재시도·이벤트. exchange(요청 프레임, 타임아웃) = 요청을 보내고 응답 프레임(없으면 null).
    // 조각마다 Host Link 프레임을 만들어 보내고, 마지막 상태를 돌려준다.
    internal FinsStatus Process(Work w, Func<byte[], int, byte[]?> exchange)
    {
        int timeout = w.Timeout ?? Timeout;
        for (int attempt = 0; ; attempt++)
        {
            int unit = UnitNumber, wait = ResponseWait;
            var r = FinsFrame.Run(FinsFrame.Chunks(w.Command, w.Items, w.Count, w.Values, MaxWordsPerRequest), c =>
            {
                var frame = exchange(HostLinkFrame.Command(unit, wait, c.Body), timeout);
                return frame == null ? FinsResponse.Invalid : HostLinkFrame.Parse(frame, unit, c);
            });
            if (r.Status is FinsStatus.Ack or FinsStatus.Nak)
            {
                Report(w, r);
                return r.Status;
            }
            Raise(r.Status == FinsStatus.FcsError ? FcsErrorReceived : TimeoutReceived, Result(w));
            if (attempt >= (w.RepeatCount ?? 0)) return r.Status;
        }
    }

    void Report(Work w, FinsResponse r)
    {
        if (r.Status == FinsStatus.Nak) Raise(NAKReceived, new NAKEventArgs(w.MessageID, w.Command, r.ErrorCode));
        else if (w.Command is FinsCommand.Read or FinsCommand.ReadBlock) Raise(DataReceived, new DataEventArgs(w.MessageID, w.Command, w.Addresses, r.Values));
        else Raise(WriteReceived, Result(w));
    }

    // 응답 프레임 하나('@' … "*" CR)를 받을 때까지 읽는다. 시간 안에 못 받으면 null.
    static byte[]? ReadFrame(SerialPort sp, int timeout)
    {
        var buf = new byte[HostLinkFrame.MaxResponse];
        int n = 0;
        var sw = Stopwatch.StartNew();
        while (true)
        {
            int remain = timeout - (int)sw.ElapsedMilliseconds;
            if (remain <= 0 || n == buf.Length) return null;
            sp.ReadTimeout = remain;
            try { n += sp.Read(buf, n, buf.Length - n); }
            catch (TimeoutException) { return null; }
            var (start, len) = HostLinkFrame.Find(buf.AsSpan(0, n));
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
