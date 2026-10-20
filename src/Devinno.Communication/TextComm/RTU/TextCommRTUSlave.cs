using System.IO.Ports;
using System.Text;
using System.Xml.Linq;
using Devinno.Communication.Internal;

namespace Devinno.Communication.TextComm.RTU;

/// <summary>
/// Slave that receives TextComm requests over serial (RS-232/485) and decides the response in the <see cref="MessageRequest"/> event.
/// </summary>
/// <remarks>
/// <para>The frame format is the same as <see cref="TextCommRTUMaster"/> (STX | slave | command | message | sum | ETX, with DLE escaping).</para>
/// <para><see cref="MessageRequest"/> is raised for every request. If the handler sets <see cref="MessageRequestEventArgs.ResponseMessage"/>, a response is sent.
/// If null (default, not set), no response is sent; if an empty string (""), a frame with no message (slave, command, sum) is sent as the response. The slave and command of the response equal those of the request.
/// To send no response, use null, not an empty string.</para>
/// <para>Requests are not filtered by slave address. The event is raised for every request, so check <see cref="MessageRequestEventArgs.Slave"/> to decide whether to respond.</para>
/// <para>A frame with a wrong sum or a malformed format, or a frame longer than 8192 bytes, is discarded without raising an event. If the handler throws, no response is sent.</para>
/// <para>The receive loop runs on a dedicated background thread, and events are raised on that thread. The response is sent after the handler returns, so the handler must finish within the master's timeout.</para>
/// </remarks>
/// <example>
/// <code>
/// using Devinno.Communication.TextComm.RTU;
///
/// var slave = new TextCommRTUSlave { Port = "COM4", Baudrate = 115200 };
/// slave.MessageRequest += (s, e) =&gt;
/// {
///     if (e.Slave != 1) return;                         // different station number: no response (ResponseMessage = null)
///     if (e.Command == 0x41) e.ResponseMessage = "OK " + e.RequestMessage;
///     else e.ResponseMessage = "";                       // respond with no message
/// };
/// slave.Start();
/// </code>
/// </example>
public class TextCommRTUSlave : IDisposable
{
    #region class : MessageRequestEventArgs
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
    /// <summary>Message string encoding (default: ASCII). Used to read requests and build responses.</summary>
    public Encoding MessageEncoding { get; set; } = Encoding.ASCII;
    /// <summary>Whether to reopen the port at 1-second intervals when it could not be opened or was lost (default: false).</summary>
    public bool AutoReconnect { get; set; } = false;

    /// <summary>Gets whether the serial port is open.</summary>
    public bool IsOpen => Volatile.Read(ref port)?.IsOpen ?? false;
    /// <summary>Gets whether the receive loop is running.</summary>
    public bool IsStart { get; private set; }
    /// <summary>Gets whether the object has been disposed.</summary>
    public bool IsDisposed { get; private set; }
    /// <summary>Gets or sets a user-defined tag.</summary>
    public object? Tag { get; set; }
    #endregion

    #region Member Variable
    readonly object lifeLock = new();
    SerialPort? port;
    Thread? thread;
    CancellationTokenSource? cts;
    #endregion

    #region Event
    /// <summary>Raised when a request from the master is received. Setting <see cref="MessageRequestEventArgs.ResponseMessage"/> sends a response.</summary>
    public event EventHandler<MessageRequestEventArgs>? MessageRequest;
    /// <summary>Raised when the serial port is opened.</summary>
    public event EventHandler? DeviceOpened;
    /// <summary>Raised when the serial port is closed.</summary>
    public event EventHandler? DeviceClosed;
    #endregion

    #region Start / Stop
    /// <summary>Opens the serial port and starts the receive loop. Does nothing if it is already running.</summary>
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
            thread = new Thread(() => Run(token)) { IsBackground = true, Name = nameof(TextCommRTUSlave) };
            thread.Start();
        }
    }

    /// <summary>Stops the receive loop and closes the port. Waits up to 2 seconds for the loop to end.</summary>
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
        if (t != null && t != Thread.CurrentThread) t.Join(2000);
        c?.Dispose();
    }

    /// <summary>Stops the receive loop and releases its resources.</summary>
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
    public TextCommRTUSlaveConfig ToConfig() => new()
    {
        Port = Port,
        Baudrate = Baudrate,
        Parity = Parity,
        DataBits = DataBits,
        StopBits = StopBits,
        MessageEncoding = MessageEncoding.WebName,
        AutoReconnect = AutoReconnect,
    };

    /// <summary>Applies the configuration object.</summary>
    /// <remarks>The whole input is validated before it is applied, so if any single value is invalid, nothing is changed.</remarks>
    /// <param name="config">The configuration to apply.</param>
    /// <exception cref="InvalidOperationException">Called while running (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">A value is invalid (the message includes the element and attribute names).</exception>
    public void ApplyConfig(TextCommRTUSlaveConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (IsStart) throw new InvalidOperationException("실행 중에는 설정을 바꿀 수 없습니다. Stop() 후 호출하세요.");
        var encoding = config.Validate();
        Port = config.Port;
        Baudrate = config.Baudrate;
        Parity = config.Parity;
        DataBits = config.DataBits;
        StopBits = config.StopBits;
        MessageEncoding = encoding;
        AutoReconnect = config.AutoReconnect;
    }

    /// <summary>Builds an XML element from the current settings.</summary>
    /// <returns>The &lt;TextCommRTUSlave&gt; element.</returns>
    public XElement ToXml() => XmlConfig.Serialize(ToConfig());

    /// <summary>Loads the settings from an XML element.</summary>
    /// <remarks>
    /// <para>Omitted attributes use their default values. An unknown attribute or element is an error (to catch typos).</para>
    /// <para>The whole input is validated before it is applied, so if any single value is invalid, nothing is changed.</para>
    /// <para>You can check the format as an XSD by passing <see cref="TextCommRTUSlaveConfig"/> to <see cref="ConfigSchema.Get{T}"/>.</para>
    /// </remarks>
    /// <param name="element">The &lt;TextCommRTUSlave&gt; element.</param>
    /// <exception cref="InvalidOperationException">Called while running (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">The root name differs, an unknown attribute or element is present, or a value is invalid.</exception>
    public void FromXml(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (IsStart) throw new InvalidOperationException("실행 중에는 설정을 불러올 수 없습니다. Stop() 후 호출하세요.");
        ApplyConfig(XmlConfig.Deserialize<TextCommRTUSlaveConfig>(element));
    }

    /// <summary>Saves the settings to an XML file.</summary>
    /// <param name="path">File path.</param>
    public void SaveXml(string path) => ToXml().Save(path);

    /// <summary>Loads the settings from an XML file. The rules are the same as <see cref="FromXml"/>.</summary>
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
                        try { Serve(sp, token); }
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

    void Serve(SerialPort sp, CancellationToken token)
    {
        var pending = new List<byte>();
        var chunk = new byte[512];
        sp.ReadTimeout = 50;
        while (!token.IsCancellationRequested)
        {
            int n;
            try { n = sp.Read(chunk, 0, chunk.Length); }
            catch (TimeoutException) { continue; }
            pending.AddRange(chunk.AsSpan(0, n).ToArray());
            // 끊긴 프레임은 다음 STX에서 다시 맞춰지므로 따로 시간으로 자르지 않는다
            foreach (var f in TextCommPacket.Extract(pending, TextCommPacket.MaxRequestLength))
                if (HandleFrame(f) is byte[] res) sp.Write(res, 0, res.Length);
        }
    }
    #endregion

    #region Frame
    // 프레임 하나를 처리하고 보낼 응답을 돌려준다(응답하지 않으면 null).
    // 정상 프레임만 이벤트를 발생시킨다. 핸들러 예외나 ResponseMessage == null이면 응답하지 않는다.
    internal byte[]? HandleFrame(TextCommFrame f)
    {
        if (f.Status != TextCommStatus.Ok) return null;
        var handler = MessageRequest;
        if (handler == null) return null;
        var encoding = MessageEncoding;
        MessageRequestEventArgs e;
        try { e = new MessageRequestEventArgs(f.Slave, f.Command, encoding.GetString(f.Message)); }
        catch (ArgumentException) { return null; }   // 인코딩이 요청 바이트를 읽지 못함
        try { handler(this, e); }
        catch { return null; }
        try { return TextCommPacket.BuildResponse(encoding, f.Slave, f.Command, e.ResponseMessage); }
        catch (ArgumentException) { return null; }   // 인코딩이 응답 문자열을 바이트로 바꾸지 못함
    }
    #endregion

    #region Raise
    void Raise(EventHandler? handler)
    {
        if (handler == null) return;
        try { handler(this, EventArgs.Empty); } catch { }
    }
    #endregion
}
