using System.Collections.Concurrent;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 장비 상수: S2F13/S2F29 골든, S2F15 검사(EAC 1·3)·앱 승인·기한, SetConstant, 그리고 통신 전이 7(CommDelay = EstablishCommunicationsTimeout)
public class GemConstantTests
{
    static GemEquipment Eq(Action<GemEquipment>? more = null) => GemKit.Equipment(e =>
    {
        GemKit.Sample(e);
        e.AddEquipmentConstant(new GemEquipmentConstant(4002, "Recipe", SecsItem.A("R1")));
        e.InitialControlState = GemControlState.OnlineRemote;
        more?.Invoke(e);
    });

    [Fact]
    public async Task S2F13_Selected_All_Unknown()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F14 <L [3] <F4 80> <U2 10> <L [0]>>", await host.Ask("S2F13 W <L <U4 4001> <U2 1101> <U4 2001>>"));
        Assert.Equal("S2F14 <L [4] <U2 10> <U1 1> <F4 80> <A \"R1\">>", await host.Ask("S2F13 W <L>"));
    }

    [Fact]
    public async Task S2F29_Golden_StandardFirst_NoMinMaxIsEmpty_Unknown()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F30 <L [4] <L [6] <U4 1101> <A \"EstablishCommunicationsTimeout\"> <U2 1> <U2 3600> <U2 10> <A \"s\">> "
            + "<L [6] <U4 1102> <A \"TimeFormat\"> <U1 0> <U1 2> <U1 1> <A \"\">> "
            + "<L [6] <U4 4001> <A \"MaxTemp\"> <F4 0> <F4 200> <F4 80> <A \"C\">> "
            + "<L [6] <U4 4002> <A \"Recipe\"> <A \"\"> <A \"\"> <A \"R1\"> <A \"\">>>", await host.Ask("S2F29 W <L>"));
        Assert.Equal("S2F30 <L [1] <L [6] <U1 9> <A \"\"> <L [0]> <L [0]> <L [0]> <A \"\">>>", await host.Ask("S2F29 W <L <U1 9>>"));
    }

    [Fact]
    public async Task S2F15_NoSubscriber_AppliesConverted_Eac0()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F16 <B 0x00>", await host.Ask("S2F15 W <L <L <U4 4001> <U1 120>> <L <U4 1102> <U4 2>>>"));
        Assert.Equal("S2F14 <L [2] <F4 120> <U1 2>>", await host.Ask("S2F13 W <L <U4 4001> <U4 1102>>"));   // 숫자 포맷은 EC 포맷으로
    }

    [Theory]
    [InlineData("<L <L <U4 4001> <F4 100>> <L <U4 4999> <F4 1>>>", "0x01")]               // 모르는 ECID (앞의 정상 항목도 적용 안 됨)
    [InlineData("<L <L <U4 2001> <F4 1>>>", "0x01")]                                      // SV는 EC가 아님
    [InlineData("<L <L <U4 4001> <F4 100>> <L <U4 4001> <F4 201>>>", "0x03")]             // Max 밖
    [InlineData("<L <L <U4 4001> <A \"100\">>>", "0x03")]                                 // 숫자가 아님
    [InlineData("<L <L <U4 4002> <U4 1>>>", "0x03")]                                      // A EC에 숫자
    [InlineData("<L <L <U4 1101> <U2 0>>>", "0x03")]                                      // 표준 EC 범위 1~3600
    [InlineData("<L <L <U4 1102> <U1 3>>>", "0x03")]                                      // TimeFormat 0~2
    [InlineData("<L <L <U4 1101> <F4 2.5>>>", "0x03")]                                    // 정수 EC에 소수
    [InlineData("<L <L <U4 1101> <U2>>>", "0x03")]                                        // 값 개수: 빈 값
    [InlineData("<L <L <U4 1101> <U2 5 6>>>", "0x03")]                                    // 값 개수: 배열
    [InlineData("<L <L <U4 1102> <U1>>>", "0x03")]
    [InlineData("<L <L <U4 1102> <U1 1 2>>>", "0x03")]
    [InlineData("<L <L <U4 1101> <U4>>>", "0x03")]                                        // 변환 전 포맷이 달라도 같은 규칙
    [InlineData("<L <L <U4 4001> <F4 1 2>>>", "0x03")]
    public async Task S2F15_Rejected_NothingApplied(string body, string eac)
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal($"S2F16 <B {eac}>", await host.Ask($"S2F15 W {body}"));
        Assert.Equal("S2F14 <L [4] <U2 10> <U1 1> <F4 80> <A \"R1\">>", await host.Ask("S2F13 W <L>"));
    }

    [Fact]
    public async Task S2F15_ValueCountMismatch_StateUnchanged_StillReconnects()
    {
        using var eq = Eq(e => e.EventsEnabledByDefault = true);
        var log = GemKit.Log(eq);
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F16 <B 0x03>", await host.Ask("S2F15 W <L <L <U4 1101> <U2>>>"));
        Assert.Equal("S2F16 <B 0x03>", await host.Ask("S2F15 W <L <L <U4 1102> <U1>>>"));
        Assert.Matches(@"^S2F18 <A ""\d{16}"">$", await host.Ask("S2F17 W"));
        Assert.Equal("S1F16 <B 0x00>", await host.Ask("S1F15 W"));                         // 전이 10이 끝까지 감(CE 본문의 Clock이 TimeFormat을 읽음)
        host.Next(6, 11);
        Assert.Equal(GemControlState.HostOffline, eq.ControlState);
        eq.DisableCommunication();                                                         // S1F13 실패 → CommDelay(EstablishCommunicationsTimeout)를 읽는 경로
        host.Reply(1, 13, "<L <B 1> <L>>");
        eq.EnableCommunication();
        host.Next(1, 13);
        await TestUtil.Until(() => log.Any(l => l.Contains("S1F13 통신 수립 실패", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task S2F15_NaN_AgainstMaxOnly_Eac3_SetConstantRejectsToo()
    {
        using var eq = Eq(e => e.AddEquipmentConstant(new GemEquipmentConstant(4003, "Limit", SecsItem.F4(1), max: SecsItem.F4(10))));
        using var host = await GemKit.Communicating(eq);
        var reply = await host.Hsms.SendAsync(new SecsMessage(2, 15, true, SecsItem.L(SecsItem.L(SecsItem.U4(4003), SecsItem.F4(float.NaN)))));
        Assert.Equal("S2F16 <B 0x03>", reply!.ToSml());
        reply = await host.Hsms.SendAsync(new SecsMessage(2, 15, true, SecsItem.L(SecsItem.L(SecsItem.U4(4003), SecsItem.F8(double.NaN)))));
        Assert.Equal("S2F16 <B 0x03>", reply!.ToSml());
        Assert.Equal(SecsItem.F4(1), eq.GetValue(4003));
        Assert.Throws<ArgumentOutOfRangeException>(() => eq.SetConstant(4003, SecsItem.F4(float.NaN)));
        Assert.Throws<ArgumentOutOfRangeException>(() => eq.SetConstant(4001, SecsItem.F4(float.NaN)));
    }

    [Fact]
    public async Task SetConstant_ValueCountMismatch_ArgumentException()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("value", Assert.Throws<ArgumentException>(() => eq.SetConstant(1101, SecsItem.U2())).ParamName);
        Assert.Equal("value", Assert.Throws<ArgumentException>(() => eq.SetConstant(1101, SecsItem.U2(5, 6))).ParamName);
        Assert.Equal("value", Assert.Throws<ArgumentException>(() => eq.SetConstant(1102, SecsItem.U4())).ParamName);
        Assert.Equal("value", Assert.Throws<ArgumentException>(() => eq.SetConstant(4001, SecsItem.F4(1, 2))).ParamName);
        eq.SetConstant(4002, SecsItem.A("A longer recipe name"));                         // A는 길이 자유
        Assert.Equal(SecsItem.U2(10), eq.GetValue(1101));
        Assert.Equal(SecsItem.U1(1), eq.GetValue(1102));
        Assert.Equal(SecsItem.F4(80), eq.GetValue(4001));
    }

    [Fact]
    public async Task S2F15_HandlerThrows_Eac2_NothingApplied_ProtocolError()
    {
        using var eq = Eq();
        var log = GemKit.Log(eq);
        eq.EquipmentConstantsChangeRequested += (s, e) => { e.Eac = GemAck.EacAccepted; throw new InvalidOperationException("앱 EC 오류"); };
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F16 <B 0x02>", await host.Ask("S2F15 W <L <L <U4 4001> <F4 150>>>"));
        Assert.Equal(SecsItem.F4(80), eq.GetValue(4001));
        await TestUtil.Until(() => log.Any(l => l.StartsWith("Error", StringComparison.Ordinal) && l.Contains("앱 EC 오류", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task S2F15_ApprovalFromPreviousRun_IsDiscarded_AfterRestart()
    {
        using var eq = Eq(e => e.CommandReplyTimeout = 60000);
        var log = GemKit.Log(eq);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        eq.EquipmentConstantsChangeRequested += (s, e) => { entered.Set(); release.Wait(20000); e.Eac = GemAck.EacAccepted; };
        using (var host = await GemKit.Communicating(eq))
        {
            _ = host.SendAsync("S2F15 W <L <L <U4 4001> <F4 150>>>");
            Assert.True(entered.Wait(5000));
            eq.Stop();                                                                     // 앱 스레드는 핸들러에 멈춰 있음(최대 3초 기다리고 넘어감)
        }
        eq.Start();
        while (log.TryDequeue(out _)) { }
        release.Set();
        await Task.Delay(500);
        Assert.Equal(SecsItem.F4(80), eq.GetValue(4001));                                  // 이전 실행의 승인은 새 실행에 적용하지 않음
        Assert.DoesNotContain(log, l => l.StartsWith("Error", StringComparison.Ordinal));  // 이전 실행의 알림도 새 실행으로 오지 않음
    }

    [Theory]
    [InlineData("S2F15 W <U4 4001>")]
    [InlineData("S2F15 W <L <L <U4 4001>>>")]
    [InlineData("S2F15 W <L <L <F4 4001> <F4 1>>>")]
    public async Task S2F15_Malformed_S9F7(string sml)
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal(7, (await Assert.ThrowsAsync<SecsErrorException>(() => host.SendAsync(sml))).Function);
    }

    [Fact]
    public async Task S2F15_AppApproval_OnAppThread_EacFromHandler()
    {
        using var eq = Eq();
        var seen = new ConcurrentQueue<string>();
        eq.EquipmentConstantsChangeRequested += (s, e) =>
        {
            seen.Enqueue($"{Thread.CurrentThread.Name} {string.Join(",", e.Changes.Select(c => $"{c.Ecid}={c.Value.ToSml()}"))}");
            e.Eac = e.Changes[0].Value.Equals(SecsItem.F4(150)) ? GemAck.EacBusy : GemAck.EacAccepted;
        };
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F16 <B 0x02>", await host.Ask("S2F15 W <L <L <U4 4001> <F4 150>>>"));
        Assert.Equal(SecsItem.F4(80), eq.GetValue(4001));                                  // 거절: 적용 안 함
        Assert.Equal("S2F16 <B 0x00>", await host.Ask("S2F15 W <L <L <U2 4001> <I4 90>>>"));
        Assert.Equal(SecsItem.F4(90), eq.GetValue(4001));
        Assert.Equal(["GemEquipment.Events 4001=<F4 150>", "GemEquipment.Events 4001=<F4 90>"], seen);
    }

    [Fact]
    public async Task S2F15_HandlerTooSlow_Eac2ByDeadline_LateResultDiscarded()
    {
        using var eq = Eq(e => e.CommandReplyTimeout = 500);
        var log = GemKit.Log(eq);
        using var release = new ManualResetEventSlim();
        eq.EquipmentConstantsChangeRequested += (s, e) => { release.Wait(5000); e.Eac = GemAck.EacAccepted; };
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F16 <B 0x02>", await host.Ask("S2F15 W <L <L <U4 4001> <F4 150>>>"));
        release.Set();
        await TestUtil.Until(() => log.Any(l => l.Contains("결과를 버렸습니다", StringComparison.Ordinal)));
        Assert.Equal(SecsItem.F4(80), eq.GetValue(4001));
        Assert.Contains(log, l => l.Contains("대신 응답했습니다", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetConstant_Applies_SendsOperatorChangeCe_Checks()
    {
        using var eq = Eq(e => e.EventsEnabledByDefault = true);
        using var host = await GemKit.Communicating(eq);
        eq.SetConstant(4001, SecsItem.U1(100));
        Assert.Equal(SecsItem.F4(100), eq.GetValue(4001));
        Assert.Equal(1305, host.Next(6, 11).Item!.Items[1].GetInt64());
        Assert.Equal("value", Assert.Throws<ArgumentOutOfRangeException>(() => eq.SetConstant(4001, SecsItem.F4(201))).ParamName);
        Assert.Equal("value", Assert.Throws<ArgumentException>(() => eq.SetConstant(4002, SecsItem.U1(1))).ParamName);
        Assert.Equal("ecid", Assert.Throws<ArgumentException>(() => eq.SetConstant(2001, SecsItem.F4(1))).ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => eq.SetConstant(1102, SecsItem.U1(3)));
        host.NoMore();
    }

    [Fact]
    public async Task T7_CommDelayExpires_AfterEstablishCommunicationsTimeout_ResendsS1F13()
    {
        using var eq = Eq();
        eq.Start();
        eq.SetConstant(1101, SecsItem.U2(1));                                              // 다음 CommDelay부터 1초
        using var host = new Fakes.FakeGemHost(GemKit.Port(eq));
        host.Reply(1, 13, "<L <B 1> <L>>");
        host.Start();
        host.Next(1, 13, 5000);                                                             // 연결 직후 S1F13(거절됨) → WAIT DELAY 1초
        var sw = System.Diagnostics.Stopwatch.StartNew();
        host.Reply(1, 13, "<L <B 0> <L>>");
        host.Next(1, 13, 5000);                                                             // 전이 7
        Assert.True(sw.ElapsedMilliseconds >= 600, $"CommDelay 전에 다시 보냈습니다({sw.ElapsedMilliseconds}ms)");
        await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.Communicating);
    }
}
