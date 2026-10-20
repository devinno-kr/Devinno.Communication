using System.Xml.Linq;
using System.Diagnostics;
using System.Net.Sockets;
using Devinno.Communication.Internal;

namespace Devinno.Communication.Modbus.TCP;

/// <summary>
/// Modbus TCP master (client). Polls through Auto (polling) and Manual (one-shot) job queues.
/// </summary>
/// <remarks>
/// <para>Manual jobs are processed before Auto jobs. After one pass through the Auto list, polling starts again from the beginning.</para>
/// <para>Responses are matched to requests by transaction ID; a response that does not match is discarded.</para>
/// <para>The communication loop runs on a dedicated background thread, and all events are raised on that thread. To update the UI, marshal asynchronously, as with BeginInvoke (WinForms) or Dispatcher.BeginInvoke (WPF). A synchronous Invoke prolongs the wait when Stop() is called from the UI thread. Exceptions thrown in a handler are ignored and the loop continues.</para>
/// <para>Write safety: with <see cref="DiscardWritesWhileDisconnected"/> true (the default), a Manual write (FC5/FC6/FC15/FC16/FC26, including <see cref="NamedWrite(string, long, int?, int?)"/>) requested while there is no connection is not queued, and writes still queued when the connection is lost are discarded right after <see cref="DeviceClosed"/>; both are reported through <see cref="WriteDiscarded"/>, which may also be raised on the calling thread. <see cref="Stop"/> always discards pending Manual writes. Manual reads and Auto jobs are never discarded. Each write ends with exactly one of: its success event, <see cref="ExceptionReceived"/>, a <see cref="TimeoutReceived"/> with IsFinal true, or <see cref="WriteDiscarded"/> (jobs removed with <see cref="ClearManual"/> or <see cref="RemoveManual"/> are not reported).</para>
/// </remarks>
/// <example>
/// <code>
/// var mb = new ModbusTCPMaster { RemoteIP = "192.168.0.10", RemotePort = 502 };
/// mb.AutoWordRead_FC3(1, 1, 0, 10);
/// mb.WordReadReceived += (s, e) =&gt; { if (e.MessageID == 1) Console.WriteLine(string.Join(",", e.ReceiveData)); };
/// mb.DeviceOpened += (s, e) =&gt;
/// {
///     // writes are accepted only while connected (otherwise WriteDiscarded, NotConnected)
///     mb.ManualWordWrite_FC6(10, 1, 100, 1234);
///     mb.NamedWrite("Recipe", [1, 2, 3]);   // calls the XML &lt;Write Name="Recipe" Id="30" Func="FC16" Slave="1" Start="10" /&gt; by name
/// };
/// mb.Start();
/// </code>
/// </example>
public class ModbusTCPMaster : IDisposable
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
        // 연결이 끊긴 동안 버리는 대상(물리 동작이 있는 쓰기)
        public bool IsWrite => Function is ModbusFunction.BITWRITE_F5 or ModbusFunction.WORDWRITE_F6
            or ModbusFunction.MULTIBITWRITE_F15 or ModbusFunction.MULTIWORDWRITE_F16 or ModbusFunction.WORDBITSET_F26;
    }
    #endregion

    #region class : EventArgs
    /// <summary>Common event arguments for request result events.</summary>
    /// <param name="messageId">The message ID given at registration.</param>
    /// <param name="slave">Unit ID</param>
    /// <param name="function">Function code.</param>
    /// <param name="startAddress">Start address.</param>
    public class ResultEventArgs(int messageId, int slave, ModbusFunction function, int startAddress) : EventArgs
    {
        /// <summary>The message ID given at registration.</summary>
        public int MessageID { get; } = messageId;
        /// <summary>Unit ID</summary>
        public int Slave { get; } = slave;
        /// <summary>Function code.</summary>
        public ModbusFunction Function { get; } = function;
        /// <summary>Start address.</summary>
        public int StartAddress { get; } = startAddress;
    }

    /// <summary>Event arguments for a failed attempt (<see cref="TimeoutReceived"/>).</summary>
    /// <param name="messageId">The message ID given at registration.</param>
    /// <param name="slave">Unit ID</param>
    /// <param name="function">Function code.</param>
    /// <param name="startAddress">Start address.</param>
    /// <param name="attempt">Attempt number, starting at 1 (1 = first attempt, 2 = first retry, ...).</param>
    /// <param name="isFinal">Whether this job will not be attempted again (retries used up or the connection was lost).</param>
    /// <param name="requestSent">Whether the request of this attempt was handed to the send function.</param>
    public class AttemptFailedEventArgs(int messageId, int slave, ModbusFunction function, int startAddress, int attempt, bool isFinal, bool requestSent)
        : ResultEventArgs(messageId, slave, function, startAddress)
    {
        /// <summary>Attempt number, starting at 1 (1 = first attempt, 2 = first retry, ...).</summary>
        public int Attempt { get; } = attempt;
        /// <summary>Whether this job will not be attempted again (retries used up or the connection was lost).</summary>
        public bool IsFinal { get; } = isFinal;
        /// <summary>Whether the request of this attempt was handed to the send function. If false, the device did not receive this attempt; if true, the result is unknown and the device may have executed it.</summary>
        public bool RequestSent { get; } = requestSent;
    }

    /// <summary>Event arguments for a bit read (FC1/FC2) response.</summary>
    /// <param name="messageId">Message ID.</param>
    /// <param name="slave">Unit ID</param>
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
    /// <param name="slave">Unit ID</param>
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
    /// <param name="slave">Unit ID</param>
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
    /// <param name="slave">Unit ID</param>
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
    /// <param name="slave">Unit ID</param>
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
    /// <param name="slave">Unit ID</param>
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
    /// <param name="slave">Unit ID</param>
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
    /// <param name="slave">Unit ID</param>
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
    /// <summary>IP address of the slave to connect to (default: "127.0.0.1").</summary>
    public string RemoteIP { get; set; } = "127.0.0.1";
    /// <summary>Port of the slave to connect to (default: 502).</summary>
    public int RemotePort { get; set; } = 502;

    /// <summary>Response timeout in ms (default: 100). A per-job timeout, if given, takes precedence.</summary>
    public int Timeout { get; set; } = 100;
    /// <summary>Wait between jobs in ms (default: 10).</summary>
    public int Interval { get; set; } = 10;
    /// <summary>Whether to reconnect at 1-second intervals when the connection could not be made or was lost (default: true).</summary>
    public bool AutoReconnect { get; set; } = true;
    /// <summary>Whether to discard writes while there is no connection (default: true). When true, a Manual write or named write requested while not connected raises <see cref="WriteDiscarded"/> (NotConnected) instead of being queued, and writes still queued when the connection is lost are discarded (Disconnected). When false, they stay queued and are sent after the next connection. Manual reads and Auto jobs are not affected, and <see cref="Stop"/> discards pending writes regardless of this setting.</summary>
    public bool DiscardWritesWhileDisconnected { get; set; } = true;

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
    // 이름 있는 쓰기 표. NamedWrite는 실행 중에도 불리므로 통째로 바꾸는 참조(volatile)로 둔다. ApplyConfig에서만 교체한다.
    volatile NamedWriteTable<ModbusWriteConfig> namedWrites = NamedWriteTable<ModbusWriteConfig>.Empty;
    ushort transactionId;
    // 연결이 살아 있는지(workLock 보호). 설정이 true면 쓰기는 이것이 true일 때만 대기열에 들어간다
    bool linkUp;
    // 이번 시도의 요청을 송신 함수에 넘겼는지(통신 스레드 전용)
    bool requestSent;
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
    /// <summary>Raised when there is no response, the response is malformed, or the connection is lost during a request. Raised on every retry; IsFinal tells whether the job will be attempted again. If RequestSent is true, the device may have executed the request — read it back before resending.</summary>
    public event EventHandler<AttemptFailedEventArgs>? TimeoutReceived;
    /// <summary>Raised when a Manual write is discarded without being sent (see <see cref="DiscardWritesWhileDisconnected"/>). May be raised on the calling thread: NotConnected inside the Manual/NamedWrite call, Stopped inside Stop(); otherwise on the communication thread.</summary>
    public event EventHandler<WriteDiscardedEventArgs>? WriteDiscarded;
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
            thread = new Thread(() => Run(token)) { IsBackground = true, Name = nameof(ModbusTCPMaster) };
            thread.Start();
        }
    }

    /// <summary>Stops the communication loop and disconnects. Waits up to (Timeout + 1000) ms for the loop to end. Pending Manual writes are discarded (WriteDiscarded, Stopped).</summary>
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
        // 설정과 무관하게 남은 쓰기를 비운다(재시작 때 늦게 실행되지 않게)
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

    static void CheckSlave(int slave)
    {
        if (slave < 0 || slave > 255) throw new ArgumentOutOfRangeException(nameof(slave), "Unit ID는 0~255 입니다.");
    }

    void AddAuto(int id, int slave, byte[] pdu, int? timeout)
    {
        CheckSlave(slave);
        lock (workLock) autoWorks.Add(new Work(id, slave, pdu, null, timeout));
    }

    void AddManual(int id, int slave, byte[] pdu, int? repeatCount, int? timeout)
    {
        CheckSlave(slave);
        var w = new Work(id, slave, pdu, repeatCount, timeout);
        bool discard = false;
        lock (workLock)
        {
            if (w.IsWrite && DiscardWritesWhileDisconnected && !linkUp) discard = true;
            else manualWorks.Add(w);
        }
        if (discard) Raise(WriteDiscarded, new WriteDiscardedEventArgs(w.MessageID, WriteDiscardReason.NotConnected));
    }

    // 연결을 얻었다. sock을 공개하기 전에 부른다(IsOpen이 true면 이미 linkUp)
    internal void SetLinkUp()
    {
        lock (workLock) linkUp = true;
    }

    // 연결을 잃었다. DeviceClosed 다음에 부른다. stopping이면 Stop으로 닫힌 것
    internal void SetLinkDown(bool stopping)
    {
        lock (workLock) linkUp = false;
        if (DiscardWritesWhileDisconnected) DiscardWrites(stopping ? WriteDiscardReason.Stopped : WriteDiscardReason.Disconnected);
    }

    // 대기 중인 Manual 쓰기를 순서대로 꺼내(읽기는 남김) lock 밖에서 하나씩 알린다. 꺼내기는 workLock 안이라 쓰기 하나당 알림은 한 번
    internal void DiscardWrites(WriteDiscardReason reason)
    {
        List<Work> dropped;
        lock (workLock)
        {
            dropped = manualWorks.FindAll(x => x.IsWrite);
            if (dropped.Count == 0) return;
            manualWorks.RemoveAll(x => x.IsWrite);
        }
        foreach (var w in dropped) Raise(WriteDiscarded, new WriteDiscardedEventArgs(w.MessageID, reason));
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
    /// <param name="slave">Unit ID 0~255</param>
    /// <param name="startAddr">Start address, 0 to 65535.</param>
    /// <param name="length">Number of items to read, 1 to 2000.</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void AutoBitRead_FC1(int id, int slave, int startAddr, int length, int? timeout = null)
        => AddAuto(id, slave, ModbusPdu.ReadRequest(ModbusFunction.BITREAD_F1, startAddr, length), timeout);

    /// <summary>Registers an FC2 discrete input read in the Auto list.</summary>
    /// <param name="id">Message ID (the MessageID of the response event).</param>
    /// <param name="slave">Unit ID 0~255</param>
    /// <param name="startAddr">Start address, 0 to 65535.</param>
    /// <param name="length">Number of items to read, 1 to 2000.</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void AutoBitRead_FC2(int id, int slave, int startAddr, int length, int? timeout = null)
        => AddAuto(id, slave, ModbusPdu.ReadRequest(ModbusFunction.BITREAD_F2, startAddr, length), timeout);

    /// <summary>Registers an FC3 holding register read in the Auto list.</summary>
    /// <param name="id">Message ID (the MessageID of the response event).</param>
    /// <param name="slave">Unit ID 0~255</param>
    /// <param name="startAddr">Start address, 0 to 65535.</param>
    /// <param name="length">Number of items to read, 1 to 125.</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void AutoWordRead_FC3(int id, int slave, int startAddr, int length, int? timeout = null)
        => AddAuto(id, slave, ModbusPdu.ReadRequest(ModbusFunction.WORDREAD_F3, startAddr, length), timeout);

    /// <summary>Registers an FC4 input register read in the Auto list.</summary>
    /// <param name="id">Message ID (the MessageID of the response event).</param>
    /// <param name="slave">Unit ID 0~255</param>
    /// <param name="startAddr">Start address, 0 to 65535.</param>
    /// <param name="length">Number of items to read, 1 to 125.</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void AutoWordRead_FC4(int id, int slave, int startAddr, int length, int? timeout = null)
        => AddAuto(id, slave, ModbusPdu.ReadRequest(ModbusFunction.WORDREAD_F4, startAddr, length), timeout);
    #endregion

    #region Manual
    /// <summary>Registers a one-shot FC1 coil read.</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="slave">Unit ID 0~255</param>
    /// <param name="startAddr">Start address.</param>
    /// <param name="length">Number of items to read, 1 to 2000.</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualBitRead_FC1(int id, int slave, int startAddr, int length, int? repeatCount = null, int? timeout = null)
        => AddManual(id, slave, ModbusPdu.ReadRequest(ModbusFunction.BITREAD_F1, startAddr, length), repeatCount, timeout);

    /// <summary>Registers a one-shot FC2 discrete input read.</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="slave">Unit ID 0~255</param>
    /// <param name="startAddr">Start address.</param>
    /// <param name="length">Number of items to read, 1 to 2000.</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualBitRead_FC2(int id, int slave, int startAddr, int length, int? repeatCount = null, int? timeout = null)
        => AddManual(id, slave, ModbusPdu.ReadRequest(ModbusFunction.BITREAD_F2, startAddr, length), repeatCount, timeout);

    /// <summary>Registers a one-shot FC3 holding register read.</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="slave">Unit ID 0~255</param>
    /// <param name="startAddr">Start address.</param>
    /// <param name="length">Number of items to read, 1 to 125.</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualWordRead_FC3(int id, int slave, int startAddr, int length, int? repeatCount = null, int? timeout = null)
        => AddManual(id, slave, ModbusPdu.ReadRequest(ModbusFunction.WORDREAD_F3, startAddr, length), repeatCount, timeout);

    /// <summary>Registers a one-shot FC4 input register read.</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="slave">Unit ID 0~255</param>
    /// <param name="startAddr">Start address.</param>
    /// <param name="length">Number of items to read, 1 to 125.</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries).</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualWordRead_FC4(int id, int slave, int startAddr, int length, int? repeatCount = null, int? timeout = null)
        => AddManual(id, slave, ModbusPdu.ReadRequest(ModbusFunction.WORDREAD_F4, startAddr, length), repeatCount, timeout);

    /// <summary>Registers an FC5 write of a single coil.</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="slave">Unit ID 0~255</param>
    /// <param name="startAddr">Address.</param>
    /// <param name="value">The value to write.</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries). A retry resends a write the device may already have executed; do not use it for pulse, counter or incremental writes.</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualBitWrite_FC5(int id, int slave, int startAddr, bool value, int? repeatCount = null, int? timeout = null)
        => AddManual(id, slave, ModbusPdu.WriteSingleCoil(startAddr, value), repeatCount, timeout);

    /// <summary>Registers an FC6 write of a single register.</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="slave">Unit ID 0~255</param>
    /// <param name="startAddr">Address.</param>
    /// <param name="value">The value to write (only the low 16 bits are sent).</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries). A retry resends a write the device may already have executed; do not use it for pulse, counter or incremental writes.</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualWordWrite_FC6(int id, int slave, int startAddr, int value, int? repeatCount = null, int? timeout = null)
        => AddManual(id, slave, ModbusPdu.WriteSingleRegister(startAddr, value), repeatCount, timeout);

    /// <summary>Registers an FC15 write of multiple coils.</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="slave">Unit ID 0~255</param>
    /// <param name="startAddr">Start address.</param>
    /// <param name="values">The values to write (1 to 1968).</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries). A retry resends a write the device may already have executed; do not use it for pulse, counter or incremental writes.</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualMultiBitWrite_FC15(int id, int slave, int startAddr, bool[] values, int? repeatCount = null, int? timeout = null)
        => AddManual(id, slave, ModbusPdu.WriteMultipleCoils(startAddr, values), repeatCount, timeout);

    /// <summary>Registers an FC16 write of multiple registers.</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="slave">Unit ID 0~255</param>
    /// <param name="startAddr">Start address.</param>
    /// <param name="values">The values to write (1 to 123; only the low 16 bits of each are sent).</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries). A retry resends a write the device may already have executed; do not use it for pulse, counter or incremental writes.</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualMultiWordWrite_FC16(int id, int slave, int startAddr, int[] values, int? repeatCount = null, int? timeout = null)
        => AddManual(id, slave, ModbusPdu.WriteMultipleRegisters(startAddr, values), repeatCount, timeout);

    /// <summary>Registers an FC26 bit set within a register (Devinno extension function).</summary>
    /// <param name="id">Message ID.</param>
    /// <param name="slave">Unit ID 0~255</param>
    /// <param name="startAddr">Register address.</param>
    /// <param name="bitIndex">Bit index, 0 to 15.</param>
    /// <param name="value">The value to set.</param>
    /// <param name="repeatCount">Retry count when there is no response (null for no retries). A retry resends a write the device may already have executed; do not use it for pulse, counter or incremental writes.</param>
    /// <param name="timeout">Timeout of this job in ms (if null, <see cref="Timeout"/>).</param>
    public void ManualWordBitSet_FC26(int id, int slave, int startAddr, int bitIndex, bool value, int? repeatCount = null, int? timeout = null)
        => AddManual(id, slave, ModbusPdu.WordBitSet(startAddr, bitIndex, value), repeatCount, timeout);

    /// <summary>Registers a write of one value by the name defined in the XML &lt;Write&gt; element. FC5 takes 0/1 and FC6 takes -32768 to 65535; FC15/FC16 send it as a one-element array.</summary>
    /// <param name="name">The Name of the &lt;Write&gt; element.</param>
    /// <param name="value">The value to write (depending on the function: 0 or 1 for FC5/FC15, -32768 to 65535 for FC6/FC16).</param>
    /// <param name="repeatCount">Retry count (if null, the RepeatCount in the XML; if that is absent too, no retries). A retry resends a write the device may already have executed; do not use it for pulse, counter or incremental writes.</param>
    /// <param name="timeout">Timeout in ms (if null, the Timeout in the XML; if that is absent too, <see cref="Timeout"/>).</param>
    /// <exception cref="ArgumentException">The name does not exist, or a value is out of range.</exception>
    public void NamedWrite(string name, long value, int? repeatCount = null, int? timeout = null)
        => NamedWrite(name, [value], repeatCount, timeout);

    /// <summary>Registers a write of several values by the name defined in the XML &lt;Write&gt; element. FC5/FC6 accept only one value.</summary>
    /// <param name="name">The Name of the &lt;Write&gt; element.</param>
    /// <param name="values">The values to write (each 0 or 1 for FC15, each -32768 to 65535 for FC16).</param>
    /// <param name="repeatCount">Retry count (if null, the RepeatCount in the XML; if that is absent too, no retries). A retry resends a write the device may already have executed; do not use it for pulse, counter or incremental writes.</param>
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
    public ModbusTCPMasterConfig ToConfig() => new()
    {
        RemoteIP = RemoteIP,
        RemotePort = RemotePort,
        Timeout = Timeout,
        Interval = Interval,
        AutoReconnect = AutoReconnect,
        DiscardWritesWhileDisconnected = DiscardWritesWhileDisconnected,
        Autos = [.. AutoWorksSnapshot().Select(w => ModbusAutoConfig.FromWork(w.MessageID, w.Slave, w.Pdu, w.Timeout))],
        Writes = namedWrites.Snapshot(),
    };

    /// <summary>Applies the communication settings, Auto list, and named writes of the configuration object. The existing Auto list and named writes are replaced by those of the configuration.</summary>
    /// <remarks>The whole input is validated before it is applied, so if any single value is invalid, nothing is changed.</remarks>
    /// <param name="config">The configuration to apply.</param>
    /// <exception cref="InvalidOperationException">Called while communicating (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">A value is out of range (the message includes the element and attribute names).</exception>
    public void ApplyConfig(ModbusTCPMasterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (IsStart) throw new InvalidOperationException("통신 중에는 설정을 바꿀 수 없습니다. Stop() 후 호출하세요.");
        config.Validate();
        var works = new List<Work>();
        foreach (var a in config.Autos ?? [])
        {
            var (pdu, t) = a.Validate(0, 255);
            works.Add(new Work(a.Id, a.Slave, pdu, null, t));
        }
        var table = NamedWriteTable<ModbusWriteConfig>.Build(config.Writes, (w, label) => w.Validate(label, 0, 255));

        RemoteIP = config.RemoteIP;
        RemotePort = config.RemotePort;
        Timeout = config.Timeout;
        Interval = config.Interval;
        AutoReconnect = config.AutoReconnect;
        DiscardWritesWhileDisconnected = config.DiscardWritesWhileDisconnected;
        namedWrites = table;
        ReplaceAutoWorks(works);
    }

    /// <summary>Builds an XML element from the current settings, Auto list, and named writes. Manual jobs are not included.</summary>
    /// <returns>The &lt;ModbusTCPMaster&gt; element.</returns>
    public XElement ToXml() => XmlConfig.Serialize(ToConfig());

    /// <summary>Loads the settings, Auto list, and named writes from an XML element. The existing Auto list and named writes are replaced by the XML content.</summary>
    /// <remarks>
    /// <para>Omitted attributes use their default values. An unknown attribute or element is an error (to catch typos).</para>
    /// <para>The whole input is validated before it is applied, so if any single value is invalid, nothing is changed.</para>
    /// <para>You can check the format as an XSD by passing <see cref="ModbusTCPMasterConfig"/> to <see cref="ConfigSchema.Get{T}"/>.</para>
    /// </remarks>
    /// <param name="element">The &lt;ModbusTCPMaster&gt; element.</param>
    /// <exception cref="InvalidOperationException">Called while communicating (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">The root name differs, an unknown attribute or element is present, or a value is invalid (the message includes the element and attribute names).</exception>
    public void FromXml(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (IsStart) throw new InvalidOperationException("통신 중에는 설정을 불러올 수 없습니다. Stop() 후 호출하세요.");
        ApplyConfig(XmlConfig.Deserialize<ModbusTCPMasterConfig>(element));
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
                var s = TryConnect(token);
                if (s != null)
                {
                    SetLinkUp();
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

    void ProcessWork(Socket s, Work w)
    {
        int attempt = 0;
        try
        {
            int timeout = w.Timeout ?? Timeout;
            for (; ; attempt++)
            {
                requestSent = false;
                var pdu = Exchange(s, w, timeout);
                if (pdu != null && HandleResponse(w, pdu)) return;
                bool isFinal = attempt >= (w.RepeatCount ?? 0);
                Raise(TimeoutReceived, new AttemptFailedEventArgs(w.MessageID, w.Slave, w.Function, w.StartAddress, attempt + 1, isFinal, requestSent));
                if (isFinal) return;
            }
        }
        catch
        {
            // 연결 오류로 작업이 중단되면 조용히 사라지지 않게 알린 뒤(더 시도하지 않으므로 최종), 재접속 경로가 돌도록 다시 던진다
            Raise(TimeoutReceived, new AttemptFailedEventArgs(w.MessageID, w.Slave, w.Function, w.StartAddress, attempt + 1, true, requestSent));
            throw;
        }
    }

    // 요청을 보내고 Transaction ID가 맞는 응답 PDU를 돌려준다. 시간 안에 못 받으면 null.
    byte[]? Exchange(Socket s, Work w, int timeout)
    {
        int tid = unchecked(++transactionId);
        var request = ModbusTcpFrame.Build(tid, w.Slave, w.Pdu);
        Drain(s);
        s.Send(request);
        requestSent = true;

        var sw = Stopwatch.StartNew();
        while (true)
        {
            var header = ReceiveExact(s, 7, timeout, sw);
            if (header == null) return null;
            if (ModbusTcpFrame.FrameLength(header) is not int total || total < 0)
                throw new IOException("MBAP 헤더가 올바르지 않습니다.");   // 스트림 정렬이 깨짐 → 재접속
            var body = ReceiveExact(s, total - 7, timeout, sw);
            if (body == null) return null;
            if ((header[0] << 8 | header[1]) == tid && header[6] == w.Slave) return body;
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
