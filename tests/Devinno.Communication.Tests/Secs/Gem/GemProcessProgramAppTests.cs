using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 장비 쪽 레시피 앱 메서드(Plan 16 Task 5): 운전자 저장·삭제·선택과 CE, 장비 → Host 업로드·요청
public class GemProcessProgramAppTests
{
    static GemEquipment Eq(InMemoryProcessProgramStore store, Action<GemEquipment>? more = null) => GemKit.Equipment(e =>
    {
        GemKit.Sample(e);
        e.InitialControlState = GemControlState.OnlineRemote;
        e.EventsEnabledByDefault = true;
        e.ProcessProgramStore = store;
        more?.Invoke(e);
    });

    // CE 1310에 (PPChangeName, PPChangeStatus), CE 1311에 (PPExecName) 리포트를 붙인다
    static async Task LinkReports(FakeGemHost host)
    {
        Assert.Equal("S2F34 <B 0x00>", await host.Ask("S2F33 W <L <U4 1> <L <L <U4 7001> <L <U4 1202> <U4 1203>>> <L <U4 7002> <L <U4 1013>>>>>"));
        Assert.Equal("S2F36 <B 0x00>", await host.Ask("S2F35 W <L <U4 1> <L <L <U4 1310> <L <U4 7001>>> <L <U4 1311> <L <U4 7002>>>>>"));
    }

    static string Values(SecsMessage s6f11) => s6f11.Item!.Items[2].Items[0].Items[1].ToSml();

    static readonly GemProcessProgram R1 = new("R1", SecsItem.B(1, 2, 3));

    [Fact]
    public async Task Save_New_Then_Edit_FiresChangeEvents()
    {
        var store = new InMemoryProcessProgramStore();
        using var eq = Eq(store);
        using var host = await GemKit.Communicating(eq);
        await LinkReports(host);
        eq.SaveProcessProgram(R1);
        var m = host.Next(6, 11);
        Assert.Equal(1310UL, m.Item!.Items[1].GetUInt64());
        Assert.Equal("<L [2] <A \"R1\"> <U1 1>>", Values(m));
        eq.SaveProcessProgram(new GemProcessProgram("R1", SecsItem.B(9)));
        Assert.Equal("<L [2] <A \"R1\"> <U1 2>>", Values(host.Next(6, 11)));
        Assert.Equal(SecsItem.B(9), store.Peek("R1")!.Body);
    }

    [Fact]
    public async Task Delete_FiresChangeEvent_AndClearsSelection()
    {
        var store = new InMemoryProcessProgramStore();
        store.Put(R1);
        using var eq = Eq(store);
        using var host = await GemKit.Communicating(eq);
        await LinkReports(host);
        eq.SelectProcessProgram("R1");
        host.Next(6, 11);
        Assert.True(eq.DeleteProcessProgram("R1"));
        Assert.Equal("<L [2] <A \"R1\"> <U1 3>>", Values(host.Next(6, 11)));
        Assert.Null(eq.SelectedProcessProgram);
        Assert.Equal("S1F4 <L [1] <A \"\">>", await host.Ask("S1F3 W <L <U4 1013>>"));
        host.NoneOf(6);
    }

    [Fact]
    public async Task Delete_Unknown_ReturnsFalse_NoEvent()
    {
        using var eq = Eq(new InMemoryProcessProgramStore());
        using var host = await GemKit.Communicating(eq);
        await LinkReports(host);
        Assert.False(eq.DeleteProcessProgram("NOPE"));
        host.NoneOf(6);
    }

    [Fact]
    public async Task Select_UpdatesPPExecName_FiresSelectedEvent()
    {
        var store = new InMemoryProcessProgramStore();
        store.Put(R1);
        using var eq = Eq(store);
        using var host = await GemKit.Communicating(eq);
        await LinkReports(host);
        eq.SelectProcessProgram("R1");
        var m = host.Next(6, 11);
        Assert.Equal(1311UL, m.Item!.Items[1].GetUInt64());
        Assert.Equal("<L [1] <A \"R1\">>", Values(m));
        Assert.Equal("R1", eq.SelectedProcessProgram);
        eq.SelectProcessProgram(null);
        host.NoneOf(6);
        Assert.Equal("S1F4 <L [1] <A \"\">>", await host.Ask("S1F3 W <L <U4 1013>>"));
        Assert.Throws<ArgumentException>(() => eq.SelectProcessProgram("NOPE"));
    }

    [Fact]
    public void Off_MethodsThrow()
    {
        using var eq = GemKit.Equipment();
        eq.Start();
        Assert.Throws<InvalidOperationException>(() => eq.SaveProcessProgram(R1));
        Assert.Throws<InvalidOperationException>(() => eq.DeleteProcessProgram("R1"));
        Assert.Throws<InvalidOperationException>(() => eq.SelectProcessProgram("R1"));
        Assert.ThrowsAsync<InvalidOperationException>(() => eq.UploadProcessProgramAsync("R1")).Wait();
        Assert.ThrowsAsync<InvalidOperationException>(() => eq.RequestProcessProgramAsync("R1")).Wait();
    }

    [Fact]
    public async Task Upload_WithInquire_Success()
    {
        var store = new InMemoryProcessProgramStore();
        store.Put(R1);
        using var eq = Eq(store);
        using var host = await GemKit.Communicating(eq, h => { h.Reply(7, 1, "<B 0>"); h.Reply(7, 3, "<B 0>"); });
        var r = await eq.UploadProcessProgramAsync("R1");
        var s7f1 = host.Next(7, 1);
        Assert.Equal(3UL, s7f1.Item!.Items[1].GetUInt64());                                  // PPBODY 데이터 3바이트
        Assert.Equal("<L [2] <A \"R1\"> <B 0x01 0x02 0x03>>", host.Next(7, 3).Item!.ToSml());
        Assert.True(r.IsSuccess);
        Assert.Equal((byte)0, r.Ppgnt);
        Assert.Equal((byte)0, r.Ackc7);
        Assert.Equal(GemUploadFailure.None, r.Failure);
    }

    [Fact]
    public async Task Upload_WithoutInquire_SendsS7F3Only()
    {
        var store = new InMemoryProcessProgramStore();
        store.Put(R1);
        using var eq = Eq(store);
        using var host = await GemKit.Communicating(eq, h => h.Reply(7, 3, "<B 0>"));
        var r = await eq.UploadProcessProgramAsync("R1", inquire: false);
        Assert.True(r.IsSuccess);
        Assert.Null(r.Ppgnt);
        host.Next(7, 3);
        host.NoneOf(7);
    }

    [Fact]
    public async Task Upload_GrantRefused_NoS7F3()
    {
        var store = new InMemoryProcessProgramStore();
        store.Put(R1);
        using var eq = Eq(store);
        using var host = await GemKit.Communicating(eq, h => h.Reply(7, 1, "<B 2>"));
        var r = await eq.UploadProcessProgramAsync("R1");
        Assert.Equal(GemUploadFailure.Rejected, r.Failure);
        Assert.Equal((byte)2, r.Ppgnt);
        Assert.Null(r.Ackc7);
        host.Next(7, 1);
        host.NoneOf(7);
    }

    [Fact]
    public async Task Upload_Ackc7Nonzero_Rejected()
    {
        var store = new InMemoryProcessProgramStore();
        store.Put(R1);
        using var eq = Eq(store);
        using var host = await GemKit.Communicating(eq, h => h.Reply(7, 3, "<B 3>"));
        var r = await eq.UploadProcessProgramAsync("R1", inquire: false);
        Assert.Equal(GemUploadFailure.Rejected, r.Failure);
        Assert.Equal((byte)3, r.Ackc7);
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public async Task Upload_NotFound_Offline_NotCommunicating_NoSendFailed()
    {
        var store = new InMemoryProcessProgramStore();
        store.Put(R1);
        using var eq = Eq(store, e => e.InitialControlState = GemControlState.HostOffline);
        var log = GemKit.Log(eq);
        eq.Start();
        Assert.Equal(GemUploadFailure.NotCommunicating, (await eq.UploadProcessProgramAsync("R1")).Failure);   // 연결 전
        using var host = new FakeGemHost(GemKit.Port(eq));
        host.Start();
        host.Next(1, 13);
        await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.Communicating, 5000);
        Assert.Equal(GemUploadFailure.Offline, (await eq.UploadProcessProgramAsync("R1")).Failure);
        Assert.Equal(GemUploadFailure.NotFound, (await eq.UploadProcessProgramAsync("NOPE")).Failure);
        await Task.Delay(200);
        Assert.Empty(GemKit.Only(log, "SendFailed"));
        host.NoneOf(7);
    }

    [Fact]
    public async Task Upload_KeepsQueueOrder()
    {
        var store = new InMemoryProcessProgramStore();
        store.Put(R1);
        using var eq = Eq(store);
        using var host = await GemKit.Communicating(eq, h => { h.Reply(7, 1, "<B 0>"); h.Reply(7, 3, "<B 0>"); });
        eq.TriggerEvent(5001);
        var upload = eq.UploadProcessProgramAsync("R1");
        eq.TriggerEvent(5002);
        Assert.True((await upload).IsSuccess);
        var order = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var m = host.NextOf(i is 0 or 2 ? 6 : 7, i switch { 0 or 2 => 11, 1 => 1, _ => 3 });
            order.Add($"S{m.Stream}F{m.Function}" + (m.Stream == 6 ? " " + m.Item!.Items[1].GetUInt64() : ""));
        }
        Assert.Equal(new[] { "S6F11 5001", "S7F1", "S6F11 5002", "S7F3" }, order);
    }

    [Fact]
    public async Task Request_Found_Denied()
    {
        var store = new InMemoryProcessProgramStore();
        using var eq = Eq(store);
        using var host = await GemKit.Communicating(eq, h => h.Reply(7, 5, "<L <A \"R9\"> <B 0x09>>"));
        var p = await eq.RequestProcessProgramAsync("R9");
        Assert.Equal("R9", p!.Ppid);
        Assert.Equal(SecsItem.B(9), p.Body);
        Assert.Equal(0, store.Count);
        host.Reply(7, 5, "<L>");
        Assert.Null(await eq.RequestProcessProgramAsync("R9"));
    }

    [Fact]
    public async Task Request_Offline_Throws()
    {
        using var eq = Eq(new InMemoryProcessProgramStore(), e => e.InitialControlState = GemControlState.HostOffline);
        using var host = await GemKit.Communicating(eq);
        var ex = await Assert.ThrowsAsync<GemProcessProgramRequestException>(() => eq.RequestProcessProgramAsync("R9"));
        Assert.Equal(GemUploadFailure.Offline, ex.Failure);
    }

    [Fact]
    public async Task Stop_CancelsPendingUpload()
    {
        var store = new InMemoryProcessProgramStore();
        store.Put(R1);
        using var eq = Eq(store);
        using var host = await GemKit.Communicating(eq, h => h.Hold(7, 1));
        var upload = eq.UploadProcessProgramAsync("R1");
        Assert.NotNull(host.Held.Take(new CancellationTokenSource(5000).Token));
        eq.Stop();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);
    }

    [Fact]
    public async Task Disconnect_FailsPendingUpload_NoSendFailed()
    {
        var store = new InMemoryProcessProgramStore();
        store.Put(R1);
        using var eq = Eq(store);
        var log = GemKit.Log(eq);
        var host = await GemKit.Communicating(eq, h => h.Hold(7, 1));
        var upload = eq.UploadProcessProgramAsync("R1");
        host.Held.Take(new CancellationTokenSource(5000).Token);
        host.Dispose();
        Assert.Equal(GemUploadFailure.NotCommunicating, (await upload).Failure);
        Assert.Empty(GemKit.Only(log, "SendFailed"));
    }
}
