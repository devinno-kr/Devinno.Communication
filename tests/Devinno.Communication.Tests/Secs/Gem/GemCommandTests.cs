using System.Collections.Concurrent;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 리모트 명령 S2F41/F42(HCACK·CPACK 골든, 검사 순서, 앱 승인·기한)와 등록 메시지(MessageReceived)
public class GemCommandTests
{
    static GemEquipment Eq(Action<GemEquipment>? more = null) => GemKit.Equipment(e =>
    {
        GemKit.Sample(e);
        e.AddRemoteCommand(new GemRemoteCommand("7"));
        e.InitialControlState = GemControlState.OnlineRemote;
        more?.Invoke(e);
    });

    #region S2F41
    [Fact]
    public async Task Accepted_HandlerOnAppThread_GetsCommandAndParameters()
    {
        using var eq = Eq();
        var seen = new ConcurrentQueue<string>();
        eq.RemoteCommandReceived += (s, e) =>
            seen.Enqueue($"{Thread.CurrentThread.Name} {e.Command} {string.Join(",", e.Parameters.Select(p => $"{p.Name}={p.Value.ToSml()}"))}");
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F42 <L [2] <B 0x00> <L [0]>>", await host.Ask("S2F41 W <L <A \"START\"> <L <L <A \"LOTID\"> <A \"LOT-1\">>>>"));
        Assert.Equal(["GemEquipment.Events START LOTID=<A \"LOT-1\">"], seen);
    }

    [Fact]
    public async Task HandlerSetsHcack4_AndParameterAcks_BecomeHcack3()
    {
        using var eq = Eq();
        eq.RemoteCommandReceived += (s, e) =>
        {
            if (e.Command == "STOP") { e.Hcack = GemAck.HcackAcceptedLater; return; }
            e.SetParameterAck("LOTID", GemAck.CpackIllegalValue);
            e.SetParameterAck("QTY", GemAck.CpackUnknownName);                              // 빠진 파라미터 알림
        };
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F42 <L [2] <B 0x04> <L [0]>>", await host.Ask("S2F41 W <L <A \"STOP\"> <L>>"));
        Assert.Equal("S2F42 <L [2] <B 0x03> <L [2] <L [2] <A \"LOTID\"> <B 0x02>> <L [2] <A \"QTY\"> <B 0x01>>>>",
            await host.Ask("S2F41 W <L <A \"START\"> <L <L <A \"LOTID\"> <A \"\">>>>"));
    }

    [Fact]
    public async Task GemAnswersWithoutApp_UnknownCommand1_Local2_BadParams3_NoSubscriber2()
    {
        using var eq = Eq();
        int calls = 0;
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F42 <L [2] <B 0x02> <L [0]>>", await host.Ask("S2F41 W <L <A \"STOP\"> <L>>"));                 // 구독자 없음
        eq.RemoteCommandReceived += (s, e) => Interlocked.Increment(ref calls);
        Assert.Equal("S2F42 <L [2] <B 0x01> <L [0]>>", await host.Ask("S2F41 W <L <A \"start\"> <L>>"));                // 대소문자 구분
        Assert.Equal("S2F42 <L [2] <B 0x03> <L [2] <L [2] <A \"QTY\"> <B 0x01>> <L [2] <U1 9> <B 0x01>>>>",
            await host.Ask("S2F41 W <L <A \"START\"> <L <L <A \"QTY\"> <U4 1>> <L <U1 9> <U4 1>>>>"));                   // 모르는 CPNAME(숫자 이름도 그대로 돌려줌)
        Assert.Equal("S2F42 <L [2] <B 0x03> <L [1] <L [2] <A \"LOTID\"> <B 0x03>>>>",
            await host.Ask("S2F41 W <L <A \"START\"> <L <L <A \"LOTID\"> <U4 1>>>>"));                                    // 포맷 불일치
        Assert.Equal("S2F42 <L [2] <B 0x00> <L [0]>>", await host.Ask("S2F41 W <L <U1 7> <L>>"));                       // RCMD 정수도 10진 문자열로
        eq.SwitchLocal();
        Assert.Equal("S2F42 <L [2] <B 0x01> <L [0]>>", await host.Ask("S2F41 W <L <A \"NONE\"> <L>>"));                 // 모름(1)이 LOCAL(2)보다 먼저
        Assert.Equal("S2F42 <L [2] <B 0x02> <L [0]>>", await host.Ask("S2F41 W <L <A \"START\"> <L <L <A \"QTY\"> <U4 1>>>>"));   // LOCAL(2)이 파라미터(3)보다 먼저
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("S2F41 W <A \"START\">")]
    [InlineData("S2F41 W <L <A \"START\">>")]
    [InlineData("S2F41 W <L <F4 1> <L>>")]
    [InlineData("S2F41 W <L <A \"START\"> <L <A \"LOTID\">>>")]
    public async Task Malformed_S9F7(string sml)
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal(7, (await Assert.ThrowsAsync<SecsErrorException>(() => host.SendAsync(sml))).Function);
    }

    [Fact]
    public async Task HandlerThrows_Hcack2_ProtocolError()
    {
        using var eq = Eq();
        var log = GemKit.Log(eq);
        eq.RemoteCommandReceived += (s, e) =>
        {
            e.Hcack = GemAck.HcackAccepted;
            e.SetParameterAck("LOTID", GemAck.CpackIllegalValue);
            throw new InvalidOperationException("앱 오류");
        };
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F42 <L [2] <B 0x02> <L [0]>>", await host.Ask("S2F41 W <L <A \"STOP\"> <L>>"));   // 실패 쪽으로(실행하지 않음)
        await TestUtil.Until(() => log.Any(l => l.StartsWith("Error", StringComparison.Ordinal) && l.Contains("앱 오류", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Offline_SxF0()
    {
        using var eq = Eq(e => e.InitialControlState = GemControlState.HostOffline);
        eq.RemoteCommandReceived += (s, e) => { };
        using var host = await GemKit.Communicating(eq);
        await Assert.ThrowsAsync<SecsAbortException>(() => host.SendAsync("S2F41 W <L <A \"STOP\"> <L>>"));
    }

    [Fact]
    public void SetParameterAck_Checks()
    {
        var a = new GemRemoteCommandEventArgs("X", []);
        Assert.Throws<ArgumentOutOfRangeException>(() => a.SetParameterAck("P", 0));
        Assert.Throws<ArgumentNullException>(() => a.SetParameterAck(null!, 1));
    }
    #endregion

    #region 등록 메시지
    [Fact]
    public async Task Registered_HandlerReplies_OnAppThread()
    {
        using var eq = Eq(e => e.RegisterMessage(7, 19));
        string? thread = null;
        eq.MessageReceived += (s, e) =>
        {
            thread = Thread.CurrentThread.Name;
            e.Reply(new SecsMessage(7, 20, false, SecsItem.L(SecsItem.A("PP1"), SecsItem.A("PP2"))));
        };
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S7F20 <L [2] <A \"PP1\"> <A \"PP2\">>", await host.Ask("S7F19 W"));
        Assert.Equal("GemEquipment.Events", thread);
        Assert.Equal(5, (await Assert.ThrowsAsync<SecsErrorException>(() => host.SendAsync("S7F1 W"))).Function);   // 등록하지 않은 S7 메시지
        Assert.Equal(3, (await Assert.ThrowsAsync<SecsErrorException>(() => host.SendAsync("S8F1 W"))).Function);
    }

    [Fact]
    public async Task Registered_NoSubscriber_Or_NoReply_SxF0()
    {
        using var eq = Eq(e => e.RegisterMessage(7, 19));
        using var host = await GemKit.Communicating(eq);
        await Assert.ThrowsAsync<SecsAbortException>(() => host.SendAsync("S7F19 W"));
        eq.MessageReceived += (s, e) => { };
        await Assert.ThrowsAsync<SecsAbortException>(() => host.SendAsync("S7F19 W"));
    }

    [Fact]
    public async Task Registered_HandlerThrows_SxF0_ProtocolError()
    {
        using var eq = Eq(e => e.RegisterMessage(7, 19));
        var log = GemKit.Log(eq);
        eq.MessageReceived += (s, e) => throw new InvalidOperationException("등록 핸들러 오류");
        using var host = await GemKit.Communicating(eq);
        await Assert.ThrowsAsync<SecsAbortException>(() => host.SendAsync("S7F19 W"));
        await TestUtil.Until(() => log.Any(l => l.StartsWith("Error", StringComparison.Ordinal) && l.Contains("등록 핸들러 오류", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Registered_ReplyRules()
    {
        using var eq = Eq(e => e.RegisterMessage(7, 19));
        var errors = new ConcurrentQueue<string>();
        GemMessageEventArgs? kept = null;
        eq.MessageReceived += (s, e) =>
        {
            kept = e;
            if (!e.Message.ReplyExpected)
            {
                try { e.Reply(new SecsMessage(7, 20, false)); } catch (InvalidOperationException) { errors.Enqueue("noW"); }
                return;
            }
            try { e.Reply(new SecsMessage(7, 22, false)); } catch (ArgumentException) { errors.Enqueue("function"); }
            e.Reply(new SecsMessage(7, 20, false, SecsItem.L()));
            try { e.Reply(new SecsMessage(7, 20, false)); } catch (InvalidOperationException) { errors.Enqueue("twice"); }
        };
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S7F20 <L [0]>", await host.Ask("S7F19 W"));
        Assert.Throws<InvalidOperationException>(() => kept!.Reply(new SecsMessage(7, 20, false)));   // 핸들러가 끝난 뒤
        await host.SendAsync("S7F19");
        await TestUtil.Until(() => errors.Count == 3);
        Assert.Equal(["function", "twice", "noW"], errors);
    }

    [Fact]
    public async Task Registered_HandlerTooSlow_SxF0ByDeadline_LateReplyRejected()
    {
        using var eq = Eq(e => { e.RegisterMessage(7, 19); e.CommandReplyTimeout = 500; });
        using var release = new ManualResetEventSlim();
        var late = new ConcurrentQueue<string>();
        eq.MessageReceived += (s, e) =>
        {
            release.Wait(5000);
            try { e.Reply(new SecsMessage(7, 20, false)); } catch (InvalidOperationException ex) { late.Enqueue(ex.Message); }
        };
        using var host = await GemKit.Communicating(eq);
        await Assert.ThrowsAsync<SecsAbortException>(() => host.SendAsync("S7F19 W"));
        release.Set();
        await TestUtil.Until(() => late.Count == 1);
        Assert.Contains("CommandReplyTimeout", late.Single());
    }

    [Fact]
    public async Task Registered_Offline_SxF0_NoEvent()
    {
        using var eq = Eq(e => { e.RegisterMessage(7, 19); e.InitialControlState = GemControlState.EquipmentOffline; });
        int calls = 0;
        eq.MessageReceived += (s, e) => Interlocked.Increment(ref calls);
        using var host = await GemKit.Communicating(eq);
        await Assert.ThrowsAsync<SecsAbortException>(() => host.SendAsync("S7F19 W"));
        Assert.Equal(0, calls);
    }
    #endregion
}
