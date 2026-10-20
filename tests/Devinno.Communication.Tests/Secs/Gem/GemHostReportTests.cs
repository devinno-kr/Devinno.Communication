using System.Collections.Concurrent;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// GemHost 리포트: 구독(설정), S2F33/35/37 골든 본문, ConfigureReports 순서·실패 단계·RPTID 매기기, 해석 표, 우리 장비와 루프백
[Collection(nameof(GemHostCollection))]
public class GemHostReportTests
{
    [Fact]
    public void Subscribe_ReplaceInPlace_Unsubscribe_Clear_FormatChecked()
    {
        using var host = GemHostKit.Host(1);
        host.Subscribe(5001, 2001, 3001);
        host.Subscribe(1302);
        host.Subscribe(5001, 2002);                                                         // 같은 CEID: 그 자리에서 바꿈
        Assert.Equal(["5001:2002", "1302:"], host.Subscriptions.Select(s => $"{s.Ceid}:{string.Join(",", s.Vids)}"));
        var copy = host.Subscriptions;
        Assert.True(host.Unsubscribe(1302));
        Assert.False(host.Unsubscribe(1302));
        Assert.Equal(2, copy.Count);                                                        // 복사본
        Assert.Throws<ArgumentException>(() => host.Subscribe("LOT_START"));               // 숫자 포맷에 문자열 CEID
        Assert.Throws<ArgumentOutOfRangeException>(() => host.Subscribe(1, -1));            // U4에 음수 VID
        host.ClearSubscriptions();
        Assert.Empty(host.Subscriptions);
        Assert.True(host.DeleteReportsFirst);
        Assert.Equal(1000, host.ReportIdStart);
        Assert.Throws<ArgumentOutOfRangeException>(() => host.ReportIdStart = -1);
    }

    [Fact]
    public async Task LowLevel_GoldenBodies_DataIdCounts_AckIsValue()
    {
        var (fake, host) = await GemHostKit.Fake(fakeSetup: f => f.Reply(2, 37, "<B 1>"));
        using var _ = fake;
        using var __ = host;
        Assert.Equal(0, await host.DefineReportsAsync([(1000, [2001, 3001])]));
        Assert.Equal("S2F33 W <L [2] <U4 1> <L [1] <L [2] <U4 1000> <L [2] <U4 2001> <U4 3001>>>>>", fake.Next(2, 33).ToSml());
        Assert.Equal(0, host.LinkEvents([(5001, [1000])]));
        Assert.Equal("S2F35 W <L [2] <U4 2> <L [1] <L [2] <U4 5001> <L [1] <U4 1000>>>>>", fake.Next(2, 35).ToSml());
        Assert.Equal(GemAck.ErackUnknownEvent, host.EnableEvents(true, 5001, 5002));       // 0이 아닌 ack는 값
        Assert.Equal("S2F37 W <L [2] <BOOLEAN T> <L [2] <U4 5001> <U4 5002>>>", fake.Next(2, 37).ToSml());
        await host.EnableEventsAsync(false);
        Assert.Equal("S2F37 W <L [2] <BOOLEAN F> <L [0]>>", fake.Next(2, 37).ToSml());
    }

    [Fact]
    public async Task DefineReports_UpdatesTableOnlyOnAck0_EmptyListClears()
    {
        var (fake, host) = await GemHostKit.Fake();
        using var _ = fake;
        using var __ = host;
        await host.DefineReportsAsync([(1000, [2001, 3001]), (1001, [2002])]);
        Assert.Equal([new GemId(2001), new GemId(3001)], host.KnownReport(1000));
        fake.Reply(2, 33, "<B 3>");
        Assert.Equal(3, await host.DefineReportsAsync([(1002, [2001])]));
        Assert.Null(host.KnownReport(1002));                                                // 거절되면 표를 바꾸지 않음
        fake.Reply(2, 33, "<B 0>");
        await host.DefineReportsAsync([(1001, [])]);                                       // 그 리포트 지우기
        Assert.Null(host.KnownReport(1001));
        Assert.NotNull(host.KnownReport(1000));
        await host.DefineReportsAsync([]);                                                 // 모두 지우기
        Assert.Null(host.KnownReport(1000));
    }

    [Fact]
    public async Task DefineReports_AFormat_NumericRptid_MatchesTextFromEquipment()
    {
        var events = new BlockingCollection<GemEventReport>();
        var (fake, host) = await GemHostKit.Fake(h => { h.RptidFormat = SecsFormat.ASCII; h.EventReceived += (s, e) => events.Add(e.Report); });
        using var _ = fake;
        using var __ = host;
        Assert.Equal(0, await host.DefineReportsAsync([(1000, [2001])]));                  // 숫자 RPTID → A "1000"
        Assert.Equal("S2F33 W <L [2] <U4 1> <L [1] <L [2] <A \"1000\"> <L [1] <U4 2001>>>>>", fake.Next(2, 33).ToSml());
        Assert.Equal([new GemId(2001)], host.KnownReport("1000"));
        Assert.Equal([new GemId(2001)], host.KnownReport(1000));
        Assert.Equal("S6F12 <B 0x00>", await fake.Ask("S6F11 W <L <U4 1> <A \"5001\"> <L <L <A \"1000\"> <L <F4 1.5>>>>>"));
        Assert.True(events.TryTake(out var report, 3000));
        Assert.Equal(new GemId(2001), report!.Reports.Single().Values.Single().Vid);       // 텍스트로 돌아온 RPTID도 해석
        Assert.Equal(new GemId("5001"), report.Ceid);                                       // A로 받은 CEID는 텍스트 ID
        Assert.Equal(0, host.DefineReports([("1000", [])]));                               // 텍스트로 지워도 같은 리포트
        Assert.Null(host.KnownReport(1000));
    }

    [Fact]
    public async Task ConfigureReports_GoldenSequence_RptidAssignment_AFormat()
    {
        var (fake, host) = await GemHostKit.Fake(h => h.RptidFormat = SecsFormat.ASCII);
        using var _ = fake;
        using var __ = host;
        host.Subscribe(5001, 2001, 3001);
        host.Subscribe(1302);                                                               // VID 없음: 켜기만
        host.Subscribe(5002, 2002);
        var result = await host.ConfigureReportsAsync();
        Assert.True(result.Success);
        Assert.Equal((GemReportSetupStep.None, (byte?)null), (result.FailedStep, result.Ack));
        Assert.Equal("S2F33 W <L [2] <U4 1> <L [0]>>", fake.Next(2, 33).ToSml());         // 모두 지우기
        Assert.Equal("S2F37 W <L [2] <BOOLEAN F> <L [0]>>", fake.Next(2, 37).ToSml());    // 모두 끄기
        Assert.Equal("S2F33 W <L [2] <U4 2> <L [2] <L [2] <A \"R1000\"> <L [2] <U4 2001> <U4 3001>>> <L [2] <A \"R1001\"> <L [1] <U4 2002>>>>>", fake.Next(2, 33).ToSml());
        Assert.Equal("S2F35 W <L [2] <U4 3> <L [2] <L [2] <U4 5001> <L [1] <A \"R1000\">>> <L [2] <U4 5002> <L [1] <A \"R1001\">>>>>", fake.Next(2, 35).ToSml());
        Assert.Equal("S2F37 W <L [2] <BOOLEAN T> <L [3] <U4 5001> <U4 1302> <U4 5002>>>", fake.Next(2, 37).ToSml());
        fake.NoMore();
        Assert.Equal([new GemId(2002)], host.KnownReport("R1001"));
    }

    [Theory]
    [InlineData(33, "<B 2>", GemReportSetupStep.DeleteReports, 2, 1)]
    [InlineData(35, "<B 5>", GemReportSetupStep.LinkEvents, 5, 4)]
    [InlineData(37, "<B 1>", GemReportSetupStep.EnableEvents, 1, 5)]
    public async Task ConfigureReports_RejectedStep_StopsThere(int function, string ack, GemReportSetupStep step, byte code, int sent)
    {
        var (fake, host) = await GemHostKit.Fake(fakeSetup: f => f.Reply(2, function, ack));
        using var _ = fake;
        using var __ = host;
        host.Subscribe(5001, 2001);
        host.Subscribe(5002);
        var result = await host.ConfigureReportsAsync();
        Assert.False(result.Success);
        Assert.Equal((step, (byte?)code), (result.FailedStep, result.Ack));
        for (int i = 0; i < sent; i++) Assert.Equal(2, fake.NextAny().Stream);              // 실패한 단계까지만 보냄
        fake.NoMore();
    }

    [Fact]
    public async Task ConfigureReports_NoDeleteFirst_NoSubscriptions_SendsNothing()
    {
        var (fake, host) = await GemHostKit.Fake(h => h.DeleteReportsFirst = false);
        using var _ = fake;
        using var __ = host;
        Assert.True((await host.ConfigureReportsAsync()).Success);
        fake.NoMore();
        host.Subscribe(5001, 2001);
        Assert.True(host.ConfigureReports().Success);
        Assert.Equal("S2F33 W <L [2] <U4 1> <L [1] <L [2] <U4 1000> <L [1] <U4 2001>>>>>", fake.Next(2, 33).ToSml());   // 지우기 없이 바로 정의
    }

    [Fact]
    public async Task ConfigureReports_RptidOutOfFormat_ArgumentException_NothingSent()
    {
        var (fake, host) = await GemHostKit.Fake(h => { h.RptidFormat = SecsFormat.U1; h.ReportIdStart = 255; });
        using var _ = fake;
        using var __ = host;
        host.Subscribe(5001, 2001);
        host.Subscribe(5002, 2002);                                                         // 두 번째 RPTID 256은 U1 밖
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => host.ConfigureReportsAsync());
        fake.NoMore();
    }

    [Fact]
    public async Task ConfigureReports_Concurrent_RunOneAtATime()
    {
        var (fake, host) = await GemHostKit.Fake();
        using var _ = fake;
        using var __ = host;
        host.Subscribe(5001, 2001);
        await Task.WhenAll(host.ConfigureReportsAsync(), host.ConfigureReportsAsync());
        var functions = Enumerable.Range(0, 10).Select(_ => fake.NextAny().Function).ToArray();
        Assert.Equal([33, 37, 33, 35, 37, 33, 37, 33, 35, 37], functions);                 // 섞이지 않음
    }

    [Fact]
    public async Task ConfigureReports_Loopback_EquipmentEnablesOnlySubscribed()
    {
        var (eq, host) = await GemHostKit.Loopback(e => { e.InitialControlState = GemControlState.OnlineRemote; e.EventsEnabledByDefault = true; });
        using var _ = eq;
        using var __ = host;
        host.Subscribe(5001, 2001, 3001);
        host.Subscribe(9001);                                                               // 알람 Set CE
        Assert.True((await host.ConfigureReportsAsync()).Success);
        Assert.Equal("<U4 5001 9001>", eq.GetValue(1004).ToSml());                         // EventsEnabled: 모두 끈 뒤 구독한 CE만
        Assert.True(eq.TriggerEvent(5001));
        Assert.False(eq.TriggerEvent(5002));
        Assert.Equal([new GemId(2001), new GemId(3001)], host.KnownReport(1000));
    }
}
