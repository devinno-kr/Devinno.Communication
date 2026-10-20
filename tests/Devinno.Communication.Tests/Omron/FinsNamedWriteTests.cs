using System.Xml.Linq;
using System.Xml.Schema;
using Devinno.Communication.Omron.Data;
using Devinno.Communication.Omron.Ethernet;
using Devinno.Communication.Omron.Serial;

namespace Devinno.Communication.Tests.Omron;

public class FinsNamedWriteTests
{
    public enum Kind { Udp, Tcp, Serial }

    record Taken(int Id, FinsCommand Cmd, string Addr, int Count, long[] Values, int? Repeat, int? Timeout);

    /// 세 클래스를 같은 모양으로 다루기 위한 얇은 어댑터
    sealed record Plc(string Root, Action<XElement> FromXml, Func<XElement> ToXml,
        Action<string, long, int?, int?> Named, Action<string, IReadOnlyList<long>, int?, int?> NamedBlock,
        Func<string, bool> Contains, Action<int, string, long> Manual, Action<int, string, IReadOnlyList<long>> ManualBlock,
        Func<Taken> Take, Func<string> Xsd);

    static Plc Make(Kind kind)
    {
        switch (kind)
        {
            case Kind.Udp:
                {
                    var p = new FinsUdp();
                    return new Plc("FinsUdp", p.FromXml, p.ToXml, (n, v, r, t) => p.NamedWrite(n, v, r, t), (n, v, r, t) => p.NamedWrite(n, v, r, t),
                        p.ContainsNamedWrite, (i, a, v) => p.ManualWrite(i, a, v), (i, a, v) => p.ManualWriteBlock(i, a, v),
                        () => { var w = p.TakeNextWork()!; return new Taken(w.MessageID, w.Command, w.Items[0].Name, w.Count, w.Values, w.RepeatCount, w.Timeout); },
                        ConfigSchema.Get<FinsUdpConfig>);
                }
            case Kind.Tcp:
                {
                    var p = new FinsTcp();
                    return new Plc("FinsTcp", p.FromXml, p.ToXml, (n, v, r, t) => p.NamedWrite(n, v, r, t), (n, v, r, t) => p.NamedWrite(n, v, r, t),
                        p.ContainsNamedWrite, (i, a, v) => p.ManualWrite(i, a, v), (i, a, v) => p.ManualWriteBlock(i, a, v),
                        () => { var w = p.TakeNextWork()!; return new Taken(w.MessageID, w.Command, w.Items[0].Name, w.Count, w.Values, w.RepeatCount, w.Timeout); },
                        ConfigSchema.Get<FinsTcpConfig>);
                }
            default:
                {
                    var p = new FinsSerial();
                    return new Plc("FinsSerial", p.FromXml, p.ToXml, (n, v, r, t) => p.NamedWrite(n, v, r, t), (n, v, r, t) => p.NamedWrite(n, v, r, t),
                        p.ContainsNamedWrite, (i, a, v) => p.ManualWrite(i, a, v), (i, a, v) => p.ManualWriteBlock(i, a, v),
                        () => { var w = p.TakeNextWork()!; return new Taken(w.MessageID, w.Command, w.Items[0].Name, w.Count, w.Values, w.RepeatCount, w.Timeout); },
                        ConfigSchema.Get<FinsSerialConfig>);
                }
        }
    }

    static Plc Load(Kind kind, string inner)
    {
        var p = Make(kind);
        // 미시작 객체의 Manual 쓰기를 Take로 확인하는 테스트라 쓰기 버림을 끈다
        p.FromXml(XElement.Parse($"<{p.Root} DiscardWritesWhileDisconnected='false'>{inner}</{p.Root}>"));
        return p;
    }

    public static IEnumerable<object[]> Kinds() => [[Kind.Udp], [Kind.Tcp], [Kind.Serial]];

    [Theory, MemberData(nameof(Kinds))]
    public void Single_SameAsManualWrite(Kind kind)
    {
        foreach (var (addr, value) in new[] { ("D100", 1234L), ("CIO0.05", 1L) })
        {
            var p = Load(kind, $"""<Write Name="A" Id="7" Address="{addr}" />""");
            p.Named("A", value, null, null);
            var named = p.Take();
            p.Manual(7, addr, value);
            var manual = p.Take();
            Assert.Equal((manual.Id, manual.Cmd, manual.Count), (named.Id, named.Cmd, named.Count));
            Assert.Equal(manual.Values, named.Values);
            Assert.Equal(manual.Addr, named.Addr);
        }
    }

    [Theory, MemberData(nameof(Kinds))]
    public void Multi_SameAsManualWriteBlock(Kind kind)
    {
        foreach (var (addr, values) in new[] { ("D100", new long[] { 1, 2 }), ("CIO0.05", new long[] { 1, 0 }) })
        {
            var p = Load(kind, $"""<Write Name="A" Id="8" Address="{addr}" />""");
            p.NamedBlock("A", values, null, null);
            var named = p.Take();
            p.ManualBlock(8, addr, values);
            var manual = p.Take();
            Assert.Equal((manual.Id, manual.Cmd, manual.Count), (named.Id, named.Cmd, named.Count));
            Assert.Equal(manual.Values, named.Values);
            Assert.Equal(manual.Addr, named.Addr);
            Assert.Equal(2, named.Count);
        }
    }

    [Theory, MemberData(nameof(Kinds))]
    public void NamedWrite_UsesXmlRepeatTimeout_AndArgumentsOverride(Kind kind)
    {
        var p = Load(kind, """<Write Name="A" Id="1" Address="D0" Timeout="500" RepeatCount="2" />""");
        p.Named("A", 1, null, null);
        var w = p.Take();
        Assert.Equal(((int?)2, (int?)500), (w.Repeat, w.Timeout));
        p.Named("A", 1, 0, 100);
        w = p.Take();
        Assert.Equal(((int?)0, (int?)100), (w.Repeat, w.Timeout));

        var q = Load(kind, """<Write Name="A" Id="1" Address="D0" />""");
        q.Named("A", 1, null, null);
        w = q.Take();
        Assert.Null(w.Repeat);
        Assert.Null(w.Timeout);
    }

    [Theory, MemberData(nameof(Kinds))]
    public void NamedWrite_Unknown_Throws_AndContains(Kind kind)
    {
        var p = Load(kind, """<Write Name="A" Id="1" Address="D0" />""");
        var ex = Assert.Throws<ArgumentException>(() => p.Named("a", 1, null, null));
        Assert.Contains("a", ex.Message);
        Assert.Throws<ArgumentException>(() => p.NamedBlock("B", [1], null, null));
        Assert.True(p.Contains("A"));
        Assert.False(p.Contains("a"));
    }

    [Theory, MemberData(nameof(Kinds))]
    public void NamedWrite_BadValue_Throws(Kind kind)
    {
        var p = Load(kind, """<Write Name="B" Id="1" Address="CIO0.05" />""");
        Assert.ThrowsAny<ArgumentException>(() => p.Named("B", 2, null, null));
    }

    [Theory]
    [InlineData(Kind.Udp, "X100")]
    [InlineData(Kind.Udp, "D40000")]
    [InlineData(Kind.Tcp, "X100")]
    [InlineData(Kind.Tcp, "D40000")]
    [InlineData(Kind.Serial, "X100")]
    [InlineData(Kind.Serial, "D40000")]
    public void FromXml_BadAddress_Throws_AndKeepsOldTable(Kind kind, string address)
    {
        var p = Load(kind, """<Write Name="Old" Id="9" Address="D0" />""");
        var bad = XElement.Parse($"""<{p.Root}><Write Name="A" Id="1" Address="{address}" /></{p.Root}>""");
        var ex = Assert.Throws<FormatException>(() => p.FromXml(bad));
        Assert.Contains("Write Name=\"A\"", ex.Message);
        Assert.Contains("Address", ex.Message);
        Assert.True(p.Contains("Old"));
        Assert.False(p.Contains("A"));
    }

    [Theory, MemberData(nameof(Kinds))]
    public void FromXml_AutoErrorBesideGoodWrite_KeepsOldTable(Kind kind)
    {
        var p = Load(kind, """<Write Name="Old" Id="9" Address="D0" />""");
        var bad = XElement.Parse($"""<{p.Root}><Read Id="1" Addresses="X1" /><Write Name="New" Id="2" Address="D0" /></{p.Root}>""");
        Assert.Throws<FormatException>(() => p.FromXml(bad));
        Assert.True(p.Contains("Old"));
        Assert.False(p.Contains("New"));
    }

    [Theory, MemberData(nameof(Kinds))]
    public void RoundTrip_KeepsWritesAfterAutos(Kind kind)
    {
        var p = Load(kind, """
            <Write Name="B" Id="3" Address="CIO0.05" RepeatCount="1" />
            <Read Id="1" Addresses="D0" />
            <Write Name="A" Id="2" Address="D5" Timeout="300" />
            """);
        var x = p.ToXml();
        Assert.Equal(new[] { "Read", "Write", "Write" }, x.Elements().Select(e => e.Name.LocalName));
        var q = Make(kind);
        q.FromXml(x);
        Assert.Equal(x.ToString(), q.ToXml().ToString());
    }

    [Theory, MemberData(nameof(Kinds))]
    public void FromXml_WithoutWrites_ClearsTable(Kind kind)
    {
        var p = Load(kind, """<Write Name="A" Id="1" Address="D0" />""");
        p.FromXml(XElement.Parse($"<{p.Root} />"));
        Assert.False(p.Contains("A"));
    }

    [Theory, MemberData(nameof(Kinds))]
    public void Schema_HasWrite_AndValidatesOwnOutput(Kind kind)
    {
        var p = Load(kind, """<Read Id="1" Addresses="D0" /><Write Name="A" Id="2" Address="D5" Timeout="300" />""");
        var xsd = p.Xsd();
        Assert.Contains("name=\"Write\"", xsd);
        var set = new XmlSchemaSet();
        set.Add(XmlSchema.Read(new StringReader(xsd), null)!);
        set.Compile();
        var errors = new List<string>();
        new XDocument(p.ToXml()).Validate(set, (_, e) => errors.Add(e.Message));
        Assert.Empty(errors);

        var element = (XmlSchemaElement)set.GlobalElements.Values.Cast<XmlSchemaObject>().Single(o => ((XmlSchemaElement)o).Name == p.Root);
        var rootType = (XmlSchemaComplexType)element.ElementSchemaType!;
        var write = FindElement((XmlSchemaParticle)rootType.ContentTypeParticle, "Write");
        var writeType = (XmlSchemaComplexType)write.ElementSchemaType!;
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
