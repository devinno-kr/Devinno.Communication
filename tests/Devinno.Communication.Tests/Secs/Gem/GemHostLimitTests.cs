using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// GemHost 리밋: DefineLimits(S2F45)·ReadLimits(S2F47) 루프백, 오류, 해제, Async
public class GemHostLimitTests
{
    static async Task<(GemEquipment Eq, GemHost Host)> Loopback()
    {
        var eq = GemKit.Equipment(e =>
        {
            GemLimitSetupTests.Define(e);
            e.InitialControlState = GemControlState.OnlineRemote;
        });
        eq.Start();
        var host = GemHostKit.Host(GemKit.Port(eq));
        host.Start();
        await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.Communicating && host.IsCommunicating, 5000);
        return (eq, host);
    }

    [Fact]
    public async Task Loopback_DefineAndRead()
    {
        var (eq, host) = await Loopback();
        using (eq)
        using (host)
        {
            var (vlaack, errors) = host.DefineLimits([((GemId)2001, [(1, SecsItem.F4(30), SecsItem.F4(20))])]);
            Assert.Equal(GemAck.VlaackOk, vlaack);
            Assert.Empty(errors);
            var info = Assert.Single(host.ReadLimits(2001));
            Assert.True(info.IsSupported);
            Assert.Equal("C", info.Units);
            var l = Assert.Single(info.Limits);
            Assert.Equal((1, SecsItem.F4(30), SecsItem.F4(20)), (l.LimitId, l.Upper, l.Lower));
            Assert.Single(eq.GetLimits(2001));
        }
    }

    [Fact]
    public async Task Loopback_Errors()
    {
        var (eq, host) = await Loopback();
        using (eq)
        using (host)
        {
            var (vlaack, errors) = host.DefineLimits([((GemId)9999, [(1, SecsItem.F4(1), SecsItem.F4(0))])]);
            Assert.Equal(GemAck.VlaackLimitError, vlaack);
            var e = Assert.Single(errors);
            Assert.Equal(((GemId)9999, GemAck.LvackUnknownVid, (byte?)null, (byte?)null), (e.Vid, e.Lvack, e.LimitId, e.Limitack));
        }
    }

    [Fact]
    public async Task ReadLimits_All()
    {
        var (eq, host) = await Loopback();
        using (eq)
        using (host)
        {
            var all = host.ReadLimits();
            Assert.Equal([(GemId)2001, (GemId)2002], all.Select(i => i.Vid));
            Assert.All(all, i => Assert.True(i.IsSupported));
            Assert.False(Assert.Single(host.ReadLimits(2003)).IsSupported);
        }
    }

    [Fact]
    public async Task Release_NullLimits()
    {
        var (eq, host) = await Loopback();
        using (eq)
        using (host)
        {
            host.DefineLimits([((GemId)2001, [(1, SecsItem.F4(30), SecsItem.F4(20)), (2, SecsItem.F4(80), SecsItem.F4(70))])]);
            host.DefineLimits([((GemId)2001, [(2, null, null)])]);
            Assert.Equal([1], eq.GetLimits(2001).Select(x => (int)x.Limit.LimitId));
            Assert.Equal(GemAck.VlaackOk, host.DefineLimits([((GemId)2001, null)]).Vlaack);
            Assert.Empty(eq.GetLimits(2001));
            host.DefineLimits([((GemId)2002, [(1, SecsItem.U4(5), SecsItem.U4(1))])]);
            Assert.Equal(GemAck.VlaackOk, host.DefineLimits([]).Vlaack);
            Assert.Empty(eq.GetLimits(2002));
        }
    }

    [Fact]
    public async Task Async_Same()
    {
        var (eq, host) = await Loopback();
        using (eq)
        using (host)
        {
            var (vlaack, errors) = await host.DefineLimitsAsync([((GemId)2001, [(1, SecsItem.F4(30), SecsItem.F4(20))])]);
            Assert.Equal(GemAck.VlaackOk, vlaack);
            Assert.Empty(errors);
            Assert.Single(Assert.Single(await host.ReadLimitsAsync([2001])).Limits);
            Assert.Equal(2, (await host.ReadLimitsAsync()).Count);
        }
    }
}
