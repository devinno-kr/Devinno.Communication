using System.IO.Ports;
using System.Text;
using System.Xml.Linq;
using Devinno.Communication.Internal;

namespace Devinno.Communication.TextComm.RTU;

/// <summary>
/// Master that exchanges TextComm frames (STX/ETX/DLE text messages) over serial (RS-232/485).
/// </summary>
/// <remarks>
/// <para>Frame: STX(0x02) | slave | command | message bytes | sum | ETX(0x03). sum is the low byte of the sum of slave + command + the message bytes, and
/// 0x02, 0x03, and 0x10 are sent as 0x10 followed by (value + 0x10) (DLE escaping).</para>
/// <para>slave and command are 0 to 255. The message is converted to bytes with <see cref="MessageEncoding"/> (default ASCII); the request is built with the encoding in effect when the job is registered, and the response is read with that same encoding.
/// Changing MessageEncoding after registration does not affect jobs that are already registered.</para>
/// <para>A response is the frame whose slave and command match the request. Other frames (such as a late response to an earlier request) and malformed frames are discarded, and reading continues until the timeout.
/// If the sum is wrong, <see cref="CheckSumErrorReceived"/> is raised; if there is no response in time, <see cref="TimeoutReceived"/> is raised; either way the request is resent up to repeatCount times (total transmissions = 1 + repeatCount).
/// A response frame with a wrong sum raises <see cref="CheckSumErrorReceived"/> and the request is resent, even if its (untrustworthy) slave and command differ from the request.</para>
/// <para>A response frame (STX to ETX) longer than <see cref="BufferSize"/> is discarded.</para>
/// <para>Manual jobs are processed before Auto jobs. Responses arrive through <see cref="MessageReceived"/>, and the id given at registration is returned as <c>MessageID</c>.</para>
/// <para>The communication loop runs on a dedicated background thread, and all events are raised on that thread. To update the UI, marshal asynchronously, as with BeginInvoke (WinForms) or Dispatcher.BeginInvoke (WPF). Exceptions thrown in a handler are ignored and the loop continues.</para>
/// <para>Write safety: TextComm cannot tell a read from a write, so every <see cref="ManualSend"/> counts as a write. With <see cref="DiscardWritesWhileDisconnected"/> true (the default), a ManualSend requested while there is no port is not queued, and ManualSend jobs still queued when the port is lost are discarded right after <see cref="DeviceClosed"/>; both are reported through <see cref="WriteDiscarded"/>, which may also be raised on the calling thread. <see cref="Stop"/> always discards pending ManualSend jobs. Auto jobs are never discarded. Each ManualSend ends with exactly one of: <see cref="MessageReceived"/>, a <see cref="TimeoutReceived"/> or <see cref="CheckSumErrorReceived"/> with IsFinal true, or <see cref="WriteDiscarded"/> (jobs removed with <see cref="ClearManual"/> or <see cref="RemoveManual"/> are not reported).</para>
/// <para>The XML configuration (<see cref="ToXml"/>, <see cref="SaveXml"/>) cannot store an Auto message that contains characters XML 1.0 cannot hold (control characters 0x00 to 0x1F other than tab and line breaks, and so on). Manage such Auto items in code.</para>
/// </remarks>
/// <example>
/// <code>
/// using Devinno.Communication.TextComm.RTU;
///
/// var tc = new TextCommRTUMaster { Port = "COM3", Baudrate = 115200 };
/// tc.AutoSend(1, 1, 0x41, "READ");
/// tc.MessageReceived += (s, e) =&gt; Console.WriteLine($"{e.MessageID} {e.Slave}/{e.Command}: {e.Message}");
/// tc.TimeoutReceived += (s, e) =&gt; Console.WriteLine($"No response {e.MessageID}");
/// tc.DeviceOpened += (s, e) =&gt;
/// {
///     // ManualSend is accepted only while open (otherwise WriteDiscarded, NotConnected)
///     tc.ManualSend(10, 1, 0x42, "SET 100", repeatCount: 2);
/// };
/// tc.Start();
/// </code>
/// </example>
public class TextCommRTUMaster : IDisposable
{
    #region class : Work
    internal sealed class Work(int messageId, int slave, int command, string message, Encoding encoding, byte[] request, int? repeatCount, int? timeout)
    {
        public int MessageID { get; } = messageId;
        public int Slave { get; } = slave;
        public int Command { get; } = command;
        public string Message { get; } = message;
        // 등록할 때의 인코딩 (요청을 만들고 응답을 읽을 때 쓴다)
        public Encoding Encoding { get; } = encoding;
        public byte[] Request { get; } = request;
        public int? RepeatCount { get; } = repeatCount;
        public int? Timeout { get; } = timeout;
    }
    #endregion

    #region class : EventArgs
    /// <summary>Common event arguments for request result events.</summary>
    /// <param name="messageId">The message ID given at registration.</param>
    /// <param name="slave">Request slave, 0 to 255.</param>
    /// <param name="command">Request command, 0 to 255.</param>
    public class ResultEventArgs(int messageId, int slave, int command) : EventArgs
    {
        /// <summary>The message ID given at registration.</summary>
        public int MessageID { get; } = messageId;
        /// <summary>Request slave, 0 to 255.</summary>
        public int Slave { get; } = slave;
        /// <summary>Request command, 0 to 255.</summary>
        public int Command { get; } = command;
    }

    /// <summary>Event arguments for a received response message.</summary>
    /// <param name="messageId">The message ID given at registration.</param>
    /// <param name="slave">slave 0~255</param>
    /// <param name="command">command 0~255</param>
    /// <param name="message">Response message (may be an empty string).</param>
    public class MessageEventArgs(int messageId, int slave, int command, string message) : ResultEventArgs(messageId, slave, command)
    {
        /// <summary>Response message. A string read with the encoding in effect at registration; may be an empty string.</summary>
        public string Message { get; } = message;
    }

    /// <summary>Event arguments for a failed attempt (<see cref="TimeoutReceived"/>, <see cref="CheckSumErrorReceived"/>).</summary>
    /// <param name="messageId">The message ID given at registration.</param>
    /// <param name="slave">Request slave, 0 to 255.</param>
    /// <param name="command">Request command, 0 to 255.</param>
    /// <param name="attempt">Attempt number, starting at 1 (1 = first attempt, 2 = first retry, ...).</param>
    /// <param name="isFinal">Whether this job will not be attempted again (retries used up or the port was lost).</param>
    /// <param name="requestSent">Whether the request of this attempt was handed to the send function.</param>
    public class AttemptFailedEventArgs(int messageId, int slave, int command, int attempt, bool isFinal, bool requestSent) : ResultEventArgs(messageId, slave, command)
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
    /// <summary>Baud rate in bps (default: 115200).</summary>
    public int Baudrate { get; set; } = 115200;
    /// <summary>Parity (default: None).</summary>
    public Parity Parity { get; set; } = Parity.None;
    /// <summary>Data bits (default: 8).</summary>
    public int DataBits { get; set; } = 8;
    /// <summary>Stop bits (default: One).</summary>
    public StopBits StopBits { get; set; } = StopBits.One;

    /// <summary>Response timeout in ms (default: 100). A per-job timeout, if given, takes precedence.</summary>
    public int Timeout { get; set; } = 100;
    /// <summary>Wait between jobs in ms (default: 10).</summary>
    public int Interval { get; set; } = 10;
    /// <summary>Whether to reopen the port at 1-second intervals when it could not be opened or was lost (default: false).</summary>
    public bool AutoReconnect { get; set; } = false;
    /// <summary>Whether to discard writes while there is no port (default: true). TextComm treats every <see cref="ManualSend"/> as a write. When true, a ManualSend requested while not open raises <see cref="WriteDiscarded"/> (NotConnected) instead of being queued, and ManualSend jobs still queued when the port is lost are discarded (Disconnected). When false, they stay queued and are sent after the next port. Auto jobs are not affected, and <see cref="Stop"/> discards pending ManualSend jobs regardless of this setting.</summary>
    public bool DiscardWritesWhileDisconnected { get; set; } = true;
    /// <summary>Maximum size in bytes of a response frame (STX to ETX) that can be received (default: 1024). Longer frames are discarded.</summary>
    public int BufferSize { get; set; } = 1024;
    /// <summary>Message string encoding (default: ASCII). Applied when a job is registered.</summary>
    public Encoding MessageEncoding { get; set; } = Encoding.ASCII;

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
    // 연결이 살아 있는지(workLock 보호). 설정이 true면 ManualSend는 이것이 true일 때만 대기열에 들어간다
    bool linkUp;
    // linkUp을 올린 루프 스레드(workLock 보호). 핸들러 안 Stop→Start로 새 루프가 뜬 뒤 옛 루프의 SetLinkDown이 새 연결 상태를 덮지 않게 한다
    Thread? linkThread;
    // 이번 시도의 요청을 송신 함수에 넘겼는지(통신 스레드 전용)
    bool requestSent;
    #endregion

    #region Event
    /// <summary>Raised when a response with the same slave and command as the request is received.</summary>
    public event EventHandler<MessageEventArgs>? MessageReceived;
    /// <summary>Raised when there is no response in time, or when the port is lost during a request. Raised on every retry; IsFinal tells whether the job will be attempted again. If RequestSent is true, the device may have executed the request — read it back before resending.</summary>
    public event EventHandler<AttemptFailedEventArgs>? TimeoutReceived;
    /// <summary>Raised when the sum byte of a response is wrong. Raised on every retry; IsFinal tells whether the job will be attempted again. If RequestSent is true, the device may have executed the request — read it back before resending.</summary>
    public event EventHandler<AttemptFailedEventArgs>? CheckSumErrorReceived;
    /// <summary>Raised when a ManualSend job is discarded without being sent (see <see cref="DiscardWritesWhileDisconnected"/>). May be raised on the calling thread: NotConnected inside the ManualSend call, Stopped inside Stop(); otherwise on the communication thread.</summary>
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
            thread = new Thread(() => Run(token)) { IsBackground = true, Name = nameof(TextCommRTUMaster) };
            thread.Start();
        }
    }

    /// <summary>Stops the communication loop and closes the port. Waits up to (Timeout + 1000) ms for the loop to end. Pending ManualSend jobs are discarded (WriteDiscarded, Stopped).</summary>
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
        // 설정과 무관하게 남은 ManualSend를 비운다(재시작 때 늦게 실행되지 않게)
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

    // 작업 하나를 만든다. 인코딩은 인자로 받는다(ApplyConfig가 적용 전에 검사할 수 있도록).
    internal static Work NewWork(int id, int slave, int command, string message, int? repeatCount, int? timeout, Encoding encoding)
    {
        var request = TextCommPacket.BuildRequest(encoding, slave, command, message);
        return new Work(id, slave, command, message, encoding, request, repeatCount, timeout);
    }

    void AddAuto(Work w) { lock (workLock) autoWorks.Add(w); }
    // TextComm은 읽기/쓰기를 구분하지 못하므로 ManualSend 전부를 쓰기로 본다
    void AddManual(Work w)
    {
        bool discard = false;
        lock (workLock)
        {
            if (DiscardWritesWhileDisconnected && !linkUp) discard = true;
            else manualWorks.Add(w);
        }
        if (discard) Raise(WriteDiscarded, new WriteDiscardedEventArgs(w.MessageID, WriteDiscardReason.NotConnected));
    }

    // 연결을 얻었다. port을 공개하기 전에 부른다(IsOpen이 true면 이미 linkUp)
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

    // 대기 중인 ManualSend를 순서대로 모두 꺼내 lock 밖에서 하나씩 알린다. 꺼내기는 workLock 안이라 작업 하나당 알림은 한 번
    internal void DiscardWrites(WriteDiscardReason reason)
    {
        List<Work> dropped;
        lock (workLock)
        {
            if (manualWorks.Count == 0) return;
            dropped = [.. manualWorks];
            manualWorks.Clear();
        }
        foreach (var w in dropped) Raise(WriteDiscarded, new WriteDiscardedEventArgs(w.MessageID, reason));
    }
    #endregion

    #region Auto / Manual
    /// <summary>Registers a message transmission in the Auto (polling) list.</summary>
    /// <param name="id">Message ID (the MessageID of the response event).</param>
    /// <param name="slave">slave 0~255</param>
    /// <param name="command">command 0~255</param>
    /// <param name="message">The message to send (an empty string is allowed; null is not).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    /// <exception cref="ArgumentOutOfRangeException">slave or command is not in 0 to 255.</exception>
    /// <exception cref="ArgumentNullException">message is null.</exception>
    public void AutoSend(int id, int slave, int command, string message, int? timeout = null)
        => AddAuto(NewWork(id, slave, command, message, null, timeout, MessageEncoding));

    /// <summary>Registers a one-shot message transmission. It is processed before Auto jobs.</summary>
    /// <param name="id">Message ID (the MessageID of the response event).</param>
    /// <param name="slave">slave 0~255</param>
    /// <param name="command">command 0~255</param>
    /// <param name="message">The message to send (an empty string is allowed; null is not).</param>
    /// <param name="repeatCount">Retry count when there is no response or the sum check fails (null for no retries). A retry resends a message the device may already have executed; do not use it for pulse, counter or incremental commands.</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    /// <exception cref="ArgumentOutOfRangeException">slave or command is not in 0 to 255.</exception>
    /// <exception cref="ArgumentNullException">message is null.</exception>
    public void ManualSend(int id, int slave, int command, string message, int? repeatCount = null, int? timeout = null)
        => AddManual(NewWork(id, slave, command, message, repeatCount, timeout, MessageEncoding));
    #endregion

    #region Config / Xml
    /// <summary>Builds a configuration object from the current communication settings and Auto list. Manual jobs are not included.</summary>
    /// <remarks>Auto jobs are built with the encoding in effect at registration. If MessageEncoding was changed after registration, the saved XML is rebuilt with the MessageEncoding at save time when it is loaded.</remarks>
    /// <returns>The configuration object.</returns>
    public TextCommRTUMasterConfig ToConfig() => new()
    {
        Port = Port,
        Baudrate = Baudrate,
        Parity = Parity,
        DataBits = DataBits,
        StopBits = StopBits,
        Timeout = Timeout,
        Interval = Interval,
        AutoReconnect = AutoReconnect,
        DiscardWritesWhileDisconnected = DiscardWritesWhileDisconnected,
        BufferSize = BufferSize,
        MessageEncoding = MessageEncoding.WebName,
        Autos = [.. AutoWorksSnapshot().Select(w =>
        {
            var a = new TextCommAutoConfig { Id = w.MessageID, Slave = w.Slave, Command = w.Command, Message = w.Message };
            if (w.Timeout is int t) a.Timeout = t;
            return a;
        })],
    };

    /// <summary>Applies the communication settings and Auto list of the configuration object. The existing Auto list is replaced by that of the configuration.</summary>
    /// <remarks>Auto items are built with the MessageEncoding of the configuration object. The whole input is validated before it is applied, so if any single value is invalid, nothing is changed.</remarks>
    /// <param name="config">The configuration to apply.</param>
    /// <exception cref="InvalidOperationException">Called while communicating (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">A value is invalid (the message includes the element and attribute names).</exception>
    public void ApplyConfig(TextCommRTUMasterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (IsStart) throw new InvalidOperationException("통신 중에는 설정을 바꿀 수 없습니다. Stop() 후 호출하세요.");
        var encoding = config.Validate();
        var works = (config.Autos ?? [])
            .Select(a => NewWork(a.Id, a.Slave, a.Command, a.Message, null, a.TimeoutSpecified ? a.Timeout : null, encoding))
            .ToList();

        Port = config.Port;
        Baudrate = config.Baudrate;
        Parity = config.Parity;
        DataBits = config.DataBits;
        StopBits = config.StopBits;
        Timeout = config.Timeout;
        Interval = config.Interval;
        AutoReconnect = config.AutoReconnect;
        DiscardWritesWhileDisconnected = config.DiscardWritesWhileDisconnected;
        BufferSize = config.BufferSize;
        MessageEncoding = encoding;
        ReplaceAutoWorks(works);
    }

    /// <summary>Builds an XML element from the current settings and Auto list. Manual jobs are not included.</summary>
    /// <returns>The &lt;TextCommRTUMaster&gt; element.</returns>
    /// <exception cref="InvalidOperationException">An Auto message contains a character that XML 1.0 cannot hold (a control character 0x00 to 0x1F other than tab and line breaks, or an unpaired surrogate).</exception>
    public XElement ToXml()
    {
        var config = ToConfig();
        TextCommXml.VerifyMessages(config.Autos);
        return XmlConfig.Serialize(config);
    }

    /// <summary>Loads the settings and Auto list from an XML element. The existing Auto list is replaced by the XML content.</summary>
    /// <remarks>
    /// <para>Omitted attributes use their default values. An unknown attribute or element is an error (to catch typos).</para>
    /// <para>The whole input is validated before it is applied, so if any single value is invalid, nothing is changed.</para>
    /// <para>You can check the format as an XSD by passing <see cref="TextCommRTUMasterConfig"/> to <see cref="ConfigSchema.Get{T}"/>.</para>
    /// </remarks>
    /// <param name="element">The &lt;TextCommRTUMaster&gt; element.</param>
    /// <exception cref="InvalidOperationException">Called while communicating (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">The root name differs, an unknown attribute or element is present, or a value is invalid.</exception>
    public void FromXml(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (IsStart) throw new InvalidOperationException("통신 중에는 설정을 불러올 수 없습니다. Stop() 후 호출하세요.");
        ApplyConfig(XmlConfig.Deserialize<TextCommRTUMasterConfig>(element));
    }

    /// <summary>Saves the settings and Auto list to an XML file.</summary>
    /// <param name="path">File path.</param>
    /// <exception cref="InvalidOperationException">An Auto message contains a character that XML 1.0 cannot hold (a control character 0x00 to 0x1F other than tab and line breaks, or an unpaired surrogate).</exception>
    public void SaveXml(string path) => ToXml().Save(path);

    /// <summary>Loads the settings and Auto list from an XML file. The rules are the same as <see cref="FromXml"/>.</summary>
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

    AttemptFailedEventArgs Failed(Work w, int attempt, bool isFinal)
        => new(w.MessageID, w.Slave, w.Command, attempt, isFinal, requestSent);

    void ProcessWork(SerialPort sp, Work w)
    {
        var buf = new byte[256];
        Process(w,
            request =>
            {
                sp.DiscardInBuffer();
                sp.Write(request, 0, request.Length);
            },
            remain =>
            {
                sp.ReadTimeout = remain;
                try { return buf.AsSpan(0, sp.Read(buf, 0, buf.Length)).ToArray(); }
                catch (TimeoutException) { return null; }
            });
    }

    // 작업 하나의 송수신·재시도·이벤트. send = 수신 버퍼를 비우고 요청 쓰기, read(남은 ms) = 받은 바이트(시간 안에 없으면 null).
    // 마지막 상태(Ok, SumError, Timeout)를 돌려준다. send가 예외 없이 돌아오면 requestSent.
    internal TextCommStatus Process(Work w, Action<byte[]> send, Func<int, byte[]?> read)
    {
        int attempt = 0;
        try
        {
            int timeout = w.Timeout ?? Timeout;
            for (; ; attempt++)
            {
                requestSent = false;
                send(w.Request);
                requestSent = true;
                // 인코딩 폴백이 예외를 던지는 인코딩이면 디코딩 못 하는 프레임은 깨진 프레임처럼 버리고 계속 기다린다
                string? text = null;
                var f = TextCommPacket.Receive(read, (byte)w.Slave, (byte)w.Command, timeout, BufferSize, fr =>
                {
                    try { text = w.Encoding.GetString(fr.Message); return true; }
                    catch (ArgumentException) { return false; }
                });
                if (f.Status == TextCommStatus.Ok)
                {
                    Raise(MessageReceived, new MessageEventArgs(w.MessageID, w.Slave, w.Command, text!));
                    return f.Status;
                }
                // 합계 오류도 슬레이브·명령이 달라도 재시도한다(기존 동작 유지)
                bool isFinal = attempt >= (w.RepeatCount ?? 0);
                Raise(f.Status == TextCommStatus.SumError ? CheckSumErrorReceived : TimeoutReceived, Failed(w, attempt + 1, isFinal));
                if (isFinal) return f.Status;
            }
        }
        catch
        {
            // 연결 오류로 작업이 중단되면 조용히 사라지지 않게 알린 뒤(더 시도하지 않으므로 최종), 재접속 경로가 돌도록 다시 던진다
            Raise(TimeoutReceived, Failed(w, attempt + 1, true));
            throw;
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
