using System.Collections.Concurrent;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Secs.Gem;

/// Host → 장비 레시피 받기(Plan 16 Task 4): S7F1·F3·F5·F17·F19, 승인 이벤트, 코드 표(6J.5.5), 기한·예외·스레드
public class GemProcessProgramReceiveTests
{
    sealed class Ctx : IDisposable
    {
        public readonly InMemoryProcessProgramStore Store = new();
        public readonly ConcurrentQueue<GemProcessProgramChangeEventArgs> Events = new();
        public GemEquipment Eq = null!;
        public FakeGemHost Host = null!;
        public ConcurrentQueue<string> Log = null!;
        public Action<GemProcessProgramChangeEventArgs>? OnChange;

        public static async Task<Ctx> Start(Action<GemEquipment>? more = null)
        {
            var c = new Ctx();
            c.Eq = GemKit.Equipment(e =>
            {
                GemKit.Sample(e);
                e.InitialControlState = GemControlState.OnlineRemote;
                e.ProcessProgramStore = c.Store;
                more?.Invoke(e);
            });
            c.Log = GemKit.Log(c.Eq);
            c.Eq.ProcessProgramChangeRequested += (s, e) => { c.Events.Enqueue(e); c.OnChange?.Invoke(e); };
            c.Host = await GemKit.Communicating(c.Eq);
            return c;
        }

        public Task<string> Ask(string sml) => Host.Ask(sml);

        public void Dispose()
        {
            Host.Dispose();
            Eq.Dispose();
        }
    }

    static string Bytes(int n) => string.Join(" ", Enumerable.Repeat("0x01", n));

    [Fact]
    public async Task S7F3_WithoutInquire_Accepted()
    {
        using var c = await Ctx.Start();
        Assert.Equal("S7F4 <B 0x00>", await c.Ask("S7F3 W <L <A \"R1\"> <B 0x01 0x02>>"));
        Assert.Equal(SecsItem.B(1, 2), c.Store.Peek("R1")!.Body);
        var e = Assert.Single(c.Events);
        Assert.Equal(GemProcessProgramChangeKind.Download, e.Kind);
        Assert.Equal(new[] { "R1" }, e.Ppids);
        Assert.Equal(SecsItem.B(1, 2), e.Program!.Body);
    }

    [Fact]
    public async Task S7F1_Grant_ThenS7F3()
    {
        using var c = await Ctx.Start();
        Assert.Equal("S7F2 <B 0x00>", await c.Ask("S7F1 W <L <A \"R1\"> <U4 10>>"));
        Assert.Equal("S7F4 <B 0x00>", await c.Ask("S7F3 W <L <A \"R1\"> <B 0x01 0x02>>"));
        var inquire = c.Events.First();
        Assert.Equal(GemProcessProgramChangeKind.Inquire, inquire.Kind);
        Assert.Equal(10UL, inquire.Length);
    }

    [Fact]
    public async Task S7F3_LongerThanGranted_Ackc7LengthError()
    {
        using var c = await Ctx.Start();
        Assert.Equal("S7F2 <B 0x00>", await c.Ask("S7F1 W <L <A \"R1\"> <U4 3>>"));
        Assert.Equal("S7F4 <B 0x02>", await c.Ask($"S7F3 W <L <A \"R1\"> <B {Bytes(8)}>>"));   // 인코딩 길이 10 > 3
        Assert.Null(c.Store.Peek("R1"));
        Assert.Equal("S7F4 <B 0x00>", await c.Ask($"S7F3 W <L <A \"R1\"> <B {Bytes(8)}>>"));   // 허락은 앞의 S7F3에서 지워짐 → 허락 없는 S7F3
    }

    [Fact]
    public async Task S7F1_TooLarge_Ppgnt5()
    {
        using var c = await Ctx.Start(e => e.MaxProcessProgramSize = 10);
        Assert.Equal("S7F2 <B 0x05>", await c.Ask("S7F1 W <L <A \"R1\"> <U4 11>>"));
        Assert.Equal("S7F2 <B 0x00>", await c.Ask("S7F1 W <L <A \"R1\"> <U4 10>>"));
    }

    [Fact]
    public async Task S7F1_BadPpid_Ppgnt3()
    {
        using var c = await Ctx.Start();
        Assert.Equal("S7F2 <B 0x03>", await c.Ask("S7F1 W <L <A \"\"> <U4 1>>"));
        Assert.Equal("S7F2 <B 0x03>", await c.Ask($"S7F1 W <L <A \"{new string('x', 121)}\"> <U4 1>>"));
        Assert.Empty(c.Events);
    }

    [Fact]
    public async Task S7F3_TooLarge_Ackc7LengthError()
    {
        using var c = await Ctx.Start(e => e.MaxProcessProgramSize = 5);
        Assert.Equal("S7F4 <B 0x02>", await c.Ask($"S7F3 W <L <A \"R1\"> <B {Bytes(4)}>>"));   // 인코딩 6
        Assert.Equal("S7F4 <B 0x00>", await c.Ask($"S7F3 W <L <A \"R1\"> <B {Bytes(3)}>>"));   // 인코딩 5
    }

    [Fact]
    public async Task S7F3_BadPpid_Ackc7PermissionNotGranted()
    {
        using var c = await Ctx.Start();
        Assert.Equal("S7F4 <B 0x01>", await c.Ask("S7F3 W <L <A \"\"> <B 0x01>>"));
        Assert.Equal(0, c.Store.Count);
    }

    [Fact]
    public async Task S7F3_Existing_Replaced()
    {
        using var c = await Ctx.Start();
        Assert.Equal("S7F4 <B 0x00>", await c.Ask("S7F3 W <L <A \"R1\"> <B 0x01>>"));
        Assert.Equal("S7F4 <B 0x00>", await c.Ask("S7F3 W <L <A \"R1\"> <A \"v2\">>"));
        Assert.Equal(SecsItem.A("v2"), c.Store.Peek("R1")!.Body);
        Assert.Equal(1, c.Store.Count);
    }

    [Fact]
    public async Task S7F3_SelectedProgram_Rejected()
    {
        using var c = await Ctx.Start();
        c.Store.Put(new GemProcessProgram("R1", SecsItem.B(1)));
        c.Eq.SetSelectedForTest("R1");
        Assert.Equal("S7F4 <B 0x01>", await c.Ask("S7F3 W <L <A \"R1\"> <B 0x09>>"));
        Assert.Equal(SecsItem.B(1), c.Store.Peek("R1")!.Body);
        Assert.Empty(c.Events);
    }

    [Fact]
    public async Task AppRejects_Download()
    {
        using var c = await Ctx.Start();
        c.OnChange = e => e.Ack = GemAck.Ackc7PermissionNotGranted;
        Assert.Equal("S7F4 <B 0x01>", await c.Ask("S7F3 W <L <A \"R1\"> <B 0x01>>"));
        Assert.Null(c.Store.Peek("R1"));
    }

    [Fact]
    public async Task AppRejects_Inquire()
    {
        using var c = await Ctx.Start(e => e.MaxProcessProgramSize = 100);
        c.OnChange = e => { if (e.Kind == GemProcessProgramChangeKind.Inquire) e.Ack = GemAck.PpgntBusy; };
        Assert.Equal("S7F2 <B 0x04>", await c.Ask("S7F1 W <L <A \"R1\"> <U4 3>>"));
        Assert.Equal("S7F4 <B 0x00>", await c.Ask($"S7F3 W <L <A \"R1\"> <B {Bytes(8)}>>"));   // 허락 기록이 없으므로 길이 3에 묶이지 않음
    }

    [Fact]
    public async Task S7F5_Known_Unknown()
    {
        using var c = await Ctx.Start();
        c.Store.Put(new GemProcessProgram("R1", SecsItem.B(1, 2)));
        Assert.Equal("S7F6 <L [2] <A \"R1\"> <B 0x01 0x02>>", await c.Ask("S7F5 W <A \"R1\">"));
        Assert.Equal("S7F6 <L [0]>", await c.Ask("S7F5 W <A \"NOPE\">"));
        Assert.Equal("S7F6 <L [0]>", await c.Ask("S7F5 W <A \"\">"));
        Assert.Empty(c.Events);
    }

    [Fact]
    public async Task S7F17_DeleteOne_DeleteAll()
    {
        using var c = await Ctx.Start();
        c.Store.Put(new GemProcessProgram("R1", SecsItem.B(1)));
        Assert.Equal("S7F18 <B 0x00>", await c.Ask("S7F17 W <L <A \"R1\">>"));
        Assert.Null(c.Store.Peek("R1"));
        c.Store.Put(new GemProcessProgram("R1", SecsItem.B(1)));
        c.Store.Put(new GemProcessProgram("R2", SecsItem.B(2)));
        Assert.Equal("S7F18 <B 0x00>", await c.Ask("S7F17 W <L>"));
        Assert.Equal(0, c.Store.Count);
        Assert.Equal(new[] { "R1", "R2" }, c.Events.Last().Ppids);
        Assert.Equal(GemProcessProgramChangeKind.Delete, c.Events.Last().Kind);
    }

    [Fact]
    public async Task S7F17_UnknownPpid_DeletesNothing()
    {
        using var c = await Ctx.Start();
        c.Store.Put(new GemProcessProgram("R1", SecsItem.B(1)));
        Assert.Equal("S7F18 <B 0x04>", await c.Ask("S7F17 W <L <A \"R1\"> <A \"NOPE\">>"));
        Assert.NotNull(c.Store.Peek("R1"));
        Assert.Empty(c.Events);
    }

    [Fact]
    public async Task S7F17_ContainsSelected_DeletesNothing()
    {
        using var c = await Ctx.Start();
        c.Store.Put(new GemProcessProgram("R1", SecsItem.B(1)));
        c.Store.Put(new GemProcessProgram("R2", SecsItem.B(2)));
        c.Eq.SetSelectedForTest("R1");
        Assert.Equal("S7F18 <B 0x01>", await c.Ask("S7F17 W <L <A \"R1\"> <A \"R2\">>"));
        Assert.Equal(2, c.Store.Count);
        Assert.Equal("S7F18 <B 0x01>", await c.Ask("S7F17 W <L>"));        // 전부 = 선택된 것도 포함
        Assert.Equal(2, c.Store.Count);
    }

    [Fact]
    public async Task S7F19_List()
    {
        using var c = await Ctx.Start();
        c.Store.Put(new GemProcessProgram("R2", SecsItem.B(2)));
        c.Store.Put(new GemProcessProgram("R1", SecsItem.B(1)));
        Assert.Equal("S7F20 <L [2] <A \"R2\"> <A \"R1\">>", await c.Ask("S7F19 W"));   // 저장소 순서 그대로
    }

    [Fact]
    public async Task Offline_S7_Aborted()
    {
        using var c = await Ctx.Start(e => e.InitialControlState = GemControlState.HostOffline);
        await Assert.ThrowsAsync<SecsAbortException>(() => c.Host.SendAsync("S7F19 W"));
    }

    [Fact]
    public async Task Local_S7Accepted()
    {
        using var c = await Ctx.Start();
        c.Eq.SwitchLocal();
        await TestUtil.Until(() => c.Eq.ControlState == GemControlState.OnlineLocal, 3000);
        Assert.Equal("S7F4 <B 0x00>", await c.Ask("S7F3 W <L <A \"R1\"> <B 0x01>>"));
    }

    [Fact]
    public async Task SlowStore_DeadlineReply()
    {
        var release = new ManualResetEventSlim(false);
        using var c = await Ctx.Start(e => e.CommandReplyTimeout = 300);
        c.Store.Hook = call => { if (call.StartsWith("Write", StringComparison.Ordinal)) release.Wait(5000); };
        try
        {
            Assert.Equal("S7F4 <B 0x01>", await c.Ask("S7F3 W <L <A \"R1\"> <B 0x01>>"));     // 저장소가 막혀 있어도 기한 응답이 옴
            release.Set();                       // 알림(ProtocolError)은 막혀 있던 앱 이벤트 스레드로 가므로 풀어 준 뒤에 온다
            await TestUtil.Until(() => GemKit.Only(c.Log, "Error").Any(l => l.Contains("CommandReplyTimeout")), 3000);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task StoreThrows_Ackc7PermissionNotGranted()
    {
        using var c = await Ctx.Start();
        c.Store.Hook = call => { if (call.StartsWith("Write", StringComparison.Ordinal)) throw new IOException("disk"); };
        Assert.Equal("S7F4 <B 0x01>", await c.Ask("S7F3 W <L <A \"R1\"> <B 0x01>>"));
        await TestUtil.Until(() => GemKit.Only(c.Log, "Error").Any(l => l.Contains("레시피 저장소 예외")), 3000);
        Assert.Single(GemKit.Only(c.Log, "Error"), l => l.Contains("레시피 저장소 예외"));
    }

    [Fact]
    public async Task StoreThrows_ReadAndList_EmptyReplies()
    {
        using var c = await Ctx.Start();
        c.Store.Hook = call => throw new IOException("disk");
        Assert.Equal("S7F6 <L [0]>", await c.Ask("S7F5 W <A \"R1\">"));
        Assert.Equal("S7F20 <L [0]>", await c.Ask("S7F19 W"));
    }

    [Fact]
    public async Task HandlerThrows_Rejects()
    {
        using var c = await Ctx.Start();
        c.OnChange = e => throw new InvalidOperationException("app");
        Assert.Equal("S7F4 <B 0x01>", await c.Ask("S7F3 W <L <A \"R1\"> <B 0x01>>"));
        Assert.Null(c.Store.Peek("R1"));
    }

    [Fact]
    public async Task NoWBit_NotApplied()
    {
        using var c = await Ctx.Start();
        await c.Host.SendAsync("S7F3 <L <A \"R1\"> <B 0x01>>");
        await TestUtil.Until(() => GemKit.Only(c.Log, "Error").Any(l => l.Contains("W비트")), 3000);
        Assert.Null(c.Store.Peek("R1"));
    }

    [Fact]
    public async Task StoreCalls_NotOnDispatcher_NotUnderGate()
    {
        using var c = await Ctx.Start();
        var bad = new ConcurrentQueue<string>();
        c.Store.Hook = call => { if (c.Eq.IsGateHeldForTest || c.Eq.OnTransportDispatcherThread) bad.Enqueue(call); };
        await c.Ask("S7F1 W <L <A \"R1\"> <U4 3>>");
        await c.Ask("S7F3 W <L <A \"R1\"> <B 0x01>>");
        await c.Ask("S7F5 W <A \"R1\">");
        await c.Ask("S7F19 W");
        await c.Ask("S7F17 W <L <A \"R1\">>");
        Assert.True(c.Store.Calls.Count >= 4);
        Assert.Empty(bad);
    }
}
