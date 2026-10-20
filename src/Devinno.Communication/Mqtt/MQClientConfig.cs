using System.Xml.Serialization;
using Devinno.Communication.Internal;

namespace Devinno.Communication.Mqtt;

/// <summary>
/// The connection settings and subscription list of <see cref="MQClient"/>. In XML it is the &lt;MQClient&gt; element.
/// </summary>
/// <remarks>
/// <para>Omitted attributes use their default values. You can check the format by generating the XSD with <see cref="ConfigSchema.Get{T}"/>.</para>
/// <para>The password is not put in the configuration so that it is never stored in plain text. Pass it through <see cref="MQClient.Start(string, string, string)"/>.</para>
/// <para>Setting AliveTopic to an empty string ("") turns off the alive check. If there are several &lt;Subscribe&gt; elements with the same Topic, the last one is used.</para>
/// </remarks>
/// <example>
/// <code>
/// &lt;MQClient BrokerHostName="127.0.0.1" BrokerPort="1883" ClientID="dev01" UserName="user"
///           AliveTopic="/test-alive" AliveInterval="1000" AliveTimeout="3000" MaxPendingMessages="100000" OverflowPolicy="DropOldest"&gt;
///   &lt;Subscribe Topic="sensor/#" Qos="LeastOnce" /&gt;
/// &lt;/MQClient&gt;
/// </code>
/// </example>
[XmlRoot(nameof(MQClient))]
public class MQClientConfig
{
    /// <summary>Host name or IP of the broker (default: "127.0.0.1").</summary>
    [XmlAttribute] public string BrokerHostName { get; set; } = "127.0.0.1";
    /// <summary>Broker port, 1 to 65535 (default: 1883).</summary>
    [XmlAttribute] public int BrokerPort { get; set; } = 1883;
    /// <summary>MQTT client identifier. If omitted, a GUID is generated on Start.</summary>
    [XmlAttribute] public string? ClientID { get; set; }
    /// <summary>Authentication user name. If omitted, the client connects without authentication.</summary>
    [XmlAttribute] public string? UserName { get; set; }
    /// <summary>Topic for the alive check (default: "/test-alive"). An empty string turns off the alive check. Wildcards cannot be used.</summary>
    [XmlAttribute] public string? AliveTopic { get; set; } = "/test-alive";
    /// <summary>Alive check interval in ms, 100 to 60000 (default: 1000).</summary>
    [XmlAttribute] public int AliveInterval { get; set; } = 1000;
    /// <summary>Time to wait for the alive PUBACK in ms, 100 to 60000 (default: 3000).</summary>
    [XmlAttribute] public int AliveTimeout { get; set; } = 3000;
    /// <summary>Upper limit of the receive queue, 1 to 10000000 (default: 100000).</summary>
    [XmlAttribute] public int MaxPendingMessages { get; set; } = 100_000;
    /// <summary>Which message to discard when the receive queue is full (default: DropOldest).</summary>
    [XmlAttribute] public MQOverflowPolicy OverflowPolicy { get; set; } = MQOverflowPolicy.DropOldest;
    /// <summary>Subscription list.</summary>
    [XmlElement("Subscribe")] public List<MQSubscribeConfig> Subscribes { get; set; } = [];

    // 값을 검사한다. 실패하면 FormatException.
    internal void Validate()
    {
        const string e = nameof(MQClient);
        XmlConfig.NotEmpty(e, nameof(BrokerHostName), BrokerHostName);
        XmlConfig.Range(e, nameof(BrokerPort), BrokerPort, 1, 65535);
        if (!string.IsNullOrEmpty(AliveTopic) && MQTopic.NameError(AliveTopic) is { } error)
            throw XmlConfig.Invalid(e, nameof(AliveTopic), AliveTopic, error);
        XmlConfig.Range(e, nameof(AliveInterval), AliveInterval, 100, 60000);
        XmlConfig.Range(e, nameof(AliveTimeout), AliveTimeout, 100, 60000);
        XmlConfig.Range(e, nameof(MaxPendingMessages), MaxPendingMessages, 1, 10_000_000);
        if (OverflowPolicy is not (MQOverflowPolicy.DropOldest or MQOverflowPolicy.DropNewest))
            throw XmlConfig.Invalid(e, nameof(OverflowPolicy), XmlConfig.EnumName(OverflowPolicy), "DropOldest, DropNewest");
        foreach (var s in Subscribes ?? [])
            (s ?? throw XmlConfig.Invalid(e, nameof(Subscribes), "null", "Subscribe 항목은 null일 수 없습니다")).Validate();
    }
}

/// <summary>
/// A single subscription item of <see cref="MQClient"/>. In XML it is the &lt;Subscribe&gt; element.
/// </summary>
/// <remarks>Topic is required. Qos is one of MostOnce (default), LeastOnce, or ExactlyOnce; GrantedFailure cannot be used (it is a result value returned by the broker).</remarks>
/// <example>
/// <code>
/// &lt;Subscribe Topic="sensor/#" Qos="LeastOnce" /&gt;
/// </code>
/// </example>
public class MQSubscribeConfig
{
    /// <summary>Subscription topic filter (required).</summary>
    [XmlAttribute, ConfigRequired] public string Topic { get; set; } = "";
    /// <summary>The QoS to request (default: MostOnce).</summary>
    [XmlAttribute] public MQQos Qos { get; set; } = MQQos.MostOnce;

    // 값을 검사한다. 실패하면 FormatException.
    internal void Validate()
    {
        var e = $"Subscribe Topic=\"{Topic}\"";
        if (MQTopic.FilterError(Topic) is { } error) throw XmlConfig.Invalid(e, nameof(Topic), Topic ?? "", error);
        if (!MQTopic.IsRequestQos(Qos))
            throw XmlConfig.Invalid(e, nameof(Qos), XmlConfig.EnumName(Qos), "MostOnce, LeastOnce, ExactlyOnce 중 하나 (GrantedFailure는 브로커가 돌려주는 결과값)");
    }
}
