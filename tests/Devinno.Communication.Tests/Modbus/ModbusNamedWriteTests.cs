using System.Xml.Linq;
using System.Xml.Schema;
using Devinno.Communication.Modbus;
using Devinno.Communication.Modbus.RTU;
using Devinno.Communication.Modbus.TCP;

namespace Devinno.Communication.Tests.Modbus;

public class ModbusNamedWriteTests
{
    const string WriteXml = """
        <{0}>
          <Write Name="A" Id="7" Func="{1}" Slave="3" Start="100" Timeout="400" RepeatCount="2" />
        </{0}>
        """;

    static ModbusRTUMaster Rtu(string xml)
    {
        var m = new ModbusRTUMaster();
        m.FromXml(XElement.Parse(xml));
        m.DiscardWritesWhileDisconnected = false;   // Start 안 한 객체에 쓰기를 넣어 대기열을 본다
        return m;
    }

    static ModbusTCPMaster Tcp(string xml)
    {
        var m = new ModbusTCPMaster();
        m.FromXml(XElement.Parse(xml));
        m.DiscardWritesWhileDisconnected = false;   // Start 안 한 객체에 쓰기를 넣어 대기열을 본다
        return m;
    }

    static string Xml(string root, string func) => string.Format(WriteXml, root, func);

    static void AssertSame(ModbusRTUMaster.Work a, ModbusRTUMaster.Work b)
    {
        Assert.Equal((b.MessageID, b.Slave, b.RepeatCount, b.Timeout), (a.MessageID, a.Slave, a.RepeatCount, a.Timeout));
        Assert.Equal(b.Pdu, a.Pdu);
    }

    static void AssertSame(ModbusTCPMaster.Work a, ModbusTCPMaster.Work b)
    {
        Assert.Equal((b.MessageID, b.Slave, b.RepeatCount, b.Timeout), (a.MessageID, a.Slave, a.RepeatCount, a.Timeout));
        Assert.Equal(b.Pdu, a.Pdu);
    }

    [Fact]
    public void Fc6_Single_SameAsManual()
    {
        var rtu = Rtu(Xml("ModbusRTUMaster", "FC6"));
        rtu.NamedWrite("A", 1234);
        var rn = rtu.TakeNextWork()!;
        rtu.ManualWordWrite_FC6(7, 3, 100, 1234, 2, 400);
        AssertSame(rn, rtu.TakeNextWork()!);

        var tcp = Tcp(Xml("ModbusTCPMaster", "FC6"));
        tcp.NamedWrite("A", 1234);
        var tn = tcp.TakeNextWork()!;
        tcp.ManualWordWrite_FC6(7, 3, 100, 1234, 2, 400);
        AssertSame(tn, tcp.TakeNextWork()!);
    }

    [Fact]
    public void Fc6_NegativeAndMax_AreAccepted()
    {
        var tcp = Tcp(Xml("ModbusTCPMaster", "FC6"));
        tcp.NamedWrite("A", -32768);
        var a = tcp.TakeNextWork()!;
        tcp.ManualWordWrite_FC6(7, 3, 100, -32768, 2, 400);
        AssertSame(a, tcp.TakeNextWork()!);
        tcp.NamedWrite("A", 65535);
        Assert.Equal(new byte[] { 6, 0, 100, 0xFF, 0xFF }, tcp.TakeNextWork()!.Pdu);
    }

    [Fact]
    public void Fc16_Multi_SameAsManual()
    {
        var rtu = Rtu(Xml("ModbusRTUMaster", "FC16"));
        rtu.NamedWrite("A", [1, 2, 3]);
        var rn = rtu.TakeNextWork()!;
        rtu.ManualMultiWordWrite_FC16(7, 3, 100, [1, 2, 3], 2, 400);
        AssertSame(rn, rtu.TakeNextWork()!);

        var tcp = Tcp(Xml("ModbusTCPMaster", "FC16"));
        tcp.NamedWrite("A", [1, 2, 3]);
        var tn = tcp.TakeNextWork()!;
        tcp.ManualMultiWordWrite_FC16(7, 3, 100, [1, 2, 3], 2, 400);
        AssertSame(tn, tcp.TakeNextWork()!);
    }

    [Fact]
    public void Fc16_SingleValue_IsOneElementArray()
    {
        var tcp = Tcp(Xml("ModbusTCPMaster", "FC16"));
        tcp.NamedWrite("A", 9);
        var tn = tcp.TakeNextWork()!;
        tcp.ManualMultiWordWrite_FC16(7, 3, 100, [9], 2, 400);
        AssertSame(tn, tcp.TakeNextWork()!);
    }

    [Fact]
    public void Fc5_Single()
    {
        var rtu = Rtu(Xml("ModbusRTUMaster", "FC5"));
        rtu.NamedWrite("A", 1);
        var rn = rtu.TakeNextWork()!;
        rtu.ManualBitWrite_FC5(7, 3, 100, true, 2, 400);
        AssertSame(rn, rtu.TakeNextWork()!);
        rtu.NamedWrite("A", 0);
        var off = rtu.TakeNextWork()!;
        rtu.ManualBitWrite_FC5(7, 3, 100, false, 2, 400);
        AssertSame(off, rtu.TakeNextWork()!);

        var tcp = Tcp(Xml("ModbusTCPMaster", "FC5"));
        tcp.NamedWrite("A", 1);
        var tn = tcp.TakeNextWork()!;
        tcp.ManualBitWrite_FC5(7, 3, 100, true, 2, 400);
        AssertSame(tn, tcp.TakeNextWork()!);
    }

    [Fact]
    public void Fc15_Multi()
    {
        var rtu = Rtu(Xml("ModbusRTUMaster", "FC15"));
        rtu.NamedWrite("A", [1, 0, 1]);
        var rn = rtu.TakeNextWork()!;
        rtu.ManualMultiBitWrite_FC15(7, 3, 100, [true, false, true], 2, 400);
        AssertSame(rn, rtu.TakeNextWork()!);

        var tcp = Tcp(Xml("ModbusTCPMaster", "FC15"));
        tcp.NamedWrite("A", [1, 0, 1]);
        var tn = tcp.TakeNextWork()!;
        tcp.ManualMultiBitWrite_FC15(7, 3, 100, [true, false, true], 2, 400);
        AssertSame(tn, tcp.TakeNextWork()!);
    }

    [Fact]
    public void ArgsOverrideXml_AndMissingXmlValuesAreNull()
    {
        var tcp = Tcp("""<ModbusTCPMaster><Write Name="A" Id="1" Func="FC6" Slave="1" /></ModbusTCPMaster>""");
        tcp.NamedWrite("A", 1);
        var w = tcp.TakeNextWork()!;
        Assert.Equal(((int?)null, (int?)null), (w.RepeatCount, w.Timeout));
        tcp.NamedWrite("A", 1, repeatCount: 0, timeout: 50);
        w = tcp.TakeNextWork()!;
        Assert.Equal(((int?)0, (int?)50), (w.RepeatCount, w.Timeout));
    }

    [Theory]
    [InlineData(70000)]
    [InlineData(-40000)]
    public void Fc6_OutOfRange_Throws(long v)
    {
        var rtu = Rtu(Xml("ModbusRTUMaster", "FC6"));
        var tcp = Tcp(Xml("ModbusTCPMaster", "FC6"));
        Assert.Throws<ArgumentException>(() => rtu.NamedWrite("A", v));
        Assert.Throws<ArgumentException>(() => tcp.NamedWrite("A", v));
        Assert.Null(rtu.TakeNextWork());
        Assert.Null(tcp.TakeNextWork());
    }


    [Fact]
    public void Fc16_OutOfRange_Throws()
    {
        var tcp = Tcp(Xml("ModbusTCPMaster", "FC16"));
        Assert.Throws<ArgumentException>(() => tcp.NamedWrite("A", [1, 70000]));
        Assert.Null(tcp.TakeNextWork());
    }

    [Fact]
    public void Fc5_NotBit_Throws()
    {
        var rtu = Rtu(Xml("ModbusRTUMaster", "FC5"));
        var tcp = Tcp(Xml("ModbusTCPMaster", "FC5"));
        Assert.Throws<ArgumentException>(() => rtu.NamedWrite("A", 2));
        Assert.Throws<ArgumentException>(() => tcp.NamedWrite("A", -1));
    }

    [Fact]
    public void Fc15_NotBit_Throws()
    {
        var rtu = Rtu(Xml("ModbusRTUMaster", "FC15"));
        var tcp = Tcp(Xml("ModbusTCPMaster", "FC15"));
        Assert.Throws<ArgumentException>(() => rtu.NamedWrite("A", [1, 2]));
        Assert.Throws<ArgumentException>(() => tcp.NamedWrite("A", [1, 2]));
    }

    [Theory]
    [InlineData("FC5")]
    [InlineData("FC6")]
    public void SingleFunc_WithList_Throws(string func)
    {
        var rtu = Rtu(Xml("ModbusRTUMaster", func));
        var tcp = Tcp(Xml("ModbusTCPMaster", func));
        Assert.Throws<ArgumentException>(() => rtu.NamedWrite("A", [1, 0]));
        Assert.Throws<ArgumentException>(() => tcp.NamedWrite("A", [1, 0]));
    }

    [Fact]
    public void Rtu_Slave0_Broadcast_Allowed()
    {
        var rtu = Rtu("""<ModbusRTUMaster><Write Name="A" Id="1" Func="FC6" Slave="0" /></ModbusRTUMaster>""");
        rtu.NamedWrite("A", 5);
        Assert.Equal(0, rtu.TakeNextWork()!.Slave);
    }

    [Fact]
    public void Tcp_Slave255_Allowed()
    {
        var tcp = Tcp("""<ModbusTCPMaster><Write Name="A" Id="1" Func="FC6" Slave="255" /></ModbusTCPMaster>""");
        tcp.NamedWrite("A", 5);
        Assert.Equal(255, tcp.TakeNextWork()!.Slave);
    }

    [Fact]
    public void Rtu_Slave248_Throws()
    {
        var rtu = Rtu("""<ModbusRTUMaster><Write Name="Old" Id="9" Func="FC6" Slave="1" /></ModbusRTUMaster>""");
        var ex = Assert.Throws<FormatException>(() => rtu.FromXml(XElement.Parse(
            """<ModbusRTUMaster><Write Name="A" Id="1" Func="FC6" Slave="248" /></ModbusRTUMaster>""")));
        Assert.Contains("Write Name=\"A\"", ex.Message);
        Assert.Contains("Slave", ex.Message);
        Assert.True(rtu.ContainsNamedWrite("Old"));
        Assert.False(rtu.ContainsNamedWrite("A"));
    }

    [Theory]
    [InlineData("FC3")]
    [InlineData("FC26")]
    public void FromXml_ReadFunc_Throws(string func)
    {
        var rtu = Rtu("""<ModbusRTUMaster><Write Name="Old" Id="9" Func="FC6" Slave="1" /></ModbusRTUMaster>""");
        var tcp = Tcp("""<ModbusTCPMaster><Write Name="Old" Id="9" Func="FC6" Slave="1" /></ModbusTCPMaster>""");
        var e1 = Assert.Throws<FormatException>(() => rtu.FromXml(XElement.Parse(Xml("ModbusRTUMaster", func))));
        var e2 = Assert.Throws<FormatException>(() => tcp.FromXml(XElement.Parse(Xml("ModbusTCPMaster", func))));
        foreach (var e in new[] { e1, e2 })
        {
            Assert.Contains("Write Name=\"A\"", e.Message);
            Assert.Contains("Func", e.Message);
        }
        Assert.True(rtu.ContainsNamedWrite("Old"));
        Assert.True(tcp.ContainsNamedWrite("Old"));
    }

    [Fact]
    public void FromXml_StartOutOfRange_Throws()
    {
        var tcp = new ModbusTCPMaster();
        var ex = Assert.Throws<FormatException>(() => tcp.FromXml(XElement.Parse(
            """<ModbusTCPMaster><Write Name="A" Id="1" Func="FC6" Slave="1" Start="65536" /></ModbusTCPMaster>""")));
        Assert.Contains("Write Name=\"A\"", ex.Message);
        Assert.Contains("Start", ex.Message);
    }

    [Fact]
    public void NamedWrite_Unknown_Throws_AndContains()
    {
        var rtu = Rtu(Xml("ModbusRTUMaster", "FC6"));
        var tcp = Tcp(Xml("ModbusTCPMaster", "FC6"));
        var e1 = Assert.Throws<ArgumentException>(() => rtu.NamedWrite("B", 1));
        Assert.Contains("B", e1.Message);
        var e2 = Assert.Throws<ArgumentException>(() => tcp.NamedWrite("a", [1]));
        Assert.Contains("a", e2.Message);
        Assert.True(rtu.ContainsNamedWrite("A"));
        Assert.False(rtu.ContainsNamedWrite("a"));
        Assert.True(tcp.ContainsNamedWrite("A"));
        Assert.False(tcp.ContainsNamedWrite("B"));
    }

    [Fact]
    public void RoundTrip_KeepsWritesAfterAutos()
    {
        var rtu = Rtu("""
            <ModbusRTUMaster>
              <Write Name="B" Id="3" Func="FC5" Slave="0" Start="2" />
              <Auto Id="1" Func="FC3" Slave="1" Start="0" Length="2" />
              <Write Name="A" Id="2" Func="FC16" Slave="2" Start="10" Timeout="300" RepeatCount="0" />
            </ModbusRTUMaster>
            """);
        var x = rtu.ToXml();
        Assert.Equal(new[] { "Auto", "Write", "Write" }, x.Elements().Select(e => e.Name.LocalName));
        var b = new ModbusRTUMaster();
        b.FromXml(x);
        Assert.Equal(x.ToString(), b.ToXml().ToString());

        var tcp = Tcp("""
            <ModbusTCPMaster>
              <Write Name="B" Id="3" Func="FC15" Slave="255" Start="2" />
              <Auto Id="1" Func="FC4" Slave="1" Start="0" Length="2" />
              <Write Name="A" Id="2" Func="FC6" Slave="2" Start="10" Timeout="300" />
            </ModbusTCPMaster>
            """);
        var tx = tcp.ToXml();
        Assert.Equal(new[] { "Auto", "Write", "Write" }, tx.Elements().Select(e => e.Name.LocalName));
        var tb = new ModbusTCPMaster();
        tb.FromXml(tx);
        Assert.Equal(tx.ToString(), tb.ToXml().ToString());
    }

    [Fact]
    public void FromXml_AutoErrorBesideGoodWrite_KeepsOldTable()
    {
        var tcp = Tcp("""<ModbusTCPMaster><Write Name="Old" Id="9" Func="FC6" Slave="1" /></ModbusTCPMaster>""");
        Assert.Throws<FormatException>(() => tcp.FromXml(XElement.Parse(
            """<ModbusTCPMaster><Auto Id="1" Func="FC3" Slave="1" Start="0" Length="0" /><Write Name="New" Id="2" Func="FC6" Slave="1" /></ModbusTCPMaster>""")));
        Assert.True(tcp.ContainsNamedWrite("Old"));
        Assert.False(tcp.ContainsNamedWrite("New"));
    }

    [Fact]
    public void ToConfig_Writes_AreCopies_AndFromXmlWithoutWritesClears()
    {
        var tcp = Tcp(Xml("ModbusTCPMaster", "FC6"));
        tcp.ToConfig().Writes[0].Start = 999;
        tcp.NamedWrite("A", 1);
        Assert.Equal(100, tcp.TakeNextWork()!.StartAddress);
        tcp.FromXml(XElement.Parse("<ModbusTCPMaster />"));
        Assert.False(tcp.ContainsNamedWrite("A"));
    }

    [Fact]
    public void Schema_HasWrite_AndValidatesOwnOutput()
    {
        var rtu = Rtu("""<ModbusRTUMaster><Auto Id="1" Func="FC3" Slave="1" Start="0" Length="2" /><Write Name="A" Id="2" Func="FC6" Slave="1" Start="5" /></ModbusRTUMaster>""");
        var tcp = Tcp("""<ModbusTCPMaster><Auto Id="1" Func="FC3" Slave="1" Start="0" Length="2" /><Write Name="A" Id="2" Func="FC6" Slave="1" Start="5" /></ModbusTCPMaster>""");
        foreach (var (xsd, xml, root) in new[]
        {
            (ConfigSchema.Get<ModbusRTUMasterConfig>(), rtu.ToXml(), "ModbusRTUMaster"),
            (ConfigSchema.Get<ModbusTCPMasterConfig>(), tcp.ToXml(), "ModbusTCPMaster"),
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
            foreach (var name in new[] { "Name", "Id", "Func", "Slave" })
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
    public async Task Tcp_NamedWrite_Loopback()
    {
        var hold = new int[100];
        using var slave = new ModbusTCPSlave { LocalPort = 0, Slave = 1, HoldingRegisters = hold };
        slave.Start();
        using var master = new ModbusTCPMaster();
        master.FromXml(XElement.Parse($"""
            <ModbusTCPMaster RemoteIP="127.0.0.1" RemotePort="{slave.BoundPort}" Timeout="500" Interval="1">
              <Write Name="레시피" Id="30" Func="FC16" Slave="1" Start="10" />
            </ModbusTCPMaster>
            """));
        var tcs = new TaskCompletionSource<ModbusTCPMaster.MultiWordWriteEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        master.MultiWordWriteReceived += (s, e) => tcs.TrySetResult(e);
        master.Start();
        await TestUtil.Until(() => master.IsOpen);
        master.NamedWrite("레시피", [1, 2, 3]);
        var e = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(30, e.MessageID);
        Assert.Equal(new[] { 1, 2, 3 }, hold[10..13]);
    }
}
