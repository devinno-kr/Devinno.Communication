using Devinno.Communication.Mqtt;
using MQTTnet.Protocol;

namespace Devinno.Communication.Tests.Mqtt;

public class MQTypesTests
{
    [Fact]
    public void Qos_ValuesMatchMqttLevels()
    {
        Assert.Equal((int)MqttQualityOfServiceLevel.AtMostOnce, (int)MQQos.MostOnce);
        Assert.Equal((int)MqttQualityOfServiceLevel.AtLeastOnce, (int)MQQos.LeastOnce);
        Assert.Equal((int)MqttQualityOfServiceLevel.ExactlyOnce, (int)MQQos.ExactlyOnce);
        Assert.Equal((0, 1, 2, 0x80), ((int)MQQos.MostOnce, (int)MQQos.LeastOnce, (int)MQQos.ExactlyOnce, (int)MQQos.GrantedFailure));
        Assert.Equal(new[] { "DropOldest", "DropNewest" }, Enum.GetNames<MQOverflowPolicy>());
        Assert.Equal(MQOverflowPolicy.DropOldest, default(MQOverflowPolicy));
    }

    [Fact]
    public void Subscribe_DefaultQos_IsMostOnce()
    {
        var s = new MQSubscribe("sensor/#");
        Assert.Equal(("sensor/#", MQQos.MostOnce), (s.Topic, s.Qos));
        var s2 = new MQSubscribe("a/+/c", MQQos.ExactlyOnce);
        Assert.Equal(("a/+/c", MQQos.ExactlyOnce), (s2.Topic, s2.Qos));
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("#")]
    [InlineData("+")]
    [InlineData("a/#")]
    [InlineData("+/b/+")]
    [InlineData("/")]
    [InlineData("온도/1층")]
    public void Filter_Valid(string filter) => Assert.Null(MQTopic.FilterError(filter));

    [Theory]
    [InlineData("", "비어")]
    [InlineData("a/#/b", "#")]
    [InlineData("a#", "#")]
    [InlineData("a/b+", "+")]
    [InlineData("a\0b", "NUL")]
    public void Filter_Invalid(string filter, string mustContain) => Assert.Contains(mustContain, MQTopic.FilterError(filter));

    [Theory]
    [InlineData("a/b")]
    [InlineData("/test-alive")]
    [InlineData("온도")]
    public void Name_Valid(string topic) => Assert.Null(MQTopic.NameError(topic));

    [Theory]
    [InlineData("", "비어")]
    [InlineData("a/#", "와일드카드")]
    [InlineData("a/+/b", "와일드카드")]
    [InlineData("a\0", "NUL")]
    public void Name_Invalid(string topic, string mustContain) => Assert.Contains(mustContain, MQTopic.NameError(topic));

    [Fact]
    public void Topic_LongerThan65535Utf8Bytes_IsInvalid()
    {
        Assert.Null(MQTopic.NameError(new string('a', 65535)));
        Assert.Contains("65535", MQTopic.NameError(new string('a', 65536)));
        Assert.Contains("65535", MQTopic.FilterError(new string('가', 21846)));   // 3바이트 × 21846 = 65538
    }

    [Fact]
    public void Subscribe_InvalidArguments_Throw()
    {
        Assert.Equal("topic", Assert.Throws<ArgumentNullException>(() => new MQSubscribe(null!)).ParamName);
        Assert.Equal("topic", Assert.Throws<ArgumentException>(() => new MQSubscribe("")).ParamName);
        Assert.Equal("topic", Assert.Throws<ArgumentException>(() => new MQSubscribe("a/#/b")).ParamName);
        Assert.Equal("qos", Assert.Throws<ArgumentOutOfRangeException>(() => new MQSubscribe("a", MQQos.GrantedFailure)).ParamName);
        Assert.Equal("qos", Assert.Throws<ArgumentOutOfRangeException>(() => new MQSubscribe("a", (MQQos)3)).ParamName);
    }

    [Fact]
    public void ReceiveArgs_KeepsTopicAndPayload()
    {
        var data = new byte[] { 1, 2, 3 };
        var e = new MQReceiveArgs("t/1", data);
        Assert.Equal("t/1", e.Topic);
        Assert.Same(data, e.Datas);
    }
}
