using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 트레이스 표본 추출과 S6F1 보고: 가짜 시계로 결정적으로(묶음·나머지·스냅숏·늦은 틱·OFF-LINE·끊김·스풀·Stop), GemLoop 대기
public class GemTraceReportTests
{
    sealed class Clock
    {
        long now;
        public long Now => Interlocked.Read(ref now);
        public void Add(long ms) => Interlocked.Add(ref now, ms);
    }

    static GemEquipment Eq(Clock clock, Action<GemEquipment>? more = null) => GemKit.Equipment(e =>
    {
        GemKit.Sample(e);
        e.InitialControlState = GemControlState.OnlineRemote;
        e.TraceClock = () => clock.Now;
        more?.Invoke(e);
    });

    static async Task Define(FakeGemHost host, int total, int group, string period = "000001", string svids = "<U4 2001> <U4 2002>", int trid = 1)
        => Assert.Equal("S2F24 <B 0x00>", await host.Ask($"S2F23 W <L <U4 {trid}> <A \"{period}\"> <U4 {total}> <U4 {group}> <L {svids}>>"));

    static ulong Remaining(GemEquipment eq) => eq.ActiveTraces.FirstOrDefault()?.RemainingSamples ?? 0;

    // 시계를 ms만큼 움직이고 표본이 하나 떠질 때까지 기다린다(다음 이동이 늦은 틱으로 합쳐지지 않게)
    static async Task Step(GemEquipment eq, Clock clock, long ms = 1000)
    {
        var before = eq.ActiveTraces.Single().RemainingSamples;
        clock.Add(ms);
        await TestUtil.Until(() => Remaining(eq) < before, 3000);
    }

    static ulong Smpln(SecsMessage s6f1) => s6f1.Item!.Items[1].GetUInt64();
    static IReadOnlyList<SecsItem> Values(SecsMessage s6f1) => s6f1.Item!.Items[3].Items;
    static string Stime(SecsMessage s6f1) => s6f1.Item!.Items[2].GetString();

    [Fact]
    public async Task Normal_6Samples_Group2_ThreeReports()
    {
        var clock = new Clock();
        using var eq = Eq(clock);
        using var host = await GemKit.Communicating(eq);
        await Define(host, 6, 2);
        clock.Add(999);
        host.NoneOf(6, 300);
        clock.Add(1);
        await TestUtil.Until(() => Remaining(eq) == 5, 3000);
        host.NoneOf(6, 100);                                       // 묶음 2개가 차야 보냄
        var got = new List<SecsMessage>();
        for (int i = 2; i <= 6; i++)
        {
            if (i == 6) clock.Add(1000);
            else await Step(eq, clock);
            if (i % 2 == 0) got.Add(host.Next(6, 1));
        }
        Assert.Equal([2UL, 4UL, 6UL], got.Select(Smpln));
        foreach (var m in got)
        {
            Assert.Equal(SecsItem.U4(1), m.Item!.Items[0]);
            Assert.Equal(4, Values(m).Count);
            Assert.Equal(SecsItem.F4(25), Values(m)[0]);
            Assert.Equal(SecsItem.F4(25), Values(m)[2]);
            Assert.True(m.ReplyExpected);
        }
        await TestUtil.Until(() => eq.ActiveTraces.Count == 0, 3000);
        clock.Add(5000);
        host.NoneOf(6, 300);
    }

    [Fact]
    public async Task NotMultiple_LastReportPartial()
    {
        var clock = new Clock();
        using var eq = Eq(clock);
        using var host = await GemKit.Communicating(eq);
        await Define(host, 5, 2);
        var got = new List<SecsMessage>();
        for (int i = 1; i <= 5; i++)
        {
            if (i == 5) clock.Add(1000);
            else await Step(eq, clock);
            if (i % 2 == 0 || i == 5) got.Add(host.Next(6, 1));
        }
        Assert.Equal([2UL, 4UL, 5UL], got.Select(Smpln));
        Assert.Equal(2, Values(got[2]).Count);
    }

    [Fact]
    public async Task ValuesAreSnapshotAtSample()
    {
        var clock = new Clock();
        using var eq = Eq(clock);
        using var host = await GemKit.Communicating(eq);
        await Define(host, 2, 2);
        eq.SetValue(2002, SecsItem.U4(3));
        await Step(eq, clock);
        eq.SetValue(2002, SecsItem.U4(7));
        clock.Add(1000);
        var m = host.Next(6, 1);
        Assert.Equal(SecsItem.U4(3), Values(m)[1]);
        Assert.Equal(SecsItem.U4(7), Values(m)[3]);
    }

    [Fact]
    public async Task StandardSv_Clock_Sampled()
    {
        var clock = new Clock();
        using var eq = Eq(clock);
        using var host = await GemKit.Communicating(eq);
        await Define(host, 1, 1, svids: "<U4 1001>");
        clock.Add(1000);
        var v = Assert.Single(Values(host.Next(6, 1)));
        Assert.Equal(SecsFormat.ASCII, v.Format);
        Assert.True(GemTime.TryParse(v.GetString(), out _));
    }

    [Fact]
    public async Task StimeIsFirstSampleTime()
    {
        var clock = new Clock();
        using var eq = Eq(clock);
        using var host = await GemKit.Communicating(eq);
        await Define(host, 2, 1);
        await Step(eq, clock);
        var a = host.Next(6, 1);
        clock.Add(1000);
        var b = host.Next(6, 1);
        Assert.True(GemTime.TryParse(Stime(a), out var ta));
        Assert.True(GemTime.TryParse(Stime(b), out var tb));
        Assert.True(tb >= ta);
        Assert.Equal(16, Stime(a).Length);                         // 기본 TimeFormat 1
    }

    [Fact]
    public async Task LateTick_OneSample_NoCatchUp()
    {
        var clock = new Clock();
        using var eq = Eq(clock);
        using var host = await GemKit.Communicating(eq);
        await Define(host, 5, 1);
        clock.Add(3500);                                           // 1000·2000·3000을 지나침
        Assert.Equal(1UL, Smpln(host.Next(6, 1)));
        host.NoneOf(6, 200);
        clock.Add(999);                                            // 4499: 건너뛴 뒤 다음 예정은 3500 + 1000
        host.NoneOf(6, 300);
        clock.Add(1);
        Assert.Equal(2UL, Smpln(host.Next(6, 1)));
    }

    [Fact]
    public async Task SkippedSlot_FlushesPartialGroup_StimeStaysExact()
    {
        // 칸을 건너뛰면 그 전까지 모은 표본을 먼저 보낸다: 보고마다 STIME + i·DSPER가 맞도록
        var clock = new Clock();
        using var eq = Eq(clock);
        using var host = await GemKit.Communicating(eq);
        await Define(host, 4, 4);
        await Step(eq, clock);                                     // 표본 1 (1000)
        clock.Add(2500);                                           // 3500: 2000·3000 칸을 건너뜀
        var first = host.Next(6, 1);
        Assert.Equal(1UL, Smpln(first));
        Assert.Equal(2, Values(first).Count);                      // 표본 1개분
        await TestUtil.Until(() => Remaining(eq) == 2, 3000);      // 표본 2 (3500, 새 묶음, 다음 예정은 4500)
        clock.Add(999);
        host.NoneOf(6, 200);
        await Step(eq, clock, 1);                                  // 표본 3 (4500)
        clock.Add(1000);                                           // 표본 4 (5500) → 끝
        var last = host.Next(6, 1);
        Assert.Equal(4UL, Smpln(last));
        Assert.Equal(6, Values(last).Count);                       // 표본 3개분
    }

    [Fact]
    public async Task Reports_UseAtMostHalfTheSendQueue_EventsStillQueued()
    {
        // Host가 S6F1에 늦게 답해도 트레이스 보고는 대기열의 절반까지만: 이벤트·알람 자리가 남는다
        var clock = new Clock();
        using var eq = Eq(clock, e => { e.MaxPendingSends = 4; e.EventsEnabledByDefault = true; });
        var log = GemKit.Log(eq);
        using var host = await GemKit.Communicating(eq, h => h.Hold(6, 1));
        await Define(host, 10, 1);
        for (int i = 0; i < 6; i++) await Step(eq, clock);
        await TestUtil.Until(() => GemKit.Only(log, "SendFailed S6F1 QueueFull").Length > 0, 3000);
        Assert.True(eq.TriggerEvent(5002));
        Assert.True(eq.TriggerEvent(5002));
        Assert.Empty(GemKit.Only(log, "SendFailed S6F11"));
    }

    [Fact]
    public async Task Offline_ReportsDropped_ResumeAfterOnline()
    {
        var clock = new Clock();
        using var eq = Eq(clock);
        var log = GemKit.Log(eq);
        using var host = await GemKit.Communicating(eq);
        await Define(host, 5, 1);
        Assert.True(eq.GoOffline());
        await Step(eq, clock);
        await Step(eq, clock);
        host.NoneOf(6, 300);
        Assert.Empty(GemKit.Only(log, "SendFailed"));
        Assert.True(eq.GoOnline());
        await TestUtil.Until(() => eq.ControlState is GemControlState.OnlineRemote or GemControlState.OnlineLocal, 3000);
        clock.Add(1000);
        Assert.Equal(3UL, Smpln(host.NextOf(6, 1)));
    }

    [Fact]
    public async Task AckNonZero_SendFailed()
    {
        var clock = new Clock();
        using var eq = Eq(clock);
        var log = GemKit.Log(eq);
        using var host = await GemKit.Communicating(eq, h => h.Reply(6, 1, "<B 1>"));
        await Define(host, 1, 1);
        clock.Add(1000);
        host.Next(6, 1);
        await TestUtil.Until(() => GemKit.Only(log, "SendFailed").Length > 0, 3000);
        Assert.Equal("SendFailed S6F1 NotAcknowledged 1", GemKit.Only(log, "SendFailed").Single());
    }

    [Fact]
    public async Task TraceReplyExpectedFalse_NoWbit()
    {
        var clock = new Clock();
        using var eq = Eq(clock, e => e.TraceReplyExpected = false);
        using var host = await GemKit.Communicating(eq);
        await Define(host, 1, 1);
        clock.Add(1000);
        Assert.False(host.Next(6, 1).ReplyExpected);
    }

    [Fact]
    public async Task Disconnected_Discarded_TraceContinues()
    {
        var clock = new Clock();
        using var eq = Eq(clock);
        var log = GemKit.Log(eq);
        var host = await GemKit.Communicating(eq);
        await Define(host, 5, 1);
        host.Dispose();
        await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.NotCommunicating, 5000);
        await Step(eq, clock);
        await TestUtil.Until(() => GemKit.Only(log, "SendFailed S6F1").Length > 0, 3000);
        Assert.Equal("SendFailed S6F1 NotCommunicating", GemKit.Only(log, "SendFailed S6F1").Single());
        Assert.Equal(4UL, Remaining(eq));
    }

    [Fact]
    public async Task Disconnected_SpoolTarget_Spooled()
    {
        var dir = Path.Combine(Path.GetTempPath(), "devinno-trace-" + Guid.NewGuid());
        try
        {
            var clock = new Clock();
            using var eq = Eq(clock, e => e.SpoolDirectory = dir);
            var host = await GemKit.Communicating(eq);
            Assert.Equal("S2F44 <L [2] <B 0x00> <L [0]>>", await host.Ask("S2F43 W <L <L <U1 6> <L <U1 1>>>>"));
            await Define(host, 5, 1);
            host.Dispose();
            await TestUtil.Until(() => eq.SpoolState == GemSpoolState.NoOutput, 5000);
            await Step(eq, clock);
            await Step(eq, clock);
            await TestUtil.Until(() => eq.SpoolCount == 2, 3000);
            using var host2 = new FakeGemHost(GemKit.Port(eq));
            host2.Start();
            host2.Next(1, 13, 5000);
            await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.Communicating, 5000);
            Assert.Equal("S6F24 <B 0x00>", await host2.Ask("S6F23 W <U1 0>"));
            Assert.Equal(1UL, Smpln(host2.NextOf(6, 1)));
            Assert.Equal(2UL, Smpln(host2.NextOf(6, 1)));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Stop_NoMoreReports()
    {
        var clock = new Clock();
        using var eq = Eq(clock);
        using (var host = await GemKit.Communicating(eq)) await Define(host, 5, 1);
        eq.Stop();
        eq.Start();
        using var host2 = await GemKit.Connect(eq);
        clock.Add(5000);
        host2.NoneOf(6, 500);
    }

    [Fact]
    public void GemLoop_TickWait()
    {
        Assert.Equal(20, GemLoop.TickWaitMs(20, null));
        Assert.Equal(1, GemLoop.TickWaitMs(20, () => 1));
        Assert.Equal(7, GemLoop.TickWaitMs(20, () => 7));
        Assert.Equal(20, GemLoop.TickWaitMs(20, () => 1000));
        Assert.Equal(20, GemLoop.TickWaitMs(20, () => int.MaxValue));
        Assert.Equal(1, GemLoop.TickWaitMs(20, () => -5));
    }
}
