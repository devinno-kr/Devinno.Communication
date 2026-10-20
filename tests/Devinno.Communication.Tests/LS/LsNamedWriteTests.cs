using System.Xml.Linq;
using System.Xml.Schema;
using Devinno.Communication.LS.CNet;
using Devinno.Communication.LS.Data;
using Devinno.Communication.LS.FEnet;

namespace Devinno.Communication.Tests.LS;

public class LsNamedWriteTests
{
    static LsCNet CNet(string xml) { var c = new LsCNet(); c.FromXml(XElement.Parse(xml)); return c; }
    static LsFEnet FEnet(string xml) { var f = new LsFEnet(); f.FromXml(XElement.Parse(xml)); return f; }

    [Fact]
    public void CNet_NamedWrite_Single_IsWss()
    {
        var c = CNet("""<LsCNet><Write Name="A" Id="5" Slave="1" Device="%MW100" /></LsCNet>""");
        c.NamedWrite("A", 1234);
        var w = c.TakeNextWork()!;
        Assert.Equal((5, 1, LsCommand.WSS), (w.MessageID, w.Slave, w.Command));

        var m = new LsCNet();
        m.ManualWSS(5, 1, [new LsValue("%MW100", 1234)]);
        Assert.Equal(m.TakeNextWork()!.Request, w.Request);
    }

    [Fact]
    public void CNet_NamedWrite_Multi_IsWsb()
    {
        var c = CNet("""<LsCNet><Write Name="A" Id="5" Slave="1" Device="%MW100" /></LsCNet>""");
        c.NamedWrite("A", [1, 2, 3]);
        var w = c.TakeNextWork()!;
        Assert.Equal((5, 1, LsCommand.WSB), (w.MessageID, w.Slave, w.Command));

        var m = new LsCNet();
        m.ManualWSB(5, 1, "%MW100", [1, 2, 3]);
        Assert.Equal(m.TakeNextWork()!.Request, w.Request);
    }

    [Fact]
    public void FEnet_NamedWrite_Single_And_Multi()
    {
        var f = FEnet("""<LsFEnet><Write Name="A" Id="7" Device="%MW100" Timeout="500" RepeatCount="2" /></LsFEnet>""");
        f.NamedWrite("A", 9);
        var w = f.TakeNextWork()!;
        Assert.Equal((7, LsCommand.WSS, (int?)2, (int?)500), (w.MessageID, w.Command, w.RepeatCount, w.Timeout));
        var m = new LsFEnet();
        m.ManualWSS(7, [new LsValue("%MW100", 9)]);
        Assert.Equal(m.TakeNextWork()!.Body, w.Body);

        f.NamedWrite("A", [1, 2], repeatCount: 0, timeout: 100);
        w = f.TakeNextWork()!;
        Assert.Equal((LsCommand.WSB, (int?)0, (int?)100), (w.Command, w.RepeatCount, w.Timeout));
        m.ManualWSB(7, "%MW100", [1, 2]);
        Assert.Equal(m.TakeNextWork()!.Body, w.Body);
    }

    [Fact]
    public void FEnet_BitDevice_SingleOk_MultiThrows()
    {
        var f = FEnet("""<LsFEnet><Write Name="A" Id="1" Device="%MX0" /></LsFEnet>""");
        f.NamedWrite("A", 1);
        Assert.Equal(LsCommand.WSS, f.TakeNextWork()!.Command);
        Assert.Throws<ArgumentException>(() => f.NamedWrite("A", [1, 0]));
    }

    [Fact]
    public void CNet_BitDevice_SingleOk_MultiThrows_BadValueThrows()
    {
        var c = CNet("""<LsCNet><Write Name="A" Id="1" Slave="0" Device="%MX0" /></LsCNet>""");
        c.NamedWrite("A", 1);
        Assert.Equal(LsCommand.WSS, c.TakeNextWork()!.Command);
        Assert.Throws<ArgumentException>(() => c.NamedWrite("A", [1, 0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => c.NamedWrite("A", 2));
    }

    [Fact]
    public void NamedWrite_Unknown_Throws_AndContains()
    {
        var c = CNet("""<LsCNet><Write Name="A" Id="1" Slave="0" Device="%MW0" /></LsCNet>""");
        var f = FEnet("""<LsFEnet><Write Name="A" Id="1" Device="%MW0" /></LsFEnet>""");
        Assert.Contains("B", Assert.Throws<ArgumentException>(() => c.NamedWrite("B", 1)).Message);
        Assert.Contains("a", Assert.Throws<ArgumentException>(() => f.NamedWrite("a", [1])).Message);
        Assert.True(c.ContainsNamedWrite("A"));
        Assert.False(c.ContainsNamedWrite("a"));
        Assert.True(f.ContainsNamedWrite("A"));
        Assert.False(f.ContainsNamedWrite("B"));
    }

    [Theory]
    [InlineData("""<LsCNet><Write Name="A" Id="1" Slave="0" Device="MW100" /></LsCNet>""", "Device")]
    [InlineData("""<LsCNet><Write Name="A" Id="1" Slave="0" Device="%MW0000000000000000" /></LsCNet>""", "Device")]
    [InlineData("""<LsCNet><Write Name="A" Id="1" Slave="256" Device="%MW0" /></LsCNet>""", "Slave")]
    public void CNet_FromXml_BadAddress_ThrowsAndKeepsOldTable(string bad, string attr)
    {
        var c = CNet("""<LsCNet><Write Name="Old" Id="9" Slave="0" Device="%MW0" /></LsCNet>""");
        var ex = Assert.Throws<FormatException>(() => c.FromXml(XElement.Parse(bad)));
        Assert.Contains("Write Name=\"A\"", ex.Message);
        Assert.Contains(attr, ex.Message);
        Assert.True(c.ContainsNamedWrite("Old"));
        Assert.False(c.ContainsNamedWrite("A"));
    }

    [Theory]
    [InlineData("""<LsFEnet><Write Name="A" Id="1" Device="MW100" /></LsFEnet>""")]
    [InlineData("""<LsFEnet><Write Name="A" Id="1" Device="%MW0000000000000000" /></LsFEnet>""")]
    public void FEnet_FromXml_BadDevice_ThrowsAndKeepsOldTable(string bad)
    {
        var f = FEnet("""<LsFEnet><Write Name="Old" Id="9" Device="%MW0" /></LsFEnet>""");
        var ex = Assert.Throws<FormatException>(() => f.FromXml(XElement.Parse(bad)));
        Assert.Contains("Write Name=\"A\"", ex.Message);
        Assert.Contains("Device", ex.Message);
        Assert.True(f.ContainsNamedWrite("Old"));
        Assert.False(f.ContainsNamedWrite("A"));
    }

    [Fact]
    public void FromXml_AutoErrorBesideGoodWrite_KeepsOldTable()
    {
        var f = FEnet("""<LsFEnet><Write Name="Old" Id="9" Device="%MW0" /></LsFEnet>""");
        Assert.Throws<FormatException>(() => f.FromXml(XElement.Parse(
            """<LsFEnet><RSB Id="1" Device="bad" Count="1" /><Write Name="New" Id="2" Device="%MW0" /></LsFEnet>""")));
        Assert.True(f.ContainsNamedWrite("Old"));
        Assert.False(f.ContainsNamedWrite("New"));
    }

    [Fact]
    public void RoundTrip_KeepsWritesAfterAutos()
    {
        var c = CNet("""
            <LsCNet>
              <Write Name="B" Id="3" Slave="1" Device="%MX0" RepeatCount="1" />
              <RSB Id="1" Slave="0" Device="%MW0" Count="1" />
              <Write Name="A" Id="2" Slave="2" Device="%MW5" Timeout="300" RepeatCount="0" />
            </LsCNet>
            """);
        var x = c.ToXml();
        Assert.Equal(new[] { "RSB", "Write", "Write" }, x.Elements().Select(e => e.Name.LocalName));
        var b = new LsCNet();
        b.FromXml(x);
        Assert.Equal(x.ToString(), b.ToXml().ToString());

        var f = FEnet("""
            <LsFEnet>
              <Write Name="B" Id="3" Device="%MW0" />
              <RSS Id="1" Devices="%MW0" />
              <Write Name="A" Id="2" Device="%MW5" Timeout="300" />
            </LsFEnet>
            """);
        var fx = f.ToXml();
        Assert.Equal(new[] { "RSS", "Write", "Write" }, fx.Elements().Select(e => e.Name.LocalName));
        var fb = new LsFEnet();
        fb.FromXml(fx);
        Assert.Equal(fx.ToString(), fb.ToXml().ToString());
    }

    [Fact]
    public void ToConfig_Writes_AreCopies_AndMissingWritesClearTable()
    {
        var f = FEnet("""<LsFEnet><Write Name="A" Id="1" Device="%MW0" /></LsFEnet>""");
        f.ToConfig().Writes[0].Device = "%MW99";
        f.NamedWrite("A", 1);
        Assert.Equal("%MW0", f.TakeNextWork()!.Devices[0]);
        f.FromXml(XElement.Parse("<LsFEnet />"));
        Assert.False(f.ContainsNamedWrite("A"));
    }

    [Fact]
    public void Schema_HasWrite_AndValidatesOwnOutput()
    {
        var c = CNet("""<LsCNet><RSB Id="1" Slave="0" Device="%MW0" Count="1" /><Write Name="A" Id="2" Slave="1" Device="%MW5" RepeatCount="1" /></LsCNet>""");
        var f = FEnet("""<LsFEnet><RSB Id="1" Device="%MW0" Count="1" /><Write Name="A" Id="2" Device="%MW5" Timeout="300" /></LsFEnet>""");
        foreach (var (xsd, xml, required, root) in new[]
        {
            (ConfigSchema.Get<LsCNetConfig>(), c.ToXml(), new[] { "Name", "Id", "Slave", "Device" }, "LsCNet"),
            (ConfigSchema.Get<LsFEnetConfig>(), f.ToXml(), new[] { "Name", "Id", "Device" }, "LsFEnet"),
        })
        {
            Assert.Contains("name=\"Write\"", xsd);
            var set = new XmlSchemaSet();
            set.Add(XmlSchema.Read(new StringReader(xsd), null)!);
            set.Compile();
            var errors = new List<string>();
            new XDocument(xml).Validate(set, (_, e) => errors.Add(e.Message));
            Assert.Empty(errors);

            var element = (XmlSchemaElement)set.GlobalElements.Values.Cast<XmlSchemaObject>().Single(o => ((XmlSchemaElement)o).Name == root);
            var rootType = (XmlSchemaComplexType)element.ElementSchemaType!;
            var write = FindElement((XmlSchemaParticle)rootType.ContentTypeParticle, "Write");
            var writeType = (XmlSchemaComplexType)write.ElementSchemaType!;
            foreach (var name in required)
                Assert.Equal(XmlSchemaUse.Required, ((XmlSchemaAttribute)writeType.AttributeUses[new System.Xml.XmlQualifiedName(name)]!).Use);
        }
    }

    static XmlSchemaElement FindElement(XmlSchemaParticle p, string name)
    {
        if (p is XmlSchemaElement e && e.Name == name) return e;
        if (p is XmlSchemaGroupBase g)
            foreach (XmlSchemaParticle c in g.Items)
                if (FindElement(c, name) is { } f) return f;
        return null!;
    }
}
