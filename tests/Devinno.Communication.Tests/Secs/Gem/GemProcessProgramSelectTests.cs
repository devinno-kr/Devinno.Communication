using System.Collections.Concurrent;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Secs.Gem;

/// PP-SELECT(Plan 16 Task 6): 자동 정의, 앱 정의 우선, 존재 검사, 승인 뒤 선택 반영, LOCAL 허용
public class GemProcessProgramSelectTests
{
    const string Select = "S2F41 W <L <A \"PP-SELECT\"> <L <L <A \"PPID\"> <A \"{0}\">>>>";

    static GemEquipment Eq(InMemoryProcessProgramStore? store, Action<GemEquipment>? more = null) => GemKit.Equipment(e =>
    {
        GemKit.Sample(e);
        e.InitialControlState = GemControlState.OnlineRemote;
        e.EventsEnabledByDefault = true;
        e.ProcessProgramStore = store;
        more?.Invoke(e);
    });

    static InMemoryProcessProgramStore StoreWithR1()
    {
        var s = new InMemoryProcessProgramStore();
        s.Put(new GemProcessProgram("R1", SecsItem.B(1)));
        return s;
    }

    [Fact]
    public async Task AutoDefined_WhenEnabled()
    {
        using (var eq = Eq(StoreWithR1()))
        {
            eq.RemoteCommandReceived += (s, e) => { };
            using var host = await GemKit.Communicating(eq);
            Assert.Equal("S2F42 <L [2] <B 0x00> <L [0]>>", await host.Ask(string.Format(Select, "R1")));
        }
        using (var off = Eq(null))
        {
            off.RemoteCommandReceived += (s, e) => { };
            using var host = await GemKit.Communicating(off);
            Assert.Equal("S2F42 <L [2] <B 0x01> <L [0]>>", await host.Ask(string.Format(Select, "R1")));
        }
    }

    [Fact]
    public async Task AppDefined_IsUsed()
    {
        using var eq = Eq(StoreWithR1(), e => e.AddRemoteCommand(new GemRemoteCommand("PP-SELECT", [new GemCommandParameter("RECIPE", SecsFormat.ASCII)])));
        var got = new ConcurrentQueue<string>();
        eq.RemoteCommandReceived += (s, e) => got.Enqueue(e.Parameters.Single().Value.GetString());
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F42 <L [2] <B 0x03> <L [1] <L [2] <A \"PPID\"> <B 0x01>>>>", await host.Ask(string.Format(Select, "R1")));
        Assert.Equal("S2F42 <L [2] <B 0x00> <L [0]>>", await host.Ask("S2F41 W <L <A \"PP-SELECT\"> <L <L <A \"RECIPE\"> <A \"NOPE\">>>>"));   // PPID 파라미터가 없으므로 존재 검사 없음
        Assert.Equal(new[] { "NOPE" }, got);
        Assert.Null(eq.SelectedProcessProgram);                                                              // 자동 선택 없음
    }

    [Fact]
    public async Task UnknownPpid_Hcack3Cpack2_HandlerNotCalled()
    {
        using var eq = Eq(StoreWithR1());
        int calls = 0;
        eq.RemoteCommandReceived += (s, e) => Interlocked.Increment(ref calls);
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F42 <L [2] <B 0x03> <L [1] <L [2] <A \"PPID\"> <B 0x02>>>>", await host.Ask(string.Format(Select, "NOPE")));
        Assert.Equal("S2F42 <L [2] <B 0x03> <L [1] <L [2] <A \"PPID\"> <B 0x02>>>>", await host.Ask("S2F41 W <L <A \"PP-SELECT\"> <L>>"));   // PPID가 빠짐
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Accepted_SelectsAfterReply()
    {
        using var eq = Eq(StoreWithR1());
        eq.RemoteCommandReceived += (s, e) => e.Hcack = GemAck.HcackAccepted;
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F42 <L [2] <B 0x00> <L [0]>>", await host.Ask(string.Format(Select, "R1")));
        var m = host.Next(6, 11);
        Assert.Equal(1311UL, m.Item!.Items[1].GetUInt64());
        await TestUtil.Until(() => eq.SelectedProcessProgram == "R1", 3000);
    }

    [Fact]
    public async Task AcceptedLater_DoesNotSelect()
    {
        using var eq = Eq(StoreWithR1());
        eq.RemoteCommandReceived += (s, e) => e.Hcack = GemAck.HcackAcceptedLater;
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F42 <L [2] <B 0x04> <L [0]>>", await host.Ask(string.Format(Select, "R1")));
        host.NoneOf(6);
        Assert.Null(eq.SelectedProcessProgram);
    }

    [Fact]
    public async Task Local_PpSelectAllowed_OtherCommandsStillRejected()
    {
        using var eq = Eq(StoreWithR1());
        int calls = 0;
        eq.RemoteCommandReceived += (s, e) => Interlocked.Increment(ref calls);
        using var host = await GemKit.Communicating(eq);
        eq.SwitchLocal();
        await TestUtil.Until(() => eq.ControlState == GemControlState.OnlineLocal, 3000);
        Assert.Equal("S2F42 <L [2] <B 0x00> <L [0]>>", await host.Ask(string.Format(Select, "R1")));
        Assert.Equal(1, calls);
        Assert.Equal("S2F42 <L [2] <B 0x02> <L [0]>>", await host.Ask("S2F41 W <L <A \"START\"> <L>>"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NoSubscriber_Hcack2()
    {
        using var eq = Eq(StoreWithR1());
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F42 <L [2] <B 0x02> <L [0]>>", await host.Ask(string.Format(Select, "R1")));
    }
}
