using System.Text;
using System.Xml.Linq;
using System.Xml.Schema;
using Devinno.Communication.TextComm;
using Devinno.Communication.TextComm.RTU;
using Devinno.Communication.TextComm.TCP;

namespace Devinno.Communication.Tests.TextComm;

public class TextCommXmlTests
{
    [Fact]
    public void RtuMaster_RoundTrip_KeepsSettingsAutosAndEncoding()
    {
        var a = new TextCommRTUMaster
        {
            Port = "COM5", Baudrate = 19200, Parity = System.IO.Ports.Parity.Even, DataBits = 7, StopBits = System.IO.Ports.StopBits.Two,
            Timeout = 700, Interval = 5, AutoReconnect = true, BufferSize = 2048, MessageEncoding = Encoding.UTF8,
        };
        a.AutoSend(1, 1, 0x41, "온도", timeout: 300);
        a.AutoSend(2, 255, 0, "");
        a.ManualSend(9, 1, 1, "M");   // Manual은 저장 안 됨

        var x = a.ToXml();
        Assert.Equal("TextCommRTUMaster", x.Name.LocalName);
        Assert.Equal("utf-8", x.Attribute("MessageEncoding")!.Value);
        Assert.Equal(new[] { "1", "2" }, x.Elements("Auto").Select(e => e.Attribute("Id")!.Value));
        Assert.Equal("", x.Elements("Auto").Last().Attribute("Message")!.Value);

        var b = new TextCommRTUMaster();
        b.FromXml(x);
        Assert.Equal((a.Port, a.Baudrate, a.Parity, a.DataBits, a.StopBits), (b.Port, b.Baudrate, b.Parity, b.DataBits, b.StopBits));
        Assert.Equal((a.Timeout, a.Interval, a.AutoReconnect, a.BufferSize, "utf-8"), (b.Timeout, b.Interval, b.AutoReconnect, b.BufferSize, b.MessageEncoding.WebName));
        Assert.Equal(x.ToString(), b.ToXml().ToString());
        var w1 = b.TakeNextWork()!;
        var w2 = b.TakeNextWork()!;
        Assert.Equal((1, 300, 2, (int?)null), (w1.MessageID, w1.Timeout, w2.MessageID, w2.Timeout));
        Assert.Equal(TextCommPacket.Build(1, 0x41, Encoding.UTF8.GetBytes("온도")), w1.Request);   // 불러온 인코딩(UTF-8)으로 요청 생성
        Assert.Equal(TextCommPacket.Build(255, 0, ""u8), w2.Request);
    }

    [Fact]
    public void RtuMaster_HandWritten_WithDefaults()
    {
        var c = new TextCommRTUMaster();
        c.FromXml(XElement.Parse("""<TextCommRTUMaster Port="COM3"><Auto Id="5" Slave="1" Command="3" Message="READ" /></TextCommRTUMaster>"""));
        Assert.Equal(("COM3", 115200, 100, 10, false, 1024, "us-ascii"), (c.Port, c.Baudrate, c.Timeout, c.Interval, c.AutoReconnect, c.BufferSize, c.MessageEncoding.WebName));
        var w = c.TakeNextWork()!;
        Assert.Equal((5, 1, 3, "READ"), (w.MessageID, w.Slave, w.Command, w.Message));
        Assert.Equal(new byte[] { 0x02, 0x01, 0x10, 0x13, 0x52, 0x45, 0x41, 0x44, 0x20, 0x03 }, w.Request);   // 기준 바이트 (1, 3, "READ")
    }

    [Fact]
    public void TcpMaster_RoundTrip()
    {
        var a = new TextCommTCPMaster { RemoteIP = "10.0.0.7", RemotePort = 9000, AutoReconnect = false, MessageEncoding = Encoding.UTF8 };
        a.AutoSend(1, 2, 3, "X", timeout: 50);
        var b = new TextCommTCPMaster();
        b.FromXml(a.ToXml());
        Assert.Equal(("10.0.0.7", 9000, false, "utf-8"), (b.RemoteIP, b.RemotePort, b.AutoReconnect, b.MessageEncoding.WebName));
        Assert.Equal(a.ToXml().ToString(), b.ToXml().ToString());
        Assert.Equal((1, 2, 3, "X", (int?)50), b.TakeNextWork() is { } w ? (w.MessageID, w.Slave, w.Command, w.Message, w.Timeout) : default);
    }

    [Fact]
    public void Slaves_RoundTrip()
    {
        var r = new TextCommRTUSlave { Port = "COM4", Baudrate = 9600, Parity = System.IO.Ports.Parity.Odd, DataBits = 7, StopBits = System.IO.Ports.StopBits.Two, AutoReconnect = true, MessageEncoding = Encoding.UTF8 };
        var r2 = new TextCommRTUSlave();
        r2.FromXml(r.ToXml());
        Assert.Equal(("COM4", 9600, System.IO.Ports.Parity.Odd, 7, System.IO.Ports.StopBits.Two, true, "utf-8"),
            (r2.Port, r2.Baudrate, r2.Parity, r2.DataBits, r2.StopBits, r2.AutoReconnect, r2.MessageEncoding.WebName));
        Assert.Equal(r.ToXml().ToString(), r2.ToXml().ToString());

        var t = new TextCommTCPSlave { LocalPort = 7000, MaxClients = 3, MessageEncoding = Encoding.UTF8 };
        var t2 = new TextCommTCPSlave();
        t2.FromXml(t.ToXml());
        Assert.Equal((7000, 3, "utf-8"), (t2.LocalPort, t2.MaxClients, t2.MessageEncoding.WebName));
        Assert.Equal("""<TextCommTCPSlave LocalPort="7000" MaxClients="3" MessageEncoding="utf-8" />""", t.ToXml().ToString());
    }

    [Theory]
    [InlineData("""<TextCommRTUMaster><Auto Id="3" Slave="256" Command="1" Message="A" /></TextCommRTUMaster>""", "Auto Id=\"3\"", "0~255")]
    [InlineData("""<TextCommRTUMaster><Auto Id="3" Slave="1" Command="-1" Message="A" /></TextCommRTUMaster>""", "Command", "0~255")]
    [InlineData("""<TextCommRTUMaster><Auto Id="3" Slave="1" Command="1" Message="A" Timeout="0" /></TextCommRTUMaster>""", "Timeout", "1~60000")]
    [InlineData("""<TextCommRTUMaster><Auto Id="3" Slave="1" Command="1" /></TextCommRTUMaster>""", "Message", "필수")]
    [InlineData("""<TextCommRTUMaster><Auto Id="3" Slave="1" Message="A" /></TextCommRTUMaster>""", "Command", "필수")]
    [InlineData("""<TextCommRTUMaster MessageEncoding="nope" />""", "MessageEncoding", "nope")]
    [InlineData("""<TextCommRTUMaster MessageEncoding="" />""", "MessageEncoding", "비어")]
    [InlineData("""<TextCommRTUMaster BufferSize="8" />""", "BufferSize", "16~65536")]
    [InlineData("""<TextCommRTUMaster Timeout="0" />""", "Timeout", "1~60000")]
    [InlineData("""<TextCommRTUMaster Parity="even" />""", "Parity", "Even")]
    [InlineData("""<TextCommRTUMaster Encoding="utf-8" />""", "Encoding", "MessageEncoding")]
    [InlineData("""<TextCommRTUMaster><Send Id="1" /></TextCommRTUMaster>""", "Send", "Auto")]
    [InlineData("""<TextCommTCPMaster />""", "TextCommRTUMaster", "TextCommTCPMaster")]
    public void RtuMaster_FromXml_Invalid_MessageIsPrecise_StateKept(string xml, string mustContain1, string mustContain2)
    {
        var c = new TextCommRTUMaster { Port = "COM9" };
        c.AutoSend(1, 1, 1, "KEEP");
        var ex = Assert.Throws<FormatException>(() => c.FromXml(XElement.Parse(xml)));
        Assert.Contains(mustContain1, ex.Message);
        Assert.Contains(mustContain2, ex.Message);
        Assert.Equal("COM9", c.Port);
        Assert.Equal("KEEP", c.TakeNextWork()!.Message);
    }

    [Theory]
    [InlineData("""<TextCommTCPMaster RemotePort="0" />""", "RemotePort", "1~65535")]
    [InlineData("""<TextCommTCPMaster MessageEncoding="x-unknown" />""", "MessageEncoding", "x-unknown")]
    [InlineData("""<TextCommTCPSlave LocalPort="0" />""", "LocalPort", "1~65535")]
    [InlineData("""<TextCommTCPSlave MaxClients="0" />""", "MaxClients", "1~1000")]
    [InlineData("""<TextCommRTUSlave MessageEncoding="nope" />""", "MessageEncoding", "nope")]
    [InlineData("""<TextCommRTUSlave DataBits="9" />""", "DataBits", "5~8")]
    public void Others_FromXml_Invalid_MessageIsPrecise(string xml, string mustContain1, string mustContain2)
    {
        var e = XElement.Parse(xml);
        Action act = e.Name.LocalName switch
        {
            "TextCommTCPMaster" => () => new TextCommTCPMaster().FromXml(e),
            "TextCommTCPSlave" => () => new TextCommTCPSlave().FromXml(e),
            _ => () => new TextCommRTUSlave().FromXml(e),
        };
        var ex = Assert.Throws<FormatException>(act);
        Assert.Contains(mustContain1, ex.Message);
        Assert.Contains(mustContain2, ex.Message);
    }

    [Fact]
    public void ApplyConfig_NullMessage_Throws_NothingApplied()
    {
        var c = new TextCommTCPMaster { RemoteIP = "10.1.1.1" };
        var cfg = new TextCommTCPMasterConfig { RemoteIP = "10.2.2.2", Autos = [new TextCommAutoConfig { Id = 4, Slave = 1, Command = 1, Message = null! }] };
        var ex = Assert.Throws<FormatException>(() => c.ApplyConfig(cfg));
        Assert.Contains("Auto Id=\"4\"", ex.Message);
        Assert.Contains("Message", ex.Message);
        Assert.Equal("10.1.1.1", c.RemoteIP);
    }

    [Fact]
    public void FromXml_WhileRunning_Throws_AndKeepsState()
    {
        using var c = new TextCommRTUMaster { Port = "COM_NOT_EXIST", AutoReconnect = true };
        c.AutoSend(1, 1, 1, "A");
        c.Start();
        var other = new TextCommRTUMaster { Port = "COM99" };
        other.AutoSend(5, 2, 2, "B");
        Assert.Throws<InvalidOperationException>(() => c.FromXml(other.ToXml()));
        Assert.Equal("COM_NOT_EXIST", c.Port);
        Assert.Equal("1", c.ToXml().Elements("Auto").Single().Attribute("Id")!.Value);

        using var s = new TextCommTCPSlave { LocalPort = 0 };
        s.Start();
        Assert.Throws<InvalidOperationException>(() => s.FromXml(new TextCommTCPSlave { MaxClients = 2 }.ToXml()));
        Assert.Equal(10, s.MaxClients);
    }

    [Fact]
    public void Schemas_Compile_AndValidateOwnOutput()
    {
        var rm = new TextCommRTUMaster();
        rm.AutoSend(1, 1, 3, "READ");
        rm.AutoSend(2, 1, 4, "", timeout: 200);
        var tm = new TextCommTCPMaster();
        tm.AutoSend(1, 1, 3, "READ");

        foreach (var (xsd, xml) in new[]
        {
            (Devinno.Communication.ConfigSchema.Get<TextCommRTUMasterConfig>(), rm.ToXml()),
            (Devinno.Communication.ConfigSchema.Get<TextCommTCPMasterConfig>(), tm.ToXml()),
            (Devinno.Communication.ConfigSchema.Get<TextCommRTUSlaveConfig>(), new TextCommRTUSlave().ToXml()),
            (Devinno.Communication.ConfigSchema.Get<TextCommTCPSlaveConfig>(), new TextCommTCPSlave().ToXml()),
        })
        {
            var set = new XmlSchemaSet();
            set.Add(XmlSchema.Read(new StringReader(xsd), null)!);
            set.Compile();
            var errors = new List<string>();
            new XDocument(xml).Validate(set, (_, e) => errors.Add(e.Message));
            Assert.Empty(errors);
        }

        var rxsd = Devinno.Communication.ConfigSchema.Get<TextCommRTUMasterConfig>();
        Assert.Contains("name=\"Auto\"", rxsd);
        Assert.Contains("default=\"us-ascii\"", rxsd);
        Assert.Contains("default=\"1024\"", rxsd);
        Assert.Contains("default=\"7897\"", Devinno.Communication.ConfigSchema.Get<TextCommTCPMasterConfig>());
        Assert.Contains("default=\"7897\"", Devinno.Communication.ConfigSchema.Get<TextCommTCPSlaveConfig>());
    }

    [Fact]
    public void Schema_AutoItem_MarksRequiredAttributes()
    {
        var xsd = Devinno.Communication.ConfigSchema.Get<TextCommRTUMasterConfig>();
        var set = new XmlSchemaSet();
        set.Add(XmlSchema.Read(new StringReader(xsd), null)!);
        set.Compile();
        var ct = (XmlSchemaComplexType)set.GlobalTypes.Values.Cast<XmlSchemaType>().Single(t => t.Name == nameof(TextCommAutoConfig));
        XmlSchemaAttribute Use(string attr) => (XmlSchemaAttribute)ct.AttributeUses[new System.Xml.XmlQualifiedName(attr)]!;
        foreach (var name in new[] { "Id", "Slave", "Command", "Message" })
            Assert.Equal(XmlSchemaUse.Required, Use(name).Use);
        Assert.Equal(XmlSchemaUse.Optional, Use("Timeout").Use);
        Assert.Null(Use("Timeout").DefaultValue);

        // 필수 속성이 빠진 Auto 항목은 XSD 검증에서도 걸린다
        foreach (var bad in new[] { """<Auto Slave="1" Command="1" Message="" />""", """<Auto Id="1" Slave="1" Command="1" />""" })
        {
            var errors = new List<string>();
            new XDocument(XElement.Parse($"<TextCommRTUMaster>{bad}</TextCommRTUMaster>")).Validate(set, (_, e) => errors.Add(e.Message));
            Assert.NotEmpty(errors);
        }
    }

    [Fact]
    public void Masters_ToXml_MessageWithXmlInvalidChars_ThrowsNamingAuto_SaveXmlWritesNothing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"textcomm-{Guid.NewGuid():N}.xml");
        try
        {
            var r = new TextCommRTUMaster();
            r.AutoSend(1, 1, 1, "OK");
            r.AutoSend(3, 1, 1, "A" + (char)0x02 + "B");
            var ex = Assert.Throws<InvalidOperationException>(() => r.ToXml());
            Assert.Contains("Auto Id=\"3\"", ex.Message);
            Assert.Contains("XML", ex.Message);
            Assert.Throws<InvalidOperationException>(() => r.SaveXml(path));
            Assert.False(File.Exists(path));

            var t = new TextCommTCPMaster();
            t.AutoSend(7, 1, 1, "X" + (char)0x1F);
            var ex2 = Assert.Throws<InvalidOperationException>(() => t.ToXml());
            Assert.Contains("Auto Id=\"7\"", ex2.Message);
            Assert.Throws<InvalidOperationException>(() => t.SaveXml(path));
            Assert.False(File.Exists(path));

            // XML이 담을 수 있는 제어 문자(탭·줄바꿈)는 그대로 저장된다
            var ok = new TextCommTCPMaster();
            ok.AutoSend(1, 1, 1, "a\tb");
            Assert.Equal("a\tb", ok.ToXml().Element("Auto")!.Attribute("Message")!.Value);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SaveXml_LoadXml_FileRoundTrip_MasterAndSlave()
    {
        var path = Path.Combine(Path.GetTempPath(), $"textcomm-{Guid.NewGuid():N}.xml");
        try
        {
            var a = new TextCommRTUMaster { Port = "COM7", Baudrate = 9600, MessageEncoding = Encoding.UTF8 };
            a.AutoSend(1, 2, 3, "온도", timeout: 400);
            a.SaveXml(path);
            var b = new TextCommRTUMaster();
            b.LoadXml(path);
            Assert.Equal(a.ToXml().ToString(), b.ToXml().ToString());
            Assert.Equal("온도", b.TakeNextWork()!.Message);

            var s = new TextCommTCPSlave { LocalPort = 7100, MaxClients = 4, MessageEncoding = Encoding.UTF8 };
            s.SaveXml(path);
            var s2 = new TextCommTCPSlave();
            s2.LoadXml(path);
            Assert.Equal((7100, 4, "utf-8"), (s2.LocalPort, s2.MaxClients, s2.MessageEncoding.WebName));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void InvalidXml_LeavesSettingsUnchanged_AllFourClasses()
    {
        var tm = new TextCommTCPMaster { RemoteIP = "10.1.1.1" };
        tm.AutoSend(1, 1, 1, "KEEP");
        Assert.Throws<FormatException>(() => tm.FromXml(XElement.Parse("""<TextCommTCPMaster RemoteIP="10.2.2.2"><Auto Id="2" Slave="300" Command="1" Message="" /></TextCommTCPMaster>""")));
        Assert.Equal("10.1.1.1", tm.RemoteIP);
        Assert.Equal("KEEP", tm.TakeNextWork()!.Message);

        var rs = new TextCommRTUSlave { Port = "COM9" };
        Assert.Throws<FormatException>(() => rs.FromXml(XElement.Parse("""<TextCommRTUSlave Port="COM1" Baudrate="0" />""")));
        Assert.Equal("COM9", rs.Port);

        var ts = new TextCommTCPSlave { LocalPort = 7001 };
        Assert.Throws<FormatException>(() => ts.FromXml(XElement.Parse("""<TextCommTCPSlave LocalPort="7002" MaxClients="0" />""")));
        Assert.Equal(7001, ts.LocalPort);
    }

    [Fact]
    public void FromXml_RootNameMismatch_Throws_AllFourClasses()
    {
        Assert.Contains("TextCommRTUMaster", Assert.Throws<FormatException>(() => new TextCommRTUMaster().FromXml(new TextCommTCPMaster().ToXml())).Message);
        Assert.Contains("TextCommTCPMaster", Assert.Throws<FormatException>(() => new TextCommTCPMaster().FromXml(new TextCommRTUMaster().ToXml())).Message);
        Assert.Contains("TextCommRTUSlave", Assert.Throws<FormatException>(() => new TextCommRTUSlave().FromXml(new TextCommTCPSlave().ToXml())).Message);
        Assert.Contains("TextCommTCPSlave", Assert.Throws<FormatException>(() => new TextCommTCPSlave().FromXml(new TextCommRTUSlave().ToXml())).Message);
    }

    [Fact]
    public void FromXml_WhileRunning_Throws_AndKeepsState_TcpMasterAndRtuSlave()
    {
        using var m = new TextCommTCPMaster { RemotePort = 1, AutoReconnect = true };
        m.AutoSend(1, 1, 1, "A");
        m.Start();
        var other = new TextCommTCPMaster { RemotePort = 2 };
        other.AutoSend(5, 1, 1, "B");
        Assert.Throws<InvalidOperationException>(() => m.FromXml(other.ToXml()));
        Assert.Equal(1, m.RemotePort);
        Assert.Equal("1", m.ToXml().Elements("Auto").Single().Attribute("Id")!.Value);

        using var s = new TextCommRTUSlave { Port = "COM_NOT_EXIST", AutoReconnect = true };
        s.Start();
        Assert.Throws<InvalidOperationException>(() => s.FromXml(new TextCommRTUSlave { Port = "COM98" }.ToXml()));
        Assert.Equal("COM_NOT_EXIST", s.Port);
    }
}
