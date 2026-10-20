using System.Xml.Linq;
using System.Xml.Schema;
using Devinno.Communication.Siemens;

namespace Devinno.Communication.Tests.Siemens;

public class S7NamedWriteTests
{
    static S7Ethernet Load(string xml)
    {
        var s7 = new S7Ethernet();
        s7.FromXml(XElement.Parse(xml));
        s7.DiscardWritesWhileDisconnected = false;   // 미시작 객체의 Manual 쓰기를 TakeNextWork로 확인하는 테스트라 쓰기 버림을 끈다(FromXml이 기본값으로 되돌리므로 그 뒤에)
        return s7;
    }

    /// NamedWrite로 만든 작업과 Manual로 만든 작업을 (ID, Command, Param, Data, Items, Count, 재시도, 타임아웃)으로 비교한다.
    static void AssertSameWork(S7Ethernet.Work expected, S7Ethernet.Work actual)
    {
        Assert.Equal(expected.MessageID, actual.MessageID);
        Assert.Equal(expected.Command, actual.Command);
        Assert.Equal(expected.Param, actual.Param);
        Assert.Equal(expected.Data, actual.Data);
        Assert.Equal(expected.Items, actual.Items);
        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected.RepeatCount, actual.RepeatCount);
        Assert.Equal(expected.Timeout, actual.Timeout);
    }

    [Fact]
    public void Single_SameAsManualWrite()
    {
        var s7 = Load("""<S7Ethernet><Write Name="A" Id="5" Address="DB1.DBW10" /></S7Ethernet>""");
        s7.NamedWrite("A", 1234);
        var named = s7.TakeNextWork()!;
        s7.ManualWrite(5, [new S7Value("DB1.DBW10", 1234)]);
        AssertSameWork(s7.TakeNextWork()!, named);
        Assert.Equal(S7Command.Write, named.Command);
    }

    [Fact]
    public void Single_UsesXmlRepeatTimeout_AndArgumentsOverride()
    {
        var s7 = Load("""<S7Ethernet><Write Name="A" Id="5" Address="MW0" Timeout="500" RepeatCount="2" /></S7Ethernet>""");
        s7.NamedWrite("A", 1);
        var w = s7.TakeNextWork()!;
        Assert.Equal(((int?)2, (int?)500), (w.RepeatCount, w.Timeout));
        s7.NamedWrite("A", 1, repeatCount: 0, timeout: 100);
        w = s7.TakeNextWork()!;
        Assert.Equal(((int?)0, (int?)100), (w.RepeatCount, w.Timeout));
    }

    [Fact]
    public void Multi_Word_BecomesBigEndianBlock()
    {
        var s7 = Load("""<S7Ethernet><Write Name="A" Id="5" Address="DB1.DBW10" /></S7Ethernet>""");
        s7.NamedWrite("A", [0x1234, -1]);
        var named = s7.TakeNextWork()!;
        s7.ManualWriteBlock(5, "DB1.DBB10", [0x12, 0x34, 0xFF, 0xFF]);
        AssertSameWork(s7.TakeNextWork()!, named);
        Assert.Equal(S7Command.WriteBlock, named.Command);
    }

    [Fact]
    public void Multi_DWord_M()
    {
        var s7 = Load("""<S7Ethernet><Write Name="A" Id="5" Address="MD4" /></S7Ethernet>""");
        s7.NamedWrite("A", [1]);
        var named = s7.TakeNextWork()!;
        s7.ManualWriteBlock(5, "MB4", [0, 0, 0, 1]);
        AssertSameWork(s7.TakeNextWork()!, named);
    }

    [Fact]
    public void Multi_Byte_Q_AndGermanAliases()
    {
        var s7 = Load("""<S7Ethernet><Write Name="Q" Id="1" Address="QB2" /><Write Name="E" Id="2" Address="EW0" /><Write Name="A" Id="3" Address="AD8" /></S7Ethernet>""");
        s7.NamedWrite("Q", [1, 255]);
        var q = s7.TakeNextWork()!;
        s7.ManualWriteBlock(1, "QB2", [1, 255]);
        AssertSameWork(s7.TakeNextWork()!, q);

        s7.NamedWrite("E", [0x0102]);
        var e = s7.TakeNextWork()!;
        s7.ManualWriteBlock(2, "IB0", [1, 2]);
        AssertSameWork(s7.TakeNextWork()!, e);

        s7.NamedWrite("A", [0x01020304, 2]);
        var a = s7.TakeNextWork()!;
        s7.ManualWriteBlock(3, "QB8", [1, 2, 3, 4, 0, 0, 0, 2]);
        AssertSameWork(s7.TakeNextWork()!, a);
    }

    [Fact]
    public void Multi_Bit_Throws()
    {
        var s7 = Load("""<S7Ethernet><Write Name="A" Id="5" Address="DB1.DBX0.0" /></S7Ethernet>""");
        Assert.Throws<ArgumentException>(() => s7.NamedWrite("A", [1, 0]));
        Assert.Null(s7.TakeNextWork());
    }

    [Theory]
    [InlineData("MB0", 256)]
    [InlineData("MB0", -1)]
    [InlineData("MW0", 65536)]
    [InlineData("MW0", -32769)]
    [InlineData("MD0", 4294967296)]
    [InlineData("MD0", -2147483649)]
    public void Multi_OutOfRange_Throws(string address, long value)
    {
        var s7 = Load($"""<S7Ethernet><Write Name="A" Id="5" Address="{address}" /></S7Ethernet>""");
        Assert.ThrowsAny<ArgumentException>(() => s7.NamedWrite("A", [0, value]));
        Assert.Null(s7.TakeNextWork());
    }

    [Fact]
    public void Multi_Boundaries_Ok()
    {
        var s7 = Load("""<S7Ethernet><Write Name="W" Id="1" Address="MW0" /><Write Name="D" Id="2" Address="MD0" /></S7Ethernet>""");
        s7.NamedWrite("W", [-32768, 65535]);
        Assert.Equal(new byte[] { 0x80, 0x00, 0xFF, 0xFF }, s7.TakeNextWork()!.Data);
        s7.NamedWrite("D", [-2147483648L, 4294967295L]);
        Assert.Equal(new byte[] { 0x80, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF }, s7.TakeNextWork()!.Data);
    }

    [Fact]
    public void Multi_EmptyList_Throws()
    {
        var s7 = Load("""<S7Ethernet><Write Name="A" Id="5" Address="MW0" /></S7Ethernet>""");
        Assert.ThrowsAny<ArgumentException>(() => s7.NamedWrite("A", new List<long>()));
    }

    [Fact]
    public void NamedWrite_Unknown_Throws_AndContains()
    {
        var s7 = Load("""<S7Ethernet><Write Name="A" Id="1" Address="MW0" /></S7Ethernet>""");
        var e1 = Assert.Throws<ArgumentException>(() => s7.NamedWrite("B", 1));
        Assert.Contains("B", e1.Message);
        var e2 = Assert.Throws<ArgumentException>(() => s7.NamedWrite("a", [1]));
        Assert.Contains("a", e2.Message);
        Assert.True(s7.ContainsNamedWrite("A"));
        Assert.False(s7.ContainsNamedWrite("a"));
    }

    [Theory]
    [InlineData("DB0.DBW0")]
    [InlineData("T1")]
    [InlineData("")]
    [InlineData("MB")]
    public void FromXml_BadAddress_ThrowsAndKeepsOldTable(string address)
    {
        var s7 = Load("""<S7Ethernet><Write Name="Old" Id="9" Address="MW0" /></S7Ethernet>""");
        var ex = Assert.Throws<FormatException>(() => s7.FromXml(XElement.Parse(
            $"""<S7Ethernet><Write Name="A" Id="1" Address="{address}" /></S7Ethernet>""")));
        Assert.Contains("Write Name=\"A\"", ex.Message);
        Assert.Contains("Address", ex.Message);
        Assert.True(s7.ContainsNamedWrite("Old"));
        Assert.False(s7.ContainsNamedWrite("A"));
    }

    [Fact]
    public void FromXml_AutoErrorBesideGoodWrite_KeepsOldTable()
    {
        var s7 = Load("""<S7Ethernet><Write Name="Old" Id="9" Address="MW0" /></S7Ethernet>""");
        Assert.Throws<FormatException>(() => s7.FromXml(XElement.Parse(
            """<S7Ethernet><Read Id="1" Addresses="T1" /><Write Name="New" Id="2" Address="MW0" /></S7Ethernet>""")));
        Assert.True(s7.ContainsNamedWrite("Old"));
        Assert.False(s7.ContainsNamedWrite("New"));
    }

    [Fact]
    public void RoundTrip_KeepsWritesAfterAutos()
    {
        var s7 = Load("""
            <S7Ethernet>
              <Write Name="B" Id="3" Address="db1.dbx0.1" RepeatCount="1" />
              <Read Id="1" Addresses="MW0" />
              <Write Name="A" Id="2" Address="MD4" Timeout="300" />
            </S7Ethernet>
            """);
        var x = s7.ToXml();
        Assert.Equal(new[] { "Read", "Write", "Write" }, x.Elements().Select(e => e.Name.LocalName));
        var b = new S7Ethernet();
        b.FromXml(x);
        Assert.Equal(x.ToString(), b.ToXml().ToString());
    }

    [Fact]
    public void ToConfig_Writes_AreCopies()
    {
        var s7 = Load("""<S7Ethernet><Write Name="A" Id="1" Address="MW0" /></S7Ethernet>""");
        s7.ToConfig().Writes[0].Address = "MW99";
        s7.NamedWrite("A", 1);
        Assert.Equal("MW0", s7.TakeNextWork()!.Items[0].Name);
    }

    [Fact]
    public void FromXml_WithoutWrites_ClearsTable()
    {
        var s7 = Load("""<S7Ethernet><Write Name="A" Id="1" Address="MW0" /></S7Ethernet>""");
        s7.FromXml(XElement.Parse("<S7Ethernet />"));
        Assert.False(s7.ContainsNamedWrite("A"));
    }

    [Fact]
    public void Schema_HasWrite_AndValidatesOwnOutput()
    {
        var s7 = Load("""<S7Ethernet><Read Id="1" Addresses="MW0" /><Write Name="A" Id="2" Address="DB1.DBW0" Timeout="300" /></S7Ethernet>""");
        var xsd = ConfigSchema.Get<S7EthernetConfig>();
        Assert.Contains("name=\"Write\"", xsd);
        var set = new XmlSchemaSet();
        set.Add(XmlSchema.Read(new StringReader(xsd), null)!);
        set.Compile();
        var errors = new List<string>();
        new XDocument(s7.ToXml()).Validate(set, (_, e) => errors.Add(e.Message));
        Assert.Empty(errors);

        var element = (XmlSchemaElement)set.GlobalElements.Values.Cast<XmlSchemaObject>().Single(o => ((XmlSchemaElement)o).Name == "S7Ethernet");
        var particle = (XmlSchemaParticle)((XmlSchemaComplexType)element.ElementSchemaType!).ContentTypeParticle;
        var writeType = (XmlSchemaComplexType)FindElement(particle, "Write").ElementSchemaType!;
        foreach (var name in new[] { "Name", "Id", "Address" })
            Assert.Equal(XmlSchemaUse.Required, ((XmlSchemaAttribute)writeType.AttributeUses[new System.Xml.XmlQualifiedName(name)]!).Use);
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
