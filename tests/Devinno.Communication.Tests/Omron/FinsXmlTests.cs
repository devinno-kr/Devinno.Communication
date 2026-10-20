using System.IO.Ports;
using System.Xml.Linq;
using System.Xml.Schema;
using Devinno.Communication.Omron.Data;
using Devinno.Communication.Omron.Ethernet;
using Devinno.Communication.Omron.Serial;

namespace Devinno.Communication.Tests.Omron;

public class FinsXmlTests
{
    #region 왕복
    [Fact]
    public void FinsUdp_RoundTrip_KeepsSettingsOrderAndKinds()
    {
        var a = new FinsUdp
        {
            RemoteIP = "10.0.0.7", RemotePort = 9601, LocalPort = 9600, DestinationNode = 7, SourceNode = 100,
            DestinationNetwork = 1, SourceNetwork = 2, MaxWordsPerRequest = 997, Timeout = 700, Interval = 5, AutoReconnect = false,
        };
        a.AutoReadBlock(1, "d100", 1000, timeout: 2000);
        a.AutoRead(2, ["100.05", "em1_2"]);
        a.ManualRead(9, ["D0"]);   // Manual은 저장 안 됨

        var x = a.ToXml();
        Assert.Equal(new[] { "ReadBlock", "Read" }, x.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("CIO100.05,E1_2", x.Element("Read")!.Attribute("Addresses")!.Value);
        var b = new FinsUdp();
        b.FromXml(x);
        Assert.Equal((a.RemoteIP, a.RemotePort, a.LocalPort, a.DestinationNode, a.SourceNode, a.DestinationNetwork, a.SourceNetwork, a.MaxWordsPerRequest, a.Timeout, a.Interval, a.AutoReconnect),
            (b.RemoteIP, b.RemotePort, b.LocalPort, b.DestinationNode, b.SourceNode, b.DestinationNetwork, b.SourceNetwork, b.MaxWordsPerRequest, b.Timeout, b.Interval, b.AutoReconnect));
        Assert.Equal(x.ToString(), b.ToXml().ToString());
        var w1 = b.TakeNextWork()!;
        Assert.Equal((1, FinsCommand.ReadBlock, "D100", 1000, (int?)2000), (w1.MessageID, w1.Command, w1.Items[0].Name, w1.Count, w1.Timeout));
        var w2 = b.TakeNextWork()!;
        Assert.Equal((2, FinsCommand.Read, (int?)null), (w2.MessageID, w2.Command, w2.Timeout));
        Assert.Equal(new[] { "CIO100.05", "E1_2" }, w2.Addresses);
    }

    [Fact]
    public void FinsTcp_RoundTrip()
    {
        var a = new FinsTcp { RemoteIP = "10.0.0.8", RemotePort = 9700, ClientNode = 12, MaxWordsPerRequest = 250, Timeout = 900, Interval = 3, AutoReconnect = false };
        a.AutoRead(5, ["W10.03"]);
        var x = a.ToXml();
        Assert.Equal("FinsTcp", x.Name.LocalName);
        var b = new FinsTcp();
        b.FromXml(x);
        Assert.Equal((a.RemoteIP, a.RemotePort, a.ClientNode, a.MaxWordsPerRequest, a.Timeout, a.Interval, a.AutoReconnect),
            (b.RemoteIP, b.RemotePort, b.ClientNode, b.MaxWordsPerRequest, b.Timeout, b.Interval, b.AutoReconnect));
        Assert.Equal(x.ToString(), b.ToXml().ToString());
        Assert.Equal(new[] { "W10.03" }, b.TakeNextWork()!.Addresses);
    }

    [Fact]
    public void FinsSerial_RoundTrip()
    {
        var a = new FinsSerial
        {
            Port = "COM7", Baudrate = 19200, Parity = Parity.None, DataBits = 8, StopBits = StopBits.One, UnitNumber = 3, ResponseWait = 2,
            MaxWordsPerRequest = 100, Timeout = 800, Interval = 4, AutoReconnect = true,
        };
        a.AutoReadBlock(1, "H0.00", 32);
        var x = a.ToXml();
        Assert.Equal("FinsSerial", x.Name.LocalName);
        var b = new FinsSerial();
        b.FromXml(x);
        Assert.Equal((a.Port, a.Baudrate, a.Parity, a.DataBits, a.StopBits, a.UnitNumber, a.ResponseWait, a.MaxWordsPerRequest, a.Timeout, a.Interval, a.AutoReconnect),
            (b.Port, b.Baudrate, b.Parity, b.DataBits, b.StopBits, b.UnitNumber, b.ResponseWait, b.MaxWordsPerRequest, b.Timeout, b.Interval, b.AutoReconnect));
        Assert.Equal(x.ToString(), b.ToXml().ToString());
        var w = b.TakeNextWork()!;
        Assert.Equal(("H0.00", 32), (w.Items[0].Name, w.Count));
    }
    #endregion

    #region 손으로 쓴 XML
    [Fact]
    public void HandWritten_Defaults_SpacesInAddresses_NoOptionalNodes()
    {
        var u = new FinsUdp { DestinationNode = 5, SourceNode = 6 };
        u.FromXml(XElement.Parse("""<FinsUdp RemoteIP="192.168.250.1"><Read Id="5" Addresses=" D100, w10.01 " /></FinsUdp>"""));
        Assert.Equal(("192.168.250.1", 9600, 0, (int?)null, (int?)null, 0, 0, 499, 1000, 10, true),
            (u.RemoteIP, u.RemotePort, u.LocalPort, u.DestinationNode, u.SourceNode, u.DestinationNetwork, u.SourceNetwork, u.MaxWordsPerRequest, u.Timeout, u.Interval, u.AutoReconnect));
        Assert.Equal(new[] { "D100", "W10.01" }, u.TakeNextWork()!.Addresses);
        Assert.Null(u.ToXml().Attribute("DestinationNode"));
        Assert.Null(u.ToXml().Attribute("SourceNode"));

        var t = new FinsTcp { ClientNode = 9 };
        t.FromXml(XElement.Parse("<FinsTcp />"));
        Assert.Equal(((int?)null, 9600, 499, true), (t.ClientNode, t.RemotePort, t.MaxWordsPerRequest, t.AutoReconnect));
        Assert.Null(t.ToXml().Attribute("ClientNode"));

        var s = new FinsSerial();
        s.FromXml(XElement.Parse("<FinsSerial Port=\"COM2\" />"));
        Assert.Equal(("COM2", 9600, Parity.Even, 7, StopBits.Two, 0, 0, 266, false), (s.Port, s.Baudrate, s.Parity, s.DataBits, s.StopBits, s.UnitNumber, s.ResponseWait, s.MaxWordsPerRequest, s.AutoReconnect));
    }
    #endregion

    #region 오류
    [Theory]
    [InlineData("""<FinsUdp><Read Id="3" Addresses="D0,T5" /></FinsUdp>""", "Read Id=\"3\"", "Addresses")]
    [InlineData("""<FinsUdp><Read Id="3" Addresses="" /></FinsUdp>""", "Read Id=\"3\"", "Addresses")]
    [InlineData("""<FinsUdp><ReadBlock Id="4" Address="D32767" Count="2" /></FinsUdp>""", "ReadBlock Id=\"4\"", "Count")]
    [InlineData("""<FinsUdp><ReadBlock Id="4" Address="X0" Count="2" /></FinsUdp>""", "ReadBlock Id=\"4\"", "Address")]
    [InlineData("""<FinsUdp><ReadBlock Id="4" Address="D0" /></FinsUdp>""", "Count", "필수")]
    [InlineData("""<FinsUdp><Read Addresses="D0" /></FinsUdp>""", "Id", "필수")]
    [InlineData("""<FinsUdp><Read Id="1" Addresses="D0" Timeout="0" /></FinsUdp>""", "Timeout", "1~60000")]
    [InlineData("""<FinsUdp RemotePort="0" />""", "RemotePort", "1~65535")]
    [InlineData("""<FinsUdp LocalPort="65536" />""", "LocalPort", "0~65535")]
    [InlineData("""<FinsUdp DestinationNode="255" />""", "DestinationNode", "0~254")]
    [InlineData("""<FinsUdp SourceNode="0" />""", "SourceNode", "1~254")]
    [InlineData("""<FinsUdp DestinationNetwork="128" />""", "DestinationNetwork", "0~127")]
    [InlineData("""<FinsUdp SourceNetwork="-1" />""", "SourceNetwork", "0~127")]
    [InlineData("""<FinsUdp MaxWordsPerRequest="998" />""", "MaxWordsPerRequest", "1~997")]
    [InlineData("""<FinsUdp Interval="-1" />""", "Interval", "0~60000")]
    [InlineData("""<FinsUdp RemoteIP="" />""", "RemoteIP", "비어")]
    [InlineData("""<FinsUdp DestNode="1" />""", "DestNode", "DestinationNode")]
    [InlineData("""<FinsUdp><Poke Id="1" /></FinsUdp>""", "Poke", "Read, ReadBlock")]
    public void FinsUdp_FromXml_Invalid_MessageIsPrecise_NothingChanged(string xml, string mustContain1, string mustContain2)
    {
        var c = new FinsUdp { RemoteIP = "10.0.0.9" };
        c.AutoRead(77, ["D0"]);
        var ex = Assert.Throws<FormatException>(() => c.FromXml(XElement.Parse(xml)));
        Assert.Contains(mustContain1, ex.Message);
        Assert.Contains(mustContain2, ex.Message);
        Assert.Equal("10.0.0.9", c.RemoteIP);
        Assert.True(c.ContainAutoID(77));
    }

    [Theory]
    [InlineData("""<FinsTcp ClientNode="255" />""", "ClientNode", "1~254")]
    [InlineData("""<FinsTcp MaxWordsPerRequest="0" />""", "MaxWordsPerRequest", "1~997")]
    [InlineData("""<FinsTcp><Read Id="2" Addresses="C0" /></FinsTcp>""", "Read Id=\"2\"", "카운터")]
    public void FinsTcp_FromXml_Invalid(string xml, string mustContain1, string mustContain2)
    {
        var c = new FinsTcp { RemoteIP = "10.0.0.9" };
        var ex = Assert.Throws<FormatException>(() => c.FromXml(XElement.Parse(xml)));
        Assert.Contains(mustContain1, ex.Message);
        Assert.Contains(mustContain2, ex.Message);
        Assert.Equal("10.0.0.9", c.RemoteIP);
    }

    [Theory]
    [InlineData("""<FinsSerial UnitNumber="32" />""", "UnitNumber", "0~31")]
    [InlineData("""<FinsSerial ResponseWait="16" />""", "ResponseWait", "0~15")]
    [InlineData("""<FinsSerial MaxWordsPerRequest="267" />""", "MaxWordsPerRequest", "1~266")]
    [InlineData("""<FinsSerial DataBits="9" />""", "DataBits", "5~8")]
    [InlineData("""<FinsSerial StopBits="None" />""", "StopBits", "One")]
    [InlineData("""<FinsSerial Parity="even" />""", "Parity", "Even")]
    [InlineData("""<FinsSerial Port="" />""", "Port", "비어")]
    [InlineData("""<FinsSerial><Read Id="1" Addresses="D0,D1,D2,D3,D4,D5,D6,D7,D8,D9,D10,D11,D12,D13,D14,D15,D16,D17,D18,D19,D20,D21,D22,D23,D24,D25,D26,D27,D28,D29,D30,D31,D32,D33,D34,D35,D36,D37,D38,D39,D40,D41,D42,D43,D44,D45,D46,D47,D48,D49,D50,D51,D52,D53,D54,D55,D56,D57,D58,D59,D60,D61,D62,D63,D64,D65,D66,D67,D68,D69,D70,D71,D72,D73,D74,D75,D76,D77,D78,D79,D80,D81,D82,D83,D84,D85,D86,D87,D88,D89,D90,D91,D92,D93,D94,D95,D96,D97,D98,D99,D100,D101,D102,D103,D104,D105,D106,D107,D108,D109,D110,D111,D112,D113,D114,D115,D116,D117,D118,D119,D120,D121,D122,D123,D124,D125,D126,D127,D128,D129,D130,D131,D132,D133,D134" /></FinsSerial>""", "Addresses", "1~134")]
    public void FinsSerial_FromXml_Invalid(string xml, string mustContain1, string mustContain2)
    {
        var c = new FinsSerial { Port = "COM9" };
        var ex = Assert.Throws<FormatException>(() => c.FromXml(XElement.Parse(xml)));
        Assert.Contains(mustContain1, ex.Message);
        Assert.Contains(mustContain2, ex.Message);
        Assert.Equal("COM9", c.Port);
    }

    [Fact]
    public void Read_ItemCount_Boundary_UdpTcp167_Serial134()
    {
        static string Names(int n) => string.Join(",", Enumerable.Range(0, n).Select(i => "D" + i));
        static XElement X(string root, int n) => XElement.Parse($"<{root}><Read Id=\"1\" Addresses=\"{Names(n)}\" /></{root}>");

        var u = new FinsUdp();
        u.FromXml(X("FinsUdp", 167));
        Assert.True(u.ContainAutoID(1));
        var ex = Assert.Throws<FormatException>(() => new FinsUdp().FromXml(X("FinsUdp", 168)));
        Assert.Contains("Addresses", ex.Message);
        Assert.Throws<FormatException>(() => new FinsTcp().FromXml(X("FinsTcp", 168)));

        var s = new FinsSerial();
        s.FromXml(X("FinsSerial", 134));
        Assert.True(s.ContainAutoID(1));
        ex = Assert.Throws<FormatException>(() => new FinsSerial().FromXml(X("FinsSerial", 135)));
        Assert.Contains("Addresses", ex.Message);
    }

    [Fact]
    public void FromXml_WrongRoot_Throws()
    {
        Assert.Throws<FormatException>(() => new FinsUdp().FromXml(XElement.Parse("<FinsTcp />")));
        Assert.Throws<FormatException>(() => new FinsTcp().FromXml(XElement.Parse("<FinsUdp />")));
        Assert.Throws<FormatException>(() => new FinsSerial().FromXml(XElement.Parse("<McSerial />")));
    }

    [Fact]
    public void FromXml_WhileRunning_Throws()
    {
        using var u = new FinsUdp { RemoteIP = "127.0.0.1", RemotePort = 1 };
        u.Start();
        Assert.Throws<InvalidOperationException>(() => u.FromXml(u.ToXml()));
        Assert.Throws<InvalidOperationException>(() => u.ApplyConfig(new FinsUdpConfig()));
        using var t = new FinsTcp { RemoteIP = "127.0.0.1", RemotePort = 1 };
        t.Start();
        Assert.Throws<InvalidOperationException>(() => t.FromXml(t.ToXml()));
        Assert.Throws<InvalidOperationException>(() => t.ApplyConfig(new FinsTcpConfig()));
    }

    [Fact]
    public void FinsSerial_FromXml_WhileRunning_Throws()
    {
        using var s = new FinsSerial { Port = "COM_DEVINNO_NONE", AutoReconnect = true };
        s.Start();
        Assert.Throws<InvalidOperationException>(() => s.FromXml(s.ToXml()));
        Assert.Throws<InvalidOperationException>(() => s.ApplyConfig(new FinsSerialConfig()));
    }
    #endregion

    #region 파일 / 스키마
    [Fact]
    public void SaveLoad_File()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fins-{Guid.NewGuid():N}.xml");
        try
        {
            var a = new FinsUdp { RemoteIP = "10.1.1.1", DestinationNode = 1 };
            a.AutoRead(1, ["A100"]);
            a.SaveXml(path);
            var b = new FinsUdp();
            b.LoadXml(path);
            Assert.Equal(a.ToXml().ToString(), b.ToXml().ToString());
        }
        finally { File.Delete(path); }
    }

    static XmlSchemaSet Compile(string xsd)
    {
        var set = new XmlSchemaSet();
        set.Add(XmlSchema.Read(new StringReader(xsd), null)!);
        set.Compile();
        return set;
    }

    static XmlSchemaAttribute Use(XmlSchemaSet set, string type, string attr)
    {
        var ct = (XmlSchemaComplexType)set.GlobalTypes.Values.Cast<XmlSchemaType>().Single(t => t.Name == type);
        return (XmlSchemaAttribute)ct.AttributeUses[new System.Xml.XmlQualifiedName(attr)]!;
    }

    [Fact]
    public void Schema_Compiles_ValidatesOwnOutput_MarksRequired()
    {
        var u = new FinsUdp { DestinationNode = 3 };
        u.AutoRead(1, ["D0"]);
        u.AutoReadBlock(2, "D0", 4, timeout: 50);
        var t = new FinsTcp();
        t.AutoRead(1, ["D0"]);
        var s = new FinsSerial();
        s.AutoReadBlock(1, "D0", 4);
        foreach (var (xsd, xml) in new[] { (ConfigSchema.Get<FinsUdpConfig>(), u.ToXml()), (ConfigSchema.Get<FinsTcpConfig>(), t.ToXml()), (ConfigSchema.Get<FinsSerialConfig>(), s.ToXml()) })
        {
            var set = Compile(xsd);
            var errors = new List<string>();
            new XDocument(xml).Validate(set, (_, e) => errors.Add(e.Message));
            Assert.Empty(errors);
            Assert.Equal(XmlSchemaUse.Required, Use(set, "FinsReadConfig", "Addresses").Use);
            Assert.Equal(XmlSchemaUse.Required, Use(set, "FinsReadConfig", "Id").Use);
            Assert.Equal(XmlSchemaUse.Required, Use(set, "FinsReadBlockConfig", "Address").Use);
            Assert.Equal(XmlSchemaUse.Required, Use(set, "FinsReadBlockConfig", "Count").Use);
            Assert.Equal(XmlSchemaUse.Optional, Use(set, "FinsReadBlockConfig", "Timeout").Use);
            Assert.Null(Use(set, "FinsReadBlockConfig", "Timeout").DefaultValue);
            foreach (var bad in new[] { "<Read Id=\"1\" />", "<ReadBlock Id=\"1\" Address=\"D0\" />", "<ReadBlock Id=\"1\" Count=\"1\" />" })
            {
                var errs = new List<string>();
                new XDocument(XElement.Parse($"<{xml.Name.LocalName}>{bad}</{xml.Name.LocalName}>")).Validate(set, (_, e) => errs.Add(e.Message));
                Assert.NotEmpty(errs);
            }
        }
        var udp = Compile(ConfigSchema.Get<FinsUdpConfig>());
        Assert.Null(Use(udp, "FinsUdpConfig", "DestinationNode").DefaultValue);
        Assert.Null(Use(udp, "FinsUdpConfig", "SourceNode").DefaultValue);
        Assert.Equal("9600", Use(udp, "FinsUdpConfig", "RemotePort").DefaultValue);
        Assert.Equal("499", Use(udp, "FinsUdpConfig", "MaxWordsPerRequest").DefaultValue);
        var tcp = Compile(ConfigSchema.Get<FinsTcpConfig>());
        Assert.Null(Use(tcp, "FinsTcpConfig", "ClientNode").DefaultValue);
        var ser = Compile(ConfigSchema.Get<FinsSerialConfig>());
        Assert.Equal("Even", Use(ser, "FinsSerialConfig", "Parity").DefaultValue);
        Assert.Equal("7", Use(ser, "FinsSerialConfig", "DataBits").DefaultValue);
        Assert.Equal("Two", Use(ser, "FinsSerialConfig", "StopBits").DefaultValue);
        Assert.Equal("266", Use(ser, "FinsSerialConfig", "MaxWordsPerRequest").DefaultValue);
    }
    #endregion
}
