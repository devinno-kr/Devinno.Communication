using System.Xml.Linq;
using System.Xml.Schema;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Secs;
using Devinno.Communication.Secs.Hsms;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 트레이스 정의 받기(S2F23): TIAACK 1~5, 검사 순서, 같은 TRID, 멈춤, 한도·설정·XML, 등록 메시지 우선
public class GemTraceSetupTests
{
    static GemEquipment Eq(Action<GemEquipment>? more = null) => GemKit.Equipment(e =>
    {
        GemKit.Sample(e);
        e.InitialControlState = GemControlState.OnlineRemote;
        more?.Invoke(e);
    });

    static string S2F23(string trid, string period, string total, string group, string svids)
        => $"S2F23 W <L {trid} <A \"{period}\"> {total} {group} <L {svids}>>";

    static string Std(string trid = "<U4 1>", string period = "000001", int total = 6, int group = 2, string svids = "<U4 2001> <U4 2002>")
        => S2F23(trid, period, $"<U4 {total}>", $"<U4 {group}>", svids);

    const string Ok = "S2F24 <B 0x00>";
    static string Tiaack(int code) => $"S2F24 <B 0x0{code}>";

    [Fact]
    public async Task Accept_Tiaack0_ListedInActiveTraces()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal(Ok, await host.Ask(Std()));
        var t = Assert.Single(eq.ActiveTraces);
        Assert.Equal((GemId)1, t.Trid);
        Assert.Equal(TimeSpan.FromSeconds(1), t.Period);
        Assert.Equal([(GemId)2001, (GemId)2002], t.Svids);
        Assert.Equal(6UL, t.TotalSamples);
        Assert.Equal(2UL, t.GroupSize);
        Assert.Equal(6UL, t.RemainingSamples);
    }

    [Fact]
    public async Task EightDigitPeriod_Accepted()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal(Ok, await host.Ask(Std(period: "00000150")));
        Assert.Equal(TimeSpan.FromMilliseconds(1500), Assert.Single(eq.ActiveTraces).Period);
    }

    [Fact]
    public async Task TooManySvids_1()
    {
        using var eq = Eq(e => e.MaxTraceVariables = 1);
        using var host = await GemKit.Communicating(eq);
        Assert.Equal(Tiaack(1), await host.Ask(Std()));
        Assert.Empty(eq.ActiveTraces);
    }

    [Fact]
    public async Task NoMoreTraces_2()
    {
        using var eq = Eq(e => e.MaxTraces = 1);
        using var host = await GemKit.Communicating(eq);
        Assert.Equal(Ok, await host.Ask(Std()));
        Assert.Equal(Tiaack(2), await host.Ask(Std(trid: "<U4 2>")));
        Assert.Equal(Ok, await host.Ask(Std(svids: "<U4 2001>")));          // 같은 TRID는 자리를 늘리지 않음
        Assert.Single(eq.ActiveTraces);
    }

    [Theory]
    [InlineData("00000000", 100)]
    [InlineData("000060", 100)]
    [InlineData("12345", 100)]
    [InlineData("00000010", 200)]
    public async Task InvalidPeriod_3(string period, int minPeriod)
    {
        using var eq = Eq(e => e.MinTracePeriod = minPeriod);
        using var host = await GemKit.Communicating(eq);
        Assert.Equal(Tiaack(3), await host.Ask(Std(period: period)));
    }

    [Theory]
    [InlineData("<U4 9999>")]
    [InlineData("<U4 3001>")]
    [InlineData("<U4 4001>")]
    [InlineData("")]
    [InlineData("<A \"2001\">")]
    public async Task UnknownSvid_4(string svids)
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal(Tiaack(4), await host.Ask(Std(svids: svids)));
    }

    [Theory]
    [InlineData(6, 0)]
    [InlineData(6, 7)]
    [InlineData(5000, 1001)]
    public async Task InvalidGroupSize_5(int total, int group)
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal(Tiaack(5), await host.Ask(Std(total: total, group: group)));
    }

    [Fact]
    public async Task CodeOrder_LowestFirst()
    {
        using var eq = Eq(e => e.MaxTraceVariables = 1);
        using var host = await GemKit.Communicating(eq);
        Assert.Equal(Tiaack(1), await host.Ask(Std(period: "000060", group: 0)));
        Assert.Equal(Tiaack(3), await host.Ask(Std(period: "000060", group: 0, svids: "<U4 9999>")));
    }

    [Fact]
    public async Task TotalZero_StopsAndRemoves()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal(Ok, await host.Ask(Std()));
        Assert.Equal(Ok, await host.Ask(Std(total: 0, group: 0, period: "000000", svids: "")));
        Assert.Empty(eq.ActiveTraces);
        Assert.Equal(Ok, await host.Ask(Std(trid: "<U4 9>", total: 0, group: 0, svids: "")));
    }

    [Fact]
    public async Task SameTrid_Replaces_KeepsPosition()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal(Ok, await host.Ask(Std()));
        Assert.Equal(Ok, await host.Ask(Std(trid: "<U4 2>")));
        Assert.Equal(Ok, await host.Ask(Std(svids: "<U4 2002>")));
        var ts = eq.ActiveTraces;
        Assert.Equal([(GemId)1, (GemId)2], ts.Select(t => t.Trid));
        Assert.Equal([(GemId)2002], ts[0].Svids);
    }

    [Fact]
    public async Task SameTrid_IntegerFormatsEqual()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal(Ok, await host.Ask(Std(trid: "<U1 1>")));
        Assert.Equal(Ok, await host.Ask(Std(trid: "<U4 1>")));
        Assert.Single(eq.ActiveTraces);
        Assert.Equal(Ok, await host.Ask(Std(trid: "<A \"1\">")));
        Assert.Equal(2, eq.ActiveTraces.Count);
    }

    [Fact]
    public async Task SameTrid_InvalidRedefinition_KeepsOld()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal(Ok, await host.Ask(Std()));
        Assert.Equal(Tiaack(4), await host.Ask(Std(svids: "<U4 9999>")));
        var t = Assert.Single(eq.ActiveTraces);
        Assert.Equal([(GemId)2001, (GemId)2002], t.Svids);
    }

    [Fact]
    public async Task Malformed_S9F7()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        var ex = await Assert.ThrowsAsync<SecsErrorException>(() => host.SendAsync(S2F23("<U4 1>", "000001", "<F4 6>", "<U4 2>", "<U4 2001>")));
        Assert.Equal(7, ex.Function);
        Assert.Empty(eq.ActiveTraces);
    }

    [Fact]
    public async Task MaxTracesZero_S9F5()
    {
        using var eq = Eq(e => e.MaxTraces = 0);
        using var host = await GemKit.Communicating(eq);
        var ex = await Assert.ThrowsAsync<SecsErrorException>(() => host.SendAsync(Std()));
        Assert.Equal(5, ex.Function);
    }

    [Fact]
    public async Task Registered_GoesToApp()
    {
        using var eq = Eq(e =>
        {
            e.RegisterMessage(2, 23);
            e.MessageReceived += (s, a) => a.Reply(new SecsMessage(2, 24, false, SecsItem.B(9)));
        });
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F24 <B 0x09>", await host.Ask(Std()));
        Assert.Empty(eq.ActiveTraces);
    }

    [Fact]
    public async Task Disable_ClearsTraces()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal(Ok, await host.Ask(Std()));
        Assert.True(eq.DisableCommunication());
        Assert.Empty(eq.ActiveTraces);
    }

    [Fact]
    public async Task StopStart_ClearsTraces()
    {
        using var eq = Eq();
        using (var host = await GemKit.Communicating(eq)) Assert.Equal(Ok, await host.Ask(Std()));
        eq.Stop();
        Assert.Empty(eq.ActiveTraces);
        eq.Start();
        Assert.Empty(eq.ActiveTraces);
    }

    [Fact]
    public void Settings_RangeAndRunning()
    {
        using var eq = GemKit.Equipment();
        Assert.Equal((10, 100, 100, true), (eq.MaxTraces, eq.MaxTraceVariables, eq.MinTracePeriod, eq.TraceReplyExpected));
        Assert.Throws<ArgumentOutOfRangeException>(() => eq.MaxTraces = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => eq.MaxTraces = 101);
        Assert.Throws<ArgumentOutOfRangeException>(() => eq.MaxTraceVariables = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => eq.MaxTraceVariables = 1001);
        Assert.Throws<ArgumentOutOfRangeException>(() => eq.MinTracePeriod = 9);
        Assert.Throws<ArgumentOutOfRangeException>(() => eq.MinTracePeriod = 3600001);
        eq.MaxTraces = 0;
        eq.MaxTraces = 100;
        eq.MaxTraceVariables = 1000;
        eq.MinTracePeriod = 10;
        eq.MinTracePeriod = 3600000;
        eq.Start();
        Assert.Throws<InvalidOperationException>(() => eq.MaxTraces = 5);
        Assert.Throws<InvalidOperationException>(() => eq.MaxTraceVariables = 5);
        Assert.Throws<InvalidOperationException>(() => eq.MinTracePeriod = 500);
        Assert.Throws<InvalidOperationException>(() => eq.TraceReplyExpected = false);
    }

    [Fact]
    public void Xml_RoundTrip()
    {
        using var a = new GemEquipment(new SecsHsms()) { MaxTraces = 3, MaxTraceVariables = 7, MinTracePeriod = 250, TraceReplyExpected = false };
        var xml = a.ToXml();
        Assert.Equal("3", (string?)xml.Attribute("MaxTraces"));
        Assert.Equal("7", (string?)xml.Attribute("MaxTraceVariables"));
        Assert.Equal("250", (string?)xml.Attribute("MinTracePeriod"));
        Assert.Equal("false", (string?)xml.Attribute("TraceReplyExpected"));
        using var b = new GemEquipment(new SecsHsms());
        b.FromXml(xml);
        Assert.Equal((3, 7, 250, false), (b.MaxTraces, b.MaxTraceVariables, b.MinTracePeriod, b.TraceReplyExpected));
        Assert.True(XNode.DeepEquals(xml, b.ToXml()));

        var set = new XmlSchemaSet();
        set.Add(XmlSchema.Read(new StringReader(ConfigSchema.Get<GemEquipmentConfig>()), null)!);
        new XDocument(xml).Validate(set, (_, e) => Assert.Fail(e.Message));

        Assert.Throws<FormatException>(() => new GemEquipment(new SecsHsms()).FromXml(XElement.Parse("<GemEquipment MaxTraces=\"101\" />")));
        Assert.Throws<FormatException>(() => new GemEquipment(new SecsHsms()).FromXml(XElement.Parse("<GemEquipment MaxTraceVariables=\"0\" />")));
        Assert.Throws<FormatException>(() => new GemEquipment(new SecsHsms()).FromXml(XElement.Parse("<GemEquipment MinTracePeriod=\"5\" />")));

        using var c = new GemEquipment(new SecsHsms());
        c.FromXml(XElement.Parse("<GemEquipment />"));
        Assert.Equal((10, 100, 100, true), (c.MaxTraces, c.MaxTraceVariables, c.MinTracePeriod, c.TraceReplyExpected));
    }
}
