using System.Net;
using System.Net.Sockets;
using System.Xml.Linq;
using Devinno.Communication.Internal;
using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Hsms;

/// <summary>
/// Exchanges SECS-II messages over HSMS-SS (SEMI E37/E37.1, TCP). It can be used as either Host or Equipment.
/// </summary>
/// <remarks>
/// <para><b>Role and connection mode</b>: <see cref="Role"/> (Host/Equipment) and <see cref="Mode"/> (Active = TCP client, Passive = TCP server) are decided separately. Usually the equipment is Passive and the host is Active.
/// Active requests Select after connecting (Selected if the status is 0 within T6), and if the connection is lost it reconnects after T5 (<see cref="AutoReconnect"/>).
/// Passive keeps waiting for a connection while <see cref="Start"/> is in effect and accepts only one connection at a time (a second connection is answered with status 1 to Select and then closed).
/// If the connection does not become Selected within T7 after connecting, it is closed.</para>
/// <para><b>Sending</b>: <see cref="Send"/>/<see cref="SendAsync"/> accept only a primary message with an odd function, and if the W-bit is set they return the reply.
/// If the reply does not arrive within T3, a <see cref="SecsTimeoutException"/> is thrown (the connection is kept); if the peer replies with SxF0, a <see cref="SecsAbortException"/>;
/// if the equipment replies with S9Fx, a <see cref="SecsErrorException"/>; and if the connection is lost while waiting, a <see cref="SecsDisconnectedException"/>. They may be called concurrently from several threads.</para>
/// <para><b>Receiving</b>: The peer's primary messages are raised by <see cref="PrimaryReceived"/> in the order received on a dedicated dispatcher thread.
/// Reply from the handler with <see cref="SecsPrimaryEventArgs.Reply"/>; if the W-bit is set and the handler does not reply, SxF0 is sent automatically. Calling <see cref="Send"/> inside the handler does not deadlock. However, there is only one dispatcher thread (one per connection), so while a synchronous <see cref="Send"/> inside the handler waits for its reply (up to T3), the peer's other primary messages are not delivered. If both sides do this in a handler right after connecting, they can wait for each other until T3, so in the connection sequence only start sending with <see cref="SendAsync"/> and handle the replies afterward (await or ContinueWith).</para>
/// <para><b>Automatic handling by Equipment only</b>: a message with a different device ID, S9F1 (no event is raised); a body that cannot be parsed, S9F7; T3 exceeded for its own primary message, S9F9; <see cref="MaxMessageSize"/> exceeded, S9F11.
/// S9F3/S9F5 (unrecognized stream/function) are not sent (the application or the GEM layer decides).</para>
/// <para><b>Keeping the connection</b>: Both sides respond to Linktest automatically; Linktest is sent every <see cref="LinktestInterval"/> and the connection is closed if no reply arrives within T6. The connection is also closed if the gap between bytes within one message exceeds T8.
/// <see cref="Stop"/> sends Separate and then closes if Selected, and the connection is closed when the peer's Separate or Deselect (after the reply) is received.</para>
/// <para><b>Threads</b>: One receive thread per connection (synchronous socket I/O and timer checks), one accept-wait thread for Passive, and one <see cref="PrimaryReceived"/> dispatcher thread are used.
/// <see cref="DeviceOpened"/>, <see cref="DeviceClosed"/>, and <see cref="ProtocolError"/> are also raised on the same dispatcher thread. Exceptions thrown in any handler are ignored.
/// To update the UI, marshal asynchronously, as with BeginInvoke (WinForms) or Dispatcher.BeginInvoke (WPF).</para>
/// <para><b>Event order</b>: <see cref="DeviceOpened"/>, <see cref="PrimaryReceived"/>, <see cref="ProtocolError"/>, and <see cref="DeviceClosed"/> are raised one at a time on the one dispatcher thread, in the order the I/O thread detected them (those not yet raised at <see cref="Stop"/> are raised in the same order by the thread that called Stop).
/// Each connection (Selected until disconnected) gives <see cref="DeviceOpened"/>, then its primary messages, then one <see cref="DeviceClosed"/>, and the next <see cref="DeviceOpened"/> comes after that <see cref="DeviceClosed"/>.
/// When the link is lost, every pending transaction of that connection ends with <see cref="SecsDisconnectedException"/>; the application may see that failure before or after the connection's <see cref="DeviceClosed"/>, but once it has seen it, that <see cref="DeviceClosed"/> has been or will soon be raised (except at <see cref="Stop"/>).
/// One transaction ends with exactly one result (reply, SxF0, S9Fx, T3, disconnection, or cancellation); a reply that arrives after T3 is an unmatched reply (<see cref="ProtocolError"/>).
/// Without a connection (after <see cref="DeviceClosed"/>, before <see cref="DeviceOpened"/>), <see cref="Send"/>/<see cref="SendAsync"/> throw <see cref="InvalidOperationException"/> at once; treat it like <see cref="SecsDisconnectedException"/> as "no link".
/// No event is raised after <see cref="Stop"/> returns (if Stop cannot end the I/O thread within 3 seconds, that connection's <see cref="DeviceClosed"/> may be missing).
/// Not guaranteed: the order between a <see cref="Send"/>/<see cref="SendAsync"/> result and the events, the order in which the results of concurrent <see cref="SendAsync"/> calls arrive, and the order between <see cref="IsOpen"/>/<see cref="State"/> and the events (a snapshot; it can still be true briefly after a <see cref="SecsDisconnectedException"/>). Judge the link state only by <see cref="DeviceOpened"/>/<see cref="DeviceClosed"/>.</para>
/// </remarks>
/// <example>
/// <code>
/// using Devinno.Communication.Secs.Data;
/// using Devinno.Communication.Secs.Hsms;
///
/// // Host: connects to the equipment (Passive, 192.168.0.50:5000)
/// var host = new SecsHsms { Role = SecsRole.Host, Mode = HsmsMode.Active, RemoteIP = "192.168.0.50", RemotePort = 5000, DeviceID = 0 };
/// host.PrimaryReceived += (s, e) =&gt;
/// {
///     if (e.Message.Stream == 6 &amp;&amp; e.Message.Function == 11)                 // event report
///         e.Reply(new SecsMessage(6, 12, false, SecsItem.B(0)));
/// };
/// host.DeviceOpened += async (s, e) =&gt;
/// {
///     var reply = await host.SendAsync(new SecsMessage(1, 13, true, SecsItem.L()));   // establish communication
///     Console.WriteLine(reply?.ToSml());
/// };
/// host.Start();
///
/// // Equipment: waits for a connection
/// var eq = new SecsHsms { Role = SecsRole.Equipment, Mode = HsmsMode.Passive, LocalPort = 5000 };
/// eq.PrimaryReceived += (s, e) =&gt;
/// {
///     if (e.Message.Stream == 1 &amp;&amp; e.Message.Function == 1)
///         e.Reply(SecsMessage.ParseSml("S1F2 &lt;L &lt;A \"MDLN\"&gt; &lt;A \"1.0\"&gt;&gt;"));
/// };
/// eq.Start();
/// </code>
/// </example>
public class SecsHsms : IDisposable
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

    #region class : Link
    // TCP 연결 하나. 송신은 lock으로 한 번에 한 프레임씩.
    sealed class Link(Socket socket)
    {
        readonly object writeLock = new();
        public Socket Socket { get; } = socket;
        public volatile bool Selected;

        public void Write(byte[] frame)
        {
            lock (writeLock) Socket.Send(frame);
        }

        public void Close()
        {
            try { Socket.Shutdown(SocketShutdown.Both); } catch (Exception) { }
            Socket.Dispose();
        }
    }
    #endregion

    #region Properties
    int deviceId, t3 = 45000, t5 = 10000, t6 = 5000, t7 = 10000, t8 = 5000, linktestInterval = 60000, maxMessageSize = 16_777_216, state, remotePort = 5000, localPort = 5000;

    /// <summary>The role of this side (default: Host). Only Equipment automatically sends S9Fx and checks the device ID.</summary>
    public SecsRole Role { get; set; } = SecsRole.Host;
    /// <summary>Device ID, 0 to 32767 (default: 0). It is the session ID of the data messages sent, and Equipment sends S9F1 if the device ID of a received message differs.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Outside 0 to 32767.</exception>
    public int DeviceID { get => Volatile.Read(ref deviceId); set => Volatile.Write(ref deviceId, Check(value, 0, 32767, nameof(DeviceID))); }
    /// <summary>T3 reply timeout in ms (default: 45000). XML accepts only 1000 to 120000.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Less than 1.</exception>
    public int T3 { get => Volatile.Read(ref t3); set => Volatile.Write(ref t3, Check(value, 1, int.MaxValue, nameof(T3))); }

    /// <summary>Connection mode (default: Active). It is read on Start.</summary>
    public HsmsMode Mode { get; set; } = HsmsMode.Active;
    /// <summary>Address of the peer to connect to when Active (default: "127.0.0.1"). A host name is also accepted. It is read on every connection.</summary>
    public string RemoteIP { get; set; } = "127.0.0.1";
    /// <summary>Port of the peer to connect to when Active, 1 to 65535 (default: 5000). It is read on every connection.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Outside 1 to 65535.</exception>
    public int RemotePort { get => Volatile.Read(ref remotePort); set => Volatile.Write(ref remotePort, Check(value, 1, 65535, nameof(RemotePort))); }
    /// <summary>Local IP on which to wait for a connection when Passive (default: "0.0.0.0" = all interfaces). It is read on Start.</summary>
    public string LocalIP { get; set; } = "0.0.0.0";
    /// <summary>Local port on which to wait for a connection when Passive, 0 to 65535 (default: 5000; 0 = chosen by the OS). It is read on Start.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Outside 0 to 65535.</exception>
    public int LocalPort { get => Volatile.Read(ref localPort); set => Volatile.Write(ref localPort, Check(value, 0, 65535, nameof(LocalPort))); }
    /// <summary>T5 connect separation timeout in ms (default: 10000). The time Active waits before reconnecting after the connection is lost; it is also the time limit of a single TCP connection attempt.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Less than 1.</exception>
    public int T5 { get => Volatile.Read(ref t5); set => Volatile.Write(ref t5, Check(value, 1, int.MaxValue, nameof(T5))); }
    /// <summary>T6 control transaction timeout in ms (default: 5000). If the reply to Select or Linktest is not received within this time, the connection is closed.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Less than 1.</exception>
    public int T6 { get => Volatile.Read(ref t6); set => Volatile.Write(ref t6, Check(value, 1, int.MaxValue, nameof(T6))); }
    /// <summary>T7 not-selected timeout in ms (default: 10000). If the connection does not become Selected within this time after connecting, it is closed.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Less than 1.</exception>
    public int T7 { get => Volatile.Read(ref t7); set => Volatile.Write(ref t7, Check(value, 1, int.MaxValue, nameof(T7))); }
    /// <summary>T8 inter-character timeout in ms (default: 5000). If the gap between bytes while receiving one message exceeds this time, the connection is closed. It is also the time limit for sending.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Less than 1.</exception>
    public int T8 { get => Volatile.Read(ref t8); set => Volatile.Write(ref t8, Check(value, 1, int.MaxValue, nameof(T8))); }
    /// <summary>Interval at which Linktest is sent in ms (default: 60000; 0 = not sent). The peer's Linktest is always answered.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Less than 0.</exception>
    public int LinktestInterval { get => Volatile.Read(ref linktestInterval); set => Volatile.Write(ref linktestInterval, Check(value, 0, int.MaxValue, nameof(LinktestInterval))); }
    /// <summary>Maximum size of a message to be received (10-byte header + body) in bytes, 1024 to 1073741824 (default: 16777216). A larger message is discarded (S9F11 for Equipment).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Out of range.</exception>
    public int MaxMessageSize { get => Volatile.Read(ref maxMessageSize); set => Volatile.Write(ref maxMessageSize, Check(value, 1024, 1_073_741_824, nameof(MaxMessageSize))); }
    /// <summary>Whether Active reconnects after T5 when the connection is lost (default: true). Passive keeps waiting while Start is in effect regardless of this value.</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>Gets the connection state.</summary>
    public HsmsState State => (HsmsState)Volatile.Read(ref state);
    /// <summary>Gets whether the state is Selected (data messages can be exchanged).</summary>
    public bool IsOpen => State == HsmsState.Selected;
    /// <summary>Gets whether it has been started.</summary>
    public bool IsStart { get; private set; }
    /// <summary>Gets whether the object has been disposed.</summary>
    public bool IsDisposed { get; private set; }
    /// <summary>Gets or sets a user-defined tag.</summary>
    public object? Tag { get; set; }

    // Passive가 실제로 기다리는 포트(LocalPort = 0이면 OS가 고른 포트). 테스트용
    internal int BoundPort { get; private set; }

    // 테스트 이음새: null이 아니면 Passive의 접속 받기(TcpListener.AcceptSocket) 대신 이 함수를 부른다
    internal Func<TcpListener, Socket>? AcceptHook { get; set; }

    // 테스트 이음새: 연결 스레드가 자기 소켓을 닫은 뒤, 공유 상태를 정리하기 직전에 부른다(지난 실행의 늦은 정리 경합 재현용)
    internal Action? LinkEndHook { get; set; }

    static int Check(int value, int min, int max, string name)
        => value < min || value > max ? throw new ArgumentOutOfRangeException(name, value, $"{name}는 {min}~{max} 입니다.") : value;
    #endregion

    #region Member Variable
    const int PollMs = 20;
    const int StopWait = 3000;
    readonly object lifeLock = new();
    readonly SecsSession session;
    CancellationTokenSource? cts;
    Thread? thread;
    TcpListener? listener;
    Link? current;
    Thread? linkThread;
    int sessionGen;         // lifeLock으로 보호: 이번 Start에서 session.Start가 돌려준 실행 번호
    #endregion

    #region Event
    /// <summary>Raised on the dispatcher thread when the peer sends a primary message. If the W-bit is set, reply inside the handler with <see cref="SecsPrimaryEventArgs.Reply"/> (otherwise SxF0 is sent).</summary>
    /// <remarks>Calling <see cref="Send"/> inside the handler does not deadlock. However, there is only one dispatcher thread (one per connection), so while a synchronous <see cref="Send"/> inside the handler waits for its reply (up to T3), the peer's other primary messages are not delivered. If both sides do this in a handler right after connecting, they can wait for each other until T3, so in the connection sequence only start sending with <see cref="SendAsync"/> and handle the replies afterward (await or ContinueWith).</remarks>
    public event EventHandler<SecsPrimaryEventArgs>? PrimaryReceived;
    /// <summary>A diagnostic notification such as an unmatched reply, a malformed frame, a received Reject, or a disconnect caused by a timer timeout. It does not affect operation.</summary>
    /// <remarks>Raised in order on the same dispatcher thread as <see cref="PrimaryReceived"/>. Calling a synchronous <see cref="Send"/> inside the handler does not deadlock, but delivery of the peer's primary messages stops while it waits for the reply (up to T3) (see <see cref="PrimaryReceived"/>). <see cref="SendAsync"/> is recommended for the connection sequence. Notifications that could not yet be raised at <see cref="Stop"/> are raised by the thread that called Stop. Notifications before start or after stop are discarded.</remarks>
    public event EventHandler<ProtocolErrorEventArgs>? ProtocolError;
    /// <summary>Raised when the state becomes Selected (<see cref="Send"/> is possible from this point).</summary>
    /// <remarks>Raised on the same dispatcher thread as <see cref="PrimaryReceived"/>, before the first primary message of that connection. Calling a synchronous <see cref="Send"/> inside the handler does not deadlock, but delivery of the peer's primary messages stops while it waits for the reply (up to T3) (see <see cref="PrimaryReceived"/>). <see cref="SendAsync"/> is recommended for the connection sequence.</remarks>
    public event EventHandler? DeviceOpened;
    /// <summary>Raised when the connection is lost while Selected.</summary>
    /// <remarks>Raised on the same dispatcher thread as <see cref="PrimaryReceived"/>, after the primary messages of that connection. The pending transactions of that connection end with <see cref="SecsDisconnectedException"/>, which the application may see before or after this event. If it could not yet be raised at <see cref="Stop"/>, it is raised by the thread that called Stop. Calling a synchronous <see cref="Send"/> inside the handler does not deadlock, but delivery of the peer's primary messages stops while it waits for the reply (up to T3) (see <see cref="PrimaryReceived"/>). <see cref="SendAsync"/> is recommended for the connection sequence.</remarks>
    public event EventHandler? DeviceClosed;
    #endregion

    /// <summary>Creates the HSMS communication object. Call <see cref="Start"/> after configuring it.</summary>
    public SecsHsms()
    {
        session = new SecsSession(() => Role, () => DeviceID, () => T3, m => HsmsFrame.DataHeader(m).ToArray(),
            e => Raise(PrimaryReceived, e), Error, nameof(SecsHsms));
    }

    #region Start / Stop
    /// <summary>Starts communication. Active starts connecting and Passive waits for a connection. Does nothing if it is already started.</summary>
    /// <exception cref="ObjectDisposedException">The object has been disposed.</exception>
    /// <exception cref="FormatException">Passive, and <see cref="LocalIP"/> is not an IP address.</exception>
    /// <exception cref="SocketException">Passive, and the port could not be opened (already in use, etc.).</exception>
    public void Start()
    {
        lock (lifeLock)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (IsStart) return;
            TcpListener? l = null;
            if (Mode == HsmsMode.Passive)
            {
                l = new TcpListener(IPAddress.Parse(LocalIP), LocalPort);
                l.Start();
                BoundPort = ((IPEndPoint)l.LocalEndpoint).Port;
            }
            cts?.Dispose();
            cts = new CancellationTokenSource();
            var token = cts.Token;
            listener = l;
            // 지난 실행의 연결 스레드가 아직 정리하지 못했을 수 있다(Stop의 Join보다 오래 살아남음): 공유 상태는 새 실행이 여기서 넘겨받고, 지난 실행은 건드리지 않는다
            current = null;
            linkThread = null;
            SetState(HsmsState.NotConnected);
            session.Disconnected();
            int gen = sessionGen = session.Start();
            IsStart = true;
            thread = l != null
                ? new Thread(() => AcceptLoop(l, token, gen)) { IsBackground = true, Name = nameof(SecsHsms) + ".Accept" }
                : new Thread(() => ActiveLoop(token, gen)) { IsBackground = true, Name = nameof(SecsHsms) };
            thread.Start();
        }
    }

    /// <summary>Stops communication. If Selected, it sends Separate and then closes the connection. Waits up to 3 seconds for the threads to end. May be called multiple times.</summary>
    /// <remarks>A <see cref="Send"/> that was waiting for a reply ends with <see cref="SecsDisconnectedException"/>, and primary messages not yet delivered are discarded. May be called from inside an event handler.</remarks>
    public void Stop()
    {
        CancellationTokenSource? c;
        Thread? t, lt;
        TcpListener? l;
        Link? link;
        int gen;
        lock (lifeLock)
        {
            c = cts; cts = null;
            t = thread; thread = null;
            l = listener; listener = null;
            link = current;
            lt = linkThread;
            IsStart = false;
            gen = sessionGen;   // 이 실행의 번호: 기다리는 사이 다시 Start돼도 새 실행의 세션을 멈추지 않게
        }
        // 수신 스레드가 취소를 보고 연결을 먼저 닫지 않도록, 취소하기 전에 Separate를 보낸다
        if (link is { Selected: true })
        {
            try { link.Write(HsmsFrame.Control(HsmsSType.SeparateReq, session.NextSystemBytes())); } catch (Exception) { }
        }
        c?.Cancel();
        try { l?.Stop(); } catch (Exception) { }
        link?.Close();
        var until = Environment.TickCount64 + StopWait;
        if (t != null && t != Thread.CurrentThread) t.Join(StopWait);
        if (lt != null && lt != Thread.CurrentThread) lt.Join((int)Math.Max(0, until - Environment.TickCount64));
        session.Stop((int)Math.Max(0, until - Environment.TickCount64), gen);
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
    /// <remarks>The system bytes and device ID are filled in when sending, and the object passed in is not modified. On the UI thread, use <see cref="SendAsync"/> (it blocks for up to T3). A <see cref="SecsDisconnectedException"/> may be seen before or after that connection's <see cref="DeviceClosed"/>. The wait does not depend on the thread pool, and calling it inside an event handler does not deadlock.</remarks>
    /// <param name="primary">The primary message (odd function).</param>
    /// <returns>The reply message. null after sending if the W-bit is not set.</returns>
    /// <exception cref="ArgumentNullException">primary is null.</exception>
    /// <exception cref="ArgumentException">The function is even.</exception>
    /// <exception cref="InvalidOperationException">The state is not Selected.</exception>
    /// <exception cref="SecsTimeoutException">No reply arrived within T3 (the connection is kept).</exception>
    /// <exception cref="SecsAbortException">The peer replied with SxF0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The connection was lost while sending or waiting.</exception>
    public SecsMessage? Send(SecsMessage primary) => session.Send(primary);

    /// <summary>Sends a primary message and, if the W-bit is set, returns a Task that waits for the reply. The rules and exceptions are the same as <see cref="Send"/>.</summary>
    /// <remarks>The continuation runs on the calling thread when the Task completes synchronously (for example without the W-bit), otherwise on the thread pool. It never runs on the I/O thread, and runs on the dispatcher thread only if SendAsync was called there. A <see cref="SecsDisconnectedException"/> may be seen before or after that connection's <see cref="DeviceClosed"/>.</remarks>
    /// <param name="primary">The primary message (odd function).</param>
    /// <param name="cancellationToken">Cancels the wait (a message that has already been sent cannot be taken back).</param>
    /// <returns>The reply message. null if the W-bit is not set.</returns>
    /// <exception cref="ArgumentNullException">primary is null.</exception>
    /// <exception cref="ArgumentException">The function is even.</exception>
    /// <exception cref="InvalidOperationException">The state is not Selected.</exception>
    /// <exception cref="OperationCanceledException">The wait was canceled.</exception>
    public Task<SecsMessage?> SendAsync(SecsMessage primary, CancellationToken cancellationToken = default)
        => session.SendAsync(primary, cancellationToken);

    /// <summary>Sends a primary message and reports its result to <paramref name="completed"/> exactly once, in wire order on the dispatcher thread. Use it instead of <see cref="SendAsync"/> when a reply must be handled before the next primary message the peer sent after that reply.</summary>
    /// <remarks>
    /// <para><b>Thread and order</b>: <paramref name="completed"/> is normally called on the dispatcher thread (the same thread as <see cref="PrimaryReceived"/>), in the order the results arrived on the wire: when the reply has been received, it is called before the <see cref="PrimaryReceived"/> of any primary message received after that reply. A message without the W-bit completes with <see cref="SecsSendResult.Reply"/> = null once sending has finished (or failed).
    /// If the dispatcher is stopped (during or after <see cref="Stop"/>), it is called at once on the thread that completed the transaction (for example the thread that called Stop).
    /// A failure caused by disconnection (<see cref="SecsDisconnectedException"/>) may be reported before or after that connection's <see cref="DeviceClosed"/>; to tell which connection a result belongs to, keep a connection count that you increase on each <see cref="DeviceOpened"/> and record it when sending.</para>
    /// <para><b>Exceptions at the call</b>: argument errors and "no connection" are thrown by the call, as with <see cref="Send"/>, and then <paramref name="completed"/> is never called. Every other outcome is passed to <paramref name="completed"/>: the reply in <see cref="SecsSendResult.Reply"/>, and a failure (SxF0, S9Fx, T3, disconnection, send failure) in <see cref="SecsSendResult.Exception"/>, with the same exception types as <see cref="Send"/>.</para>
    /// <para><b>Callback rules</b>: the same as a <see cref="PrimaryReceived"/> handler. A synchronous <see cref="Send"/> inside the callback does not deadlock, but events are delayed while it waits for its reply (up to T3).
    /// Waiting inside the callback or an event handler for the completion of another SendOrdered deadlocks, because that completion is queued behind it on the same dispatcher.
    /// Exceptions thrown by the callback are ignored (the dispatcher keeps running). There is no cancellation token; T3 bounds the wait.</para>
    /// </remarks>
    /// <param name="primary">The primary message (odd function). The system bytes and device ID are filled in when sending, and the object passed in is not modified.</param>
    /// <param name="completed">Receives the result exactly once.</param>
    /// <exception cref="ArgumentNullException">primary or completed is null.</exception>
    /// <exception cref="ArgumentException">The function is even.</exception>
    /// <exception cref="InvalidOperationException">The state is not Selected.</exception>
    public void SendOrdered(SecsMessage primary, Action<SecsSendResult> completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        session.SendOrdered(primary, (wire, t) => SecsSendResult.Invoke(completed, wire ?? primary, t));
    }

    // GEM 내부용: 순서 있는 완료로 보낸다(완료는 디스패처에서 와이어 순서로, SecsSession.SendOrdered)
    internal void SendOrdered(SecsMessage primary, Action<Task<SecsMessage?>> completed) => session.SendOrdered(primary, completed);

    // GEM 내부용: 디스패처 대기열에 넣는다(멈춰 있으면 버림)
    internal void Post(Action action) => session.Post(action);

    internal bool IsDispatcherThread => session.IsDispatcherThread;
    #endregion

    #region Config / Xml
    /// <summary>Creates a configuration object from the current communication settings.</summary>
    /// <returns>The configuration object.</returns>
    public SecsHsmsConfig ToConfig() => new()
    {
        Role = Role,
        DeviceID = DeviceID,
        T3 = T3,
        Mode = Mode,
        RemoteIP = RemoteIP,
        RemotePort = RemotePort,
        LocalIP = LocalIP,
        LocalPort = LocalPort,
        T5 = T5,
        T6 = T6,
        T7 = T7,
        T8 = T8,
        LinktestInterval = LinktestInterval,
        MaxMessageSize = MaxMessageSize,
        AutoReconnect = AutoReconnect,
    };

    /// <summary>Applies the communication settings of a configuration object.</summary>
    /// <remarks>Everything is validated first and then applied, so if any value is invalid, nothing is changed. The timer ranges are checked with the XML rules (e.g., T3 1000 to 120000).</remarks>
    /// <param name="config">The configuration to apply.</param>
    /// <exception cref="InvalidOperationException">Called while communicating (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">A value is invalid (the message includes the element and attribute names).</exception>
    public void ApplyConfig(SecsHsmsConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (IsStart) throw new InvalidOperationException("통신 중에는 설정을 바꿀 수 없습니다. Stop() 후 호출하세요.");
        config.Validate();
        Role = config.Role;
        DeviceID = config.DeviceID;
        T3 = config.T3;
        Mode = config.Mode;
        RemoteIP = config.RemoteIP;
        RemotePort = config.RemotePort;
        LocalIP = config.LocalIP;
        LocalPort = config.LocalPort;
        T5 = config.T5;
        T6 = config.T6;
        T7 = config.T7;
        T8 = config.T8;
        LinktestInterval = config.LinktestInterval;
        MaxMessageSize = config.MaxMessageSize;
        AutoReconnect = config.AutoReconnect;
    }

    /// <summary>Creates an XML element from the current communication settings. Messages to send and event handlers are not saved.</summary>
    /// <returns>The &lt;SecsHsms&gt; element.</returns>
    public XElement ToXml() => XmlConfig.Serialize(ToConfig());

    /// <summary>Loads the communication settings from an XML element.</summary>
    /// <remarks>
    /// <para>Omitted attributes use their default values. An unknown attribute or element is an error (to catch typos).</para>
    /// <para>Everything is validated first and then applied, so if any value is invalid, nothing is changed.</para>
    /// <para>You can check the format by generating the XSD with <see cref="ConfigSchema.Get{T}"/> for <see cref="SecsHsmsConfig"/>.</para>
    /// </remarks>
    /// <param name="element">The &lt;SecsHsms&gt; element.</param>
    /// <exception cref="InvalidOperationException">Called while communicating (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">The root name differs, an unknown attribute or element is present, or a value is invalid.</exception>
    public void FromXml(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (IsStart) throw new InvalidOperationException("통신 중에는 설정을 불러올 수 없습니다. Stop() 후 호출하세요.");
        ApplyConfig(XmlConfig.Deserialize<SecsHsmsConfig>(element));
    }

    /// <summary>Saves the communication settings to an XML file.</summary>
    /// <param name="path">File path.</param>
    public void SaveXml(string path) => ToXml().Save(path);

    /// <summary>Loads the communication settings from an XML file. The rules are the same as <see cref="FromXml"/>.</summary>
    /// <param name="path">File path.</param>
    public void LoadXml(string path) => FromXml(XElement.Load(path));
    #endregion

    #region Active / Passive
    void ActiveLoop(CancellationToken token, int runGen)
    {
        string? reason = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var s = TryConnect(token);
                if (s != null)
                {
                    var link = new Link(s);
                    lock (lifeLock)
                    {
                        if (token.IsCancellationRequested) { link.Close(); break; }
                        current = link;
                    }
                    RunLink(link, active: true, token, runGen);
                }
                if (!AutoReconnect || token.IsCancellationRequested) break;
                token.WaitHandle.WaitOne(T5);
            }
        }
        catch (Exception ex) { if (!token.IsCancellationRequested) reason = $"접속 루프가 예기치 않은 오류로 끝났습니다: {ex.Message}"; }
        finally { EndLoop(reason); }
    }

    Socket? TryConnect(CancellationToken token)
    {
        var s = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            var connect = s.ConnectAsync(RemoteIP, RemotePort, token).AsTask();
            if (connect.Wait(T5) && s.Connected) return s;
            if (!token.IsCancellationRequested) Error($"{RemoteIP}:{RemotePort}에 T5({T5}ms) 안에 접속하지 못했습니다.");
        }
        catch (Exception ex) when (ex is AggregateException or SocketException or OperationCanceledException)
        {
            if (!token.IsCancellationRequested) Error($"{RemoteIP}:{RemotePort}에 접속하지 못했습니다: {(ex as AggregateException)?.InnerException?.Message ?? ex.Message}");
        }
        s.Dispose();
        return null;
    }

    void AcceptLoop(TcpListener l, CancellationToken token, int runGen)
    {
        string? reason = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                Socket s;
                try { s = AcceptHook != null ? AcceptHook(l) : l.AcceptSocket(); }
                catch (SocketException ex) when (!token.IsCancellationRequested)
                {
                    // 받기 전에 상대가 리셋한 경우 등 일시적 오류: 알리고 잠깐 쉰 뒤 계속 기다린다. 취소되면 루프가 끝난다
                    Error($"접속을 받지 못했습니다(계속 기다립니다): {ex.Message}");
                    token.WaitHandle.WaitOne(100);
                    continue;
                }
                lock (lifeLock)
                {
                    if (token.IsCancellationRequested) { s.Dispose(); break; }
                    if (current == null)
                    {
                        var link = new Link(s);
                        current = link;
                        linkThread = new Thread(() => RunLink(link, active: false, token, runGen)) { IsBackground = true, Name = nameof(SecsHsms) + ".Link" };
                        linkThread.Start();
                        continue;
                    }
                }
                // 이미 연결이 있으면: 받아서 Select.req에 상태 1로 답하고 닫는다(E37 권장)
                new Thread(() => RejectSecond(s, token)) { IsBackground = true, Name = nameof(SecsHsms) + ".Busy" }.Start();
            }
        }
        catch (Exception ex) { if (!token.IsCancellationRequested) reason = $"접속 대기가 예기치 않은 오류로 끝났습니다: {ex.Message}"; }
        finally { EndLoop(reason); }
    }

    // 두 번째 연결: T7 동안 Select.req를 기다려 상태 1로 답하고 닫는다
    void RejectSecond(Socket s, CancellationToken token)
    {
        var reader = new HsmsFrameReader(() => MaxMessageSize);
        var frames = new List<HsmsFrameReader.Frame>();
        var buf = new byte[4096];
        long deadline = Environment.TickCount64 + T7;
        try
        {
            while (!token.IsCancellationRequested && Environment.TickCount64 < deadline)
            {
                if (!s.Poll(PollMs * 1000, SelectMode.SelectRead)) continue;
                int n = s.Receive(buf);
                if (n == 0) break;
                reader.Feed(buf.AsSpan(0, n), frames);
                var select = frames.FirstOrDefault(f => f.Header.SType == (byte)HsmsSType.SelectReq);
                if (select.Header.SType == (byte)HsmsSType.SelectReq)
                {
                    s.Send(HsmsFrame.Control(HsmsSType.SelectRsp, select.Header.SystemBytes, select.Header.SessionId, byte3: HsmsFrame.SelectAlreadyActive));
                    break;
                }
                frames.Clear();
            }
        }
        catch (Exception) { }
        Error("이미 연결이 있어 두 번째 연결을 닫았습니다(Select 상태 1).");
        try { s.Shutdown(SocketShutdown.Both); } catch (Exception) { }
        s.Dispose();
    }

    // 루프가 스스로 끝났을 때(AutoReconnect = false, 예기치 않은 오류) Stop처럼 정리한다: 리스너·지금 연결을 닫고, reason이 있으면 ProtocolError.
    // Stop이 이미 맡았으면(thread가 바뀜) 아무것도 하지 않고, 그 사이 다시 Start됐어도 새 실행의 세션은 멈추지 않는다.
    void EndLoop(string? reason)
    {
        CancellationTokenSource? c;
        TcpListener? l;
        Link? link;
        Thread? lt;
        lock (lifeLock)
        {
            if (thread != Thread.CurrentThread) return;
            c = cts;
            l = listener;
            link = current;
            lt = linkThread;
        }
        // IsStart를 내리기 전에 정리한다(그래야 Start가 끼어들어도 이 실행의 리스너·연결과 섞이지 않음)
        try { c?.Cancel(); } catch (ObjectDisposedException) { }
        try { l?.Stop(); } catch (Exception) { }
        link?.Close();
        if (lt != null && lt != Thread.CurrentThread) lt.Join(StopWait);   // 수신 스레드의 DeviceClosed가 세션을 멈추기 전에 들어가게
        if (reason != null) Error(reason);
        int gen;
        lock (lifeLock)
        {
            if (thread != Thread.CurrentThread) return;   // 정리하는 사이 Stop이 불림: 남은 일은 Stop이 한다
            IsStart = false;
            thread = null;
            if (listener == l) listener = null;
            if (current == link) { current = null; linkThread = null; }
            gen = sessionGen;
        }
        session.Stop(0, gen);
    }
    #endregion

    #region Link
    // 연결 하나의 수신 루프: Select·Linktest·T6/T7/T8·T3 검사·프레임 처리
    // runGen = 이 실행의 번호(Start의 sessionGen). 공유 상태(state·세션 연결·DeviceOpened/Closed)는 "이 실행이 아직 지금 실행"
    // (lifeLock 안에서 Stop 전·다시 Start 전)일 때만 바꾼다. 지난 실행의 늦은 정리는 자기 연결(소켓)만 닫는다
    void RunLink(Link link, bool active, CancellationToken token, int runGen)
    {
        var s = link.Socket;
        var reader = new HsmsFrameReader(() => MaxMessageSize);
        var frames = new List<HsmsFrameReader.Frame>();
        var buf = new byte[65536];
        long now = Environment.TickCount64, lastByte = now, t7Deadline = now + T7, selectDeadline = 0, linktestDeadline = 0, nextLinktest = 0;
        uint? selectSys = null, linktestSys = null;
        bool opened = false;
        try
        {
            lock (lifeLock)
            {
                if (!Current()) return;   // finally가 연결만 닫는다
                SetState(HsmsState.NotSelected);
            }
            s.NoDelay = true;
            s.SendTimeout = T8;
            if (active)
            {
                selectSys = session.NextSystemBytes();
                selectDeadline = now + T6;
                link.Write(HsmsFrame.Control(HsmsSType.SelectReq, selectSys.Value));
            }
            while (!token.IsCancellationRequested)
            {
                now = Environment.TickCount64;
                if (!link.Selected && now > t7Deadline) throw new TimeoutException($"T7({T7}ms) 안에 Selected가 되지 않아 끊습니다.");
                if (selectSys != null && now > selectDeadline) throw new TimeoutException($"T6({T6}ms) 안에 Select 응답이 없어 끊습니다.");
                if (link.Selected)
                {
                    if (linktestSys != null && now > linktestDeadline) throw new TimeoutException($"T6({T6}ms) 안에 Linktest 응답이 없어 끊습니다.");
                    int interval = LinktestInterval;
                    if (linktestSys == null && interval > 0 && now >= nextLinktest)
                    {
                        linktestSys = session.NextSystemBytes();
                        linktestDeadline = now + T6;
                        link.Write(HsmsFrame.Control(HsmsSType.LinktestReq, linktestSys.Value));
                    }
                    session.CheckTimeouts();
                }
                if (reader.InFrame && now - lastByte > T8) throw new TimeoutException($"메시지를 받는 중 T8({T8}ms) 동안 바이트가 오지 않아 끊습니다.");

                if (!s.Poll(PollMs * 1000, SelectMode.SelectRead)) continue;
                int n = s.Receive(buf);
                if (n == 0) throw new IOException("상대가 연결을 닫았습니다.");
                lastByte = Environment.TickCount64;
                reader.Feed(buf.AsSpan(0, n), frames);
                foreach (var f in frames)
                {
                    var h = f.Header;
                    if (f.TooLongLength > 0)
                    {
                        if (link.Selected && h.SType == (byte)HsmsSType.Data) session.TooLong(h.ToArray(), f.TooLongLength);
                        else Error($"최대 크기를 넘는 메시지({f.TooLongLength}바이트, SType {h.SType})를 버렸습니다.");
                        continue;
                    }
                    if (h.PType != 0) { Reject(link, h, HsmsFrame.RejectPType, h.PType); continue; }
                    switch ((HsmsSType)h.SType)
                    {
                        case HsmsSType.Data:
                            if (!link.Selected) { Reject(link, h, HsmsFrame.RejectNotSelected, h.SType); break; }
                            SecsMessage msg;
                            try { msg = HsmsFrame.ToMessage(h, f.Body); }
                            catch (FormatException ex) { session.DecodeFailed(h.ToArray(), ex.Message); break; }
                            session.Received(msg, h.ToArray());
                            break;
                        case HsmsSType.SelectReq:
                            if (link.Selected)
                            {
                                link.Write(HsmsFrame.Control(HsmsSType.SelectRsp, h.SystemBytes, h.SessionId, byte3: HsmsFrame.SelectAlreadyActive));
                                break;
                            }
                            link.Write(HsmsFrame.Control(HsmsSType.SelectRsp, h.SystemBytes, h.SessionId, byte3: HsmsFrame.SelectOk));
                            Open();
                            break;
                        case HsmsSType.SelectRsp:
                            if (selectSys != h.SystemBytes) { Reject(link, h, HsmsFrame.RejectNoTransaction, h.SType); break; }
                            selectSys = null;
                            if (h.Byte3 != HsmsFrame.SelectOk) throw new IOException($"상대가 Select를 거부했습니다(상태 {h.Byte3}).");
                            if (!link.Selected) Open();
                            break;
                        case HsmsSType.DeselectReq:
                            link.Write(HsmsFrame.Control(HsmsSType.DeselectRsp, h.SystemBytes, h.SessionId, byte3: 0));
                            throw new IOException("상대가 Deselect를 보내 응답 후 끊습니다.");
                        case HsmsSType.LinktestReq:
                            link.Write(HsmsFrame.Control(HsmsSType.LinktestRsp, h.SystemBytes));
                            break;
                        case HsmsSType.LinktestRsp:
                            if (linktestSys != h.SystemBytes) { Reject(link, h, HsmsFrame.RejectNoTransaction, h.SType); break; }
                            linktestSys = null;
                            nextLinktest = Environment.TickCount64 + LinktestInterval;
                            break;
                        case HsmsSType.RejectReq:
                            Error($"상대가 Reject를 보냈습니다(SType {h.Byte2}, 이유 {h.Byte3}).");
                            break;
                        case HsmsSType.SeparateReq:
                            throw new IOException("상대가 Separate를 보내 끊습니다.");
                        default:
                            // DeselectRsp(보낸 적 없음)는 열린 트랜잭션 없음, 그 밖의 SType은 지원하지 않음
                            Reject(link, h, h.SType == (byte)HsmsSType.DeselectRsp ? HsmsFrame.RejectNoTransaction : HsmsFrame.RejectSType, h.SType);
                            break;
                    }
                }
                frames.Clear();
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or TimeoutException or FormatException)
        {
            LinkError(ex is FormatException ? $"깨진 프레임을 받아 끊습니다: {ex.Message}" : ex.Message);
        }
        catch (Exception ex) { LinkError($"연결 처리 중 예기치 않은 오류로 끊습니다: {ex.Message}"); }
        finally
        {
            link.Selected = false;
            link.Close();
            LinkEndHook?.Invoke();
            lock (lifeLock)
            {
                if (current == link) { current = null; linkThread = null; }
                if (sessionGen == runGen)   // 그 사이 다시 Start됐으면 새 실행의 상태·세션·이벤트는 건드리지 않는다
                {
                    if (opened) session.Disconnected();
                    SetState(HsmsState.NotConnected);
                    if (opened) session.Post(() => Raise(DeviceClosed));
                }
            }
        }

        // lifeLock 안에서: 이 실행이 Stop되지 않았고 다시 Start되지도 않았는지
        bool Current() => sessionGen == runGen && !token.IsCancellationRequested && IsStart;

        // 끊긴 이유 알림: 지금 실행일 때만(멈춘·지난 실행의 알림이 새 실행의 ProtocolError로 가지 않게)
        void LinkError(string reason)
        {
            lock (lifeLock) { if (Current()) Error(reason); }
        }

        void Open()
        {
            lock (lifeLock)
            {
                if (!Current()) throw new IOException("멈춘 실행의 연결이라 열지 않고 끊습니다.");
                link.Selected = true;
                opened = true;
                nextLinktest = Environment.TickCount64 + LinktestInterval;
                SetState(HsmsState.Selected);
                session.Connected((wire, _) => Transmit(link, wire));
                session.Post(() => Raise(DeviceOpened));
            }
        }
    }

    Task Transmit(Link link, SecsMessage wire)
    {
        try
        {
            link.Write(HsmsFrame.Data(wire));
            return Task.CompletedTask;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException)
        {
            link.Close();   // 수신 루프가 끊김을 알아채고 정리한다
            return Task.FromException(new SecsDisconnectedException($"S{wire.Stream}F{wire.Function}을(를) 보내지 못했습니다(연결 끊김): {ex.Message}"));
        }
    }

    void Reject(Link link, HsmsHeader h, byte reason, byte byte2)
    {
        link.Write(HsmsFrame.Control(HsmsSType.RejectReq, h.SystemBytes, byte2: byte2, byte3: reason));
        Error($"SType {h.SType}(PType {h.PType}) 메시지를 Reject 했습니다(이유 {reason}).");
    }

    void SetState(HsmsState s) => Volatile.Write(ref state, (int)s);
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
