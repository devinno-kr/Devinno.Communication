using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

// 통신 상태 머신(E30 전이 1~10·14·15)과 S1F13/S1F14
public partial class GemEquipment
{
    // 장비가 시작하는 통신 수립(EQUIPMENT-INITIATED CONNECT)의 하위 상태. HOST-INITIATED(WAIT CR FROM HOST)는 늘 함께 열려 있다(전이 10)
    enum EquipmentConnect { None, WaitCra, WaitDelay }

    GemEnableState initialCommunicationState = GemEnableState.Enabled;
    bool initiateCommunication = true;
    EquipmentConnect equipmentConnect;      // gate
    long commDelayDeadline;                 // gate: WAIT DELAY가 끝나는 시각(Environment.TickCount64)

    /// <summary>Gets or sets whether communication is enabled at start (default: Enabled)</summary>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public GemEnableState InitialCommunicationState { get => initialCommunicationState; set => Setting(() => initialCommunicationState = value); }

    /// <summary>Gets or sets whether the equipment establishes communication by sending S1F13 first (default: true)</summary>
    /// <remarks>If true, S1F13 is sent as soon as the connection is made. If it fails while connected (T3, COMMACK other than 0, S1F0), it is sent again after EstablishCommunicationsTimeout seconds (or at once when the Host sends a primary message other than S1F13 in the meantime).
    /// After a disconnection (including one while waiting for S1F14), it is sent as soon as the next connection opens. If false, it only waits for the Host's S1F13.</remarks>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public bool InitiateCommunication { get => initiateCommunication; set => Setting(() => initiateCommunication = value); }

    /// <summary>Raised on the application event thread when the communication state changed.</summary>
    /// <remarks>The GEM states change in wire order: the handling of the Host's primary messages and the results of GEM sends (S1F14 → this event, S1F2 → <see cref="ControlStateChanged"/>, S2F18 → <see cref="ClockSet"/>, <see cref="SendFailed"/>) happen, and their events are raised, in the order the equipment received those messages.
    /// A disconnection changes Communicating → NotCommunicating at most once per connection, and no event about that connection follows it. After reconnecting, S5/S6/S10 are not sent until communication is established again with S1F13, provided no application handler of the transport events (<c>DeviceOpened</c>, <c>DeviceClosed</c>, <c>ProtocolError</c>) holds the transport dispatcher across the reconnection.</remarks>
    public event EventHandler<GemCommunicationStateEventArgs>? CommunicationStateChanged;

    /// <summary>Enables communication (DISABLED → ENABLED, transition 2). Does nothing if it was already enabled.</summary>
    /// <returns>true if the state changed</returns>
    /// <exception cref="InvalidOperationException">Not started</exception>
    public bool EnableCommunication()
    {
        Started();
        lock (gate)
        {
            if (commState != GemCommunicationState.Disabled) return false;
            SetCommunicationLocked(GemCommunicationState.NotCommunicating);
            StartWaitCraLocked(linkUp);
            return true;
        }
    }

    /// <summary>Disables communication (ENABLED → DISABLED, transition 3). Discards the send queue (<see cref="SendFailed"/> Disabled) and ignores the result of a GEM send that was waiting for a reply. The transport connection is kept.</summary>
    /// <returns>true if the state changed</returns>
    /// <exception cref="InvalidOperationException">Not started</exception>
    public bool DisableCommunication()
    {
        Started();
        lock (gate)
        {
            if (commState == GemCommunicationState.Disabled) return false;
            ClearQueueLocked(GemSendFailure.Disabled);
            equipmentConnect = EquipmentConnect.None;
            SetCommunicationLocked(GemCommunicationState.Disabled);
            OnCommunicationLostLocked();
            return true;
        }
    }

    // 통신이 끊기거나 꺼짐(gate 안): 제어 상태 머신이 ATTEMPT ON-LINE을 실패로 끝낸다(전이 4)
    partial void OnCommunicationLostLocked();

    partial void AddCommunicationHandlers() => handlers[S1F13] = OnS1F13;

    partial void ResetCommunication()
    {
        equipmentConnect = EquipmentConnect.None;
        commState = initialCommunicationState == GemEnableState.Enabled ? GemCommunicationState.NotCommunicating : GemCommunicationState.Disabled;
        if (commState == GemCommunicationState.NotCommunicating) StartWaitCraLocked(connected: false);   // 전이 1·4·5(아직 연결 없음 → WAIT DELAY)
    }

    void SetCommunicationLocked(GemCommunicationState next)
    {
        var prev = commState;
        if (prev == next) return;
        commState = next;
        Raise(CommunicationStateChanged, new GemCommunicationStateEventArgs(prev, next));
    }

    int CommDelayMs => (int)values[catalog.Std(GemStandard.EstablishCommunicationsTimeout)].GetInt64() * 1000;

    // 전이 5·7·8: S1F13을 보내고 WAIT CRA. 연결이 없으면 보내지 않고 WAIT DELAY를 다시 시작한다(gate 안)
    void StartWaitCraLocked(bool connected)
    {
        if (!initiateCommunication)
        {
            equipmentConnect = EquipmentConnect.None;
            return;
        }
        if (!connected)
        {
            equipmentConnect = EquipmentConnect.WaitDelay;
            commDelayDeadline = Environment.TickCount64 + CommDelayMs;
            return;
        }
        equipmentConnect = EquipmentConnect.WaitCra;
        EnqueueLocked(GemMessages.S1F13(modelName, softwareRevision), S1F13Done);
    }

    // 장비 S1F13의 결과(송신 완료, 보통 디스패처): COMMACK 0이면 전이 9, 아니면 전이 6(WAIT DELAY)
    void S1F13Done(Outgoing o, Task<SecsMessage?> t)
    {
        string? failure = null;
        if (!t.IsCompletedSuccessfully) failure = t.Exception?.InnerException?.Message;
        else if (t.Result == null) failure = "S1F14 본문 구조가 틀림: (응답 없음)";
        else
        {
            try
            {
                byte ack = GemMessages.ParseS1F14(t.Result).Commack;          // MDLN·SOFTREV는 보지 않음
                if (ack != GemAck.CommackAccepted) failure = $"COMMACK {ack}";
            }
            catch (FormatException ex) { failure = ex.Message; }            // 구조 오류·COMMACK 해석 실패(2층 문장, 응답 SML 포함)
        }
        int delay;
        lock (gate)
        {
            if (!running || commState != GemCommunicationState.NotCommunicating || equipmentConnect != EquipmentConnect.WaitCra) return;   // 이미 Host S1F13으로 수립됨 등
            if (failure == null)
            {
                equipmentConnect = EquipmentConnect.None;
                SetCommunicationLocked(GemCommunicationState.Communicating);         // 전이 9
                return;
            }
            equipmentConnect = EquipmentConnect.WaitDelay;                             // 전이 6
            delay = CommDelayMs;
            commDelayDeadline = Environment.TickCount64 + delay;
        }
        Error($"S1F13 통신 수립 실패({failure}). {delay / 1000}초 뒤 다시 보냅니다.");
    }

    // Host의 S1F13: 언제나(DISABLED 제외) COMMACK 0. NOT COMMUNICATING이면 전이 15
    void OnS1F13(SecsPrimaryEventArgs e)
    {
        Respond(e, GemMessages.S1F14(GemAck.CommackAccepted, modelName, softwareRevision));
        lock (gate)
        {
            if (!running || commState != GemCommunicationState.NotCommunicating) return;
            equipmentConnect = EquipmentConnect.None;
            if (inFlight != null && IsS1F13(inFlight.Message)) inFlight = null;       // 장비 S1F13의 늦은 S1F14는 보지 않고 대기열을 계속 쓴다
            SetCommunicationLocked(GemCommunicationState.Communicating);
            worker?.Post(Pump);
        }
    }

    // 통신 실패(전이 14, gate 안): 대기열을 버림 경로로 비우고 NOT COMMUNICATING → 다시 S1F13
    void CommunicationLostLocked(bool connected)
    {
        ClearQueueLocked(GemSendFailure.NotCommunicating);
        SetCommunicationLocked(GemCommunicationState.NotCommunicating);
        OnCommunicationLostLocked();
        StartWaitCraLocked(connected);
    }

    partial void OnTransportOpened()
    {
        lock (gate)
        {
            linkUp = true;
            ++linkEpoch;
            // 연결 직후에는 CommDelay를 기다리지 않고 바로 S1F13 (WAIT DELAY를 만료로 봄)
            if (running && commState == GemCommunicationState.NotCommunicating && equipmentConnect == EquipmentConnect.WaitDelay)
                StartWaitCraLocked(connected: true);
            worker?.Post(Pump);                        // 링크 없음으로 기다리던 송신을 시작
        }
    }

    partial void OnTransportClosed()
    {
        lock (gate)
        {
            linkUp = false;
            ++linkEpoch;
            if (!running) return;
            // 그 연결에 속한 일을 여기서 모두 끝낸다(끊김으로 끝난 송신 완료는 상태를 바꾸지 않으므로 순서와 무관)
            if (commState == GemCommunicationState.Communicating) CommunicationLostLocked(connected: false);   // 전이 14
            else if (commState == GemCommunicationState.NotCommunicating && equipmentConnect == EquipmentConnect.WaitCra)
            {
                // WAIT CRA 중 끊김: 응답 대기·대기열의 장비 S1F13을 놓고(알리지 않음) WAIT DELAY. 다음 DeviceOpened가 바로 S1F13
                ClearQueueLocked(GemSendFailure.NotCommunicating);
                StartWaitCraLocked(connected: false);
            }
        }
    }

    partial void OnCommunicationFailure()
    {
        lock (gate)
        {
            if (running && commState == GemCommunicationState.Communicating) CommunicationLostLocked(linkUp);
        }
    }

    partial void OnNotCommunicatingPrimary()
    {
        lock (gate)
        {
            // 전이 8: WAIT DELAY 중 S1F13이 아닌 1차 메시지를 받음 → 바로 S1F13
            if (running && commState == GemCommunicationState.NotCommunicating && equipmentConnect == EquipmentConnect.WaitDelay)
                StartWaitCraLocked(connected: true);
        }
    }

    partial void OnWorkerTick(long now)
    {
        lock (gate)
        {
            // 전이 7: CommDelay 만료 → S1F13 (연결이 없으면 WAIT DELAY 다시)
            if (running && commState == GemCommunicationState.NotCommunicating && equipmentConnect == EquipmentConnect.WaitDelay && now >= commDelayDeadline)
                StartWaitCraLocked(linkUp);
        }
    }
}
