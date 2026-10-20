using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Devinno.Communication.Internal;

namespace Devinno.Communication.TextComm.TCP;

/// <summary>
/// Slave (server) that accepts TCP connections from several clients and answers TextComm requests through the <see cref="MessageRequest"/> event.
/// </summary>
/// <remarks>
/// <para>The frame format is the same as <c>TextCommTCPMaster</c> (STX | slave | command | message | sum | ETX, with DLE escaping).
/// Handles both several frames arriving in one receive and a single frame split across several receives.</para>
/// <para><see cref="MessageRequest"/> is raised for every request. If the handler sets <see cref="MessageRequestEventArgs.ResponseMessage"/>, the response is sent to that client.
/// If null (default, not set), no response is sent; if an empty string (""), a frame with no message (slave, command, sum) is sent as the response. The slave and command of the response equal those of the request.
/// To send no response, use null, not an empty string.</para>
/// <para>Requests are not filtered by slave address. The event is raised for every request, so check <see cref="MessageRequestEventArgs.Slave"/> to decide whether to respond.</para>
/// <para>A frame with a wrong sum or a malformed format, or a frame longer than 8192 bytes (STX to ETX), is discarded without raising an event and the connection is kept. If the handler throws, no response is sent.</para>
/// <para>Connection handling runs asynchronously, and events are raised on thread-pool threads. Even when requests from several clients arrive at the same time, <see cref="MessageRequest"/> is invoked one at a time.
/// To update the UI, marshal asynchronously, as with BeginInvoke (WinForms) or Dispatcher.BeginInvoke (WPF).</para>
/// <para>Disconnected clients (for example, after a power loss) are cleaned up within about 20 seconds by TCP keepalive.</para>
/// </remarks>
/// <example>
/// <code>
/// using Devinno.Communication.TextComm.TCP;
///
/// var slave = new TextCommTCPSlave { LocalPort = 7897, MaxClients = 4 };
/// slave.MessageRequest += (s, e) =&gt;
/// {
///     if (e.Command == 0x41) e.ResponseMessage = "OK " + e.RequestMessage;   // no response for any other command
/// };
/// slave.ClientConnected += (s, e) =&gt; Console.WriteLine($"Connected: {e.RemoteEndPoint}");
/// slave.Start();
/// </code>
/// </example>
public class TextCommTCPSlave : IDisposable
{
    #region class : EventArgs
    /// <summary>Event arguments for a request received from the master. Setting <see cref="ResponseMessage"/> sends a response.</summary>
    /// <param name="slave">Request slave, 0 to 255.</param>
    /// <param name="command">Request command, 0 to 255.</param>
    /// <param name="requestMessage">Request message (may be an empty string).</param>
    public class MessageRequestEventArgs(int slave, int command, string requestMessage) : EventArgs
    {
        /// <summary>Request slave, 0 to 255.</summary>
        public int Slave { get; } = slave;
        /// <summary>Request command, 0 to 255.</summary>
        public int Command { get; } = command;
        /// <summary>Request message (may be an empty string).</summary>
        public string RequestMessage { get; } = requestMessage;
        /// <summary>Response message. If null (default), no response is sent; if "", a response with no message is sent.</summary>
        public string? ResponseMessage { get; set; }
    }

    /// <summary>Event arguments for client connect/disconnect events.</summary>
    /// <param name="remoteEndPoint">Client address (for example, "192.168.0.5:51234").</param>
    public class ClientEventArgs(string remoteEndPoint) : EventArgs
    {
        /// <summary>Client address (for example, "192.168.0.5:51234").</summary>
        public string RemoteEndPoint { get; } = remoteEndPoint;
    }
    #endregion

    #region Properties
    /// <summary>Listening port (default: 7897).</summary>
    public int LocalPort { get; set; } = 7897;
    /// <summary>Maximum number of simultaneous connections (default: 10). Connections beyond this are closed immediately.</summary>
    public int MaxClients { get; set; } = 10;
    /// <summary>Message string encoding (default: ASCII). Used to read requests and build responses.</summary>
    public Encoding MessageEncoding { get; set; } = Encoding.ASCII;

    /// <summary>Gets whether the server is running.</summary>
    public bool IsStart { get; private set; }
    /// <summary>Gets whether the object has been disposed.</summary>
    public bool IsDisposed { get; private set; }
    /// <summary>Gets the number of currently connected clients.</summary>
    public int ClientCount { get { lock (clientLock) return clients.Count; } }
    /// <summary>Gets or sets a user-defined tag.</summary>
    public object? Tag { get; set; }

    internal int BoundPort { get; private set; }

    // 접속을 받을 로컬 주소. 기본은 모든 인터페이스. 테스트는 루프백으로 바꿔 Windows 방화벽 확인 창을 피한다.
    internal static IPAddress BindAddress { get; set; } = IPAddress.Any;
    #endregion

    #region Member Variable
    readonly object lifeLock = new();
    readonly object clientLock = new();
    readonly object requestLock = new();
    readonly List<TcpClient> clients = [];
    TcpListener? listener;
    CancellationTokenSource? cts;
    Task? acceptTask;
    #endregion

    #region Event
    /// <summary>Raised when a request from the master is received. Setting <see cref="MessageRequestEventArgs.ResponseMessage"/> sends a response.</summary>
    public event EventHandler<MessageRequestEventArgs>? MessageRequest;
    /// <summary>Raised when a client connects.</summary>
    public event EventHandler<ClientEventArgs>? ClientConnected;
    /// <summary>Raised when a client connection is lost.</summary>
    public event EventHandler<ClientEventArgs>? ClientDisconnected;
    #endregion

    #region Start / Stop
    /// <summary>Binds to the port and starts accepting connections. Does nothing if it is already running.</summary>
    /// <exception cref="SocketException">The port is already in use by another program.</exception>
    public void Start()
    {
        lock (lifeLock)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (IsStart) return;
            var l = new TcpListener(BindAddress, LocalPort);
            l.Start();
            listener = l;
            BoundPort = ((IPEndPoint)l.LocalEndpoint).Port;
            cts = new CancellationTokenSource();
            IsStart = true;
            acceptTask = AcceptLoop(l, cts.Token);
        }
    }

    /// <summary>Stops listening and disconnects all clients.</summary>
    /// <remarks>Events that were already in progress may still be raised briefly right after Stop() returns.</remarks>
    public void Stop()
    {
        Task? t;
        CancellationTokenSource? c;
        lock (lifeLock)
        {
            if (!IsStart) return;
            IsStart = false;
            c = cts; cts = null;
            c?.Cancel();
            try { listener?.Stop(); } catch { }
            listener = null;
            t = acceptTask;
            acceptTask = null;
        }
        // 목록을 복사한 뒤 lock 밖에서 끊는다. Dispose 중 ServeClient의 finally가 같은 목록을 수정하기 때문
        TcpClient[] snapshot;
        lock (clientLock) snapshot = [.. clients];
        foreach (var cl in snapshot) cl.Dispose();
        try { t?.Wait(1000); } catch { }
        c?.Dispose();
    }

    /// <summary>Stops the server and releases its resources.</summary>
    public void Dispose()
    {
        if (IsDisposed) return;
        Stop();
        IsDisposed = true;
        GC.SuppressFinalize(this);
    }
    #endregion

    #region Config / Xml
    /// <summary>Builds a configuration object from the current settings.</summary>
    /// <returns>The configuration object.</returns>
    public TextCommTCPSlaveConfig ToConfig() => new()
    {
        LocalPort = LocalPort,
        MaxClients = MaxClients,
        MessageEncoding = MessageEncoding.WebName,
    };

    /// <summary>Applies the configuration object.</summary>
    /// <remarks>The whole input is validated before it is applied, so if any single value is invalid, nothing is changed.</remarks>
    /// <param name="config">The configuration to apply.</param>
    /// <exception cref="InvalidOperationException">Called while running (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">A value is invalid (the message includes the element and attribute names).</exception>
    public void ApplyConfig(TextCommTCPSlaveConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (IsStart) throw new InvalidOperationException("실행 중에는 설정을 바꿀 수 없습니다. Stop() 후 호출하세요.");
        var encoding = config.Validate();
        LocalPort = config.LocalPort;
        MaxClients = config.MaxClients;
        MessageEncoding = encoding;
    }

    /// <summary>Builds an XML element from the current settings.</summary>
    /// <returns>The &lt;TextCommTCPSlave&gt; element.</returns>
    public XElement ToXml() => XmlConfig.Serialize(ToConfig());

    /// <summary>Loads the settings from an XML element.</summary>
    /// <remarks>
    /// <para>Omitted attributes use their default values. An unknown attribute or element is an error (to catch typos).</para>
    /// <para>The whole input is validated before it is applied, so if any single value is invalid, nothing is changed.</para>
    /// <para>You can check the format as an XSD by passing <see cref="TextCommTCPSlaveConfig"/> to <see cref="ConfigSchema.Get{T}"/>.</para>
    /// </remarks>
    /// <param name="element">The &lt;TextCommTCPSlave&gt; element.</param>
    /// <exception cref="InvalidOperationException">Called while running (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">The root name differs, an unknown attribute or element is present, or a value is invalid.</exception>
    public void FromXml(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (IsStart) throw new InvalidOperationException("실행 중에는 설정을 불러올 수 없습니다. Stop() 후 호출하세요.");
        ApplyConfig(XmlConfig.Deserialize<TextCommTCPSlaveConfig>(element));
    }

    /// <summary>Saves the settings to an XML file.</summary>
    /// <param name="path">File path.</param>
    public void SaveXml(string path) => ToXml().Save(path);

    /// <summary>Loads the settings from an XML file. The rules are the same as <see cref="FromXml"/>.</summary>
    /// <param name="path">File path.</param>
    public void LoadXml(string path) => FromXml(XElement.Load(path));
    #endregion

    #region Loop
    async Task AcceptLoop(TcpListener l, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await l.AcceptTcpClientAsync(token).ConfigureAwait(false); }
                catch (SocketException) when (!token.IsCancellationRequested)
                {
                    // 대기 중 연결 리셋 등 일시적 오류: 접속 대기를 계속한다. 취소되면 Delay가 루프를 끝낸다
                    await Task.Delay(100, token).ConfigureAwait(false);
                    continue;
                }
                bool accept;
                lock (clientLock)
                {
                    accept = clients.Count < MaxClients;
                    if (accept) clients.Add(c);
                }
                if (accept) _ = ServeClient(c, token);
                else c.Dispose();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException) { }
    }

    async Task ServeClient(TcpClient c, CancellationToken token)
    {
        string ep = "";
        try
        {
            ep = c.Client.RemoteEndPoint?.ToString() ?? "";
            EnableKeepAlive(c);
            Raise(ClientConnected, new ClientEventArgs(ep));
            var stream = c.GetStream();
            var chunk = new byte[1024];
            var pending = new List<byte>();
            while (!token.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
                if (n == 0) break;
                pending.AddRange(chunk.AsSpan(0, n).ToArray());
                // 다 받은 프레임을 모두 처리한다. 끝나지 않은 프레임은 pending에 남는다(최대 8192바이트)
                foreach (var f in TextCommPacket.Extract(pending, TextCommPacket.MaxRequestLength))
                    if (HandleFrame(f) is byte[] res) await stream.WriteAsync(res, token).ConfigureAwait(false);
            }
        }
        catch { }
        finally
        {
            lock (clientLock) clients.Remove(c);
            c.Dispose();
            Raise(ClientDisconnected, new ClientEventArgs(ep));
        }
    }

    static void EnableKeepAlive(TcpClient c)
    {
        c.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        // 플랫폼에 따라 지원하지 않는 옵션이 있을 수 있다
        try
        {
            c.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 10);
            c.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 3);
            c.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch { }
    }
    #endregion

    #region Frame
    // 프레임 하나를 처리하고 보낼 응답을 돌려준다(응답하지 않으면 null).
    // 정상 프레임만 이벤트를 발생시킨다. 여러 클라이언트의 요청은 requestLock으로 한 번에 하나씩 처리한다.
    internal byte[]? HandleFrame(TextCommFrame f)
    {
        if (f.Status != TextCommStatus.Ok) return null;
        var handler = MessageRequest;
        if (handler == null) return null;
        var encoding = MessageEncoding;
        MessageRequestEventArgs e;
        try { e = new MessageRequestEventArgs(f.Slave, f.Command, encoding.GetString(f.Message)); }
        catch (ArgumentException) { return null; }   // 인코딩이 요청 바이트를 읽지 못함
        lock (requestLock)
        {
            try { handler(this, e); }
            catch { return null; }
        }
        try { return TextCommPacket.BuildResponse(encoding, f.Slave, f.Command, e.ResponseMessage); }
        catch (ArgumentException) { return null; }   // 인코딩이 응답 문자열을 바이트로 바꾸지 못함
    }
    #endregion

    #region Raise
    void Raise<T>(EventHandler<T>? handler, T args)
    {
        if (handler == null) return;
        try { handler(this, args); } catch { }
    }
    #endregion
}
