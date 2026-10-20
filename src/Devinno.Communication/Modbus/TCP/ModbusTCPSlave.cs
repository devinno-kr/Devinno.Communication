using System.Xml.Linq;
using Devinno.Communication.Internal;
using System.Net;
using System.Net.Sockets;

namespace Devinno.Communication.Modbus.TCP;

/// <summary>
/// Modbus TCP slave (server) that accepts connections from several clients. It uses arrays supplied by the user as its memory.
/// </summary>
/// <remarks>
/// <para>Addresses equal the array index (0-based). A request for an area that is null or out of range gets exception response 02, and an unsupported function gets 01.</para>
/// <para>Responds to requests whose unit ID is <see cref="Slave"/>, 0, or 255. Requests with any other unit ID are ignored.</para>
/// <para>Connection handling runs asynchronously, and events are raised on thread-pool threads. To update the UI, marshal asynchronously, as with BeginInvoke (WinForms) or Dispatcher.BeginInvoke (WPF). A synchronous Invoke prolongs the wait when Stop() is called from the UI thread.</para>
/// <para>Disconnected clients (for example, after a power loss) are cleaned up within about 20 seconds by TCP keepalive.</para>
/// <para>Each request is processed inside the <see cref="SyncRoot"/> lock, so requests from several clients at once are never interleaved within a single request.</para>
/// </remarks>
/// <example>
/// <code>
/// var slave = new ModbusTCPSlave { LocalPort = 502, Slave = 1, HoldingRegisters = new int[1000] };
/// slave.ClientConnected += (s, e) =&gt; Console.WriteLine($"Connected: {e.RemoteEndPoint}");
/// slave.Start();
/// </code>
/// </example>
public class ModbusTCPSlave : IDisposable
{
    #region class : EventArgs
    /// <summary>Event arguments delivered after a master's write request has been applied to the array.</summary>
    /// <param name="slave">Unit ID of the request.</param>
    /// <param name="function">Request function code.</param>
    /// <param name="startAddress">Start address of the write (array index).</param>
    /// <param name="length">Number of items written.</param>
    public class WrittenEventArgs(int slave, ModbusFunction function, int startAddress, int length) : EventArgs
    {
        /// <summary>Unit ID of the request.</summary>
        public int Slave { get; } = slave;
        /// <summary>Request function code.</summary>
        public ModbusFunction Function { get; } = function;
        /// <summary>Start address of the write (array index).</summary>
        public int StartAddress { get; } = startAddress;
        /// <summary>Number of items written.</summary>
        public int Length { get; } = length;
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
    /// <summary>Listening port (default: 502).</summary>
    public int LocalPort { get; set; } = 502;
    /// <summary>Unit ID of this slave, 1 to 247 (default: 1). It also always responds to 0 and 255.</summary>
    public int Slave { get; set; } = 1;
    /// <summary>Maximum number of simultaneous connections (default: 10). Connections beyond this are closed immediately.</summary>
    public int MaxClients { get; set; } = 10;

    /// <summary>Coil area (FC1, FC5, FC15). If null, such requests get exception 02.</summary>
    public bool[]? Coils { get; set; }
    /// <summary>Discrete input area (FC2). If null, such requests get exception 02.</summary>
    public bool[]? Contacts { get; set; }
    /// <summary>Holding register area (FC3, FC6, FC16, FC26). Only the low 16 bits of each value are used. If null, exception 02.</summary>
    public int[]? HoldingRegisters { get; set; }
    /// <summary>Input register area (FC4). Only the low 16 bits of each value are used. If null, exception 02.</summary>
    public int[]? InputRegisters { get; set; }

    /// <summary>The lock object held while a request is processed. Use it to change several values atomically from the application.</summary>
    public object SyncRoot { get; } = new();

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
    readonly List<TcpClient> clients = [];
    TcpListener? listener;
    CancellationTokenSource? cts;
    Task? acceptTask;
    #endregion

    #region Event
    /// <summary>Raised after a coil write (FC5, FC15) has been applied to the array.</summary>
    public event EventHandler<WrittenEventArgs>? BitWritten;
    /// <summary>Raised after a register write (FC6, FC16, FC26) has been applied to the array.</summary>
    public event EventHandler<WrittenEventArgs>? WordWritten;
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
    /// <summary>Builds a configuration object from the current settings. The memory arrays are not included.</summary>
    /// <returns>The configuration object.</returns>
    public ModbusTCPSlaveConfig ToConfig() => new()
    {
        LocalPort = LocalPort,
        Slave = Slave,
        MaxClients = MaxClients,
    };

    /// <summary>Applies the configuration object. The memory arrays are not changed.</summary>
    /// <remarks>The whole input is validated before it is applied, so if any single value is invalid, nothing is changed.</remarks>
    /// <param name="config">The configuration to apply.</param>
    /// <exception cref="InvalidOperationException">Called while running (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">A value is out of range (the message includes the element and attribute names).</exception>
    public void ApplyConfig(ModbusTCPSlaveConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (IsStart) throw new InvalidOperationException("실행 중에는 설정을 바꿀 수 없습니다. Stop() 후 호출하세요.");
        config.Validate();
        LocalPort = config.LocalPort;
        Slave = config.Slave;
        MaxClients = config.MaxClients;
    }

    /// <summary>Builds an XML element from the current settings. The memory arrays are not included.</summary>
    /// <returns>The &lt;ModbusTCPSlave&gt; element.</returns>
    public XElement ToXml() => XmlConfig.Serialize(ToConfig());

    /// <summary>Loads the settings from an XML element. The memory arrays are not changed.</summary>
    /// <remarks>
    /// <para>Omitted attributes use their default values. An unknown attribute or element is an error (to catch typos).</para>
    /// <para>The whole input is validated before it is applied, so if any single value is invalid, nothing is changed.</para>
    /// <para>You can check the format as an XSD by passing <see cref="ModbusTCPSlaveConfig"/> to <see cref="ConfigSchema.Get{T}"/>.</para>
    /// </remarks>
    /// <param name="element">The &lt;ModbusTCPSlave&gt; element.</param>
    /// <exception cref="InvalidOperationException">Called while running (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">The root name differs, an unknown attribute or element is present, or a value is invalid (the message includes the element and attribute names).</exception>
    public void FromXml(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (IsStart) throw new InvalidOperationException("실행 중에는 설정을 불러올 수 없습니다. Stop() 후 호출하세요.");
        ApplyConfig(XmlConfig.Deserialize<ModbusTCPSlaveConfig>(element));
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
            var buf = new byte[1024];
            int count = 0;
            while (!token.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buf.AsMemory(count), token).ConfigureAwait(false);
                if (n == 0) break;
                count += n;

                bool broken = false;
                while (true)
                {
                    var len = ModbusTcpFrame.FrameLength(buf.AsSpan(0, count));
                    if (len == null || (len > 0 && count < len)) break;
                    if (len < 0) { broken = true; break; }   // 스트림 정렬이 깨짐 → 연결 종료

                    int frameLen = len!.Value;
                    var res = HandleFrame(buf.AsSpan(0, frameLen));
                    if (res != null) await stream.WriteAsync(res, token).ConfigureAwait(false);
                    Buffer.BlockCopy(buf, frameLen, buf, 0, count - frameLen);
                    count -= frameLen;
                }
                if (broken) break;
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
    internal byte[]? HandleFrame(ReadOnlySpan<byte> frame)
    {
        int unit = frame[6];
        if (unit != Slave && unit != 0 && unit != 255) return null;

        byte[] resPdu;
        ModbusWrite? written;
        lock (SyncRoot)
            resPdu = ModbusPdu.Execute(frame[7..], Coils, Contacts, HoldingRegisters, InputRegisters, out written);

        if (written is ModbusWrite w)
            Raise(w.IsBit ? BitWritten : WordWritten, new WrittenEventArgs(unit, w.Function, w.StartAddress, w.Length));

        return ModbusTcpFrame.Build(frame[0] << 8 | frame[1], unit, resPdu);
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
