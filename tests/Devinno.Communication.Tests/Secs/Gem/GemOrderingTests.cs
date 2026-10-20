using System.Collections.Concurrent;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Secs.Hsms;
using Devinno.Communication.Secs.Serial;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Secs.Gem;

/// GemEquipment 송신 완료의 순서(스펙 6G.5.1·6G.5.2): 완료는 디스패처에서 와이어 순서로 처리되므로, 응답과 Host의 다음 1차 메시지가
/// 한 세그먼트로 와도(스레드풀이 막혀 있어도) 응답이 먼저 반영된다. 지난 연결의 늦은 완료는 연결 세대(epoch)로 버린다.
[Collection(nameof(GemHostPoolCollection))]
public class GemOrderingTests
{
    static readonly SecsItem S1F14Body = SecsItem.L(SecsItem.B(0), SecsItem.L());

    // 장비를 시작하고 가짜 상대가 접속·Select한 뒤 장비가 먼저 보낸 S1F13을 읽는다
    static (FakeHsmsPeer Peer, SecsMessage S1F13) Selected(GemEquipment eq)
    {
        eq.Start();
        var peer = FakeHsmsPeer.Connect(GemKit.Port(eq));
        Assert.Equal(HsmsFrame.SelectOk, peer.Select());
        return (peer, peer.ReadData());
    }

    static string[] WithoutErrors(ConcurrentQueue<string> log) => [.. log.Where(l => !l.StartsWith("Error ", StringComparison.Ordinal))];

    // P1: S1F14(통신 수립)와 Host S1F17(ON-LINE 요청)이 한 세그먼트. 풀이 막혀 있어도 S1F14가 먼저 반영되어 S1F17이 ONLACK 0
    static GemEquipment RunP1()
    {
        var eq = GemKit.Equipment(GemKit.Sample);
        var (peer, s1f13) = Selected(eq);
        using (peer)
        using (var s = PoolStarver.Start())
        {
            peer.SendData(new SecsMessage(1, 14, false, S1F14Body).WithHeader(s1f13.SystemBytes, 1), new SecsMessage(1, 17, true).WithHeader(100, 1));
            var reply = peer.ReadData();
            Assert.Equal("S1F18 <B 0x00>", reply.ToSml());
            Assert.Equal(GemCommunicationState.Communicating, eq.CommunicationState);
            TestUtil.Wait(() => eq.ControlState == GemControlState.OnlineRemote);
            Assert.False(s.ProbeRan);
        }
        return eq;
    }

    [Fact]
    public void P1_S1F14AndS1F17InOneSegment_PoolStarved_OnlineAck()
    {
        using var eq = RunP1();
    }

    [Fact]
    public void P3_S1F2AndS1F15InOneSegment_PoolStarved_HostOffline()
    {
        using var eq = GemKit.Equipment(e => { GemKit.Sample(e); e.InitialControlState = GemControlState.EquipmentOffline; });
        var (peer, s1f13) = Selected(eq);
        using (peer)
        {
            peer.SendData(new SecsMessage(1, 14, false, S1F14Body).WithHeader(s1f13.SystemBytes, 1));
            TestUtil.Wait(() => eq.CommunicationState == GemCommunicationState.Communicating);
            using var s = PoolStarver.Start();
            Assert.True(eq.GoOnline());
            var s1f1 = peer.ReadData();
            Assert.Equal((1, 1), (s1f1.Stream, s1f1.Function));
            peer.SendData(new SecsMessage(1, 2, false, SecsItem.L()).WithHeader(s1f1.SystemBytes, 1), new SecsMessage(1, 15, true).WithHeader(101, 1));
            Assert.Equal("S1F16 <B 0x00>", peer.ReadData().ToSml());
            TestUtil.Wait(() => eq.ControlState == GemControlState.HostOffline);
            Assert.False(s.ProbeRan);
        }
    }

    [Fact]
    public async Task P7_E2b_T3ThenHostS1F3_PoolStarved_Aborted_EventOrder()
    {
        using var eq = GemKit.Equipment(e => { GemKit.Sample(e); e.InitialControlState = GemControlState.OnlineRemote; e.EventsEnabledByDefault = true; });
        using var host = await GemKit.Communicating(eq, h => h.Hold(6, 11));
        var log = GemKit.Log(eq);                            // 처음 통신 수립 뒤부터 기록
        host.Hold(1, 13);                                    // T3 뒤 다시 보내는 S1F13에 응답하지 않는다(NOT COMMUNICATING 유지)
        Assert.True(eq.TriggerEvent(5002));
        Assert.True(eq.TriggerEvent(5002));
        Assert.True(host.Held.TryTake(out _, 3000));         // 하나는 응답 대기, 하나는 대기열
        using (var s = PoolStarver.Start())
        {
            host.NextOf(9, 9, 5000);                          // 장비의 T3 → S9F9 (T3 완료는 S9F9보다 먼저 디스패처 대기열에 들어감)
            Assert.Throws<SecsAbortException>(() => host.Hsms.Send(new SecsMessage(1, 3, true, SecsItem.L())));   // 이미 NOT COMMUNICATING → S1F0
            Assert.False(s.ProbeRan);
        }
        await TestUtil.Until(() => WithoutErrors(log).Length >= 3, 5000);
        await Task.Delay(200);
        Assert.Equal(["SendFailed S6F11 Timeout", "SendFailed S6F11 NotCommunicating", "Comm Communicating->NotCommunicating"], WithoutErrors(log));
    }

    [Fact]
    public async Task E2c_GoOnlineS1F1T3_EventOrder()
    {
        using var eq = GemKit.Equipment(e => { GemKit.Sample(e); e.InitialControlState = GemControlState.EquipmentOffline; });
        using var host = await GemKit.Communicating(eq, h => h.Hold(1, 1));
        var log = GemKit.Log(eq);                            // 처음 통신 수립 뒤부터 기록
        host.Hold(1, 13);
        Assert.True(eq.GoOnline());
        await TestUtil.Until(() => WithoutErrors(log).Length >= 4, 6000);
        await Task.Delay(200);
        Assert.Equal(["Control EquipmentOffline->AttemptOnline", "SendFailed S1F1 Timeout", "Control AttemptOnline->EquipmentOffline", "Comm Communicating->NotCommunicating"], WithoutErrors(log));
    }

    // C1a: W비트 없는 GEM 송신(완료가 응답 없이 null)도 모두 차례로 나간다
    static void NoWBit(GemEquipment e)
    {
        GemKit.Sample(e);
        e.InitialControlState = GemControlState.OnlineRemote;
        e.EventsEnabledByDefault = e.AlarmsEnabledByDefault = true;
        e.EventReplyExpected = e.AlarmReplyExpected = e.TerminalReplyExpected = false;
    }

    static void SendNoWBitBurst(GemEquipment eq)
    {
        for (int i = 0; i < 3; i++) Assert.True(eq.TriggerEvent(5002));
        Assert.True(eq.SetAlarm(1));
        Assert.True(eq.SendTerminalMessage(0, "hi"));
    }

    static readonly (int, int, bool)[] NoWBitExpected = [(6, 11, false), (6, 11, false), (6, 11, false), (5, 1, false), (6, 11, false), (10, 1, false)];

    static async Task<GemEquipment> RunNoWBitHsms()
    {
        var eq = GemKit.Equipment(NoWBit);
        var log = GemKit.Log(eq);
        using var host = await GemKit.Communicating(eq);
        SendNoWBitBurst(eq);
        foreach (var (st, f, w) in NoWBitExpected)
        {
            var m = host.NextOf(st, f, 5000);
            Assert.Equal((st, f, w), (m.Stream, m.Function, m.ReplyExpected));
        }
        await Task.Delay(200);
        Assert.DoesNotContain(log, l => l.StartsWith("SendFailed", StringComparison.Ordinal));
        return eq;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task C1a_NoWBitMessages_AllSent(bool serial)
    {
        if (!serial)
        {
            using var h = await RunNoWBitHsms();
            return;
        }
        var (a, b) = MemoryLine.Pair();
        var eqLine = new SecsSerial { DeviceID = 1, T1 = 200, T2 = 1000, T3 = 3000, T4 = 2000 };
        eqLine.OpenLine = a.ToLine;
        using var eq = new GemEquipment(eqLine) { ModelName = "DV-100", SoftwareRevision = "1.0.0" };
        NoWBit(eq);
        var log = GemKit.Log(eq);
        using var host = new SecsSerial { Role = SecsRole.Host, DeviceID = 1, T1 = 200, T2 = 1000, T3 = 3000, T4 = 2000 };
        host.OpenLine = b.ToLine;
        var got = new BlockingCollection<SecsMessage>();
        host.PrimaryReceived += (s, e) =>
        {
            if (e.Message.Stream == 1 && e.Message.Function == 13) { e.Reply(new SecsMessage(1, 14, false, S1F14Body)); return; }
            got.Add(e.Message);
        };
        host.Start();
        eq.Start();
        await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.Communicating, 5000);
        SendNoWBitBurst(eq);
        foreach (var (st, f, w) in NoWBitExpected)
        {
            Assert.True(got.TryTake(out var m, 5000), $"S{st}F{f}이(가) 오지 않았습니다.");
            Assert.Equal((st, f, w), (m!.Stream, m.Function, m.ReplyExpected));
        }
        await Task.Delay(200);
        Assert.DoesNotContain(log, l => l.StartsWith("SendFailed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Completed_NeverSendsOnDispatcher()
    {
        using (var eq = await RunNoWBitHsms()) Assert.Equal(0, eq.DispatcherSendCount);
        using (var eq = RunP1()) Assert.Equal(0, eq.DispatcherSendCount);
    }

    // 회귀: 지난 연결의 늦은 실패가 새 연결을 건드리지 않는다. 그 연결의 DeviceClosed(전이 14)가 이미 inFlight를 비웠으므로 inFlight != o로 버려진다(epoch 검사까지 가지 않음)
    [Fact]
    public void C2_LateFailureOfPreviousLink_AfterReconnect_Ignored()
    {
        var fake = new FakeGemTransport();
        using var eq = new GemEquipment(fake.Transport) { ModelName = "DV-100", SoftwareRevision = "1.0.0" };
        GemKit.Sample(eq);
        eq.InitialControlState = GemControlState.OnlineRemote;
        eq.EventsEnabledByDefault = true;
        var log = GemKit.Log(eq);
        eq.Start();
        fake.Open();
        fake.Next(1, 13).Completed(Task.FromResult<SecsMessage?>(new SecsMessage(1, 14, false, S1F14Body)));
        TestUtil.Wait(() => eq.CommunicationState == GemCommunicationState.Communicating);
        Assert.True(eq.TriggerEvent(5002));
        var s6f11 = fake.Next(6, 11);                        // 완료를 붙잡아 둔다
        fake.Close();                                        // 전이 14: 응답 대기 중인 S6F11은 버림 경로(NotCommunicating)
        TestUtil.Wait(() => eq.CommunicationState == GemCommunicationState.NotCommunicating);
        fake.Open();                                         // 새 연결(새 epoch)
        var s1f13 = fake.Next(1, 13);
        s6f11.Completed(Task.FromException<SecsMessage?>(new SecsDisconnectedException("지난 연결의 쓰기 실패")));   // 지난 연결의 늦은 실패
        Thread.Sleep(200);
        Assert.Equal(GemCommunicationState.NotCommunicating, eq.CommunicationState);
        s1f13.Completed(Task.FromResult<SecsMessage?>(new SecsMessage(1, 14, false, S1F14Body)));   // 새 S1F13은 그대로 응답 대기였음
        TestUtil.Wait(() => eq.CommunicationState == GemCommunicationState.Communicating);
        Thread.Sleep(200);
        Assert.DoesNotContain(log, l => l.StartsWith("SendFailed S6F11 Disconnected", StringComparison.Ordinal));
        Assert.DoesNotContain(log, l => l.StartsWith("Error ", StringComparison.Ordinal));
        Assert.Equal(["SendFailed S6F11 NotCommunicating"], GemKit.Only(log, "SendFailed"));
    }

    // 지난 연결의 S1F13(응답 대기 중 끊김)의 늦은 실패: DeviceClosed가 WAIT CRA를 정리해 WAIT DELAY로 보냈으므로 재연결 때 새 S1F13이 바로 나가고,
    // 늦은 실패는 버려져(inFlight != o) 새 S1F13은 그대로 응답 대기, ProtocolError·상태 변화 없음
    [Fact]
    public void C2_LateS1F13FailureOfPreviousLink_NewS1F13StaysInFlight()
    {
        var fake = new FakeGemTransport();
        using var eq = new GemEquipment(fake.Transport) { ModelName = "DV-100", SoftwareRevision = "1.0.0" };
        GemKit.Sample(eq);
        var log = GemKit.Log(eq);
        eq.Start();
        fake.Open();
        var stale = fake.Next(1, 13);                        // 완료를 붙잡아 둔다
        fake.Close();                                        // WAIT CRA 중 끊김 → WAIT DELAY(알림 없음)
        fake.Open();                                         // 새 연결: CommDelay를 기다리지 않고 바로 S1F13
        var s1f13 = fake.Next(1, 13, 1000);
        stale.Completed(Task.FromException<SecsMessage?>(new SecsDisconnectedException("지난 연결의 쓰기 실패")));
        Thread.Sleep(200);
        Assert.Equal(GemCommunicationState.NotCommunicating, eq.CommunicationState);
        Assert.Empty(log);
        s1f13.Completed(Task.FromResult<SecsMessage?>(new SecsMessage(1, 14, false, S1F14Body)));   // 새 S1F13은 그대로 응답 대기였음
        TestUtil.Wait(() => eq.CommunicationState == GemCommunicationState.Communicating);
        Thread.Sleep(100);
        Assert.Equal(["Comm NotCommunicating->Communicating"], [.. log]);
        Assert.False(fake.Sent.TryTake(out _, 100));       // S1F13을 한 번 더 보내지 않음
    }

    // P2: S1F14 직후 끊김(풀 고갈). S1F14 성공 완료가 먼저(COMMUNICATING), DeviceClosed가 전이 14 → NOT COMMUNICATING.
    // 재연결 뒤에는 S1F13으로 다시 수립하기 전까지 S6F11을 보내지 않는다(E5)
    [Fact]
    public void P2_S1F14ThenClose_PoolStarved_EndsNotCommunicating_ReestablishesOnReconnect()
    {
        using var eq = GemKit.Equipment(e => { GemKit.Sample(e); e.InitialControlState = GemControlState.OnlineRemote; e.EventsEnabledByDefault = true; });
        var (peer, s1f13) = Selected(eq);
        using (peer)
        {
            using (var s = PoolStarver.Start())
            {
                peer.SendData(new SecsMessage(1, 14, false, S1F14Body).WithHeader(s1f13.SystemBytes, 1));
                peer.Close();
                TestUtil.Wait(() => !eq.IsOpen);
            }
            Thread.Sleep(300);
            Assert.Equal(GemCommunicationState.NotCommunicating, eq.CommunicationState);
        }
        using var peer2 = FakeHsmsPeer.Connect(GemKit.Port(eq));
        Assert.Equal(HsmsFrame.SelectOk, peer2.Select());
        Assert.False(eq.TriggerEvent(5002));                 // 링크 없이 COMMUNICATING으로 남지 않음
        var first = peer2.ReadData();
        Assert.Equal((1, 13), (first.Stream, first.Function));
    }

    // P4·E2a·E3·E4: COMMUNICATING에서 끊김(응답 대기 1 + 대기열 2). 버림 알림이 보낼 순서대로 → 상태 변화 한 번, Disconnected·S1F13 실패 알림 없음, 그 뒤 이벤트 없음
    [Fact]
    public async Task P4_E2a_E3_E4_CloseWithQueuedEvents_DiscardInOrder_OneStateChange()
    {
        using var eq = GemKit.Equipment(e => { GemKit.Sample(e); e.InitialControlState = GemControlState.OnlineRemote; e.EventsEnabledByDefault = true; });
        var host = await GemKit.Communicating(eq, h => h.Hold(6, 11));
        var log = GemKit.Log(eq);
        for (int i = 0; i < 3; i++) Assert.True(eq.TriggerEvent(5002));
        Assert.True(host.Held.TryTake(out _, 3000));         // 하나는 응답 대기, 둘은 대기열
        host.Dispose();
        await TestUtil.Until(() => GemKit.Only(log, "Comm").Length >= 1, 5000);
        int n = log.Count;
        await Task.Delay(500);
        Assert.Equal(["SendFailed S6F11 NotCommunicating", "SendFailed S6F11 NotCommunicating", "SendFailed S6F11 NotCommunicating", "Comm Communicating->NotCommunicating"], [.. log]);
        Assert.Equal(n, log.Count);                          // 그 연결에 관한 이벤트가 더 오지 않음
    }

    // P5: 끊긴 직후(GEM이 DeviceClosed를 아직 처리하지 않았을 수 있음) 통신을 켜도 죽은 링크로 S1F13 실패가 나지 않고, 재연결 때 바로 S1F13
    [Fact]
    public void P5_EnableCommunicationRightAfterClose_NoDeadLinkS1F13()
    {
        for (int i = 0; i < 20; i++)
        {
            using var eq = GemKit.Equipment(e => { GemKit.Sample(e); e.InitialCommunicationState = GemEnableState.Disabled; });
            var log = GemKit.Log(eq);
            eq.Start();
            using (var peer = FakeHsmsPeer.Connect(GemKit.Port(eq)))
            {
                Assert.Equal(HsmsFrame.SelectOk, peer.Select());
                TestUtil.Wait(() => eq.IsOpen);
                peer.Close();
                Assert.True(eq.EnableCommunication());
                Thread.Sleep(300);
            }
            Assert.DoesNotContain(log, l => l.StartsWith("Error S1F13 통신 수립 실패", StringComparison.Ordinal));
            using var peer2 = FakeHsmsPeer.Connect(GemKit.Port(eq));
            Assert.Equal(HsmsFrame.SelectOk, peer2.Select());
            var m = peer2.ReadData(1000);
            Assert.Equal((1, 13), (m.Stream, m.Function));
        }
    }

    // P6: WAIT CRA 중 끊김(풀 고갈) → WAIT DELAY. 재연결하면 EstablishCommunicationsTimeout(10초)을 기다리지 않고 바로 S1F13
    [Fact]
    public void P6_CloseDuringWaitCra_Reconnect_S1F13WithoutCommDelay_PoolStarved()
    {
        using var eq = GemKit.Equipment(GemKit.Sample);
        var log = GemKit.Log(eq);
        var (peer1, _) = Selected(eq);                       // S1F13에 응답하지 않는다
        FakeHsmsPeer peer2;
        using (PoolStarver.Start())
        {
            peer1.Close();
            peer1.Dispose();
            peer2 = FakeHsmsPeer.Connect(GemKit.Port(eq));
            Assert.Equal(HsmsFrame.SelectOk, peer2.Select());
            var m = peer2.ReadData(2000);
            Assert.Equal((1, 13), (m.Stream, m.Function));
        }
        using var p2 = peer2;
        Thread.Sleep(200);
        Assert.Empty(log);                                   // 끊김으로 놓은 S1F13은 알리지 않는다(원인이 분명함, 6G.5.3)
    }
}
