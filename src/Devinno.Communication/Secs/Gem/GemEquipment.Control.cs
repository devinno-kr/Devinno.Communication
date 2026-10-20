using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

// 제어 상태 머신(E30 전이 1~12), S1F1/S1F2, S1F15~S1F18, 운전자 메서드
public partial class GemEquipment
{
    const int S1F1 = 1 * 256 + 1, S1F15 = 1 * 256 + 15;

    GemControlState initialControlState = GemControlState.HostOffline;
    GemControlState onlineFailedState = GemControlState.EquipmentOffline;
    byte previousControlValue;              // gate: PreviousControlState SV (처음 0)

    /// <summary>Gets or sets the control state at start: EquipmentOffline, HostOffline, OnlineLocal, OnlineRemote (default: HostOffline)</summary>
    /// <remarks>With HostOffline, the Host must send S1F17 to go ON-LINE (the usual Host connection sequence). The initial position of the switch (<see cref="LocalRemoteSwitch"/>) is Local for OnlineLocal and Remote otherwise.</remarks>
    /// <exception cref="ArgumentException">AttemptOnline or an undefined value</exception>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public GemControlState InitialControlState
    {
        get => initialControlState;
        set => Setting(() => initialControlState = value is GemControlState.EquipmentOffline or GemControlState.HostOffline or GemControlState.OnlineLocal or GemControlState.OnlineRemote
            ? value : throw new ArgumentException("시작 제어 상태는 EquipmentOffline, HostOffline, OnlineLocal, OnlineRemote 중 하나입니다.", nameof(InitialControlState)));
    }

    /// <summary>Gets or sets the state to go to when an ON-LINE attempt (<see cref="GoOnline"/>) fails: EquipmentOffline or HostOffline (default: EquipmentOffline)</summary>
    /// <exception cref="ArgumentException">Neither of the two values</exception>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public GemControlState OnlineFailedState
    {
        get => onlineFailedState;
        set => Setting(() => onlineFailedState = value is GemControlState.EquipmentOffline or GemControlState.HostOffline
            ? value : throw new ArgumentException("실패 대상은 EquipmentOffline 또는 HostOffline입니다.", nameof(OnlineFailedState)));
    }

    /// <summary>Raised on the application event thread when the control state changed (after the ControlState SV is updated and the state CE is queued).</summary>
    public event EventHandler<GemControlStateEventArgs>? ControlStateChanged;

    // 표준 CE를 보낸다(리포트 기능이 구현, gate 안)
    partial void TriggerStandardEventLocked(GemStandard ce);

    partial void AddControlHandlers()
    {
        handlers[S1F1] = OnS1F1;
        handlers[S1F15] = OnS1F15;
        handlers[S1F17] = OnS1F17;
        standardValues[GemStandard.ControlState] = () => SecsItem.U1((byte)control);
        standardValues[GemStandard.PreviousControlState] = () => SecsItem.U1(previousControlValue);
    }

    partial void ResetControl()
    {
        control = initialControlState;
        previousControlValue = 0;
        localRemote = initialControlState == GemControlState.OnlineLocal ? GemLocalRemote.Local : GemLocalRemote.Remote;
    }

    #region 운전자 메서드
    /// <summary>The operator turns on the ON-LINE switch (EQUIPMENT OFF-LINE → ATTEMPT ON-LINE, transition 3). S1F1 is sent, and if S1F2 is received the state is ON-LINE (transitions 5 and 7).</summary>
    /// <remarks>If the answer is S1F0, T3, S9, or a communication failure instead of S1F2, it goes to <see cref="OnlineFailedState"/> (transition 4). If communication is not established, it fails immediately. If the state is not EQUIPMENT OFF-LINE, nothing happens (ignored during ATTEMPT ON-LINE).</remarks>
    /// <returns>true if the transition was started</returns>
    /// <exception cref="InvalidOperationException">Not started</exception>
    public bool GoOnline()
    {
        Started();
        lock (gate)
        {
            if (!running || control != GemControlState.EquipmentOffline) return false;
            ChangeControlLocked(GemControlState.AttemptOnline);
            if (commState != GemCommunicationState.Communicating) ChangeControlLocked(onlineFailedState);      // 전이 4
            else if (!EnqueueLocked(new SecsMessage(1, 1, true), S1F1Done)) ChangeControlLocked(onlineFailedState);   // 대기열이 가득 차 S1F1이 버려짐: 결과가 오지 않으므로 전이 4
            return true;
        }
    }

    /// <summary>The operator turns on the OFF-LINE switch (ON-LINE or HOST OFF-LINE → EQUIPMENT OFF-LINE, transitions 6 and 12). From ON-LINE (transition 6) the EquipmentOffline CE is sent before the state changes; from HOST OFF-LINE (transition 12) it is already OFF-LINE, so no CE is sent.</summary>
    /// <returns>true if it made the transition (false from EQUIPMENT OFF-LINE or ATTEMPT ON-LINE)</returns>
    /// <exception cref="InvalidOperationException">Not started</exception>
    public bool GoOffline()
    {
        Started();
        lock (gate)
        {
            if (!running || control is GemControlState.EquipmentOffline or GemControlState.AttemptOnline) return false;
            TriggerStandardEventLocked(GemStandard.EquipmentOffline);
            ChangeControlLocked(GemControlState.EquipmentOffline);
            return true;
        }
    }

    /// <summary>Sets the LOCAL/REMOTE switch to LOCAL. From ON-LINE REMOTE it becomes LOCAL (transition 9, ControlStateLocal CE). While OFF-LINE only the position changes and is used at the next ON-LINE.</summary>
    /// <returns>true if the switch position changed</returns>
    /// <exception cref="InvalidOperationException">Not started</exception>
    public bool SwitchLocal() => Switch(GemLocalRemote.Local);

    /// <summary>Sets the LOCAL/REMOTE switch to REMOTE. From ON-LINE LOCAL it becomes REMOTE (transition 8, ControlStateRemote CE). While OFF-LINE only the position changes.</summary>
    /// <returns>true if the switch position changed</returns>
    /// <exception cref="InvalidOperationException">Not started</exception>
    public bool SwitchRemote() => Switch(GemLocalRemote.Remote);

    bool Switch(GemLocalRemote position)
    {
        Started();
        lock (gate)
        {
            if (!running || localRemote == position) return false;
            localRemote = position;
            if (control is GemControlState.OnlineLocal or GemControlState.OnlineRemote) EnterOnlineLocked();   // 전이 8·9
            return true;
        }
    }
    #endregion

    #region 전이
    // 상태를 바꾸고(PreviousControlState 갱신) 이벤트를 넣는다. ceAfter가 있으면 그 사이에 CE (SV → CE → 이벤트 순서)
    void ChangeControlLocked(GemControlState next, GemStandard? ceAfter = null)
    {
        var prev = control;
        previousControlValue = (byte)prev;
        control = next;
        if (ceAfter != null) TriggerStandardEventLocked(ceAfter.Value);
        Raise(ControlStateChanged, new GemControlStateEventArgs(prev, next));
    }

    // ON-LINE 진입(전이 7): 스위치 위치대로 LOCAL/REMOTE + 상태 CE
    void EnterOnlineLocked()
    {
        if (localRemote == GemLocalRemote.Local) ChangeControlLocked(GemControlState.OnlineLocal, GemStandard.ControlStateLocal);
        else ChangeControlLocked(GemControlState.OnlineRemote, GemStandard.ControlStateRemote);
    }

    // S1F1의 결과(작업 스레드): S1F2면 전이 5(→7), 실패면 전이 4
    void S1F1Done(Outgoing o, Task<SecsMessage?> t)
    {
        if (!t.IsCompletedSuccessfully) ReportFailure(o.Message, t.Exception!.InnerException!);
        lock (gate)
        {
            if (!running || control != GemControlState.AttemptOnline) return;
            if (t.IsCompletedSuccessfully) EnterOnlineLocked();
            else ChangeControlLocked(onlineFailedState);
        }
    }

    partial void OnCommunicationLostLocked()
    {
        if (control == GemControlState.AttemptOnline) ChangeControlLocked(onlineFailedState);    // 전이 4(통신 실패)
    }
    #endregion

    #region 받은 메시지
    void OnS1F1(SecsPrimaryEventArgs e) => Respond(e, SecsItem.L(SecsItem.A(modelName), SecsItem.A(softwareRevision)));

    // Host의 OFF-LINE 요청: OFLACK 0 후 전이 10 (EquipmentOffline CE는 상태를 바꾸기 전에)
    void OnS1F15(SecsPrimaryEventArgs e)
    {
        Respond(e, GemItems.Ack(GemAck.OflackAccepted));
        lock (gate)
        {
            if (!running || control is not (GemControlState.OnlineLocal or GemControlState.OnlineRemote)) return;
            TriggerStandardEventLocked(GemStandard.EquipmentOffline);
            ChangeControlLocked(GemControlState.HostOffline);
        }
    }

    // Host의 ON-LINE 요청: HOST OFF-LINE에서만 ONLACK 0 → 전이 11(→7). ON-LINE이면 2, 그 밖은 1
    void OnS1F17(SecsPrimaryEventArgs e)
    {
        byte ack;
        lock (gate)
            ack = control switch
            {
                GemControlState.HostOffline => GemAck.OnlackAccepted,
                GemControlState.OnlineLocal or GemControlState.OnlineRemote => GemAck.OnlackAlreadyOnline,
                _ => GemAck.OnlackNotAllowed,
            };
        Respond(e, GemItems.Ack(ack));
        if (ack != GemAck.OnlackAccepted) return;
        lock (gate)
        {
            if (running && control == GemControlState.HostOffline) EnterOnlineLocked();
        }
    }
    #endregion
}
