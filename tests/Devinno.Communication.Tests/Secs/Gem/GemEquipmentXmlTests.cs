using System.Xml.Linq;
using System.Xml.Schema;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Secs.Hsms;
using Devinno.Communication.Secs.Serial;

namespace Devinno.Communication.Tests.Secs.Gem;

/// GemEquipment XML: 왕복, 스펙 예제, 엄격 모드, 필수 속성, 전송 요소 종류·Role, 정의 겹침, 부분 적용 금지, 실행 중 불러오기, 스키마
public class GemEquipmentXmlTests
{
    const string SpecExample = """
        <GemEquipment ModelName="DV-100" SoftwareRevision="1.0.0" VidFormat="U4" CeidFormat="U4" RptidFormat="U4" AlidFormat="U4" DataidFormat="U4"
                      InitialCommunicationState="Enabled" InitiateCommunication="true" InitialControlState="HostOffline" OnlineFailedState="EquipmentOffline"
                      EventsEnabledByDefault="false" AlarmsEnabledByDefault="false" EventReplyExpected="true" AlarmReplyExpected="true" TerminalReplyExpected="true"
                      CommandReplyTimeout="10000" MaxPendingSends="1000">
          <SecsHsms DeviceID="1" Mode="Passive" LocalPort="5000" />
          <Standard Clock="1001" ControlState="1002" EstablishCommunicationsTimeout="1101" TimeFormat="1102" />
          <SV Id="2001" Name="Temperature" Format="F4" Units="C" />
          <DV Id="3001" Name="LotID" Format="A" />
          <EC Id="4001" Name="MaxTemp" Format="F4" Default="80" Min="0" Max="200" Units="C" />
          <CE Id="5001" Name="ProcessStarted" Vids="2001,3001" />
          <Alarm Id="1" Text="Over temperature" Category="2" SetCeid="9001" ClearCeid="9002" />
          <Command Name="START"><Param Name="LOTID" Format="A" /></Command>
          <Message Stream="7" Function="1" />
        </GemEquipment>
        """;

    static GemEquipment Hsms() => new(new SecsHsms());

    [Fact]
    public void SpecExample_Loads()
    {
        using var eq = Hsms();
        eq.FromXml(XElement.Parse(SpecExample));
        var t = (SecsHsms)eq.Transport;
        Assert.Equal((SecsRole.Equipment, 1, HsmsMode.Passive, 5000), (t.Role, t.DeviceID, t.Mode, t.LocalPort));
        Assert.Equal(("DV-100", "1.0.0"), (eq.ModelName, eq.SoftwareRevision));
        Assert.Equal(SecsFormat.F4, eq.StatusVariables.Single().Format);
        Assert.Equal(SecsFormat.ASCII, eq.DataVariables.Single().Format);
        var ec = eq.EquipmentConstants.Single();
        Assert.Equal((SecsItem.F4(80), SecsItem.F4(0), SecsItem.F4(200), "C"), (ec.Default, ec.Min, ec.Max, ec.Units));
        Assert.Equal([(GemId)2001, 3001], eq.CollectionEvents.Single().DataVariables);
        Assert.Equal((2, (GemId)9001), (eq.Alarms.Single().Category, eq.Alarms.Single().SetCeid));
        Assert.Equal(SecsFormat.ASCII, eq.RemoteCommands.Single().Parameters.Single().Format);
        Assert.Equal([(7, 1)], eq.RegisteredMessages);
        Assert.Equal((GemId)1001, eq.StandardIds.Clock);
        Assert.Null(eq.StandardIds.AlarmsSet);
        t.LocalIP = "127.0.0.1";                                                            // 테스트는 루프백·OS가 고른 포트로
        t.LocalPort = 0;
        eq.Start();                                                                        // 불러온 정의로 시작됨
        Assert.Equal(SecsItem.F4(80), eq.GetValue(4001));
    }

    [Fact]
    public void RoundTrip_AllSettingsAndDefinitions()
    {
        using var a = new GemEquipment(new SecsSerial { Port = "COM7", Baudrate = 19200 })
        {
            ModelName = "M", SoftwareRevision = "2.0", VidFormat = SecsFormat.U2, CeidFormat = SecsFormat.I4, AlidFormat = SecsFormat.ASCII,
            InitialCommunicationState = GemEnableState.Disabled, InitiateCommunication = false, InitialControlState = GemControlState.OnlineLocal,
            OnlineFailedState = GemControlState.HostOffline, EventsEnabledByDefault = true, AlarmsEnabledByDefault = true,
            EventReplyExpected = false, AlarmReplyExpected = false, TerminalReplyExpected = false, CommandReplyTimeout = 2000, MaxPendingSends = 5,
            MaxPendingEvents = 50,
        };
        a.StandardIds.MessageRecognition = 7000;
        a.AddStatusVariable(new GemStatusVariable(2001, "Temp", SecsFormat.F8, "C", SecsItem.F8(1.5)));
        a.AddStatusVariable(new GemStatusVariable(2002, "Flags", SecsFormat.Binary, initialValue: SecsItem.B(1, 2)));
        a.AddDataVariable(new GemDataVariable(3001, "Ok", SecsFormat.Boolean, initialValue: SecsItem.Boolean(true)));
        a.AddEquipmentConstant(new GemEquipmentConstant(4001, "Limit", SecsItem.I2(-5), SecsItem.I2(-10), null, "mm"));
        a.AddCollectionEvent(new GemCollectionEvent(5001, "E", [2001, 3001]));
        a.AddAlarm(new GemAlarm("AL1", "Text", 8, 9001, 9002));
        a.AddRemoteCommand(new GemRemoteCommand("GO", [new GemCommandParameter("X"), new GemCommandParameter("Y", SecsFormat.U4)]));
        a.RegisterMessage(7, 3);
        var xml = a.ToXml();
        Assert.Equal("SecsSerial", xml.Elements().First().Name.LocalName);                // 전송 요소는 항상 씀
        using var b = new GemEquipment(new SecsSerial());
        b.FromXml(xml);
        Assert.True(XNode.DeepEquals(xml, b.ToXml()), b.ToXml().ToString());
        Assert.Equal(("COM7", 19200, SecsRole.Equipment), (((SecsSerial)b.Transport).Port, ((SecsSerial)b.Transport).Baudrate, ((SecsSerial)b.Transport).Role));
        Assert.Equal(SecsItem.B(1, 2), b.StatusVariables[1].InitialValue);
        Assert.Equal((GemId)"AL1", b.Alarms.Single().Id);
    }

    [Fact]
    public void SaveXml_LoadXml_File()
    {
        var path = Path.Combine(Path.GetTempPath(), $"devinno-gem-{Guid.NewGuid():N}.xml");
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
    [InlineData("<GemEquipment Foo=\"1\" />", "알 수 없는 속성")]
    [InlineData("<GemEquipment><SV Id=\"1\" Name=\"X\" Format=\"U4\" Unit=\"C\" /></GemEquipment>", "Unit")]
    [InlineData("<GemEquipment><Variable /></GemEquipment>", "알 수 없는 요소")]
    [InlineData("<GemEquipment><SV Id=\"1\" Name=\"X\" /></GemEquipment>", "필수 속성")]
    [InlineData("<GemEquipment><EC Id=\"1\" Name=\"X\" Format=\"U4\" /></GemEquipment>", "Default")]
    [InlineData("<GemEquipment><Alarm Id=\"1\" Text=\"t\" SetCeid=\"1\" ClearCeid=\"2\" /></GemEquipment>", "Category")]
    [InlineData("<GemEquipment><Message Stream=\"7\" /></GemEquipment>", "Function")]
    [InlineData("<GemEquipment VidFormat=\"F4\" />", "VidFormat")]
    [InlineData("<GemEquipment VidFormat=\"ASCII\" />", "VidFormat")]
    [InlineData("<GemEquipment CommandReplyTimeout=\"999\" />", "1000~110000")]
    [InlineData("<GemEquipment MaxPendingSends=\"0\" />", "MaxPendingSends")]
    [InlineData("<GemEquipment MaxPendingEvents=\"0\" />", "MaxPendingEvents")]
    [InlineData("<GemEquipment ModelName=\"123456789012345678901\" />", "ModelName")]
    [InlineData("<GemEquipment InitialControlState=\"AttemptOnline\" />", "InitialControlState")]
    [InlineData("<GemEquipment OnlineFailedState=\"OnlineRemote\" />", "OnlineFailedState")]
    [InlineData("<GemEquipment InitialControlState=\"hostoffline\" />", "InitialControlState")]
    [InlineData("<GemEquipment><SV Id=\"x\" Name=\"X\" Format=\"U4\" /></GemEquipment>", "SV Id=\"x\"")]
    [InlineData("<GemEquipment><SV Id=\"1\" Name=\"X\" Format=\"U5\" /></GemEquipment>", "Format")]
    [InlineData("<GemEquipment><EC Id=\"1\" Name=\"X\" Format=\"F4\" Default=\"abc\" /></GemEquipment>", "Default")]
    [InlineData("<GemEquipment><SV Id=\"1\" Name=\"X\" Format=\"U4\" InitialValue=\"1 2\" /></GemEquipment>", "InitialValue")]
    [InlineData("<GemEquipment><Alarm Id=\"1\" Text=\"t\" Category=\"9\" SetCeid=\"1\" ClearCeid=\"2\" /></GemEquipment>", "Alarm Id=\"1\"")]
    [InlineData("<GemEquipment><Message Stream=\"1\" Function=\"3\" /></GemEquipment>", "Message")]
    [InlineData("<GemEquipment><Standard Clock=\"abc\" /></GemEquipment>", "Clock")]
    public void Invalid_IsFormatException_WithLocation(string xml, string contains)
        => Assert.Contains(contains, Assert.Throws<FormatException>(() => Hsms().FromXml(XElement.Parse(xml))).Message);

    [Theory]
    [InlineData("<GemEquipment><SV Id=\"2001\" Name=\"A\" Format=\"U4\" /><DV Id=\"2001\" Name=\"B\" Format=\"U4\" /></GemEquipment>")]   // VID 겹침
    [InlineData("<GemEquipment><SV Id=\"1001\" Name=\"A\" Format=\"U4\" /></GemEquipment>")]                                           // 표준 SV와 겹침
    [InlineData("<GemEquipment><CE Id=\"5001\" Name=\"E\" Vids=\"7777\" /></GemEquipment>")]                                           // 모르는 VID
    [InlineData("<GemEquipment><CE Id=\"9001\" Name=\"E\" /><Alarm Id=\"1\" Text=\"t\" Category=\"1\" SetCeid=\"9001\" ClearCeid=\"9002\" /></GemEquipment>")]   // 알람 CE와 겹침
    [InlineData("<GemEquipment VidFormat=\"U1\" />")]                                                                                  // 표준 ID 1001이 U1 밖
    public void DefinitionErrors_SameChecksAsStart(string xml)
        => Assert.Throws<FormatException>(() => Hsms().FromXml(XElement.Parse(xml)));

    [Fact]
    public void TransportElement_KindMismatch_RoleRules()
    {
        Assert.Contains("SecsSerial", Assert.Throws<FormatException>(() => Hsms().FromXml(XElement.Parse("<GemEquipment><SecsSerial Port=\"COM1\" /></GemEquipment>"))).Message);
        Assert.Throws<FormatException>(() => new GemEquipment(new SecsSerial()).FromXml(XElement.Parse("<GemEquipment><SecsHsms /></GemEquipment>")));
        Assert.Throws<FormatException>(() => Hsms().FromXml(XElement.Parse("<GemEquipment><SecsHsms /><SecsSerial /></GemEquipment>")));
        Assert.Contains("Role", Assert.Throws<FormatException>(() => Hsms().FromXml(XElement.Parse("<GemEquipment><SecsHsms Role=\"Host\" /></GemEquipment>"))).Message);
        Assert.Contains("T3", Assert.Throws<FormatException>(() => Hsms().FromXml(XElement.Parse("<GemEquipment><SecsHsms T3=\"10\" /></GemEquipment>"))).Message);
        using var eq = Hsms();
        eq.FromXml(XElement.Parse("<GemEquipment><SecsHsms Role=\"Equipment\" DeviceID=\"7\" /></GemEquipment>"));
        Assert.Equal((SecsRole.Equipment, 7), (((SecsHsms)eq.Transport).Role, ((SecsHsms)eq.Transport).DeviceID));
    }

    [Fact]
    public void OmittedTransportElement_KeepsTransportSettings()
    {
        using var eq = new GemEquipment(new SecsHsms { DeviceID = 9, LocalPort = 6000 });
        eq.FromXml(XElement.Parse("<GemEquipment ModelName=\"X\" />"));
        Assert.Equal((9, 6000), (((SecsHsms)eq.Transport).DeviceID, ((SecsHsms)eq.Transport).LocalPort));
    }

    [Fact]
    public void Invalid_LeavesPreviousSettingsAndDefinitions()
    {
        using var eq = Hsms();
        eq.FromXml(XElement.Parse(SpecExample));
        var before = eq.ToXml();
        Assert.Throws<FormatException>(() => eq.FromXml(XElement.Parse("<GemEquipment ModelName=\"Y\"><SecsHsms DeviceID=\"3\" /><SV Id=\"1\" Name=\"X\" Format=\"Q\" /></GemEquipment>")));
        Assert.True(XNode.DeepEquals(before, eq.ToXml()));
        Assert.Equal(1, ((SecsHsms)eq.Transport).DeviceID);
    }

    [Fact]
    public void WhileRunning_InvalidOperation()
    {
        using var eq = GemKit.Equipment();
        eq.Start();
        Assert.Throws<InvalidOperationException>(() => eq.FromXml(XElement.Parse("<GemEquipment />")));
        Assert.Throws<InvalidOperationException>(() => eq.ApplyConfig(new GemEquipmentConfig()));
    }

    [Fact]
    public void AsciiIdFormats_TextIds()
    {
        using var eq = Hsms();
        eq.FromXml(XElement.Parse("<GemEquipment VidFormat=\"A\" CeidFormat=\"A\"><SV Id=\"TEMP\" Name=\"T\" Format=\"F4\" /><CE Id=\"START\" Name=\"S\" Vids=\"TEMP,Clock\" /></GemEquipment>"));
        Assert.Equal((GemId)"TEMP", eq.StatusVariables.Single().Id);
        Assert.Equal([(GemId)"TEMP", "Clock"], eq.CollectionEvents.Single().DataVariables);   // A 포맷의 표준 ID는 이름
        Assert.Equal(SecsFormat.ASCII, eq.VidFormat);
    }

    [Fact]
    public void ToConfig_UnwritableArrayValue_IsInvalidOperation()
    {
        using var eq = Hsms();
        eq.AddStatusVariable(new GemStatusVariable(1, "Arr", SecsFormat.U4, initialValue: SecsItem.U4(1, 2)));
        Assert.Throws<InvalidOperationException>(eq.ToXml);
    }

    [Fact]
    public void Schema_ValidatesSpecExample_RejectsMissingRequired()
    {
        var xsd = ConfigSchema.Get<GemEquipmentConfig>();
        Assert.Contains("name=\"SecsHsms\"", xsd);
        Assert.Contains("default=\"10000\"", xsd);
        var set = new XmlSchemaSet();
        set.Add(XmlSchema.Read(new StringReader(xsd), null)!);
        XDocument.Parse(SpecExample).Validate(set, (s, e) => Assert.Fail(e.Message));
        var errors = new List<string>();
        XDocument.Parse("<GemEquipment><SV Name=\"X\" Format=\"U4\" /></GemEquipment>").Validate(set, (s, e) => errors.Add(e.Message));
        Assert.Contains(errors, m => m.Contains("Id", StringComparison.Ordinal));      // Id는 필수
    }
}
