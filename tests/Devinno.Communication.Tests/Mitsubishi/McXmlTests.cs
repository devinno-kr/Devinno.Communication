using System.Text;
using System.Xml.Linq;
using System.Xml.Schema;
using Devinno.Communication.Mitsubishi.Data;
using Devinno.Communication.Mitsubishi.Ethernet;
using Devinno.Communication.Mitsubishi.Serial;

namespace Devinno.Communication.Tests.Mitsubishi;

public class McXmlTests
{
    [Fact]
    public void Serial_RoundTrip_KeepsOrderKindsAndFrameSettings()
    {
        var a = new McSerial
        {
            Port = "COM5", Baudrate = 19200, Timeout = 700, Interval = 5, AutoReconnect = true,
            Format = McSerialFormat.Format4, SumCheck = false, MessageWait = 3, XYNotation = McXYNotation.Hex,
        };
        a.AutoWR(1, 2, "D200", 10, timeout: 300);
        a.AutoBR(2, 2, "X1F", 4);
        a.ManualWW(9, 1, "D0", [1]);   // Manual은 저장 안 됨

        var x = a.ToXml();
        Assert.Equal(new[] { "WR", "BR" }, x.Elements().Select(e => e.Name.LocalName));
        var b = new McSerial();
        b.FromXml(x);
        Assert.Equal((a.Port, a.Baudrate, a.Timeout, a.Interval, a.AutoReconnect), (b.Port, b.Baudrate, b.Timeout, b.Interval, b.AutoReconnect));
        Assert.Equal((a.Format, a.SumCheck, a.MessageWait, a.XYNotation), (b.Format, b.SumCheck, b.MessageWait, b.XYNotation));
        Assert.Equal(x.ToString(), b.ToXml().ToString());
        var w1 = b.TakeNextWork()!;
        var w2 = b.TakeNextWork()!;
        Assert.Equal((1, 2), (w1.MessageID, w2.MessageID));
        Assert.Equal("02FFBR3X001F04\r\n", Encoding.ASCII.GetString(w2.Request[1..]));   // 불러온 설정(형식 4, 섬 없음, 대기 3, 16진)으로 프레임 생성
    }

    [Fact]
    public void Serial_HandWritten_WithDefaults()
    {
        var c = new McSerial();
        c.FromXml(XElement.Parse("""<McSerial Port="COM3"><BR Id="5" Slave="0" Device="m0" Count="16" /></McSerial>"""));
        Assert.Equal(("COM3", 9600, 1000, McSerialFormat.Format1, true, 0, McXYNotation.Auto),
            (c.Port, c.Baudrate, c.Timeout, c.Format, c.SumCheck, c.MessageWait, c.XYNotation));
        var w = c.TakeNextWork()!;
        Assert.Equal(("M0", McCommand.BR, 16), (w.Device, w.Command, w.Count));
    }

    [Fact]
    public void Ethernet_RoundTrip_E1()
    {
        var a = new McEthernet { RemoteIP = "10.0.0.7", RemotePort = 5002, Frame = McFrame.E1, AutoReconnect = false };
        a.AutoWR(1, "D10", 4);
        a.AutoBR(2, "X17", 8, timeout: 200);
        var b = new McEthernet();
        b.FromXml(a.ToXml());
        Assert.Equal(("10.0.0.7", 5002, McFrame.E1, false), (b.RemoteIP, b.RemotePort, b.Frame, b.AutoReconnect));
        Assert.Equal(a.ToXml().ToString(), b.ToXml().ToString());
        b.TakeNextWork();
        var w = b.TakeNextWork()!;
        Assert.Equal((McFrame.E1, 15), (w.Frame, w.Device.Number));   // E1 Auto = 8진수
    }

    [Theory]
    [InlineData("""<McSerial><WR Id="1" Slave="40" Device="D0" Count="1" /></McSerial>""", "Slave", "0~31")]
    [InlineData("""<McSerial><WR Id="1" Slave="0" Device="SM400" Count="1" /></McSerial>""", "Device", "SM400")]
    [InlineData("""<McSerial><WR Id="1" Slave="0" Device="D0" Count="65" /></McSerial>""", "Count", "1~64")]
    [InlineData("""<McSerial><BR Id="1" Slave="0" Device="D0" Count="1" /></McSerial>""", "Device", "비트")]
    [InlineData("""<McSerial><BR Id="1" Device="M0" Count="1" /></McSerial>""", "Slave", "필수")]
    [InlineData("""<McSerial><WR Id="1" Slave="0" Count="1" /></McSerial>""", "Device", "필수")]
    [InlineData("""<McSerial MessageWait="16" />""", "MessageWait", "0~15")]
    [InlineData("""<McSerial Format="Format2" />""", "Format", "Format1, Format4")]
    [InlineData("""<McSerial XYNotation="octal" />""", "XYNotation", "Octal")]
    [InlineData("""<McSerial><BW Id="1" /></McSerial>""", "BW", "BR, WR")]
    [InlineData("""<McSerial Timout="1" />""", "Timout", "Timeout")]
    [InlineData("""<McSerial XYNotation="Octal"><BR Id="3" Slave="0" Device="X8" Count="1" /></McSerial>""", "BR Id=\"3\"", "X8")]
    public void Serial_FromXml_Invalid_MessageIsPrecise(string xml, string mustContain1, string mustContain2)
    {
        var c = new McSerial { Port = "COM9" };
        var ex = Assert.Throws<FormatException>(() => c.FromXml(XElement.Parse(xml)));
        Assert.Contains(mustContain1, ex.Message);
        Assert.Contains(mustContain2, ex.Message);
        Assert.Equal("COM9", c.Port);
    }

    [Theory]
    [InlineData("""<McEthernet Frame="E1"><WR Id="3" Device="SM400" Count="1" /></McEthernet>""", "WR Id=\"3\"", "Device")]
    [InlineData("""<McEthernet><BR Id="4" Device="M0" Count="3585" /></McEthernet>""", "BR Id=\"4\"", "1~3584")]
    [InlineData("""<McEthernet Frame="E2" />""", "Frame", "E3, E1")]
    [InlineData("""<McEthernet RemotePort="0" />""", "RemotePort", "1~65535")]
    [InlineData("""<McSerial />""", "McEthernet", "McSerial")]
    public void Ethernet_FromXml_Invalid_MessageIsPrecise(string xml, string mustContain1, string mustContain2)
    {
        var c = new McEthernet { RemoteIP = "10.1.1.1" };
        var ex = Assert.Throws<FormatException>(() => c.FromXml(XElement.Parse(xml)));
        Assert.Contains(mustContain1, ex.Message);
        Assert.Contains(mustContain2, ex.Message);
        Assert.Equal("10.1.1.1", c.RemoteIP);
    }

    [Fact]
    public void FromXml_WhileRunning_Throws()
    {
        using var c = new McSerial { Port = "COM_NOT_EXIST", AutoReconnect = true };
        c.AutoWR(1, 0, "D0", 2);
        c.Start();
        var other = new McSerial { Port = "COM99" };
        other.AutoBR(5, 3, "M0", 8);
        other.AutoWR(6, 4, "D5", 1);
        Assert.Throws<InvalidOperationException>(() => c.FromXml(other.ToXml()));
        Assert.Equal("COM_NOT_EXIST", c.Port);
        Assert.Equal(new[] { "WR" }, c.ToXml().Elements().Select(e => e.Name.LocalName));
        Assert.Equal("1", c.ToXml().Elements().Single().Attribute("Id")!.Value);
    }

    [Fact]
    public void Serial_RoundTrip_KeepsParityDataBitsStopBits()
    {
        var a = new McSerial { Port = "COM7", Parity = System.IO.Ports.Parity.Even, DataBits = 7, StopBits = System.IO.Ports.StopBits.Two };
        var b = new McSerial();
        b.FromXml(a.ToXml());
        Assert.Equal((System.IO.Ports.Parity.Even, 7, System.IO.Ports.StopBits.Two), (b.Parity, b.DataBits, b.StopBits));
        Assert.Equal(a.ToXml().ToString(), b.ToXml().ToString());
    }

    [Fact]
    public void Ethernet_FromXml_WhileRunning_Throws_AndKeepsState()
    {
        using var c = new McEthernet { RemoteIP = "127.0.0.1", RemotePort = 1, AutoReconnect = true };
        c.AutoWR(1, "D0", 2);
        c.Start();
        var other = new McEthernet { RemoteIP = "10.9.9.9" };
        other.AutoBR(5, "M0", 8);
        other.AutoWR(6, "D5", 1);
        Assert.Throws<InvalidOperationException>(() => c.FromXml(other.ToXml()));
        Assert.Equal(("127.0.0.1", 1), (c.RemoteIP, c.RemotePort));
        Assert.Equal(new[] { "WR" }, c.ToXml().Elements().Select(e => e.Name.LocalName));
        Assert.Equal("1", c.ToXml().Elements().Single().Attribute("Id")!.Value);
    }

    [Fact]
    public void Schemas_Compile_AndValidateOwnOutput()
    {
        var serial = new McSerial();
        serial.AutoBR(1, 0, "M0", 8);
        serial.AutoWR(2, 0, "D0", 2, timeout: 50);
        var eth = new McEthernet();
        eth.AutoWR(1, "D0", 2);

        foreach (var (xsd, xml) in new[]
        {
            (Devinno.Communication.ConfigSchema.Get<McSerialConfig>(), serial.ToXml()),
            (Devinno.Communication.ConfigSchema.Get<McEthernetConfig>(), eth.ToXml()),
        })
        {
            var set = new XmlSchemaSet();
            set.Add(XmlSchema.Read(new StringReader(xsd), null)!);
            set.Compile();
            var errors = new List<string>();
            new XDocument(xml).Validate(set, (_, e) => errors.Add(e.Message));
            Assert.Empty(errors);
        }

        var sxsd = Devinno.Communication.ConfigSchema.Get<McSerialConfig>();
        Assert.Contains("name=\"BR\"", sxsd);
        Assert.Contains("name=\"WR\"", sxsd);
        Assert.Contains("default=\"Format1\"", sxsd);
        Assert.Contains("default=\"5000\"", Devinno.Communication.ConfigSchema.Get<McEthernetConfig>());
    }

    [Theory]
    [InlineData(typeof(McSerialConfig))]
    [InlineData(typeof(McEthernetConfig))]
    public void Schemas_AutoItems_MarkRequiredAttributes(Type configType)
    {
        bool serial = configType == typeof(McSerialConfig);
        var xsd = serial
            ? Devinno.Communication.ConfigSchema.Get<McSerialConfig>()
            : Devinno.Communication.ConfigSchema.Get<McEthernetConfig>();
        var set = new XmlSchemaSet();
        set.Add(XmlSchema.Read(new StringReader(xsd), null)!);
        set.Compile();
        var prefix = serial ? "McSerial" : "McEthernet";
        XmlSchemaAttribute Use(string type, string attr)
        {
            var ct = (XmlSchemaComplexType)set.GlobalTypes.Values.Cast<XmlSchemaType>().Single(t => t.Name == prefix + type);
            return (XmlSchemaAttribute)ct.AttributeUses[new System.Xml.XmlQualifiedName(attr)]!;
        }

        foreach (var type in new[] { "BrConfig", "WrConfig" })
        {
            Assert.Equal(XmlSchemaUse.Required, Use(type, "Id").Use);
            Assert.Equal(XmlSchemaUse.Required, Use(type, "Device").Use);
            Assert.Equal(XmlSchemaUse.Required, Use(type, "Count").Use);
            Assert.Equal(XmlSchemaUse.Optional, Use(type, "Timeout").Use);
            Assert.Null(Use(type, "Timeout").DefaultValue);
            if (serial) Assert.Equal(XmlSchemaUse.Required, Use(type, "Slave").Use);
        }

        // 필수 속성이 빠진 Auto 항목은 XSD 검증에서도 걸린다
        var slave = serial ? " Slave=\"0\"" : "";
        foreach (var bad in new[] { $"<WR Id=\"1\"{slave} Device=\"D0\" />", $"<BR Id=\"1\"{slave} Count=\"1\" />", $"<WR{slave} Device=\"D0\" Count=\"1\" />" })
        {
            var errors = new List<string>();
            new XDocument(XElement.Parse($"<{prefix}>{bad}</{prefix}>")).Validate(set, (_, e) => errors.Add(e.Message));
            Assert.NotEmpty(errors);
        }
    }
}
