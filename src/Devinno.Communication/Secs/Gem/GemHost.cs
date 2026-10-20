using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Hsms;
using Devinno.Communication.Secs.Serial;

namespace Devinno.Communication.Secs.Gem;

/// <summary>
/// The Host side of GEM (SEMI E30). It replies automatically to the primary messages the equipment sends, and uses command methods to query or instruct the equipment.
/// </summary>
/// <remarks>
/// <para><b>Creation and lifetime</b>: it takes a <see cref="SecsHsms"/> or a <see cref="SecsSerial"/> and sets its Role to Host. GemHost owns the transport Start/Stop/Dispose, so do not call those on the transport object directly,
/// and do not subscribe to the transport <c>PrimaryReceived</c> either (GemHost replies to every primary message). Messages that GemHost does not handle are delivered through <see cref="MessageReceived"/>.</para>
/// <para><b>Automatic replies</b>: S1F1 (&lt;L&gt;), S1F13 (COMMACK 0), S2F17 (Host time), S6F5 (GRANT 0), S6F11 (ACKC6 0 → <see cref="EventReceived"/>), S5F1 (ACKC5 0 → <see cref="AlarmReceived"/>),
/// and S10F1 (ACKC10 → <see cref="TerminalReceived"/>) are answered directly on the transport dispatcher thread, without any application code. An S9Fx sent by the equipment gets no reply and is only reported through <see cref="ProtocolError"/>.</para>
/// <para><b>Connect sequence</b>: on every connection (<see cref="AutoConnectSequence"/>), it sends S1F13 (retried every <see cref="EstablishCommunicationRetry"/> seconds if it fails) → S1F17 → (S2F31) → report setup → S5F3 in order, and reports the result through <see cref="ConnectSequenceCompleted"/>.
/// If you declare with <see cref="Subscribe"/> that "when this collection event occurs, I want these VID values", the sequence defines, links, and enables the reports on the equipment, and interprets the equipment S6F11 with that definition and delivers the values per VID.</para>
/// <para><b>Command methods</b>: each comes as a synchronous (<c>ReadStatus</c>) and asynchronous (<c>ReadStatusAsync</c>) pair. A nonzero ack (EAC, HCACK, DRACK, and so on) is a return value, not an exception,
/// and a communication failure is thrown as an exception (<see cref="SecsTimeoutException"/>, <see cref="SecsAbortException"/> — usually this one if the equipment is OFF-LINE, <see cref="SecsErrorException"/>, <see cref="SecsDisconnectedException"/>); a malformed reply is a <see cref="FormatException"/>.
/// A synchronous method waits for the reply on the calling thread using the transport synchronous <c>Send</c>, so it does not rely on the thread pool or a SynchronizationContext. It is not delayed even if the thread pool is exhausted (ASP.NET and so on), and
/// it does not deadlock when called from the UI thread or from inside a GemHost event handler (that thread is blocked in the meantime). An Async method resumes the reply on the thread pool and does not return to the calling context (ConfigureAwait(false)).
/// Note that calling a synchronous method from the transport <c>DeviceOpened</c> handler (the dispatcher thread) delays the handling of the equipment other primary messages until the reply arrives.
/// The order between a command method's return or exception and the app events (<see cref="CommunicationStateChanged"/> and so on) is not guaranteed (they run on different threads).</para>
/// <para><b>ID formats</b>: when sending, it uses <see cref="VidFormat"/>, <see cref="CeidFormat"/>, <see cref="RptidFormat"/>, <see cref="AlidFormat"/>, and <see cref="DataidFormat"/>; IDs sent by the equipment are accepted in any of the eight integer formats or A.</para>
/// <para><b>Threads</b>: besides the transport threads, it uses one GEM worker thread (reply deadlines, starting the connect sequence) and one app event thread. All public events are raised on the app event thread in order of occurrence, and handler exceptions are ignored.
/// Even if a handler takes a long time, automatic replies are not blocked (when <see cref="MaxPendingEvents"/> events pile up, the oldest are dropped). Marshal UI updates with BeginInvoke (WinForms) or Dispatcher.BeginInvoke (WPF).</para>
/// <para>Periodic SV reading (polling) is not included. On sites that do not use events, call <see cref="ReadStatusAsync"/> from an app timer (see the example).
/// For messages for which GemHost has no helper (S7 recipes and so on), send directly with <c>SendAsync</c> of <see cref="Transport"/>.</para>
/// </remarks>
/// <example>
/// <code>
/// using Devinno.Communication.Secs.Data;
/// using Devinno.Communication.Secs.Gem;
/// using Devinno.Communication.Secs.Hsms;
///
/// var hsms = new SecsHsms { Mode = HsmsMode.Active, RemoteIP = "192.168.0.50", RemotePort = 5000, DeviceID = 1 };
/// var host = new GemHost(hsms);
/// host.Subscribe(5001, 2001, 3001);                    // when CE 5001 occurs, receive the values of SV 2001 and DV 3001
/// host.ConnectSequenceCompleted += (s, e) =&gt; Console.WriteLine(e.Success ? "Ready" : $"Connect failed: {e.FailedStep} {e.Ack} {e.Exception?.Message}");
/// host.EventReceived += (s, e) =&gt; Console.WriteLine($"CE {e.Report.Ceid}: temperature {e.Report.GetValue(2001)?.GetDouble()}");
/// host.AlarmReceived += (s, e) =&gt; Console.WriteLine($"Alarm {e.Alid} {(e.IsSet ? "set" : "cleared")}: {e.Text}");
/// host.Start();                                        // once connected: S1F13 → S1F17 → report setup → enable alarms
///
/// // Commands (when the equipment is ON-LINE REMOTE)
/// var result = await host.RemoteCommandAsync("START", [("LOTID", SecsItem.A("LOT-1"))]);
/// if (!result.IsAccepted) Console.WriteLine($"Rejected: HCACK {result.Hcack}");
/// byte eac = await host.WriteConstantsAsync([(4001, SecsItem.F4(120))]);
///
/// // On a site without events: read the SVs periodically with an app timer
/// using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
/// while (await timer.WaitForNextTickAsync())
///     if (host.IsCommunicating) Console.WriteLine((await host.ReadStatusAsync([2001]))[0].GetDouble());
/// </code>
/// </example>
public partial class GemHost : IDisposable
{
    #region class : Deferred
    // 앱이 정해야 하는 응답(MessageReceived). 앱의 응답과 기한 응답(SxF0) 중 먼저 온 것 하나만 보낸다. Run = 받은 실행의 번호
    sealed class Deferred(SecsPrimaryEventArgs args, long deadline, int run, string what)
    {
        int claimed;
        public SecsPrimaryEventArgs Args { get; } = args;
        public long Deadline { get; } = deadline;
        public int Run { get; } = run;
        public string What { get; } = what;
        // 응답할 권리를 한 번만 얻는다
        public bool TryClaim() => Interlocked.Exchange(ref claimed, 1) == 0;
    }
    #endregion

    #region Fields
    const int StopWait = 3000;
    const int S1F1 = 1 * 256 + 1, S1F13 = 1 * 256 + 13, S2F17 = 2 * 256 + 17, S6F5 = 6 * 256 + 5;
    readonly GemTransport transport;
    readonly object lifeLock = new();
    // 상태 lock 하나. 이 안에서는 전송 호출·이벤트 핸들러 실행을 하지 않는다(이벤트는 앱 이벤트 스레드 대기열에 넣기만 함)
    readonly object gate = new();
    // 받은 1차 메시지 처리기(Stream * 256 + Function). 생성자에서 기능별 partial 파일이 채운다. 없는 것은 MessageReceived
    readonly Dictionary<int, Action<SecsPrimaryEventArgs>> handlers = [];
    readonly EventHandler<SecsPrimaryEventArgs> onPrimary;
    readonly EventHandler onOpened, onClosed;
    GemLoop? worker, app;               // gate: 작업 스레드(응답 기한), 앱 이벤트 스레드
    GemLoop? lastApp;                   // gate: 마지막으로 시작한 앱 이벤트 스레드(Stop 뒤에도 버린 이벤트 수를 읽음)
    bool running;                       // gate: Start ~ Stop
    int run;                            // gate: 실행 번호(Start마다 1 늘림). 앱 스레드에서 늦게 돌아온 이전 실행의 결과를 가려낸다
    readonly List<Deferred> deferreds = [];       // gate
    bool communicating;                 // gate
    bool linkUp;                        // gate: GEM이 아는 연결 상태(디스패처의 DeviceOpened/DeviceClosed로 바뀜)
    int linkEpoch;                      // gate: 연결 세대(열림·닫힘마다 1 늘림). 끊긴 연결의 늦은 S1F14를 가려낸다
    GemConnectResult? pendingSequenceResult;   // gate: 자동 시퀀스의 끊김 실패(그 연결이 아직 열려 있을 때). DeviceClosed가 상태 이벤트 뒤에 알린다
    (int Epoch, SecsItem? Model)? pendingEstablish;   // gate: GEM이 DeviceOpened를 처리하기 전에 온 그 연결의 S1F14(COMMACK 0). 그 DeviceOpened가 적용한다
    string modelName = "", softwareRevision = "";   // gate: 장비가 알려 준 MDLN·SOFTREV

    SecsFormat vidFormat = SecsFormat.U4, ceidFormat = SecsFormat.U4, rptidFormat = SecsFormat.U4, alidFormat = SecsFormat.U4, dataidFormat = SecsFormat.U4;
    int timeFormat = 1, commandReplyTimeout = 10000, maxPendingEvents = 10000;
    #endregion

    #region Extension points
    // 기능별 partial 파일(GemHost.*.cs)이 구현하는 연결점. 구현이 없는 동안은 컴파일러가 호출을 지운다.
    // 장비가 보내는 보고(S5F1·S6F11·S10F1) 처리기 등록(생성자)
    partial void AddEventHandlers();
    // Start 때 상태 초기화(gate 안, 전송 시작 전)
    partial void ResetReports();
    // 전송 DeviceOpened/DeviceClosed (디스패처 스레드, 실행 중일 때만)
    partial void OnTransportOpened(int epoch);
    partial void OnTransportClosed();
    // 통신 수립 여부가 바뀜(gate 안). Start 때도 false로 부른다
    partial void OnCommunicatingChangedLocked(bool value);
    // 멈춤(gate 밖, running = false 뒤)
    partial void OnStopped();
    #endregion

    #region Constructor
    /// <summary>Creates a GEM Host over an HSMS transport. Sets the transport Role to Host.</summary>
    /// <param name="transport">The HSMS transport (not yet started). GemHost owns its lifetime (Start/Stop/Dispose).</param>
    /// <exception cref="ArgumentNullException"><paramref name="transport"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The transport has already been started.</exception>
    public GemHost(SecsHsms transport) : this(GemTransport.Of(Ready(transport))) { }

    /// <summary>Creates a GEM Host over a SECS-I transport. Sets the transport Role to Host.</summary>
    /// <param name="transport">The SECS-I transport (not yet started). GemHost owns its lifetime (Start/Stop/Dispose).</param>
    /// <exception cref="ArgumentNullException"><paramref name="transport"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The transport has already been started.</exception>
    public GemHost(SecsSerial transport) : this(GemTransport.Of(Ready(transport))) { }

    GemHost(GemTransport transport)
    {
        this.transport = transport;
        onPrimary = OnPrimary;
        onOpened = (_, _) =>
        {
            int epoch;
            lock (gate)
            {
                if (!running) return;
                // DeviceClosed 없이 다시 열림: 남은 시퀀스 실패는 새 연결을 다루기 전에 알린다
                if (pendingSequenceResult is { } stale)
                {
                    pendingSequenceResult = null;
                    Raise(ConnectSequenceCompleted, stale);
                }
                linkUp = true;
                epoch = ++linkEpoch;
                // 이 연결에서 보낸 S1F13의 응답이 먼저 처리됐으면 이제 수립을 반영한다
                if (pendingEstablish is { } est)
                {
                    pendingEstablish = null;
                    if (est.Epoch == linkEpoch)
                    {
                        StoreModel(est.Model);
                        SetCommunicatingLocked(true);
                    }
                }
            }
            OnTransportOpened(epoch);
        };
        onClosed = (_, _) =>
        {
            lock (gate)
            {
                if (!running) return;
                linkUp = false;
                ++linkEpoch;
                pendingEstablish = null;
                SetCommunicatingLocked(false);
                // 보류된 자동 시퀀스 실패는 CommunicationStateChanged 다음에(H2)
                if (pendingSequenceResult is { } pending)
                {
                    pendingSequenceResult = null;
                    Raise(ConnectSequenceCompleted, pending);
                }
            }
            OnTransportClosed();
        };
        handlers[S1F1] = e => Respond(e, SecsItem.L());
        handlers[S1F13] = OnS1F13;
        handlers[S2F17] = e => Respond(e, SecsItem.A(GemTime.Format(DateTime.Now, timeFormat)));
        handlers[S6F5] = e => Respond(e, GemItems.Ack(GemAck.GrantGranted));
        AddEventHandlers();
        transport.Attach(onPrimary, onOpened, onClosed);
    }

    static SecsHsms Ready(SecsHsms t)
    {
        ArgumentNullException.ThrowIfNull(t);
        if (t.IsStart) throw new InvalidOperationException("전송 객체가 이미 시작되어 있습니다. GemHost.Start()가 전송을 시작합니다.");
        t.Role = SecsRole.Host;
        return t;
    }

    static SecsSerial Ready(SecsSerial t)
    {
        ArgumentNullException.ThrowIfNull(t);
        if (t.IsStart) throw new InvalidOperationException("전송 객체가 이미 시작되어 있습니다. GemHost.Start()가 전송을 시작합니다.");
        t.Role = SecsRole.Host;
        return t;
    }
    #endregion

    #region Properties
    /// <summary>Gets the transport object passed to the constructor (<see cref="SecsHsms"/> or <see cref="SecsSerial"/>).</summary>
    /// <remarks>Change the communication settings (IP, port, timers) on this object. Do not call its <c>Start</c>/<c>Stop</c> directly; use the GemHost ones.
    /// For messages for which GemHost has no helper (for example, S7 recipes), send directly with <c>Send</c>/<c>SendAsync</c> of this object.</remarks>
    public object Transport => transport.Target;
    /// <summary>Gets a value indicating whether it has been started.</summary>
    public bool IsStart { get; private set; }
    /// <summary>Gets a value indicating whether it has been disposed.</summary>
    public bool IsDisposed { get; private set; }
    /// <summary>Gets a value indicating whether the transport is connected (Selected for HSMS, port open for SECS-I).</summary>
    /// <remarks>A snapshot at that moment, not ordered with the events. Judge the communication state with <see cref="IsCommunicating"/> (<see cref="CommunicationStateChanged"/>).</remarks>
    public bool IsOpen => transport.IsOpen();
    /// <summary>Gets or sets a user-defined tag.</summary>
    public object? Tag { get; set; }

    /// <summary>Gets a value indicating whether communications with the equipment are established. It becomes true when S1F13/S1F14 completes with COMMACK 0 (whichever side sent it), and false when the connection drops.</summary>
    public bool IsCommunicating { get { lock (gate) return communicating; } }
    /// <summary>Gets the model name MDLN reported by the equipment (received in S1F13, S1F14, or S1F2; "" at Start).</summary>
    public string ModelName { get { lock (gate) return modelName; } }
    /// <summary>Gets the software revision SOFTREV reported by the equipment (received in S1F13, S1F14, or S1F2; "" at Start).</summary>
    public string SoftwareRevision { get { lock (gate) return softwareRevision; } }

    /// <summary>Gets or sets the format used to send SVID, ECID, and DVID (VID): U1, U2, U4, U8, I1, I2, I4, I8, or A (default: U4). IDs sent by the equipment are accepted regardless of format.</summary>
    /// <exception cref="ArgumentException">The value is not an ID format.</exception>
    /// <exception cref="InvalidOperationException">Changed while running.</exception>
    public SecsFormat VidFormat { get => vidFormat; set => Setting(() => vidFormat = GemItems.CheckIdFormat(value, nameof(VidFormat))); }
    /// <summary>Gets or sets the format used to send CEID (default: U4). The rules are the same as for <see cref="VidFormat"/>.</summary>
    /// <exception cref="ArgumentException">The value is not an ID format.</exception>
    /// <exception cref="InvalidOperationException">Changed while running.</exception>
    public SecsFormat CeidFormat { get => ceidFormat; set => Setting(() => ceidFormat = GemItems.CheckIdFormat(value, nameof(CeidFormat))); }
    /// <summary>Gets or sets the format used to send RPTID (default: U4).</summary>
    /// <exception cref="ArgumentException">The value is not an ID format.</exception>
    /// <exception cref="InvalidOperationException">Changed while running.</exception>
    public SecsFormat RptidFormat { get => rptidFormat; set => Setting(() => rptidFormat = GemItems.CheckIdFormat(value, nameof(RptidFormat))); }
    /// <summary>Gets or sets the format used to send ALID (default: U4).</summary>
    /// <exception cref="ArgumentException">The value is not an ID format.</exception>
    /// <exception cref="InvalidOperationException">Changed while running.</exception>
    public SecsFormat AlidFormat { get => alidFormat; set => Setting(() => alidFormat = GemItems.CheckIdFormat(value, nameof(AlidFormat))); }
    /// <summary>Gets or sets the DATAID format placed in S2F33 and S2F35 (default: U4). DATAID increments from 1 at every Start.</summary>
    /// <exception cref="ArgumentException">The value is not an ID format.</exception>
    /// <exception cref="InvalidOperationException">Changed while running.</exception>
    public SecsFormat DataidFormat { get => dataidFormat; set => Setting(() => dataidFormat = GemItems.CheckIdFormat(value, nameof(DataidFormat))); }

    /// <summary>Gets or sets the format of the time the Host sends (the S2F18 reply, S2F31), 0 to 2 (default: 1).</summary>
    /// <remarks>0 = YYMMDDhhmmss (12 characters), 1 = YYYYMMDDhhmmsscc (16 characters, cc = 1/100 second), 2 = YYYY-MM-DDThh:mm:ss.ss+09:00. Times sent by the equipment are accepted in all three formats regardless of this setting.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is out of range.</exception>
    /// <exception cref="InvalidOperationException">Changed while running.</exception>
    public int TimeFormat { get => timeFormat; set => Setting(() => timeFormat = Check(value, 0, 2, nameof(TimeFormat))); }

    /// <summary>Gets or sets the deadline in ms, 1000 to 110000 (default: 10000, the same range as XML), by which a <see cref="MessageReceived"/> handler must reply.</summary>
    /// <remarks>If the handler neither replies nor returns within this time, GemHost replies with SxF0 on its behalf. Keep it comfortably shorter than the equipment T3 (usually 45 seconds).</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is out of range.</exception>
    /// <exception cref="InvalidOperationException">Changed while running.</exception>
    public int CommandReplyTimeout { get => commandReplyTimeout; set => Setting(() => commandReplyTimeout = Check(value, 1000, 110000, nameof(CommandReplyTimeout))); }

    // 테스트 전용: 범위 검사 없이 기한을 정한다(짧은 기한으로 빠르게 시험)
    internal int CommandReplyTimeoutForTest { set => Setting(() => commandReplyTimeout = value); }

    /// <summary>Gets or sets the upper limit of the app event queue, 1 to 1000000 (default: 10000). When this many events that have not yet been raised pile up because a handler is stuck, the oldest are dropped and <see cref="DroppedEventCount"/> is incremented.</summary>
    /// <remarks>If a dropped event is a <see cref="MessageReceived"/>, GemHost replies with SxF0 on its behalf after <see cref="CommandReplyTimeout"/>. Automatic replies are independent of this queue.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is out of range.</exception>
    /// <exception cref="InvalidOperationException">Changed while running.</exception>
    public int MaxPendingEvents { get => maxPendingEvents; set => Setting(() => maxPendingEvents = Check(value, 1, 1000000, nameof(MaxPendingEvents))); }
    /// <summary>Gets the number of events dropped because the app event queue was full (counted from 0 at each Start; the last value can still be read after Stop).</summary>
    public long DroppedEventCount { get { lock (gate) return lastApp?.Dropped ?? 0; } }

    bool IsRunning { get { lock (gate) return running; } }

    // 테스트 전용: GEM이 연결을 아는가(디스패처가 DeviceOpened를 처리한 뒤 true, DeviceClosed 뒤 false)
    internal bool LinkUp { get { lock (gate) return linkUp; } }

    void Setting(Action set)
    {
        lock (lifeLock)
        {
            if (IsStart) throw new InvalidOperationException("실행 중에는 바꿀 수 없습니다. Stop() 후 바꾸세요.");
            set();
        }
    }

    static int Check(int value, int min, int max, string name)
        => value < min || value > max ? throw new ArgumentOutOfRangeException(name, value, $"{name}는 {min}~{max} 입니다.") : value;
    #endregion

    #region Events
    /// <summary>Raised on the app event thread when whether communications are established (<see cref="IsCommunicating"/>) changes (NotCommunicating ↔ Communicating; Disabled is not used).</summary>
    /// <remarks>Once GemHost has handled the transport <c>DeviceClosed</c> of a connection, Communicating is not raised for that connection, and <see cref="IsCommunicating"/> does not become true for it.</remarks>
    public event EventHandler<GemCommunicationStateEventArgs>? CommunicationStateChanged;

    /// <summary>Raised on the app event thread when a primary message from the equipment that GemHost does not handle is received. Reply inside the handler through <see cref="GemMessageEventArgs.Reply"/>.</summary>
    /// <remarks>If there are no subscribers or the handler does not reply (including when it throws), the reply is SxF0. If the handler does not return within <see cref="CommandReplyTimeout"/>, GemHost replies with SxF0 on its behalf, and a later <c>Reply</c> throws <see cref="InvalidOperationException"/>.</remarks>
    public event EventHandler<GemMessageEventArgs>? MessageReceived;

    /// <summary>Raised on the app event thread for diagnostic notifications (an S9 sent by the equipment, a malformed body, a deadline overrun, a connect sequence warning, and so on). It does not affect operation.</summary>
    public event EventHandler<GemProtocolErrorEventArgs>? ProtocolError;
    #endregion

    #region Start / Stop
    /// <summary>Starts the GEM threads and the transport. Does nothing if already started.</summary>
    /// <remarks>Whether communications are established, the equipment name, and the report interpretation table return to their initial values at every Start (the subscription list is configuration, so it is kept).
    /// Even if the transport Role was changed to Equipment in the meantime (for example, by LoadXml on the transport object), it is set back to Host before the transport is started.</remarks>
    /// <exception cref="ObjectDisposedException">Already disposed.</exception>
    /// <exception cref="InvalidOperationException">The transport has already been started.</exception>
    public void Start()
    {
        lock (lifeLock)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (IsStart) return;
            if (transport.IsStart()) throw new InvalidOperationException("전송 객체가 이미 시작되어 있습니다. 전송의 Start/Stop은 GemHost가 맡습니다.");
            var w = new GemLoop(nameof(GemHost) + ".Worker", WorkerTick);
            var a = new GemLoop(nameof(GemHost) + ".Events", maxPending: maxPendingEvents);
            lock (gate)
            {
                worker = w;
                app = lastApp = a;
                running = true;
                run++;
                deferreds.Clear();
                communicating = false;
                linkUp = false;
                pendingSequenceResult = null;
                pendingEstablish = null;
                OnCommunicatingChangedLocked(false);
                modelName = softwareRevision = "";
                ResetReports();
            }
            w.Start();
            a.Start();
            IsStart = true;
            try
            {
                transport.EnsureHost();
                transport.Start();
            }
            catch
            {
                StopLocked(out var w2, out var a2);
                w2?.Stop(StopWait);
                a2?.Stop(StopWait);
                throw;
            }
        }
    }

    /// <summary>Stops the transport and the GEM threads. Events that have not yet been raised are dropped. It is safe to call more than once.</summary>
    /// <remarks>A command method that was waiting for a reply ends with <see cref="SecsDisconnectedException"/>. It waits up to 3 seconds for each thread to finish. It may be called from inside an event handler (that thread is not waited for).</remarks>
    public void Stop()
    {
        GemLoop? w, a;
        lock (lifeLock)
        {
            if (!IsStart) return;
            StopLocked(out w, out a);          // running = false를 먼저: 아래 전송 Stop의 DeviceClosed가 이벤트를 만들지 않는다
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
            pendingSequenceResult = null;                // Stop 뒤 이벤트 없음
            pendingEstablish = null;
            deferreds.Clear();
            w = worker;
            a = app;
            worker = null;
            app = null;
        }
        OnStopped();
    }

    /// <summary>Stops and releases the transport object as well. It is safe to call more than once.</summary>
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
    #endregion

    #region Receive (디스패처 스레드: 자동 응답만, 앱 코드 없음)
    void OnPrimary(object? sender, SecsPrimaryEventArgs e)
    {
        if (!IsRunning) return;
        var m = e.Message;
        // 장비가 보낸 S9(짝이 없는 S9F9 등)는 응답 없이 알리기만 한다(Host는 S9를 보내지 않음)
        if (m.Stream == 9)
        {
            e.Defer();
            Error($"장비가 S9F{m.Function}을(를) 보냈습니다: {GemItems.Sml(m)}");
            return;
        }
        if (!handlers.TryGetValue(m.Stream * 256 + m.Function, out var handler))
        {
            HandleOther(e);
            return;
        }
        // 본문 구조가 틀리면(FormatException) 응답하지 않는다 → 전송 계층이 SxF0
        try { handler(e); }
        catch (FormatException ex) { Error($"S{m.Stream}F{m.Function} 본문 구조 오류(SxF0으로 응답): {ex.Message}"); }
        catch (Exception ex) { Error($"S{m.Stream}F{m.Function} 처리 중 오류: {ex.Message}"); }
    }

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

    // 장비의 S1F13: 언제나 COMMACK 0 (본문 <L 0 <L>>). 응답을 보낸 뒤에 장비 이름을 기억하고 통신 수립을 표시한다
    // (먼저 표시하면 CommunicationStateChanged 핸들러의 명령이 장비가 S1F14를 받기 전에 도착해 S1F0이 될 수 있음)
    void OnS1F13(SecsPrimaryEventArgs e)
    {
        Respond(e, SecsItem.L(GemItems.Ack(GemAck.CommackAccepted), SecsItem.L()));
        StoreModel(e.Message.Item);
        SetCommunicating(true);
    }

    // <L MDLN SOFTREV>이면 장비 이름을 기억한다(모양이 다르면 그대로 둠)
    void StoreModel(SecsItem? item)
    {
        if (item is not { Format: SecsFormat.List, Count: 2 } || item.Items[0].Format != SecsFormat.ASCII || item.Items[1].Format != SecsFormat.ASCII) return;
        lock (gate)
        {
            modelName = item.Items[0].GetString();
            softwareRevision = item.Items[1].GetString();
        }
    }

    void SetCommunicating(bool value)
    {
        lock (gate) SetCommunicatingLocked(value);
    }

    void SetCommunicatingLocked(bool value)
    {
        if (!running || communicating == value) return;
        communicating = value;
        OnCommunicatingChangedLocked(value);
        Raise(CommunicationStateChanged, value
            ? new GemCommunicationStateEventArgs(GemCommunicationState.NotCommunicating, GemCommunicationState.Communicating)
            : new GemCommunicationStateEventArgs(GemCommunicationState.Communicating, GemCommunicationState.NotCommunicating));
    }

    // 지금 보내는 메시지가 속할 연결 세대(D1): GEM이 링크 없음으로 아는데 전송이 열려 있으면 그 송신은 GEM이 다음에 열 연결의 것
    int SendEpoch()
    {
        lock (gate) return linkUp ? linkEpoch : linkEpoch + 1;
    }
    #endregion

    #region MessageReceived (앱이 정하는 응답)
    void HandleOther(SecsPrimaryEventArgs e)
    {
        var handler = MessageReceived;
        if (handler == null) return;                                       // W면 전송 계층이 SxF0
        var m = e.Message;
        var abort = new SecsMessage(m.Stream, 0, false);
        string what = $"S{m.Stream}F{m.Function}(MessageReceived)";
        Deferred? d = null;
        int r;
        if (m.ReplyExpected) e.Defer();
        lock (gate)
        {
            r = run;
            if (m.ReplyExpected)
            {
                d = new Deferred(e, Environment.TickCount64 + commandReplyTimeout, r, what);
                deferreds.Add(d);
            }
        }
        var args = new GemMessageEventArgs(m, e.Validate, reply =>
        {
            if (d == null) return false;
            lock (gate)
            {
                // Stop(→ Start)을 넘겨 늦게 부른 응답: 이전 실행의 메시지이므로 보내지 않는다
                if (!running || run != d.Run) throw new InvalidOperationException("GemHost가 멈췄거나 다시 시작되어 이 메시지에는 응답할 수 없습니다.");
            }
            if (!d.TryClaim()) return false;
            lock (gate) deferreds.Remove(d);
            e.Reply(reply);
            return true;
        });
        PostApp(() =>
        {
            try { handler(this, args); }
            catch (Exception ex) { ErrorIn(r, $"{what}: 핸들러 예외: {ex.GetType().Name}: {ex.Message}"); }
            if (args.Close() && d != null) FinishDeferred(d, abort);
        });
    }

    // 핸들러가 응답하지 않고 돌아옴(앱 이벤트 스레드): 기한 전이면 SxF0. 이미 기한 응답이 나갔거나 그 사이 Stop(→ Start)했으면 아무것도 안 함
    void FinishDeferred(Deferred d, SecsMessage reply)
    {
        lock (gate)
        {
            deferreds.Remove(d);
            if (!running || run != d.Run) return;
        }
        if (d.TryClaim()) ReplyNow(d.Args, reply, d.What);
    }

    // 기한이 지난 미룬 응답을 SxF0으로 대신 보낸다(작업 스레드)
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
            var m = d.Args.Message;
            ReplyNow(d.Args, new SecsMessage(m.Stream, 0, false), d.What);
            ErrorIn(d.Run, $"{d.What}: 앱이 CommandReplyTimeout({commandReplyTimeout}ms) 안에 응답하지 않아 S{m.Stream}F0으로 대신 응답했습니다.");
        }
    }

    void WorkerTick() => CheckDeadlines(Environment.TickCount64);
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

    // 앱 이벤트 스레드에서 일을 한다. 멈춰 있으면 버린다
    void PostApp(Action action) => Volatile.Read(ref app)?.Post(action);
    #endregion

    #region Send helpers (명령 메서드가 함께 씀)
    // 메시지 여러 개를 보내는 메서드(ConfigureReports, 수동 접속 시퀀스)가 몸통 하나로 동기·비동기를 함께 쓰려고 받는 "묻기" 방법
    delegate ValueTask<SecsMessage> Asker(SecsMessage primary);

    // 동기: 1차 메시지(W비트)를 보내고 호출 스레드에서 응답을 기다린다(전송의 동기 Send — 스레드풀·SynchronizationContext를 거치지 않음).
    // 실패는 Plan 7 예외 그대로(T3·SxF0·S9·끊김·연결 전)
    SecsMessage Ask(SecsMessage primary) => transport.Send(primary)!;

    // 비동기: 규칙은 Ask와 같다
    async Task<SecsMessage> AskAsync(SecsMessage primary, CancellationToken cancellationToken)
        => (await transport.SendAsync(primary, cancellationToken).ConfigureAwait(false))!;

    // 동기 묻기: 이미 끝난 ValueTask를 돌려주므로 이것으로 돌린 async 몸통은 호출 스레드에서 끝까지 실행된다
    Asker SyncAsker => primary => new ValueTask<SecsMessage>(Ask(primary));

    Asker AsyncAsker(CancellationToken cancellationToken) => primary => new ValueTask<SecsMessage>(AskAsync(primary, cancellationToken));

    // SyncAsker로 돌린 몸통의 결과(이미 끝나 있음: 기다리지 않고 결과나 예외를 그대로 꺼냄)
    static T Done<T>(Task<T> task)
    {
        System.Diagnostics.Debug.Assert(task.IsCompleted, "동기 묻기로 돌린 몸통이 호출 스레드에서 끝나지 않았습니다.");
        return task.GetAwaiter().GetResult();
    }

    // 응답 본문이 기대와 다름(메시지에 응답 SML)
    static FormatException Unexpected(SecsMessage reply, string expected)
        => new($"S{reply.Stream}F{reply.Function} 응답 본문이 기대({expected})와 다릅니다: {GemItems.Sml(reply)}");

    // ack 하나(숫자 아이템, 0~255). 아니면 응답 SML을 담은 FormatException
    static byte AckOf(SecsItem? item, SecsMessage reply, string expected)
    {
        try { return GemItems.ReadAck(item); }
        catch (FormatException) { throw Unexpected(reply, expected); }
    }

    // 받은 ID(장비가 보낸 포맷 무엇이든: 정수 8종·A). 아니면 응답 SML을 담은 FormatException
    static GemId IdOf(SecsItem item, SecsMessage reply, string expected)
    {
        try { return GemId.FromItem(item); }
        catch (FormatException) { throw Unexpected(reply, expected); }
    }

    // 보낼 ID 목록 <L ID…> (설정 포맷). null = 빈 목록
    static SecsItem IdList(IReadOnlyList<GemId>? ids, SecsFormat format) => SecsItem.L([.. (ids ?? []).Select(i => i.ToItem(format))]);
    #endregion
}
