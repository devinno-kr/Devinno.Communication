using System.Collections.Concurrent;
using System.Diagnostics;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// GemHost 보고 받기와 해석(S6F11·S5F1·S10F1), 리포트 요청(S6F15/19), 알람(S5F3/5/7), 터미널(S10F3), 리모트 명령(S2F41)
[Collection(nameof(GemHostCollection))]
public class GemHostEventTests
{
    static Action<GemEquipment> Online => e => e.InitialControlState = GemControlState.OnlineRemote;

    static async Task<T> Take<T>(BlockingCollection<T> c, int timeoutMs = 3000)
    {
        await TestUtil.Until(() => c.Count > 0, timeoutMs);
        return c.Take();
    }

    [Fact]
    public async Task S6F11_Loopback_DecodedWithVids_OnAppThread()
    {
        var events = new BlockingCollection<(GemEventReport Report, string? Thread)>();
        var (eq, host) = await GemHostKit.Loopback(Online, h => h.EventReceived += (s, e) => events.Add((e.Report, Thread.CurrentThread.Name)));
        using var _ = eq;
        using var __ = host;
        host.Subscribe(5001, 2001, 3001);
        Assert.True((await host.ConfigureReportsAsync()).Success);
        eq.SetValue(2001, SecsItem.F4(36.5f));
        Assert.True(eq.TriggerEvent(5001, new Dictionary<GemId, SecsItem> { [3001] = SecsItem.A("LOT-7") }));
        var (report, thread) = await Take(events);
        Assert.Equal("GemHost.Events", thread);
        Assert.Equal(new GemId(5001), report.Ceid);
        var r = report.Reports.Single();
        Assert.Equal(new GemId(1000), r.Rptid);
        Assert.Equal(["2001=<F4 36.5>", "3001=<A \"LOT-7\">"], r.Values.Select(v => $"{v.Vid}={v.Value.ToSml()}"));
        Assert.Equal(SecsItem.A("LOT-7"), report.GetValue(3001));
        Assert.Null(report.GetValue(2002));
        Assert.Equal(SecsFormat.U4, report.DataId.Format);
    }

    [Fact]
    public async Task S6F11_Fake_LenientIds_UnknownReportHasNoVid_GoldenAck()
    {
        var events = new BlockingCollection<GemEventReport>();
        var (fake, host) = await GemHostKit.Fake(h => h.EventReceived += (s, e) => events.Add(e.Report));
        using var _ = fake;
        using var __ = host;
        Assert.Equal("S6F12 <B 0x00>", await fake.Ask("S6F11 W <L <B 0x01> <I2 5001> <L <L <U1 7> <L <F4 1.5>>> <L <A \"R9\"> <L>>>>"));
        var report = await Take(events);
        Assert.Equal((new GemId(5001), "<B 0x01>"), (report.Ceid, report.DataId.ToSml()));
        Assert.Equal([new GemId(7), new GemId("R9")], report.Reports.Select(x => x.Rptid));
        Assert.Null(report.Reports[0].Values.Single().Vid);                                // 정의하지 않은 리포트: 값만
        Assert.Equal(SecsItem.F4(1.5f), report.Reports[0].Values.Single().Value);
        Assert.Equal("S6F12 <B 0x00>", await fake.Ask("S6F11 W <L <U4 2> <A \"LOT_START\"> <L>>"));   // 숫자 포맷 설정이어도 A CEID를 받음
        Assert.Equal(new GemId("LOT_START"), (await Take(events)).Ceid);
    }

    [Fact]
    public async Task S6F11_ValueCountMismatch_NoVid_ProtocolError()
    {
        var events = new BlockingCollection<GemEventReport>();
        var (fake, host) = await GemHostKit.Fake(h => h.EventReceived += (s, e) => events.Add(e.Report));
        using var _ = fake;
        using var __ = host;
        var log = GemHostKit.Log(host);
        Assert.Equal(0, await host.DefineReportsAsync([(7, [2001, 2002])]));
        Assert.Equal("S6F12 <B 0x00>", await fake.Ask("S6F11 W <L <U4 1> <U4 5001> <L <L <U4 7> <L <F4 1.5>>>>>"));
        var report = await Take(events);
        Assert.Null(report.Reports.Single().Values.Single().Vid);
        await TestUtil.Until(() => log.Any(l => l.Contains("7(값 1개/정의 2개)", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task S6F11_ManyMismatchedReports_OneCombinedProtocolError()
    {
        var events = new BlockingCollection<GemEventReport>();
        var (fake, host) = await GemHostKit.Fake(h => h.EventReceived += (s, e) => events.Add(e.Report));
        using var _ = fake;
        using var __ = host;
        var log = GemHostKit.Log(host);
        Assert.Equal(0, await host.DefineReportsAsync([.. Enumerable.Range(1, 20).Select(i => ((GemId)i, (IReadOnlyList<GemId>)[2001, 2002]))]));
        var reports = string.Concat(Enumerable.Range(1, 20).Select(i => $" <L <U4 {i}> <L <F4 1.5>>>"));
        Assert.Equal("S6F12 <B 0x00>", await fake.Ask($"S6F11 W <L <U4 1> <U4 5001> <L{reports}>>"));
        Assert.Equal(20, (await Take(events)).Reports.Count(r => r.Values.Single().Vid == null));
        await Task.Delay(300);                                                              // 늦게 오는 알림이 더 없는지
        var error = Assert.Single(log, l => l.Contains("정의와 달라", StringComparison.Ordinal));
        Assert.Contains("리포트 20개", error);
        Assert.Contains("1(값 1개/정의 2개)", error);
        Assert.Contains("5(값 1개/정의 2개), …", error);                                   // 앞 5개만 적음
        Assert.DoesNotContain("6(값", error);
    }

    [Fact]
    public async Task HugeBodies_DiagnosticSmlIsCapped()
    {
        var (fake, host) = await GemHostKit.Fake(fakeSetup: f => f.Reply(6, 19, $"<A \"{new string('x', 20000)}\">"));
        using var _ = fake;
        using var __ = host;
        var log = GemHostKit.Log(host);
        var huge = new string('y', 20000);
        await Assert.ThrowsAsync<SecsAbortException>(() => fake.SendAsync($"S6F11 W <L <U4 1> <U4 5001> <A \"{huge}\">>"));   // 구조 오류 → S6F0
        await TestUtil.Until(() => log.Any(l => l.Contains("S6F11 본문 구조 오류", StringComparison.Ordinal)));
        var error = log.Single(l => l.Contains("S6F11 본문 구조 오류", StringComparison.Ordinal));
        Assert.True(error.Length < GemItems.SmlLimit + 300, $"{error.Length}자");
        Assert.Contains("…(생략: ", error);
        var ex = Assert.Throws<FormatException>(() => host.RequestReport(1));               // 응답 구조 오류(Unexpected)
        Assert.True(ex.Message.Length < GemItems.SmlLimit + 300, $"{ex.Message.Length}자");
        Assert.Contains("…(생략: ", ex.Message);
    }

    [Fact]
    public async Task S6F11_HugeTextCeid_MismatchWarningIsCapped()
    {
        var events = new BlockingCollection<GemEventReport>();
        var (fake, host) = await GemHostKit.Fake(h => h.EventReceived += (s, e) => events.Add(e.Report));
        using var _ = fake;
        using var __ = host;
        var log = GemHostKit.Log(host);
        Assert.Equal(0, await host.DefineReportsAsync([(7, [2001, 2002])]));
        var ceid = new string('z', 20000);
        Assert.Equal("S6F12 <B 0x00>", await fake.Ask($"S6F11 W <L <U4 1> <A \"{ceid}\"> <L <L <U4 7> <L <F4 1.5>>>>>"));
        Assert.Equal(ceid, (await Take(events)).Ceid.Text);
        await TestUtil.Until(() => log.Any(l => l.Contains("정의와 달라", StringComparison.Ordinal)));
        var warning = log.Single(l => l.Contains("정의와 달라", StringComparison.Ordinal));
        Assert.True(warning.Length < GemItems.IdLimit + 300, $"{warning.Length}자");
        Assert.Contains("…(전체 20000자)", warning);
    }

    [Fact]
    public void GemCommandResult_ParameterAcks_ReadOnlyCopy()
    {
        var source = new List<(string Name, byte Cpack)> { ("LOTID", 2) };
        var result = new GemCommandResult(GemAck.HcackParameterInvalid, source);
        source.Add(("PPID", 1));
        Assert.Single(result.ParameterAcks);
        Assert.True(((ICollection<(string Name, byte Cpack)>)result.ParameterAcks).IsReadOnly);
        Assert.IsNotType<List<(string Name, byte Cpack)>>(result.ParameterAcks);
    }

    [Fact]
    public async Task S6F11_BadStructure_S6F0_NoEvent_WithoutWBit_EventOnly()
    {
        var events = new BlockingCollection<GemEventReport>();
        var (fake, host) = await GemHostKit.Fake(h => h.EventReceived += (s, e) => events.Add(e.Report));
        using var _ = fake;
        using var __ = host;
        var log = GemHostKit.Log(host);
        await Assert.ThrowsAsync<SecsAbortException>(() => fake.SendAsync("S6F11 W <L <U4 1> <F4 5001> <L>>"));     // CEID가 F4
        await Assert.ThrowsAsync<SecsAbortException>(() => fake.SendAsync("S6F11 W <L <U4 1> <U4 5001>>"));         // 리포트 목록 없음
        await TestUtil.Until(() => log.Count(l => l.Contains("S6F11 본문 구조 오류", StringComparison.Ordinal)) == 2);
        Assert.Empty(events);
        Assert.Null(await fake.SendAsync("S6F11 <L <U4 1> <U4 5002> <L>>"));                                          // W 없음: 응답 없이 이벤트만
        Assert.Equal(new GemId(5002), (await Take(events)).Ceid);
    }

    [Fact]
    public async Task RequestEventReport_RequestReport_Loopback_AndGoldenRequests()
    {
        var (eq, host) = await GemHostKit.Loopback(Online);
        using var _ = eq;
        using var __ = host;
        host.Subscribe(5001, 2001, 3001);
        Assert.True(host.ConfigureReports().Success);
        var report = await host.RequestEventReportAsync(5001);
        Assert.Equal(SecsItem.F4(25), report.GetValue(2001));
        Assert.Equal(["<F4 25>", "<A \"\">"], host.RequestReport(1000).Select(v => v.ToSml()));

        var (fake, host2) = await GemHostKit.Fake(fakeSetup: f =>
        {
            f.Reply(6, 15, "<L <U4 1> <U4 5001> <L>>");
            f.Reply(6, 19, "<L>");
        });
        using var ___ = fake;
        using var ____ = host2;
        Assert.Empty((await host2.RequestEventReportAsync(5001)).Reports);
        Assert.Equal("S6F15 W <U4 5001>", fake.Next(6, 15).ToSml());
        Assert.Empty(await host2.RequestReportAsync(1000));
        Assert.Equal("S6F19 W <U4 1000>", fake.Next(6, 19).ToSml());
    }

    [Fact]
    public async Task Alarms_Loopback_EnableSetClear_ListAndEnabledList()
    {
        var alarms = new BlockingCollection<GemAlarmEventArgs>();
        var (eq, host) = await GemHostKit.Loopback(Online, h => h.AlarmReceived += (s, e) => alarms.Add(e));
        using var _ = eq;
        using var __ = host;
        Assert.Equal(GemAck.Ackc5Accepted, await host.EnableAlarmAsync(null, true));
        Assert.Equal(GemAck.Ackc5Error, host.EnableAlarm(99, true));                      // 모르는 ALID: 값으로
        Assert.True(eq.SetAlarm(1));
        var set = await Take(alarms);
        Assert.Equal((new GemId(1), true, 2, "Over temperature", (byte)0x82), (set.Alid, set.IsSet, set.Category, set.Text, set.Alcd));
        Assert.True(eq.ClearAlarm(1));
        Assert.False((await Take(alarms)).IsSet);
        var list = host.ListAlarms(1);
        Assert.Equal(("Over temperature", false, 2), (list.Single().Text, list.Single().IsSet, list.Single().Category));
        Assert.Equal([new GemId(1)], (await host.ListEnabledAlarmsAsync()).Select(a => a.Alid));
        Assert.Equal(GemAck.Ackc5Accepted, await host.EnableAlarmAsync(1, false));
        Assert.Empty(await host.ListEnabledAlarmsAsync());
    }

    [Fact]
    public async Task Alarms_GoldenRequests_UnknownAlid_S5F1AckAndBadBody()
    {
        var alarms = new BlockingCollection<GemAlarmEventArgs>();
        var (fake, host) = await GemHostKit.Fake(h => h.AlarmReceived += (s, e) => alarms.Add(e), f =>
        {
            f.Reply(5, 5, "<L <L <B> <U4 9> <A>> <L <B 0x81> <U4 1> <A \"x\">>>");
            f.Reply(5, 7, "<L>");
        });
        using var _ = fake;
        using var __ = host;
        await host.EnableAlarmAsync(null, true);
        Assert.Equal("S5F3 W <L [2] <B 0x80> <U4>>", fake.Next(5, 3).ToSml());             // 길이 0 ALID = 모든 알람
        await host.EnableAlarmAsync(5, false);
        Assert.Equal("S5F3 W <L [2] <B 0x00> <U4 5>>", fake.Next(5, 3).ToSml());
        var list = await host.ListAlarmsAsync([9, 1]);
        Assert.Equal("S5F5 W <U4 9 1>", fake.Next(5, 5).ToSml());
        Assert.Equal(((byte)0, ""), (list[0].Alcd, list[0].Text));                         // 모르는 ALID: ALCD 길이 0 → 0
        Assert.True(list[1].IsSet);
        Assert.Empty(host.ListEnabledAlarms());
        Assert.Equal("S5F7 W", fake.Next(5, 7).ToSml());

        Assert.Equal("S5F2 <B 0x00>", await fake.Ask("S5F1 W <L <B 0x82> <U2 1> <A \"Over\">>"));
        var a = await Take(alarms);
        Assert.Equal((new GemId(1), true, 2), (a.Alid, a.IsSet, a.Category));
        await Assert.ThrowsAsync<SecsAbortException>(() => fake.SendAsync("S5F1 W <L <B 0x82> <U4 1>>"));   // 구조 오류 → S5F0
    }

    [Fact]
    public async Task Alarms_AsciiAlidFormat_ZeroOrOneOnly()
    {
        var (fake, host) = await GemHostKit.Fake(h => h.AlidFormat = SecsFormat.ASCII, f => f.Reply(5, 5, "<L <L <B 0x01> <A \"A1\"> <A \"t\">>>"));
        using var _ = fake;
        using var __ = host;
        Assert.Equal(new GemId("A1"), host.ListAlarms("A1").Single().Alid);
        Assert.Equal("S5F5 W <A \"A1\">", fake.Next(5, 5).ToSml());
        Assert.Throws<ArgumentException>(() => host.ListAlarms("A1", "A2"));
        await host.EnableAlarmAsync(null, true);
        Assert.Equal("S5F3 W <L [2] <B 0x80> <A \"\">>", fake.Next(5, 3).ToSml());
    }

    [Fact]
    public async Task Terminal_BothWays_Loopback_NoSubscriber2()
    {
        var hostTerminal = new BlockingCollection<GemTerminalEventArgs>();
        var eqTerminal = new BlockingCollection<GemTerminalEventArgs>();
        var (eq, host) = await GemHostKit.Loopback(e => { Online(e); e.TerminalReceived += (s, a) => eqTerminal.Add(a); }, h => h.TerminalReceived += (s, a) => hostTerminal.Add(a));
        using var _ = eq;
        using var __ = host;
        Assert.True(eq.SendTerminalMessage(2, "hello host"));
        var t = await Take(hostTerminal);
        Assert.Equal((2, "hello host"), (t.Tid, t.Lines.Single()));
        Assert.Equal(GemAck.Ackc10Accepted, await host.SendTerminalAsync(1, "hello equipment"));
        Assert.Equal("hello equipment", (await Take(eqTerminal)).Lines.Single());
        Assert.Throws<ArgumentException>(() => host.SendTerminal(1, "한글"));

        var (fake, host2) = await GemHostKit.Fake();
        using var ___ = fake;
        using var ____ = host2;
        Assert.Equal("S10F2 <B 0x02>", await fake.Ask("S10F1 W <L <B 0x01> <A \"x\">>"));   // 구독자 없음 = 터미널 없음
    }

    [Fact]
    public async Task RemoteCommand_Loopback_HcackValues_IsAccepted()
    {
        var (eq, host) = await GemHostKit.Loopback(e =>
        {
            Online(e);
            e.RemoteCommandReceived += (s, a) => a.Hcack = a.Command == "START" ? GemAck.HcackAcceptedLater : GemAck.HcackAccepted;
        });
        using var _ = eq;
        using var __ = host;
        var start = await host.RemoteCommandAsync("START", [("LOTID", SecsItem.A("L1"))]);
        Assert.Equal((GemAck.HcackAcceptedLater, true), (start.Hcack, start.IsAccepted));
        Assert.Equal((GemAck.HcackAccepted, true), (host.RemoteCommand("STOP").Hcack, host.RemoteCommand("STOP").IsAccepted));
        var unknown = host.RemoteCommand("FOO");
        Assert.Equal((GemAck.HcackInvalidCommand, false), (unknown.Hcack, unknown.IsAccepted));
        var bad = host.RemoteCommand("START", ("BAD", SecsItem.A("x")));
        Assert.Equal(GemAck.HcackParameterInvalid, bad.Hcack);
        Assert.Equal([("BAD", GemAck.CpackUnknownName)], bad.ParameterAcks);
    }

    [Fact]
    public async Task RemoteCommand_GoldenRequest_LenientCpname()
    {
        var (fake, host) = await GemHostKit.Fake(fakeSetup: f => f.Reply(2, 41, "<L <B 3> <L <L <U4 7> <B 1>>>>"));
        using var _ = fake;
        using var __ = host;
        var result = await host.RemoteCommandAsync("START", [("LOTID", SecsItem.A("L1"))]);
        Assert.Equal("S2F41 W <L [2] <A \"START\"> <L [1] <L [2] <A \"LOTID\"> <A \"L1\">>>>", fake.Next(2, 41).ToSml());
        Assert.Equal([("7", (byte)1)], result.ParameterAcks);
        Assert.Throws<ArgumentNullException>(() => host.RemoteCommand(null!));
    }

    [Fact]
    public async Task StuckEventHandler_DoesNotDelayAcks()
    {
        using var release = new ManualResetEventSlim();
        var (fake, host) = await GemHostKit.Fake(h => h.EventReceived += (s, e) => release.Wait(10000));
        using var _ = fake;
        using var __ = host;
        for (int i = 0; i < 3; i++)
        {
            var sw = Stopwatch.StartNew();
            Assert.Equal("S6F12 <B 0x00>", await fake.Ask("S6F11 W <L <U4 1> <U4 5001> <L>>"));
            Assert.True(sw.ElapsedMilliseconds < 2500, $"{sw.ElapsedMilliseconds}ms");    // 핸들러는 10초 멈춤, 부하를 감안해 T3(3초)보다 짧게
        }
        release.Set();
    }
}
