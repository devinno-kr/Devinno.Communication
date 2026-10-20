using System.IO.Ports;
using System.Xml.Linq;
using Devinno.Communication.Internal;
using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Serial;

/// <summary>
/// Exchanges SECS-II messages over SECS-I (SEMI E4, RS-232). It can be used as either Host or Equipment.
/// </summary>
/// <remarks>
/// <para><b>Line</b>: Half-duplex block transfer. To send: ENQ, then EOT (within T2), then the block, then ACK (within T2). To receive: answer ENQ with EOT, receive the block, and answer ACK/NAK.
/// A block consists of a length byte, a 10-byte header, up to 244 bytes of data, and a 2-byte checksum; a long message is split into 244-byte blocks (up to 7,995,148 bytes).
/// If there is no EOT or the response is not an ACK, it sends again from ENQ, and if it still fails after <see cref="RetryLimit"/> retries, only that message fails (an <see cref="IOException"/> from <see cref="Send"/>; the line is kept).</para>
/// <para><b>Contention</b>: If both sides send ENQ at the same time, the master (<see cref="IsMaster"/>, Equipment by default) keeps waiting for EOT, and the slave first receives the peer's block and then sends its own again.</para>
/// <para><b>Receive checks</b>: If the inter-character time exceeds T1, the length is out of range, or there is a checksum error, the data is discarded until the line is quiet for T1 and a NAK is sent. For a multi-block message, if the time between blocks exceeds T4 the message is discarded,
/// and a block whose header is the same as that of the previous block is treated as a duplicate and discarded (<see cref="DuplicateDetection"/>). If blocks arrive interleaved, the message being assembled is discarded.
/// T3 for a reply is measured up to the first block of the reply, and the following blocks are measured by T4.</para>
/// <para><b>Sending, receiving, and automatic handling</b>: <see cref="Send"/>, <see cref="SendAsync"/>, <see cref="PrimaryReceived"/>, the automatic S9F1/F7/F9 of Equipment, and the automatic SxF0 are the same as in <c>SecsHsms</c>.
/// Messages are sent one at a time, in order (no interleaving).</para>
/// <para><b>Threads</b>: One line I/O thread (handles the send queue and receiving together, and checks timers) and one <see cref="PrimaryReceived"/> dispatcher thread are used.
/// <see cref="DeviceOpened"/>, <see cref="DeviceClosed"/>, and <see cref="ProtocolError"/> are also raised on the same dispatcher thread. Exceptions thrown in any handler are ignored. For <see cref="Send"/> inside a handler, see the description of <see cref="PrimaryReceived"/>.</para>
/// <para><b>Event order</b>: <see cref="DeviceOpened"/>, <see cref="PrimaryReceived"/>, <see cref="ProtocolError"/>, and <see cref="DeviceClosed"/> are raised one at a time on the one dispatcher thread, in the order the I/O thread detected them (those not yet raised at <see cref="Stop"/> are raised in the same order by the thread that called Stop).
/// Each connection (port open until closed) gives <see cref="DeviceOpened"/>, then its primary messages, then one <see cref="DeviceClosed"/>, and the next <see cref="DeviceOpened"/> comes after that <see cref="DeviceClosed"/>.
/// When the link is lost, every pending transaction of that connection ends with <see cref="SecsDisconnectedException"/>; the application may see that failure before or after the connection's <see cref="DeviceClosed"/>, but once it has seen it, that <see cref="DeviceClosed"/> has been or will soon be raised (except at <see cref="Stop"/>).
/// One transaction ends with exactly one result (reply, SxF0, S9Fx, T3, disconnection, or cancellation); a reply that arrives after T3 is an unmatched reply (<see cref="ProtocolError"/>).
/// Without a connection (after <see cref="DeviceClosed"/>, before <see cref="DeviceOpened"/>), <see cref="Send"/>/<see cref="SendAsync"/> throw <see cref="InvalidOperationException"/> at once; treat it like <see cref="SecsDisconnectedException"/> as "no link".
/// No event is raised after <see cref="Stop"/> returns (if Stop cannot end the I/O thread within 3 seconds, that connection's <see cref="DeviceClosed"/> may be missing).
/// Not guaranteed: the order between a <see cref="Send"/>/<see cref="SendAsync"/> result and the events, the order in which the results of concurrent <see cref="SendAsync"/> calls arrive, and the order between <see cref="IsOpen"/> and the events (a snapshot; it can still be true briefly after a <see cref="SecsDisconnectedException"/>). Judge the link state only by <see cref="DeviceOpened"/>/<see cref="DeviceClosed"/>.</para>
/// </remarks>
/// <example>
/// <code>
/// using Devinno.Communication.Secs.Data;
/// using Devinno.Communication.Secs.Serial;
///
/// var eq = new SecsSerial { Role = SecsRole.Equipment, Port = "COM3", Baudrate = 9600, DeviceID = 1 };
/// eq.PrimaryReceived += (s, e) =&gt;
/// {
///     if (e.Message.Stream == 1 &amp;&amp; e.Message.Function == 1)
///         e.Reply(new SecsMessage(1, 2, false, SecsItem.L(SecsItem.A("MDLN"), SecsItem.A("1.0"))));
/// };
/// eq.Start();
/// var ack = eq.Send(new SecsMessage(5, 1, true, SecsItem.L(SecsItem.B(0x81), SecsItem.U4(1001), SecsItem.A("DOOR OPEN"))));
/// </code>
/// </example>
public class SecsSerial : IDisposable
{
    #region class : EventArgs
    /// <summary>Arguments of <see cref="ProtocolError"/>.</summary>
    /// <param name="reason">What happened (a diagnostic sentence).</param>
    public class ProtocolErrorEventArgs(string reason) : EventArgs
    {
        /// <summary>What happened (a diagnostic sentence).</summary>
        public string Reason { get; } = reason;
    }
    #endregion

    #region class : Outgoing / Receiving
    // 보낼 메시지 하나(블록들)와 보내기 완료 알림
    sealed class Outgoing(SecsMessage wire, List<byte[]> blocks)
    {
        public SecsMessage Wire { get; } = wire;
        public List<byte[]> Blocks { get; } = blocks;
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Next;
    }

    // 받는 중인 다중 블록 메시지 하나
    sealed class Receiving(SecsIHeader first, byte[] firstHeaderBytes)
    {
        public SecsIHeader First { get; } = first;
        public byte[] FirstHeaderBytes { get; } = firstHeaderBytes;
        public SecsIHeader Last = first;
        public readonly List<byte> Body = [];
        public long LastBlockTick = Environment.TickCount64;
    }
    #endregion

    #region Properties
    int deviceId, t3 = 45000, t1 = 500, t2 = 10000, t4 = 45000, retryLimit = 3;

    /// <summary>The role of this side (default: Host). Only Equipment automatically sends S9Fx and checks the device ID. The R-bit of the blocks sent (Equipment = 1) is also decided by this value.</summary>
    public SecsRole Role { get; set; } = SecsRole.Host;
    /// <summary>Device ID, 0 to 32767 (default: 0).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Outside 0 to 32767.</exception>
    public int DeviceID { get => Volatile.Read(ref deviceId); set => Volatile.Write(ref deviceId, Check(value, 0, 32767, nameof(DeviceID))); }
    /// <summary>T3 reply timeout in ms (default: 45000). Measured up to the first block of the reply. XML accepts only 1000 to 120000.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Less than 1.</exception>
    public int T3 { get => Volatile.Read(ref t3); set => Volatile.Write(ref t3, Check(value, 1, int.MaxValue, nameof(T3))); }

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
    /// <summary>T1 inter-character timeout in ms (default: 500). If the gap between bytes while receiving a block exceeds it, a NAK is sent. XML accepts only 100 to 10000.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Less than 1.</exception>
    public int T1 { get => Volatile.Read(ref t1); set => Volatile.Write(ref t1, Check(value, 1, int.MaxValue, nameof(T1))); }
    /// <summary>T2 protocol timeout in ms (default: 10000). The wait for ENQ to EOT, EOT to length byte, and block to ACK. XML accepts only 200 to 25000.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Less than 1.</exception>
    public int T2 { get => Volatile.Read(ref t2); set => Volatile.Write(ref t2, Check(value, 1, int.MaxValue, nameof(T2))); }
    /// <summary>T4 inter-block timeout in ms (default: 45000). If the time between blocks of a multi-block message exceeds it, the message being assembled is discarded. XML accepts only 1000 to 120000.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Less than 1.</exception>
    public int T4 { get => Volatile.Read(ref t4); set => Volatile.Write(ref t4, Check(value, 1, int.MaxValue, nameof(T4))); }
    /// <summary>Retry count for sending a block, 0 to 31 (default: 3). Total attempts = 1 + RetryLimit.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Outside 0 to 31.</exception>
    public int RetryLimit { get => Volatile.Read(ref retryLimit); set => Volatile.Write(ref retryLimit, Check(value, 0, 31, nameof(RetryLimit))); }
    /// <summary>Whether to act as master on contention (default: null = master if Role is Equipment).</summary>
    public bool? IsMaster { get; set; }
    /// <summary>Whether to discard a block whose header is the same as that of the previous block as a duplicate (default: true).</summary>
    public bool DuplicateDetection { get; set; } = true;
    /// <summary>Whether to reopen the port at 1-second intervals when it could not be opened or was lost (default: false).</summary>
    public bool AutoReconnect { get; set; } = false;

    /// <summary>Gets whether the port is open (<see cref="Send"/> is possible from this point).</summary>
    public bool IsOpen => session.IsConnected;
    /// <summary>Gets whether it has been started.</summary>
    public bool IsStart { get; private set; }
    /// <summary>Gets whether the object has been disposed.</summary>
    public bool IsDisposed { get; private set; }
    /// <summary>Gets or sets a user-defined tag.</summary>
    public object? Tag { get; set; }

    // 테스트 이음새: null이 아니면 SerialPort 대신 이 회선을 연다
    internal Func<SecsSerialLine>? OpenLine { get; set; }

    bool Master => IsMaster ?? Role == SecsRole.Equipment;
    bool RBit => Role == SecsRole.Equipment;

    static int Check(int value, int min, int max, string name)
        => value < min || value > max ? throw new ArgumentOutOfRangeException(name, value, $"{name}는 {min}~{max} 입니다.") : value;
    #endregion

    #region Member Variable
    const int PollMs = 20;
    const int StopWait = 3000;
    readonly object lifeLock = new();
    readonly object sendLock = new();
    readonly Queue<Outgoing> sendQueue = new();
    int linkGen;            // sendLock으로 보호: 연결마다 1씩 늘림(지난 연결의 메시지가 새 연결로 넘어가지 않게)
    bool linkUp;            // sendLock으로 보호: 연결된 동안만 true
    Outgoing? inFlight;     // sendLock으로 보호: I/O 스레드가 보내는 중인 메시지
    readonly SecsSession session;
    CancellationTokenSource? cts;
    Thread? thread;
    SecsSerialLine? line;
    int sessionGen;         // lifeLock으로 보호: 이번 Start에서 session.Start가 돌려준 실행 번호
    #endregion

    #region Event
    /// <summary>Raised on the dispatcher thread when the peer sends a primary message. If the W-bit is set, reply inside the handler with <see cref="SecsPrimaryEventArgs.Reply"/> (otherwise SxF0 is sent).</summary>
    /// <remarks>Calling <see cref="Send"/> inside the handler does not deadlock. However, there is only one dispatcher thread (one per connection), so while a synchronous <see cref="Send"/> inside the handler waits for its reply (up to T3), the peer's other primary messages are not delivered. If both sides do this in a handler right after connecting, they can wait for each other until T3, so in the connection sequence only start sending with <see cref="SendAsync"/> and handle the replies afterward (await or ContinueWith).</remarks>
    public event EventHandler<SecsPrimaryEventArgs>? PrimaryReceived;
    /// <summary>A diagnostic notification such as a NAK or retry, a checksum error, a discarded block, or an unmatched reply. It does not affect operation.</summary>
    /// <remarks>Raised in order on the same dispatcher thread as <see cref="PrimaryReceived"/>. Calling a synchronous <see cref="Send"/> inside the handler does not deadlock, but delivery of the peer's primary messages stops while it waits for the reply (up to T3) (see <see cref="PrimaryReceived"/>). <see cref="SendAsync"/> is recommended for the connection sequence. Notifications that could not yet be raised at <see cref="Stop"/> are raised by the thread that called Stop. Notifications before start or after stop are discarded.</remarks>
    public event EventHandler<ProtocolErrorEventArgs>? ProtocolError;
    /// <summary>Raised when the port is opened (<see cref="Send"/> is possible from this point).</summary>
    /// <remarks>Raised on the same dispatcher thread as <see cref="PrimaryReceived"/>, before the first primary message of that connection. Calling a synchronous <see cref="Send"/> inside the handler does not deadlock, but delivery of the peer's primary messages stops while it waits for the reply (up to T3) (see <see cref="PrimaryReceived"/>). <see cref="SendAsync"/> is recommended for the connection sequence.</remarks>
    public event EventHandler? DeviceOpened;
    /// <summary>Raised when the port is closed.</summary>
    /// <remarks>Raised on the same dispatcher thread as <see cref="PrimaryReceived"/>, after the primary messages of that connection. The pending transactions of that connection end with <see cref="SecsDisconnectedException"/>, which the application may see before or after this event. If it could not yet be raised at <see cref="Stop"/>, it is raised by the thread that called Stop. Calling a synchronous <see cref="Send"/> inside the handler does not deadlock, but delivery of the peer's primary messages stops while it waits for the reply (up to T3) (see <see cref="PrimaryReceived"/>). <see cref="SendAsync"/> is recommended for the connection sequence.</remarks>
    public event EventHandler? DeviceClosed;
    #endregion

    /// <summary>Creates the SECS-I communication object. Call <see cref="Start"/> after configuring it.</summary>
    public SecsSerial()
    {
        session = new SecsSession(() => Role, () => DeviceID, () => T3, m => SecsIBlock.FirstHeader(m, RBit, SecsIBlock.BlockCount(SecsCodec.Encode(m.Item).Length)).ToArray(),
            e => Raise(PrimaryReceived, e), Error, nameof(SecsSerial));
    }

    #region Start / Stop
    /// <summary>Opens the port and starts communication. Does nothing if it is already started.</summary>
    /// <remarks>If the port cannot be opened and <see cref="AutoReconnect"/> is false, it ends and <see cref="IsStart"/> becomes false.</remarks>
    /// <exception cref="ObjectDisposedException">The object has been disposed.</exception>
    public void Start()
    {
        lock (lifeLock)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (IsStart) return;
            cts?.Dispose();
            cts = new CancellationTokenSource();
            var token = cts.Token;
            // 지난 실행의 스레드가 아직 정리하지 못했을 수 있다(Stop의 Join보다 오래 살아남음): 공유 상태는 새 실행이 여기서 넘겨받고, 지난 실행은 건드리지 않는다
            Volatile.Write(ref line, null);
            FailQueued();
            session.Disconnected();
            int gen = sessionGen = session.Start();
            IsStart = true;
            thread = new Thread(() => Run(token, gen)) { IsBackground = true, Name = nameof(SecsSerial) };
            thread.Start();
        }
    }

    /// <summary>Stops communication and closes the port. Waits up to 3 seconds for the threads to end. May be called multiple times.</summary>
    /// <remarks>A <see cref="Send"/> that was waiting for a reply and messages not yet sent end with <see cref="SecsDisconnectedException"/>. May be called from inside an event handler.</remarks>
    public void Stop()
    {
        CancellationTokenSource? c;
        Thread? t;
        SecsSerialLine? l;
        int gen;
        lock (lifeLock)
        {
            c = cts; cts = null;
            t = thread; thread = null;
            IsStart = false;
            c?.Cancel();
            l = Volatile.Read(ref line);   // 잠금 안에서 읽는다: 기다리는 사이 다시 Start된 실행의 회선을 닫지 않게
            gen = sessionGen;
        }
        l?.Close();
        var until = Environment.TickCount64 + StopWait;
        if (t != null && t != Thread.CurrentThread) t.Join(StopWait);
        session.Stop((int)Math.Max(0, until - Environment.TickCount64), gen);
        lock (lifeLock)
        {
            if (sessionGen == gen) FailQueued();   // 그 사이 다시 Start됐으면 새 실행의 송신 대기열은 건드리지 않는다
        }
    }

    /// <summary>Stops communication and releases its resources. May be called multiple times.</summary>
    public void Dispose()
    {
        lock (lifeLock)
        {
            if (IsDisposed) return;
            IsDisposed = true;
        }
        Stop();
        GC.SuppressFinalize(this);
    }
    #endregion

    #region Send
    /// <summary>Sends a primary message and, if the W-bit is set, waits for and returns the reply. It waits on the calling thread.</summary>
    /// <remarks>The system bytes and device ID are filled in when sending, and the object passed in is not modified. T3 is measured from the time the ACK of the last block is received. A <see cref="SecsDisconnectedException"/> may be seen before or after that connection's <see cref="DeviceClosed"/>. The wait does not depend on the thread pool, and calling it inside an event handler does not deadlock.</remarks>
    /// <param name="primary">The primary message (odd function).</param>
    /// <returns>The reply message. null after sending if the W-bit is not set.</returns>
    /// <exception cref="ArgumentNullException">primary is null.</exception>
    /// <exception cref="ArgumentException">The function is even, or the body exceeds 7,995,148 bytes.</exception>
    /// <exception cref="InvalidOperationException">The port is not open.</exception>
    /// <exception cref="IOException">The peer did not accept the block up to the retry limit (the line is kept).</exception>
    /// <exception cref="SecsTimeoutException">The first block of the reply did not arrive within T3, or the time between reply blocks exceeded T4.</exception>
    /// <exception cref="SecsAbortException">The peer replied with SxF0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The port was closed while sending or waiting.</exception>
    public SecsMessage? Send(SecsMessage primary) => session.Send(primary);

    /// <summary>Sends a primary message and, if the W-bit is set, returns a Task that waits for the reply. The rules and exceptions are the same as <see cref="Send"/>.</summary>
    /// <remarks>The continuation runs on the calling thread (when the Task completes synchronously) or on the thread pool, never on the I/O or dispatcher thread. A <see cref="SecsDisconnectedException"/> may be seen before or after that connection's <see cref="DeviceClosed"/>.</remarks>
    /// <param name="primary">The primary message (odd function).</param>
    /// <param name="cancellationToken">Cancels the wait (a message whose sending has already started is sent to the end).</param>
    /// <returns>The reply message. null if the W-bit is not set.</returns>
    /// <exception cref="ArgumentNullException">primary is null.</exception>
    /// <exception cref="ArgumentException">The function is even, or the body is too large.</exception>
    /// <exception cref="InvalidOperationException">The port is not open.</exception>
    /// <exception cref="OperationCanceledException">The wait was canceled.</exception>
    public Task<SecsMessage?> SendAsync(SecsMessage primary, CancellationToken cancellationToken = default)
        => session.SendAsync(primary, cancellationToken);

    // GEM 내부용: 순서 있는 완료로 보낸다(완료는 디스패처에서 와이어 순서로, SecsSession.SendOrdered)
    internal void SendOrdered(SecsMessage primary, Action<Task<SecsMessage?>> completed) => session.SendOrdered(primary, completed);

    // GEM 내부용: 디스패처 대기열에 넣는다(멈춰 있으면 버림)
    internal void Post(Action action) => session.Post(action);

    internal bool IsDispatcherThread => session.IsDispatcherThread;

    // 세션 송신 함수(gen = 그 연결의 번호): 블록으로 나눠 송신 큐에 넣고, I/O 스레드가 다 보내면(마지막 ACK) 완료
    // 송신 실패는 Done과 함께 session.TransmitFailed로도 알린다(ordered 송신은 송신 Task를 기다리지 않음). TransmitFailed는 sendLock 밖에서(완료가 그 자리에서 돌 수 있음)
    Task Transmit(SecsMessage wire, int gen)
    {
        var o = new Outgoing(wire, SecsIBlock.Split(wire, RBit));
        bool down;
        lock (sendLock)
        {
            down = !linkUp || gen != linkGen;
            if (!down) sendQueue.Enqueue(o);
        }
        if (down)
        {
            var ex = new SecsDisconnectedException($"S{wire.Stream}F{wire.Function}을(를) 보내기 전에 포트가 닫혔습니다.");
            o.Done.TrySetException(ex);
            session.TransmitFailed(wire, ex);
        }
        return o.Done.Task;
    }

    Outgoing? NextOutgoing()
    {
        lock (sendLock) return inFlight = sendQueue.Count > 0 ? sendQueue.Dequeue() : null;
    }

    void ClearInFlight()
    {
        lock (sendLock) inFlight = null;
    }

    void FailQueued()
    {
        List<Outgoing> list;
        lock (sendLock)
        {
            linkUp = false;
            list = [.. sendQueue];
            sendQueue.Clear();
            if (inFlight != null) { list.Add(inFlight); inFlight = null; }
        }
        foreach (var o in list)
        {
            var ex = new SecsDisconnectedException($"S{o.Wire.Stream}F{o.Wire.Function}을(를) 보내기 전에 포트가 닫혔습니다.");
            o.Done.TrySetException(ex);
            session.TransmitFailed(o.Wire, ex);
        }
    }
    #endregion

    #region Config / Xml
    /// <summary>Creates a configuration object from the current communication settings.</summary>
    /// <returns>The configuration object.</returns>
    public SecsSerialConfig ToConfig()
    {
        var c = new SecsSerialConfig
        {
            Role = Role,
            DeviceID = DeviceID,
            T3 = T3,
            Port = Port,
            Baudrate = Baudrate,
            Parity = Parity,
            DataBits = DataBits,
            StopBits = StopBits,
            T1 = T1,
            T2 = T2,
            T4 = T4,
            RetryLimit = RetryLimit,
            DuplicateDetection = DuplicateDetection,
            AutoReconnect = AutoReconnect,
        };
        if (IsMaster is bool m) c.IsMaster = m;
        return c;
    }

    /// <summary>Applies the communication settings of a configuration object.</summary>
    /// <remarks>Everything is validated first and then applied, so if any value is invalid, nothing is changed. The timer ranges are checked with the XML rules (e.g., T1 100 to 10000).</remarks>
    /// <param name="config">The configuration to apply.</param>
    /// <exception cref="InvalidOperationException">Called while communicating (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">A value is invalid (the message includes the element and attribute names).</exception>
    public void ApplyConfig(SecsSerialConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (IsStart) throw new InvalidOperationException("통신 중에는 설정을 바꿀 수 없습니다. Stop() 후 호출하세요.");
        config.Validate();
        Role = config.Role;
        DeviceID = config.DeviceID;
        T3 = config.T3;
        Port = config.Port;
        Baudrate = config.Baudrate;
        Parity = config.Parity;
        DataBits = config.DataBits;
        StopBits = config.StopBits;
        T1 = config.T1;
        T2 = config.T2;
        T4 = config.T4;
        RetryLimit = config.RetryLimit;
        IsMaster = config.IsMasterSpecified ? config.IsMaster : null;
        DuplicateDetection = config.DuplicateDetection;
        AutoReconnect = config.AutoReconnect;
    }

    /// <summary>Creates an XML element from the current communication settings. Messages to send and event handlers are not saved.</summary>
    /// <returns>The &lt;SecsSerial&gt; element.</returns>
    public XElement ToXml() => XmlConfig.Serialize(ToConfig());

    /// <summary>Loads the communication settings from an XML element.</summary>
    /// <remarks>
    /// <para>Omitted attributes use their default values (if IsMaster is omitted, the Role default applies). An unknown attribute or element is an error (to catch typos).</para>
    /// <para>Everything is validated first and then applied, so if any value is invalid, nothing is changed.</para>
    /// <para>You can check the format by generating the XSD with <see cref="ConfigSchema.Get{T}"/> for <see cref="SecsSerialConfig"/>.</para>
    /// </remarks>
    /// <param name="element">The &lt;SecsSerial&gt; element.</param>
    /// <exception cref="InvalidOperationException">Called while communicating (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">The root name differs, an unknown attribute or element is present, or a value is invalid.</exception>
    public void FromXml(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (IsStart) throw new InvalidOperationException("통신 중에는 설정을 불러올 수 없습니다. Stop() 후 호출하세요.");
        ApplyConfig(XmlConfig.Deserialize<SecsSerialConfig>(element));
    }

    /// <summary>Saves the communication settings to an XML file.</summary>
    /// <param name="path">File path.</param>
    public void SaveXml(string path) => ToXml().Save(path);

    /// <summary>Loads the communication settings from an XML file. The rules are the same as <see cref="FromXml"/>.</summary>
    /// <param name="path">File path.</param>
    public void LoadXml(string path) => FromXml(XElement.Load(path));
    #endregion

    #region Loop
    // runGen = 이 실행의 번호(Start의 sessionGen). 공유 상태(line·linkGen/linkUp·송신 대기열·세션 연결·DeviceOpened/Closed)는
    // "이 실행이 아직 지금 실행"(lifeLock 안에서 Stop 전·다시 Start 전)일 때만 바꾼다. 지난 실행은 자기 회선만 닫는다
    void Run(CancellationToken token, int runGen)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var l = TryOpen();
                if (l != null)
                {
                    lock (lifeLock)
                    {
                        // Stop은 lifeLock 안에서 취소하므로, 여기서 취소되지 않았으면 Stop·다시 Start 전이다
                        if (token.IsCancellationRequested || sessionGen != runGen) { l.Close(); break; }
                        Volatile.Write(ref line, l);
                        int gen;
                        lock (sendLock) { linkUp = true; gen = ++linkGen; }
                        session.Connected((wire, _) => Transmit(wire, gen));
                        session.Post(() => Raise(DeviceOpened));
                    }
                    try
                    {
                        LinkLoop(l, token);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ObjectDisposedException or OperationCanceledException)
                    {
                        if (!token.IsCancellationRequested) Error($"회선 오류로 포트를 닫습니다: {ex.Message}");
                    }
                    finally
                    {
                        Interlocked.CompareExchange(ref line, null, l);
                        l.Close();
                        lock (lifeLock)
                        {
                            if (sessionGen == runGen)   // 그 사이 다시 Start됐으면 새 실행의 대기열·세션·이벤트는 건드리지 않는다
                            {
                                FailQueued();
                                session.Disconnected();
                                session.Post(() => Raise(DeviceClosed));
                            }
                        }
                    }
                }
                if (!AutoReconnect || token.IsCancellationRequested) break;
                token.WaitHandle.WaitOne(1000);
            }
        }
        catch (Exception ex) { if (!token.IsCancellationRequested) Error($"회선 루프가 예기치 않은 오류로 끝났습니다: {ex.Message}"); }
        finally
        {
            // 스스로 끝났을 때(AutoReconnect = false, 예기치 않은 오류): Stop이 이미 맡았으면 아무것도 하지 않고,
            // 그 사이 다시 Start됐어도 새 실행의 세션은 멈추지 않는다(실행 번호로 확인)
            int? gen = null;
            lock (lifeLock)
            {
                if (thread == Thread.CurrentThread) { IsStart = false; thread = null; gen = sessionGen; }
            }
            if (gen != null) session.Stop(0, gen);
        }
    }

    SecsSerialLine? TryOpen()
    {
        if (OpenLine != null)
        {
            try { return OpenLine(); }
            catch (Exception) { return null; }
        }
        SerialPort? sp = null;
        try
        {
            sp = new SerialPort(Port, Baudrate, Parity, DataBits, StopBits);
            sp.Open();
            return SecsSerialLine.From(sp);
        }
        catch (Exception)
        {
            sp?.Dispose();
            return null;
        }
    }

    // 회선 하나: 보낼 메시지가 있으면 블록 단위로 보내고, 없으면 상대의 ENQ를 기다린다
    void LinkLoop(SecsSerialLine l, CancellationToken token)
    {
        Receiving? rx = null;
        byte[]? lastHeader = null;
        Outgoing? cur = null;
        while (!token.IsCancellationRequested)
        {
            session.CheckTimeouts();
            if (rx != null && Environment.TickCount64 - rx.LastBlockTick > T4) Abandon(ref rx, $"블록 사이 간격이 T4({T4}ms)를 넘었습니다");

            cur ??= NextOutgoing();
            if (cur != null)
            {
                if (!SendBlock(l, cur.Blocks[cur.Next], ref rx, ref lastHeader, token))
                {
                    if (token.IsCancellationRequested) break;
                    var ex = new IOException($"S{cur.Wire.Stream}F{cur.Wire.Function} 블록 {cur.Next + 1}을(를) 재시도 {RetryLimit}번 뒤에도 보내지 못했습니다.");
                    cur.Done.TrySetException(ex);
                    session.TransmitFailed(cur.Wire, ex);   // 회선은 그대로: 이 메시지(W 있든 없든)만 실패
                    cur = null;
                    ClearInFlight();
                    continue;
                }
                if (++cur.Next == cur.Blocks.Count)
                {
                    session.TransmitCompleted(cur.Wire);   // T3는 여기(마지막 ACK)부터: Done의 이어 실행은 스레드풀에서 늦게 돌 수 있다
                    cur.Done.TrySetResult();
                    cur = null;
                    ClearInFlight();
                }
                continue;
            }

            int b = l.ReadByte(PollMs);
            if (b == SecsIBlock.ENQ)
            {
                l.Write(SecsIBlock.EOT);
                ReceiveBlock(l, ref rx, ref lastHeader);
            }
            else if (b >= 0) Error($"기다리지 않은 바이트 0x{b:X2}를 버렸습니다.");
        }
        if (cur != null)
        {
            var ex = new SecsDisconnectedException($"S{cur.Wire.Stream}F{cur.Wire.Function}을(를) 보내는 중에 포트가 닫혔습니다.");
            cur.Done.TrySetException(ex);
            session.TransmitFailed(cur.Wire, ex);
        }
    }

    // 블록 하나를 ENQ부터 보낸다. 재시도 한계를 넘으면 false
    bool SendBlock(SecsSerialLine l, byte[] block, ref Receiving? rx, ref byte[]? lastHeader, CancellationToken token)
    {
        int failures = 0;
        while (!token.IsCancellationRequested)
        {
            if (failures > RetryLimit) return false;
            l.Write(SecsIBlock.ENQ);
            bool eot = false, yielded = false;
            long deadline = Environment.TickCount64 + T2;
            while (!token.IsCancellationRequested)
            {
                int left = (int)(deadline - Environment.TickCount64);
                if (left <= 0) break;
                int c = l.ReadByte(Math.Min(left, PollMs));
                if (c == SecsIBlock.EOT) { eot = true; break; }
                if (c == SecsIBlock.ENQ)
                {
                    if (Master) continue;          // master: 상대 ENQ는 무시하고 EOT를 계속 기다림
                    l.Write(SecsIBlock.EOT);       // slave: 양보해서 상대 블록을 먼저 받고 ENQ부터 다시
                    ReceiveBlock(l, ref rx, ref lastHeader);
                    yielded = true;
                    break;
                }
            }
            if (yielded) continue;
            if (!eot)
            {
                failures++;
                Error($"ENQ에 T2({T2}ms) 안에 EOT가 없습니다(실패 {failures}번).");
                continue;
            }
            l.Write(block);
            int r = l.ReadByte(T2);
            if (r == SecsIBlock.ACK) return true;
            failures++;
            Error(r < 0 ? $"블록에 T2({T2}ms) 안에 ACK가 없습니다(실패 {failures}번)." : $"블록에 ACK 대신 0x{r:X2}를 받았습니다(실패 {failures}번).");
        }
        return false;
    }

    // EOT를 보낸 뒤: 길이 바이트·블록·체크섬을 받아 ACK/NAK
    void ReceiveBlock(SecsSerialLine l, ref Receiving? rx, ref byte[]? lastHeader)
    {
        int len = l.ReadByte(T2);
        if (len < 0) { Nak(l, false, $"EOT 뒤 T2({T2}ms) 안에 길이 바이트가 없습니다."); return; }
        if (len < SecsIBlock.MinLength || len > SecsIBlock.MaxLength) { Nak(l, true, $"블록 길이 {len}이(가) 10~254 밖입니다."); return; }
        var buf = new byte[len + 2];
        for (int i = 0; i < buf.Length; i++)
        {
            int c = l.ReadByte(T1);
            if (c < 0) { Nak(l, false, $"블록을 받는 중 T1({T1}ms) 동안 문자가 오지 않았습니다."); return; }
            buf[i] = (byte)c;
        }
        if (SecsIBlock.Checksum(buf.AsSpan(0, len)) != (buf[len] << 8 | buf[len + 1])) { Nak(l, true, "블록 체크섬이 맞지 않습니다."); return; }
        l.Write(SecsIBlock.ACK);
        OnBlock(buf.AsSpan(0, SecsIHeader.Length).ToArray(), buf.AsSpan(SecsIHeader.Length, len - SecsIHeader.Length), ref rx, ref lastHeader);
    }

    // drain이면 T1 동안 조용해질 때까지 버린 뒤 NAK
    void Nak(SecsSerialLine l, bool drain, string reason)
    {
        if (drain)
        {
            long until = Environment.TickCount64 + T2;   // 상대가 계속 보내도 T2를 넘겨 붙잡히지 않는다
            while (Environment.TickCount64 < until && l.ReadByte(T1) >= 0) { }
        }
        l.Write(SecsIBlock.NAK);
        Error(reason + " NAK를 보냈습니다.");
    }

    // 제대로 받은 블록 하나: 중복 검사, 다중 블록 조립, 완성되면 세션에 넘김
    void OnBlock(byte[] headerBytes, ReadOnlySpan<byte> data, ref Receiving? rx, ref byte[]? lastHeader)
    {
        if (DuplicateDetection && lastHeader != null && headerBytes.AsSpan().SequenceEqual(lastHeader))
        {
            Error("직전 블록과 헤더가 같은 중복 블록을 버렸습니다.");
            return;
        }
        lastHeader = headerBytes;
        var h = SecsIHeader.Parse(headerBytes);

        if (rx != null)
        {
            if (h.IsNextOf(rx.Last))
            {
                rx.Body.AddRange(data);
                rx.Last = h;
                rx.LastBlockTick = Environment.TickCount64;
                if (h.EBit) Complete(ref rx);
                return;
            }
            Abandon(ref rx, "다른 메시지의 블록이 왔습니다");
        }

        if (!(h.BlockNumber == 1 || (h.BlockNumber == 0 && h.EBit)))
        {
            Error($"첫 블록 번호가 {h.BlockNumber}인 블록을 버렸습니다(S{h.Stream}F{h.Function}).");
            return;
        }
        rx = new Receiving(h, headerBytes);
        rx.Body.AddRange(data);
        if (h.EBit) Complete(ref rx);
        else session.ReplyStarted(h.SystemBytes, h.Stream, h.Function);
    }

    void Complete(ref Receiving? rx)
    {
        var r = rx!;
        rx = null;
        var f = r.First;
        SecsMessage msg;
        try { msg = new SecsMessage(f.Stream, f.Function, f.WBit, SecsCodec.Decode(r.Body.ToArray())).WithHeader(f.SystemBytes, f.DeviceID); }
        catch (FormatException ex)
        {
            session.DecodeFailed(r.FirstHeaderBytes, ex.Message);
            if (f.Function % 2 == 0) session.ReplyAbandoned(f.SystemBytes, "응답 본문을 해석하지 못했습니다");
            return;
        }
        session.Received(msg, r.FirstHeaderBytes);
    }

    void Abandon(ref Receiving? rx, string reason)
    {
        var r = rx!;
        rx = null;
        Error($"조립 중인 S{r.First.Stream}F{r.First.Function}(블록 {r.Last.BlockNumber}까지)을(를) 버렸습니다: {reason}.");
        if (r.First.Function % 2 == 0) session.ReplyAbandoned(r.First.SystemBytes, reason);
    }
    #endregion

    #region Raise
    void Error(string reason) => session.Post(() => Raise(ProtocolError, new ProtocolErrorEventArgs(reason)));

    void Raise(EventHandler? handler)
    {
        if (handler == null) return;
        try { handler(this, EventArgs.Empty); } catch (Exception) { }
    }

    void Raise<T>(EventHandler<T>? handler, T args)
    {
        if (handler == null) return;
        try { handler(this, args); } catch (Exception) { }
    }
    #endregion
}
