using System.Collections.Concurrent;
using Devinno.Communication.Secs;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 리밋 정의 받기(S2F45)·조회(S2F47): 코드 표, 검사 순서, 원자성, 해제 3단계, A로 온 DB, 등록 시 능력 끔, 영속
public class GemLimitSetupTests
{
    // SV 2001 F4 "C"(리밋 3, 0~100, CE 7001), 2002 U4(리밋 2, CE 7002), 2003 A(리밋 없음), DV 3001
    internal static void Define(GemEquipment e)
    {
        e.AddStatusVariable(new GemStatusVariable(2001, "Temperature", SecsFormat.F4, "C", null, 3, SecsItem.F4(0), SecsItem.F4(100), (GemId)7001));
        e.AddStatusVariable(new GemStatusVariable(2002, "Count", SecsFormat.U4, "", null, 2, limitCeid: (GemId)7002));
        e.AddStatusVariable(new GemStatusVariable(2003, "Name", SecsFormat.ASCII));
        e.AddDataVariable(new GemDataVariable(3001, "Lot", SecsFormat.ASCII));
    }

    static GemEquipment Eq(Action<GemEquipment>? more = null) => GemKit.Equipment(e =>
    {
        Define(e);
        e.InitialControlState = GemControlState.OnlineRemote;
        more?.Invoke(e);
    });

    static Task<string> S2F45(FakeGemHost host, string vids) => host.Ask($"S2F45 W <L <U4 1> <L {vids}>>");
    const string Ok = "S2F46 <L [2] <B 0x00> <L [0]>>";
    static string Err(string entries, int n = 1) => $"S2F46 <L [2] <B 0x01> <L [{n}] {entries}>>";
    static string Lim(int id, string upper, string lower) => $"<L <B {id}> <L {upper} {lower}>>";

    static (byte Id, SecsItem Upper, SecsItem Lower, GemLimitZone Zone)[] Limits(GemEquipment eq, GemId vid)
        => [.. eq.GetLimits(vid).Select(l => (l.Limit.LimitId, l.Limit.Upper, l.Limit.Lower, l.Zone))];

    [Fact]
    public async Task Define_Ok_GetLimitsAndS2F48()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal(Ok, await S2F45(host, $"<L <U4 2001> <L {Lim(1, "<F4 30>", "<F4 20>")} {Lim(2, "<F4 80>", "<F4 70>")}>>"));
        Assert.Equal([(1, SecsItem.F4(30), SecsItem.F4(20), GemLimitZone.NoZone), (2, SecsItem.F4(80), SecsItem.F4(70), GemLimitZone.NoZone)], Limits(eq, 2001));
        Assert.Equal("S2F48 <L [1] <L [2] <U4 2001> <L [4] <A \"C\"> <F4 0> <F4 100> <L [2] <L [3] <B 0x01> <F4 30> <F4 20>> <L [3] <B 0x02> <F4 80> <F4 70>>>>>>",
            await host.Ask("S2F47 W <L <U4 2001>>"));
        Assert.Empty(eq.GetLimits(2002));
        Assert.Empty(eq.GetLimits(2003));
        Assert.Throws<ArgumentException>(() => eq.GetLimits(9999));
    }

    [Fact]
    public async Task S2F47_Empty_AllLimitable()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F48 <L [2] <L [2] <U4 2001> <L [4] <A \"C\"> <F4 0> <F4 100> <L [0]>>> <L [2] <U4 2002> <L [4] <A \"\"> <U4 0> <U4 4294967295> <L [0]>>>>",
            await host.Ask("S2F47 W <L>"));
        Assert.Equal("S2F48 <L [2] <L [2] <U4 2003> <L [0]>> <L [2] <U4 9999> <L [0]>>>", await host.Ask("S2F47 W <L <U4 2003> <U4 9999>>"));
    }

    [Theory]
    [InlineData("<L <U4 9999> <L>>", "<L [3] <U4 9999> <B 0x01> <L [0]>>")]
    [InlineData("<L <U4 2003> <L>>", "<L [3] <U4 2003> <B 0x02> <L [0]>>")]
    [InlineData("<L <U4 3001> <L>>", "<L [3] <U4 3001> <B 0x02> <L [0]>>")]
    [InlineData("<L <U4 2001> <L>> <L <U4 2001> <L>>", "<L [3] <U4 2001> <B 0x03> <L [0]>>")]
    [InlineData("<L <U4 2001> <L <L <B 4> <L <F4 30> <F4 20>>>>>", "<L [3] <U4 2001> <B 0x04> <L [2] <B 0x04> <B 0x01>>>")]
    [InlineData("<L <U4 2001> <L <L <B 0> <L>>>>", "<L [3] <U4 2001> <B 0x04> <L [2] <B 0x00> <B 0x01>>>")]
    [InlineData("<L <U4 2001> <L <L <B 1> <L <F4 101> <F4 20>>>>>", "<L [3] <U4 2001> <B 0x04> <L [2] <B 0x01> <B 0x02>>>")]
    [InlineData("<L <U4 2001> <L <L <B 1> <L <F4 30> <F4 -1>>>>>", "<L [3] <U4 2001> <B 0x04> <L [2] <B 0x01> <B 0x03>>>")]
    [InlineData("<L <U4 2001> <L <L <B 1> <L <F4 10> <F4 20>>>>>", "<L [3] <U4 2001> <B 0x04> <L [2] <B 0x01> <B 0x04>>>")]
    [InlineData("<L <U4 2001> <L <L <B 1> <L <BOOLEAN T> <F4 20>>>>>", "<L [3] <U4 2001> <B 0x04> <L [2] <B 0x01> <B 0x05>>>")]
    [InlineData("<L <U4 2001> <L <L <B 1> <L <F4 [2] 1 2> <F4 0>>>>>", "<L [3] <U4 2001> <B 0x04> <L [2] <B 0x01> <B 0x05>>>")]
    [InlineData("<L <U4 2001> <L <L <B 1> <L <A \"x\"> <F4 20>>>>>", "<L [3] <U4 2001> <B 0x04> <L [2] <B 0x01> <B 0x06>>>")]
    [InlineData("<L <U4 2001> <L <L <B 1> <L <F4 30> <F4 20>>> <L <B 1> <L <F4 40> <F4 30>>>>>", "<L [3] <U4 2001> <B 0x04> <L [2] <B 0x01> <B 0x07>>>")]
    [InlineData("<L <U4 2002> <L <L <B 1> <L <F4 1.5> <U4 0>>>>>", "<L [3] <U4 2002> <B 0x04> <L [2] <B 0x01> <B 0x05>>>")]
    [InlineData("<L <U4 2002> <L <L <B 1> <L <A \"1.5\"> <U4 0>>>>>", "<L [3] <U4 2002> <B 0x04> <L [2] <B 0x01> <B 0x06>>>")]
    [InlineData("<L <U4 2002> <L <L <B 1> <L <U4 300> <I4 -1>>>>>", "<L [3] <U4 2002> <B 0x04> <L [2] <B 0x01> <B 0x03>>>")]
    public async Task Codes(string vids, string error)
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal(Err(error), await S2F45(host, vids));
        Assert.Empty(eq.GetLimits(2001));
    }

    [Fact]
    public async Task CheckOrder_FirstErrorPerVid()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal(Err("<L [3] <U4 2001> <B 0x04> <L [2] <B 0x04> <B 0x01>>>"), await S2F45(host, $"<L <U4 2001> <L {Lim(4, "<F4 101>", "<F4 20>")} {Lim(1, "<F4 10>", "<F4 20>")}>>"));
        Assert.Equal(Err("<L [3] <U4 2001> <B 0x04> <L [2] <B 0x01> <B 0x05>>> <L [3] <U4 9999> <B 0x01> <L [0]>>", 2),
            await S2F45(host, $"<L <U4 2001> <L {Lim(1, "<BOOLEAN T>", "<A \"x\">")}>> <L <U4 9999> <L>>"));
    }

    [Fact]
    public async Task AsciiDb_Accepted()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal(Ok, await S2F45(host, $"<L <U4 2001> <L {Lim(1, "<A \"30\">", "<A \"20.5\">")}>> <L <U4 2002> <L {Lim(2, "<I2 9>", "<A \"3\">")}>>"));
        Assert.Equal([(1, SecsItem.F4(30), SecsItem.F4(20.5f), GemLimitZone.NoZone)], Limits(eq, 2001));
        Assert.Equal([(2, SecsItem.U4(9), SecsItem.U4(3), GemLimitZone.NoZone)], Limits(eq, 2002));
    }

    [Fact]
    public async Task AnyError_ChangesNothing()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        eq.SetValue(2001, SecsItem.F4(50));
        Assert.Equal(Ok, await S2F45(host, $"<L <U4 2001> <L {Lim(1, "<F4 30>", "<F4 20>")}>>"));
        var before = Limits(eq, 2001);
        Assert.Equal(GemLimitZone.Above, before[0].Zone);
        Assert.StartsWith("S2F46 <L [2] <B 0x01>", await S2F45(host, $"<L <U4 2001> <L {Lim(1, "<F4 60>", "<F4 55>")} {Lim(2, "<F4 9>", "<F4 1>")}>> <L <U4 2002> <L {Lim(1, "<U4 5>", "<U4 1>")}>> <L <U4 9999> <L>>"));
        Assert.Equal(before, Limits(eq, 2001));
        Assert.Empty(eq.GetLimits(2002));
        Assert.StartsWith("S2F46 <L [2] <B 0x01>", await S2F45(host, "<L <U4 9999> <L>>"));         // m = 0 해제도 오류가 있으면 안 함
        Assert.Equal(before, Limits(eq, 2001));
    }

    [Fact]
    public async Task Release_ThreeLevels()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        string all = $"<L <U4 2001> <L {Lim(1, "<F4 30>", "<F4 20>")} {Lim(2, "<F4 80>", "<F4 70>")}>> <L <U4 2002> <L {Lim(1, "<U4 5>", "<U4 1>")}>>";
        Assert.Equal(Ok, await S2F45(host, all));
        Assert.Equal(Ok, await S2F45(host, "<L <U4 2001> <L <L <B 1> <L>>>>"));          // p = 0
        Assert.Equal([2], eq.GetLimits(2001).Select(l => (int)l.Limit.LimitId));
        Assert.Single(eq.GetLimits(2002));
        Assert.Equal(Ok, await S2F45(host, "<L <U4 2001> <L>>"));                         // n = 0
        Assert.Empty(eq.GetLimits(2001));
        Assert.Single(eq.GetLimits(2002));
        Assert.Equal(Ok, await S2F45(host, all));
        Assert.Equal(Ok, await host.Ask("S2F45 W <L <U4 1> <L>>"));                      // m = 0
        Assert.Empty(eq.GetLimits(2001));
        Assert.Empty(eq.GetLimits(2002));
    }

    [Fact]
    public async Task Redefine_SameLimitId_Replaces_ZoneFromCurrentValue()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        eq.SetValue(2001, SecsItem.F4(50));
        Assert.Equal(Ok, await S2F45(host, $"<L <U4 2001> <L {Lim(1, "<F4 30>", "<F4 20>")}>>"));
        Assert.Equal(GemLimitZone.Above, Limits(eq, 2001)[0].Zone);
        Assert.Equal(Ok, await S2F45(host, $"<L <U4 2001> <L {Lim(1, "<F4 90>", "<F4 60>")}>>"));
        Assert.Equal([(1, SecsItem.F4(90), SecsItem.F4(60), GemLimitZone.Below)], Limits(eq, 2001));
    }

    [Fact]
    public async Task Malformed_S9F7()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        var ex = await Assert.ThrowsAsync<SecsErrorException>(() => host.SendAsync("S2F45 W <L <U4 1> <L <L <U4 2001> <L <L <B 1> <L <F4 1>>>>>>>"));
        Assert.Equal(7, ex.Function);
    }

    [Fact]
    public async Task RegisterOne_CapabilityOff_OtherS9F5()
    {
        using var eq = Eq(e =>
        {
            e.RegisterMessage(2, 47);
            e.MessageReceived += (s, a) => a.Reply(new SecsMessage(2, 48, false, SecsItem.L()));
        });
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F48 <L [0]>", await host.Ask("S2F47 W <L>"));
        var ex = await Assert.ThrowsAsync<SecsErrorException>(() => host.SendAsync("S2F45 W <L <U4 1> <L>>"));
        Assert.Equal(5, ex.Function);
    }

    static string TempDir() => Path.Combine(Path.GetTempPath(), "devinno-limit-" + Guid.NewGuid());

    static void Delete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task Persist_RoundTrip()
    {
        var dir = TempDir();
        try
        {
            using var eq = Eq(e => e.StateDirectory = dir);
            using (var host = await GemKit.Communicating(eq))
            {
                Assert.Equal(Ok, await S2F45(host, $"<L <U4 2001> <L {Lim(1, "<F4 30>", "<F4 20>")} {Lim(3, "<F4 99>", "<F4 1>")}>> <L <U4 2002> <L {Lim(2, "<U4 7>", "<U4 7>")}>>"));
                eq.SetValue(2001, SecsItem.F4(50));
            }
            eq.Stop();
            eq.Start();
            Assert.Equal([(1, SecsItem.F4(30), SecsItem.F4(20), GemLimitZone.NoZone), (3, SecsItem.F4(99), SecsItem.F4(1), GemLimitZone.NoZone)], Limits(eq, 2001));
            Assert.Equal([(2, SecsItem.U4(7), SecsItem.U4(7), GemLimitZone.NoZone)], Limits(eq, 2002));
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task Persist_DropsMismatch()
    {
        var dir = TempDir();
        try
        {
            using (var a = Eq(e => e.StateDirectory = dir))
            {
                using (var host = await GemKit.Communicating(a))
                    Assert.Equal(Ok, await S2F45(host, $"<L <U4 2002> <L {Lim(1, "<U4 5>", "<U4 1>")} {Lim(2, "<U4 9>", "<U4 8>")}>>"));
                a.Stop();
            }
            using var b = GemKit.Equipment(e =>
            {
                e.AddStatusVariable(new GemStatusVariable(2001, "Temperature", SecsFormat.F4, "C", null, 3, SecsItem.F4(0), SecsItem.F4(100), (GemId)7001));
                e.AddStatusVariable(new GemStatusVariable(2002, "Count", SecsFormat.U4, "", null, 1, limitCeid: (GemId)7002));
                e.StateDirectory = dir;
            });
            var log = GemKit.Log(b);
            b.Start();
            Assert.Equal([(1, SecsItem.U4(5), SecsItem.U4(1), GemLimitZone.NoZone)], Limits(b, 2002));
            await TestUtil.Until(() => GemKit.Only(log, "Error").Length > 0, 3000);
            Assert.Contains("버렸습니다", Assert.Single(GemKit.Only(log, "Error")));
        }
        finally { Delete(dir); }
    }
}
