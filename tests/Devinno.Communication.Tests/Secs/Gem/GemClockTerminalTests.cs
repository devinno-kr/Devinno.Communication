using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 시계(S2F17/18, S2F31/32, Clock SV, TimeFormat), 터미널(S10F1/3/5), 처리 상태(ProcessState)
public class GemClockTerminalTests
{
    static GemEquipment Eq(Action<GemEquipment>? more = null) => GemKit.Equipment(e =>
    {
        GemKit.Sample(e);
        e.InitialControlState = GemControlState.OnlineRemote;
        more?.Invoke(e);
    });

    static DateTime Parse(string sml)
    {
        var text = SecsItem.ParseSml(sml[sml.IndexOf('<')..]).GetString();
        Assert.True(GemTime.TryParse(text, out var t), text);
        return t;
    }

    [Fact]
    public async Task S2F17_TimeFollowsTimeFormat_ClockSvToo()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        var r1 = await host.Ask("S2F17 W");
        Assert.Matches(new Regex("^S2F18 <A \"\\d{16}\">$"), r1);                         // 기본 TimeFormat 1
        Assert.True(Math.Abs((Parse(r1) - DateTime.Now).TotalSeconds) < 5);
        eq.SetConstant(1102, SecsItem.U1(0));
        Assert.Matches(new Regex("^S2F18 <A \"\\d{12}\">$"), await host.Ask("S2F17 W"));
        eq.SetConstant(1102, SecsItem.U1(2));
        Assert.Matches(new Regex("^S2F18 <A \"\\d{4}-\\d\\d-\\d\\dT\\d\\d:\\d\\d:\\d\\d\\.\\d\\d(Z|[+-]\\d\\d:\\d\\d)\">$"), await host.Ask("S2F17 W"));
        Assert.Matches(new Regex("^S1F4 <L \\[1\\] <A \"\\d{4}-"), await host.Ask("S1F3 W <L <U4 1001>>"));   // Clock SV도 같은 형식
    }

    [Theory]
    [InlineData("300101000000")]
    [InlineData("2030010100000000")]
    [InlineData("2030-01-01T00:00:00+09:00")]
    public async Task S2F31_AllThreeForms_Tiack0_ShiftsGemClock_ClockSet(string time)
    {
        using var eq = Eq();
        var events = new ConcurrentQueue<GemClockEventArgs>();
        eq.ClockSet += (s, e) => events.Enqueue(e);
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F32 <B 0x00>", await host.Ask($"S2F31 W <A \"{time}\">"));
        Assert.True(GemTime.TryParse(time, out var expected));
        Assert.True(Math.Abs((eq.Now - expected).TotalSeconds) < 5);
        Assert.True(Math.Abs((Parse(await host.Ask("S2F17 W")) - expected).TotalSeconds) < 5);
        await TestUtil.Until(() => events.Count == 1);
        Assert.Equal((expected, GemClockSource.S2F31), (events.Single().Time, events.Single().Source));
    }

    [Theory]
    [InlineData("S2F31 W <A \"20301301000000\">")]
    [InlineData("S2F31 W <A \"yesterday\">")]
    [InlineData("S2F31 W <A \"9999123123595999\">")]                                   // 연도 범위(1970~2999) 밖: 시계 계산이 넘침
    [InlineData("S2F31 W <A \"9999-12-31T23:59:59\">")]
    [InlineData("S2F31 W <A \"0001-01-01T00:00:00Z\">")]
    [InlineData("S2F31 W <A \"3000-01-01T00:00:00\">")]
    [InlineData("S2F31 W <A \"1969-12-31T23:59:59\">")]
    [InlineData("S2F31 W <U4 1>")]
    [InlineData("S2F31 W")]
    public async Task S2F31_Invalid_Tiack1_NoChange(string sml)
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F32 <B 0x01>", await host.Ask(sml));
        Assert.True(Math.Abs((eq.Now - DateTime.Now).TotalSeconds) < 2);
    }

    [Fact]
    public async Task RequestHostTime_S2F18SetsClock_InvalidReplyIsSendFailed()
    {
        using var eq = Eq();
        var log = GemKit.Log(eq);
        var events = new ConcurrentQueue<GemClockEventArgs>();
        eq.ClockSet += (s, e) => events.Enqueue(e);
        using var host = await GemKit.Communicating(eq);
        Assert.True(eq.RequestHostTime());
        Assert.Equal("S2F17 W", host.Next(2, 17).ToSml());
        await TestUtil.Until(() => events.Count == 1);
        Assert.Equal((new DateTime(2026, 10, 14, 12, 0, 0), GemClockSource.S2F18), (events.Single().Time, events.Single().Source));
        Assert.True(Math.Abs((eq.Now - new DateTime(2026, 10, 14, 12, 0, 0)).TotalSeconds) < 5);
        host.Reply(2, 17, "<A \"not a time\">");
        eq.RequestHostTime();
        await TestUtil.Until(() => log.Contains("SendFailed S2F17 InvalidReply"));
        eq.GoOffline();
        Assert.False(eq.RequestHostTime());
    }

    [Fact]
    public async Task RequestHostTime_S2F18OutOfYearRange_InvalidReply_ClockUnchanged()
    {
        using var eq = Eq();
        var log = GemKit.Log(eq);
        using var host = await GemKit.Communicating(eq);
        host.Reply(2, 17, "<A \"9999123123595999\">");
        Assert.True(eq.RequestHostTime());
        await TestUtil.Until(() => log.Contains("SendFailed S2F17 InvalidReply"));
        Assert.True(Math.Abs((eq.Now - DateTime.Now).TotalSeconds) < 2);
        Assert.Matches(@"^S1F4 <L \[1\] <A ""\d{16}"">>$", await host.Ask("S1F3 W <L <U4 1001>>"));
    }

    [Fact]
    public async Task S10F3_S10F5_Ack_AndTerminalReceived()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S10F4 <B 0x02>", await host.Ask("S10F3 W <L <B 1> <A \"Hello\">>"));   // 구독자 없음 = 터미널 없음
        var got = new ConcurrentQueue<string>();
        eq.TerminalReceived += (s, e) => got.Enqueue($"{e.Tid}:{string.Join("|", e.Lines)}");
        Assert.Equal("S10F4 <B 0x00>", await host.Ask("S10F3 W <L <B 1> <A \"Hello\">>"));
        Assert.Equal("S10F6 <B 0x00>", await host.Ask("S10F5 W <L <B 2> <L <A \"line1\"> <A \"line2\">>>"));
        await TestUtil.Until(() => got.Count == 2);
        Assert.Equal(["1:Hello", "2:line1|line2"], got);
        Assert.Equal(7, (await Assert.ThrowsAsync<SecsErrorException>(() => host.SendAsync("S10F3 W <L <B 1> <U4 5>>"))).Function);
        Assert.Equal(7, (await Assert.ThrowsAsync<SecsErrorException>(() => host.SendAsync("S10F5 W <L <B 1> <A \"x\">>"))).Function);
    }

    [Fact]
    public async Task SendTerminalMessage_GoldenS10F1_AckChecks_Conditions()
    {
        using var eq = Eq();
        var log = GemKit.Log(eq);
        using var host = await GemKit.Communicating(eq);
        Assert.True(eq.SendTerminalMessage(1, "Operator call"));
        Assert.Equal("S10F1 W <L [2] <B 0x01> <A \"Operator call\">>", host.Next(10, 1).ToSml());
        host.Reply(10, 1, "<B 1>");
        eq.SendTerminalMessage(1, "x");
        await TestUtil.Until(() => log.Contains("SendFailed S10F1 NotAcknowledged 1"));
        Assert.Throws<ArgumentException>(() => eq.SendTerminalMessage(1, "한글"));
        eq.GoOffline();
        Assert.False(eq.SendTerminalMessage(1, "offline"));
    }

    [Fact]
    public async Task SendTerminalMessage_NoWBitOption_And_NotCommunicatingDiscard()
    {
        using var eq = Eq(e => e.TerminalReplyExpected = false);
        using var host = await GemKit.Communicating(eq);
        eq.SendTerminalMessage(3, "t");
        Assert.False(host.Next(10, 1).ReplyExpected);

        using var eq2 = Eq(e => e.InitiateCommunication = false);
        var log = GemKit.Log(eq2);
        using var host2 = await GemKit.Connect(eq2);
        Assert.False(eq2.SendTerminalMessage(1, "t"));
        await TestUtil.Until(() => log.Contains("SendFailed S10F1 NotCommunicating"));
    }

    [Fact]
    public async Task RecognizeTerminalMessage_And_SetProcessState_StandardEvents()
    {
        using var eq = Eq(e => e.EventsEnabledByDefault = true);
        using var host = await GemKit.Communicating(eq);
        Assert.True(eq.RecognizeTerminalMessage());
        Assert.Equal(1306, host.Next(6, 11).Item!.Items[1].GetInt64());
        await host.Ask("S2F33 W <L <U4 0> <L <L <U4 1000> <L <U4 1007> <U4 1008>>>>>");
        await host.Ask("S2F35 W <L <U4 0> <L <L <U4 1304> <L <U4 1000>>>>>");
        eq.SetProcessState(2);
        Assert.Equal("<L [3] <U4 2> <U4 1304> <L [1] <L [2] <U4 1000> <L [2] <U1 2> <U1 0>>>>>", host.Next(6, 11).Item!.ToSml());
        eq.SetProcessState(2);                                                             // 같은 값: CE 없음
        eq.SetProcessState(3);
        Assert.Equal("<L [2] <U1 3> <U1 2>>", host.Next(6, 11).Item!.Items[2].Items[0].Items[1].ToSml());
        host.NoMore();
        Assert.Equal(SecsItem.U1(3), eq.GetValue(1007));
    }
}
