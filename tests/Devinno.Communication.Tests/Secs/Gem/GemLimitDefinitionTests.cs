using System.Xml.Linq;
using System.Xml.Schema;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Secs.Hsms;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 리밋 정의: GemStatusVariable 선택 인수 검사, 리밋 SV가 있을 때만 표준 DV·Limit CE, CEID 겹침, 표준 ID 바꾸기, XML
public class GemLimitDefinitionTests
{
    static GemStatusVariable Pressure(int count = 2, GemId? ceid = null) => new(2101, "Pressure", SecsFormat.F4, "kPa", SecsItem.F4(0), count, SecsItem.F4(0), SecsItem.F4(100), ceid ?? (GemId)7001);

    static GemEquipment Eq(Action<GemEquipment>? more = null) => GemKit.Equipment(e =>
    {
        GemKit.Sample(e);
        e.InitialControlState = GemControlState.OnlineRemote;
        more?.Invoke(e);
    });

    [Fact]
    public void Ctor_Validation()
    {
        Assert.Throws<ArgumentException>(() => new GemStatusVariable(1, "S", SecsFormat.ASCII, limitCount: 1, limitCeid: 7));
        Assert.Throws<ArgumentException>(() => new GemStatusVariable(1, "S", SecsFormat.Boolean, limitCount: 1, limitCeid: 7));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GemStatusVariable(1, "S", SecsFormat.F4, limitCount: 256, limitCeid: 7));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GemStatusVariable(1, "S", SecsFormat.F4, limitCount: -1));
        Assert.Throws<ArgumentException>(() => new GemStatusVariable(1, "S", SecsFormat.F4, limitCount: 1));
        Assert.Throws<ArgumentException>(() => new GemStatusVariable(1, "S", SecsFormat.F4, limitCount: 1, limitMin: SecsItem.F4(5), limitMax: SecsItem.F4(1), limitCeid: 7));
        Assert.Throws<ArgumentException>(() => new GemStatusVariable(1, "S", SecsFormat.F4, limitCount: 1, limitMin: SecsItem.U4(0), limitCeid: 7));
        Assert.Throws<ArgumentException>(() => new GemStatusVariable(1, "S", SecsFormat.F4, limitCount: 1, limitMin: SecsItem.F4([1f, 2f]), limitCeid: 7));
        var v = Pressure();
        Assert.Equal(2, v.LimitCount);
        Assert.Equal(SecsItem.F4(0), v.LimitMin);
        Assert.Equal(SecsItem.F4(100), v.LimitMax);
        Assert.Equal((GemId)7001, v.LimitCeid);
        var plain = new GemStatusVariable(1, "S", SecsFormat.ASCII);
        Assert.Equal(0, plain.LimitCount);
        Assert.Null(plain.LimitCeid);
        Assert.Null(plain.LimitMin);
        new GemStatusVariable(1, "S", SecsFormat.U8, limitCount: 255, limitCeid: 7);            // 범위 끝은 포맷에서
    }

    [Fact]
    public async Task NoLimitSv_NoNewStandardIds()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        var dvs = await host.Ask("S1F21 W <L>");
        foreach (var id in new[] { 1204, 1205, 1206 }) Assert.DoesNotContain($"<U4 {id}>", dvs);
        Assert.DoesNotContain("Limit Zone Transition", await host.Ask("S1F23 W <L>"));
    }

    [Fact]
    public async Task LimitSv_DefinesDvsAndCe()
    {
        using var eq = Eq(e => e.AddStatusVariable(Pressure()));
        using var host = await GemKit.Communicating(eq);
        var dvs = await host.Ask("S1F21 W <L>");
        Assert.Contains("<U4 1204> <A \"LimitVariable\">", dvs);
        Assert.Contains("<U4 1205> <A \"EventLimit\">", dvs);
        Assert.Contains("<U4 1206> <A \"TransitionType\">", dvs);
        Assert.Equal("S1F24 <L [1] <L [3] <U4 7001> <A \"Limit Zone Transition Pressure\"> <L [3] <U4 1204> <U4 1205> <U4 1206>>>>", await host.Ask("S1F23 W <L <U4 7001>>"));
    }

    [Fact]
    public void DuplicateLimitCeid_StartFails()
    {
        using (var eq = Eq(e =>
        {
            e.AddStatusVariable(Pressure());
            e.AddStatusVariable(new GemStatusVariable(2102, "Flow", SecsFormat.U4, limitCount: 1, limitCeid: 7001));
        }))
            Assert.Throws<InvalidOperationException>(() => eq.Start());
        using (var eq = Eq(e => e.AddStatusVariable(Pressure(ceid: 5001))))
            Assert.Throws<InvalidOperationException>(() => eq.Start());
        using (var eq = Eq(e => e.AddStatusVariable(Pressure(ceid: 9001))))                  // 알람 CE
            Assert.Throws<InvalidOperationException>(() => eq.Start());
    }

    [Fact]
    public async Task StandardIds_Override()
    {
        using var eq = Eq(e =>
        {
            e.AddStatusVariable(Pressure());
            e.StandardIds.EventLimit = 9205;
        });
        using var host = await GemKit.Communicating(eq);
        var dvs = await host.Ask("S1F21 W <L>");
        Assert.Contains("<U4 9205> <A \"EventLimit\">", dvs);
        Assert.DoesNotContain("<U4 1205>", dvs);
    }

    [Fact]
    public void Xml_RoundTrip()
    {
        using var a = new GemEquipment(new SecsHsms());
        a.AddStatusVariable(Pressure());
        a.AddStatusVariable(new GemStatusVariable(2102, "Flow", SecsFormat.U4, limitCount: 1, limitCeid: 7002));
        a.AddStatusVariable(new GemStatusVariable(2103, "Name", SecsFormat.ASCII));
        a.StandardIds.TransitionType = 9206;
        var xml = a.ToXml();
        var svs = xml.Elements("SV").ToList();
        Assert.Equal("2", (string?)svs[0].Attribute("LimitCount"));
        Assert.Equal("0", (string?)svs[0].Attribute("LimitMin"));
        Assert.Equal("100", (string?)svs[0].Attribute("LimitMax"));
        Assert.Equal("7001", (string?)svs[0].Attribute("LimitCeid"));
        Assert.Null(svs[1].Attribute("LimitMin"));
        Assert.Null(svs[2].Attribute("LimitCount"));
        Assert.Null(svs[2].Attribute("LimitCeid"));
        Assert.Equal("9206", (string?)xml.Element("Standard")!.Attribute("TransitionType"));
        using var b = new GemEquipment(new SecsHsms());
        b.FromXml(xml);
        Assert.True(XNode.DeepEquals(xml, b.ToXml()));

        var set = new XmlSchemaSet();
        set.Add(XmlSchema.Read(new StringReader(ConfigSchema.Get<GemEquipmentConfig>()), null)!);
        new XDocument(xml).Validate(set, (_, e) => Assert.Fail(e.Message));

        Assert.Throws<FormatException>(() => new GemEquipment(new SecsHsms()).FromXml(XElement.Parse("<GemEquipment><SV Id=\"1\" Name=\"S\" Format=\"F4\" LimitCount=\"300\" LimitCeid=\"7\" /></GemEquipment>")));
        Assert.Throws<FormatException>(() => new GemEquipment(new SecsHsms()).FromXml(XElement.Parse("<GemEquipment><SV Id=\"1\" Name=\"S\" Format=\"F4\" LimitCount=\"1\" /></GemEquipment>")));
        Assert.Throws<FormatException>(() => new GemEquipment(new SecsHsms()).FromXml(XElement.Parse("<GemEquipment><SV Id=\"1\" Name=\"S\" Format=\"F4\" LimitCount=\"1\" LimitCeid=\"7\" LimitMin=\"x\" /></GemEquipment>")));
    }
}
