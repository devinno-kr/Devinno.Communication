using System.Xml.Linq;
using System.Xml.Schema;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Secs.Hsms;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 스풀링 켜기 설정: 꺼져 있으면 지금과 같고, 켜면 표준 스풀 SV/EC/CE가 생긴다. 저장소 열기·XML
public class GemSpoolSetupTests
{
    static string TempDir() => Path.Combine(Path.GetTempPath(), "devinno-spool-" + Guid.NewGuid());

    static GemEquipment Eq(Action<GemEquipment>? more = null) => GemKit.Equipment(e =>
    {
        e.InitialControlState = GemControlState.OnlineRemote;
        more?.Invoke(e);
    });

    static void Delete(string dir)
    {
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task Off_NoSpoolStandardVariables()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        var s1f12 = await host.Ask("S1F11 W <L>");
        foreach (var id in new[] { 1009, 1010, 1011, 1012 }) Assert.DoesNotContain($"<U4 {id}>", s1f12);
        var s2f30 = await host.Ask("S2F29 W <L>");
        foreach (var id in new[] { 1103, 1104, 1105 }) Assert.DoesNotContain($"<U4 {id}>", s2f30);
        var s1f24 = await host.Ask("S1F23 W <L>");
        foreach (var id in new[] { 1307, 1308, 1309 }) Assert.DoesNotContain($"<U4 {id}>", s1f24);
        Assert.Equal(GemSpoolState.Inactive, eq.SpoolState);
        Assert.False(eq.IsSpoolFull);
        Assert.Equal(0, eq.SpoolCount);
    }

    [Fact]
    public async Task Off_S2F43_S6F23_AreS9F5()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal(5, (await Assert.ThrowsAsync<SecsErrorException>(() => host.SendAsync("S2F43 W <L>"))).Function);
        Assert.Equal(5, (await Assert.ThrowsAsync<SecsErrorException>(() => host.SendAsync("S6F23 W <B 0>"))).Function);
    }

    [Fact]
    public async Task Off_AppCanRegisterS2F43()
    {
        using var eq = Eq(e => e.RegisterMessage(2, 43));
        int got = 0;
        eq.MessageReceived += (s, e) => { Interlocked.Increment(ref got); e.Reply(new SecsMessage(2, 44, false, SecsItem.L())); };
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F44 <L [0]>", await host.Ask("S2F43 W <L>"));
        Assert.Equal(1, got);
    }

    [Fact]
    public async Task On_StandardVariablesDefined()
    {
        var dir = TempDir();
        try
        {
            using var eq = Eq(e => e.SpoolDirectory = dir);
            using var host = await GemKit.Communicating(eq);
            Assert.Equal("S1F4 <L [4] <U4 0> <U4 0> <A \"\"> <A \"\">>", await host.Ask("S1F3 W <L <U4 1009> <U4 1010> <U4 1011> <U4 1012>>"));
            Assert.Equal("S2F14 <L [3] <BOOLEAN T> <U4 0> <BOOLEAN F>>", await host.Ask("S2F13 W <L <U4 1103> <U4 1104> <U4 1105>>"));
            var s1f24 = await host.Ask("S1F23 W <L>");
            Assert.Contains("<U4 1307> <A \"SpoolingActivated\"> <L [1] <U4 1012>>", s1f24);
            Assert.Contains("<U4 1308> <A \"SpoolingDeactivated\"> <L [1] <U4 1010>>", s1f24);
            Assert.Contains("<U4 1309> <A \"SpoolTransmitFailure\"> <L [2] <U4 1009> <U4 1010>>", s1f24);
            Assert.Equal(GemSpoolState.Inactive, eq.SpoolState);
        }
        finally { Delete(dir); }
    }

    [Fact]
    public void On_RegisterS2F43_StartFails()
    {
        var dir = TempDir();
        try
        {
            foreach (var (s, f) in new[] { (2, 43), (6, 23) })
            {
                using var eq = GemKit.Equipment(e => { e.SpoolDirectory = dir; e.RegisterMessage(s, f); });
                Assert.Throws<InvalidOperationException>(() => eq.Start());
                Assert.False(eq.IsStart);
            }
        }
        finally { Delete(dir); }
    }

    [Fact]
    public void On_StandardIdCollision_UseStandardIds()
    {
        var dir = TempDir();
        try
        {
            using var eq = GemKit.Equipment(e =>
            {
                e.SpoolDirectory = dir;
                e.AddStatusVariable(new GemStatusVariable(1009, "Mine", SecsFormat.U4));
            });
            Assert.Throws<InvalidOperationException>(() => eq.Start());
            eq.StandardIds.SpoolCountActual = 5009;
            eq.Start();
            Assert.True(eq.IsStart);
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task Start_SpoolDirectoryInUse_Throws()
    {
        var dir = TempDir();
        try
        {
            using var a = Eq(e => e.SpoolDirectory = dir);
            using var host = await GemKit.Communicating(a);
            using var b = Eq(e => e.SpoolDirectory = dir);
            Assert.ThrowsAny<IOException>(() => b.Start());
            Assert.False(b.IsStart);
            Assert.Equal("S1F4 <L [1] <U4 0>>", await host.Ask("S1F3 W <L <U4 1009>>"));   // 첫째는 계속 동작
        }
        finally { Delete(dir); }
    }

    [Fact]
    public void StopThenStart_ReopensStore()
    {
        var dir = TempDir();
        try
        {
            using var eq = GemKit.Equipment(e => e.SpoolDirectory = dir);
            eq.Start();
            eq.Stop();
            using var other = GemKit.Equipment(e => e.SpoolDirectory = dir);
            other.Start();      // Stop이 잠금을 풀었다
            other.Stop();
            eq.Start();
        }
        finally { Delete(dir); }
    }

    [Fact]
    public void MaxSpoolMessages_Range()
    {
        using var eq = GemKit.Equipment();
        Assert.Equal(1000, eq.MaxSpoolMessages);
        Assert.Throws<ArgumentOutOfRangeException>(() => eq.MaxSpoolMessages = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => eq.MaxSpoolMessages = 1000001);
        eq.MaxSpoolMessages = 1;
        eq.MaxSpoolMessages = 1000000;
        Assert.Equal(1000000, eq.MaxSpoolMessages);
    }

    [Fact]
    public void SpoolDirectory_ChangeWhileRunning_Throws()
    {
        using var eq = GemKit.Equipment();
        eq.Start();
        Assert.Throws<InvalidOperationException>(() => eq.SpoolDirectory = "x");
        Assert.Throws<InvalidOperationException>(() => eq.MaxSpoolMessages = 5);
        Assert.Null(eq.SpoolDirectory);
    }

    [Fact]
    public void Xml_RoundTrip_SpoolSettings()
    {
        using var a = new GemEquipment(new SecsHsms());
        a.SpoolDirectory = "spool-dir";
        a.MaxSpoolMessages = 77;
        a.StandardIds.SpoolCountActual = 5009;
        a.StandardIds.SpoolingActivated = 6307;
        var xml = a.ToXml();
        Assert.Equal("spool-dir", (string?)xml.Attribute("SpoolDirectory"));
        Assert.Equal("77", (string?)xml.Attribute("MaxSpoolMessages"));
        Assert.Equal("5009", (string?)xml.Element("Standard")!.Attribute("SpoolCountActual"));
        using var b = new GemEquipment(new SecsHsms());
        b.FromXml(xml);
        Assert.Equal("spool-dir", b.SpoolDirectory);
        Assert.Equal(77, b.MaxSpoolMessages);
        Assert.Equal((GemId)5009, b.StandardIds.SpoolCountActual);
        Assert.Equal((GemId)6307, b.StandardIds.SpoolingActivated);
        Assert.True(XNode.DeepEquals(xml, b.ToXml()));

        // 스키마 검증
        var set = new XmlSchemaSet();
        set.Add(XmlSchema.Read(new StringReader(ConfigSchema.Get<GemEquipmentConfig>()), null)!);
        new XDocument(xml).Validate(set, (_, e) => Assert.Fail(e.Message));

        // 범위 밖
        Assert.Throws<FormatException>(() => new GemEquipment(new SecsHsms()).FromXml(XElement.Parse("<GemEquipment MaxSpoolMessages=\"0\" />")));

        // 기본값이면 속성을 쓰지 않는다
        using var c = new GemEquipment(new SecsHsms());
        Assert.Null(c.ToXml().Attribute("SpoolDirectory"));
    }
}
