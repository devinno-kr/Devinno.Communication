using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Secs.Hsms;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 동적 리포트 설정(S2F33/35/37 표의 행마다), S2F39, S6F11(TriggerEvent: 골든 본문·ID 포맷 9종·DATAID·스냅숏·순서·버림 경로), S6F15/S6F19
public class GemReportTests
{
    const string Define1000 = "S2F33 W <L <U4 0> <L <L <U4 1000> <L <U4 2001> <U4 3001>>>>>";
    const string Link5001 = "S2F35 W <L <U4 0> <L <L <U4 5001> <L <U4 1000>>>>>";
    const string Enable5001 = "S2F37 W <L <BOOLEAN T> <L <U4 5001>>>";

    static GemEquipment Eq(Action<GemEquipment>? more = null) => GemKit.Equipment(e =>
    {
        GemKit.Sample(e);
        e.InitialControlState = GemControlState.OnlineRemote;
        more?.Invoke(e);
    });

    #region S2F33
    [Fact]
    public async Task S2F33_Define_NewReport_Drack0_S6F19Values()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F34 <B 0x00>", await host.Ask(Define1000));
        Assert.Equal("S6F20 <L [2] <F4 25> <A \"\">>", await host.Ask("S6F19 W <U4 1000>"));
        Assert.Equal("S6F20 <L [0]>", await host.Ask("S6F19 W <U4 1999>"));               // 모르는 RPTID
    }

    [Fact]
    public async Task S2F33_EmptyOuterList_DeletesAllReportsAndLinks()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        await host.Ask(Define1000);
        await host.Ask(Link5001);
        await host.Ask(Enable5001);
        Assert.Equal("S2F34 <B 0x00>", await host.Ask("S2F33 W <L <U4 0> <L>>"));
        Assert.Equal("S6F20 <L [0]>", await host.Ask("S6F19 W <U4 1000>"));
        eq.TriggerEvent(5001);
        Assert.Equal("<L [0]>", host.Next(6, 11).Item!.Items[2].ToSml());                // 링크도 지워짐 → 빈 리포트 목록
    }

    [Fact]
    public async Task S2F33_RptidWithEmptyVids_DeletesThatReportAndItsLinks_UnknownAlsoOk()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        await host.Ask("S2F33 W <L <U4 0> <L <L <U4 1000> <L <U4 2001>>> <L <U4 1001> <L <U4 2002>>>>>");
        await host.Ask("S2F35 W <L <U4 0> <L <L <U4 5001> <L <U4 1000> <U4 1001>>>>>");
        await host.Ask(Enable5001);
        Assert.Equal("S2F34 <B 0x00>", await host.Ask("S2F33 W <L <U4 0> <L <L <U4 1000> <L>> <L <U4 4444> <L>>>>"));
        Assert.Equal("S6F20 <L [0]>", await host.Ask("S6F19 W <U4 1000>"));
        Assert.Equal("S6F20 <L [1] <U4>>", await host.Ask("S6F19 W <U4 1001>"));
        eq.TriggerEvent(5001);
        Assert.Equal("<L [1] <L [2] <U4 1001> <L [1] <U4>>>>", host.Next(6, 11).Item!.Items[2].ToSml());
    }

    [Fact]
    public async Task S2F33_AlreadyDefined_Drack3_AllOrNothing()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        await host.Ask(Define1000);
        Assert.Equal("S2F34 <B 0x03>", await host.Ask("S2F33 W <L <U4 0> <L <L <U4 1001> <L <U4 2001>>> <L <U4 1000> <L <U4 2002>>>>>"));
        Assert.Equal("S6F20 <L [0]>", await host.Ask("S6F19 W <U4 1001>"));               // 앞의 정상 정의도 적용 안 됨
        Assert.Equal("S2F34 <B 0x03>", await host.Ask("S2F33 W <L <U4 0> <L <L <U4 1002> <L <U4 2001>>> <L <U4 1002> <L <U4 2002>>>>>"));   // 같은 메시지 안 중복
        Assert.Equal("S6F20 <L [0]>", await host.Ask("S6F19 W <U4 1002>"));
    }

    [Fact]
    public async Task S2F33_UnknownVid_Drack4_StandardVidsAreKnown()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F34 <B 0x04>", await host.Ask("S2F33 W <L <U4 0> <L <L <U4 1000> <L <U4 2001> <U4 7777>>>>>"));
        Assert.Equal("S2F34 <B 0x00>", await host.Ask("S2F33 W <L <U4 0> <L <L <U4 1000> <L <U4 1002> <U4 1201> <U4 4001> <U4 1101>>>>>"));
        Assert.Equal("S6F20 <L [4] <U1 5> <U4> <F4 80> <U2 10>>", await host.Ask("S6F19 W <U4 1000>"));
    }

    [Theory]
    [InlineData("S2F33 W <U4 0>")]
    [InlineData("S2F33 W <L <U4 0> <U4 1>>")]
    [InlineData("S2F33 W <L <U4 0> <L <L <F4 1000> <L <U4 2001>>>>>")]
    [InlineData("S2F33 W <L <U4 0> <L <L <U4 1000> <U4 2001>>>>")]
    [InlineData("S2F33 W <L <U4 0> <L <L <U4 1000> <L <A \"2001\">>>>>")]
    [InlineData("S2F33 W <L <U4 0> <L <L <I8 -1> <L <U4 2001>>>>>")]                    // RptidFormat(U4)로 보낼 수 없음
    public async Task S2F33_Malformed_Drack2(string sml)
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F34 <B 0x02>", await host.Ask(sml));
    }

    [Fact]
    public async Task S2F33_LenientIdFormats_DataIdIgnored()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F34 <B 0x00>", await host.Ask("S2F33 W <L <B 0x07> <L <L <U1 9> <L <U2 2001> <I4 3001>>>>>"));
        Assert.Equal("S6F20 <L [2] <F4 25> <A \"\">>", await host.Ask("S6F19 W <I8 9>"));
    }
    #endregion

    #region S2F35
    [Fact]
    public async Task S2F35_Link_Lrack0_DoesNotChangeEnabled()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        await host.Ask(Define1000);
        Assert.Equal("S2F36 <B 0x00>", await host.Ask(Link5001));
        Assert.False(eq.TriggerEvent(5001));                                              // 아직 비활성
        Assert.Equal("S2F36 <B 0x00>", await host.Ask("S2F35 W <L <U4 0> <L>>"));         // 빈 목록: 아무것도 안 함
        await host.Ask(Enable5001);
        Assert.True(eq.TriggerEvent(5001));
        Assert.Equal("<L [1] <L [2] <U4 1000> <L [2] <F4 25> <A \"\">>>>", host.Next(6, 11).Item!.Items[2].ToSml());
    }

    [Fact]
    public async Task S2F35_EmptyRptidList_Unlinks_ThenRelinkAllowed()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        await host.Ask(Define1000);
        await host.Ask(Link5001);
        Assert.Equal("S2F36 <B 0x03>", await host.Ask(Link5001));                        // 이미 링크 있음(덧붙이지 않음)
        Assert.Equal("S2F36 <B 0x00>", await host.Ask("S2F35 W <L <U4 0> <L <L <U4 5001> <L>>>>"));
        Assert.Equal("S2F36 <B 0x00>", await host.Ask(Link5001));
    }

    [Fact]
    public async Task S2F35_UnknownCeid4_UnknownRptid5_DuplicateCeid3_AllOrNothing()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        await host.Ask(Define1000);
        Assert.Equal("S2F36 <B 0x04>", await host.Ask("S2F35 W <L <U4 0> <L <L <U4 7777> <L <U4 1000>>>>>"));
        Assert.Equal("S2F36 <B 0x05>", await host.Ask("S2F35 W <L <U4 0> <L <L <U4 5001> <L <U4 1000>>> <L <U4 5002> <L <U4 1999>>>>>"));
        Assert.Equal("S2F36 <B 0x00>", await host.Ask(Link5001));                         // 앞 메시지의 5001 링크는 적용되지 않았음
        Assert.Equal("S2F36 <B 0x03>", await host.Ask("S2F35 W <L <U4 0> <L <L <U4 5002> <L <U4 1000>>> <L <U4 5002> <L>>>>"));
    }

    [Theory]
    [InlineData("S2F35 W <L <U4 0>>")]
    [InlineData("S2F35 W <L <U4 0> <L <L <U4 5001>>>>")]
    [InlineData("S2F35 W <L <U4 0> <L <L <F8 5001> <L <U4 1000>>>>>")]
    public async Task S2F35_Malformed_Lrack2(string sml)
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F36 <B 0x02>", await host.Ask(sml));
    }
    #endregion

    #region S2F37 / S2F39
    [Fact]
    public async Task S2F37_EnableSpecific_EmptyListMeansAll_EventsEnabledSv()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S1F4 <L [1] <U4>>", await host.Ask("S1F3 W <L <U4 1004>>"));
        Assert.Equal("S2F38 <B 0x00>", await host.Ask("S2F37 W <L <BOOLEAN T> <L <U4 5002> <U4 9001>>>"));
        Assert.Equal("S1F4 <L [1] <U4 5002 9001>>", await host.Ask("S1F3 W <L <U4 1004>>"));   // 정의 순서
        Assert.Equal("S2F38 <B 0x00>", await host.Ask("S2F37 W <L <BOOLEAN T> <L>>"));
        Assert.Equal(10, eq.GetValue(1004).Count);
        Assert.Equal("S2F38 <B 0x00>", await host.Ask("S2F37 W <L <B 0x00> <L>>"));          // CEED를 B로(관대하게): 모두 끔
        Assert.Equal(SecsItem.U4(), eq.GetValue(1004));
    }

    [Fact]
    public async Task S2F37_UnknownCeid_Erack1_NothingChanged()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F38 <B 0x01>", await host.Ask("S2F37 W <L <BOOLEAN T> <L <U4 5001> <U4 7777>>>"));
        Assert.Equal(SecsItem.U4(), eq.GetValue(1004));
        Assert.Equal(7, (await Assert.ThrowsAsync<SecsErrorException>(() => host.SendAsync("S2F37 W <L <A \"T\"> <L>>"))).Function);   // 구조 오류는 S9F7
    }

    [Fact]
    public async Task S2F39_AlwaysGranted()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F40 <B 0x00>", await host.Ask("S2F39 W <L <U4 1> <U4 100000>>"));
    }
    #endregion

    #region S6F11
    [Fact]
    public async Task TriggerEvent_GoldenBody_DataValuesOverride_DataIdCounts()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        await host.Ask(Define1000);
        await host.Ask(Link5001);
        await host.Ask(Enable5001);
        Assert.True(eq.TriggerEvent(5001, new Dictionary<GemId, SecsItem> { [3001] = SecsItem.A("LOT-1") }));
        Assert.Equal("S6F11 W <L [3] <U4 1> <U4 5001> <L [1] <L [2] <U4 1000> <L [2] <F4 25> <A \"LOT-1\">>>>>", host.Next(6, 11).ToSml());
        Assert.Equal(SecsItem.A(""), eq.GetValue(3001));                                  // 이번 리포트에만 씀
        eq.TriggerEvent(5001);
        Assert.Equal("<U4 2>", host.Next(6, 11).Item!.Items[0].ToSml());
    }

    [Theory]
    [InlineData("disabledEvent")]
    [InlineData("offline")]
    [InlineData("commDisabled")]
    public async Task TriggerEvent_NotSent_NoDiscardPath(string why)
    {
        using var eq = Eq(e =>
        {
            e.EventsEnabledByDefault = why != "disabledEvent";
            if (why == "offline") e.InitialControlState = GemControlState.HostOffline;
            if (why == "commDisabled") e.InitialCommunicationState = GemEnableState.Disabled;
        });
        var log = GemKit.Log(eq);
        using var host = why == "commDisabled" ? await GemKit.Connect(eq) : await GemKit.Communicating(eq);
        Assert.False(eq.TriggerEvent(5001));
        host.NoMore();
        Assert.DoesNotContain(log, l => l.StartsWith("SendFailed", StringComparison.Ordinal));   // 이 경우들은 버림 경로가 아님
    }

    [Fact]
    public async Task TriggerEvent_OnlineButNotCommunicating_DiscardPath()
    {
        using var eq = Eq(e => { e.EventsEnabledByDefault = true; e.InitiateCommunication = false; });
        var log = GemKit.Log(eq);
        using var host = await GemKit.Connect(eq);
        Assert.False(eq.TriggerEvent(5001));
        await TestUtil.Until(() => log.Contains("SendFailed S6F11 NotCommunicating"));
    }

    [Fact]
    public void TriggerEvent_ArgumentChecks()
    {
        using var eq = Eq();
        Assert.Throws<InvalidOperationException>(() => eq.TriggerEvent(5001));
        eq.Start();
        Assert.Equal("ceid", Assert.Throws<ArgumentException>(() => eq.TriggerEvent(7777)).ParamName);
        Assert.Equal("dataValues", Assert.Throws<ArgumentException>(() => eq.TriggerEvent(5001, new Dictionary<GemId, SecsItem> { [2001] = SecsItem.F4(1) })).ParamName);   // SV
        Assert.Equal("dataValues", Assert.Throws<ArgumentException>(() => eq.TriggerEvent(5001, new Dictionary<GemId, SecsItem> { [3001] = SecsItem.U4(1) })).ParamName);  // 포맷
        Assert.Equal("dataValues", Assert.Throws<ArgumentException>(() => eq.TriggerEvent(5001, new Dictionary<GemId, SecsItem> { [9999] = SecsItem.U4(1) })).ParamName);
    }

    [Fact]
    public async Task TriggerEvent_ValuesSnapshotAtCall_SentInOrderOneAtATime()
    {
        using var eq = Eq(e => e.EventsEnabledByDefault = true);
        using var host = await GemKit.Communicating(eq, h => h.Hold(6, 11));
        await host.Ask("S2F33 W <L <U4 0> <L <L <U4 1000> <L <U4 2002>>>>>");
        await host.Ask("S2F35 W <L <U4 0> <L <L <U4 5001> <L <U4 1000>>> <L <U4 5002> <L <U4 1000>>>>>");
        eq.SetValue(2002, SecsItem.U4(1));
        eq.TriggerEvent(5001);
        eq.SetValue(2002, SecsItem.U4(2));
        eq.TriggerEvent(5002);
        eq.SetValue(2002, SecsItem.U4(3));
        Assert.True(host.Held.TryTake(out var first, 3000));
        Assert.Equal("<L [1] <L [2] <U4 1000> <L [1] <U4 1>>>>", first!.Message.Item!.Items[2].ToSml());
        Assert.False(host.Held.TryTake(out _, 300));                                      // 첫 응답 전에는 다음 것을 보내지 않음
        first.Reply(new SecsMessage(6, 12, false, SecsItem.B(0)));
        Assert.True(host.Held.TryTake(out var second, 3000));
        Assert.Equal("<L [1] <L [2] <U4 1000> <L [1] <U4 2>>>>", second!.Message.Item!.Items[2].ToSml());   // 부른 순간의 값
        Assert.Equal((5001, 5002), (first.Message.Item.Items[1].GetInt64(), second.Message.Item!.Items[1].GetInt64()));
        second.Reply(new SecsMessage(6, 12, false, SecsItem.B(0)));
    }

    [Fact]
    public async Task TriggerEvent_AckNonZero_And_Abort_AreSendFailed_NoResend()
    {
        using var eq = Eq(e => e.EventsEnabledByDefault = true);
        var log = GemKit.Log(eq);
        using var host = await GemKit.Communicating(eq, h => h.Reply(6, 11, "<B 3>"));
        eq.TriggerEvent(5001);
        host.Next(6, 11);
        await TestUtil.Until(() => log.Contains("SendFailed S6F11 NotAcknowledged 3"));
        host.Reply(6, 11, null);
        eq.TriggerEvent(5001);
        host.Next(6, 11);
        await TestUtil.Until(() => log.Contains("SendFailed S6F11 Aborted"));
        host.Reply(6, 11, "<A \"OK\">");
        eq.TriggerEvent(5001);
        host.Next(6, 11);
        await TestUtil.Until(() => log.Contains("SendFailed S6F11 InvalidReply"));
        host.NoMore();
        Assert.Equal(GemCommunicationState.Communicating, eq.CommunicationState);
    }

    [Fact]
    public async Task EventReplyExpectedFalse_NoWBit()
    {
        using var eq = Eq(e => { e.EventsEnabledByDefault = true; e.EventReplyExpected = false; });
        using var host = await GemKit.Communicating(eq);
        eq.TriggerEvent(5002);
        Assert.False(host.Next(6, 11).ReplyExpected);
    }

    [Fact]
    public async Task QueueFull_DiscardsNew_SendFailedQueueFull()
    {
        using var eq = Eq(e => { e.EventsEnabledByDefault = true; e.MaxPendingSends = 2; });
        var log = GemKit.Log(eq);
        using var host = await GemKit.Communicating(eq, h => h.Hold(6, 11));
        Assert.True(eq.TriggerEvent(5001));
        Assert.True(host.Held.TryTake(out var held, 3000));                               // 1개는 응답 대기 중
        Assert.True(eq.TriggerEvent(5001));
        Assert.True(eq.TriggerEvent(5001));
        Assert.False(eq.TriggerEvent(5001));                                              // 대기열 2개 + 응답 대기 1개 → 넘침
        await TestUtil.Until(() => log.Contains("SendFailed S6F11 QueueFull"));
        held!.Reply(new SecsMessage(6, 12, false, SecsItem.B(0)));
    }

    [Fact]
    public async Task DisableCommunication_And_LinkLoss_DiscardQueue()
    {
        using var eq = Eq(e => e.EventsEnabledByDefault = true);
        var log = GemKit.Log(eq);
        var host = await GemKit.Communicating(eq, h => h.Hold(6, 11));
        eq.TriggerEvent(5001);
        eq.TriggerEvent(5002);
        Assert.True(host.Held.TryTake(out _, 3000));
        eq.DisableCommunication();
        await TestUtil.Until(() => log.Count(l => l == "SendFailed S6F11 Disabled") == 2);   // 응답 대기 1 + 대기열 1
        eq.EnableCommunication();
        host.NextOf(1, 13);
        await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.Communicating);
        eq.TriggerEvent(5001);
        eq.TriggerEvent(5002);
        Assert.True(host.Held.TryTake(out _, 3000));
        host.Dispose();                                                                  // 통신 실패(전이 14)
        await TestUtil.Until(() => log.Count(l => l == "SendFailed S6F11 NotCommunicating") == 2, 5000);
    }

    [Fact]
    public async Task LinkLoss_SendFailsBeforeDeviceClosed_StillDiscardPath()
    {
        // 끊기면 전송은 응답 대기 트랜잭션을 먼저 SecsDisconnectedException으로 끝내고(순서 있는 완료라 디스패처에서 DeviceClosed보다 앞) DeviceClosed는 그 뒤에 낸다.
        // 끊김 알림(전송 ProtocolError, DeviceClosed 바로 앞)에서 디스패처를 붙잡아도(앱 핸들러의 head-of-line) GEM은 끊김 실패 완료로는 상태를 바꾸지 않고,
        // 뒤이은 DeviceClosed의 전이 14가 응답 대기·대기열을 함께 버림 경로로 보낸다(붙잡는 동안 COMMUNICATING 그대로라 SpinUntil은 시간 초과로 풀린다)
        using var eq = Eq(e => e.EventsEnabledByDefault = true);
        var log = GemKit.Log(eq);
        bool arm = false, blocked = false;
        ((SecsHsms)eq.Transport).ProtocolError += (s, e) =>
        {
            if (!Volatile.Read(ref arm)) return;
            blocked = true;
            SpinWait.SpinUntil(() => eq.CommunicationState != GemCommunicationState.Communicating, 1000);
        };
        var host = await GemKit.Communicating(eq, h => h.Hold(6, 11));
        eq.TriggerEvent(5001);
        eq.TriggerEvent(5002);
        Assert.True(host.Held.TryTake(out _, 3000));
        Volatile.Write(ref arm, true);
        host.Dispose();
        await TestUtil.Until(() => log.Count(l => l == "SendFailed S6F11 NotCommunicating") == 2);   // 응답 대기 1 + 대기열 1, 모두 버림 경로
        Assert.True(blocked);
        await Task.Delay(300);
        Assert.DoesNotContain(log, l => l.StartsWith("SendFailed S6F11 Disconnected", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(SecsFormat.U1)]
    [InlineData(SecsFormat.U2)]
    [InlineData(SecsFormat.U4)]
    [InlineData(SecsFormat.U8)]
    [InlineData(SecsFormat.I1)]
    [InlineData(SecsFormat.I2)]
    [InlineData(SecsFormat.I4)]
    [InlineData(SecsFormat.I8)]
    [InlineData(SecsFormat.ASCII)]
    public async Task S6F11_GoldenBody_EachIdFormat(SecsFormat f)
    {
        string Id(long n) => f == SecsFormat.ASCII ? $"<A \"{n}\">" : $"<{GemItems.FormatName(f)} {n}>";
        using var eq = GemKit.Equipment(e =>
        {
            e.VidFormat = e.CeidFormat = e.RptidFormat = e.AlidFormat = e.DataidFormat = f;
            SmallStandardIds(e.StandardIds);
            e.AddStatusVariable(new GemStatusVariable(21, "Temp", SecsFormat.F4, initialValue: SecsItem.F4(25)));
            e.AddCollectionEvent(new GemCollectionEvent(51, "Started"));
            e.InitialControlState = GemControlState.OnlineRemote;
            e.EventsEnabledByDefault = true;
        });
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F34 <B 0x00>", await host.Ask($"S2F33 W <L <U4 0> <L <L {Id(7)} <L {Id(21)} {Id(102)}>>>>"));
        Assert.Equal("S2F36 <B 0x00>", await host.Ask($"S2F35 W <L <U4 0> <L <L {Id(51)} <L {Id(7)}>>>>"));
        eq.TriggerEvent(51);
        Assert.Equal($"S6F11 W <L [3] {Id(1)} {Id(51)} <L [1] <L [2] {Id(7)} <L [2] <F4 25> <U1 5>>>>>", host.Next(6, 11).ToSml());
    }

    /// 1바이트 포맷(U1·I1)에도 들어가는 표준 ID
    internal static void SmallStandardIds(GemStandardIds ids)
    {
        long n = 100;
        foreach (var s in Enum.GetValues<GemStandard>()) ids.Set(s, ++n);
    }

    [Fact]
    public async Task DataId_WrapsToOne_AfterFormatMax()
    {
        using var eq = Eq(e => e.DataidFormat = SecsFormat.I1);
        using var host = await GemKit.Communicating(eq);
        for (int i = 1; i <= 127; i++) await host.SendAsync("S6F15 W <U4 5002>");
        Assert.Equal("<I1 1>", (await host.SendAsync("S6F15 W <U4 5002>"))!.Item!.Items[0].ToSml());
    }

    [Fact]
    public async Task StandardControlEvents_CeBeforeOfflineChange_StateCeAfterOnline()
    {
        using var eq = Eq(e => e.EventsEnabledByDefault = true);
        using var host = await GemKit.Communicating(eq);
        await host.Ask("S2F33 W <L <U4 0> <L <L <U4 1000> <L <U4 1002>>>>>");
        await host.Ask("S2F35 W <L <U4 0> <L <L <U4 1301> <L <U4 1000>>> <L <U4 1302> <L <U4 1000>>> <L <U4 1303> <L <U4 1000>>>>>");
        string Ce(SecsMessage m) => $"{m.Item!.Items[1].GetInt64()}:{m.Item.Items[2].Items[0].Items[1].Items[0].GetInt64()}";
        eq.SwitchLocal();                                                                  // 전이 9: 상태 바꾼 뒤 CE
        Assert.Equal("1302:4", Ce(host.Next(6, 11)));
        await host.Ask("S1F15 W");                                                        // 전이 10: CE를 바꾸기 전에(ControlState 4)
        Assert.Equal("1301:4", Ce(host.Next(6, 11)));
        await host.Ask("S1F17 W");                                                        // 전이 11 → 7(LOCAL)
        Assert.Equal("1302:4", Ce(host.Next(6, 11)));
        eq.SwitchRemote();                                                                 // 전이 8
        Assert.Equal("1303:5", Ce(host.Next(6, 11)));
        eq.GoOffline();                                                                    // 전이 6
        Assert.Equal("1301:5", Ce(host.Next(6, 11)));
        eq.GoOnline();                                                                     // 전이 3 → 5 → 7(REMOTE)
        host.Next(1, 1);
        Assert.Equal("1303:5", Ce(host.Next(6, 11)));
        await host.Ask("S1F15 W");
        host.Next(6, 11);
        Assert.Equal(GemControlState.HostOffline, eq.ControlState);
        Assert.True(eq.GoOffline());                                                       // 전이 12: 이미 OFF-LINE이라(E30: 장비 주도 메시지 금지) EquipmentOffline CE를 보내지 않음
        Assert.Equal(GemControlState.EquipmentOffline, eq.ControlState);
        host.NoMore();
    }

    [Fact]
    public async Task S6F15_SameBodyAsS6F11_RegardlessOfEnable_UnknownCeid()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        await host.Ask(Define1000);
        await host.Ask(Link5001);
        Assert.Equal("S6F16 <L [3] <U4 1> <U4 5001> <L [1] <L [2] <U4 1000> <L [2] <F4 25> <A \"\">>>>>", await host.Ask("S6F15 W <U2 5001>"));
        Assert.Equal("S6F16 <L [3] <U4 2> <I2 777> <L [0]>>", await host.Ask("S6F15 W <I2 777>"));
        Assert.Equal(7, (await Assert.ThrowsAsync<SecsErrorException>(() => host.SendAsync("S6F15 W <L>"))).Function);
    }
    #endregion
}
