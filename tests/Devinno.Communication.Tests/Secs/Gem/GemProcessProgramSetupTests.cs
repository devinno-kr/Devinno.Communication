using System.Xml.Linq;
using System.Xml.Schema;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Secs.Hsms;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 레시피 켜기 설정(Plan 16 Task 3): 꺼짐 회귀, 켰을 때만 표준 SV/DV/CE, 등록 메시지와의 관계, XML
public class GemProcessProgramSetupTests
{
    static GemEquipment Eq(Action<GemEquipment>? more = null) => GemKit.Equipment(e =>
    {
        GemKit.Sample(e);
        e.InitialControlState = GemControlState.OnlineRemote;
        more?.Invoke(e);
    });

    [Fact]
    public async Task Off_Stream7_IsS9F3()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        var ex = await Assert.ThrowsAsync<SecsErrorException>(() => host.SendAsync("S7F19 W"));
        Assert.Contains("S9F3", ex.Message);
        var names = await host.Ask("S1F11 W <L>");
        Assert.DoesNotContain("<U4 1013>", names);
        Assert.DoesNotContain("<U4 1014>", names);
    }

    [Fact]
    public async Task Off_AppCanRegisterS7F1()
    {
        using var eq = Eq(e => e.RegisterMessage(7, 1));
        eq.MessageReceived += (s, e) => e.Reply(new SecsMessage(7, 2, false, SecsItem.B(0)));
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S7F2 <B 0x00>", await host.Ask("S7F1 W <L <A \"R\"> <U4 1>>"));
    }

    [Fact]
    public async Task On_StandardsDefined()
    {
        using var eq = Eq(e => e.ProcessProgramStore = new InMemoryProcessProgramStore());
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S1F4 <L [2] <A \"\"> <U1 1>>", await host.Ask("S1F3 W <L <U4 1013> <U4 1014>>"));
        var dvs = await host.Ask("S1F21 W <L>");
        Assert.Contains("<U4 1202>", dvs);
        Assert.Contains("<U4 1203>", dvs);
        var ces = await host.Ask("S1F23 W <L <U4 1310> <U4 1311>>");
        Assert.Contains("<U4 1310> <A \"ProcessProgramChange\"> <L [2] <U4 1202> <U4 1203>>", ces);
        Assert.Contains("<U4 1311> <A \"ProcessProgramSelected\"> <L [1] <U4 1013>>", ces);
    }

    [Fact]
    public void On_RegisteredS7_StartFails()
    {
        using var eq = Eq(e => { e.ProcessProgramStore = new InMemoryProcessProgramStore(); e.RegisterMessage(7, 1); });
        var ex = Assert.Throws<InvalidOperationException>(() => eq.Start());
        Assert.Contains("S7F1", ex.Message);
        Assert.False(eq.IsStart);
    }

    [Fact]
    public void On_StandardIdCollision_UseStandardIds()
    {
        using var eq = Eq(e =>
        {
            e.ProcessProgramStore = new InMemoryProcessProgramStore();
            e.AddStatusVariable(new GemStatusVariable(1013, "Mine", SecsFormat.U4));
        });
        Assert.Throws<InvalidOperationException>(() => eq.Start());
        eq.StandardIds.PPExecName = 5013;
        eq.Start();
        Assert.True(eq.IsStart);
    }

    [Fact]
    public void MaxProcessProgramSize_Range()
    {
        using var eq = GemKit.Equipment();
        Assert.Equal(4194304, eq.MaxProcessProgramSize);
        Assert.Throws<ArgumentOutOfRangeException>(() => eq.MaxProcessProgramSize = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => eq.MaxProcessProgramSize = 16777216);
        eq.MaxProcessProgramSize = 1;
        eq.MaxProcessProgramSize = 16777215;
        Assert.Equal(16777215, eq.MaxProcessProgramSize);
    }

    [Fact]
    public void Store_ChangeWhileRunning_Throws()
    {
        using var eq = GemKit.Equipment();
        eq.Start();
        Assert.Throws<InvalidOperationException>(() => eq.ProcessProgramStore = new InMemoryProcessProgramStore());
        Assert.Throws<InvalidOperationException>(() => eq.MaxProcessProgramSize = 5);
        Assert.Null(eq.ProcessProgramStore);
    }

    [Fact]
    public void Xml_RoundTrip_ProcessProgramSettings()
    {
        using var a = new GemEquipment(new SecsHsms());
        a.ProcessProgramStore = new GemProcessProgramFolder("pp-dir");
        a.MaxProcessProgramSize = 1000;
        a.StandardIds.PPExecName = 5013;
        a.StandardIds.ProcessProgramSelected = 6311;
        var xml = a.ToXml();
        Assert.Equal("pp-dir", (string?)xml.Attribute("ProcessProgramDirectory"));
        Assert.Equal("1000", (string?)xml.Attribute("MaxProcessProgramSize"));
        Assert.Equal("5013", (string?)xml.Element("Standard")!.Attribute("PPExecName"));
        using var b = new GemEquipment(new SecsHsms());
        b.FromXml(xml);
        Assert.Equal("pp-dir", Assert.IsType<GemProcessProgramFolder>(b.ProcessProgramStore).Directory);
        Assert.Equal(1000, b.MaxProcessProgramSize);
        Assert.Equal((GemId)5013, b.StandardIds.PPExecName);
        Assert.Equal((GemId)6311, b.StandardIds.ProcessProgramSelected);
        Assert.True(XNode.DeepEquals(xml, b.ToXml()));

        var set = new XmlSchemaSet();
        set.Add(XmlSchema.Read(new StringReader(ConfigSchema.Get<GemEquipmentConfig>()), null)!);
        new XDocument(xml).Validate(set, (_, e) => Assert.Fail(e.Message));

        Assert.Throws<FormatException>(() => new GemEquipment(new SecsHsms()).FromXml(XElement.Parse("<GemEquipment ProcessProgramDirectory=\"\" />")));
        Assert.Throws<FormatException>(() => new GemEquipment(new SecsHsms()).FromXml(XElement.Parse("<GemEquipment MaxProcessProgramSize=\"0\" />")));

        using var c = new GemEquipment(new SecsHsms());
        Assert.Null(c.ToXml().Attribute("ProcessProgramDirectory"));
        Assert.Equal("4194304", (string?)c.ToXml().Attribute("MaxProcessProgramSize"));     // 다른 정수 설정처럼 기본값도 씀
        c.ProcessProgramStore = new InMemoryProcessProgramStore();          // 앱 저장소는 XML로 쓰지 않음
        Assert.Null(c.ToXml().Attribute("ProcessProgramDirectory"));
    }

    [Fact]
    public void Xml_StandardIdKinds()
    {
        using var eq = new GemEquipment(new SecsHsms());
        eq.FromXml(XElement.Parse("<GemEquipment VidFormat=\"A\" CeidFormat=\"A\"><Standard ProcessProgramChange=\"PPC\" PPExecName=\"EXEC\" /></GemEquipment>"));
        Assert.Equal(new GemId("PPC"), eq.StandardIds.ProcessProgramChange);
        Assert.Equal(new GemId("EXEC"), eq.StandardIds.PPExecName);
        Assert.Throws<FormatException>(() => new GemEquipment(new SecsHsms()).FromXml(XElement.Parse("<GemEquipment CeidFormat=\"A\"><Standard PPChangeName=\"X\" /></GemEquipment>")));   // DV는 VidFormat(U4) 쪽
    }
}
