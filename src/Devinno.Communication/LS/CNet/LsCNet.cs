using System.Diagnostics;
using System.IO.Ports;
using System.Xml.Linq;
using Devinno.Communication.Internal;
using Devinno.Communication.LS.Data;

namespace Devinno.Communication.LS.CNet;

/// <summary>
/// LS PLC master that exchanges the XGT dedicated protocol (Cnet) over a serial line (RS-232/485).
/// </summary>
/// <remarks>
/// <para>Command names follow the official names in the manual: RSS (random read), RSB (block read), WSS (random write), WSB (block write). Lowercase commands and BCC are used.</para>
/// <para>Devices are specified by direct variable name (for example %MW100, %MX0, %DD10). A random command takes 1 to 16 devices at a time, and all size characters (X/B/W/D/L) must be the same. A block command cannot use bits (X) and is limited to 120 bytes.</para>
/// <para>Manual jobs are processed before Auto jobs. Responses arrive through the Received events, and the id given at registration is returned as <c>MessageID</c>. When the PLC sends a NAK, only <see cref="NAKReceived"/> is raised and no retry is made.</para>
/// <para>A Cnet frame is limited to 256 bytes. The library does not check this size at registration. If a request or response exceeds 256 bytes (for example 16 long device names or 16 L values), the PLC rejects it and the rejection arrives as <see cref="NAKReceived"/>.</para>
/// <para>The communication loop runs on a dedicated background thread, and all events are raised on that thread. To update the UI, marshal asynchronously, as with BeginInvoke (WinForms) or Dispatcher.BeginInvoke (WPF). Exceptions thrown in a handler are ignored and the loop continues.</para>
/// </remarks>
/// <example>
/// <code>
/// using Devinno.Communication.LS.CNet;
/// using Devinno.Communication.LS.Data;   // LsValue, LsCommand
///
/// var cnet = new LsCNet { Port = "COM5", Baudrate = 9600 };
/// cnet.AutoRSB(1, 1, "%MW100", 10);
/// cnet.DataReceived += (s, e) =&gt; { if (e.MessageID == 1) Console.WriteLine(string.Join(",", e.Values)); };
/// cnet.Start();
/// cnet.ManualWSS(10, 1, [new LsValue("%MW200", 1234), new LsValue("%MW201", 5)]);
/// cnet.NamedWrite("Speed", 1500);   // calls the XML &lt;Write Name="Speed" Id="20" Slave="1" Device="%MW300" /&gt; by name
/// </code>
/// </example>
public class LsCNet : IDisposable
{
    #region class : Work
    internal sealed class Work(int messageId, int slave, LsCommand command, string[] devices, int width, int count, byte[] request, int? repeatCount, int? timeout)
    {
        public int MessageID { get; } = messageId;
        public int Slave { get; } = slave;
        public LsCommand Command { get; } = command;
        public string[] Devices { get; } = devices;
        public int Width { get; } = width;
        public int Count { get; } = count;
        public byte[] Request { get; } = request;
        public int? RepeatCount { get; } = repeatCount;
        public int? Timeout { get; } = timeout;
    }
    #endregion

    #region class : EventArgs
    /// <summary>Common event arguments for request result events. Write response, timeout, and BCC error events use this type as-is.</summary>
    /// <param name="messageId">The message ID given at registration.</param>
    /// <param name="slave">Station number.</param>
    /// <param name="command">Command.</param>
    public class ResultEventArgs(int messageId, int slave, LsCommand command) : EventArgs
    {
        /// <summary>The message ID given at registration.</summary>
        public int MessageID { get; } = messageId;
        /// <summary>Station number.</summary>
        public int Slave { get; } = slave;
        /// <summary>Command.</summary>
        public LsCommand Command { get; } = command;
    }

    /// <summary>Event arguments for a read (RSS/RSB) response.</summary>
    /// <param name="messageId">Message ID.</param>
    /// <param name="slave">Station number.</param>
    /// <param name="command">Command (RSS or RSB).</param>
    /// <param name="devices">The requested devices for RSS; the single start device for RSB.</param>
    /// <param name="values">Values read. Unsigned raw values of the device size (X is 0/1, L is a 64-bit bit pattern).</param>
    public class DataEventArgs(int messageId, int slave, LsCommand command, string[] devices, long[] values)
        : ResultEventArgs(messageId, slave, command)
    {
        /// <summary>The requested devices for RSS; the single start device for RSB (uppercase).</summary>
        public string[] Devices { get; } = devices;
        /// <summary>Values read. Unsigned raw values of the device size (X is 0/1, L is a 64-bit bit pattern).</summary>
        public long[] Values { get; } = values;
    }

    /// <summary>Event arguments for when the PLC sends a NAK.</summary>
    /// <param name="messageId">Message ID.</param>
    /// <param name="slave">Station number.</param>
    /// <param name="command">Command.</param>
    /// <param name="errorCode">NAK error code (for example 0x1132 device area error, 0x1232 size exceeded).</param>
    public class NAKEventArgs(int messageId, int slave, LsCommand command, int errorCode)
        : ResultEventArgs(messageId, slave, command)
    {
        /// <summary>NAK error code (for example 0x1132 device area error, 0x1232 size exceeded).</summary>
        public int ErrorCode { get; } = errorCode;
    }
    #endregion

    #region Properties
    /// <summary>Serial port name (default: "COM1").</summary>
    public string Port { get; set; } = "COM1";
    /// <summary>Baud rate in bps (default: 9600).</summary>
    public int Baudrate { get; set; } = 9600;
    /// <summary>Parity (default: None).</summary>
    public Parity Parity { get; set; } = Parity.None;
    /// <summary>Data bits (default: 8).</summary>
    public int DataBits { get; set; } = 8;
    /// <summary>Stop bits (default: One).</summary>
    public StopBits StopBits { get; set; } = StopBits.One;

    /// <summary>Response timeout in ms (default: 1000). A per-job timeout, if given, takes precedence.</summary>
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
    /// <summary>Raised when a read (RSS/RSB) response is received.</summary>
    public event EventHandler<DataEventArgs>? DataReceived;
    /// <summary>Raised when a write (WSS/WSB) response is received.</summary>
    public event EventHandler<ResultEventArgs>? WriteReceived;
    /// <summary>Raised when the PLC sends a NAK. No retry is made.</summary>
    public event EventHandler<NAKEventArgs>? NAKReceived;
    /// <summary>Raised when there is no response, the response is malformed, or the port is lost during a request. Raised on every retry.</summary>
    public event EventHandler<ResultEventArgs>? TimeoutReceived;
    /// <summary>Raised when the BCC of a response is incorrect. Raised on every retry.</summary>
    public event EventHandler<ResultEventArgs>? BCCErrorReceived;
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
            thread = new Thread(() => Run(token)) { IsBackground = true, Name = nameof(LsCNet) };
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

    static void CheckSlave(int slave)
    {
        if (slave < 0 || slave > 255) throw new ArgumentOutOfRangeException(nameof(slave), "국번은 0~255 입니다.");
    }

    static LsDevice ParseBlock(string device, int count, string countParam)
    {
        var d = LsDevice.Parse(device, nameof(device));
        if (d.IsBit) throw new ArgumentException("연속 읽기/쓰기에는 비트(X) 디바이스를 쓸 수 없습니다.", nameof(device));
        int max = CNetFrame.MaxBlockBytes / d.Width;
        if (count < 1 || count > max) throw new ArgumentOutOfRangeException(countParam, $"{d.Name} 개수는 1~{max} 입니다.");
        return d;
    }

    internal Work RssWork(int id, int slave, IReadOnlyList<string> devices, int? repeatCount, int? timeout)
    {
        CheckSlave(slave);
        var ds = LsDevice.ParseSingles(devices, nameof(devices));
        return new Work(id, slave, LsCommand.RSS, [.. ds.Select(d => d.Name)], ds[0].Width, ds.Count, CNetFrame.RSS(slave, ds), repeatCount, timeout);
    }

    internal Work RsbWork(int id, int slave, string device, int count, int? repeatCount, int? timeout)
    {
        CheckSlave(slave);
        var d = ParseBlock(device, count, nameof(count));
        return new Work(id, slave, LsCommand.RSB, [d.Name], d.Width, count, CNetFrame.RSB(slave, d, count), repeatCount, timeout);
    }

    Work WssWork(int id, int slave, IReadOnlyList<LsValue> values, int? repeatCount, int? timeout)
    {
        CheckSlave(slave);
        ArgumentNullException.ThrowIfNull(values);
        var ds = LsDevice.ParseSingles([.. values.Select(v => v.Device)], nameof(values));
        for (int i = 0; i < ds.Count; i++) ds[i].CheckValue(values[i].Value, nameof(values));
        var pairs = ds.Select((d, i) => (d, values[i].Value)).ToList();
        return new Work(id, slave, LsCommand.WSS, [.. ds.Select(d => d.Name)], ds[0].Width, ds.Count, CNetFrame.WSS(slave, pairs), repeatCount, timeout);
    }

    Work WsbWork(int id, int slave, string device, IReadOnlyList<long> values, int? repeatCount, int? timeout)
    {
        CheckSlave(slave);
        ArgumentNullException.ThrowIfNull(values);
        var d = ParseBlock(device, values.Count, nameof(values));
        foreach (var v in values) d.CheckValue(v, nameof(values));
        return new Work(id, slave, LsCommand.WSB, [d.Name], d.Width, values.Count, CNetFrame.WSB(slave, d, values), repeatCount, timeout);
    }

    void AddAuto(Work w) { lock (workLock) autoWorks.Add(w); }
    void AddManual(Work w) { lock (workLock) manualWorks.Add(w); }

    // 이름 있는 쓰기 표. NamedWrite는 실행 중에도 불리므로 통째로 바꾸는 참조(volatile)로 둔다. ApplyConfig에서만 교체한다.
    volatile NamedWriteTable<LsCNetWriteConfig> namedWrites = NamedWriteTable<LsCNetWriteConfig>.Empty;
    #endregion

    #region Auto / Manual
    /// <summary>Registers an RSS (random read) in the Auto list.</summary>
    /// <param name="id">Message ID (the MessageID of the response event).</param>
    /// <param name="slave">Station number, 0 to 255.</param>
    /// <param name="devices">1 to 16 devices; all size characters must be the same (for example ["%MW100", "%MW105"]).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void AutoRSS(int id, int slave, IReadOnlyList<string> devices, int? timeout = null)
        => AddAuto(RssWork(id, slave, devices, null, timeout));

    /// <summary>Registers an RSB (block read) in the Auto list.</summary>
    /// <param name="id">Message ID (the MessageID of the response event).</param>
    /// <param name="slave">Station number, 0 to 255.</param>
    /// <param name="device">Start device (bit X not allowed, for example "%MW100").</param>
    /// <param name="count">Number of items to read. At most 120 bytes (60 for W, 30 for D).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void AutoRSB(int id, int slave, string device, int count, int? timeout = null)
        => AddAuto(RsbWork(id, slave, device, count, null, timeout));

    /// <summary>Registers a one-shot RSS (random read).</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="slave">Station number, 0 to 255.</param>
    /// <param name="devices">1 to 16 devices; all size characters must be the same.</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualRSS(int id, int slave, IReadOnlyList<string> devices, int? repeatCount = null, int? timeout = null)
        => AddManual(RssWork(id, slave, devices, repeatCount, timeout));

    /// <summary>Registers a one-shot RSB (block read).</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="slave">Station number, 0 to 255.</param>
    /// <param name="device">Start device (bit X not allowed).</param>
    /// <param name="count">Number of items to read. At most 120 bytes.</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualRSB(int id, int slave, string device, int count, int? repeatCount = null, int? timeout = null)
        => AddManual(RsbWork(id, slave, device, count, repeatCount, timeout));

    /// <summary>Registers a one-shot WSS (random write).</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="slave">Station number, 0 to 255.</param>
    /// <param name="values">1 to 16 device and value pairs; all size characters must be the same. Bits accept only 0/1.</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualWSS(int id, int slave, IReadOnlyList<LsValue> values, int? repeatCount = null, int? timeout = null)
        => AddManual(WssWork(id, slave, values, repeatCount, timeout));

    /// <summary>Registers a one-shot WSB (block write).</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="slave">Station number, 0 to 255.</param>
    /// <param name="device">Start device (bit X not allowed).</param>
    /// <param name="values">The values to write. At most 120 bytes; each value must be within the size range.</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualWSB(int id, int slave, string device, IReadOnlyList<long> values, int? repeatCount = null, int? timeout = null)
        => AddManual(WsbWork(id, slave, device, values, repeatCount, timeout));

    /// <summary>Registers a write of one value by the name defined in the XML &lt;Write&gt; element. It is sent as WSS (random write).</summary>
    /// <param name="name">The Name of the &lt;Write&gt; element.</param>
    /// <param name="value">The value to write. 0/1 for a bit; otherwise within the device size range.</param>
    /// <param name="repeatCount">Retry count (if null, the RepeatCount in the XML; if that is absent too, no retries).</param>
    /// <param name="timeout">Timeout in ms (if null, the Timeout in the XML; if that is absent too, <see cref="Timeout"/>).</param>
    /// <exception cref="ArgumentException">The name does not exist, or a value is invalid.</exception>
    public void NamedWrite(string name, long value, int? repeatCount = null, int? timeout = null)
        => NamedWrite(name, [value], repeatCount, timeout);

    /// <summary>Registers a write of several values to consecutive devices by the name defined in the XML &lt;Write&gt; element. One value is sent as WSS, several values as WSB.</summary>
    /// <param name="name">The Name of the &lt;Write&gt; element.</param>
    /// <param name="values">The values to write (several values follow the same rules as <see cref="ManualWSB"/>: no bit devices, at most 120 bytes).</param>
    /// <param name="repeatCount">Retry count (if null, the RepeatCount in the XML; if that is absent too, no retries).</param>
    /// <param name="timeout">Timeout in ms (if null, the Timeout in the XML; if that is absent too, <see cref="Timeout"/>).</param>
    /// <exception cref="ArgumentException">The name does not exist, or a value is invalid.</exception>
    public void NamedWrite(string name, IReadOnlyList<long> values, int? repeatCount = null, int? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        var table = namedWrites;
        var item = table.Get(name);
        var (r, t) = table.Resolve(item, repeatCount, timeout);
        AddManual(values.Count == 1
            ? WssWork(item.Id, item.Slave, [new LsValue(item.Device, values[0])], r, t)
            : WsbWork(item.Id, item.Slave, item.Device, values, r, t));
    }

    /// <summary>Checks whether a named write with the given name was loaded from the XML. Names are case-sensitive.</summary>
    /// <param name="name">The Name of the &lt;Write&gt; element.</param>
    /// <returns>true if it exists.</returns>
    public bool ContainsNamedWrite(string name) => namedWrites.Contains(name);
    #endregion

    #region Config / Xml
    /// <summary>Builds a configuration object from the current communication settings, Auto list, and named write list. Manual jobs are not included.</summary>
    /// <returns>The configuration object.</returns>
    public LsCNetConfig ToConfig() => new()
    {
        Port = Port,
        Baudrate = Baudrate,
        Parity = Parity,
        DataBits = DataBits,
        StopBits = StopBits,
        Timeout = Timeout,
        Interval = Interval,
        AutoReconnect = AutoReconnect,
        Autos = [.. AutoWorksSnapshot().Select(w =>
        {
            LsCNetAutoConfig a = w.Command == LsCommand.RSS
                ? new LsCNetRssConfig { Id = w.MessageID, Slave = w.Slave, Devices = string.Join(",", w.Devices) }
                : new LsCNetRsbConfig { Id = w.MessageID, Slave = w.Slave, Device = w.Devices[0], Count = w.Count };
            if (w.Timeout is int t) a.Timeout = t;
            return a;
        })],
        Writes = namedWrites.Snapshot(),
    };

    /// <summary>Applies the communication settings, Auto list, and named write list of the configuration object. The existing Auto list and named writes are replaced by those of the configuration.</summary>
    /// <remarks>The whole input is validated before it is applied, so if any single value is invalid, nothing is changed.</remarks>
    /// <param name="config">The configuration to apply.</param>
    /// <exception cref="InvalidOperationException">Called while communicating (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">A value is invalid (the message includes the element and attribute names).</exception>
    public void ApplyConfig(LsCNetConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (IsStart) throw new InvalidOperationException("통신 중에는 설정을 바꿀 수 없습니다. Stop() 후 호출하세요.");
        config.Validate();
        var works = new List<Work>();
        foreach (var a in config.Autos ?? [])
        {
            var label = $"{(a is LsCNetRssConfig ? "RSS" : "RSB")} Id=\"{a.Id}\"";
            if (a.TimeoutSpecified) XmlConfig.Range(label, nameof(a.Timeout), a.Timeout, 1, 60000);
            int? t = a.TimeoutSpecified ? a.Timeout : null;
            try
            {
                works.Add(a switch
                {
                    LsCNetRssConfig r => RssWork(a.Id, a.Slave, LsDevice.SplitNames(r.Devices), null, t),
                    LsCNetRsbConfig b => RsbWork(a.Id, a.Slave, b.Device, b.Count, null, t),
                    _ => throw new FormatException($"알 수 없는 Auto 항목: {a.GetType().Name}"),
                });
            }
            catch (ArgumentException ex)
            {
                throw XmlConfig.Invalid(label, ex, new Dictionary<string, (string, string)>
                {
                    ["slave"] = (nameof(a.Slave), a.Slave.ToString()),
                    ["devices"] = ("Devices", (a as LsCNetRssConfig)?.Devices ?? ""),
                    ["device"] = ("Device", (a as LsCNetRsbConfig)?.Device ?? ""),
                    ["count"] = ("Count", (a as LsCNetRsbConfig)?.Count.ToString() ?? ""),
                });
            }
        }

        var table = NamedWriteTable<LsCNetWriteConfig>.Build(config.Writes, (w, label) =>
        {
            try { WssWork(w.Id, w.Slave, [new LsValue(w.Device, 0)], null, null); }
            catch (ArgumentException ex)
            {
                throw XmlConfig.Invalid(label, ex, new Dictionary<string, (string, string)>
                {
                    ["slave"] = (nameof(w.Slave), w.Slave.ToString()),
                    ["device"] = (nameof(w.Device), w.Device ?? ""),
                    ["values"] = (nameof(w.Device), w.Device ?? ""),
                });
            }
        });

        Port = config.Port;
        Baudrate = config.Baudrate;
        Parity = config.Parity;
        DataBits = config.DataBits;
        StopBits = config.StopBits;
        Timeout = config.Timeout;
        Interval = config.Interval;
        AutoReconnect = config.AutoReconnect;
        namedWrites = table;
        ReplaceAutoWorks(works);
    }

    /// <summary>Builds an XML element from the current settings, Auto list, and named write list. Manual jobs are not included.</summary>
    /// <returns>The &lt;LsCNet&gt; element.</returns>
    public XElement ToXml() => XmlConfig.Serialize(ToConfig());

    /// <summary>Loads the settings, Auto list, and named write list from an XML element. The existing Auto list and named writes are replaced by the XML content.</summary>
    /// <remarks>
    /// <para>Omitted attributes use their default values. An unknown attribute or element is an error (to catch typos).</para>
    /// <para>The whole input is validated before it is applied, so if any single value is invalid, nothing is changed.</para>
    /// <para>You can check the format as an XSD by passing <see cref="LsCNetConfig"/> to <see cref="ConfigSchema.Get{T}"/>.</para>
    /// </remarks>
    /// <param name="element">The &lt;LsCNet&gt; element.</param>
    /// <exception cref="InvalidOperationException">Called while communicating (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">The root name differs, an unknown attribute or element is present, or a value is invalid.</exception>
    public void FromXml(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (IsStart) throw new InvalidOperationException("통신 중에는 설정을 불러올 수 없습니다. Stop() 후 호출하세요.");
        ApplyConfig(XmlConfig.Deserialize<LsCNetConfig>(element));
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

    static ResultEventArgs Result(Work w) => new(w.MessageID, w.Slave, w.Command);

    void ProcessWork(SerialPort sp, Work w)
    {
        try
        {
            int timeout = w.Timeout ?? Timeout;
            for (int attempt = 0; ; attempt++)
            {
                sp.DiscardInBuffer();
                sp.Write(w.Request, 0, w.Request.Length);
                var frame = ReadFrame(sp, timeout);
                var status = frame == null ? LsStatus.Invalid : HandleResponse(w, frame);
                if (status is LsStatus.Ack or LsStatus.Nak) return;
                Raise(status == LsStatus.BccError ? BCCErrorReceived : TimeoutReceived, Result(w));
                if (attempt >= (w.RepeatCount ?? 0)) return;
            }
        }
        catch
        {
            // 포트 오류로 작업이 중단되면 조용히 사라지지 않게 알린 뒤, 재접속 경로가 돌도록 다시 던진다
            Raise(TimeoutReceived, Result(w));
            throw;
        }
    }

    // ETX와 BCC까지 받을 때까지 읽는다. 시간 안에 못 받으면 null.
    static byte[]? ReadFrame(SerialPort sp, int timeout)
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
            int len = CNetFrame.FrameLength(buf.AsSpan(0, n));
            if (len > 0) return buf.AsSpan(0, len).ToArray();
        }
    }

    internal LsStatus HandleResponse(Work w, ReadOnlySpan<byte> frame)
    {
        var r = CNetFrame.Parse(frame, w.Request, w.Width, w.Count);
        if (r.Status == LsStatus.Ack)
        {
            if (w.Command is LsCommand.RSS or LsCommand.RSB)
                Raise(DataReceived, new DataEventArgs(w.MessageID, w.Slave, w.Command, [.. w.Devices], r.Values));
            else
                Raise(WriteReceived, Result(w));
        }
        else if (r.Status == LsStatus.Nak)
            Raise(NAKReceived, new NAKEventArgs(w.MessageID, w.Slave, w.Command, r.ErrorCode));
        return r.Status;
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
