using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Secs.Hsms;

namespace Devinno.Communication.Tests.Secs.Gem;

/// GemHost 명령 메서드 S1(통신 수립·온라인·상태·이름)·S2(EC·시계): 우리 GemEquipment와 루프백, 가짜 장비로 골든 요청·관대한 ID·예외
[Collection(nameof(GemHostCollection))]
public class GemHostCommandTests
{
    static Action<GemEquipment> Online => e => e.InitialControlState = GemControlState.OnlineRemote;

    [Fact]
    public async Task EstablishCommunication_Loopback_Commack0_ModelStored()
    {
        using var eq = GemKit.Equipment(e => { GemKit.Sample(e); e.InitiateCommunication = false; });
        eq.Start();
        using var host = GemHostKit.Host(GemKit.Port(eq));
        host.Start();
        await TestUtil.Until(() => host.IsOpen, 5000);
        Assert.False(host.IsCommunicating);
        Assert.Equal(0, await host.EstablishCommunicationAsync());
        Assert.True(host.IsCommunicating);
        Assert.Equal(("DV-100", "1.0.0"), (host.ModelName, host.SoftwareRevision));
        await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.Communicating);
    }

    [Fact]
    public async Task EstablishCommunication_Fake_GoldenRequest_Commack1_IsValueNotException()
    {
        var (fake, host) = await GemHostKit.Fake(fakeSetup: f => f.Reply(1, 13, "<L <B 1> <L>>"));
        using var _ = fake;
        using var __ = host;
        Assert.Equal(1, await host.EstablishCommunicationAsync());
        Assert.Equal("S1F13 W <L [0]>", fake.Next(1, 13).ToSml());
        Assert.False(host.IsCommunicating);
    }

    [Fact]
    public async Task OnlineOffline_AreYouThere_Loopback()
    {
        var (eq, host) = await GemHostKit.Loopback();
        using var _ = eq;
        using var __ = host;
        await Assert.ThrowsAsync<SecsAbortException>(() => host.AreYouThereAsync());      // 장비가 HOST OFF-LINE: S1F1에 S1F0
        Assert.Equal(GemAck.OnlackAccepted, host.RequestOnline());
        await TestUtil.Until(() => eq.ControlState == GemControlState.OnlineRemote);
        Assert.Equal(("DV-100", "1.0.0"), host.AreYouThere());
        Assert.Equal(GemAck.OnlackAlreadyOnline, await host.RequestOnlineAsync());
        Assert.Equal(GemAck.OflackAccepted, await host.RequestOfflineAsync());
        await TestUtil.Until(() => eq.ControlState == GemControlState.HostOffline);
        await Assert.ThrowsAsync<SecsAbortException>(() => host.RequestOfflineAsync());   // 이미 OFF-LINE: S1F0
    }

    [Fact]
    public async Task AreYouThere_EmptyList_BothEmpty_GoldenRequests()
    {
        var (fake, host) = await GemHostKit.Fake(fakeSetup: f => f.Reply(1, 1, "<L>"));
        using var _ = fake;
        using var __ = host;
        Assert.Equal(("", ""), await host.AreYouThereAsync());
        Assert.Equal("S1F1 W", fake.Next(1, 1).ToSml());
        await host.RequestOnlineAsync();
        Assert.Equal("S1F17 W", fake.Next(1, 17).ToSml());
        await host.RequestOfflineAsync();
        Assert.Equal("S1F15 W", fake.Next(1, 15).ToSml());
    }

    [Fact]
    public async Task ReadStatus_Loopback_SpecificAllUnknown()
    {
        var (eq, host) = await GemHostKit.Loopback(Online);
        using var _ = eq;
        using var __ = host;
        eq.SetValue(2002, SecsItem.U4(7));
        var values = await host.ReadStatusAsync([2001, 2002, 9999]);
        Assert.Equal(["<F4 25>", "<U4 7>", "<L [0]>"], values.Select(v => v.ToSml()));
        var all = host.ReadStatus();
        Assert.Equal(10, all.Count);                                                        // 표준 SV 8개 + 정의 2개
        Assert.Equal(SecsItem.F4(25), all[8]);
    }

    [Fact]
    public async Task Names_Loopback_StatusDataEvent()
    {
        var (eq, host) = await GemHostKit.Loopback(Online);
        using var _ = eq;
        using var __ = host;
        var sv = await host.ReadStatusNamesAsync([2001, 9999]);
        Assert.Equal(("2001", "Temperature", "C"), (sv[0].Id.ToString(), sv[0].Name, sv[0].Units));
        Assert.Equal(("9999", ""), (sv[1].Id.ToString(), sv[1].Name));
        Assert.Contains(host.ReadDataVariableNames(), v => v.Id == 3001 && v.Name == "LotID");
        var ce = host.ReadEventNames(5001);
        Assert.Equal("ProcessStarted", ce.Single().Name);
        Assert.Equal([new GemId(2001), new GemId(3001)], ce.Single().Vids);
        Assert.Contains(await host.ReadEventNamesAsync(), e => e.Ceid == 5002);
    }

    [Fact]
    public async Task Constants_Loopback_ReadWriteNames_AckIsValue()
    {
        var (eq, host) = await GemHostKit.Loopback(Online);
        using var _ = eq;
        using var __ = host;
        Assert.Equal(SecsItem.F4(80), (await host.ReadConstantsAsync([4001])).Single());
        Assert.Equal(GemAck.EacAccepted, await host.WriteConstantsAsync([(4001, SecsItem.F4(120))]));
        Assert.Equal(SecsItem.F4(120), eq.GetValue(4001));
        Assert.Equal(GemAck.EacOutOfRange, host.WriteConstants([(4001, SecsItem.F4(500))]));   // 0이 아닌 ack는 예외가 아니라 값
        Assert.Equal(GemAck.EacUnknownConstant, host.WriteConstants([(9999, SecsItem.U4(1))]));
        var info = host.ReadConstantNames(4001).Single();
        Assert.Equal(("MaxTemp", "C"), (info.Name, info.Units));
        Assert.Equal((SecsItem.F4(0), SecsItem.F4(200), SecsItem.F4(80)), (info.Min, info.Max, info.Default));
        Assert.Throws<ArgumentException>(() => host.WriteConstants([]));
        Assert.Throws<ArgumentNullException>(() => host.WriteConstants([(4001, null!)]));
    }

    [Fact]
    public async Task Clock_Loopback_SetThenRead()
    {
        var (eq, host) = await GemHostKit.Loopback(Online);
        using var _ = eq;
        using var __ = host;
        var set = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Local);
        Assert.Equal(GemAck.TiackAccepted, await host.SetEquipmentTimeAsync(set));
        var read = await host.ReadEquipmentTimeAsync();
        Assert.True(Math.Abs((read - set).TotalSeconds) < 5, read.ToString("O"));
        Assert.Equal(GemAck.TiackAccepted, host.SetEquipmentTime());                     // null = Host의 지금 시각
        Assert.True(Math.Abs((host.ReadEquipmentTime() - DateTime.Now).TotalSeconds) < 5);
    }

    [Fact]
    public async Task GoldenRequests_ConfiguredFormats()
    {
        var (fake, host) = await GemHostKit.Fake(h => { h.VidFormat = SecsFormat.U2; h.CeidFormat = SecsFormat.ASCII; h.TimeFormat = 0; }, f =>
        {
            f.Reply(1, 3, "<L <U4 1> <U4 2>>");
            f.Reply(1, 23, "<L <L <A \"5001\"> <A \"E\"> <L>>>");
            f.Reply(2, 13, "<L>");
            f.Reply(2, 15, "<B 0>");
        });
        using var _ = fake;
        using var __ = host;
        await host.ReadStatusAsync([1, 2]);
        Assert.Equal("S1F3 W <L [2] <U2 1> <U2 2>>", fake.Next(1, 3).ToSml());
        await host.ReadEventNamesAsync([5001]);
        Assert.Equal("S1F23 W <L [1] <A \"5001\">>", fake.Next(1, 23).ToSml());            // A 포맷: 숫자 ID는 10진 문자열
        await host.ReadConstantsAsync();
        Assert.Equal("S2F13 W <L [0]>", fake.Next(2, 13).ToSml());
        await host.WriteConstantsAsync([(4001, SecsItem.F4(120))]);
        Assert.Equal("S2F15 W <L [1] <L [2] <U2 4001> <F4 120>>>", fake.Next(2, 15).ToSml());
        await host.SetEquipmentTimeAsync(new DateTime(2030, 1, 2, 3, 4, 5));
        Assert.Equal("S2F31 W <A \"300102030405\">", fake.Next(2, 31).ToSml());
    }

    [Fact]
    public async Task LenientReplyIds_AnyIntegerOrAscii()
    {
        var (fake, host) = await GemHostKit.Fake(fakeSetup: f =>
        {
            f.Reply(1, 11, "<L <L <I2 7> <A \"X\"> <A \"u\">> <L <A \"SV8\"> <A \"Y\"> <A \"\">>>");
            f.Reply(1, 23, "<L <L <U1 5> <A \"E\"> <L <U8 1> <I1 -2> <A \"V\">>>>");
            f.Reply(2, 29, "<L <L <U2 4001> <A \"MaxTemp\"> <L> <L> <F8 1.5> <A \"\">>>");
        });
        using var _ = fake;
        using var __ = host;
        var sv = await host.ReadStatusNamesAsync();
        Assert.Equal([new GemId(7), new GemId("SV8")], sv.Select(v => v.Id));
        var ce = (await host.ReadEventNamesAsync()).Single();
        Assert.Equal((new GemId(5), "E"), (ce.Ceid, ce.Name));
        Assert.Equal([new GemId(1), new GemId(-2), new GemId("V")], ce.Vids);
        var ec = (await host.ReadConstantNamesAsync()).Single();
        Assert.Equal((new GemId(4001), "<L [0]>", "<F8 1.5>"), (ec.Id, ec.Min.ToSml(), ec.Default.ToSml()));
    }

    [Fact]
    public async Task Errors_PlanSevenExceptions_FormatException_ArgumentChecks()
    {
        var (fake, host) = await GemHostKit.Fake(h => ((SecsHsms)h.Transport).T3 = 1000, f =>
        {
            f.Reply(1, 17, "<L>");                       // ONLACK 자리에 리스트
            f.Reply(1, 3, "<L <U4 1>>");                 // 2개를 물었는데 1개
            f.Reply(1, 15, null);                        // 응답 안 함 → S1F0
            f.Hold(2, 17);                               // 붙잡음 → T3
            f.Reply(1, 11, "<L <L <F4 1> <A \"X\"> <A \"\">>>");   // ID가 F4
        });
        using var _ = fake;
        using var __ = host;
        Assert.Contains("S1F18", (await Assert.ThrowsAsync<FormatException>(() => host.RequestOnlineAsync())).Message);
        Assert.Contains("요청(2개)", (await Assert.ThrowsAsync<FormatException>(() => host.ReadStatusAsync([1, 2]))).Message);
        await Assert.ThrowsAsync<SecsAbortException>(() => host.RequestOfflineAsync());
        await Assert.ThrowsAsync<SecsTimeoutException>(() => host.ReadEquipmentTimeAsync());
        await Assert.ThrowsAsync<FormatException>(() => host.ReadStatusNamesAsync());
        Assert.Throws<ArgumentException>(() => host.ReadStatus("TEMP"));                   // 숫자 포맷에 문자열 ID
        host.Stop();
        host.VidFormat = SecsFormat.U1;
        Assert.Throws<ArgumentOutOfRangeException>(() => host.ReadStatus(300));            // U1 범위 밖
        Assert.Throws<InvalidOperationException>(() => host.ReadStatus(1));                // 연결 전
    }

    [Fact]
    public async Task SyncCommand_FromAppEventHandler_NoDeadlock()
    {
        var result = new TaskCompletionSource<SecsItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        GemEquipment? equipment = null;
        var (eq, host) = await GemHostKit.Loopback(e => { Online(e); equipment = e; }, h => h.CommunicationStateChanged += (s, e) =>
        {
            if (e.Current != GemCommunicationState.Communicating) return;
            // 장비도 S1F14를 받아 수립될 때까지(그 전의 S1F3은 장비가 S1F0으로 답함)
            SpinWait.SpinUntil(() => equipment!.CommunicationState == GemCommunicationState.Communicating, 3000);
            try { result.TrySetResult(((GemHost)s!).ReadStatus(2001).Single()); }      // 앱 이벤트 스레드에서 동기 호출
            catch (Exception ex) { result.TrySetException(ex); }
        });
        using var _ = eq;
        using var __ = host;
        Assert.Equal(SecsItem.F4(25), await result.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    sealed class NoPostContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => throw new InvalidOperationException("await가 호출 스레드의 동기화 문맥으로 돌아오려 함");
    }

    [Fact]
    public async Task SyncCommand_UnderSynchronizationContext_NeverCapturesContext()
    {
        var (eq, host) = await GemHostKit.Loopback(Online);
        using var _ = eq;
        using var __ = host;
        Exception? error = null;
        SecsItem? value = null;
        var t = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NoPostContext());     // UI 스레드 흉내: 문맥으로 돌아오면 실패
            try { value = host.ReadStatus(2001).Single(); }
            catch (Exception ex) { error = ex; }
        });
        t.Start();
        Assert.True(t.Join(5000));
        Assert.Null(error);
        Assert.Equal(SecsItem.F4(25), value);
    }
}
