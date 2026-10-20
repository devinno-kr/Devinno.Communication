using System.Xml.Linq;
using Devinno.Communication.Internal;
using System.IO.Ports;
using System.Runtime.InteropServices;

namespace Devinno.Communication.Modbus.RTU;

/// <summary>
/// Modbus RTU slave that uses arrays supplied by the user as its memory.
/// </summary>
/// <remarks>
/// <para>Addresses equal the array index (0-based). A request for an area that is null or out of range gets exception response 02, and an unsupported function gets 01.</para>
/// <para>The receive loop runs on a dedicated background thread, and all events are raised on that thread. To update the UI, marshal asynchronously, as with BeginInvoke (WinForms) or Dispatcher.BeginInvoke (WPF). A synchronous Invoke prolongs the wait when Stop() is called from the UI thread.</para>
/// <para>Each request is processed inside the <see cref="SyncRoot"/> lock. When the application changes several values at once, taking the same lock keeps the master from reading an intermediate state.</para>
/// <para>When several slaves share the same RS-485 bus, a master polling interval of 20 ms or more is recommended. A frame interleaved with another slave's response is discarded, and framing resynchronizes at the next silent interval.</para>
/// </remarks>
/// <example>
/// <code>
/// var hold = new int[1000];
/// var slave = new ModbusRTUSlave { Port = "COM4", Baudrate = 115200, Slave = 1, HoldingRegisters = hold };
/// slave.WordWritten += (s, e) =&gt; Console.WriteLine($"{e.Length} changed from {e.StartAddress}");
/// slave.Start();
/// lock (slave.SyncRoot) { hold[0] = 1; hold[1] = 2; }
/// </code>
/// </example>
public class ModbusRTUSlave : IDisposable
{
    #region class : WrittenEventArgs
    /// <summary>Event arguments delivered after a master's write request has been applied to the array.</summary>
    /// <param name="slave">Slave address of the request (0 for broadcast).</param>
    /// <param name="function">Request function code.</param>
    /// <param name="startAddress">Start address of the write (array index).</param>
    /// <param name="length">Number of items written.</param>
    public class WrittenEventArgs(int slave, ModbusFunction function, int startAddress, int length) : EventArgs
    {
        /// <summary>Slave address of the request (0 for broadcast).</summary>
        public int Slave { get; } = slave;
        /// <summary>Request function code.</summary>
        public ModbusFunction Function { get; } = function;
        /// <summary>Start address of the write (array index).</summary>
        public int StartAddress { get; } = startAddress;
        /// <summary>Number of items written.</summary>
        public int Length { get; } = length;
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

    /// <summary>Address of this slave, 1 to 247 (default: 1).</summary>
    public int Slave { get; set; } = 1;

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
    const int FrameGapMs = 20;
    readonly object lifeLock = new();
    SerialPort? port;
    Thread? thread;
    CancellationTokenSource? cts;
    #endregion

    #region Event
    /// <summary>Raised after a coil write (FC5, FC15) has been applied to the array.</summary>
    public event EventHandler<WrittenEventArgs>? BitWritten;
    /// <summary>Raised after a register write (FC6, FC16, FC26) has been applied to the array.</summary>
    public event EventHandler<WrittenEventArgs>? WordWritten;
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
            thread = new Thread(() => Run(token)) { IsBackground = true, Name = nameof(ModbusRTUSlave) };
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
    /// <summary>Builds a configuration object from the current settings. The memory arrays are not included.</summary>
    /// <returns>The configuration object.</returns>
    public ModbusRTUSlaveConfig ToConfig() => new()
    {
        Port = Port,
        Baudrate = Baudrate,
        Parity = Parity,
        DataBits = DataBits,
        StopBits = StopBits,
        Slave = Slave,
        AutoReconnect = AutoReconnect,
    };

    /// <summary>Applies the configuration object. The memory arrays are not changed.</summary>
    /// <remarks>The whole input is validated before it is applied, so if any single value is invalid, nothing is changed.</remarks>
    /// <param name="config">The configuration to apply.</param>
    /// <exception cref="InvalidOperationException">Called while running (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">A value is out of range (the message includes the element and attribute names).</exception>
    public void ApplyConfig(ModbusRTUSlaveConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (IsStart) throw new InvalidOperationException("실행 중에는 설정을 바꿀 수 없습니다. Stop() 후 호출하세요.");
        config.Validate();
        Port = config.Port;
        Baudrate = config.Baudrate;
        Parity = config.Parity;
        DataBits = config.DataBits;
        StopBits = config.StopBits;
        Slave = config.Slave;
        AutoReconnect = config.AutoReconnect;
    }

    /// <summary>Builds an XML element from the current settings. The memory arrays are not included.</summary>
    /// <returns>The &lt;ModbusRTUSlave&gt; element.</returns>
    public XElement ToXml() => XmlConfig.Serialize(ToConfig());

    /// <summary>Loads the settings from an XML element. The memory arrays are not changed.</summary>
    /// <remarks>
    /// <para>Omitted attributes use their default values. An unknown attribute or element is an error (to catch typos).</para>
    /// <para>The whole input is validated before it is applied, so if any single value is invalid, nothing is changed.</para>
    /// <para>You can check the format as an XSD by passing <see cref="ModbusRTUSlaveConfig"/> to <see cref="ConfigSchema.Get{T}"/>.</para>
    /// </remarks>
    /// <param name="element">The &lt;ModbusRTUSlave&gt; element.</param>
    /// <exception cref="InvalidOperationException">Called while running (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">The root name differs, an unknown attribute or element is present, or a value is invalid (the message includes the element and attribute names).</exception>
    public void FromXml(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (IsStart) throw new InvalidOperationException("실행 중에는 설정을 불러올 수 없습니다. Stop() 후 호출하세요.");
        ApplyConfig(XmlConfig.Deserialize<ModbusRTUSlaveConfig>(element));
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
            // Stop()이 이미 IsStart를 내렸거나 새 Start()가 thread를 바꿨다면 이 스레드는 건드리지 않는다
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
        var buffer = new List<byte>();
        var chunk = new byte[512];
        var skip = false; // CRC가 틀린 프레임 뒤에는 다음 무응답 구간까지 수신 바이트를 버린다
        sp.ReadTimeout = FrameGapMs;
        while (!token.IsCancellationRequested)
        {
            int n;
            try { n = sp.Read(chunk, 0, chunk.Length); }
            catch (TimeoutException) { n = 0; }

            if (n > 0)
            {
                if (skip) continue;
                for (int i = 0; i < n; i++) buffer.Add(chunk[i]);
                while (ModbusRtuFrame.RequestLength(CollectionsMarshal.AsSpan(buffer)) is int len && buffer.Count >= len)
                {
                    var frame = buffer.GetRange(0, len).ToArray();
                    if (!ModbusCRC.Check(frame))
                    {
                        // 다른 슬레이브 응답 등이 섞여 길이로 잘린 조각: 버리고 다음 무응답 구간에서 다시 맞춘다
                        buffer.Clear();
                        skip = true;
                        break;
                    }
                    Respond(sp, frame);
                    buffer.RemoveRange(0, len);
                }
            }
            else
            {
                skip = false;
                if (buffer.Count > 0)
                {
                    // 길이를 모르는 FC이거나 깨진 데이터: FrameGapMs 동안 수신이 없으면 프레임 끝으로 본다
                    Respond(sp, buffer.ToArray());
                    buffer.Clear();
                }
            }
        }
    }

    void Respond(SerialPort sp, byte[] frame)
    {
        var res = HandleFrame(frame);
        if (res != null) sp.Write(res, 0, res.Length);
    }
    #endregion

    #region Frame
    internal byte[]? HandleFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 4 || !ModbusCRC.Check(frame)) return null;
        int addr = frame[0];
        if (addr != Slave && addr != 0) return null;

        byte[] resPdu;
        ModbusWrite? written;
        lock (SyncRoot)
            resPdu = ModbusPdu.Execute(frame[1..^2], Coils, Contacts, HoldingRegisters, InputRegisters, out written);

        if (written is ModbusWrite w)
            Raise(w.IsBit ? BitWritten : WordWritten, new WrittenEventArgs(addr, w.Function, w.StartAddress, w.Length));

        return addr == 0 ? null : ModbusRtuFrame.Build(addr, resPdu);
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
