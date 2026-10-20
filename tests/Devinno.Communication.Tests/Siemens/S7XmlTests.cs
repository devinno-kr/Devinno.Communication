using System.Xml.Linq;
using System.Xml.Schema;
using Devinno.Communication.Siemens;

namespace Devinno.Communication.Tests.Siemens;

public class S7XmlTests
{
    [Fact]
    public void RoundTrip_KeepsSettingsOrderAndKinds()
    {
        var a = new S7Ethernet
        {
            RemoteIP = "10.0.0.7", RemotePort = 1102, Rack = 1, Slot = 3, ConnectionType = S7ConnectionType.Basic,
            RemoteTsap = 0x2000, LocalTsap = 0x1000, PduSize = 480, Timeout = 700, Interval = 5, AutoReconnect = false,
        };
        a.AutoReadBlock(1, "db1.dbb10", 300, timeout: 2000);
        a.AutoRead(2, ["DB1.DBW0", "m10.0"]);
        a.ManualRead(9, ["MW0"]);   // Manual은 저장 안 됨

        var x = a.ToXml();
        Assert.Equal(new[] { "ReadBlock", "Read" }, x.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("8192", x.Attribute("RemoteTsap")!.Value);
        var b = new S7Ethernet();
        b.FromXml(x);
        Assert.Equal((a.RemoteIP, a.RemotePort, a.Rack, a.Slot, a.ConnectionType, a.RemoteTsap, a.LocalTsap, a.PduSize, a.Timeout, a.Interval, a.AutoReconnect),
            (b.RemoteIP, b.RemotePort, b.Rack, b.Slot, b.ConnectionType, b.RemoteTsap, b.LocalTsap, b.PduSize, b.Timeout, b.Interval, b.AutoReconnect));
        Assert.Equal(x.ToString(), b.ToXml().ToString());
        var w1 = b.TakeNextWork()!;
        Assert.Equal((1, S7Command.ReadBlock, "DB1.DBB10", 300, (int?)2000), (w1.MessageID, w1.Command, w1.Items[0].Name, w1.Count, w1.Timeout));
        var w2 = b.TakeNextWork()!;
        Assert.Equal((2, S7Command.Read), (w2.MessageID, w2.Command));
        Assert.Equal(new[] { "DB1.DBW0", "M10.0" }, w2.Addresses);
    }

    [Fact]
    public void HandWritten_Defaults_SpacesInAddresses_NoRemoteTsap()
    {
        var c = new S7Ethernet { RemoteTsap = 5 };
        c.FromXml(XElement.Parse("""<S7Ethernet RemoteIP="192.168.0.10"><Read Id="5" Addresses=" DB1.DBW0, mw2 " /></S7Ethernet>"""));
        Assert.Equal(("192.168.0.10", 102, 0, 1, S7ConnectionType.PG, (int?)null, 0x0100, 960, 1000, 10, true),
            (c.RemoteIP, c.RemotePort, c.Rack, c.Slot, c.ConnectionType, c.RemoteTsap, c.LocalTsap, c.PduSize, c.Timeout, c.Interval, c.AutoReconnect));
        Assert.Equal(new[] { "DB1.DBW0", "MW2" }, c.TakeNextWork()!.Addresses);
        Assert.Null(c.ToXml().Attribute("RemoteTsap"));
    }

    [Theory]
    [InlineData("""<S7Ethernet><Read Id="3" Addresses="DB1.DBW0,T5" /></S7Ethernet>""", "Read Id=\"3\"", "Addresses")]
    [InlineData("""<S7Ethernet><Read Id="3" Addresses="MW0,MW2,MW4,MW6,MW8,MW10,MW12,MW14,MW16,MW18,MW20,MW22,MW24,MW26,MW28,MW30,MW32,MW34,MW36,MW38" /></S7Ethernet>""", "Addresses", "240")]
    [InlineData("""<S7Ethernet><ReadBlock Id="4" Address="DB1.DBW0" Count="2" /></S7Ethernet>""", "ReadBlock Id=\"4\"", "Address")]
    [InlineData("""<S7Ethernet><ReadBlock Id="4" Address="DB1.DBB0" Count="65536" /></S7Ethernet>""", "Count", "1~65535")]
    [InlineData("""<S7Ethernet><ReadBlock Id="4" Address="DB1.DBB0" /></S7Ethernet>""", "Count", "필수")]
    [InlineData("""<S7Ethernet><Read Addresses="MW0" /></S7Ethernet>""", "Id", "필수")]
    [InlineData("""<S7Ethernet><Read Id="1" Addresses="MW0" Timeout="0" /></S7Ethernet>""", "Timeout", "1~60000")]
    [InlineData("""<S7Ethernet Rack="8" />""", "Rack", "0~7")]
    [InlineData("""<S7Ethernet Slot="32" />""", "Slot", "0~31")]
    [InlineData("""<S7Ethernet PduSize="1920" />""", "PduSize", "240~960")]
    [InlineData("""<S7Ethernet RemoteTsap="65536" />""", "RemoteTsap", "0~65535")]
    [InlineData("""<S7Ethernet LocalTsap="-1" />""", "LocalTsap", "0~65535")]
    [InlineData("""<S7Ethernet ConnectionType="pg" />""", "ConnectionType", "PG, OP, Basic")]
    [InlineData("""<S7Ethernet RemotePort="0" />""", "RemotePort", "1~65535")]
    [InlineData("""<S7Ethernet Slott="1" />""", "Slott", "Slot")]
    [InlineData("""<S7Ethernet><Foo Id="1" /></S7Ethernet>""", "Foo", "Read, ReadBlock")]
    public void FromXml_Invalid_MessageIsPrecise_NothingChanged(string xml, string mustContain1, string mustContain2)
    {
        var c = new S7Ethernet { RemoteIP = "10.0.0.9" };
        c.AutoRead(77, ["MW0"]);
        var ex = Assert.Throws<FormatException>(() => c.FromXml(XElement.Parse(xml)));
        Assert.Contains(mustContain1, ex.Message);
        Assert.Contains(mustContain2, ex.Message);
        Assert.Equal("10.0.0.9", c.RemoteIP);
        Assert.True(c.ContainAutoID(77));
    }

    [Fact]
    public void FromXml_WrongRoot_Throws()
        => Assert.Throws<FormatException>(() => new S7Ethernet().FromXml(XElement.Parse("<LsFEnet />")));

    [Fact]
    public void FromXml_WhileRunning_Throws()
    {
        using var c = new S7Ethernet { RemoteIP = "127.0.0.1", RemotePort = 1, AutoReconnect = true };
        c.Start();
        Assert.Throws<InvalidOperationException>(() => c.FromXml(c.ToXml()));
        Assert.Throws<InvalidOperationException>(() => c.ApplyConfig(new S7EthernetConfig()));
    }

    [Fact]
    public void SaveLoad_File()
    {
        var path = Path.Combine(Path.GetTempPath(), $"s7-{Guid.NewGuid():N}.xml");
        try
        {
            var a = new S7Ethernet { RemoteIP = "10.1.1.1", Slot = 2 };
            a.AutoRead(1, ["IB0"]);
            a.SaveXml(path);
            var b = new S7Ethernet();
            b.LoadXml(path);
            Assert.Equal(a.ToXml().ToString(), b.ToXml().ToString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Schema_Compiles_ValidatesOwnOutput_MarksRequired()
    {
        var a = new S7Ethernet { RemoteTsap = 0x0301 };
        a.AutoRead(1, ["MW0"]);
        a.AutoReadBlock(2, "MB0", 4, timeout: 50);
        var xsd = Devinno.Communication.ConfigSchema.Get<S7EthernetConfig>();
        var set = new XmlSchemaSet();
        set.Add(XmlSchema.Read(new StringReader(xsd), null)!);
        set.Compile();
        var errors = new List<string>();
        new XDocument(a.ToXml()).Validate(set, (_, e) => errors.Add(e.Message));
        Assert.Empty(errors);

        XmlSchemaAttribute Use(string type, string attr)
        {
            var ct = (XmlSchemaComplexType)set.GlobalTypes.Values.Cast<XmlSchemaType>().Single(t => t.Name == type);
            return (XmlSchemaAttribute)ct.AttributeUses[new System.Xml.XmlQualifiedName(attr)]!;
        }
        Assert.Equal(XmlSchemaUse.Required, Use("S7ReadConfig", "Addresses").Use);
        Assert.Equal(XmlSchemaUse.Required, Use("S7ReadConfig", "Id").Use);
        Assert.Equal(XmlSchemaUse.Required, Use("S7ReadBlockConfig", "Address").Use);
        Assert.Equal(XmlSchemaUse.Required, Use("S7ReadBlockConfig", "Count").Use);
        Assert.Equal(XmlSchemaUse.Optional, Use("S7ReadBlockConfig", "Timeout").Use);
        Assert.Null(Use("S7ReadBlockConfig", "Timeout").DefaultValue);
        Assert.Null(Use("S7EthernetConfig", "RemoteTsap").DefaultValue);
        Assert.Equal("102", Use("S7EthernetConfig", "RemotePort").DefaultValue);
        Assert.Equal("1", Use("S7EthernetConfig", "Slot").DefaultValue);
        Assert.Equal("PG", Use("S7EthernetConfig", "ConnectionType").DefaultValue);
        Assert.Equal("960", Use("S7EthernetConfig", "PduSize").DefaultValue);

        foreach (var bad in new[] { "<Read Id=\"1\" />", "<ReadBlock Id=\"1\" Address=\"MB0\" />", "<ReadBlock Id=\"1\" Count=\"1\" />" })
        {
            var errs = new List<string>();
            new XDocument(XElement.Parse($"<S7Ethernet>{bad}</S7Ethernet>")).Validate(set, (_, e) => errs.Add(e.Message));
            Assert.NotEmpty(errs);
        }
    }
}
