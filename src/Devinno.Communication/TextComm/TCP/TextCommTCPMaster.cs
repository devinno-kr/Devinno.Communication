using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Devinno.Communication.Internal;

namespace Devinno.Communication.TextComm.TCP;

/// <summary>
/// Master (client) that exchanges TextComm frames (STX/ETX/DLE text messages) over TCP.
/// </summary>
/// <remarks>
/// <para>Frame: STX(0x02) | slave | command | message bytes | sum | ETX(0x03). sum is the low byte of the sum of slave + command + the message bytes, and
/// 0x02, 0x03, and 0x10 are sent as 0x10 followed by (value + 0x10) (DLE escaping).</para>
/// <para>slave and command are 0 to 255. The message is converted to bytes with <see cref="MessageEncoding"/> (default ASCII); the request is built with the encoding in effect when the job is registered, and the response is read with that same encoding.
/// Changing MessageEncoding after registration does not affect jobs that are already registered.</para>
/// <para>Bytes left in the socket are discarded just before a request is sent. A response is the frame whose slave and command match the request; other frames (such as a late response to an earlier request) and malformed frames are discarded, and reading continues until the timeout.
/// If the sum is wrong, <see cref="CheckSumErrorReceived"/> is raised; if there is no response in time, <see cref="TimeoutReceived"/> is raised; either way the request is resent up to repeatCount times (total transmissions = 1 + repeatCount).
/// A response frame with a wrong sum raises <see cref="CheckSumErrorReceived"/> and the request is resent, even if its (untrustworthy) slave and command differ from the request.</para>
/// <para>A response frame (STX to ETX) longer than <see cref="BufferSize"/> is discarded. The default port 7897 is a conventional default; the protocol defines no port.</para>
/// <para>Manual jobs are processed before Auto jobs. Responses arrive through <see cref="MessageReceived"/>, and the id given at registration is returned as <c>MessageID</c>.</para>
/// <para>The communication loop runs on a dedicated background thread, and all events are raised on that thread. To update the UI, marshal asynchronously, as with BeginInvoke (WinForms) or Dispatcher.BeginInvoke (WPF). Exceptions thrown in a handler are ignored and the loop continues.</para>
/// <para>The XML configuration (<see cref="ToXml"/>, <see cref="SaveXml"/>) cannot store an Auto message that contains characters XML 1.0 cannot hold (control characters 0x00 to 0x1F other than tab and line breaks, and so on). Manage such Auto items in code.</para>
/// </remarks>
/// <example>
/// <code>
/// using Devinno.Communication.TextComm.TCP;
///
/// var tc = new TextCommTCPMaster { RemoteIP = "192.168.0.50", RemotePort = 7897 };
/// tc.AutoSend(1, 1, 0x41, "READ");
/// tc.MessageReceived += (s, e) =&gt; Console.WriteLine($"{e.MessageID} {e.Slave}/{e.Command}: {e.Message}");
/// tc.CheckSumErrorReceived += (s, e) =&gt; Console.WriteLine($"Sum check error {e.MessageID}");
/// tc.Start();
/// tc.ManualSend(10, 1, 0x42, "SET 100", repeatCount: 2);
/// </code>
/// </example>
public class TextCommTCPMaster : IDisposable
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
    /// <summary>Common event arguments for request result events. Timeout and sum check error events use this type as-is.</summary>
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
    #endregion

    #region Properties
    /// <summary>IP address or host name of the slave to connect to (default: "127.0.0.1").</summary>
    public string RemoteIP { get; set; } = "127.0.0.1";
    /// <summary>Port to connect to (default: 7897).</summary>
    public int RemotePort { get; set; } = 7897;

    /// <summary>Response timeout in ms (default: 100). A per-job timeout, if given, takes precedence.</summary>
    public int Timeout { get; set; } = 100;
    /// <summary>Wait between jobs in ms (default: 10).</summary>
    public int Interval { get; set; } = 10;
    /// <summary>Whether to reconnect at 1-second intervals when the connection could not be made or was lost (default: true).</summary>
    public bool AutoReconnect { get; set; } = true;
    /// <summary>Maximum size in bytes of a response frame (STX to ETX) that can be received (default: 1024). Longer frames are discarded.</summary>
    public int BufferSize { get; set; } = 1024;
    /// <summary>Message string encoding (default: ASCII). Applied when a job is registered.</summary>
    public Encoding MessageEncoding { get; set; } = Encoding.ASCII;

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
    #endregion

    #region Event
    /// <summary>Raised when a response with the same slave and command as the request is received.</summary>
    public event EventHandler<MessageEventArgs>? MessageReceived;
    /// <summary>Raised when there is no response in time, or when the connection is lost during a request. Raised on every retry.</summary>
    public event EventHandler<ResultEventArgs>? TimeoutReceived;
    /// <summary>Raised when the sum byte of a response is wrong. Raised on every retry.</summary>
    public event EventHandler<ResultEventArgs>? CheckSumErrorReceived;
    /// <summary>Raised when the TCP connection is established.</summary>
    public event EventHandler? DeviceOpened;
    /// <summary>Raised when the TCP connection is lost.</summary>
    public event EventHandler? DeviceClosed;
    #endregion

    #region Start / Stop
    /// <summary>Connects to the slave and starts the communication loop. Does nothing if it is already running.</summary>
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
            thread = new Thread(() => Run(token)) { IsBackground = true, Name = nameof(TextCommTCPMaster) };
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

    // 작업 하나를 만든다. 인코딩은 인자로 받는다(ApplyConfig가 적용 전에 검사할 수 있도록).
    internal static Work NewWork(int id, int slave, int command, string message, int? repeatCount, int? timeout, Encoding encoding)
    {
        var request = TextCommPacket.BuildRequest(encoding, slave, command, message);
        return new Work(id, slave, command, message, encoding, request, repeatCount, timeout);
    }

    void AddAuto(Work w) { lock (workLock) autoWorks.Add(w); }
    void AddManual(Work w) { lock (workLock) manualWorks.Add(w); }
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
    /// <param name="repeatCount">Retry count when there is no response or the sum check fails (null for no retries).</param>
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
    public TextCommTCPMasterConfig ToConfig() => new()
    {
        RemoteIP = RemoteIP,
        RemotePort = RemotePort,
        Timeout = Timeout,
        Interval = Interval,
        AutoReconnect = AutoReconnect,
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
    public void ApplyConfig(TextCommTCPMasterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (IsStart) throw new InvalidOperationException("통신 중에는 설정을 바꿀 수 없습니다. Stop() 후 호출하세요.");
        var encoding = config.Validate();
        var works = (config.Autos ?? [])
            .Select(a => NewWork(a.Id, a.Slave, a.Command, a.Message, null, a.TimeoutSpecified ? a.Timeout : null, encoding))
            .ToList();

        RemoteIP = config.RemoteIP;
        RemotePort = config.RemotePort;
        Timeout = config.Timeout;
        Interval = config.Interval;
        AutoReconnect = config.AutoReconnect;
        BufferSize = config.BufferSize;
        MessageEncoding = encoding;
        ReplaceAutoWorks(works);
    }

    /// <summary>Builds an XML element from the current settings and Auto list. Manual jobs are not included.</summary>
    /// <returns>The &lt;TextCommTCPMaster&gt; element.</returns>
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
    /// <para>You can check the format as an XSD by passing <see cref="TextCommTCPMasterConfig"/> to <see cref="ConfigSchema.Get{T}"/>.</para>
    /// </remarks>
    /// <param name="element">The &lt;TextCommTCPMaster&gt; element.</param>
    /// <exception cref="InvalidOperationException">Called while communicating (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">The root name differs, an unknown attribute or element is present, or a value is invalid.</exception>
    public void FromXml(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (IsStart) throw new InvalidOperationException("통신 중에는 설정을 불러올 수 없습니다. Stop() 후 호출하세요.");
        ApplyConfig(XmlConfig.Deserialize<TextCommTCPMasterConfig>(element));
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

    static ResultEventArgs Result(Work w) => new(w.MessageID, w.Slave, w.Command);

    void ProcessWork(Socket s, Work w)
    {
        var buf = new byte[1024];
        try
        {
            Process(w,
                request =>
                {
                    Drain(s);   // 이전 요청의 늦은 응답을 먼저 버린다
                    s.Send(request);
                },
                remain =>
                {
                    if (!s.Poll(remain * 1000, SelectMode.SelectRead)) return null;
                    int n = s.Receive(buf, 0, buf.Length, SocketFlags.None);
                    if (n == 0) throw new IOException("상대가 연결을 끊었습니다.");
                    return buf.AsSpan(0, n).ToArray();
                });
        }
        catch
        {
            // 연결 오류로 작업이 중단되면 조용히 사라지지 않게 알린 뒤, 재접속 경로가 돌도록 다시 던진다
            Raise(TimeoutReceived, Result(w));
            throw;
        }
    }

    static void Drain(Socket s)
    {
        if (s.Available == 0) return;
        var tmp = new byte[s.Available];
        while (s.Available > 0) s.Receive(tmp, 0, Math.Min(tmp.Length, s.Available), SocketFlags.None);
    }

    // 작업 하나의 송수신·재시도·이벤트. send = 남은 바이트를 버리고 요청 쓰기, read(남은 ms) = 받은 바이트(시간 안에 없으면 null).
    // 마지막 상태(Ok, SumError, Timeout)를 돌려준다.
    internal TextCommStatus Process(Work w, Action<byte[]> send, Func<int, byte[]?> read)
    {
        int timeout = w.Timeout ?? Timeout;
        for (int attempt = 0; ; attempt++)
        {
            send(w.Request);
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
            Raise(f.Status == TextCommStatus.SumError ? CheckSumErrorReceived : TimeoutReceived, Result(w));
            if (attempt >= (w.RepeatCount ?? 0)) return f.Status;
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
