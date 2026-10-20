using System.Diagnostics;
using System.Net.Sockets;
using System.Xml.Linq;
using Devinno.Communication.Internal;
using Devinno.Communication.LS.Data;

namespace Devinno.Communication.LS.FEnet;

/// <summary>
/// LS PLC master that exchanges the XGT dedicated protocol with the PLC's FEnet (Ethernet) module over TCP. The default port is 2004.
/// </summary>
/// <remarks>
/// <para>Command names follow the official names in the manual: RSS (random read), RSB (block read), WSS (random write), WSB (block write).</para>
/// <para>Devices are specified by direct variable name (for example %MW100, %MX0). A random command takes 1 to 16 devices, and all size characters must be the same.
/// A block command is converted internally to a byte variable before sending (%MW100 x10 becomes %MB200 x20 bytes), so only numeric addresses are allowed; bits (X) and dotted addresses cannot be used, and the limit is 1400 bytes.</para>
/// <para>Manual jobs are processed before Auto jobs. Responses are matched to requests by Invoke ID; a response that does not match is discarded. When the PLC sends an error, only <see cref="NAKReceived"/> is raised and no retry is made.</para>
/// <para>The communication loop runs on a dedicated background thread, and all events are raised on that thread. To update the UI, marshal asynchronously, as with BeginInvoke (WinForms) or Dispatcher.BeginInvoke (WPF). Exceptions thrown in a handler are ignored and the loop continues.</para>
/// </remarks>
/// <example>
/// <code>
/// using Devinno.Communication.LS.FEnet;
/// using Devinno.Communication.LS.Data;   // LsValue, LsCommand
///
/// var fenet = new LsFEnet { RemoteIP = "192.168.0.20" };
/// fenet.AutoRSB(1, "%MW100", 10);
/// fenet.DataReceived += (s, e) =&gt; { if (e.MessageID == 1) Console.WriteLine(string.Join(",", e.Values)); };
/// fenet.Start();
/// fenet.ManualWSB(10, "%MW200", [1, 2, 3]);
/// fenet.NamedWrite("Speed", 1500);   // calls the XML &lt;Write Name="Speed" Id="20" Device="%MW300" /&gt; by name
/// </code>
/// </example>
public class LsFEnet : IDisposable
{
    #region class : Work
    internal sealed class Work(int messageId, LsCommand command, string[] devices, int width, int count, byte[] body, int? repeatCount, int? timeout)
    {
        public int MessageID { get; } = messageId;
        public LsCommand Command { get; } = command;
        public string[] Devices { get; } = devices;
        public int Width { get; } = width;
        public int Count { get; } = count;
        public byte[] Body { get; } = body;
        public int? RepeatCount { get; } = repeatCount;
        public int? Timeout { get; } = timeout;
    }
    #endregion

    #region class : EventArgs
    /// <summary>Common event arguments for request result events. Write response and timeout events use this type as-is.</summary>
    /// <param name="messageId">The message ID given at registration.</param>
    /// <param name="command">Command.</param>
    public class ResultEventArgs(int messageId, LsCommand command) : EventArgs
    {
        /// <summary>The message ID given at registration.</summary>
        public int MessageID { get; } = messageId;
        /// <summary>Command.</summary>
        public LsCommand Command { get; } = command;
    }

    /// <summary>Event arguments for a read (RSS/RSB) response.</summary>
    /// <param name="messageId">Message ID.</param>
    /// <param name="command">Command (RSS or RSB).</param>
    /// <param name="devices">The requested devices for RSS; the single start device for RSB (as registered, uppercase).</param>
    /// <param name="values">Values read. Unsigned raw values of the device size (X is 0/1, L is a 64-bit bit pattern).</param>
    public class DataEventArgs(int messageId, LsCommand command, string[] devices, long[] values)
        : ResultEventArgs(messageId, command)
    {
        /// <summary>The requested devices for RSS; the single start device for RSB (as registered, uppercase).</summary>
        public string[] Devices { get; } = devices;
        /// <summary>Values read. Unsigned raw values of the device size (X is 0/1, L is a 64-bit bit pattern).</summary>
        public long[] Values { get; } = values;
    }

    /// <summary>Event arguments for when the PLC sends an error response.</summary>
    /// <param name="messageId">Message ID.</param>
    /// <param name="command">Command.</param>
    /// <param name="errorCode">Error code (for example 0x0003 unsupported device, 0x0004 device range exceeded).</param>
    public class NAKEventArgs(int messageId, LsCommand command, int errorCode)
        : ResultEventArgs(messageId, command)
    {
        /// <summary>Error code (for example 0x0003 unsupported device, 0x0004 device range exceeded).</summary>
        public int ErrorCode { get; } = errorCode;
    }
    #endregion

    #region Properties
    /// <summary>IP address of the PLC (FEnet module) to connect to (default: "127.0.0.1").</summary>
    public string RemoteIP { get; set; } = "127.0.0.1";
    /// <summary>Port to connect to (default: 2004).</summary>
    public int RemotePort { get; set; } = 2004;

    /// <summary>Response timeout in ms (default: 1000). A per-job timeout, if given, takes precedence.</summary>
    public int Timeout { get; set; } = 1000;
    /// <summary>Wait between jobs in ms (default: 10).</summary>
    public int Interval { get; set; } = 10;
    /// <summary>Whether to reconnect at 1-second intervals when the connection could not be made or was lost (default: true).</summary>
    public bool AutoReconnect { get; set; } = true;

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
    ushort invokeId;
    #endregion

    #region Event
    /// <summary>Raised when a read (RSS/RSB) response is received.</summary>
    public event EventHandler<DataEventArgs>? DataReceived;
    /// <summary>Raised when a write (WSS/WSB) response is received.</summary>
    public event EventHandler<ResultEventArgs>? WriteReceived;
    /// <summary>Raised when the PLC sends an error response. No retry is made.</summary>
    public event EventHandler<NAKEventArgs>? NAKReceived;
    /// <summary>Raised when there is no response, the response is malformed, or the connection is lost during a request. Raised on every retry.</summary>
    public event EventHandler<ResultEventArgs>? TimeoutReceived;
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
            thread = new Thread(() => Run(token)) { IsBackground = true, Name = nameof(LsFEnet) };
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

    static (LsDevice Device, LsDevice ByteDevice) ParseBlock(string device, int count, string countParam)
    {
        var d = LsDevice.Parse(device, nameof(device));
        if (d.IsBit) throw new ArgumentException("연속 읽기/쓰기에는 비트(X) 디바이스를 쓸 수 없습니다.", nameof(device));
        var bd = d.ToByteDevice(nameof(device));
        int max = FEnetFrame.MaxBytes / d.Width;
        if (count < 1 || count > max) throw new ArgumentOutOfRangeException(countParam, $"{d.Name} 개수는 1~{max} 입니다.");
        return (d, bd);
    }

    internal Work RssWork(int id, IReadOnlyList<string> devices, int? repeatCount, int? timeout)
    {
        var ds = LsDevice.ParseSingles(devices, nameof(devices));
        return new Work(id, LsCommand.RSS, [.. ds.Select(d => d.Name)], ds[0].Width, ds.Count, FEnetFrame.RSS(ds), repeatCount, timeout);
    }

    internal Work RsbWork(int id, string device, int count, int? repeatCount, int? timeout)
    {
        var (d, bd) = ParseBlock(device, count, nameof(count));
        return new Work(id, LsCommand.RSB, [d.Name], d.Width, count, FEnetFrame.RSB(bd, count * d.Width), repeatCount, timeout);
    }

    Work WssWork(int id, IReadOnlyList<LsValue> values, int? repeatCount, int? timeout)
    {
        ArgumentNullException.ThrowIfNull(values);
        var ds = LsDevice.ParseSingles([.. values.Select(v => v.Device)], nameof(values));
        for (int i = 0; i < ds.Count; i++) ds[i].CheckValue(values[i].Value, nameof(values));
        var pairs = ds.Select((d, i) => (d, values[i].Value)).ToList();
        return new Work(id, LsCommand.WSS, [.. ds.Select(d => d.Name)], ds[0].Width, ds.Count, FEnetFrame.WSS(pairs), repeatCount, timeout);
    }

    Work WsbWork(int id, string device, IReadOnlyList<long> values, int? repeatCount, int? timeout)
    {
        ArgumentNullException.ThrowIfNull(values);
        var (d, bd) = ParseBlock(device, values.Count, nameof(values));
        foreach (var v in values) d.CheckValue(v, nameof(values));
        return new Work(id, LsCommand.WSB, [d.Name], d.Width, values.Count, FEnetFrame.WSB(bd, values, d.Width), repeatCount, timeout);
    }

    void AddAuto(Work w) { lock (workLock) autoWorks.Add(w); }
    void AddManual(Work w) { lock (workLock) manualWorks.Add(w); }

    // 이름 있는 쓰기 표. NamedWrite는 실행 중에도 불리므로 통째로 바꾸는 참조(volatile)로 둔다. ApplyConfig에서만 교체한다.
    volatile NamedWriteTable<LsFEnetWriteConfig> namedWrites = NamedWriteTable<LsFEnetWriteConfig>.Empty;
    #endregion

    #region Auto / Manual
    /// <summary>Registers an RSS (random read) in the Auto list.</summary>
    /// <param name="id">Message ID (the MessageID of the response event).</param>
    /// <param name="devices">1 to 16 devices; all size characters must be the same (for example ["%MW100", "%MW105"]).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void AutoRSS(int id, IReadOnlyList<string> devices, int? timeout = null)
        => AddAuto(RssWork(id, devices, null, timeout));

    /// <summary>Registers an RSB (block read) in the Auto list.</summary>
    /// <param name="id">Message ID (the MessageID of the response event).</param>
    /// <param name="device">Start device (numeric address only, bit X not allowed, for example "%MW100").</param>
    /// <param name="count">Number of items to read. At most 1400 bytes (700 for W).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void AutoRSB(int id, string device, int count, int? timeout = null)
        => AddAuto(RsbWork(id, device, count, null, timeout));

    /// <summary>Registers a one-shot RSS (random read).</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="devices">1 to 16 devices; all size characters must be the same.</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualRSS(int id, IReadOnlyList<string> devices, int? repeatCount = null, int? timeout = null)
        => AddManual(RssWork(id, devices, repeatCount, timeout));

    /// <summary>Registers a one-shot RSB (block read).</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="device">Start device (numeric address only, bit X not allowed).</param>
    /// <param name="count">Number of items to read. At most 1400 bytes.</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualRSB(int id, string device, int count, int? repeatCount = null, int? timeout = null)
        => AddManual(RsbWork(id, device, count, repeatCount, timeout));

    /// <summary>Registers a one-shot WSS (random write).</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="values">1 to 16 device and value pairs; all size characters must be the same. Bits accept only 0/1.</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualWSS(int id, IReadOnlyList<LsValue> values, int? repeatCount = null, int? timeout = null)
        => AddManual(WssWork(id, values, repeatCount, timeout));

    /// <summary>Registers a one-shot WSB (block write).</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="device">Start device (numeric address only, bit X not allowed).</param>
    /// <param name="values">The values to write. At most 1400 bytes; each value must be within the size range.</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualWSB(int id, string device, IReadOnlyList<long> values, int? repeatCount = null, int? timeout = null)
        => AddManual(WsbWork(id, device, values, repeatCount, timeout));

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
    /// <param name="values">The values to write (several values follow the same rules as <see cref="ManualWSB"/>: numeric addresses only, no bit devices, at most 1400 bytes).</param>
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
            ? WssWork(item.Id, [new LsValue(item.Device, values[0])], r, t)
            : WsbWork(item.Id, item.Device, values, r, t));
    }

    /// <summary>Checks whether a named write with the given name was loaded from the XML. Names are case-sensitive.</summary>
    /// <param name="name">The Name of the &lt;Write&gt; element.</param>
    /// <returns>true if it exists.</returns>
    public bool ContainsNamedWrite(string name) => namedWrites.Contains(name);
    #endregion

    #region Config / Xml
    /// <summary>Builds a configuration object from the current communication settings, Auto list, and named write list. Manual jobs are not included.</summary>
    /// <returns>The configuration object.</returns>
    public LsFEnetConfig ToConfig() => new()
    {
        RemoteIP = RemoteIP,
        RemotePort = RemotePort,
        Timeout = Timeout,
        Interval = Interval,
        AutoReconnect = AutoReconnect,
        Autos = [.. AutoWorksSnapshot().Select(w =>
        {
            LsFEnetAutoConfig a = w.Command == LsCommand.RSS
                ? new LsFEnetRssConfig { Id = w.MessageID, Devices = string.Join(",", w.Devices) }
                : new LsFEnetRsbConfig { Id = w.MessageID, Device = w.Devices[0], Count = w.Count };
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
    public void ApplyConfig(LsFEnetConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (IsStart) throw new InvalidOperationException("통신 중에는 설정을 바꿀 수 없습니다. Stop() 후 호출하세요.");
        config.Validate();
        var works = new List<Work>();
        foreach (var a in config.Autos ?? [])
        {
            var label = $"{(a is LsFEnetRssConfig ? "RSS" : "RSB")} Id=\"{a.Id}\"";
            if (a.TimeoutSpecified) XmlConfig.Range(label, nameof(a.Timeout), a.Timeout, 1, 60000);
            int? t = a.TimeoutSpecified ? a.Timeout : null;
            try
            {
                works.Add(a switch
                {
                    LsFEnetRssConfig r => RssWork(a.Id, LsDevice.SplitNames(r.Devices), null, t),
                    LsFEnetRsbConfig b => RsbWork(a.Id, b.Device, b.Count, null, t),
                    _ => throw new FormatException($"알 수 없는 Auto 항목: {a.GetType().Name}"),
                });
            }
            catch (ArgumentException ex)
            {
                throw XmlConfig.Invalid(label, ex, new Dictionary<string, (string, string)>
                {
                    ["devices"] = ("Devices", (a as LsFEnetRssConfig)?.Devices ?? ""),
                    ["device"] = ("Device", (a as LsFEnetRsbConfig)?.Device ?? ""),
                    ["count"] = ("Count", (a as LsFEnetRsbConfig)?.Count.ToString() ?? ""),
                });
            }
        }

        var table = NamedWriteTable<LsFEnetWriteConfig>.Build(config.Writes, (w, label) =>
        {
            try { WssWork(w.Id, [new LsValue(w.Device, 0)], null, null); }
            catch (ArgumentException ex)
            {
                throw XmlConfig.Invalid(label, ex, new Dictionary<string, (string, string)>
                {
                    ["device"] = (nameof(w.Device), w.Device ?? ""),
                    ["values"] = (nameof(w.Device), w.Device ?? ""),
                });
            }
        });

        RemoteIP = config.RemoteIP;
        RemotePort = config.RemotePort;
        Timeout = config.Timeout;
        Interval = config.Interval;
        AutoReconnect = config.AutoReconnect;
        namedWrites = table;
        ReplaceAutoWorks(works);
    }

    /// <summary>Builds an XML element from the current settings, Auto list, and named write list. Manual jobs are not included.</summary>
    /// <returns>The &lt;LsFEnet&gt; element.</returns>
    public XElement ToXml() => XmlConfig.Serialize(ToConfig());

    /// <summary>Loads the settings, Auto list, and named write list from an XML element. The existing Auto list and named writes are replaced by the XML content.</summary>
    /// <remarks>
    /// <para>Omitted attributes use their default values. An unknown attribute or element is an error (to catch typos).</para>
    /// <para>The whole input is validated before it is applied, so if any single value is invalid, nothing is changed.</para>
    /// <para>You can check the format as an XSD by passing <see cref="LsFEnetConfig"/> to <see cref="ConfigSchema.Get{T}"/>.</para>
    /// </remarks>
    /// <param name="element">The &lt;LsFEnet&gt; element.</param>
    /// <exception cref="InvalidOperationException">Called while communicating (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">The root name differs, an unknown attribute or element is present, or a value is invalid.</exception>
    public void FromXml(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (IsStart) throw new InvalidOperationException("통신 중에는 설정을 불러올 수 없습니다. Stop() 후 호출하세요.");
        ApplyConfig(XmlConfig.Deserialize<LsFEnetConfig>(element));
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

    void ProcessWork(Socket s, Work w)
    {
        try
        {
            int timeout = w.Timeout ?? Timeout;
            for (int attempt = 0; ; attempt++)
            {
                var body = Exchange(s, w, timeout);
                if (body != null && HandleResponse(w, body) is LsStatus.Ack or LsStatus.Nak) return;
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

    // 요청을 보내고 Invoke ID가 맞는 응답 본문을 돌려준다. 시간 안에 못 받으면 null.
    byte[]? Exchange(Socket s, Work w, int timeout)
    {
        int invoke = ++invokeId;
        var request = FEnetFrame.Request(invoke, w.Body);
        Drain(s);
        s.Send(request);

        var sw = Stopwatch.StartNew();
        while (true)
        {
            var header = ReceiveExact(s, FEnetFrame.HeaderLength, timeout, sw);
            if (header == null) return null;
            var h = FEnetFrame.ParseHeader(header) ?? throw new IOException("FEnet 응답 헤더가 올바르지 않습니다.");   // 스트림 정렬이 깨짐 → 재접속
            var body = ReceiveExact(s, h.BodyLength, timeout, sw);
            if (body == null) return null;
            if (h.InvokeId == invoke) return body;
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

    internal LsStatus HandleResponse(Work w, ReadOnlySpan<byte> body)
    {
        var r = FEnetFrame.Parse(body, w.Body, w.Width, w.Count);
        if (r.Status == LsStatus.Ack)
        {
            if (w.Command is LsCommand.RSS or LsCommand.RSB)
                Raise(DataReceived, new DataEventArgs(w.MessageID, w.Command, [.. w.Devices], r.Values));
            else
                Raise(WriteReceived, Result(w));
        }
        else if (r.Status == LsStatus.Nak)
            Raise(NAKReceived, new NAKEventArgs(w.MessageID, w.Command, r.ErrorCode));
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
