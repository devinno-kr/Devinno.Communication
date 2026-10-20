using System.IO.Ports;
using System.Xml.Linq;
using System.Xml.Schema;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Serial;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Secs.Serial;

public class SecsSerialXmlTests
{
    static (SecsRole, int, int, string, int, Parity, int, StopBits, int, int, int, int, bool?, bool, bool) All(SecsSerial x)
        => (x.Role, x.DeviceID, x.T3, x.Port, x.Baudrate, x.Parity, x.DataBits, x.StopBits, x.T1, x.T2, x.T4, x.RetryLimit, x.IsMaster, x.DuplicateDetection, x.AutoReconnect);

    [Fact]
    public void RoundTrip_KeepsAllSettings()
    {
        var a = new SecsSerial
        {
            Role = SecsRole.Equipment, DeviceID = 100, T3 = 1000, Port = "COM9", Baudrate = 19200, Parity = Parity.Even, DataBits = 7,
            StopBits = StopBits.Two, T1 = 100, T2 = 25000, T4 = 120000, RetryLimit = 31, IsMaster = false, DuplicateDetection = false, AutoReconnect = true,
        };
        var x = a.ToXml();
        Assert.Equal("SecsSerial", x.Name.LocalName);
        Assert.Equal("false", x.Attribute("IsMaster")!.Value);
        var b = new SecsSerial();
        b.FromXml(x);
        Assert.Equal(All(a), All(b));
        Assert.Equal(x.ToString(), b.ToXml().ToString());
    }

    [Fact]
    public void IsMasterNull_IsOmitted_AndLoadsAsNull()
    {
        var a = new SecsSerial { IsMaster = null };
        var x = a.ToXml();
        Assert.Null(x.Attribute("IsMaster"));
        var b = new SecsSerial { IsMaster = true };
        b.FromXml(x);
        Assert.Null(b.IsMaster);
    }

    [Fact]
    public void SaveXml_LoadXml_File()
    {
        var path = Path.Combine(Path.GetTempPath(), $"secs-serial-{Guid.NewGuid():N}.xml");
        try
        {
            var a = new SecsSerial { Port = "COM4", RetryLimit = 0 };
            a.SaveXml(path);
            var b = new SecsSerial();
            b.LoadXml(path);
            Assert.Equal(All(a), All(b));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("T1=\"99\"", "T1")]
    [InlineData("T1=\"10001\"", "T1")]
    [InlineData("T2=\"199\"", "T2")]
    [InlineData("T2=\"25001\"", "T2")]
    [InlineData("T3=\"999\"", "T3")]
    [InlineData("T4=\"120001\"", "T4")]
    [InlineData("RetryLimit=\"32\"", "RetryLimit")]
    [InlineData("DeviceID=\"-1\"", "DeviceID")]
    [InlineData("DataBits=\"9\"", "DataBits")]
    [InlineData("StopBits=\"None\"", "StopBits")]
    [InlineData("Port=\"\"", "Port")]
    [InlineData("IsMaster=\"maybe\"", "IsMaster")]
    [InlineData("Parity=\"even\"", "Parity")]
    [InlineData("Mode=\"Active\"", "Mode")]
    public void Invalid_FormatException_KeepsSettings(string attribute, string name)
    {
        var x = new SecsSerial { Port = "COM5", T1 = 700 };
        var ex = Assert.Throws<FormatException>(() => x.FromXml(XElement.Parse($"<SecsSerial {attribute} />")));
        Assert.Contains(name, ex.Message);
        Assert.Equal(("COM5", 700), (x.Port, x.T1));
    }

    [Fact]
    public void WhileStarted_Throws()
    {
        var (a, _) = MemoryLine.Pair();
        using var x = new SecsSerial { OpenLine = a.ToLine };
        x.Start();
        Assert.Throws<InvalidOperationException>(() => x.FromXml(new SecsSerial().ToXml()));
    }

    [Fact]
    public void Schema_ValidatesSavedXml()
    {
        var xsd = ConfigSchema.Get<SecsSerialConfig>();
        Assert.Contains("name=\"SecsSerial\"", xsd);
        Assert.Contains("name=\"RetryLimit\"", xsd);
        Assert.DoesNotContain("IsMasterSpecified", xsd);
        var set = new XmlSchemaSet();
        set.Add(XmlSchema.Read(new StringReader(xsd), null)!);
        new XDocument(new SecsSerial { IsMaster = true }.ToXml()).Validate(set, (s, e) => Assert.Fail(e.Message));
    }
}
