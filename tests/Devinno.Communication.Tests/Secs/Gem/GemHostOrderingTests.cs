using System.Collections.Concurrent;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Secs.Hsms;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Secs.Gem;

/// GemHost 연결 세대 보호와 표 갱신 순서(스펙 6G.5.4, 6G.6.2 H1~H3): 끊긴 연결의 S1F14는 통신 수립을 만들지 않고,
/// 자동 시퀀스의 끊김 실패는 상태 이벤트 뒤에 알리며, 리포트 표 갱신은 디스패처에서 와이어 순서로 반영된다.
[Collection(nameof(GemHostPoolCollection))]
public class GemHostOrderingTests
{
    // 가짜 장비(FakeHsmsPeer)에 Host(Active)가 접속해 Select하고 GEM이 연결을 알 때까지
    static (FakeHsmsPeer Peer, GemHost Host) Selected(Action<GemHost>? setup = null)
    {
        var peer = FakeHsmsPeer.Listen();
        var host = GemHostKit.Host(peer.Port, setup);
        host.Start();
        peer.Accept();
        peer.AnswerSelect();
        TestUtil.Wait(() => host.LinkUp);
        return (peer, host);
    }

    static SecsMessage Wire(SecsMessage m, uint sys) => m.WithHeader(sys, 1);

    static readonly SecsItem S1F14Body = SecsItem.L(SecsItem.B(0), SecsItem.L(SecsItem.A("M"), SecsItem.A("1")));

    // H1(P8): S1F14 응답 완료가 풀에 묶인 사이 연결이 끊기면, 늦게 처리된 COMMACK 0은 끊긴 연결의 것이라 통신 수립을 만들지 않는다
    [Fact]
    public async Task P8_S1F14ThenClose_PoolStarved_NotCommunicating()
    {
        var (peer, host) = Selected();
        using var _ = host;
        using var __ = peer;
        var log = GemHostKit.Log(host);
        var t = host.EstablishCommunicationAsync();
        var s1f13 = peer.ReadData();
        Assert.Equal((1, 13), (s1f13.Stream, s1f13.Function));
        using (var s = PoolStarver.Start())
        {
            peer.SendData(Wire(new SecsMessage(1, 14, false, S1F14Body), s1f13.SystemBytes));
            peer.Close();
            TestUtil.Wait(() => !host.LinkUp);
            Assert.False(s.ProbeRan);
        }
        Assert.Equal(0, await t.WaitAsync(TimeSpan.FromSeconds(5)));                        // COMMACK은 그대로 돌려줌 (막힌 구간 밖에서 await)
        Thread.Sleep(200);                                                                  // 앱 이벤트 스레드가 따라잡을 시간
        Assert.False(host.IsCommunicating);
        Assert.DoesNotContain("Comm NotCommunicating->Communicating", log);
    }

    // 자동 시퀀스(S1F17 붙잡음) + 끊김 알림(전송 ProtocolError, DeviceClosed 바로 앞)에서 디스패처를 holdMs 동안 붙잡는다
    static async Task<(FakeGemEquipment Fake, GemHost Host, ConcurrentQueue<string> Log, Func<bool> Blocked)> HeldAutoSequence(int holdMs, Action<GemHost>? more = null)
    {
        var log = new ConcurrentQueue<string>();
        bool arm = false, blocked = false;
        var (fake, host) = await GemHostKit.Fake(h =>
        {
            h.AutoConnectSequence = true;
            h.CommunicationStateChanged += (s, e) => log.Enqueue($"Comm {e.Previous}->{e.Current}");
            h.ConnectSequenceCompleted += (s, e) => log.Enqueue($"Result {e.FailedStep} {e.Exception?.GetType().Name}");
            ((SecsHsms)h.Transport).ProtocolError += (s, e) =>
            {
                if (!Volatile.Read(ref arm) || !e.Reason.Contains("상대가 연결을 닫았습니다", StringComparison.Ordinal)) return;
                Volatile.Write(ref blocked, true);
                Thread.Sleep(holdMs);
            };
            more?.Invoke(h);
        }, f => f.Hold(1, 17));
        fake.Next(1, 13);
        fake.Next(1, 17);
        Assert.True(fake.Held.TryTake(out _, 3000));
        await TestUtil.Until(() => host.IsCommunicating);
        await TestUtil.Until(() => log.Contains("Comm NotCommunicating->Communicating"));
        log.Enqueue("ARM");
        Volatile.Write(ref arm, true);
        fake.Drop();
        return (fake, host, log, () => Volatile.Read(ref blocked));
    }

    // H2(P9): 자동 시퀀스가 끊김으로 실패하면 CommunicationStateChanged(→ NotCommunicating) 다음 ConnectSequenceCompleted(SecsDisconnectedException)
    [Fact]
    public async Task P9_AutoSequenceDisconnect_StateChangeBeforeResult()
    {
        var (fake, host, log, blocked) = await HeldAutoSequence(300);
        using var _ = fake;
        using var __ = host;
        await TestUtil.Until(() => log.SkipWhile(l => l != "ARM").Any(l => l.StartsWith("Result ", StringComparison.Ordinal)), 5000);
        Assert.True(blocked());
        var after = log.SkipWhile(l => l != "ARM").Skip(1).ToList();
        int comm = after.IndexOf("Comm Communicating->NotCommunicating");
        int result = after.FindIndex(l => l.StartsWith("Result ", StringComparison.Ordinal));
        Assert.True(comm >= 0 && comm < result, string.Join(" | ", after));
        Assert.Matches("^Result (EstablishCommunication|RequestOnline) SecsDisconnectedException$", after[result]);
    }

    // D1: 끊긴 뒤 수동 수립은 보내지 않고 전송의 InvalidOperationException(지금과 같은 형식·문장)
    [Fact]
    public async Task Q4_ManualEstablishAfterDisconnect_NotSent_InvalidOperation()
    {
        var (fake, host) = await GemHostKit.Fake(h => ((SecsHsms)h.Transport).AutoReconnect = false);
        using var _ = fake;
        using var __ = host;
        await TestUtil.Until(() => host.LinkUp);
        fake.Drop();
        await TestUtil.Until(() => !host.LinkUp);
        var ex = Assert.Throws<InvalidOperationException>(() => host.EstablishCommunication());
        Assert.Equal(SecsSession.NotConnectedMessage, ex.Message);
        fake.NoMore();
    }

    // H2: 보류된 시퀀스 실패는 Stop이 알림 없이 버린다(Stop 뒤 이벤트 없음)
    [Fact]
    public async Task Q5_PendingSequenceResult_DroppedByStop()
    {
        var (fake, host, log, blocked) = await HeldAutoSequence(1000);
        using var _ = fake;
        using var __ = host;
        await TestUtil.Until(blocked, 3000);
        await Task.Delay(200);                                                              // 시퀀스가 끊김 실패를 받아 보류할 시간(디스패처는 붙잡힌 채)
        var stop = Task.Run(host.Stop);
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(1000);
        Assert.DoesNotContain(log, l => l.StartsWith("Result ", StringComparison.Ordinal));
    }

    // Review Focus: 전송 IsOpen 직후(GEM이 DeviceOpened를 아직 처리하지 않았을 수 있음) 부른 수동 시퀀스도 예외 없이 수립까지 간다
    [Fact]
    public async Task Manual_RightAfterTransportOpen_Establishes()
    {
        for (int i = 0; i < 20; i++)
        {
            using var fake = new FakeGemEquipment();
            fake.Start();
            using var host = GemHostKit.Host(fake.Port);
            host.Start();
            await TestUtil.Until(() => host.IsOpen, 5000);
            var result = host.RunConnectSequence();
            Assert.True(result.Success, $"#{i}: {result.FailedStep} {result.Ack} {result.Exception}");
            Assert.True(host.IsCommunicating, $"#{i}");
        }
    }

    // D1: GEM이 DeviceOpened를 처리하기 전에 수동 S1F13의 응답(COMMACK 0)이 돌아와도, 그 연결의 DeviceOpened를 처리할 때 수립이 반영된다
    [Fact]
    public async Task Manual_ReplyBeforeOpenedHandled_EstablishedWhenOpenedHandled()
    {
        using var fake = new FakeGemEquipment();
        fake.Start();
        using var release = new ManualResetEventSlim();
        var hsms = GemHostKit.Transport(fake.Port);
        hsms.DeviceOpened += (s, e) => release.Wait(10000);                                // GemHost보다 먼저 구독: GEM의 DeviceOpened 처리를 붙잡음
        using var host = new GemHost(hsms) { AutoConnectSequence = false };
        var log = GemHostKit.Log(host);
        try
        {
            host.Start();
            await TestUtil.Until(() => host.IsOpen, 5000);
            Assert.Equal(0, await Task.Run(host.EstablishCommunication).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(host.LinkUp);                                                     // 아직 GEM은 연결을 모름
            Assert.False(host.IsCommunicating);
        }
        finally { release.Set(); }
        await TestUtil.Until(() => host.IsCommunicating);
        await TestUtil.Until(() => log.Contains("Comm NotCommunicating->Communicating"));
        Assert.Equal("FAKE", host.ModelName);
    }

    // H3(P10): S2F34보다 먼저 받은 S6F11은 옛 표로 해석된다(디스패처를 붙잡아 S6F11과 S2F34를 한 세그먼트로)
    [Fact]
    public async Task P10_OldS6F11BeforeS2F34_InterpretedWithOldTable()
    {
        var events = new BlockingCollection<GemEventReport>();
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        bool arm = false;
        var (peer, host) = Selected(h =>
        {
            h.EventReceived += (s, e) => events.Add(e.Report);
            ((SecsHsms)h.Transport).ProtocolError += (s, e) =>
            {
                if (!Volatile.Read(ref arm) || !e.Reason.Contains("짝이 없는 응답", StringComparison.Ordinal)) return;
                entered.Set();
                release.Wait(10000);
            };
        });
        using var _ = host;
        using var __ = peer;
        try
        {
            var est = Task.Run(host.EstablishCommunication);
            var s1f13 = peer.ReadData();
            peer.SendData(Wire(new SecsMessage(1, 14, false, S1F14Body), s1f13.SystemBytes));
            Assert.Equal(0, await est.WaitAsync(TimeSpan.FromSeconds(5)));

            var def = Task.Run(() => host.DefineReports([(1000, [2001])]));
            var first = peer.ReadData();
            Assert.Equal((2, 33), (first.Stream, first.Function));
            peer.SendData(Wire(new SecsMessage(2, 34, false, SecsItem.B(0)), first.SystemBytes));
            Assert.Equal(0, await def.WaitAsync(TimeSpan.FromSeconds(5)));
            TestUtil.Wait(() => host.KnownReport(1000) is [var v] && v == new GemId(2001));

            Volatile.Write(ref arm, true);
            peer.SendData(Wire(new SecsMessage(1, 2, false, SecsItem.L()), 0xDEAD));       // 짝 없는 응답 → 전송 ProtocolError에서 디스패처를 붙잡음
            Assert.True(entered.Wait(3000));

            var t = host.DefineReportsAsync([(1000, [3001])]);
            var s2f33 = peer.ReadData();
            Assert.Equal((2, 33), (s2f33.Stream, s2f33.Function));
            var s6f11 = new SecsMessage(6, 11, true, SecsItem.L(SecsItem.U4(1), SecsItem.U4(5001),
                SecsItem.L(SecsItem.L(SecsItem.U4(1000), SecsItem.L(SecsItem.F4(25))))));
            peer.SendData(Wire(s6f11, 200), Wire(new SecsMessage(2, 34, false, SecsItem.B(0)), s2f33.SystemBytes));
            Assert.Equal(0, await t.WaitAsync(TimeSpan.FromSeconds(5)));                    // 표 갱신을 기다리지 않고 끝남(디스패처는 붙잡힌 채)
        }
        finally { release.Set(); }

        Assert.True(events.TryTake(out var report, 3000));
        Assert.Equal(SecsItem.F4(25), report!.GetValue(2001));                             // 옛 표
        Assert.Null(report.GetValue(3001));
        TestUtil.Wait(() => host.KnownReport(1000) is [var n] && n == new GemId(3001));    // 표 갱신은 그 뒤에 반영
    }
}
