using System.Collections.Concurrent;
using System.Diagnostics;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Secs.Hsms;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Secs.Gem;

/// GemHost 접속 시퀀스: 자동(연결마다)·수동, 단계 순서, 통신 수립 재시도, 실패 단계·경고, 재접속, 작업 스레드를 막지 않음
[Collection(nameof(GemHostCollection))]
public class GemHostConnectTests
{
    static async Task<GemConnectResult> Next(BlockingCollection<GemConnectResult> results, int timeoutMs = 5000)
    {
        await TestUtil.Until(() => results.Count > 0, timeoutMs);
        return results.Take();
    }

    static Action<GemHost> Auto(BlockingCollection<GemConnectResult> results, Action<GemHost>? more = null) => h =>
    {
        h.AutoConnectSequence = true;
        h.ConnectSequenceCompleted += (s, e) => results.Add(e);
        more?.Invoke(h);
    };

    [Fact]
    public void Settings_DefaultsAndRange()
    {
        using var host = new GemHost(GemHostKit.Transport(1));
        Assert.Equal((true, true, false, true, true, 10), (host.AutoConnectSequence, host.ConnectRequestOnline, host.ConnectSetClock, host.ConnectConfigureReports, host.ConnectEnableAlarms, host.EstablishCommunicationRetry));
        Assert.Throws<ArgumentOutOfRangeException>(() => host.EstablishCommunicationRetry = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => host.EstablishCommunicationRetry = 3601);
        host.Start();
        Assert.Throws<InvalidOperationException>(() => host.ConnectSetClock = true);
        host.Stop();
    }

    [Fact]
    public async Task Auto_Loopback_EquipmentEndsOnlineWithSubscribedEventsAndAlarms()
    {
        var results = new BlockingCollection<GemConnectResult>();
        var events = new BlockingCollection<GemEventReport>();
        using var eq = GemKit.Equipment(GemKit.Sample);                                   // 기본: HOST OFF-LINE, CE·알람 보고 꺼짐
        eq.Start();
        using var host = GemHostKit.Host(GemKit.Port(eq), Auto(results, h =>
        {
            h.Subscribe(5001, 2001, 3001);
            h.EventReceived += (s, e) => events.Add(e.Report);
        }));
        host.Start();
        var result = await Next(results);
        Assert.True(result.Success, $"{result.FailedStep} {result.Ack} {result.Exception}");
        Assert.True(result.ReportSetup!.Success);
        Assert.Equal(GemControlState.OnlineRemote, eq.ControlState);
        Assert.Equal("<U4 5001>", eq.GetValue(1004).ToSml());                              // EventsEnabled
        Assert.Equal("<U4 1>", eq.GetValue(1005).ToSml());                                 // AlarmsEnabled
        Assert.True(eq.TriggerEvent(5001));
        await TestUtil.Until(() => events.Count == 1);
        Assert.Equal(SecsItem.F4(25), events.Take().GetValue(2001));
    }

    [Fact]
    public async Task Auto_Fake_GoldenOrder_AllSteps()
    {
        var results = new BlockingCollection<GemConnectResult>();
        var (fake, host) = await GemHostKit.Fake(Auto(results, h => { h.ConnectSetClock = true; h.Subscribe(5001, 2001); }));
        using var _ = fake;
        using var __ = host;
        Assert.Equal("S1F13 W <L [0]>", fake.Next(1, 13).ToSml());
        Assert.Equal("S1F17 W", fake.Next(1, 17).ToSml());
        Assert.Equal(16, fake.Next(2, 31).Item!.GetString().Length);                       // TimeFormat 1
        Assert.Equal("S2F33 W <L [2] <U4 1> <L [0]>>", fake.Next(2, 33).ToSml());
        Assert.Equal("S2F37 W <L [2] <BOOLEAN F> <L [0]>>", fake.Next(2, 37).ToSml());
        Assert.Equal("S2F33 W <L [2] <U4 2> <L [1] <L [2] <U4 1000> <L [1] <U4 2001>>>>>", fake.Next(2, 33).ToSml());
        Assert.Equal("S2F35 W <L [2] <U4 3> <L [1] <L [2] <U4 5001> <L [1] <U4 1000>>>>>", fake.Next(2, 35).ToSml());
        Assert.Equal("S2F37 W <L [2] <BOOLEAN T> <L [1] <U4 5001>>>", fake.Next(2, 37).ToSml());
        Assert.Equal("S5F3 W <L [2] <B 0x80> <U4>>", fake.Next(5, 3).ToSml());
        Assert.True((await Next(results)).Success);
        Assert.Equal(("FAKE", "9.9"), (host.ModelName, host.SoftwareRevision));
        fake.NoMore();
    }

    [Fact]
    public async Task Auto_Commack1_RetriesAfterDelay()
    {
        var results = new BlockingCollection<GemConnectResult>();
        ConcurrentQueue<string>? log = null;
        var (fake, host) = await GemHostKit.Fake(Auto(results, h => { log = GemHostKit.Log(h); h.EstablishCommunicationRetry = 1; h.ConnectConfigureReports = false; h.ConnectEnableAlarms = false; }),
            f => f.Reply(1, 13, "<L <B 1> <L>>"));
        using var _ = fake;
        using var __ = host;
        fake.Next(1, 13);
        fake.Reply(1, 13, "<L <B 0> <L>>");
        var sw = Stopwatch.StartNew();
        fake.Next(1, 13, 5000);                                                             // 1초 뒤 다시
        Assert.True(sw.ElapsedMilliseconds > 500, $"{sw.ElapsedMilliseconds}ms");
        fake.Next(1, 17);
        Assert.True((await Next(results)).Success);
        Assert.Contains(log!, l => l.Contains("COMMACK 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Auto_EquipmentS1F13DuringRetryWait_ProceedsAtOnce()
    {
        var results = new BlockingCollection<GemConnectResult>();
        var (fake, host) = await GemHostKit.Fake(Auto(results, h => { h.EstablishCommunicationRetry = 60; h.ConnectConfigureReports = false; h.ConnectEnableAlarms = false; }),
            f => f.Reply(1, 13, "<L <B 1> <L>>"));
        using var _ = fake;
        using var __ = host;
        fake.Next(1, 13);
        Assert.Equal("S1F14 <L [2] <B 0x00> <L [0]>>", await fake.Ask("S1F13 W <L <A \"EQ\"> <A \"2\">>"));   // 장비가 수립
        fake.Next(1, 13, 3000);                                                             // 60초를 기다리지 않고 다시 보냄
        fake.Next(1, 17);                                                                   // COMMACK 1이어도 이미 수립됐으므로 다음 단계
        Assert.True((await Next(results)).Success);
    }

    [Fact]
    public async Task Auto_Onlack1_FailsAtRequestOnline_StopsThere()
    {
        var results = new BlockingCollection<GemConnectResult>();
        var (fake, host) = await GemHostKit.Fake(Auto(results), f => f.Reply(1, 17, "<B 1>"));
        using var _ = fake;
        using var __ = host;
        var r = await Next(results);
        Assert.Equal((false, GemConnectStep.RequestOnline, (byte?)1, (Exception?)null), (r.Success, r.FailedStep, r.Ack, r.Exception));
        fake.Next(1, 13);
        fake.Next(1, 17);
        fake.NoMore();
    }

    [Fact]
    public async Task Auto_Onlack2_AlreadyOnline_Passes_Warnings_Tiack_Ackc5()
    {
        var results = new BlockingCollection<GemConnectResult>();
        var log = new ConcurrentQueue<string>();
        var (fake, host) = await GemHostKit.Fake(Auto(results, h => { h.ConnectSetClock = true; h.ProtocolError += (s, e) => log.Enqueue(e.Reason); }), f =>
        {
            f.Reply(1, 17, "<B 2>");
            f.Reply(2, 31, "<B 1>");
            f.Reply(5, 3, "<B 1>");
        });
        using var _ = fake;
        using var __ = host;
        Assert.True((await Next(results)).Success);
        await TestUtil.Until(() => log.Count(l => l.StartsWith("접속 시퀀스", StringComparison.Ordinal)) == 2);
        Assert.Contains(log, l => l.Contains("TIACK 1", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("ACKC5 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Auto_ReportRejected_FailsWithReportSetup_NoAlarmStep()
    {
        var results = new BlockingCollection<GemConnectResult>();
        var (fake, host) = await GemHostKit.Fake(Auto(results, h => h.Subscribe(5001, 2001)), f => f.Reply(2, 35, "<B 4>"));
        using var _ = fake;
        using var __ = host;
        var r = await Next(results);
        Assert.Equal((GemConnectStep.ConfigureReports, (byte?)4, GemReportSetupStep.LinkEvents), (r.FailedStep, r.Ack, r.ReportSetup!.FailedStep));
        for (int i = 0; i < 6; i++) Assert.NotEqual(5, fake.NextAny().Stream);           // S1F13 S1F17 S2F33 S2F37 S2F33 S2F35
        fake.NoMore();
    }

    [Fact]
    public async Task Auto_S1F17Aborted_FailureHoldsException()
    {
        var results = new BlockingCollection<GemConnectResult>();
        var (fake, host) = await GemHostKit.Fake(Auto(results), f => f.Reply(1, 17, null));
        using var _ = fake;
        using var __ = host;
        var r = await Next(results);
        Assert.Equal(GemConnectStep.RequestOnline, r.FailedStep);
        Assert.IsType<SecsAbortException>(r.Exception);
        Assert.Null(r.Ack);
    }

    [Fact]
    public async Task Manual_NoRetry_ReturnsResult_RaisesEvent_AlwaysSendsS1F13()
    {
        var results = new BlockingCollection<GemConnectResult>();
        var (fake, host) = await GemHostKit.Fake(h => h.ConnectSequenceCompleted += (s, e) => results.Add(e), f => f.Reply(1, 13, "<L <B 1> <L>>"));
        using var _ = fake;
        using var __ = host;
        var failed = await host.RunConnectSequenceAsync();                                  // 수동: 다시 시도하지 않음
        Assert.Equal((GemConnectStep.EstablishCommunication, (byte?)1), (failed.FailedStep, failed.Ack));
        Assert.Same(failed, await Next(results));
        fake.Reply(1, 13, "<L <B 0> <L>>");
        Assert.True(host.RunConnectSequence().Success);
        Assert.True((await Next(results)).Success);
        while (fake.NextAny().Function != 3) { }                                            // 두 번째 실행의 마지막 S5F3까지 비움
        Assert.True(host.IsCommunicating);
        Assert.True((await host.RunConnectSequenceAsync()).Success);
        Assert.Equal(13, fake.NextAny().Function);                                          // 이미 수립돼도 S1F13부터(장비와의 경합 방지)
    }

    [Fact]
    public async Task Manual_WhileRunning_InvalidOperation_UserCancellation()
    {
        var (fake, host) = await GemHostKit.Fake(h => { h.ConnectConfigureReports = false; h.ConnectEnableAlarms = false; }, f => f.Hold(1, 17));
        using var _ = fake;
        using var __ = host;
        var first = host.RunConnectSequenceAsync();
        fake.Next(1, 13);
        fake.Next(1, 17);                                                                   // 붙잡힘: 시퀀스 실행 중
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.RunConnectSequenceAsync());
        fake.Held.Take().Reply(new SecsMessage(1, 18, false, SecsItem.B(0)));
        Assert.True((await first).Success);

        using var cts = new CancellationTokenSource();
        var cancelled = host.RunConnectSequenceAsync(cts.Token);
        fake.Next(1, 13);
        fake.Next(1, 17);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        fake.Unhold(1, 17);
        Assert.True((await host.RunConnectSequenceAsync()).Success);                       // 취소 뒤에도 다시 실행 가능
    }

    [Fact]
    public async Task Reconnect_RerunsSequence_DisconnectDuringRetryIsReported()
    {
        var results = new BlockingCollection<GemConnectResult>();
        var (fake, host) = await GemHostKit.Fake(Auto(results, h => { h.EstablishCommunicationRetry = 60; h.ConnectConfigureReports = false; h.ConnectEnableAlarms = false; }));
        using var _ = fake;
        using var __ = host;
        Assert.True((await Next(results)).Success);
        fake.Next(1, 13);
        fake.Next(1, 17);
        fake.Reply(1, 13, "<L <B 1> <L>>");
        fake.Drop();                                                                        // 끊김 → Host가 다시 접속 → 처음부터
        fake.Next(1, 13, 5000);
        fake.Drop();                                                                        // 재시도 대기(60초) 중 끊김 → 실패로 알림
        var r = await Next(results);
        Assert.Equal(GemConnectStep.EstablishCommunication, r.FailedStep);
        Assert.IsType<SecsDisconnectedException>(r.Exception);
        fake.Reply(1, 13, "<L <B 0> <L>>");
        fake.Next(1, 13, 5000);                                                             // 다음 연결에서 다시
        Assert.True((await Next(results)).Success);
    }

    [Fact]
    public async Task Auto_LinkDropsBeforeCloseIsHandled_ReportedAsDisconnected()
    {
        // 연결이 끊겼지만 GemHost가 아직 DeviceClosed를 처리하지 못한 사이(시퀀스 취소 전)에 다음 단계를 보내면 전송은 InvalidOperationException("연결되어 있지 않습니다")을 던진다.
        // 전송 디스패처를 DeviceClosed에서 붙잡아(GemHost보다 먼저 구독) 그 순간을 매번 같게 만든다: S1F13 거절 → 재시도 대기(3초) 중 끊김 → 재시도 S1F13
        // 재시도 간격을 3초로 둬 부하가 커도 끊기 전에 재시도가 나가지 않게 한다.
        var results = new BlockingCollection<GemConnectResult>();
        using var fake = new FakeGemEquipment();
        fake.Reply(1, 13, "<L <B 1> <L>>");
        fake.Start();
        using var release = new ManualResetEventSlim();
        var hsms = GemHostKit.Transport(fake.Port);
        hsms.DeviceClosed += (s, e) => release.Wait(10000);
        using var host = new GemHost(hsms) { EstablishCommunicationRetry = 3, ConnectConfigureReports = false, ConnectEnableAlarms = false };
        host.ConnectSequenceCompleted += (s, e) => results.Add(e);
        try
        {
            host.Start();
            fake.Next(1, 13, 5000);
            fake.Hsms.Stop();                                                               // 끊고 다시 받지 않음(재시도 S1F13은 연결 없이 보내짐)
            // H2: 끊김 실패는 GemHost가 DeviceClosed를 처리할 때(CommunicationStateChanged 뒤) 알린다 → 붙잡은 동안에는 오지 않는다.
            // 재시도(3초 뒤)가 붙잡은 동안 나가도록 넉넉히 기다린 뒤 풀어 준다
            Assert.False(results.TryTake(out _, 6000));
            release.Set();
            var r = await Next(results, 10000);
            Assert.Equal(GemConnectStep.EstablishCommunication, r.FailedStep);
            var ex = Assert.IsType<SecsDisconnectedException>(r.Exception);
            Assert.IsType<InvalidOperationException>(ex.InnerException);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task SequenceWaitingForReply_DoesNotBlockDeadlineReplies()
    {
        var results = new BlockingCollection<GemConnectResult>();
        using var release = new ManualResetEventSlim();
        var (fake, host) = await GemHostKit.Fake(Auto(results, h =>
        {
            ((SecsHsms)h.Transport).T3 = 10000;                                             // 붙잡은 S1F18을 늦게 줘도 시퀀스가 T3에 걸리지 않게(부하 대비)
            h.CommandReplyTimeoutForTest = 500;
            h.MessageReceived += (s, e) => release.Wait(10000);
        }), f => f.Hold(1, 17));
        using var _ = fake;
        using var __ = host;
        fake.Next(1, 13);
        fake.Next(1, 17);                                                                   // 시퀀스가 S1F18을 기다리는 중
        await Assert.ThrowsAsync<SecsAbortException>(() => fake.SendAsync("S7F1 W"));     // 기한 응답(S7F0)이 가짜 장비의 T3(3초) 전에 옴(늦으면 SecsTimeoutException)
        release.Set();
        fake.Held.Take().Reply(new SecsMessage(1, 18, false, SecsItem.B(0)));
        Assert.True((await Next(results)).Success);
    }
}
