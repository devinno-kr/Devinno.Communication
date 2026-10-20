using System.Xml.Linq;
using System.Diagnostics;
using System.IO.Ports;
using Devinno.Communication.Internal;

namespace Devinno.Communication.Modbus.RTU;

/// <summary>
/// Serial (RS-232/485) Modbus RTU master. Polls through Auto (polling) and Manual (one-shot) job queues.
/// </summary>
/// <remarks>
/// <para>Manual jobs are processed before Auto jobs. After one pass through the Auto list, polling starts again from the beginning.</para>
/// <para>Responses arrive through the individual Received events, and the id given at registration is returned as <c>MessageID</c>.</para>
/// <para>The communication loop runs on a dedicated background thread, and all events are raised on that thread. To update the UI, marshal asynchronously, as with BeginInvoke (WinForms) or Dispatcher.BeginInvoke (WPF). A synchronous Invoke prolongs the wait when Stop() is called from the UI thread. Exceptions thrown in a handler are ignored and the loop continues.</para>
/// </remarks>
/// <example>
/// <code>
/// var mb = new ModbusRTUMaster { Port = "COM3", Baudrate = 115200, AutoReconnect = true };
/// mb.AutoWordRead_FC3(1, 1, 0, 10);
/// mb.WordReadReceived += (s, e) =&gt; { if (e.MessageID == 1) Console.WriteLine(string.Join(",", e.ReceiveData)); };
/// mb.Start();
/// mb.ManualWordWrite_FC6(10, 1, 100, 1234);
/// mb.NamedWrite("Recipe", [1, 2, 3]);   // calls the XML &lt;Write Name="Recipe" Id="30" Func="FC16" Slave="1" Start="10" /&gt; by name
/// </code>
/// </example>
public class ModbusRTUMaster : IDisposable
{
    #region class : Work
    internal sealed class Work(int messageId, int slave, byte[] pdu, int? repeatCount, int? timeout)
    {
        public int MessageID { get; } = messageId;
        public int Slave { get; } = slave;
        public byte[] Pdu { get; } = pdu;
        public int? RepeatCount { get; } = repeatCount;
        public int? Timeout { get; } = timeout;
        public ModbusFunction Function => (ModbusFunction)Pdu[0];
        public int StartAddress => ModbusPdu.Word(Pdu, 1);
    }
    #endregion

    #region class : EventArgs
    /// <summary>Common event arguments for request result events. Timeout and CRC error events use this type as-is.</summary>
    /// <param name="messageId">The message ID given at registration.</param>
    /// <param name="slave">Slave address.</param>
    /// <param name="function">Function code.</param>
    /// <param name="startAddress">Start address.</param>
    public class ResultEventArgs(int messageId, int slave, ModbusFunction function, int startAddress) : EventArgs
    {
        /// <summary>The message ID given at registration.</summary>
        public int MessageID { get; } = messageId;
        /// <summary>Slave address.</summary>
        public int Slave { get; } = slave;
        /// <summary>Function code.</summary>
        public ModbusFunction Function { get; } = function;
        /// <summary>Start address.</summary>
        public int StartAddress { get; } = startAddress;
    }

    /// <summary>Event arguments for a bit read (FC1/FC2) response.</summary>
    /// <param name="messageId">Message ID.</param>
    /// <param name="slave">Slave address.</param>
    /// <param name="function">Function code.</param>
    /// <param name="startAddress">Start address.</param>
    /// <param name="receiveData">Bits read (as many as the requested length).</param>
    public class BitReadEventArgs(int messageId, int slave, ModbusFunction function, int startAddress, bool[] receiveData)
        : ResultEventArgs(messageId, slave, function, startAddress)
    {
        /// <summary>Bits read (as many as the requested length).</summary>
        public bool[] ReceiveData { get; } = receiveData;
    }

    /// <summary>Event arguments for a word read (FC3/FC4) response.</summary>
    /// <param name="messageId">Message ID.</param>
    /// <param name="slave">Slave address.</param>
    /// <param name="function">Function code.</param>
    /// <param name="startAddress">Start address.</param>
    /// <param name="receiveData">Values read (0 to 65535).</param>
    public class WordReadEventArgs(int messageId, int slave, ModbusFunction function, int startAddress, int[] receiveData)
        : ResultEventArgs(messageId, slave, function, startAddress)
    {
        /// <summary>Values read (0 to 65535).</summary>
        public int[] ReceiveData { get; } = receiveData;
    }

    /// <summary>Event arguments for a single-coil write (FC5) response.</summary>
    /// <param name="messageId">Message ID.</param>
    /// <param name="slave">Slave address.</param>
    /// <param name="function">Function code.</param>
    /// <param name="startAddress">Address.</param>
    /// <param name="writeValue">The value written.</param>
    public class BitWriteEventArgs(int messageId, int slave, ModbusFunction function, int startAddress, bool writeValue)
        : ResultEventArgs(messageId, slave, function, startAddress)
    {
        /// <summary>The value written.</summary>
        public bool WriteValue { get; } = writeValue;
    }

    /// <summary>Event arguments for a single-register write (FC6) response.</summary>
    /// <param name="messageId">Message ID.</param>
    /// <param name="slave">Slave address.</param>
    /// <param name="function">Function code.</param>
    /// <param name="startAddress">Address.</param>
    /// <param name="writeValue">The value written (0 to 65535).</param>
    public class WordWriteEventArgs(int messageId, int slave, ModbusFunction function, int startAddress, int writeValue)
        : ResultEventArgs(messageId, slave, function, startAddress)
    {
        /// <summary>The value written (0 to 65535).</summary>
        public int WriteValue { get; } = writeValue;
    }

    /// <summary>Event arguments for a multiple-coil write (FC15) response.</summary>
    /// <param name="messageId">Message ID.</param>
    /// <param name="slave">Slave address.</param>
    /// <param name="function">Function code.</param>
    /// <param name="startAddress">Start address.</param>
    /// <param name="writeValues">The values written.</param>
    public class MultiBitWriteEventArgs(int messageId, int slave, ModbusFunction function, int startAddress, bool[] writeValues)
        : ResultEventArgs(messageId, slave, function, startAddress)
    {
        /// <summary>The values written.</summary>
        public bool[] WriteValues { get; } = writeValues;
    }

    /// <summary>Event arguments for a multiple-register write (FC16) response.</summary>
    /// <param name="messageId">Message ID.</param>
    /// <param name="slave">Slave address.</param>
    /// <param name="function">Function code.</param>
    /// <param name="startAddress">Start address.</param>
    /// <param name="writeValues">The values written (0 to 65535).</param>
    public class MultiWordWriteEventArgs(int messageId, int slave, ModbusFunction function, int startAddress, int[] writeValues)
        : ResultEventArgs(messageId, slave, function, startAddress)
    {
        /// <summary>The values written (0 to 65535).</summary>
        public int[] WriteValues { get; } = writeValues;
    }

    /// <summary>Event arguments for a register bit set (FC26) response.</summary>
    /// <param name="messageId">Message ID.</param>
    /// <param name="slave">Slave address.</param>
    /// <param name="function">Function code.</param>
    /// <param name="startAddress">Register address.</param>
    /// <param name="bitIndex">Bit index, 0 to 15.</param>
    /// <param name="writeValue">The value that was set.</param>
    public class WordBitSetEventArgs(int messageId, int slave, ModbusFunction function, int startAddress, int bitIndex, bool writeValue)
        : ResultEventArgs(messageId, slave, function, startAddress)
    {
        /// <summary>Bit index, 0 to 15.</summary>
        public int BitIndex { get; } = bitIndex;
        /// <summary>The value that was set.</summary>
        public bool WriteValue { get; } = writeValue;
    }

    /// <summary>Event arguments for when the slave sends a Modbus exception response.</summary>
    /// <param name="messageId">Message ID.</param>
    /// <param name="slave">Slave address.</param>
    /// <param name="function">Request function code.</param>
    /// <param name="startAddress">Start address.</param>
    /// <param name="exceptionCode">Exception code (1: unsupported function, 2: invalid address, 3: invalid value, ...).</param>
    public class ExceptionEventArgs(int messageId, int slave, ModbusFunction function, int startAddress, int exceptionCode)
        : ResultEventArgs(messageId, slave, function, startAddress)
    {
        /// <summary>Exception code (1: unsupported function, 2: invalid address, 3: invalid value, ...).</summary>
        public int ExceptionCode { get; } = exceptionCode;
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
    // 이름 있는 쓰기 표. NamedWrite는 실행 중에도 불리므로 통째로 바꾸는 참조(volatile)로 둔다. ApplyConfig에서만 교체한다.
    volatile NamedWriteTable<ModbusWriteConfig> namedWrites = NamedWriteTable<ModbusWriteConfig>.Empty;
    #endregion

    #region Event
    /// <summary>Raised when a bit read (FC1/FC2) response is received.</summary>
    public event EventHandler<BitReadEventArgs>? BitReadReceived;
    /// <summary>Raised when a word read (FC3/FC4) response is received.</summary>
    public event EventHandler<WordReadEventArgs>? WordReadReceived;
    /// <summary>Raised when a single-coil write (FC5) response is received.</summary>
    public event EventHandler<BitWriteEventArgs>? BitWriteReceived;
    /// <summary>Raised when a single-register write (FC6) response is received.</summary>
    public event EventHandler<WordWriteEventArgs>? WordWriteReceived;
    /// <summary>Raised when a multiple-coil write (FC15) response is received.</summary>
    public event EventHandler<MultiBitWriteEventArgs>? MultiBitWriteReceived;
    /// <summary>Raised when a multiple-register write (FC16) response is received.</summary>
    public event EventHandler<MultiWordWriteEventArgs>? MultiWordWriteReceived;
    /// <summary>Raised when a register bit set (FC26) response is received.</summary>
    public event EventHandler<WordBitSetEventArgs>? WordBitSetReceived;
    /// <summary>Raised when the slave sends an exception response.</summary>
    public event EventHandler<ExceptionEventArgs>? ExceptionReceived;
    /// <summary>Raised when there is no response, the response is malformed, or the port connection fails during a request. Raised on every retry.</summary>
    public event EventHandler<ResultEventArgs>? TimeoutReceived;
    /// <summary>Raised when the CRC of a response is wrong. Raised on every retry.</summary>
    public event EventHandler<ResultEventArgs>? CRCErrorReceived;
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
            thread = new Thread(() => Run(token)) { IsBackground = true, Name = nameof(ModbusRTUMaster) };
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

    static void CheckSlave(int slave, bool allowBroadcast)
    {
        if (slave < (allowBroadcast ? 0 : 1) || slave > 247)
            throw new ArgumentOutOfRangeException(nameof(slave), allowBroadcast ? "슬레이브 주소는 0~247 입니다." : "슬레이브 주소는 1~247 입니다.");
    }

    void AddAuto(int id, int slave, byte[] pdu, int? timeout)
    {
        CheckSlave(slave, false);
        lock (workLock) autoWorks.Add(new Work(id, slave, pdu, null, timeout));
    }

    void AddManual(int id, int slave, byte[] pdu, int? repeatCount, int? timeout, bool allowBroadcast)
    {
        CheckSlave(slave, allowBroadcast);
        lock (workLock) manualWorks.Add(new Work(id, slave, pdu, repeatCount, timeout));
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
    #endregion

    #region Auto
    /// <summary>Registers an FC1 coil read in the Auto list.</summary>
    /// <param name="id">Message ID (the MessageID of the response event).</param>
    /// <param name="slave">Slave address, 1 to 247.</param>
    /// <param name="startAddr">Start address, 0 to 65535.</param>
    /// <param name="length">Number of items to read, 1 to 2000.</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void AutoBitRead_FC1(int id, int slave, int startAddr, int length, int? timeout = null)
        => AddAuto(id, slave, ModbusPdu.ReadRequest(ModbusFunction.BITREAD_F1, startAddr, length), timeout);

    /// <summary>Registers an FC2 discrete input read in the Auto list.</summary>
    /// <param name="id">Message ID (the MessageID of the response event).</param>
    /// <param name="slave">Slave address, 1 to 247.</param>
    /// <param name="startAddr">Start address, 0 to 65535.</param>
    /// <param name="length">Number of items to read, 1 to 2000.</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void AutoBitRead_FC2(int id, int slave, int startAddr, int length, int? timeout = null)
        => AddAuto(id, slave, ModbusPdu.ReadRequest(ModbusFunction.BITREAD_F2, startAddr, length), timeout);

    /// <summary>Registers an FC3 holding register read in the Auto list.</summary>
    /// <param name="id">Message ID (the MessageID of the response event).</param>
    /// <param name="slave">Slave address, 1 to 247.</param>
    /// <param name="startAddr">Start address, 0 to 65535.</param>
    /// <param name="length">Number of items to read, 1 to 125.</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void AutoWordRead_FC3(int id, int slave, int startAddr, int length, int? timeout = null)
        => AddAuto(id, slave, ModbusPdu.ReadRequest(ModbusFunction.WORDREAD_F3, startAddr, length), timeout);

    /// <summary>Registers an FC4 input register read in the Auto list.</summary>
    /// <param name="id">Message ID (the MessageID of the response event).</param>
    /// <param name="slave">Slave address, 1 to 247.</param>
    /// <param name="startAddr">Start address, 0 to 65535.</param>
    /// <param name="length">Number of items to read, 1 to 125.</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void AutoWordRead_FC4(int id, int slave, int startAddr, int length, int? timeout = null)
        => AddAuto(id, slave, ModbusPdu.ReadRequest(ModbusFunction.WORDREAD_F4, startAddr, length), timeout);
    #endregion

    #region Manual
    /// <summary>Registers a one-shot FC1 coil read.</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="slave">Slave address, 1 to 247.</param>
    /// <param name="startAddr">Start address.</param>
    /// <param name="length">Number of items to read, 1 to 2000.</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualBitRead_FC1(int id, int slave, int startAddr, int length, int? repeatCount = null, int? timeout = null)
        => AddManual(id, slave, ModbusPdu.ReadRequest(ModbusFunction.BITREAD_F1, startAddr, length), repeatCount, timeout, false);

    /// <summary>Registers a one-shot FC2 discrete input read.</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="slave">Slave address, 1 to 247.</param>
    /// <param name="startAddr">Start address.</param>
    /// <param name="length">Number of items to read, 1 to 2000.</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualBitRead_FC2(int id, int slave, int startAddr, int length, int? repeatCount = null, int? timeout = null)
        => AddManual(id, slave, ModbusPdu.ReadRequest(ModbusFunction.BITREAD_F2, startAddr, length), repeatCount, timeout, false);

    /// <summary>Registers a one-shot FC3 holding register read.</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="slave">Slave address, 1 to 247.</param>
    /// <param name="startAddr">Start address.</param>
    /// <param name="length">Number of items to read, 1 to 125.</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualWordRead_FC3(int id, int slave, int startAddr, int length, int? repeatCount = null, int? timeout = null)
        => AddManual(id, slave, ModbusPdu.ReadRequest(ModbusFunction.WORDREAD_F3, startAddr, length), repeatCount, timeout, false);

    /// <summary>Registers a one-shot FC4 input register read.</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="slave">Slave address, 1 to 247.</param>
    /// <param name="startAddr">Start address.</param>
    /// <param name="length">Number of items to read, 1 to 125.</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualWordRead_FC4(int id, int slave, int startAddr, int length, int? repeatCount = null, int? timeout = null)
        => AddManual(id, slave, ModbusPdu.ReadRequest(ModbusFunction.WORDREAD_F4, startAddr, length), repeatCount, timeout, false);

    /// <summary>Registers an FC5 write of a single coil.</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="slave">Slave address, 0 to 247 (0 is broadcast; no response).</param>
    /// <param name="startAddr">Address.</param>
    /// <param name="value">The value to write.</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualBitWrite_FC5(int id, int slave, int startAddr, bool value, int? repeatCount = null, int? timeout = null)
        => AddManual(id, slave, ModbusPdu.WriteSingleCoil(startAddr, value), repeatCount, timeout, true);

    /// <summary>Registers an FC6 write of a single register.</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="slave">Slave address, 0 to 247 (0 is broadcast; no response).</param>
    /// <param name="startAddr">Address.</param>
    /// <param name="value">The value to write (only the low 16 bits are sent).</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualWordWrite_FC6(int id, int slave, int startAddr, int value, int? repeatCount = null, int? timeout = null)
        => AddManual(id, slave, ModbusPdu.WriteSingleRegister(startAddr, value), repeatCount, timeout, true);

    /// <summary>Registers an FC15 write of multiple coils.</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="slave">Slave address, 0 to 247 (0 is broadcast; no response).</param>
    /// <param name="startAddr">Start address.</param>
    /// <param name="values">The values to write (1 to 1968).</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualMultiBitWrite_FC15(int id, int slave, int startAddr, bool[] values, int? repeatCount = null, int? timeout = null)
        => AddManual(id, slave, ModbusPdu.WriteMultipleCoils(startAddr, values), repeatCount, timeout, true);

    /// <summary>Registers an FC16 write of multiple registers.</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="slave">Slave address, 0 to 247 (0 is broadcast; no response).</param>
    /// <param name="startAddr">Start address.</param>
    /// <param name="values">The values to write (1 to 123; only the low 16 bits of each are sent).</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualMultiWordWrite_FC16(int id, int slave, int startAddr, int[] values, int? repeatCount = null, int? timeout = null)
        => AddManual(id, slave, ModbusPdu.WriteMultipleRegisters(startAddr, values), repeatCount, timeout, true);

    /// <summary>Registers an FC26 bit set within a register (Devinno extension function).</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="slave">Slave address, 0 to 247 (0 is broadcast; no response).</param>
    /// <param name="startAddr">Register address.</param>
    /// <param name="bitIndex">Bit index, 0 to 15.</param>
    /// <param name="value">The value to set.</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualWordBitSet_FC26(int id, int slave, int startAddr, int bitIndex, bool value, int? repeatCount = null, int? timeout = null)
        => AddManual(id, slave, ModbusPdu.WordBitSet(startAddr, bitIndex, value), repeatCount, timeout, true);

    /// <summary>Registers a write of one value by the name defined in the XML &lt;Write&gt; element. FC5 takes 0/1 and FC6 takes -32768 to 65535; FC15/FC16 send it as a one-element array.</summary>
    /// <param name="name">The Name of the &lt;Write&gt; element.</param>
    /// <param name="value">The value to write (depending on the function: 0 or 1 for FC5/FC15, -32768 to 65535 for FC6/FC16).</param>
    /// <param name="repeatCount">Retry count (if null, the RepeatCount in the XML; if that is absent too, no retries).</param>
    /// <param name="timeout">Timeout in ms (if null, the Timeout in the XML; if that is absent too, <see cref="Timeout"/>).</param>
    /// <exception cref="ArgumentException">The name does not exist, or a value is out of range.</exception>
    public void NamedWrite(string name, long value, int? repeatCount = null, int? timeout = null)
        => NamedWrite(name, [value], repeatCount, timeout);

    /// <summary>Registers a write of several values by the name defined in the XML &lt;Write&gt; element. FC5/FC6 accept only one value.</summary>
    /// <param name="name">The Name of the &lt;Write&gt; element.</param>
    /// <param name="values">The values to write (each 0 or 1 for FC15, each -32768 to 65535 for FC16).</param>
    /// <param name="repeatCount">Retry count (if null, the RepeatCount in the XML; if that is absent too, no retries).</param>
    /// <param name="timeout">Timeout in ms (if null, the Timeout in the XML; if that is absent too, <see cref="Timeout"/>).</param>
    /// <exception cref="ArgumentException">The name does not exist, a value is out of range, or more than one value is given for FC5/FC6.</exception>
    public void NamedWrite(string name, IReadOnlyList<long> values, int? repeatCount = null, int? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        var table = namedWrites;
        var w = table.Get(name);
        var (r, t) = table.Resolve(w, repeatCount, timeout);
        switch (w.Func)
        {
            case ModbusFunction.BITWRITE_F5:
                ManualBitWrite_FC5(w.Id, w.Slave, w.Start, ModbusWriteConfig.Bit(ModbusWriteConfig.Single(values)), r, t); break;
            case ModbusFunction.WORDWRITE_F6:
                ManualWordWrite_FC6(w.Id, w.Slave, w.Start, ModbusWriteConfig.Word(ModbusWriteConfig.Single(values)), r, t); break;
            case ModbusFunction.MULTIBITWRITE_F15:
                ManualMultiBitWrite_FC15(w.Id, w.Slave, w.Start, ModbusWriteConfig.Bits(values), r, t); break;
            default:
                ManualMultiWordWrite_FC16(w.Id, w.Slave, w.Start, ModbusWriteConfig.Words(values), r, t); break;
        }
    }

    /// <summary>Checks whether a named write with the given name was loaded from XML. Names are case-sensitive.</summary>
    /// <param name="name">The Name of the &lt;Write&gt; element.</param>
    /// <returns>true if it exists.</returns>
    public bool ContainsNamedWrite(string name) => namedWrites.Contains(name);
    #endregion

    #region Config / Xml
    /// <summary>Builds a configuration object from the current communication settings, Auto list, and named writes. Manual jobs are not included.</summary>
    /// <returns>The configuration object.</returns>
    public ModbusRTUMasterConfig ToConfig() => new()
    {
        Port = Port,
        Baudrate = Baudrate,
        Parity = Parity,
        DataBits = DataBits,
        StopBits = StopBits,
        Timeout = Timeout,
        Interval = Interval,
        AutoReconnect = AutoReconnect,
        Autos = [.. AutoWorksSnapshot().Select(w => ModbusAutoConfig.FromWork(w.MessageID, w.Slave, w.Pdu, w.Timeout))],
        Writes = namedWrites.Snapshot(),
    };

    /// <summary>Applies the communication settings, Auto list, and named writes of the configuration object. The existing Auto list and named writes are replaced by those of the configuration.</summary>
    /// <remarks>The whole input is validated before it is applied, so if any single value is invalid, nothing is changed.</remarks>
    /// <param name="config">The configuration to apply.</param>
    /// <exception cref="InvalidOperationException">Called while communicating (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">A value is out of range (the message includes the element and attribute names).</exception>
    public void ApplyConfig(ModbusRTUMasterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (IsStart) throw new InvalidOperationException("통신 중에는 설정을 바꿀 수 없습니다. Stop() 후 호출하세요.");
        config.Validate();
        var works = new List<Work>();
        foreach (var a in config.Autos ?? [])
        {
            var (pdu, t) = a.Validate(1, 247);
            works.Add(new Work(a.Id, a.Slave, pdu, null, t));
        }
        var table = NamedWriteTable<ModbusWriteConfig>.Build(config.Writes, (w, label) => w.Validate(label, 0, 247));

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

    /// <summary>Builds an XML element from the current settings, Auto list, and named writes. Manual jobs are not included.</summary>
    /// <returns>The &lt;ModbusRTUMaster&gt; element.</returns>
    public XElement ToXml() => XmlConfig.Serialize(ToConfig());

    /// <summary>Loads the settings, Auto list, and named writes from an XML element. The existing Auto list and named writes are replaced by the XML content.</summary>
    /// <remarks>
    /// <para>Omitted attributes use their default values. An unknown attribute or element is an error (to catch typos).</para>
    /// <para>The whole input is validated before it is applied, so if any single value is invalid, nothing is changed.</para>
    /// <para>You can check the format as an XSD by passing <see cref="ModbusRTUMasterConfig"/> to <see cref="ConfigSchema.Get{T}"/>.</para>
    /// </remarks>
    /// <param name="element">The &lt;ModbusRTUMaster&gt; element.</param>
    /// <exception cref="InvalidOperationException">Called while communicating (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">The root name differs, an unknown attribute or element is present, or a value is invalid (the message includes the element and attribute names).</exception>
    public void FromXml(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (IsStart) throw new InvalidOperationException("통신 중에는 설정을 불러올 수 없습니다. Stop() 후 호출하세요.");
        ApplyConfig(XmlConfig.Deserialize<ModbusRTUMasterConfig>(element));
    }

    /// <summary>Saves the settings, Auto list, and named writes to an XML file.</summary>
    /// <param name="path">File path.</param>
    public void SaveXml(string path) => ToXml().Save(path);

    /// <summary>Loads the settings, Auto list, and named writes from an XML file. The rules are the same as <see cref="FromXml"/>.</summary>
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

    void ProcessWork(SerialPort sp, Work w)
    {
        try
        {
            int timeout = w.Timeout ?? Timeout;
            var request = ModbusRtuFrame.Build(w.Slave, w.Pdu);
            for (int attempt = 0; ; attempt++)
            {
                sp.DiscardInBuffer();
                sp.Write(request, 0, request.Length);
                if (w.Slave == 0) return; // 브로드캐스트는 응답 없음

                var frame = ReadResponse(sp, w, timeout);
                if (frame == null) Raise(TimeoutReceived, Result(w));
                else if (!ModbusCRC.Check(frame)) Raise(CRCErrorReceived, Result(w));
                else if (frame[0] == w.Slave && HandleResponse(w, frame.AsSpan(1, frame.Length - 3))) return;
                else Raise(TimeoutReceived, Result(w));

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

    static byte[]? ReadResponse(SerialPort sp, Work w, int timeout)
    {
        int expected = 1 + ModbusPdu.ExpectedResponseLength(w.Pdu) + 2;
        var buf = new byte[Math.Max(expected, 5)];
        int n = 0;
        var sw = Stopwatch.StartNew();
        while (n < expected)
        {
            int remain = timeout - (int)sw.ElapsedMilliseconds;
            if (remain <= 0) return null;
            sp.ReadTimeout = remain;
            try { n += sp.Read(buf, n, expected - n); }
            catch (TimeoutException) { return null; }
            if (n >= 2 && (buf[1] & 0x80) != 0) expected = 5; // 예외 응답: Slave, FC|0x80, Code, CRC 2
        }
        return buf.AsSpan(0, expected).ToArray();
    }

    static ResultEventArgs Result(Work w) => new(w.MessageID, w.Slave, w.Function, w.StartAddress);

    internal bool HandleResponse(Work w, ReadOnlySpan<byte> pdu)
    {
        if (ModbusPdu.IsException(w.Pdu, pdu))
        {
            Raise(ExceptionReceived, new ExceptionEventArgs(w.MessageID, w.Slave, w.Function, w.StartAddress, pdu[1]));
            return true;
        }
        if (!ModbusPdu.IsValidResponse(w.Pdu, pdu)) return false;

        var req = w.Pdu;
        switch (w.Function)
        {
            case ModbusFunction.BITREAD_F1:
            case ModbusFunction.BITREAD_F2:
                Raise(BitReadReceived, new BitReadEventArgs(w.MessageID, w.Slave, w.Function, w.StartAddress, ModbusPdu.ParseBits(req, pdu)));
                break;
            case ModbusFunction.WORDREAD_F3:
            case ModbusFunction.WORDREAD_F4:
                Raise(WordReadReceived, new WordReadEventArgs(w.MessageID, w.Slave, w.Function, w.StartAddress, ModbusPdu.ParseWords(pdu)));
                break;
            case ModbusFunction.BITWRITE_F5:
                Raise(BitWriteReceived, new BitWriteEventArgs(w.MessageID, w.Slave, w.Function, w.StartAddress, req[3] == 0xFF));
                break;
            case ModbusFunction.WORDWRITE_F6:
                Raise(WordWriteReceived, new WordWriteEventArgs(w.MessageID, w.Slave, w.Function, w.StartAddress, ModbusPdu.Word(req, 3)));
                break;
            case ModbusFunction.MULTIBITWRITE_F15:
                Raise(MultiBitWriteReceived, new MultiBitWriteEventArgs(w.MessageID, w.Slave, w.Function, w.StartAddress,
                    BitUtil.UnpackBits(req.AsSpan(6), ModbusPdu.Word(req, 3))));
                break;
            case ModbusFunction.MULTIWORDWRITE_F16:
                Raise(MultiWordWriteReceived, new MultiWordWriteEventArgs(w.MessageID, w.Slave, w.Function, w.StartAddress, ModbusPdu.Words(req.AsSpan(6))));
                break;
            case ModbusFunction.WORDBITSET_F26:
                Raise(WordBitSetReceived, new WordBitSetEventArgs(w.MessageID, w.Slave, w.Function, w.StartAddress, req[3], req[4] == 0xFF));
                break;
        }
        return true;
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
