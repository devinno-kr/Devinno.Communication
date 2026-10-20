using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Hsms;
using Devinno.Communication.Secs.Serial;

namespace Devinno.Communication.Secs.Gem;

/// <summary>
/// GEM (SEMI E30) equipment side. The application only defines variables, events, alarms, and commands; the library handles the state machines and the replies to Host messages.
/// </summary>
/// <remarks>
/// <para><b>Creation and lifetime</b>: Takes one <see cref="SecsHsms"/> or <see cref="SecsSerial"/> and sets its Role to Equipment. GEM owns Start/Stop/Dispose of the transport, so do not call those on the transport object directly, and
/// do not subscribe to the transport's <c>PrimaryReceived</c> either (GEM replies to every primary message). Messages that GEM does not handle are registered with <see cref="RegisterMessage"/> and received through <see cref="MessageReceived"/>.
/// You may subscribe to the transport's <c>DeviceOpened</c>/<c>DeviceClosed</c> for connection notifications.</para>
/// <para><b>Definitions</b>: Register SV, DV, EC, CE, alarms, and remote commands before <see cref="Start"/> (XML can also be used). The library manages the values of the standard SV/EC/DV/CE (Clock, ControlState, EventsEnabled, AlarmsSet, EstablishCommunicationsTimeout, TimeFormat, AlarmID, EquipmentOffline, and so on); change their IDs with <see cref="StandardIds"/>.</para>
/// <para><b>State machines</b>: the communication state (<see cref="CommunicationState"/>: once connected, communication is established with S1F13, and on failure it is retried every EstablishCommunicationsTimeout seconds) and the control state (<see cref="ControlState"/>: while OFF-LINE, every W message except the Host's S1F13 and S1F17 is answered with SxF0).
/// Operator actions are <see cref="GoOnline"/>, <see cref="GoOffline"/>, <see cref="SwitchLocal"/>, and <see cref="SwitchRemote"/>.</para>
/// <para><b>Automatic replies</b>: S1F1/3/11/13/15/17/21/23, S2F13/17/29/31/33/35/37/39, S5F3/5/7, S6F15/19, and S10F3/5 are answered directly on the transport dispatcher without application code. An unknown Stream gets S9F3, an unknown Function gets S9F5, and a body structure error gets S9F7.
/// Replies decided by the application (S2F41 remote command, S2F15 EC change approval, registered messages) are sent when the application event handler returns, and if <see cref="CommandReplyTimeout"/> elapses GEM replies on its behalf.</para>
/// <para><b>Sending</b>: <see cref="TriggerEvent"/> (S6F11), <see cref="SetAlarm"/>/<see cref="ClearAlarm"/> (S5F1 + alarm CE), <see cref="SendTerminalMessage"/> (S10F1), and <see cref="RequestHostTime"/> (S2F17) capture their values at the moment of the call, put the message in the queue, and return immediately.
/// The GEM worker thread sends them one at a time in the order they were queued, and a failure or a nonzero ack is reported through <see cref="SendFailed"/>.</para>
/// <para><b>Threads</b>: Besides the transport threads, one GEM worker thread (send queue, timers, deadlines) and one application event thread are used. All public events are raised on the application event thread in the order they occurred, and exceptions thrown by handlers are ignored.
/// A slow handler does not block automatic replies. To update the UI, marshal with BeginInvoke (WinForms) / Dispatcher.BeginInvoke (WPF).</para>
/// <para>The E30 original has not been checked against, so do not use this as evidence for a GEM Compliance Statement. Spooling, trace, limits, recipes (S7), and S2F49 are not supported.</para>
/// </remarks>
/// <example>
/// <code>
/// using Devinno.Communication.Secs.Data;
/// using Devinno.Communication.Secs.Gem;
/// using Devinno.Communication.Secs.Hsms;
///
/// var hsms = new SecsHsms { Mode = HsmsMode.Passive, LocalPort = 5000, DeviceID = 1 };
/// var eq = new GemEquipment(hsms) { ModelName = "DV-100", SoftwareRevision = "1.0.0" };
/// eq.AddStatusVariable(new GemStatusVariable(2001, "Temperature", SecsFormat.F4, "C"));
/// eq.AddDataVariable(new GemDataVariable(3001, "LotID", SecsFormat.ASCII));
/// eq.AddCollectionEvent(new GemCollectionEvent(5001, "ProcessStarted", [2001, 3001]));
/// eq.AddAlarm(new GemAlarm(1, "Over temperature", 2, 9001, 9002));
/// eq.AddRemoteCommand(new GemRemoteCommand("START", [new GemCommandParameter("LOTID", SecsFormat.ASCII)]));
/// eq.RemoteCommandReceived += (s, e) =&gt;
/// {
///     // A long-running job is answered with "accepted, will finish later" (4) and run separately; TriggerEvent reports when it finishes
///     e.Hcack = GemAck.HcackAcceptedLater;
///     var lot = e.Parameters.First(p =&gt; p.Name == "LOTID").Value.GetString();
///     Task.Run(() =&gt; eq.TriggerEvent(5001, new Dictionary&lt;GemId, SecsItem&gt; { [3001] = SecsItem.A(lot) }));
/// };
/// eq.Start();
///
/// eq.SetValue(2001, SecsItem.F4(36.5f));   // update the value
/// eq.SetAlarm(1);                           // alarm occurs → S5F1 + CE 9001
/// </code>
/// </example>
public partial class GemEquipment : IDisposable
{
    #region class : Outgoing / Deferred
    // GEM이 보내는 1차 메시지 하나와 완료 처리(보통 디스패처에서 와이어 순서로 부름)
    sealed class Outgoing(SecsMessage message, Action<Outgoing, Task<SecsMessage?>> done)
    {
        public SecsMessage Message { get; } = message;
        public Action<Outgoing, Task<SecsMessage?>> Done { get; } = done;
        public int Epoch;                   // gate: 보낸 연결의 세대(linkEpoch). 지난 연결의 늦은 완료를 가려낸다
    }

    // 앱이 정해야 하는 응답(리모트 명령·EC 승인·등록 메시지). 앱 결과와 기한 응답 중 먼저 온 것 하나만 보낸다. Run = 받은 실행의 번호
    sealed class Deferred(SecsPrimaryEventArgs args, long deadline, SecsMessage timeoutReply, string what, int run)
    {
        int claimed;
        public SecsPrimaryEventArgs Args { get; } = args;
        public int Run { get; } = run;
        public long Deadline { get; } = deadline;
        public SecsMessage TimeoutReply { get; } = timeoutReply;
        public string What { get; } = what;
        // 응답할 권리를 한 번만 얻는다
        public bool TryClaim() => Interlocked.Exchange(ref claimed, 1) == 0;
    }
    #endregion

    #region Fields
    const int StopWait = 3000;
    const int S1F13 = 1 * 256 + 13, S1F17 = 1 * 256 + 17;
    readonly GemTransport transport;
    readonly object lifeLock = new();
    // GEM 상태 lock 하나. 이 안에서는 전송 호출·이벤트 핸들러 실행을 하지 않는다(이벤트는 앱 이벤트 스레드 대기열에 넣기만 함)
    readonly object gate = new();
    readonly GemDefinitionSet definitions = new();
    // 받은 1차 메시지 처리기(Stream * 256 + Function). 생성자에서 기능별 partial 파일이 채운다
    readonly Dictionary<int, Action<SecsPrimaryEventArgs>> handlers = [];
    readonly EventHandler<SecsPrimaryEventArgs> onPrimary;
    readonly EventHandler onOpened, onClosed;
    GemCatalog catalog = null!;         // Start 때 만듦(실행 중 읽기 전용)
    GemLoop? worker, app;               // 작업 스레드, 앱 이벤트 스레드
    GemLoop? lastApp;                   // gate: 마지막으로 시작한 앱 이벤트 스레드(Stop 뒤에도 버린 이벤트 수를 읽음)
    bool running;                       // gate: Start ~ Stop
    int run;                            // gate: 실행 번호(Start마다 1 늘림). 앱 스레드에서 늦게 돌아온 이전 실행의 결과를 가려낸다
    readonly Queue<Outgoing> sendQueue = new();   // gate
    Outgoing? inFlight;                 // gate: 응답을 기다리는 GEM 송신(한 번에 하나)
    // gate: GEM이 아는 링크 상태와 연결 세대. 디스패처의 DeviceOpened/DeviceClosed(와이어 순서)로만 바뀐다(전송 IsOpen 스냅숏은 읽지 않음)
    bool linkUp;
    int linkEpoch;
    readonly List<Deferred> deferreds = [];       // gate
    // 변수 값(앱 SV·DV·EC와 표준 EC·DV). 표준 SV는 값을 저장하지 않고 상태에서 계산한다
    readonly Dictionary<GemId, SecsItem> values = [];    // gate
    // 표준 SV의 현재 값 계산(gate 안에서 부름). 생성자에서 기능별 partial 파일이 채운다(없으면 정의의 처음 값)
    readonly Dictionary<GemStandard, Func<SecsItem>> standardValues = [];
    GemCommunicationState commState = GemCommunicationState.NotCommunicating;   // gate
    GemControlState control = GemControlState.HostOffline;                      // gate
    GemLocalRemote localRemote = GemLocalRemote.Remote;                         // gate

    string modelName = "", softwareRevision = "";
    SecsFormat vidFormat = SecsFormat.U4, ceidFormat = SecsFormat.U4, rptidFormat = SecsFormat.U4, alidFormat = SecsFormat.U4, dataidFormat = SecsFormat.U4;
    int commandReplyTimeout = 10000, maxPendingSends = 1000, maxPendingEvents = 10000;
    int dispatcherSendCount;            // 디스패처 스레드에서 Pump가 송신을 시작한 횟수(테스트 확인용, 0이어야 함)
    #endregion

    #region Extension points
    // 기능별 partial 파일(GemEquipment.*.cs)이 구현하는 연결점. 구현이 없는 동안은 컴파일러가 호출을 지운다.
    partial void AddCommunicationHandlers();
    partial void AddControlHandlers();
    partial void AddDataHandlers();
    partial void AddReportHandlers();
    partial void AddAlarmHandlers();
    partial void AddConstantHandlers();
    partial void AddCommandHandlers();
    partial void AddClockHandlers();
    // Start 때 상태 초기화(gate 안, 정의 표가 만들어진 뒤, 전송 시작 전)
    partial void ResetCommunication();
    partial void ResetControl();
    partial void ResetReports();
    partial void ResetAlarms();
    partial void ResetClock();
    // 전송 DeviceOpened/DeviceClosed (디스패처 스레드)
    partial void OnTransportOpened();
    partial void OnTransportClosed();
    // 작업 스레드 타이머(약 20ms마다)
    partial void OnWorkerTick(long now);
    // GEM이 보낸 1차 메시지가 T3로 끝남 = 통신 실패(송신 완료, 보통 디스패처)
    partial void OnCommunicationFailure();
    // NOT COMMUNICATING에서 S1F13이 아닌 1차 메시지를 받음(디스패처, 전이 8)
    partial void OnNotCommunicatingPrimary();
    // 등록 메시지를 받음(디스패처, 제어 상태 검사 뒤)
    partial void HandleRegistered(SecsPrimaryEventArgs e);
    #endregion

    #region Constructor
    /// <summary>Creates a GEM equipment over an HSMS transport. Sets the transport's Role to Equipment.</summary>
    /// <param name="transport">HSMS transport (not yet started). GEM owns its lifetime (Start/Stop/Dispose).</param>
    /// <exception cref="ArgumentNullException">transport is null</exception>
    /// <exception cref="InvalidOperationException">The transport has already been started</exception>
    public GemEquipment(SecsHsms transport) : this(GemTransport.Of(Ready(transport))) { }

    /// <summary>Creates a GEM equipment over a SECS-I transport. Sets the transport's Role to Equipment.</summary>
    /// <param name="transport">SECS-I transport (not yet started). GEM owns its lifetime (Start/Stop/Dispose).</param>
    /// <exception cref="ArgumentNullException">transport is null</exception>
    /// <exception cref="InvalidOperationException">The transport has already been started</exception>
    public GemEquipment(SecsSerial transport) : this(GemTransport.Of(Ready(transport))) { }

    internal GemEquipment(GemTransport transport)
    {
        this.transport = transport;
        onPrimary = OnPrimary;
        onOpened = (_, _) => { if (IsRunning) OnTransportOpened(); };
        onClosed = (_, _) => { if (IsRunning) OnTransportClosed(); };
        AddCommunicationHandlers();
        AddControlHandlers();
        AddDataHandlers();
        AddReportHandlers();
        AddAlarmHandlers();
        AddConstantHandlers();
        AddCommandHandlers();
        AddClockHandlers();
        transport.Attach(onPrimary, onOpened, onClosed);
    }

    static SecsHsms Ready(SecsHsms t)
    {
        ArgumentNullException.ThrowIfNull(t);
        if (t.IsStart) throw new InvalidOperationException("전송 객체가 이미 시작되어 있습니다. GemEquipment.Start()가 전송을 시작합니다.");
        t.Role = SecsRole.Equipment;
        return t;
    }

    static SecsSerial Ready(SecsSerial t)
    {
        ArgumentNullException.ThrowIfNull(t);
        if (t.IsStart) throw new InvalidOperationException("전송 객체가 이미 시작되어 있습니다. GemEquipment.Start()가 전송을 시작합니다.");
        t.Role = SecsRole.Equipment;
        return t;
    }
    #endregion

    #region Properties
    /// <summary>The transport object received by the constructor (<see cref="SecsHsms"/> or <see cref="SecsSerial"/>)</summary>
    /// <remarks>Change the communication settings (port, timers) on this object. Do not call <c>Start</c>/<c>Stop</c> on it directly; use GEM's.</remarks>
    public object Transport => transport.Target;
    /// <summary>Gets whether it has been started</summary>
    public bool IsStart { get; private set; }
    /// <summary>Gets whether it has been disposed</summary>
    public bool IsDisposed { get; private set; }
    /// <summary>Gets whether the transport is connected (Selected for HSMS, port open for SECS-I)</summary>
    /// <remarks>A snapshot at that moment, not ordered with the events. Judge the communication state with <see cref="CommunicationState"/> (<see cref="CommunicationStateChanged"/>).</remarks>
    public bool IsOpen => transport.IsOpen();
    /// <summary>User-defined tag</summary>
    public object? Tag { get; set; }

    /// <summary>Gets the current communication state</summary>
    public GemCommunicationState CommunicationState { get { lock (gate) return commState; } }
    /// <summary>Gets the current control state</summary>
    public GemControlState ControlState { get { lock (gate) return control; } }
    /// <summary>Gets the position of the operator's LOCAL/REMOTE switch</summary>
    public GemLocalRemote LocalRemoteSwitch { get { lock (gate) return localRemote; } }

    /// <summary>Equipment model name MDLN (ASCII, 0 to 20 characters, default: ""). It is carried in S1F2, S1F13, and S1F14.</summary>
    /// <exception cref="ArgumentException">Longer than 20 characters or not ASCII</exception>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public string ModelName { get => modelName; set => Setting(() => modelName = Text20(value, nameof(ModelName))); }
    /// <summary>Software revision SOFTREV (ASCII, 0 to 20 characters, default: "")</summary>
    /// <exception cref="ArgumentException">Longer than 20 characters or not ASCII</exception>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public string SoftwareRevision { get => softwareRevision; set => Setting(() => softwareRevision = Text20(value, nameof(SoftwareRevision))); }

    /// <summary>Format used to send SVID, ECID, and DVID: U1 U2 U4 U8 I1 I2 I4 I8 A (default: U4). When receiving, any of the eight integer formats is accepted for a numeric ID.</summary>
    /// <exception cref="ArgumentException">Not an ID format</exception>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public SecsFormat VidFormat { get => vidFormat; set => Setting(() => vidFormat = GemItems.CheckIdFormat(value, nameof(VidFormat))); }
    /// <summary>CEID format (default: U4). The rules are the same as for <see cref="VidFormat"/>.</summary>
    /// <exception cref="ArgumentException">Not an ID format</exception>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public SecsFormat CeidFormat { get => ceidFormat; set => Setting(() => ceidFormat = GemItems.CheckIdFormat(value, nameof(CeidFormat))); }
    /// <summary>RPTID format (default: U4). The RPTID defined by the Host is carried in S6F11 in this format.</summary>
    /// <exception cref="ArgumentException">Not an ID format</exception>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public SecsFormat RptidFormat { get => rptidFormat; set => Setting(() => rptidFormat = GemItems.CheckIdFormat(value, nameof(RptidFormat))); }
    /// <summary>ALID format (default: U4)</summary>
    /// <exception cref="ArgumentException">Not an ID format</exception>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public SecsFormat AlidFormat { get => alidFormat; set => Setting(() => alidFormat = GemItems.CheckIdFormat(value, nameof(AlidFormat))); }
    /// <summary>S6F11 DATAID format (default: U4). DATAID increases from 1 at each Start and wraps back to 1 when it exceeds the maximum of the format.</summary>
    /// <exception cref="ArgumentException">Not an ID format</exception>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public SecsFormat DataidFormat { get => dataidFormat; set => Setting(() => dataidFormat = GemItems.CheckIdFormat(value, nameof(DataidFormat))); }

    /// <summary>Deadline in ms for replies the application must decide (remote command, EC change approval, registered messages) (default: 10000). XML accepts only 1000 to 110000.</summary>
    /// <remarks>If the handler does not return within this time, GEM replies on its behalf (S2F41 HCACK 2, S2F15 EAC 2, SxF0 for registered messages). Keep it well below the Host's T3 (usually 45 seconds).</remarks>
    /// <exception cref="ArgumentOutOfRangeException">Less than 1</exception>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public int CommandReplyTimeout { get => commandReplyTimeout; set => Setting(() => commandReplyTimeout = Check(value, 1, int.MaxValue, nameof(CommandReplyTimeout))); }
    /// <summary>Upper limit of the GEM send queue, 1 to 100000 (default: 1000). When exceeded, new messages are discarded and <see cref="SendFailed"/> (QueueFull) is raised</summary>
    /// <exception cref="ArgumentOutOfRangeException">Out of range</exception>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public int MaxPendingSends { get => maxPendingSends; set => Setting(() => maxPendingSends = Check(value, 1, 100000, nameof(MaxPendingSends))); }

    /// <summary>Upper limit of the application event queue, 1 to 1000000 (default: 10000). If handlers stall and this many events have accumulated without being raised, the oldest are discarded and <see cref="DroppedEventCount"/> is increased.</summary>
    /// <remarks>If a discarded event is the handler of a reply the application decides (remote command, EC approval, registered message), GEM replies on its behalf at <see cref="CommandReplyTimeout"/>. Automatic replies are unrelated to this queue.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">Out of range</exception>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public int MaxPendingEvents { get => maxPendingEvents; set => Setting(() => maxPendingEvents = Check(value, 1, 1000000, nameof(MaxPendingEvents))); }
    /// <summary>Gets the number of events discarded because the application event queue was full (counted from 0 at each Start; the last value can still be read after Stop)</summary>
    public long DroppedEventCount { get { lock (gate) return lastApp?.Dropped ?? 0; } }

    /// <summary>IDs of the standard variables and events (a null property uses the default ID). Read at <see cref="Start"/>.</summary>
    public GemStandardIds StandardIds { get; } = new();

    bool IsRunning { get { lock (gate) return running; } }

    // 디스패처 스레드에서 송신을 시작한 횟수(GEM은 디스패처에서 자기 1차 메시지를 보내지 않으므로 늘 0, 테스트 확인용)
    internal int DispatcherSendCount => Interlocked.CompareExchange(ref dispatcherSendCount, 0, 0);

    // GEM이 링크를 연결로 아는지(테스트가 DeviceOpened 처리를 기다릴 때)
    internal bool LinkUp { get { lock (gate) return linkUp; } }

    void Setting(Action set)
    {
        lock (lifeLock)
        {
            NotRunning();
            set();
        }
    }

    void NotRunning()
    {
        if (IsStart) throw new InvalidOperationException("실행 중에는 바꿀 수 없습니다. Stop() 후 바꾸세요.");
    }

    static string Text20(string value, string name)
    {
        GemDefinitionCheck.Ascii(value, name);
        if (value.Length > 20) throw new ArgumentException($"{name}는 20자 이하입니다.", name);
        return value;
    }

    static int Check(int value, int min, int max, string name)
        => value < min || value > max ? throw new ArgumentOutOfRangeException(name, value, $"{name}는 {min}~{max} 입니다.") : value;
    #endregion

    #region Definitions
    /// <summary>Defines a status variable (SV). Can be called only before <see cref="Start"/>.</summary>
    /// <param name="variable">Definition</param>
    /// <exception cref="ArgumentNullException">variable is null</exception>
    /// <exception cref="ArgumentException">The same VID already exists (SV, DV, and EC share one ID space)</exception>
    /// <exception cref="InvalidOperationException">Running</exception>
    public void AddStatusVariable(GemStatusVariable variable) => Setting(() => definitions.Add(variable));
    /// <summary>Defines a data variable (DV). Can be called only before <see cref="Start"/>.</summary>
    /// <param name="variable">Definition</param>
    /// <exception cref="ArgumentNullException">variable is null</exception>
    /// <exception cref="ArgumentException">The same VID already exists</exception>
    /// <exception cref="InvalidOperationException">Running</exception>
    public void AddDataVariable(GemDataVariable variable) => Setting(() => definitions.Add(variable));
    /// <summary>Defines an equipment constant (EC). Can be called only before <see cref="Start"/>.</summary>
    /// <param name="constant">Definition</param>
    /// <exception cref="ArgumentNullException">constant is null</exception>
    /// <exception cref="ArgumentException">The same VID already exists</exception>
    /// <exception cref="InvalidOperationException">Running</exception>
    public void AddEquipmentConstant(GemEquipmentConstant constant) => Setting(() => definitions.Add(constant));
    /// <summary>Defines a collection event (CE). Can be called only before <see cref="Start"/>.</summary>
    /// <param name="collectionEvent">Definition</param>
    /// <exception cref="ArgumentNullException">collectionEvent is null</exception>
    /// <exception cref="ArgumentException">The same CEID already exists</exception>
    /// <exception cref="InvalidOperationException">Running</exception>
    public void AddCollectionEvent(GemCollectionEvent collectionEvent) => Setting(() => definitions.Add(collectionEvent));
    /// <summary>Defines an alarm (the Set/Clear CE are defined automatically by GEM). Can be called only before <see cref="Start"/>.</summary>
    /// <param name="alarm">Definition</param>
    /// <exception cref="ArgumentNullException">alarm is null</exception>
    /// <exception cref="ArgumentException">The same ALID already exists</exception>
    /// <exception cref="InvalidOperationException">Running</exception>
    public void AddAlarm(GemAlarm alarm) => Setting(() => definitions.Add(alarm));
    /// <summary>Defines a remote command (S2F41). Can be called only before <see cref="Start"/>.</summary>
    /// <param name="command">Definition</param>
    /// <exception cref="ArgumentNullException">command is null</exception>
    /// <exception cref="ArgumentException">The same name already exists</exception>
    /// <exception cref="InvalidOperationException">Running</exception>
    public void AddRemoteCommand(GemRemoteCommand command) => Setting(() => definitions.Add(command));
    /// <summary>Registers a Host primary message that GEM does not handle so that the application receives it (for example, S7F1 recipe). When it arrives, <c>MessageReceived</c> is raised.</summary>
    /// <param name="stream">Stream, 1 to 127 (excluding 9)</param>
    /// <param name="function">Function (odd, 1 to 255)</param>
    /// <exception cref="ArgumentOutOfRangeException">Stream is out of range</exception>
    /// <exception cref="ArgumentException">An even Function, S9, a message that GEM handles, or an already registered message</exception>
    /// <exception cref="InvalidOperationException">Running</exception>
    public void RegisterMessage(int stream, int function) => Setting(() => definitions.AddMessage(stream, function));
    /// <summary>Clears all definitions (variables, events, alarms, commands, registered messages). Can be called only before <see cref="Start"/>.</summary>
    /// <exception cref="InvalidOperationException">Running</exception>
    public void ClearDefinitions() => Setting(definitions.Clear);

    /// <summary>Gets the defined status variables (a copy, excluding standard SVs)</summary>
    public IReadOnlyList<GemStatusVariable> StatusVariables { get { lock (lifeLock) return [.. definitions.StatusVariables]; } }
    /// <summary>Gets the defined data variables (a copy, excluding standard DVs)</summary>
    public IReadOnlyList<GemDataVariable> DataVariables { get { lock (lifeLock) return [.. definitions.DataVariables]; } }
    /// <summary>Gets the defined equipment constants (a copy, excluding standard ECs)</summary>
    public IReadOnlyList<GemEquipmentConstant> EquipmentConstants { get { lock (lifeLock) return [.. definitions.Constants]; } }
    /// <summary>Gets the defined collection events (a copy, excluding standard and alarm CEs)</summary>
    public IReadOnlyList<GemCollectionEvent> CollectionEvents { get { lock (lifeLock) return [.. definitions.Events]; } }
    /// <summary>Gets the defined alarms (a copy)</summary>
    public IReadOnlyList<GemAlarm> Alarms { get { lock (lifeLock) return [.. definitions.Alarms]; } }
    /// <summary>Gets the defined remote commands (a copy)</summary>
    public IReadOnlyList<GemRemoteCommand> RemoteCommands { get { lock (lifeLock) return [.. definitions.Commands]; } }
    /// <summary>Gets the registered messages (a copy)</summary>
    public IReadOnlyList<(int Stream, int Function)> RegisteredMessages { get { lock (lifeLock) return [.. definitions.Messages]; } }
    #endregion

    #region Events
    /// <summary>Raised on the application event thread when a message sent by GEM (S1F1, S2F17, S5F1, S6F11, S10F1) failed or was discarded without being sent.</summary>
    /// <remarks>
    /// <para>Order of the notifications: on a disconnection or <see cref="DisableCommunication"/>, first this event for each discarded message (NotCommunicating or Disabled) in the original send order (the one waiting for a reply first, then the queue from the front; the equipment's S1F13 is not reported),
    /// then <see cref="CommunicationStateChanged"/>, then the transition 4 <see cref="ControlStateChanged"/> if it was ATTEMPT ON-LINE.
    /// On T3 of S5F1, S6F11, S10F1, or S2F17: this event (Timeout), this event (NotCommunicating) for the rest of the queue, <see cref="CommunicationStateChanged"/>, then the transition 4 <see cref="ControlStateChanged"/> if it was ATTEMPT ON-LINE.
    /// On T3 of the S1F1 of <see cref="GoOnline"/>: this event (Timeout), <see cref="ControlStateChanged"/> (to <see cref="OnlineFailedState"/>), this event (NotCommunicating) for the rest of the queue, then <see cref="CommunicationStateChanged"/>.
    /// A failure of the equipment's S1F13 is reported only through <see cref="ProtocolError"/>. So inside this handler, <see cref="CommunicationState"/> may still be Communicating.</para>
    /// <para>A disconnection does not end a GEM message with Disconnected (it is reported as NotCommunicating as above); Disconnected remains only for a SECS-I send that exceeded the retry limit (the line is kept).</para>
    /// </remarks>
    public event EventHandler<GemSendFailedEventArgs>? SendFailed;
    /// <summary>Raised on the application event thread for diagnostic notifications (S9 sent, body structure error, deadline exceeded, overlapping reply, communication establishment failure, and so on). It does not affect operation.</summary>
    public event EventHandler<GemProtocolErrorEventArgs>? ProtocolError;
    #endregion

    #region Start / Stop
    /// <summary>Checks the definitions, initializes the state, and then starts the GEM threads and the transport. Does nothing if already started.</summary>
    /// <remarks>The state (values, reports, links, enable flags, alarms, clock) returns to its initial values at every Start (persistence is planned for later).</remarks>
    /// <exception cref="ObjectDisposedException">Disposed</exception>
    /// <exception cref="InvalidOperationException">A definition is wrong (ID overlap, format range, unknown VID reference) or the transport has already been started. The transport is not started.</exception>
    public void Start()
    {
        lock (lifeLock)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (IsStart) return;
            if (transport.IsStart()) throw new InvalidOperationException("전송 객체가 이미 시작되어 있습니다. 전송의 Start/Stop은 GEM이 맡습니다.");
            var c = GemCatalog.Build(definitions, StandardIds, vidFormat, ceidFormat, rptidFormat, alidFormat, dataidFormat);
            var w = new GemLoop(nameof(GemEquipment) + ".Worker", WorkerTick);
            var a = new GemLoop(nameof(GemEquipment) + ".Events", maxPending: maxPendingEvents);
            lock (gate)
            {
                catalog = c;
                worker = w;
                app = lastApp = a;
                running = true;
                run++;
                sendQueue.Clear();
                inFlight = null;
                linkUp = false;
                deferreds.Clear();
                values.Clear();
                foreach (var v in c.Vars.Values)
                    if (v.Kind != GemVarKind.Status || v.Standard == null) values[v.Key] = v.Initial;
                commState = GemCommunicationState.NotCommunicating;
                ResetCommunication();
                ResetControl();
                ResetReports();
                ResetAlarms();
                ResetClock();
            }
            w.Start();
            a.Start();
            IsStart = true;
            try { transport.Start(); }
            catch
            {
                StopLocked(out var w2, out var a2);
                w2?.Stop(StopWait);
                a2?.Stop(StopWait);
                throw;
            }
        }
    }

    /// <summary>Stops the transport and the GEM threads. Messages that could not be sent and events not yet raised are discarded. It may be called several times.</summary>
    /// <remarks>Waits up to 3 seconds for each thread to finish. It may also be called from inside an event handler (that thread is not waited for).</remarks>
    public void Stop()
    {
        GemLoop? w, a;
        lock (lifeLock)
        {
            if (!IsStart) return;
            StopLocked(out w, out a);          // running = false를 먼저: 아래 전송 Stop의 DeviceClosed·송신 실패가 대기열을 SendFailed로 알리지 않는다(알림 없이 버림)
            transport.Stop();
        }
        w?.Stop(StopWait);
        a?.Stop(StopWait);
    }

    void StopLocked(out GemLoop? w, out GemLoop? a)
    {
        IsStart = false;
        lock (gate)
        {
            running = false;
            sendQueue.Clear();
            inFlight = null;
            deferreds.Clear();
            w = worker;
            a = app;
            worker = null;
            app = null;
        }
    }

    /// <summary>Stops and also releases the transport object. It may be called several times.</summary>
    public void Dispose()
    {
        lock (lifeLock)
        {
            if (IsDisposed) return;
            IsDisposed = true;
        }
        Stop();
        transport.Detach(onPrimary, onOpened, onClosed);
        transport.Dispose();
        GC.SuppressFinalize(this);
    }

    // 실행 중이어야 하는 메서드의 검사
    void Started()
    {
        if (!IsStart) throw new InvalidOperationException("Start() 후에 부를 수 있습니다.");
    }
    #endregion

    #region Receive (디스패처 스레드: 자동 응답만, 앱 코드 없음)
    void OnPrimary(object? sender, SecsPrimaryEventArgs e)
    {
        var m = e.Message;
        GemCommunicationState comm;
        GemControlState ctl;
        GemCatalog c;
        lock (gate)
        {
            if (!running) return;
            comm = commState;
            ctl = control;
            c = catalog;
        }
        int key = m.Stream * 256 + m.Function;
        // ① DISABLED: 아무것도 해석하지 않는다(W면 전송 계층이 SxF0)
        if (comm == GemCommunicationState.Disabled) return;
        if (comm == GemCommunicationState.NotCommunicating && key != S1F13) OnNotCommunicatingPrimary();
        // Host가 보낸 S9는 응답·S9 없이 알리기만 한다(응답 고리 방지)
        if (m.Stream == 9)
        {
            e.Defer();
            Error($"Host가 S9F{m.Function}을(를) 보냈습니다(무시함): {GemItems.Sml(m)}");
            return;
        }
        // ② 지원하지 않는 Stream → S9F3, 지원 Stream의 모르는 Function → S9F5 (상태와 무관)
        bool registered = c.Messages.Contains(key);
        if (!c.Streams.Contains(m.Stream)) { SendS9(e, 3, $"지원하지 않는 Stream: {GemItems.Sml(m)}"); return; }
        if (!registered && !handlers.ContainsKey(key)) { SendS9(e, 5, $"모르는 Function: {GemItems.Sml(m)}"); return; }
        // ③ NOT COMMUNICATING: S1F13만 처리 ④ OFF-LINE: S1F13·S1F17만 처리. 나머지 W는 SxF0
        if (comm != GemCommunicationState.Communicating && key != S1F13) return;
        if (IsOffline(ctl) && key != S1F13 && key != S1F17) return;
        // ⑤ 처리 표. 본문 구조가 틀리면(FormatException) S9F7을 보내고 응답하지 않는다
        try
        {
            if (registered) HandleRegistered(e);
            else handlers[key](e);
        }
        catch (FormatException ex) { SendS9(e, 7, $"S{m.Stream}F{m.Function} 본문 구조 오류: {ex.Message}"); }
        catch (Exception ex) { Error($"S{m.Stream}F{m.Function} 처리 중 오류: {ex.Message}"); }
    }

    static bool IsOffline(GemControlState s) => s is GemControlState.EquipmentOffline or GemControlState.AttemptOnline or GemControlState.HostOffline;

    // 자동 응답(W비트일 때만). 실패는 ProtocolError
    void Respond(SecsPrimaryEventArgs e, SecsItem? body)
    {
        if (!e.Message.ReplyExpected) return;
        ReplyNow(e, new SecsMessage(e.Message.Stream, e.Message.Function + 1, false, body), $"S{e.Message.Stream}F{e.Message.Function}");
    }

    void ReplyNow(SecsPrimaryEventArgs e, SecsMessage reply, string what)
    {
        try { e.Reply(reply); }
        catch (Exception ex) { Error($"{what}의 응답을 보내지 못했습니다: {ex.Message}"); }
    }

    // S9F3/F5/F7: MHEAD = 받은 헤더 그대로. 응답은 하지 않는다(미룸 표시로 자동 SxF0도 막음). 송신은 시작만 한다
    void SendS9(SecsPrimaryEventArgs e, int function, string reason)
    {
        e.Defer();
        var s9 = new SecsMessage(9, function, false, SecsItem.B(e.ReceivedHeader));
        try
        {
            transport.SendAsync(s9, CancellationToken.None).ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully) Error($"S9F{function}을(를) 보내지 못했습니다: {t.Exception?.InnerException?.Message}");
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (Exception ex) { Error($"S9F{function}을(를) 보내지 못했습니다: {ex.Message}"); }
        Error($"S9F{function}을(를) 보냈습니다: {reason}");
    }
    #endregion

    #region Deferred reply (앱이 정하는 응답)
    // 응답을 미루고 기한(CommandReplyTimeout)을 건다. 디스패처에서 부른다
    Deferred DeferReply(SecsPrimaryEventArgs e, SecsMessage timeoutReply, string what)
    {
        e.Defer();
        lock (gate)
        {
            var d = new Deferred(e, Environment.TickCount64 + commandReplyTimeout, timeoutReply, what, run);
            deferreds.Add(d);
            return d;
        }
    }

    // 앱 핸들러가 돌아온 뒤(앱 이벤트 스레드): 기한 전이면 beforeReply(결과 적용)를 하고 응답한 뒤 true, 이미 기한 응답이 나갔으면 결과를 버리고 false.
    // 그 사이 Stop(→ Start)했으면 이전 실행의 결과이므로 응답·적용·알림 없이 버린다
    bool FinishDeferred(Deferred d, SecsMessage reply, Action? beforeReply = null)
    {
        lock (gate)
        {
            deferreds.Remove(d);
            if (!running || run != d.Run) return false;
        }
        if (!d.TryClaim())
        {
            ErrorIn(d.Run, $"{d.What}: 기한(CommandReplyTimeout {commandReplyTimeout}ms)이 지난 뒤 돌아온 앱의 결과를 버렸습니다.");
            return false;
        }
        beforeReply?.Invoke();
        ReplyNow(d.Args, reply, d.What);
        return true;
    }

    // 기한이 지난 미룬 응답을 대신 보낸다(작업 스레드)
    void CheckDeadlines(long now)
    {
        List<Deferred>? expired = null;
        lock (gate)
        {
            foreach (var d in deferreds)
                if (d.Deadline <= now) (expired ??= []).Add(d);
            if (expired != null) foreach (var d in expired) deferreds.Remove(d);
        }
        if (expired == null) return;
        foreach (var d in expired)
        {
            if (!d.TryClaim()) continue;
            ReplyNow(d.Args, d.TimeoutReply, d.What);
            Error($"{d.What}: 앱이 CommandReplyTimeout({commandReplyTimeout}ms) 안에 응답하지 않아 대신 응답했습니다: {d.TimeoutReply.ToSml()}");
        }
    }
    #endregion

    #region Send queue (작업 스레드에서 한 번에 하나씩, 넣은 순서대로)
    void WorkerTick()
    {
        long now = Environment.TickCount64;
        CheckDeadlines(now);
        OnWorkerTick(now);
        Pump();
    }

    // 송신 대기열에 넣는다(gate 안). 가득 차면 버림 경로. 넣었으면 true
    bool EnqueueLocked(SecsMessage message, Action<Outgoing, Task<SecsMessage?>>? done = null)
    {
        if (sendQueue.Count >= maxPendingSends)
        {
            DiscardLocked(message, GemSendFailure.QueueFull);
            return false;
        }
        sendQueue.Enqueue(new Outgoing(message, done ?? AckDone));
        worker?.Post(Pump);
        return true;
    }

    // 버림 경로(스풀링 훅 자리): 보내지 못하는 GEM 1차 메시지는 모두 여기로 온다(gate 안). 지금은 SendFailed만 발생시킨다.
    // 나중의 스풀링은 여기서 "S1이 아니고 스풀 대상 Stream/Function이면 저장"을 한다.
    void DiscardLocked(SecsMessage message, GemSendFailure reason) => Raise(SendFailed, new GemSendFailedEventArgs(message, reason, null, null));

    // 대기열과 응답을 기다리는 메시지를 모두 버린다(gate 안). 응답을 기다리던 메시지의 결과는 나중에 와도 무시한다
    void ClearQueueLocked(GemSendFailure reason)
    {
        if (inFlight != null && !IsS1F13(inFlight.Message)) DiscardLocked(inFlight.Message, reason);
        inFlight = null;
        while (sendQueue.Count > 0)
        {
            var m = sendQueue.Dequeue().Message;
            if (!IsS1F13(m)) DiscardLocked(m, reason);   // 장비의 S1F13은 내부 시도라 알리지 않는다
        }
    }

    static bool IsS1F13(SecsMessage m) => m.Stream == 1 && m.Function == 13;

    // 다음 GEM 송신을 시작한다(작업 스레드). GEM이 링크 없음으로 알면 보내지 않고 DeviceOpened를 기다린다
    void Pump()
    {
        Outgoing o;
        lock (gate)
        {
            if (!running || !linkUp || inFlight != null || sendQueue.Count == 0) return;
            o = inFlight = sendQueue.Dequeue();
            o.Epoch = linkEpoch;
        }
        if (transport.IsDispatcherThread()) Interlocked.Increment(ref dispatcherSendCount);
        // 완료는 디스패처에서 와이어 순서로 정확히 한 번(디스패처가 멈춰 있으면 완료시킨 스레드에서). 동기 예외면 완료가 오지 않으므로 여기서 끝낸다
        try { transport.SendOrdered(o.Message, t => Completed(o, t)); }
        catch (Exception ex) { Completed(o, Task.FromException<SecsMessage?>(ex)); }   // 연결 안 됨(InvalidOperationException) 등
    }

    // 송신 완료(보통 디스패처, 와이어 순서). gate만 잡고 전송을 부르지 않는다(전송 lifeLock 안에서 불릴 수 있음). 다음 송신은 작업 스레드로 넘긴다
    void Completed(Outgoing o, Task<SecsMessage?> t)
    {
        bool s1f13 = IsS1F13(o.Message);
        GemLoop? w;
        lock (gate)
        {
            if (inFlight != o) return;                 // 이미 버린 메시지(통신 끔·통신 실패·끊김·Stop/Start)
            if (o.Epoch != linkEpoch) return;          // 지난 연결의 늦은 완료(그 연결의 DeviceClosed가 이미 정리함)
            // 링크가 끊겨 끝난 송신은 아무 상태도 바꾸지 않는다: 그 연결의 DeviceClosed가 전이 14(또는 WAIT CRA 정리)로 이 메시지와 대기열을 끝낸다
            if (t.Exception?.InnerException is { } ex && (ex is SecsDisconnectedException || SecsSession.IsNotConnected(ex))) return;
            inFlight = null;
            w = worker;
        }
        o.Done(o, t);
        // T3만 통신 실패(장비 S1F13의 T3는 NOT COMMUNICATING 안의 실패라 제외)
        if (t.Exception?.InnerException is SecsTimeoutException && !s1f13) OnCommunicationFailure();
        w?.Post(Pump);
    }

    // 기본 완료 처리(S5F1·S6F11·S10F1): 실패·0이 아닌 ack는 SendFailed
    void AckDone(Outgoing o, Task<SecsMessage?> t)
    {
        if (!t.IsCompletedSuccessfully)
        {
            ReportFailure(o.Message, t.Exception!.InnerException!);
            return;
        }
        if (t.Result == null) return;                  // W비트 없음
        byte ack;
        try { ack = GemItems.ReadAck(t.Result.Item); }
        catch (FormatException ex)
        {
            Raise(SendFailed, new GemSendFailedEventArgs(o.Message, GemSendFailure.InvalidReply, null, ex));
            return;
        }
        if (ack != 0) Raise(SendFailed, new GemSendFailedEventArgs(o.Message, GemSendFailure.NotAcknowledged, ack, null));
    }

    void ReportFailure(SecsMessage message, Exception ex)
    {
        var reason = ex switch
        {
            SecsTimeoutException => GemSendFailure.Timeout,
            SecsAbortException => GemSendFailure.Aborted,
            SecsErrorException => GemSendFailure.Error,
            _ => GemSendFailure.Disconnected,
        };
        Raise(SendFailed, new GemSendFailedEventArgs(message, reason, null, ex));
    }
    #endregion

    #region Raise (앱 이벤트 스레드)
    // 앱 이벤트 스레드 대기열에 넣는다(넣은 순서대로 발생, 핸들러 예외는 삼킴). gate 안에서 불러도 된다
    void Raise<T>(EventHandler<T>? handler, T args)
    {
        if (handler == null) return;
        Volatile.Read(ref app)?.Post(() => { try { handler(this, args); } catch (Exception) { } });
    }

    void Error(string reason) => Raise(ProtocolError, new GemProtocolErrorEventArgs(reason));

    // 실행 번호 r이 아직 지금 실행일 때만 알린다(앱 스레드에서 늦게 돌아온 이전 실행의 알림이 새 실행으로 가지 않게)
    void ErrorIn(int r, string reason)
    {
        lock (gate)
        {
            if (running && run == r) Error(reason);
        }
    }

    int CurrentRun { get { lock (gate) return run; } }

    // 앱 이벤트 스레드에서 일을 한다(미룬 응답 핸들러). 멈춰 있으면 버린다
    void PostApp(Action action) => Volatile.Read(ref app)?.Post(action);
    #endregion
}
