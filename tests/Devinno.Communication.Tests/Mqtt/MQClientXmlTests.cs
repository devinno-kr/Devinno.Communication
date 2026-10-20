using System.Xml.Linq;
using System.Xml.Schema;
using Devinno.Communication.Mqtt;

namespace Devinno.Communication.Tests.Mqtt;

public class MQClientXmlTests
{
    static MQClient Configured()
    {
        var mq = new MQClient();
        mq.ApplyConfig(new MQClientConfig
        {
            BrokerHostName = "10.0.0.5", BrokerPort = 8883, ClientID = "dev01", UserName = "user", AliveTopic = "plant/alive",
            AliveInterval = 500, AliveTimeout = 2000, MaxPendingMessages = 5000, OverflowPolicy = MQOverflowPolicy.DropNewest,
            Subscribes = [new() { Topic = "sensor/#", Qos = MQQos.LeastOnce }, new() { Topic = "cmd/+/run" }],
        });
        return mq;
    }

    [Fact]
    public void RoundTrip_KeepsSettingsAndSubscriptions()
    {
        using var a = Configured();
        var x = a.ToXml();
        Assert.Equal("MQClient", x.Name.LocalName);
        Assert.Equal(("10.0.0.5", "8883", "dev01", "user", "plant/alive"),
            (x.Attribute("BrokerHostName")!.Value, x.Attribute("BrokerPort")!.Value, x.Attribute("ClientID")!.Value, x.Attribute("UserName")!.Value, x.Attribute("AliveTopic")!.Value));
        Assert.Equal(("500", "2000", "5000", "DropNewest"),
            (x.Attribute("AliveInterval")!.Value, x.Attribute("AliveTimeout")!.Value, x.Attribute("MaxPendingMessages")!.Value, x.Attribute("OverflowPolicy")!.Value));
        Assert.Equal(new[] { "sensor/#", "cmd/+/run" }, x.Elements("Subscribe").Select(e => e.Attribute("Topic")!.Value));

        using var b = new MQClient();
        b.FromXml(x);
        Assert.Equal(("10.0.0.5", 8883, "dev01", "user", "plant/alive"), (b.BrokerHostName, b.BrokerPort, b.ClientID, b.UserName, b.AliveTopic));
        Assert.Equal((500, 2000, 5000, MQOverflowPolicy.DropNewest), (b.AliveInterval, b.AliveTimeout, b.MaxPendingMessages, b.OverflowPolicy));
        Assert.Equal(new[] { ("sensor/#", MQQos.LeastOnce), ("cmd/+/run", MQQos.MostOnce) }, b.Subscribes.Select(s => (s.Topic, s.Qos)));
        Assert.Equal(x.ToString(), b.ToXml().ToString());
    }

    [Fact]
    public void HandWritten_WithDefaults_DuplicateTopicLastWins()
    {
        using var c = new MQClient();
        c.FromXml(XElement.Parse("""
            <MQClient BrokerHostName="192.168.0.10">
              <Subscribe Topic="sensor/#" Qos="LeastOnce" />
              <Subscribe Topic="cmd" />
              <Subscribe Topic="sensor/#" Qos="ExactlyOnce" />
            </MQClient>
            """));
        Assert.Equal(("192.168.0.10", 1883, "/test-alive"), (c.BrokerHostName, c.BrokerPort, c.AliveTopic));
        Assert.Equal((1000, 3000, 100_000, MQOverflowPolicy.DropOldest), (c.AliveInterval, c.AliveTimeout, c.MaxPendingMessages, c.OverflowPolicy));
        Assert.Null(c.ClientID);
        Assert.Null(c.UserName);
        Assert.Equal(new[] { ("cmd", MQQos.MostOnce), ("sensor/#", MQQos.ExactlyOnce) }, c.Subscribes.Select(s => (s.Topic, s.Qos)));
    }

    [Fact]
    public void AliveTopicNull_SavedAsEmpty_LoadsDisabled()
    {
        using var a = new MQClient { AliveTopic = null };
        var x = a.ToXml();
        Assert.Equal("", x.Attribute("AliveTopic")!.Value);
        using var b = new MQClient();
        b.FromXml(x);
        Assert.True(string.IsNullOrEmpty(b.AliveTopic));
    }

    [Fact]
    public void Password_IsNeverSaved()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mqclient-{Guid.NewGuid():N}.xml");
        try
        {
            using var mq = new MQClient { BrokerPort = Mosquitto.ClosedPort(), AliveTopic = null };
            mq.Start("dev01", "user", "p@ss-SECRET-123");
            mq.Stop();
            var x = mq.ToXml();
            Assert.DoesNotContain("p@ss-SECRET-123", x.ToString());
            Assert.Null(x.Attribute("Password"));
            Assert.Equal(("dev01", "user"), (x.Attribute("ClientID")!.Value, x.Attribute("UserName")!.Value));
            mq.SaveXml(path);
            Assert.DoesNotContain("p@ss-SECRET-123", File.ReadAllText(path));
            Assert.Null(typeof(MQClientConfig).GetProperty("Password"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData("""<MQClient BrokerPort="0" />""", "BrokerPort", "1~65535")]
    [InlineData("""<MQClient BrokerPort="65536" />""", "BrokerPort", "1~65535")]
    [InlineData("""<MQClient BrokerHostName="" />""", "BrokerHostName", "비어")]
    [InlineData("""<MQClient AliveTopic="alive/#" />""", "AliveTopic", "와일드카드")]
    [InlineData("""<MQClient Password="x" />""", "Password", "사용할 수 있는 속성")]
    [InlineData("""<MQClient AliveInterval="99" />""", "AliveInterval", "100~60000")]
    [InlineData("""<MQClient AliveTimeout="60001" />""", "AliveTimeout", "100~60000")]
    [InlineData("""<MQClient MaxPendingMessages="0" />""", "MaxPendingMessages", "1~10000000")]
    [InlineData("""<MQClient OverflowPolicy="DropAll" />""", "OverflowPolicy", "DropOldest, DropNewest")]
    [InlineData("""<MQClient><Sub Topic="a" /></MQClient>""", "Sub", "Subscribe")]
    [InlineData("""<MQClient><Subscribe Qos="LeastOnce" /></MQClient>""", "Topic", "필수")]
    [InlineData("""<MQClient><Subscribe Topic="a/#/b" /></MQClient>""", "Topic", "#")]
    [InlineData("""<MQClient><Subscribe Topic="" /></MQClient>""", "Topic", "비어")]
    [InlineData("""<MQClient><Subscribe Topic="a" Qos="GrantedFailure" /></MQClient>""", "Qos", "GrantedFailure")]
    [InlineData("""<MQClient><Subscribe Topic="a" Qos="leastonce" /></MQClient>""", "Qos", "LeastOnce")]
    [InlineData("""<MQClient><Subscribe Topic="a" Qos="1" /></MQClient>""", "Qos", "MostOnce")]
    [InlineData("""<MQClient><Subscribe Topic="a" Retain="true" /></MQClient>""", "Retain", "Topic")]
    public void FromXml_Invalid_MessageIsPrecise(string xml, string mustContain1, string mustContain2)
    {
        var ex = Assert.Throws<FormatException>(() => new MQClient().FromXml(XElement.Parse(xml)));
        Assert.Contains(mustContain1, ex.Message);
        Assert.Contains(mustContain2, ex.Message);
    }

    [Fact]
    public void InvalidXml_LeavesSettingsUnchanged()
    {
        using var c = Configured();
        var before = c.ToXml().ToString();
        Assert.Throws<FormatException>(() => c.FromXml(XElement.Parse("""<MQClient BrokerHostName="1.2.3.4" ClientID="x"><Subscribe Topic="ok" /><Subscribe Topic="a" Qos="GrantedFailure" /></MQClient>""")));
        Assert.Throws<FormatException>(() => c.ApplyConfig(new MQClientConfig { BrokerHostName = "1.2.3.4", Subscribes = [new() { Topic = "x", Qos = (MQQos)7 }] }));
        Assert.Throws<FormatException>(() => c.ApplyConfig(new MQClientConfig { BrokerHostName = "1.2.3.4", OverflowPolicy = (MQOverflowPolicy)9 }));
        Assert.Throws<FormatException>(() => c.ApplyConfig(new MQClientConfig { BrokerHostName = "1.2.3.4", Subscribes = [new() { Topic = "ok" }, null!] }));
        Assert.Equal(before, c.ToXml().ToString());
    }

    [Fact]
    public void FromXml_RootNameMismatch_Throws()
    {
        var ex = Assert.Throws<FormatException>(() => new MQClient().FromXml(XElement.Parse("<MqttClient />")));
        Assert.Contains("MQClient", ex.Message);
    }

    [Fact]
    public void FromXml_WhileRunning_Throws_AndKeepsState()
    {
        int port = Mosquitto.ClosedPort();
        using var mq = new MQClient { BrokerPort = port, AliveTopic = null };
        mq.Subscribe("keep");
        mq.Start();
        using var other = Configured();
        Assert.Throws<InvalidOperationException>(() => mq.FromXml(other.ToXml()));
        Assert.Throws<InvalidOperationException>(() => mq.ApplyConfig(other.ToConfig()));
        Assert.Equal(port, mq.BrokerPort);
        Assert.Equal(new[] { "keep" }, mq.Subscribes.Select(s => s.Topic));
        mq.Stop();
        mq.FromXml(other.ToXml());
        Assert.Equal("10.0.0.5", mq.BrokerHostName);
    }

    [Fact]
    public void Schema_Compiles_ValidatesOwnOutput_MarksTopicRequired()
    {
        var xsd = Devinno.Communication.ConfigSchema.Get<MQClientConfig>();
        var set = new XmlSchemaSet();
        set.Add(XmlSchema.Read(new StringReader(xsd), null)!);
        set.Compile();

        using var mq = Configured();
        var errors = new List<string>();
        new XDocument(mq.ToXml()).Validate(set, (_, e) => errors.Add(e.Message));
        Assert.Empty(errors);

        Assert.Contains("default=\"1883\"", xsd);
        Assert.Contains("default=\"/test-alive\"", xsd);
        Assert.Contains("default=\"3000\"", xsd);
        Assert.Contains("default=\"100000\"", xsd);
        Assert.Contains("default=\"DropOldest\"", xsd);
        Assert.Contains("default=\"MostOnce\"", xsd);
        Assert.DoesNotContain("Password", xsd);
        var ct = (XmlSchemaComplexType)set.GlobalTypes.Values.Cast<XmlSchemaType>().Single(t => t.Name == nameof(MQSubscribeConfig));
        Assert.Equal(XmlSchemaUse.Required, ((XmlSchemaAttribute)ct.AttributeUses[new System.Xml.XmlQualifiedName("Topic")]!).Use);

        var bad = new List<string>();
        new XDocument(XElement.Parse("""<MQClient><Subscribe Qos="LeastOnce" /></MQClient>""")).Validate(set, (_, e) => bad.Add(e.Message));
        Assert.NotEmpty(bad);
    }

    [Fact]
    public void SaveXml_LoadXml_FileRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mqclient-{Guid.NewGuid():N}.xml");
        try
        {
            using var a = Configured();
            a.SaveXml(path);
            using var b = new MQClient();
            b.LoadXml(path);
            Assert.Equal(a.ToXml().ToString(), b.ToXml().ToString());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void GeneratedClientID_IsNotSaved()
    {
        using var mq = new MQClient { BrokerPort = Mosquitto.ClosedPort(), AliveTopic = null };
        mq.Start();
        mq.Stop();
        Assert.False(string.IsNullOrEmpty(mq.ClientID));
        var x = mq.ToXml();
        Assert.True(string.IsNullOrEmpty(x.Attribute("ClientID")?.Value));
        Assert.DoesNotContain(mq.ClientID!, x.ToString());
    }
}
