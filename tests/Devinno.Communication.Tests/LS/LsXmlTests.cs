using System.Xml.Linq;
using System.Xml.Schema;
using Devinno.Communication.LS.CNet;
using Devinno.Communication.LS.FEnet;

namespace Devinno.Communication.Tests.LS;

public class LsXmlTests
{
    [Fact]
    public void CNet_RoundTrip_KeepsOrderAndKinds()
    {
        var a = new LsCNet { Port = "COM5", Baudrate = 19200, Timeout = 700, Interval = 5, AutoReconnect = true };
        a.AutoRSB(1, 2, "%MW200", 10, timeout: 300);
        a.AutoRSS(2, 2, ["%MX0", "%MX1"]);
        a.ManualWSB(9, 1, "%MW0", [1]);   // Manual은 저장 안 됨

        var x = a.ToXml();
        Assert.Equal(new[] { "RSB", "RSS" }, x.Elements().Select(e => e.Name.LocalName));
        var b = new LsCNet();
        b.FromXml(x);
        Assert.Equal((a.Port, a.Baudrate, a.Timeout, a.Interval, a.AutoReconnect), (b.Port, b.Baudrate, b.Timeout, b.Interval, b.AutoReconnect));
        Assert.Equal(x.ToString(), b.ToXml().ToString());
        Assert.Equal(1, b.TakeNextWork()!.MessageID);
        Assert.Equal(2, b.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void CNet_HandWritten_WithDefaults_AndSpacesInDevices()
    {
        var c = new LsCNet();
        c.FromXml(XElement.Parse("""<LsCNet Port="COM3"><RSS Id="5" Slave="1" Devices=" %MW1, %mw2 " /></LsCNet>"""));
        Assert.Equal(("COM3", 9600, 1000), (c.Port, c.Baudrate, c.Timeout));
        Assert.Equal(new[] { "%MW1", "%MW2" }, c.TakeNextWork()!.Devices);
    }

    [Fact]
    public void FEnet_RoundTrip()
    {
        var a = new LsFEnet { RemoteIP = "10.0.0.7", RemotePort = 2005, AutoReconnect = false };
        a.AutoRSB(1, "%DW10", 4);
        a.AutoRSS(2, ["%MD0"], timeout: 200);
        var b = new LsFEnet();
        b.FromXml(a.ToXml());
        Assert.Equal(("10.0.0.7", 2005, false), (b.RemoteIP, b.RemotePort, b.AutoReconnect));
        Assert.Equal(a.ToXml().ToString(), b.ToXml().ToString());
    }

    [Theory]
    [InlineData("""<LsCNet><RSS Id="1" Slave="1" Devices="%MW1,%MD2" /></LsCNet>""", "Devices", "크기 문자")]
    [InlineData("""<LsCNet><RSS Id="1" Slave="1" Devices="%MQ1" /></LsCNet>""", "Devices", "%MQ1")]
    [InlineData("""<LsCNet><RSB Id="1" Slave="1" Device="%MW0" Count="61" /></LsCNet>""", "Count", "1~60")]
    [InlineData("""<LsCNet><RSB Id="1" Slave="1" Device="%MX0" Count="1" /></LsCNet>""", "Device", "비트")]
    [InlineData("""<LsCNet><RSB Id="1" Slave="300" Device="%MW0" Count="1" /></LsCNet>""", "Slave", "0~255")]
    [InlineData("""<LsCNet><RSB Id="1" Device="%MW0" Count="1" /></LsCNet>""", "Slave", "필수")]
    [InlineData("""<LsCNet><RSX Id="1" /></LsCNet>""", "RSX", "RSS, RSB")]
    [InlineData("""<LsCNet Baudrat="9600" />""", "Baudrat", "Baudrate")]
    public void CNet_FromXml_Invalid_MessageIsPrecise(string xml, string mustContain1, string mustContain2)
    {
        var c = new LsCNet { Port = "COM9" };
        var ex = Assert.Throws<FormatException>(() => c.FromXml(XElement.Parse(xml)));
        Assert.Contains(mustContain1, ex.Message);
        Assert.Contains(mustContain2, ex.Message);
        Assert.Equal("COM9", c.Port);
    }

    [Fact]
    public void FromXml_MixedSizes_NamesDevices()
    {
        var ex = Assert.Throws<FormatException>(() => new LsFEnet().FromXml(XElement.Parse("""<LsFEnet><RSS Id="3" Devices="%MW0,%MX0" /></LsFEnet>""")));
        Assert.Contains("RSS Id=\"3\"", ex.Message);
        Assert.Contains("Devices", ex.Message);
    }

    [Fact]
    public void FEnet_RsbDottedAddress_Rejected()
    {
        var ex = Assert.Throws<FormatException>(() => new LsFEnet().FromXml(XElement.Parse("""<LsFEnet><RSB Id="1" Device="%IW0.0.0" Count="1" /></LsFEnet>""")));
        Assert.Contains("Device", ex.Message);
    }

    [Fact]
    public void FromXml_WhileRunning_Throws()
    {
        using var c = new LsCNet { Port = "COM_NOT_EXIST", AutoReconnect = true };
        c.Start();
        Assert.Throws<InvalidOperationException>(() => c.FromXml(c.ToXml()));
    }

    [Fact]
    public void Schemas_Compile_AndValidateOwnOutput()
    {
        var cnet = new LsCNet();
        cnet.AutoRSS(1, 1, ["%MW0"]);
        cnet.AutoRSB(2, 1, "%MW0", 2, timeout: 50);
        var fenet = new LsFEnet();
        fenet.AutoRSB(1, "%MW0", 2);

        foreach (var (xsd, xml) in new[]
        {
            (Devinno.Communication.ConfigSchema.Get<LsCNetConfig>(), cnet.ToXml()),
            (Devinno.Communication.ConfigSchema.Get<LsFEnetConfig>(), fenet.ToXml()),
        })
        {
            var set = new XmlSchemaSet();
            set.Add(XmlSchema.Read(new StringReader(xsd), null)!);
            set.Compile();
            var errors = new List<string>();
            new XDocument(xml).Validate(set, (_, e) => errors.Add(e.Message));
            Assert.Empty(errors);
        }

        var cxsd = Devinno.Communication.ConfigSchema.Get<LsCNetConfig>();
        Assert.Contains("name=\"RSS\"", cxsd);
        Assert.Contains("name=\"RSB\"", cxsd);
        Assert.Contains("default=\"9600\"", cxsd);
    }

    [Theory]
    [InlineData(typeof(LsCNetConfig))]
    [InlineData(typeof(LsFEnetConfig))]
    public void Schemas_DerivedAutoTypes_MarkRequiredAttributes(Type configType)
    {
        var xsd = configType == typeof(LsCNetConfig)
            ? Devinno.Communication.ConfigSchema.Get<LsCNetConfig>()
            : Devinno.Communication.ConfigSchema.Get<LsFEnetConfig>();
        var set = new XmlSchemaSet();
        set.Add(XmlSchema.Read(new StringReader(xsd), null)!);
        set.Compile();
        var prefix = configType == typeof(LsCNetConfig) ? "LsCNet" : "LsFEnet";
        XmlSchemaAttribute Use(string type, string attr)
        {
            var ct = (XmlSchemaComplexType)set.GlobalTypes.Values.Cast<XmlSchemaType>().Single(t => t.Name == prefix + type);
            return (XmlSchemaAttribute)ct.AttributeUses[new System.Xml.XmlQualifiedName(attr)]!;
        }

        Assert.Equal(XmlSchemaUse.Required, Use("RssConfig", "Devices").Use);
        Assert.Equal(XmlSchemaUse.Required, Use("RsbConfig", "Device").Use);
        Assert.Equal(XmlSchemaUse.Required, Use("RsbConfig", "Count").Use);
        Assert.Equal(XmlSchemaUse.Required, Use("RsbConfig", "Id").Use);
        Assert.Equal(XmlSchemaUse.Optional, Use("RsbConfig", "Timeout").Use);
        Assert.Null(Use("RsbConfig", "Timeout").DefaultValue);
        if (prefix == "LsCNet") Assert.Equal(XmlSchemaUse.Required, Use("RssConfig", "Slave").Use);

        // 필수 속성이 빠진 Auto 항목은 XSD 검증에서도 걸린다
        var slave = prefix == "LsCNet" ? " Slave=\"1\"" : "";
        foreach (var bad in new[] { $"<RSS Id=\"1\"{slave} />", $"<RSB Id=\"1\"{slave} Device=\"%MW0\" />", $"<RSB Id=\"1\"{slave} Count=\"1\" />" })
        {
            var errors = new List<string>();
            new XDocument(XElement.Parse($"<{prefix}>{bad}</{prefix}>")).Validate(set, (_, e) => errors.Add(e.Message));
            Assert.NotEmpty(errors);
        }
    }
}
