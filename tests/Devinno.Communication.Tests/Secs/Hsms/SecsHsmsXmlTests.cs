using System.Xml.Linq;
using System.Xml.Schema;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Hsms;

namespace Devinno.Communication.Tests.Secs.Hsms;

public class SecsHsmsXmlTests
{
    static (SecsRole, int, int, HsmsMode, string, int, string, int, int, int, int, int, int, int, bool) All(SecsHsms x)
        => (x.Role, x.DeviceID, x.T3, x.Mode, x.RemoteIP, x.RemotePort, x.LocalIP, x.LocalPort, x.T5, x.T6, x.T7, x.T8, x.LinktestInterval, x.MaxMessageSize, x.AutoReconnect);

    [Fact]
    public void RoundTrip_KeepsAllSettings()
    {
        var a = new SecsHsms
        {
            Role = SecsRole.Equipment, DeviceID = 32767, T3 = 120000, Mode = HsmsMode.Passive, RemoteIP = "10.0.0.5", RemotePort = 6000,
            LocalIP = "127.0.0.1", LocalPort = 0, T5 = 1000, T6 = 240000, T7 = 2000, T8 = 120000, LinktestInterval = 0,
            MaxMessageSize = 1024, AutoReconnect = false,
        };
        var x = a.ToXml();
        Assert.Equal("SecsHsms", x.Name.LocalName);
        Assert.Empty(x.Elements());
        var b = new SecsHsms();
        b.FromXml(x);
        Assert.Equal(All(a), All(b));
        Assert.Equal(x.ToString(), b.ToXml().ToString());
    }

    [Fact]
    public void SaveXml_LoadXml_File()
    {
        var path = Path.Combine(Path.GetTempPath(), $"secs-hsms-{Guid.NewGuid():N}.xml");
        try
        {
            var a = new SecsHsms { Role = SecsRole.Equipment, Mode = HsmsMode.Passive, LocalPort = 5001, LinktestInterval = 30000 };
            a.SaveXml(path);
            var b = new SecsHsms();
            b.LoadXml(path);
            Assert.Equal(All(a), All(b));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void HandWritten_DefaultsForOmitted()
    {
        var x = new SecsHsms { DeviceID = 9, T3 = 1234 };
        x.FromXml(XElement.Parse("<SecsHsms Role=\"Equipment\" Mode=\"Passive\" LocalPort=\"5000\" />"));
        Assert.Equal(All(new SecsHsms { Role = SecsRole.Equipment, Mode = HsmsMode.Passive }), All(x));
    }

    [Theory]
    [InlineData("DeviceID=\"32768\"", "DeviceID")]
    [InlineData("T3=\"999\"", "T3")]
    [InlineData("T3=\"120001\"", "T3")]
    [InlineData("T5=\"240001\"", "T5")]
    [InlineData("T6=\"999\"", "T6")]
    [InlineData("T7=\"0\"", "T7")]
    [InlineData("T8=\"120001\"", "T8")]
    [InlineData("LinktestInterval=\"999\"", "LinktestInterval")]
    [InlineData("LinktestInterval=\"3600001\"", "LinktestInterval")]
    [InlineData("MaxMessageSize=\"1023\"", "MaxMessageSize")]
    [InlineData("RemotePort=\"0\"", "RemotePort")]
    [InlineData("LocalPort=\"65536\"", "LocalPort")]
    [InlineData("LocalIP=\"pc-name\"", "LocalIP")]
    [InlineData("RemoteIP=\"\"", "RemoteIP")]
    [InlineData("Mode=\"passive\"", "Mode")]
    [InlineData("Role=\"Equip\"", "Role")]
    [InlineData("AutoReconnect=\"yes\"", "AutoReconnect")]
    [InlineData("Timeout=\"100\"", "Timeout")]
    public void Invalid_FormatException_NamesAttribute_KeepsSettings(string attribute, string name)
    {
        var x = new SecsHsms { DeviceID = 7, T3 = 5000 };
        var ex = Assert.Throws<FormatException>(() => x.FromXml(XElement.Parse($"<SecsHsms {attribute} />")));
        Assert.Contains(name, ex.Message);
        Assert.Equal((7, 5000), (x.DeviceID, x.T3));
    }

    [Fact]
    public void LinktestInterval0_IsAllowed()
    {
        var x = new SecsHsms();
        x.FromXml(XElement.Parse("<SecsHsms LinktestInterval=\"0\" />"));
        Assert.Equal(0, x.LinktestInterval);
    }

    [Fact]
    public void ChildElement_WrongRoot_WhileStarted_Throw()
    {
        var x = new SecsHsms();
        Assert.Contains("알 수 없는 요소", Assert.Throws<FormatException>(() => x.FromXml(XElement.Parse("<SecsHsms><Auto /></SecsHsms>"))).Message);
        Assert.Contains("<SecsHsms>", Assert.Throws<FormatException>(() => x.FromXml(XElement.Parse("<SecsSerial />"))).Message);
        using var started = SecsHsmsTests.Passive(SecsRole.Equipment);
        started.Start();
        Assert.Throws<InvalidOperationException>(() => started.FromXml(new SecsHsms().ToXml()));
        Assert.Throws<InvalidOperationException>(() => started.ApplyConfig(new SecsHsmsConfig()));
    }

    [Fact]
    public void Schema_ValidatesSavedXml()
    {
        var xsd = ConfigSchema.Get<SecsHsmsConfig>();
        Assert.Contains("name=\"SecsHsms\"", xsd);
        Assert.Contains("name=\"LinktestInterval\"", xsd);
        Assert.Contains("default=\"45000\"", xsd);
        var set = new XmlSchemaSet();
        set.Add(XmlSchema.Read(new StringReader(xsd), null)!);
        var doc = new XDocument(new SecsHsms { Role = SecsRole.Equipment }.ToXml());
        doc.Validate(set, (s, e) => Assert.Fail(e.Message));
    }
}
