using System.Collections.Concurrent;
using System.Diagnostics;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Secs.Hsms;
using Devinno.Communication.Secs.Serial;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Secs.Gem;

/// GemHost 골격: 만들기·수명·설정, 장비 1차 메시지 자동 응답(S1F1·S1F13·S2F17·S6F5·S9), 통신 수립 여부, MessageReceived(미룬 응답·기한), 스레드
[Collection(nameof(GemHostCollection))]
public class GemHostCoreTests
{
    [Fact]
    public void Constructor_SetsHostRole_RejectsStartedTransportAndNull()
    {
        var hsms = new SecsHsms { Role = SecsRole.Equipment };
        using var host = new GemHost(hsms);
        Assert.Equal(SecsRole.Host, hsms.Role);
        Assert.Same(hsms, host.Transport);
        var serial = new SecsSerial { Role = SecsRole.Equipment };
        using var host2 = new GemHost(serial);
        Assert.Equal(SecsRole.Host, serial.Role);
        Assert.Throws<ArgumentNullException>(() => new GemHost((SecsHsms)null!));
        Assert.Throws<ArgumentNullException>(() => new GemHost((SecsSerial)null!));
        using var started = new SecsHsms { Mode = HsmsMode.Passive, LocalIP = "127.0.0.1", LocalPort = 0 };
        started.Start();
        Assert.Throws<InvalidOperationException>(() => new GemHost(started));
    }

    [Fact]
    public void Start_ResetsTransportRoleToHost()
    {
        var hsms = GemHostKit.Transport(1);
        using var host = new GemHost(hsms) { AutoConnectSequence = false };
        hsms.Role = SecsRole.Equipment;                                                     // 예: 전송 객체를 직접 바꿈(LoadXml 등)
        host.Start();
        Assert.Equal(SecsRole.Host, hsms.Role);
        host.Stop();

        var (_, line) = MemoryLine.Pair();
        var serial = new SecsSerial { DeviceID = 1 };
        serial.OpenLine = line.ToLine;
        using var host2 = new GemHost(serial) { AutoConnectSequence = false };
        serial.Role = SecsRole.Equipment;
        host2.Start();
        Assert.Equal(SecsRole.Host, serial.Role);
        host2.Stop();
    }

    [Fact]
    public void Settings_DefaultsAndValidation_LockedWhileRunning()
    {
        using var host = GemHostKit.Host(1);
        Assert.Equal((SecsFormat.U4, SecsFormat.U4, SecsFormat.U4, SecsFormat.U4, SecsFormat.U4), (host.VidFormat, host.CeidFormat, host.RptidFormat, host.AlidFormat, host.DataidFormat));
        Assert.Equal((1, 10000, 10000), (host.TimeFormat, host.CommandReplyTimeout, host.MaxPendingEvents));
        Assert.Throws<ArgumentException>(() => host.VidFormat = SecsFormat.F4);
        Assert.Throws<ArgumentException>(() => host.RptidFormat = SecsFormat.Binary);
        Assert.Throws<ArgumentOutOfRangeException>(() => host.TimeFormat = 3);
        Assert.Throws<ArgumentOutOfRangeException>(() => host.CommandReplyTimeout = 999);                    // XML과 같은 범위 1000~110000
        Assert.Throws<ArgumentOutOfRangeException>(() => host.CommandReplyTimeout = 110001);
        host.CommandReplyTimeout = 110000;
        host.CommandReplyTimeout = 1000;
        Assert.Throws<ArgumentOutOfRangeException>(() => host.MaxPendingEvents = 1000001);
        host.CeidFormat = SecsFormat.ASCII;
        host.Start();
        Assert.Throws<InvalidOperationException>(() => host.VidFormat = SecsFormat.U2);
        Assert.Throws<InvalidOperationException>(() => host.TimeFormat = 0);
        host.Stop();
        host.VidFormat = SecsFormat.U2;
        Assert.Equal((SecsFormat.U2, SecsFormat.ASCII), (host.VidFormat, host.CeidFormat));
    }

    [Fact]
    public async Task StartStopDispose_Idempotent_TransportFollows()
    {
        var (fake, host) = await GemHostKit.Fake();
        using var _ = fake;
        host.Start();                                                                       // 두 번째 Start는 아무것도 안 함
        Assert.True(host.IsStart && ((SecsHsms)host.Transport).IsStart);
        host.Stop();
        host.Stop();
        Assert.False(host.IsStart || ((SecsHsms)host.Transport).IsStart);
        host.Start();
        await TestUtil.Until(() => host.IsOpen, 5000);
        host.Dispose();
        host.Dispose();
        Assert.True(host.IsDisposed && ((SecsHsms)host.Transport).IsDisposed);
        Assert.Throws<ObjectDisposedException>(host.Start);
    }

    [Theory]
    [InlineData(0, 12)]
    [InlineData(1, 16)]
    [InlineData(2, 0)]
    public async Task AutoReplies_S1F1_S2F17_S6F5(int timeFormat, int length)
    {
        var (fake, host) = await GemHostKit.Fake(h => h.TimeFormat = timeFormat);
        using var _ = fake;
        using var __ = host;
        Assert.Equal("S1F2 <L [0]>", await fake.Ask("S1F1 W"));
        Assert.Equal("S6F6 <B 0x00>", await fake.Ask("S6F5 W <L <U4 1> <U4 100>>"));
        var time = (await fake.SendAsync("S2F17 W"))!.Item!.GetString();
        if (length > 0) Assert.Equal(length, time.Length);
        else Assert.Contains('T', time);
        Assert.True(GemTime.TryParse(time, out var t));
        Assert.True(Math.Abs((t - DateTime.Now).TotalMinutes) < 1, time);
    }

    [Fact]
    public async Task EquipmentS1F13_Commack0_Communicating_StoresModel_LinkLossNotCommunicating()
    {
        var (fake, host) = await GemHostKit.Fake();
        using var _ = fake;
        using var __ = host;
        var log = GemHostKit.Log(host);
        Assert.False(host.IsCommunicating);
        Assert.Equal("S1F14 <L [2] <B 0x00> <L [0]>>", await fake.Ask("S1F13 W <L <A \"DV-100\"> <A \"1.0.0\">>"));
        await TestUtil.Until(() => host.IsCommunicating);                                  // 응답을 보낸 뒤 표시
        Assert.Equal(("DV-100", "1.0.0"), (host.ModelName, host.SoftwareRevision));
        Assert.Equal("S1F14 <L [2] <B 0x00> <L [0]>>", await fake.Ask("S1F13 W <L>"));      // 수립 중에도 0, 이름은 그대로(빈 목록)
        Assert.Equal("DV-100", host.ModelName);
        fake.Drop();                                                                        // 연결이 끊김 → 통신 수립 아님
        await TestUtil.Until(() => !host.IsCommunicating, 5000);
        await TestUtil.Until(() => log.Count(l => l.StartsWith("Comm", StringComparison.Ordinal)) == 2);
        Assert.Equal(["Comm NotCommunicating->Communicating", "Comm Communicating->NotCommunicating"], log.Where(l => l.StartsWith("Comm", StringComparison.Ordinal)));
        host.Stop();
        host.Start();
        Assert.Equal("", host.ModelName);                                                   // Start마다 처음 값
    }

    [Fact]
    public async Task EquipmentS9_ProtocolErrorOnly_NoReply()
    {
        var (fake, host) = await GemHostKit.Fake();
        using var _ = fake;
        using var __ = host;
        var log = GemHostKit.Log(host);
        Assert.Null(await fake.SendAsync("S9F9 <B 0x00 0x01 0x81 0x01 0x00 0x00 0x00 0x00 0x00 0x07>"));
        await TestUtil.Until(() => log.Any(l => l.StartsWith("Error 장비가 S9F9", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task MessageReceived_HandlerReplies_OnAppThread()
    {
        string? thread = null;
        var (fake, host) = await GemHostKit.Fake(h => h.MessageReceived += (s, e) =>
        {
            thread = Thread.CurrentThread.Name;
            e.Reply(new SecsMessage(7, 2, false, SecsItem.B(0)));
        });
        using var _ = fake;
        using var __ = host;
        Assert.Equal("S7F2 <B 0x00>", await fake.Ask("S7F1 W <L <A \"PP1\"> <U4 100>>"));
        Assert.Equal("GemHost.Events", thread);
    }

    [Fact]
    public async Task MessageReceived_NoSubscriber_NoReply_Throws_AllSxF0()
    {
        var (fake, host) = await GemHostKit.Fake();
        using var _ = fake;
        using var __ = host;
        var log = GemHostKit.Log(host);
        await Assert.ThrowsAsync<SecsAbortException>(() => fake.SendAsync("S7F1 W"));     // 구독자 없음
        EventHandler<GemMessageEventArgs> silent = (s, e) => { };
        host.MessageReceived += silent;
        await Assert.ThrowsAsync<SecsAbortException>(() => fake.SendAsync("S7F1 W"));     // 응답하지 않음
        host.MessageReceived -= silent;
        host.MessageReceived += (s, e) => throw new InvalidOperationException("Host 핸들러 오류");
        await Assert.ThrowsAsync<SecsAbortException>(() => fake.SendAsync("S7F1 W"));     // 예외
        await TestUtil.Until(() => log.Any(l => l.Contains("Host 핸들러 오류", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task MessageReceived_HandlerTooSlow_SxF0ByDeadline_LateReplyRejected()
    {
        using var release = new ManualResetEventSlim();
        var late = new ConcurrentQueue<string>();
        var (fake, host) = await GemHostKit.Fake(h =>
        {
            h.CommandReplyTimeoutForTest = 500;
            h.MessageReceived += (s, e) =>
            {
                release.Wait(5000);
                try { e.Reply(new SecsMessage(7, 2, false)); } catch (InvalidOperationException ex) { late.Enqueue(ex.Message); }
            };
        });
        using var _ = fake;
        using var __ = host;
        var log = GemHostKit.Log(host);
        await Assert.ThrowsAsync<SecsAbortException>(() => fake.SendAsync("S7F1 W"));     // 기한(0.5초)에 작업 스레드가 S7F0(가짜 장비의 T3 3초 전, 늦으면 SecsTimeoutException)
        release.Set();
        await TestUtil.Until(() => late.Count == 1);
        Assert.Contains("CommandReplyTimeout", late.Single());
        Assert.Contains(log, l => l.Contains("S7F0으로 대신 응답", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MessageReceived_WithoutWBit_EventOnly_ReplyThrows()
    {
        var results = new BlockingCollection<string>();
        var (fake, host) = await GemHostKit.Fake(h => h.MessageReceived += (s, e) =>
        {
            try { e.Reply(new SecsMessage(7, 4, false)); results.Add("replied"); }
            catch (InvalidOperationException) { results.Add($"noW S{e.Message.Stream}F{e.Message.Function}"); }
        });
        using var _ = fake;
        using var __ = host;
        Assert.Null(await fake.SendAsync("S7F3 <L>"));
        Assert.True(results.TryTake(out var r, 3000));
        Assert.Equal("noW S7F3", r);
    }

    [Fact]
    public async Task MessageReceived_ReplyAfterStop_Rejected()
    {
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var late = new ConcurrentQueue<string>();
        var (fake, host) = await GemHostKit.Fake(h => h.MessageReceived += (s, e) =>
        {
            entered.Set();
            release.Wait(10000);
            try { e.Reply(new SecsMessage(7, 2, false)); } catch (Exception ex) { late.Enqueue($"{ex.GetType().Name}: {ex.Message}"); }
        });
        using var _ = fake;
        using var __ = host;
        var ask = fake.SendAsync("S7F1 W");
        Assert.True(entered.Wait(3000));
        var stopping = Task.Run(host.Stop);
        await TestUtil.Until(() => !host.IsStart);
        release.Set();
        await stopping;
        await Assert.ThrowsAnyAsync<Exception>(() => ask);
        await TestUtil.Until(() => late.Count == 1);
        Assert.StartsWith("InvalidOperationException", late.Single());
        Assert.Contains("다시 시작", late.Single());
    }

    [Fact]
    public async Task StuckMessageHandler_DoesNotBlockAutoReplies_EventQueueBounded()
    {
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var (fake, host) = await GemHostKit.Fake(h =>
        {
            h.MaxPendingEvents = 2;
            h.MessageReceived += (s, e) => { entered.Set(); release.Wait(10000); };
        });
        using var _ = fake;
        using var __ = host;
        Assert.Null(await fake.SendAsync("S7F3"));                                           // 핸들러가 앱 이벤트 스레드를 붙잡음
        Assert.True(entered.Wait(3000));
        for (int i = 0; i < 5; i++) Assert.Null(await fake.SendAsync("S7F3"));
        var sw = Stopwatch.StartNew();
        Assert.Equal("S1F2 <L [0]>", await fake.Ask("S1F1 W"));                            // 자동 응답은 막히지 않음
        Assert.True(sw.ElapsedMilliseconds < 2500, $"{sw.ElapsedMilliseconds}ms");    // 핸들러는 10초 멈춤, 부하를 감안해 T3(3초)보다 짧게
        await TestUtil.Until(() => host.DroppedEventCount == 3);                           // 5개 중 2개만 남음
        release.Set();
    }
}
