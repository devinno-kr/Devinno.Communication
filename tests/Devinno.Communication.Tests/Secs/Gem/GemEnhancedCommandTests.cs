using System.Collections.Concurrent;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 장비의 S2F49 확장 리모트 명령(Plan 17 Task 2): S2F41과 같은 명령 표, OBJSPEC, 리스트 CEPVAL·CEPACK
public class GemEnhancedCommandTests
{
    static GemEquipment Eq(Action<GemEquipment>? more = null) => GemKit.Equipment(e =>
    {
        GemKit.Sample(e);
        e.InitialControlState = GemControlState.OnlineRemote;
        more?.Invoke(e);
    });

    const string StartL1 = "S2F49 W <L <U4 1> <A \"CH1\"> <A \"START\"> <L <L <A \"LOTID\"> <A \"L1\">>>>";

    [Fact]
    public async Task S2F49_Accepted_PassesObjectSpec()
    {
        using var eq = Eq();
        var got = new ConcurrentQueue<string>();
        eq.RemoteCommandReceived += (s, e) => got.Enqueue($"{e.ObjectSpec} {e.IsEnhanced} {e.Command} {e.Parameters[0].Name}={e.Parameters[0].Value.GetString()}");
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F50 <L [2] <B 0x00> <L [0]>>", await host.Ask(StartL1));
        Assert.Equal(new[] { "CH1 True START LOTID=L1" }, got);
    }

    [Fact]
    public async Task S2F41_StillS2F42_ObjectSpecNull()
    {
        using var eq = Eq();
        var got = new ConcurrentQueue<string>();
        eq.RemoteCommandReceived += (s, e) => got.Enqueue($"{e.ObjectSpec ?? "null"} {e.IsEnhanced}");
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F42 <L [2] <B 0x00> <L [0]>>", await host.Ask("S2F41 W <L <A \"START\"> <L <L <A \"LOTID\"> <A \"L1\">>>>"));
        Assert.Equal(new[] { "null False" }, got);
    }

    [Fact]
    public async Task S2F49_UnknownCommand_Local_NoSubscriber()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F50 <L [2] <B 0x02> <L [0]>>", await host.Ask(StartL1));                            // 구독자 없음
        eq.RemoteCommandReceived += (s, e) => { };
        Assert.Equal("S2F50 <L [2] <B 0x01> <L [0]>>", await host.Ask("S2F49 W <L <U4 1> <A \"\"> <A \"NOPE\"> <L>>"));
        eq.SwitchLocal();
        await TestUtil.Until(() => eq.ControlState == GemControlState.OnlineLocal, 3000);
        Assert.Equal("S2F50 <L [2] <B 0x02> <L [0]>>", await host.Ask(StartL1));
    }

    [Fact]
    public async Task S2F49_UnknownParam_Cepack1_FormatMismatch_Cepack3()
    {
        using var eq = Eq();
        eq.RemoteCommandReceived += (s, e) => { };
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F50 <L [2] <B 0x03> <L [1] <L [2] <A \"NOPE\"> <U1 1>>>>", await host.Ask("S2F49 W <L <U4 1> <A \"\"> <A \"START\"> <L <L <A \"NOPE\"> <A \"x\">>>>"));
        Assert.Equal("S2F50 <L [2] <B 0x03> <L [1] <L [2] <A \"LOTID\"> <U1 3>>>>", await host.Ask("S2F49 W <L <U4 1> <A \"\"> <A \"START\"> <L <L <A \"LOTID\"> <U4 1>>>>"));
    }

    [Fact]
    public async Task S2F49_ListCepval_AllowedWhenFormatNull()
    {
        using var eq = Eq(e =>
        {
            e.AddRemoteCommand(new GemRemoteCommand("SETUP", [new GemCommandParameter("STEPS")]));
            e.AddRemoteCommand(new GemRemoteCommand("TABLE", [new GemCommandParameter("ROWS", SecsFormat.List)]));
        });
        var got = new ConcurrentQueue<SecsItem>();
        eq.RemoteCommandReceived += (s, e) => got.Enqueue(e.Parameters[0].Value);
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F50 <L [2] <B 0x00> <L [0]>>", await host.Ask("S2F49 W <L <U4 1> <A \"\"> <A \"SETUP\"> <L <L <A \"STEPS\"> <L <U4 1> <U4 2>>>>>"));
        Assert.Equal(SecsItem.L(SecsItem.U4(1), SecsItem.U4(2)), got.Single());
        Assert.Equal("S2F50 <L [2] <B 0x03> <L [1] <L [2] <A \"ROWS\"> <U1 3>>>>", await host.Ask("S2F49 W <L <U4 1> <A \"\"> <A \"TABLE\"> <L <L <A \"ROWS\"> <A \"x\">>>>"));
    }

    [Fact]
    public async Task S2F49_ListCepack_EchoedAsGiven()
    {
        using var eq = Eq(e => e.AddRemoteCommand(new GemRemoteCommand("SETUP", [new GemCommandParameter("STEPS"), new GemCommandParameter("LOT")])));
        eq.RemoteCommandReceived += (s, e) =>
        {
            e.SetParameterAck("STEPS", SecsItem.L(SecsItem.U1(0), SecsItem.U1(2)));
            e.SetParameterAck("LOT", 4);
        };
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F50 <L [2] <B 0x03> <L [2] <L [2] <A \"STEPS\"> <L [2] <U1 0> <U1 2>>> <L [2] <A \"LOT\"> <U1 4>>>>",
            await host.Ask("S2F49 W <L <U4 1> <A \"\"> <A \"SETUP\"> <L <L <A \"STEPS\"> <L <U4 1> <U4 2>>> <L <A \"LOT\"> <A \"x\">>>>"));
    }

    [Fact]
    public async Task S2F41_ListCepack_Throws_Hcack2()
    {
        using var eq = Eq();
        var log = GemKit.Log(eq);
        eq.RemoteCommandReceived += (s, e) => e.SetParameterAck("X", SecsItem.L());
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F42 <L [2] <B 0x02> <L [0]>>", await host.Ask("S2F41 W <L <A \"STOP\"> <L>>"));
        await TestUtil.Until(() => GemKit.Only(log, "Error").Length == 1, 3000);
    }

    [Fact]
    public async Task S2F49_PpSelect_SameAsS2F41()
    {
        var store = new InMemoryProcessProgramStore();
        store.Put(new GemProcessProgram("R1", SecsItem.B(1)));
        using var eq = Eq(e => e.ProcessProgramStore = store);
        eq.RemoteCommandReceived += (s, e) => e.Hcack = GemAck.HcackAccepted;
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F50 <L [2] <B 0x00> <L [0]>>", await host.Ask("S2F49 W <L <U4 1> <A \"\"> <A \"PP-SELECT\"> <L <L <A \"PPID\"> <A \"R1\">>>>"));
        await TestUtil.Until(() => eq.SelectedProcessProgram == "R1", 3000);
        Assert.Equal("S2F50 <L [2] <B 0x03> <L [1] <L [2] <A \"PPID\"> <U1 2>>>>", await host.Ask("S2F49 W <L <U4 1> <A \"\"> <A \"PP-SELECT\"> <L <L <A \"PPID\"> <A \"NOPE\">>>>"));
    }

    [Fact]
    public async Task S2F49_Registered_AppHandles()
    {
        using var eq = Eq(e => e.RegisterMessage(2, 49));
        eq.MessageReceived += (s, e) => e.Reply(new SecsMessage(2, 50, false, SecsItem.L(SecsItem.B(9), SecsItem.L())));
        int commands = 0;
        eq.RemoteCommandReceived += (s, e) => Interlocked.Increment(ref commands);
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F50 <L [2] <B 0x09> <L [0]>>", await host.Ask(StartL1));
        Assert.Equal(0, commands);
        Assert.Throws<ArgumentException>(() => Eq(e => e.RegisterMessage(2, 41)));
    }

    [Fact]
    public async Task S2F49_NoWBit_HandlerCalled_NoReply()
    {
        using var eq = Eq();
        var called = new ManualResetEventSlim(false);
        eq.RemoteCommandReceived += (s, e) => called.Set();
        using var host = await GemKit.Communicating(eq);
        await host.SendAsync("S2F49 <L <U4 1> <A \"\"> <A \"STOP\"> <L>>");
        Assert.True(called.Wait(3000));
    }

    [Fact]
    public async Task S2F49_DeadlineReply_Hcack2()
    {
        using var eq = Eq(e => e.CommandReplyTimeout = 300);
        eq.RemoteCommandReceived += (s, e) => Thread.Sleep(1000);
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F50 <L [2] <B 0x02> <L [0]>>", await host.Ask(StartL1));
    }
}
