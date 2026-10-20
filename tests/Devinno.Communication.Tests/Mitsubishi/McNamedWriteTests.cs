using System.Xml.Linq;
using System.Xml.Schema;
using Devinno.Communication.Mitsubishi.Data;
using Devinno.Communication.Mitsubishi.Ethernet;
using Devinno.Communication.Mitsubishi.Serial;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Mitsubishi;

public class McNamedWriteTests
{
    static McEthernet Eth(string xml)
    {
        var mc = new McEthernet();
        mc.FromXml(XElement.Parse(xml));
        return mc;
    }

    static McSerial Ser(string xml)
    {
        var mc = new McSerial();
        mc.FromXml(XElement.Parse(xml));
        return mc;
    }

    [Fact]
    public void Ethernet_NamedWrite_WordDevice_IsWW()
    {
        var mc = Eth("""<McEthernet><Write Name="속도" Id="20" Device="D200" /></McEthernet>""");
        mc.NamedWrite("속도", 1500);
        var w = mc.TakeNextWork()!;
        Assert.Equal((20, McCommand.WW, "D200"), (w.MessageID, w.Command, w.Device.Name));
        Assert.Equal(new long[] { 1500 }, w.Values);
    }

    [Fact]
    public void Ethernet_NamedWrite_BitDevice_IsBW()
    {
        var mc = Eth("""<McEthernet><Write Name="리셋" Id="21" Device="M100" /></McEthernet>""");
        mc.NamedWrite("리셋", [1, 0]);
        var w = mc.TakeNextWork()!;
        Assert.Equal((21, McCommand.BW, "M100"), (w.MessageID, w.Command, w.Device.Name));
        Assert.Equal(new long[] { 1, 0 }, w.Values);
    }

    [Fact]
    public void Serial_NamedWrite_UsesSlaveAndXmlRepeatTimeout()
    {
        var mc = Ser("""<McSerial><Write Name="A" Id="1" Slave="3" Device="D0" Timeout="500" RepeatCount="2" /></McSerial>""");
        mc.NamedWrite("A", 1);
        var w = mc.TakeNextWork()!;
        Assert.Equal((1, 3, McCommand.WW, "D0", (int?)2, (int?)500), (w.MessageID, w.Slave, w.Command, w.Device, w.RepeatCount, w.Timeout));

        mc.NamedWrite("A", 1, repeatCount: 0, timeout: 100);
        w = mc.TakeNextWork()!;
        Assert.Equal(((int?)0, (int?)100), (w.RepeatCount, w.Timeout));
    }

    [Fact]
    public void Serial_NamedWrite_BitDevice_IsBW_AndMissingXmlValuesAreNull()
    {
        var mc = Ser("""<McSerial><Write Name="A" Id="1" Slave="0" Device="M0" /></McSerial>""");
        mc.NamedWrite("A", [1]);
        var w = mc.TakeNextWork()!;
        Assert.Equal(McCommand.BW, w.Command);
        Assert.Null(w.RepeatCount);
        Assert.Null(w.Timeout);
    }

    [Fact]
    public void NamedWrite_Unknown_Throws_AndContains()
    {
        var eth = Eth("""<McEthernet><Write Name="A" Id="1" Device="D0" /></McEthernet>""");
        var ser = Ser("""<McSerial><Write Name="A" Id="1" Slave="0" Device="D0" /></McSerial>""");
        var e1 = Assert.Throws<ArgumentException>(() => eth.NamedWrite("B", 1));
        Assert.Contains("B", e1.Message);
        var e2 = Assert.Throws<ArgumentException>(() => ser.NamedWrite("a", [1]));
        Assert.Contains("a", e2.Message);
        Assert.True(eth.ContainsNamedWrite("A"));
        Assert.False(eth.ContainsNamedWrite("a"));
        Assert.True(ser.ContainsNamedWrite("A"));
        Assert.False(ser.ContainsNamedWrite("B"));
    }

    [Fact]
    public void NamedWrite_BadValue_Throws()
    {
        var eth = Eth("""<McEthernet><Write Name="B" Id="1" Device="M0" /></McEthernet>""");
        Assert.Throws<ArgumentOutOfRangeException>(() => eth.NamedWrite("B", 2));
    }

    [Theory]
    [InlineData("""<McEthernet><Write Name="Old" Id="9" Device="D0" /></McEthernet>""",
                """<McEthernet><Write Name="A" Id="1" Device="Q1" /></McEthernet>""")]
    [InlineData("""<McEthernet><Write Name="Old" Id="9" Device="D0" /></McEthernet>""",
                """<McEthernet><Write Name="A" Id="1" Device="" /></McEthernet>""")]
    [InlineData("""<McEthernet Frame="E1"><Write Name="Old" Id="9" Device="D0" /></McEthernet>""",
                """<McEthernet Frame="E1"><Write Name="A" Id="1" Device="SM400" /></McEthernet>""")]
    public void Ethernet_FromXml_BadDevice_ThrowsAndKeepsOldTable(string good, string bad)
    {
        var mc = Eth(good);
        var ex = Assert.Throws<FormatException>(() => mc.FromXml(XElement.Parse(bad)));
        Assert.Contains("Write Name=\"A\"", ex.Message);
        Assert.Contains("Device", ex.Message);
        Assert.True(mc.ContainsNamedWrite("Old"));
        Assert.False(mc.ContainsNamedWrite("A"));
    }

    [Theory]
    [InlineData("""<McSerial><Write Name="A" Id="1" Slave="0" Device="Q1" /></McSerial>""", "Device")]
    [InlineData("""<McSerial><Write Name="A" Id="1" Slave="0" Device="" /></McSerial>""", "Device")]
    [InlineData("""<McSerial><Write Name="A" Id="1" Slave="32" Device="D0" /></McSerial>""", "Slave")]
    public void Serial_FromXml_BadAddress_ThrowsAndKeepsOldTable(string bad, string attr)
    {
        var mc = Ser("""<McSerial><Write Name="Old" Id="9" Slave="0" Device="D0" /></McSerial>""");
        var ex = Assert.Throws<FormatException>(() => mc.FromXml(XElement.Parse(bad)));
        Assert.Contains("Write Name=\"A\"", ex.Message);
        Assert.Contains(attr, ex.Message);
        Assert.True(mc.ContainsNamedWrite("Old"));
    }

    [Fact]
    public void FromXml_AutoErrorBesideGoodWrite_KeepsOldTable()
    {
        var mc = Eth("""<McEthernet><Write Name="Old" Id="9" Device="D0" /></McEthernet>""");
        Assert.Throws<FormatException>(() => mc.FromXml(XElement.Parse(
            """<McEthernet><WR Id="1" Device="Q1" Count="1" /><Write Name="New" Id="2" Device="D0" /></McEthernet>""")));
        Assert.True(mc.ContainsNamedWrite("Old"));
        Assert.False(mc.ContainsNamedWrite("New"));
    }

    [Fact]
    public void RoundTrip_KeepsWritesAfterAutos()
    {
        var eth = Eth("""
            <McEthernet>
              <Write Name="B" Id="3" Device="M0" RepeatCount="1" />
              <WR Id="1" Device="D0" Count="1" />
              <Write Name="A" Id="2" Device="D5" Timeout="300" />
            </McEthernet>
            """);
        var x = eth.ToXml();
        Assert.Equal(new[] { "WR", "Write", "Write" }, x.Elements().Select(e => e.Name.LocalName));
        var b = new McEthernet();
        b.FromXml(x);
        Assert.Equal(x.ToString(), b.ToXml().ToString());

        var ser = Ser("""
            <McSerial>
              <Write Name="B" Id="3" Slave="1" Device="M0" />
              <BR Id="1" Slave="0" Device="M0" Count="1" />
              <Write Name="A" Id="2" Slave="2" Device="D5" Timeout="300" RepeatCount="0" />
            </McSerial>
            """);
        var sx = ser.ToXml();
        Assert.Equal(new[] { "BR", "Write", "Write" }, sx.Elements().Select(e => e.Name.LocalName));
        var sb = new McSerial();
        sb.FromXml(sx);
        Assert.Equal(sx.ToString(), sb.ToXml().ToString());
    }

    [Fact]
    public void ToConfig_Writes_AreCopies()
    {
        var mc = Eth("""<McEthernet><Write Name="A" Id="1" Device="D0" /></McEthernet>""");
        mc.ToConfig().Writes[0].Device = "D99";
        mc.NamedWrite("A", 1);
        Assert.Equal("D0", mc.TakeNextWork()!.Device.Name);
    }

    [Fact]
    public void FromXml_WithoutWrites_ClearsTable()
    {
        var mc = Eth("""<McEthernet><Write Name="A" Id="1" Device="D0" /></McEthernet>""");
        mc.FromXml(XElement.Parse("<McEthernet />"));
        Assert.False(mc.ContainsNamedWrite("A"));
    }

    [Fact]
    public void Schema_HasWrite_AndValidatesOwnOutput()
    {
        var eth = Eth("""<McEthernet><WR Id="1" Device="D0" Count="1" /><Write Name="A" Id="2" Device="D5" Timeout="300" /></McEthernet>""");
        var ser = Ser("""<McSerial><BR Id="1" Slave="0" Device="M0" Count="1" /><Write Name="A" Id="2" Slave="1" Device="D5" RepeatCount="1" /></McSerial>""");
        foreach (var (xsd, xml, required, root) in new[]
        {
            (ConfigSchema.Get<McEthernetConfig>(), eth.ToXml(), new[] { "Name", "Id", "Device" }, "McEthernet"),
            (ConfigSchema.Get<McSerialConfig>(), ser.ToXml(), new[] { "Name", "Id", "Slave", "Device" }, "McSerial"),
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
            var particle = (XmlSchemaParticle)rootType.ContentTypeParticle;
            var write = FindElement(particle, "Write");
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

    [Fact]
    public async Task Ethernet_NamedWrite_Loopback_WhileRunning()
    {
        using var plc = new FakeMcPlc { XYOctal = false };
        plc.Start(McFrame.E3);
        using var mc = new McEthernet();
        mc.FromXml(XElement.Parse($"""
            <McEthernet RemoteIP="127.0.0.1" RemotePort="{plc.Port}" Timeout="500" Interval="1">
              <WR Id="1" Device="D0" Count="1" />
              <Write Name="속도" Id="20" Device="D200" />
            </McEthernet>
            """));
        var tcs = new TaskCompletionSource<McEthernet.ResultEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        mc.WriteReceived += (s, e) => tcs.TrySetResult(e);
        mc.Start();
        mc.NamedWrite("속도", 1234);
        var e = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(20, e.MessageID);
        Assert.Equal(1234L, plc.Get("D200"));
    }
}
