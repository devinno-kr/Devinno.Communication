using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

/// <summary>A step of the connect sequence.</summary>
public enum GemConnectStep
{
    /// <summary>No step failed (success).</summary>
    None,
    /// <summary>1. Establish communications (S1F13).</summary>
    EstablishCommunication,
    /// <summary>2. Request ON-LINE (S1F17).</summary>
    RequestOnline,
    /// <summary>3. Set the equipment clock (S2F31).</summary>
    SetClock,
    /// <summary>4. Configure reports (S2F33/35/37).</summary>
    ConfigureReports,
    /// <summary>5. Enable alarm reporting (S5F3).</summary>
    EnableAlarms,
}

/// <summary>The result of the connect sequence. It is returned by <c>RunConnectSequence</c> and is also the argument of the <c>ConnectSequenceCompleted</c> event.</summary>
public sealed class GemConnectResult : EventArgs
{
    internal GemConnectResult(GemConnectStep failedStep, byte? ack, Exception? exception, GemReportSetupResult? reportSetup)
    {
        FailedStep = failedStep;
        Ack = ack;
        Exception = exception;
        ReportSetup = reportSetup;
    }

    /// <summary>Gets a value indicating whether all steps completed (TIACK and ACKC5 failures, which are reported only as warnings, count as success).</summary>
    public bool Success => FailedStep == GemConnectStep.None;
    /// <summary>Gets the step that failed (None on success).</summary>
    public GemConnectStep FailedStep { get; }
    /// <summary>Gets the ack the equipment rejected with (COMMACK or ONLACK; for report setup, the DRACK, LRACK, or ERACK of that step). null if the step failed with an exception or the sequence succeeded.</summary>
    public byte? Ack { get; }
    /// <summary>Gets the exception that caused the failure (T3, SxF0, S9, disconnection, malformed reply, or ID format), or null if there is none.</summary>
    public Exception? Exception { get; }
    /// <summary>Gets the result of the report setup step (null if that step was not run).</summary>
    public GemReportSetupResult? ReportSetup { get; }
}

// 접속 시퀀스: S1F13(재시도) → S1F17 → (S2F31) → 리포트 설정 → S5F3. 연결될 때마다 자동(AutoConnectSequence) 또는 수동
public partial class GemHost
{
    bool autoConnectSequence = true, connectRequestOnline = true, connectSetClock, connectConfigureReports = true, connectEnableAlarms = true;
    int establishCommunicationRetry = 10;
    Task sequence = Task.CompletedTask;                     // gate: 실행 중인(또는 마지막) 접속 시퀀스
    CancellationTokenSource? linkCts;                       // gate: 지금 연결의 자동 시퀀스 취소(끊김·Stop)
    TaskCompletionSource communicated = new(TaskCreationOptions.RunContinuationsAsynchronously);   // gate: 통신이 수립되면 완료

    /// <summary>Gets or sets whether the connect sequence runs automatically on every connection (default: true).</summary>
    /// <exception cref="InvalidOperationException">Changed while running.</exception>
    public bool AutoConnectSequence { get => autoConnectSequence; set => Setting(() => autoConnectSequence = value); }
    /// <summary>Gets or sets whether connect sequence step 2 sends the ON-LINE request (S1F17) (default: true, XML attribute RequestOnline).</summary>
    /// <exception cref="InvalidOperationException">Changed while running.</exception>
    public bool ConnectRequestOnline { get => connectRequestOnline; set => Setting(() => connectRequestOnline = value); }
    /// <summary>Gets or sets whether connect sequence step 3 sets the equipment clock to the Host time (S2F31) (default: false, XML attribute SetEquipmentClock).</summary>
    /// <exception cref="InvalidOperationException">Changed while running.</exception>
    public bool ConnectSetClock { get => connectSetClock; set => Setting(() => connectSetClock = value); }
    /// <summary>Gets or sets whether connect sequence step 4 configures reports according to the subscriptions (<see cref="ConfigureReports"/>) (default: true, XML attribute ConfigureReports).</summary>
    /// <exception cref="InvalidOperationException">Changed while running.</exception>
    public bool ConnectConfigureReports { get => connectConfigureReports; set => Setting(() => connectConfigureReports = value); }
    /// <summary>Gets or sets whether connect sequence step 5 enables reporting of all alarms (S5F3) (default: true, XML attribute EnableAlarms).</summary>
    /// <exception cref="InvalidOperationException">Changed while running.</exception>
    public bool ConnectEnableAlarms { get => connectEnableAlarms; set => Setting(() => connectEnableAlarms = value); }
    /// <summary>Gets or sets the number of seconds the automatic connect sequence waits before resending S1F13 after establishing communications fails, 1 to 3600 (default: 10).</summary>
    /// <remarks>If the equipment sends S1F13 during the wait and communications are established, the sequence moves on to the next step immediately.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is out of range.</exception>
    /// <exception cref="InvalidOperationException">Changed while running.</exception>
    public int EstablishCommunicationRetry { get => establishCommunicationRetry; set => Setting(() => establishCommunicationRetry = Check(value, 1, 3600, nameof(EstablishCommunicationRetry))); }

    /// <summary>Raised on the app event thread when the connect sequence finishes (success or failure). It applies to both automatic and manual runs.</summary>
    /// <remarks>If an automatic run stops because the connection dropped, it is reported as a failure carrying a <see cref="SecsDisconnectedException"/> (the original exception of the step that failed because of the disconnection is its <see cref="Exception.InnerException"/>), and the sequence runs again from the start on the next connection.
    /// If communications had already been established, <see cref="CommunicationStateChanged"/> (→ NotCommunicating) is raised first and then this event (if not, only this event).
    /// The following cases are not reported: a run stopped by <see cref="Stop"/>, a manual run that was canceled (it throws <see cref="OperationCanceledException"/>),
    /// and an automatic run that never started because that connection also dropped while it waited for the previous sequence after the connection dropped several times in quick succession.</remarks>
    public event EventHandler<GemConnectResult>? ConnectSequenceCompleted;

    #region 연결·끊김
    partial void OnCommunicatingChangedLocked(bool value)
    {
        if (value) communicated.TrySetResult();
        else if (communicated.Task.IsCompleted) communicated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    partial void OnTransportOpened(int epoch)
    {
        if (!autoConnectSequence) return;
        CancellationTokenSource cts;
        CancellationTokenSource? previous;
        GemLoop? w;
        lock (gate)
        {
            if (!running) return;
            previous = linkCts;                                // 닫힘 알림 없이 다시 열렸어도 이전 연결의 시퀀스는 멈춘다
            cts = linkCts = new CancellationTokenSource();
            w = worker;
        }
        previous?.Cancel();
        w?.Post(() => StartAutoSequence(cts.Token, epoch));   // 작업 스레드에서 시작만 한다(응답은 기다리지 않음)
    }

    partial void OnTransportClosed() => CancelLink();

    partial void OnStopped() => CancelLink();

    void CancelLink()
    {
        CancellationTokenSource? cts;
        lock (gate)
        {
            cts = linkCts;
            linkCts = null;
        }
        cts?.Cancel();                                        // gate 밖에서: 취소 콜백이 시퀀스 코드를 이어서 실행할 수 있음
    }
    #endregion

    #region 실행
    // 자동 시퀀스를 시작한다(작업 스레드). 이전 시퀀스(끊긴 연결의 것)가 끝나기를 기다린 뒤 실행한다
    // startEpoch = 이 시퀀스를 시작한 연결의 세대
    void StartAutoSequence(CancellationToken token, int startEpoch)
    {
        Task previous;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int r;
        lock (gate)
        {
            if (!running || token.IsCancellationRequested) return;
            previous = sequence;
            sequence = done.Task;
            r = run;
        }
        _ = AutoSequenceAsync(previous, token, done, r, startEpoch);
    }

    async Task AutoSequenceAsync(Task previous, CancellationToken token, TaskCompletionSource done, int r, int startEpoch)
    {
        try
        {
            try { await previous.ConfigureAwait(false); } catch (Exception) { }
            // 기다리는 사이 이 연결도 끊김(빠른 재접속 반복): 시작하지 않고 알리지도 않는다. 끊긴 연결마다 실패가 쌓여 마지막 연결의 결과 뒤에 오지 않게
            if (token.IsCancellationRequested) return;
            var result = await SequenceAsync(AsyncAsker(token), sync: false, auto: true, r, startEpoch, token).ConfigureAwait(false);
            PublishAuto(result, r, startEpoch);
        }
        catch (Exception) { }
        finally { done.TrySetResult(); }
    }

    /// <summary>Runs the connect sequence now (the same steps as the automatic run, except that a failure to establish communications is not retried and fails immediately).</summary>
    /// <remarks>
    /// <para>Steps: 1 establish communications (S1F13, sent even if the equipment already established communications; fails if COMMACK is not 0, unless communications were already established by the equipment S1F13) → 2 request ON-LINE (S1F17; passes if ONLACK is 0 or 2, fails if 1) → 3 set the clock (S2F31; a TIACK other than 0 is a warning) →
    /// 4 configure reports (<see cref="ConfigureReports"/>; fails if any ack is not 0) → 5 enable alarm reporting (S5F3; an ACKC5 other than 0 is a warning). Warnings are reported through <see cref="ProtocolError"/>.</para>
    /// <para>Communication exceptions (T3, SxF0, S9, disconnection) are not thrown; they are carried in the result <see cref="GemConnectResult.Exception"/>. When not connected, it also does not throw and
    /// returns a step 1 failure (<see cref="GemConnectResult.Exception"/> = <see cref="InvalidOperationException"/>). <see cref="ConnectSequenceCompleted"/> is raised as well.</para>
    /// <para>The synchronous method runs every step on the calling thread using a synchronous Send (it does not rely on the thread pool).</para>
    /// </remarks>
    /// <returns>The result.</returns>
    /// <exception cref="InvalidOperationException">Another connect sequence (including an automatic one) is running.</exception>
    public GemConnectResult RunConnectSequence() => Done(RunCore(SyncAsker, sync: true, CancellationToken.None));

    /// <summary>Runs the connect sequence now. The rules and exceptions are the same as for <see cref="RunConnectSequence"/>.</summary>
    /// <param name="cancellationToken">Cancellation (when canceled, <see cref="OperationCanceledException"/> is thrown and <see cref="ConnectSequenceCompleted"/> is not raised).</param>
    /// <returns>The result.</returns>
    public Task<GemConnectResult> RunConnectSequenceAsync(CancellationToken cancellationToken = default)
        => RunCore(AsyncAsker(cancellationToken), sync: false, cancellationToken);

    // 수동 실행 몸통(동기·비동기 공용, SyncAsker면 호출 스레드에서 끝까지 실행)
    async Task<GemConnectResult> RunCore(Asker ask, bool sync, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int r;
        lock (gate)
        {
            if (!sequence.IsCompleted) throw new InvalidOperationException("접속 시퀀스가 이미 실행 중입니다.");
            sequence = done.Task;
            r = run;
        }
        try
        {
            var result = await SequenceAsync(ask, sync, auto: false, r, startEpoch: 0, cancellationToken).ConfigureAwait(false);   // 수동: startEpoch는 쓰지 않음
            Publish(result, r);
            return result;
        }
        finally { done.TrySetResult(); }
    }

    void Publish(GemConnectResult result, int r)
    {
        lock (gate)
        {
            if (running && run == r) Raise(ConnectSequenceCompleted, result);
        }
    }

    // 자동 실행의 알림(H2): 끊김 실패인데 GEM이 아직 그 연결을 열린 것으로 알면 보류하고, DeviceClosed가 CommunicationStateChanged 다음에 알린다
    void PublishAuto(GemConnectResult result, int r, int startEpoch)
    {
        lock (gate)
        {
            if (!running || run != r) return;
            if (result.Exception is SecsDisconnectedException && linkUp && linkEpoch == startEpoch) pendingSequenceResult = result;
            else Raise(ConnectSequenceCompleted, result);
        }
    }

    // 시작한 연결이 끝났는가(GEM이 그 연결의 DeviceClosed를 처리했거나 다른 연결로 바뀜)
    bool LinkEnded(int startEpoch)
    {
        lock (gate) return linkEpoch != startEpoch || !linkUp;
    }

    // ask = 묻기 방법(sync면 동기 Send: 모든 await가 이미 끝나 호출 스레드에서 실행), r = 시작한 실행 번호(경고는 그 실행일 때만 알림),
    // startEpoch = 자동 실행을 시작한 연결 세대(끊김 판정). 재시도 대기는 자동(비동기)만
    async Task<GemConnectResult> SequenceAsync(Asker ask, bool sync, bool auto, int r, int startEpoch, CancellationToken token)
    {
        var step = GemConnectStep.EstablishCommunication;
        GemReportSetupResult? reports = null;
        try
        {
            // 1. 통신 수립: 이미 수립됐어도(장비의 S1F13에 답함) Host도 S1F13을 보낸다. 장비가 Host의 S1F14를 처리하기 전에 S1F17이 도착하면 S1F0이 되는 경합을 막는다
            //    (E30: 수립된 장비는 S1F13에 COMMACK 0, 장비는 받은 순서대로 처리하므로 S1F14가 오면 장비도 수립됨). 자동 실행은 연결이 있는 동안 EstablishCommunicationRetry초마다 다시
            while (true)
            {
                byte? ack = null;
                Exception? failure = null;
                try
                {
                    int epoch = SendEpoch();
                    ack = EstablishReply(await ask(EstablishRequest()).ConfigureAwait(false), epoch);
                    if (ack == GemAck.CommackAccepted) break;
                }
                catch (Exception ex) when (ex is SecsTimeoutException or SecsAbortException or SecsErrorException or FormatException) { failure = ex; }
                if (IsCommunicating) break;                                                  // 장비의 S1F13으로 이미 수립됨: Host의 S1F13 결과와 상관없이 다음 단계
                if (!auto) return new GemConnectResult(step, ack, failure, null);
                ErrorIn(r, $"접속 시퀀스: S1F13 통신 수립 실패({(failure != null ? failure.Message : $"COMMACK {ack}")}). {establishCommunicationRetry}초 뒤 다시 보냅니다.");
                Task established;
                lock (gate) established = communicated.Task;
                await Task.WhenAny(Task.Delay(establishCommunicationRetry * 1000, token), established).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
            // 2. ON-LINE 요청: 0(받음)·2(이미 ON-LINE)만 통과
            if (connectRequestOnline)
            {
                step = GemConnectStep.RequestOnline;
                byte onlack = OnlineReply(await ask(OnlineRequest()).ConfigureAwait(false));
                if (onlack is not (GemAck.OnlackAccepted or GemAck.OnlackAlreadyOnline)) return new GemConnectResult(step, onlack, null, null);
            }
            // 3. 시계 맞춤: 거절은 경고만
            if (connectSetClock)
            {
                step = GemConnectStep.SetClock;
                byte tiack = TiackReply(await ask(SetTimeRequest(null)).ConfigureAwait(false));
                if (tiack != GemAck.TiackAccepted) ErrorIn(r, $"접속 시퀀스: 장비가 시계 맞춤을 거절했습니다(TIACK {tiack}).");
            }
            // 4. 리포트 설정: 거절은 실패
            if (connectConfigureReports)
            {
                step = GemConnectStep.ConfigureReports;
                reports = await ConfigureReportsCore(ask, sync, token).ConfigureAwait(false);
                if (!reports.Success) return new GemConnectResult(step, reports.Ack, null, reports);
            }
            // 5. 알람 보고 켜기: 거절은 경고만
            if (connectEnableAlarms)
            {
                step = GemConnectStep.EnableAlarms;
                byte ackc5 = Ackc5Reply(await ask(EnableAlarmRequest(null, true)).ConfigureAwait(false));
                if (ackc5 != GemAck.Ackc5Accepted) ErrorIn(r, $"접속 시퀀스: 장비가 알람 보고 켜기를 거절했습니다(ACKC5 {ackc5}).");
            }
            return new GemConnectResult(GemConnectStep.None, null, null, reports);
        }
        catch (OperationCanceledException) when (!auto) { throw; }
        catch (Exception ex)
        {
            // 자동 실행: 끊김이 원인인 실패는 SecsDisconnectedException으로 알린다(원래 예외는 InnerException). 끊긴 직후 GemHost가 DeviceClosed를 처리해
            // 시퀀스를 취소하기 전에 다음 단계를 보내면 전송이 InvalidOperationException("연결되어 있지 않습니다")을 던지므로 그것도 끊김으로 본다.
            // 다른 실패(T3 등)는 시작한 연결이 끝났으면(GEM의 연결 세대 기준) 끊김으로 본다
            if (auto && (token.IsCancellationRequested || SecsSession.IsNotConnected(ex) || ex is not SecsDisconnectedException && LinkEnded(startEpoch)))
                ex = new SecsDisconnectedException("연결이 끊겨 접속 시퀀스를 멈췄습니다. 다음 연결 때 처음부터 다시 실행합니다.", ex);
            return new GemConnectResult(step, null, ex, reports);
        }
    }
    #endregion
}
