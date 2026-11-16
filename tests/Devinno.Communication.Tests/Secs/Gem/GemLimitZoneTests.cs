using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 리밋 구역 판정과 Limit Zone Transition CE: 넘어감·데드밴드·경계·정의 직후·여러 LIMITID·배열 값·OFF-LINE·CE 끔·복원·스풀
public class GemLimitZoneTests
{
    static GemEquipment Eq(Action<GemEquipment>? more = null) => GemKit.Equipment(e =>
    {
        GemLimitSetupTests.Define(e);
        e.InitialControlState = GemControlState.OnlineRemote;
        e.EventsEnabledByDefault = true;
        more?.Invoke(e);
    });

    // CE 7001에 리포트 8001 [LimitVariable, EventLimit, TransitionType]
    static async Task<FakeGemHost> Host(GemEquipment eq)
    {
        var host = await GemKit.Communicating(eq);
        await Link(host);
        return host;
    }

    static async Task Link(FakeGemHost host)
    {
        Assert.Equal("S2F34 <B 0x00>", await host.Ask("S2F33 W <L <U4 0> <L <L <U4 8001> <L <U4 1204> <U4 1205> <U4 1206>>>>>"));
        Assert.Equal("S2F36 <B 0x00>", await host.Ask("S2F35 W <L <U4 0> <L <L <U4 7001> <L <U4 8001>>>>>"));
        Assert.Equal("S2F38 <B 0x00>", await host.Ask("S2F37 W <L <BOOLEAN F> <L>>"));                   // 표준 CE(OFF-LINE 등)는 끄고 7001만
        Assert.Equal("S2F38 <B 0x00>", await host.Ask("S2F37 W <L <BOOLEAN T> <L <U4 7001>>>"));
    }

    static async Task Limits(FakeGemHost host, string limits)
        => Assert.Equal("S2F46 <L [2] <B 0x00> <L [0]>>", await host.Ask($"S2F45 W <L <U4 1> <L <L <U4 2001> <L {limits}>>>>"));

    static string Lim(int id, float upper, float lower) => $"<L <B {id}> <L <F4 {upper}> <F4 {lower}>>>";

    static void Set(GemEquipment eq, float v) => eq.SetValue(2001, SecsItem.F4(v));

    // 받은 S6F11(CE 7001)의 리포트 값: LimitVariable, EventLimit, TransitionType
    static (SecsItem Vid, SecsItem Limits, SecsItem Type) Event(FakeGemHost host)
    {
        var m = host.NextOf(6, 11);
        Assert.Equal(7001UL, m.Item!.Items[1].GetUInt64());
        var vs = m.Item.Items[2].Items[0].Items[1].Items;
        return (vs[0], vs[1], vs[2]);
    }

    static SecsItem Ids(params byte[] ids) => SecsItem.L([.. ids.Select(i => SecsItem.B(i))]);

    static GemLimitZone Zone(GemEquipment eq, int index = 0) => eq.GetLimits(2001)[index].Zone;

    [Fact]
    public async Task CrossUp_OneEvent()
    {
        using var eq = Eq();
        using var host = await Host(eq);
        await Limits(host, Lim(1, 30, 20));
        Set(eq, 10);                                               // NO ZONE → BELOW: CE 없음
        host.NoneOf(6, 300);
        Assert.Equal(GemLimitZone.Below, Zone(eq));
        Set(eq, 35);
        Assert.Equal((SecsItem.U4(2001), Ids(1), SecsItem.B(0)), Event(host));
        Assert.Equal(GemLimitZone.Above, Zone(eq));
    }

    [Fact]
    public async Task CrossDown_OneEvent()
    {
        using var eq = Eq();
        using var host = await Host(eq);
        await Limits(host, Lim(1, 30, 20));
        Set(eq, 35);
        host.NoneOf(6, 300);
        Set(eq, 15);
        Assert.Equal((SecsItem.U4(2001), Ids(1), SecsItem.B(1)), Event(host));
    }

    [Fact]
    public async Task Deadband_NoEvent_CrossingOnce()
    {
        using var eq = Eq();
        using var host = await Host(eq);
        await Limits(host, Lim(1, 30, 20));
        foreach (var v in new float[] { 10, 25, 12, 25, 29.9f }) Set(eq, v);
        host.NoneOf(6, 300);
        Set(eq, 31);
        Assert.Equal(SecsItem.B(0), Event(host).Type);
        host.NoneOf(6, 300);
    }

    [Fact]
    public async Task Boundaries_Inclusive()
    {
        using var eq = Eq();
        using var host = await Host(eq);
        await Limits(host, Lim(1, 30, 20));
        Set(eq, 30);
        Assert.Equal(GemLimitZone.Above, Zone(eq));
        Set(eq, 20);
        Assert.Equal(GemLimitZone.Below, Zone(eq));
        Assert.Equal(SecsItem.B(1), Event(host).Type);
    }

    [Fact]
    public async Task DefineWhileAbove_NoEvent()
    {
        using var eq = Eq();
        using var host = await Host(eq);
        Set(eq, 50);
        await Limits(host, Lim(1, 30, 20));
        Assert.Equal(GemLimitZone.Above, Zone(eq));
        Set(eq, 60);
        host.NoneOf(6, 300);
    }

    [Fact]
    public async Task MultipleLimits_OneEventPerDirection()
    {
        using var eq = Eq();
        using var host = await Host(eq);
        await Limits(host, $"{Lim(1, 30, 20)} {Lim(2, 60, 50)} {Lim(3, 90, 80)}");
        Set(eq, 10);
        Set(eq, 95);
        var up = Event(host);
        Assert.Equal((Ids(1, 2, 3), SecsItem.B(0)), (up.Limits, up.Type));
        host.NoneOf(6, 200);
        Set(eq, 85);                                               // 3의 데드밴드
        host.NoneOf(6, 300);
        Set(eq, 55);                                               // 3만 아래로
        Assert.Equal((Ids(3), SecsItem.B(1)), (Event(host).Limits, SecsItem.B(1)));
        Set(eq, 5);
        var down = Event(host);
        Assert.Equal((Ids(1, 2), SecsItem.B(1)), (down.Limits, down.Type));
    }

    [Fact]
    public async Task NonSingleValue_NoEvaluation()
    {
        using var eq = Eq();
        using var host = await Host(eq);
        await Limits(host, Lim(1, 30, 20));
        Set(eq, 10);
        eq.SetValue(2001, SecsItem.F4([50f, 60f]));
        eq.SetValue(2001, SecsItem.Raw(SecsFormat.F4, Array.Empty<float>()));
        Assert.Equal(GemLimitZone.Below, Zone(eq));
        host.NoneOf(6, 300);
    }

    [Fact]
    public async Task Offline_ZoneUpdated_NoEvent()
    {
        using var eq = Eq();
        using var host = await Host(eq);
        await Limits(host, Lim(1, 30, 20));
        Set(eq, 10);
        Assert.True(eq.GoOffline());
        Set(eq, 35);
        Assert.Equal(GemLimitZone.Above, Zone(eq));
        Assert.True(eq.GoOnline());
        await TestUtil.Until(() => eq.ControlState is GemControlState.OnlineRemote or GemControlState.OnlineLocal, 3000);
        Set(eq, 40);
        host.NoneOf(6, 300);
        Set(eq, 15);
        Assert.Equal(SecsItem.B(1), Event(host).Type);
    }

    [Fact]
    public async Task Disabled_NotSent_ZoneUpdated()
    {
        using var eq = Eq();
        using var host = await Host(eq);
        await Limits(host, Lim(1, 30, 20));
        Assert.Equal("S2F38 <B 0x00>", await host.Ask("S2F37 W <L <BOOLEAN F> <L <U4 7001>>>"));
        Set(eq, 10);
        Set(eq, 35);
        host.NoneOf(6, 300);
        Assert.Equal(GemLimitZone.Above, Zone(eq));
    }

    [Fact]
    public async Task Restored_StartsNoZone_FirstSetValueNoEvent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "devinno-limit-" + Guid.NewGuid());
        try
        {
            using var eq = Eq(e => e.StateDirectory = dir);
            using (var host = await Host(eq))
            {
                await Limits(host, Lim(1, 30, 20));
                Set(eq, 10);
            }
            eq.Stop();
            eq.Start();
            using var host2 = await GemKit.Communicating(eq);         // 리포트·링크도 영속(Plan 15)
            Assert.Equal(GemLimitZone.NoZone, Zone(eq));
            Set(eq, 50);
            host2.NoneOf(6, 300);
            Assert.Equal(GemLimitZone.Above, Zone(eq));
            Set(eq, 10);
            Assert.Equal(SecsItem.B(1), Event(host2).Type);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Spooled_WhenLinkDown()
    {
        var dir = Path.Combine(Path.GetTempPath(), "devinno-limit-" + Guid.NewGuid());
        try
        {
            using var eq = Eq(e => e.SpoolDirectory = dir);
            var host = await Host(eq);
            Assert.Equal("S2F44 <L [2] <B 0x00> <L [0]>>", await host.Ask("S2F43 W <L <L <U1 6> <L <U1 11>>>>"));
            await Limits(host, Lim(1, 30, 20));
            Set(eq, 10);
            host.Dispose();
            await TestUtil.Until(() => eq.SpoolState == GemSpoolState.NoOutput, 5000);
            int before = eq.SpoolCount;
            Set(eq, 35);
            await TestUtil.Until(() => eq.SpoolCount == before + 1, 3000);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
