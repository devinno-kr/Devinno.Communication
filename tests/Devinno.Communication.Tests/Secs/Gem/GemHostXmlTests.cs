using System.Xml.Linq;
using System.Xml.Schema;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Secs.Hsms;
using Devinno.Communication.Secs.Serial;

namespace Devinno.Communication.Tests.Secs.Gem;

/// GemHost XML: 왕복, 스펙 예제, 엄격 모드, 필수 속성, 범위, 구독 ID·ReportIdStart 검사, 전송 요소 종류·Role, 부분 적용 금지, 실행 중 불러오기, 스키마
[Collection(nameof(GemHostCollection))]
public class GemHostXmlTests
{
    const string SpecExample = """
        <GemHost VidFormat="U4" CeidFormat="U4" RptidFormat="U4" AutoConnectSequence="true" ReportIdStart="1000">
          <SecsHsms Mode="Active" RemoteIP="192.168.0.50" RemotePort="5000" DeviceID="1" />
          <Subscribe Ceid="5001" Vids="2001,3001" />
          <Subscribe Ceid="1302" />
        </GemHost>
        """;

    static GemHost Hsms() => new(new SecsHsms());

    [Fact]
    public void SpecExample_Loads()
    {
        using var host = Hsms();
        host.FromXml(XElement.Parse(SpecExample));
        var t = (SecsHsms)host.Transport;
        Assert.Equal((SecsRole.Host, HsmsMode.Active, "192.168.0.50", 5000, 1), (t.Role, t.Mode, t.RemoteIP, t.RemotePort, t.DeviceID));
        Assert.Equal(["5001:2001,3001", "1302:"], host.Subscriptions.Select(s => $"{s.Ceid}:{string.Join(",", s.Vids)}"));
        Assert.True(host.AutoConnectSequence);
        Assert.Equal(1000, host.ReportIdStart);
    }

    [Fact]
    public void RoundTrip_AllSettings()
    {
        using var a = new GemHost(new SecsSerial { Port = "COM7", Baudrate = 19200 })
        {
            VidFormat = SecsFormat.U2, CeidFormat = SecsFormat.ASCII, RptidFormat = SecsFormat.I4, AlidFormat = SecsFormat.U1, DataidFormat = SecsFormat.U8,
            TimeFormat = 2, AutoConnectSequence = false, ConnectRequestOnline = false, ConnectSetClock = true, ConnectConfigureReports = false, ConnectEnableAlarms = false,
            DeleteReportsFirst = false, ReportIdStart = 77, EstablishCommunicationRetry = 30, CommandReplyTimeout = 2000, MaxPendingEvents = 50,
        };
        a.Subscribe("START", 1, 2);
        a.Subscribe("END");
        var xml = a.ToXml();
        Assert.Equal("SecsSerial", xml.Elements().First().Name.LocalName);                // 전송 요소는 항상 씀
        Assert.Null(xml.Elements("Subscribe").Last().Attribute("Vids"));                  // VID 없는 구독은 Vids 생략
        using var b = new GemHost(new SecsSerial());
        b.FromXml(xml);
        Assert.True(XNode.DeepEquals(xml, b.ToXml()), b.ToXml().ToString());
        Assert.Equal(("COM7", SecsRole.Host), (((SecsSerial)b.Transport).Port, ((SecsSerial)b.Transport).Role));
        Assert.Equal((SecsFormat.ASCII, 2, false, true, 77L), (b.CeidFormat, b.TimeFormat, b.ConnectRequestOnline, b.ConnectSetClock, b.ReportIdStart));
    }

    [Fact]
    public void SaveXml_LoadXml_File()
    {
        var path = Path.Combine(Path.GetTempPath(), $"devinno-gemhost-{Guid.NewGuid():N}.xml");
        try
        {
            using var a = Hsms();
            a.FromXml(XElement.Parse(SpecExample));
            a.SaveXml(path);
            using var b = Hsms();
            b.LoadXml(path);
            Assert.True(XNode.DeepEquals(a.ToXml(), b.ToXml()));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("<GemHost Foo=\"1\" />", "알 수 없는 속성")]
    [InlineData("<GemHost><Subscription Ceid=\"1\" /></GemHost>", "알 수 없는 요소")]
    [InlineData("<GemHost><Subscribe Vids=\"1\" /></GemHost>", "필수 속성")]
    [InlineData("<GemHost VidFormat=\"F4\" />", "VidFormat")]
    [InlineData("<GemHost TimeFormat=\"3\" />", "0~2")]
    [InlineData("<GemHost EstablishCommunicationRetry=\"0\" />", "EstablishCommunicationRetry")]
    [InlineData("<GemHost CommandReplyTimeout=\"999\" />", "1000~110000")]
    [InlineData("<GemHost MaxPendingEvents=\"0\" />", "MaxPendingEvents")]
    [InlineData("<GemHost AutoConnectSequence=\"yes\" />", "AutoConnectSequence")]
    [InlineData("<GemHost ReportIdStart=\"-1\" />", "ReportIdStart")]
    [InlineData("<GemHost><Subscribe Ceid=\"x\" /></GemHost>", "Subscribe Ceid=\"x\"")]
    [InlineData("<GemHost><Subscribe Ceid=\"1\" Vids=\"2,abc\" /></GemHost>", "Vids")]
    [InlineData("<GemHost CeidFormat=\"U1\"><Subscribe Ceid=\"300\" /></GemHost>", "범위 밖")]
    [InlineData("<GemHost RptidFormat=\"U1\" ReportIdStart=\"255\"><Subscribe Ceid=\"1\" Vids=\"1\" /><Subscribe Ceid=\"2\" Vids=\"2\" /></GemHost>", "ReportIdStart")]
    public void Invalid_IsFormatException_WithLocation(string xml, string contains)
        => Assert.Contains(contains, Assert.Throws<FormatException>(() => Hsms().FromXml(XElement.Parse(xml))).Message);

    [Fact]
    public void ReportIdStart_FitsWhenSubscriptionsWithoutVidsDoNotCount()
    {
        using var host = Hsms();
        host.FromXml(XElement.Parse("<GemHost RptidFormat=\"U1\" ReportIdStart=\"255\"><Subscribe Ceid=\"1\" Vids=\"1\" /><Subscribe Ceid=\"2\" /></GemHost>"));
        Assert.Equal(255, host.ReportIdStart);
    }

    [Fact]
    public void DuplicateCeid_LaterReplacesInPlace()
    {
        using var host = Hsms();
        host.FromXml(XElement.Parse("<GemHost><Subscribe Ceid=\"1\" Vids=\"10\" /><Subscribe Ceid=\"2\" /><Subscribe Ceid=\"1\" Vids=\"11,12\" /></GemHost>"));
        Assert.Equal(["1:11,12", "2:"], host.Subscriptions.Select(s => $"{s.Ceid}:{string.Join(",", s.Vids)}"));
    }

    [Fact]
    public void TransportElement_KindMismatch_RoleRules()
    {
        Assert.Contains("SecsSerial", Assert.Throws<FormatException>(() => Hsms().FromXml(XElement.Parse("<GemHost><SecsSerial Port=\"COM1\" /></GemHost>"))).Message);
        Assert.Throws<FormatException>(() => new GemHost(new SecsSerial()).FromXml(XElement.Parse("<GemHost><SecsHsms /></GemHost>")));
        Assert.Throws<FormatException>(() => Hsms().FromXml(XElement.Parse("<GemHost><SecsHsms /><SecsSerial /></GemHost>")));
        Assert.Contains("Role", Assert.Throws<FormatException>(() => Hsms().FromXml(XElement.Parse("<GemHost><SecsHsms Role=\"Equipment\" /></GemHost>"))).Message);
        using var host = Hsms();
        host.FromXml(XElement.Parse("<GemHost><SecsHsms Role=\"Host\" DeviceID=\"7\" /></GemHost>"));
        Assert.Equal((SecsRole.Host, 7), (((SecsHsms)host.Transport).Role, ((SecsHsms)host.Transport).DeviceID));
    }

    [Fact]
    public void Invalid_LeavesPreviousSettingsAndSubscriptions()
    {
        using var host = Hsms();
        host.FromXml(XElement.Parse(SpecExample));
        var before = host.ToXml();
        Assert.Throws<FormatException>(() => host.FromXml(XElement.Parse("<GemHost TimeFormat=\"0\"><SecsHsms DeviceID=\"3\" /><Subscribe Ceid=\"9\" Vids=\"x\" /></GemHost>")));
        Assert.True(XNode.DeepEquals(before, host.ToXml()));
        Assert.Equal(1, ((SecsHsms)host.Transport).DeviceID);
    }

    [Fact]
    public void WhileRunning_InvalidOperation()
    {
        using var host = GemHostKit.Host(1);
        host.Start();
        Assert.Throws<InvalidOperationException>(() => host.FromXml(XElement.Parse("<GemHost />")));
        Assert.Throws<InvalidOperationException>(() => host.ApplyConfig(new GemHostConfig()));
    }

    [Fact]
    public void Schema_ValidatesSpecExample_RejectsMissingCeid()
    {
        var xsd = ConfigSchema.Get<GemHostConfig>();
        Assert.Contains("name=\"Subscribe\"", xsd);
        Assert.Contains("default=\"1000\"", xsd);
        var set = new XmlSchemaSet();
        set.Add(XmlSchema.Read(new StringReader(xsd), null)!);
        XDocument.Parse(SpecExample).Validate(set, (s, e) => Assert.Fail(e.Message));
        var errors = new List<string>();
        XDocument.Parse("<GemHost><Subscribe Vids=\"1\" /></GemHost>").Validate(set, (s, e) => errors.Add(e.Message));
        Assert.Contains(errors, m => m.Contains("Ceid", StringComparison.Ordinal));
    }
}
