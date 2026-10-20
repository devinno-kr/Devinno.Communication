using System.IO.Ports;
using System.Xml.Linq;
using System.Xml.Schema;
using Devinno.Communication.Modbus.RTU;
using Devinno.Communication.Modbus.TCP;

namespace Devinno.Communication.Tests.Modbus;

public class ModbusXmlTests
{
    [Fact]
    public void RtuMaster_RoundTrip()
    {
        var a = new ModbusRTUMaster { Port = "COM7", Baudrate = 9600, Parity = Parity.Even, DataBits = 7, StopBits = StopBits.Two, Timeout = 250, Interval = 5, AutoReconnect = true };
        a.AutoWordRead_FC3(1, 2, 100, 10);
        a.AutoBitRead_FC1(2, 3, 0, 16, timeout: 300);
        a.ManualWordWrite_FC6(9, 1, 0, 1);  // Manual은 저장되지 않음

        var x = a.ToXml();
        var b = new ModbusRTUMaster();
        b.FromXml(x);

        Assert.Equal((a.Port, a.Baudrate, a.Parity, a.DataBits, a.StopBits, a.Timeout, a.Interval, a.AutoReconnect),
                     (b.Port, b.Baudrate, b.Parity, b.DataBits, b.StopBits, b.Timeout, b.Interval, b.AutoReconnect));
        Assert.Equal(x.ToString(), b.ToXml().ToString());
        Assert.Equal(2, x.Elements("Auto").Count());
        Assert.Equal(1, b.TakeNextWork()!.MessageID);
        Assert.Equal(2, b.TakeNextWork()!.MessageID);
        Assert.Equal(1, b.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void RtuMaster_ParsesHandWrittenXml_WithDefaults()
    {
        var x = XElement.Parse("""
            <ModbusRTUMaster Port="COM3">
              <Auto Id="1" Func="FC4" Slave="1" Start="0" Length="5" />
            </ModbusRTUMaster>
            """);
        var m = new ModbusRTUMaster();
        m.FromXml(x);
        Assert.Equal(("COM3", 115200, 100, 10, false), (m.Port, m.Baudrate, m.Timeout, m.Interval, m.AutoReconnect));
        var w = m.TakeNextWork()!;
        Assert.Equal(Devinno.Communication.Modbus.ModbusFunction.WORDREAD_F4, w.Function);
    }

    [Fact]
    public void TcpMaster_RoundTrip()
    {
        var a = new ModbusTCPMaster { RemoteIP = "10.0.0.5", RemotePort = 1502, Timeout = 300, Interval = 20, AutoReconnect = false };
        a.AutoWordRead_FC4(5, 0, 10, 2);
        var b = new ModbusTCPMaster();
        b.FromXml(a.ToXml());
        Assert.Equal(("10.0.0.5", 1502, 300, 20, false), (b.RemoteIP, b.RemotePort, b.Timeout, b.Interval, b.AutoReconnect));
        Assert.Equal(a.ToXml().ToString(), b.ToXml().ToString());
    }

    [Fact]
    public void RtuSlave_RoundTrip_ArraysNotSaved()
    {
        var a = new ModbusRTUSlave { Port = "COM9", Baudrate = 19200, Slave = 5, AutoReconnect = true, HoldingRegisters = new int[10] };
        var x = a.ToXml();
        Assert.Null(x.Attribute("HoldingRegisters"));
        var b = new ModbusRTUSlave();
        b.FromXml(x);
        Assert.Equal(("COM9", 19200, 5, true), (b.Port, b.Baudrate, b.Slave, b.AutoReconnect));
        Assert.Null(b.HoldingRegisters);
    }

    [Fact]
    public void TcpSlave_RoundTrip()
    {
        var a = new ModbusTCPSlave { LocalPort = 1502, Slave = 3, MaxClients = 4 };
        var b = new ModbusTCPSlave();
        b.FromXml(a.ToXml());
        Assert.Equal((1502, 3, 4), (b.LocalPort, b.Slave, b.MaxClients));
    }

    [Fact]
    public void SaveXml_LoadXml_File()
    {
        var path = Path.Combine(Path.GetTempPath(), $"devinno-{Guid.NewGuid():N}.xml");
        try
        {
            var a = new ModbusTCPMaster { RemoteIP = "1.2.3.4" };
            a.AutoWordRead_FC3(1, 1, 0, 1);
            a.SaveXml(path);
            var b = new ModbusTCPMaster();
            b.LoadXml(path);
            Assert.Equal("1.2.3.4", b.RemoteIP);
            Assert.True(b.ContainAutoID(1));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("""<ModbusRTUMaster Baudrate="abc" />""", "Baudrate")]
    [InlineData("""<ModbusRTUMaster DataBits="9" />""", "DataBits")]
    [InlineData("""<ModbusRTUMaster Parity="Weird" />""", "Parity")]
    [InlineData("""<ModbusRTUMaster><Auto Id="1" Func="FC6" Slave="1" Start="0" Length="1" /></ModbusRTUMaster>""", "Func")]
    [InlineData("""<ModbusRTUMaster><Auto Id="1" Func="FC3" Slave="0" Start="0" Length="1" /></ModbusRTUMaster>""", "Slave")]
    [InlineData("""<ModbusRTUMaster><Auto Func="FC3" Slave="1" Start="0" Length="1" /></ModbusRTUMaster>""", "Id")]
    [InlineData("""<ModbusRTUMaster><Auto Id="1" Func="FC3" Slave="1" Start="0" Length="200" /></ModbusRTUMaster>""", "Length")]
    public void FromXml_InvalidValue_MessageNamesAttribute(string xml, string attribute)
    {
        var ex = Assert.Throws<FormatException>(() => new ModbusRTUMaster().FromXml(XElement.Parse(xml)));
        Assert.Contains(attribute, ex.Message);
    }

    [Fact]
    public void FromXml_InvalidValue_KeepsPreviousConfig()
    {
        var m = new ModbusRTUMaster { Port = "COM5" };
        m.AutoWordRead_FC3(1, 1, 0, 1);
        var bad = XElement.Parse("""
            <ModbusRTUMaster Port="COM8">
              <Auto Id="2" Func="FC3" Slave="1" Start="0" Length="1" />
              <Auto Id="3" Func="FC3" Slave="1" Start="0" Length="0" />
            </ModbusRTUMaster>
            """);
        Assert.Throws<FormatException>(() => m.FromXml(bad));
        Assert.Equal("COM5", m.Port);
        Assert.True(m.ContainAutoID(1));
        Assert.False(m.ContainAutoID(2));
    }

    [Fact]
    public void FromXml_WrongRoot_Throws()
    {
        var ex = Assert.Throws<FormatException>(() => new ModbusTCPMaster().FromXml(XElement.Parse("<ModbusRTUMaster />")));
        Assert.Contains("ModbusTCPMaster", ex.Message);
    }

    [Fact]
    public void FromXml_WhileRunning_Throws()
    {
        using var slave = new ModbusTCPSlave { LocalPort = 0 };
        slave.Start();
        Assert.Throws<InvalidOperationException>(() => slave.FromXml(slave.ToXml()));
    }

    [Fact]
    public void FromXml_StopBitsNone_Throws()
    {
        var m = new ModbusRTUMaster();
        var ex1 = Assert.Throws<FormatException>(() => m.FromXml(XElement.Parse("<ModbusRTUMaster StopBits=\"None\" />")));
        Assert.Contains("StopBits", ex1.Message);
        Assert.Equal(StopBits.One, m.StopBits);

        var s = new ModbusRTUSlave();
        var ex2 = Assert.Throws<FormatException>(() => s.FromXml(XElement.Parse("<ModbusRTUSlave StopBits=\"None\" />")));
        Assert.Contains("StopBits", ex2.Message);
        Assert.Equal(StopBits.One, s.StopBits);
    }

    [Fact]
    public void FromXml_UnknownAttribute_Throws_ListsAllowed()
    {
        var m = new ModbusRTUMaster();
        var ex = Assert.Throws<FormatException>(() => m.FromXml(XElement.Parse("""<ModbusRTUMaster Baudrat="9600" />""")));
        Assert.Contains("Baudrat", ex.Message);
        Assert.Contains("Baudrate", ex.Message);
        Assert.Equal(115200, m.Baudrate);
    }

    [Fact]
    public void FromXml_UnknownAttributeOnAuto_Throws()
    {
        var ex = Assert.Throws<FormatException>(() => new ModbusTCPMaster().FromXml(XElement.Parse(
            """<ModbusTCPMaster><Auto Id="1" Func="FC3" Slave="1" Length="1" Timout="50" /></ModbusTCPMaster>""")));
        Assert.Contains("Timout", ex.Message);
        Assert.Contains("<Auto>", ex.Message);
    }

    [Fact]
    public void FromXml_UnknownElement_Throws()
    {
        var ex = Assert.Throws<FormatException>(() => new ModbusRTUMaster().FromXml(XElement.Parse(
            """<ModbusRTUMaster><auto Id="1" Func="FC3" Slave="1" Length="1" /></ModbusRTUMaster>""")));
        Assert.Contains("auto", ex.Message);
        Assert.Contains("Auto", ex.Message);
    }

    [Theory]
    [InlineData("""<ModbusRTUMaster Parity="even" />""", "Parity", "Even")]
    [InlineData("""<ModbusRTUMaster Parity="1" />""", "Parity", "Even")]
    [InlineData("""<ModbusRTUMaster AutoReconnect="yes" />""", "AutoReconnect", "true 또는 false")]
    [InlineData("""<ModbusRTUMaster><Auto Id="3" Func="FC3" Slave="x" Length="1" /></ModbusRTUMaster>""", "Slave", "Id=\"3\"")]
    [InlineData("""<ModbusRTUMaster><Auto Id="4" Func="FC9" Slave="1" Length="1" /></ModbusRTUMaster>""", "Func", "FC3")]
    public void FromXml_BadValue_MessageNamesAttributeAndHint(string xml, string attribute, string hint)
    {
        var ex = Assert.Throws<FormatException>(() => new ModbusRTUMaster().FromXml(XElement.Parse(xml)));
        Assert.Contains(attribute, ex.Message);
        Assert.Contains(hint, ex.Message);
    }

    [Fact]
    public void FromXml_TcpAuto_MissingSlave_Throws()
    {
        var ex = Assert.Throws<FormatException>(() => new ModbusTCPMaster().FromXml(XElement.Parse(
            """<ModbusTCPMaster><Auto Id="1" Func="FC3" Length="1" /></ModbusTCPMaster>""")));
        Assert.Contains("Slave", ex.Message);
        Assert.Contains("필수", ex.Message);
    }

    [Fact]
    public void FromXml_TcpAuto_SlaveRange()
    {
        var m = new ModbusTCPMaster();
        m.FromXml(XElement.Parse("""<ModbusTCPMaster><Auto Id="1" Func="FC3" Slave="0" Length="1" /></ModbusTCPMaster>"""));
        Assert.Equal(0, m.TakeNextWork()!.Slave);
        Assert.Throws<FormatException>(() => m.FromXml(XElement.Parse(
            """<ModbusTCPMaster><Auto Id="1" Func="FC3" Slave="256" Length="1" /></ModbusTCPMaster>""")));
    }

    [Fact]
    public void ConfigObject_RoundTrip_AndApply()
    {
        var a = new ModbusRTUMaster { Port = "COM2", Baudrate = 19200, Timeout = 300 };
        a.AutoWordRead_FC4(7, 2, 10, 3, timeout: 150);

        var cfg = a.ToConfig();
        Assert.Equal(("COM2", 19200, 300), (cfg.Port, cfg.Baudrate, cfg.Timeout));
        var auto = Assert.Single(cfg.Autos);
        Assert.Equal((7, Devinno.Communication.Modbus.ModbusFunction.WORDREAD_F4, 2, 10, 3, 150), (auto.Id, auto.Func, auto.Slave, auto.Start, auto.Length, auto.Timeout));

        cfg.Interval = 50;
        var b = new ModbusRTUMaster();
        b.ApplyConfig(cfg);
        Assert.Equal(("COM2", 19200, 300, 50), (b.Port, b.Baudrate, b.Timeout, b.Interval));
        Assert.True(b.ContainAutoID(7));
    }

    [Fact]
    public void ApplyConfig_Invalid_KeepsPrevious()
    {
        var m = new ModbusTCPSlave { LocalPort = 1502 };
        var ex = Assert.Throws<FormatException>(() => m.ApplyConfig(new ModbusTCPSlaveConfig { LocalPort = 600, MaxClients = 0 }));
        Assert.Contains("MaxClients", ex.Message);
        Assert.Equal(1502, m.LocalPort);
    }

    [Fact]
    public void ToXml_OmitsUnsetAutoTimeout()
    {
        var m = new ModbusRTUMaster();
        m.AutoWordRead_FC3(1, 1, 0, 1);
        var auto = Assert.Single(m.ToXml().Elements("Auto"));
        Assert.Null(auto.Attribute("Timeout"));
        Assert.Equal("FC3", auto.Attribute("Func")!.Value);
    }

    [Fact]
    public void Schema_MarksRequiredAndDefaults_AndValidatesToXml()
    {
        var xsd = Devinno.Communication.ConfigSchema.Get<ModbusRTUMasterConfig>();
        var schema = System.Xml.Schema.XmlSchema.Read(new StringReader(xsd), null)!;
        var set = new System.Xml.Schema.XmlSchemaSet();
        set.Add(schema);
        set.Compile();

        var autoType = (System.Xml.Schema.XmlSchemaComplexType)set.GlobalTypes.Values.Cast<System.Xml.Schema.XmlSchemaType>().Single(t => t.Name == "ModbusAutoConfig");
        var attrs = autoType.Attributes.Cast<System.Xml.Schema.XmlSchemaAttribute>().ToDictionary(a => a.Name!);
        Assert.Equal(System.Xml.Schema.XmlSchemaUse.Required, attrs["Id"].Use);
        Assert.Equal(System.Xml.Schema.XmlSchemaUse.Optional, attrs["Timeout"].Use);
        Assert.Equal("0", attrs["Start"].DefaultValue);

        var rootType = (System.Xml.Schema.XmlSchemaComplexType)set.GlobalTypes.Values.Cast<System.Xml.Schema.XmlSchemaType>().Single(t => t.Name == "ModbusRTUMasterConfig");
        var rootAttrs = rootType.Attributes.Cast<System.Xml.Schema.XmlSchemaAttribute>().ToDictionary(a => a.Name!);
        Assert.Equal("115200", rootAttrs["Baudrate"].DefaultValue);
        Assert.Equal("false", rootAttrs["AutoReconnect"].DefaultValue);
        Assert.Equal("None", rootAttrs["Parity"].DefaultValue);

        var m = new ModbusRTUMaster();
        m.AutoWordRead_FC3(1, 1, 0, 1);
        m.AutoBitRead_FC1(2, 1, 0, 8, timeout: 50);
        var errors = new List<string>();
        new XDocument(m.ToXml()).Validate(set, (_, e) => errors.Add(e.Message));
        Assert.Empty(errors);
        new XDocument(XElement.Parse("""<ModbusRTUMaster Port="COM3"><Auto Id="1" Func="FC3" Slave="1" Length="2" /></ModbusRTUMaster>""")).Validate(set, (_, e) => errors.Add(e.Message));
        Assert.Empty(errors);
    }

    [Fact]
    public void Schema_AllConfigTypes_Compile()
    {
        foreach (var xsd in new[]
        {
            Devinno.Communication.ConfigSchema.Get<ModbusRTUMasterConfig>(),
            Devinno.Communication.ConfigSchema.Get<ModbusRTUSlaveConfig>(),
            Devinno.Communication.ConfigSchema.Get<ModbusTCPMasterConfig>(),
            Devinno.Communication.ConfigSchema.Get<ModbusTCPSlaveConfig>(),
        })
        {
            var set = new System.Xml.Schema.XmlSchemaSet();
            set.Add(System.Xml.Schema.XmlSchema.Read(new StringReader(xsd), null)!);
            set.Compile();
            Assert.True(set.IsCompiled);
        }
    }
}
