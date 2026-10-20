using System.Text;

namespace Devinno.Communication.Mqtt;

/// <summary>
/// Quality of service (QoS) level of an MQTT message. The values are the same as the QoS values of the MQTT specification.
/// </summary>
public enum MQQos : byte
{
    /// <summary>At least once delivery (QoS 1).</summary>
    LeastOnce = 1,
    /// <summary>At most once delivery (QoS 0).</summary>
    MostOnce = 0,
    /// <summary>Exactly once delivery (QoS 2).</summary>
    ExactlyOnce = 2,
    /// <summary>Subscription failure (0x80). This is a result value that the broker returns in SUBACK; it cannot be used in publish or subscribe requests.</summary>
    GrantedFailure = 0x80,
}

/// <summary>
/// Determines which message is discarded when the receive queue of MQClient is full (<c>MQClient.MaxPendingMessages</c>).
/// </summary>
public enum MQOverflowPolicy
{
    /// <summary>Discards the oldest message in the queue and adds the new one (keeps the latest values). This is the default.</summary>
    DropOldest,
    /// <summary>Discards the newly arrived message (keeps the messages that arrived first).</summary>
    DropNewest,
}

/// <summary>
/// MQTT subscription information (topic filter and QoS).
/// </summary>
/// <remarks>
/// Wildcards can be used in the topic filter. <c>+</c> matches one level (e.g., <c>a/+/c</c>), and <c>#</c> can be used only alone as the last level (e.g., <c>a/#</c>, <c>#</c>).
/// The filter is validated on creation, so a filter that the broker would reject cannot be created.
/// </remarks>
public class MQSubscribe
{
    /// <summary>Creates the subscription information.</summary>
    /// <param name="topic">Topic filter to subscribe to (must not be empty, at most 65535 bytes in UTF-8).</param>
    /// <param name="qos">QoS to request. The default is MostOnce (QoS 0); GrantedFailure cannot be used.</param>
    /// <exception cref="ArgumentNullException">topic is null.</exception>
    /// <exception cref="ArgumentException">The topic filter format is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">qos is not MostOnce, LeastOnce, or ExactlyOnce.</exception>
    public MQSubscribe(string topic, MQQos qos = MQQos.MostOnce)
    {
        MQTopic.CheckFilter(topic, nameof(topic));
        MQTopic.CheckQos(qos, nameof(qos));
        Topic = topic;
        Qos = qos;
    }

    /// <summary>Subscription topic filter.</summary>
    public string Topic { get; }
    /// <summary>The requested QoS.</summary>
    public MQQos Qos { get; }
}

/// <summary>
/// Event arguments for when an MQTT message is received.
/// </summary>
/// <param name="Topic">Topic of the received message.</param>
/// <param name="Datas">Payload of the received message.</param>
public class MQReceiveArgs(string Topic, byte[] Datas) : EventArgs
{
    /// <summary>Topic of the received message.</summary>
    public string Topic { get; } = Topic;
    /// <summary>Payload of the received message.</summary>
    public byte[] Datas { get; } = Datas;
}

/// <summary>
/// Data of the event raised when a Publish is not sent because the client is not connected.
/// </summary>
/// <param name="Topic">Topic of the message that was not sent.</param>
/// <param name="Qos">QoS of the message that was not sent.</param>
public class MQPublishDiscardedEventArgs(string Topic, MQQos Qos) : EventArgs
{
    /// <summary>Topic of the message that was not sent.</summary>
    public string Topic { get; } = Topic;
    /// <summary>QoS of the message that was not sent.</summary>
    public MQQos Qos { get; } = Qos;
}

// MQTT 토픽·QoS 검사. 잘못된 값은 MQTTnet에 넘기기 전에 호출한 곳에서 ArgumentException으로 막는다(규격보다 느슨한 MQTTnet 검사에 기대지 않음).
internal static class MQTopic
{
    // 발행 토픽 이름의 오류 설명. 올바르면 null
    public static string? NameError(string? topic)
    {
        if (string.IsNullOrEmpty(topic)) return "비어 있으면 안 됨";
        if (topic.IndexOfAny(['#', '+']) >= 0) return "발행 토픽에는 와일드카드(#, +)를 쓸 수 없음";
        return CommonError(topic);
    }

    // 구독 토픽 필터의 오류 설명. 올바르면 null
    public static string? FilterError(string? filter)
    {
        if (string.IsNullOrEmpty(filter)) return "비어 있으면 안 됨";
        var levels = filter.Split('/');
        for (int i = 0; i < levels.Length; i++)
        {
            var level = levels[i];
            if (level.Contains('#') && (level != "#" || i != levels.Length - 1)) return "#은 마지막 단계에 혼자만 쓸 수 있음 (예: a/#)";
            if (level.Contains('+') && level != "+") return "+는 한 단계에 혼자만 쓸 수 있음 (예: a/+/c)";
        }
        return CommonError(filter);
    }

    static string? CommonError(string topic)
    {
        if (topic.Contains('\0')) return "NUL 문자(U+0000)를 쓸 수 없음";
        if (Encoding.UTF8.GetByteCount(topic) > 65535) return "UTF-8로 65535바이트 이하여야 함";
        return null;
    }

    // 발행·구독 요청에 쓸 수 있는 QoS인지 (GrantedFailure와 정의되지 않은 값은 안 됨)
    public static bool IsRequestQos(MQQos qos) => qos is MQQos.MostOnce or MQQos.LeastOnce or MQQos.ExactlyOnce;

    public static void CheckName(string topic, string paramName)
    {
        ArgumentNullException.ThrowIfNull(topic, paramName);
        if (NameError(topic) is { } e) throw new ArgumentException($"발행 토픽이 잘못되었습니다: {e}", paramName);
    }

    public static void CheckFilter(string topic, string paramName)
    {
        ArgumentNullException.ThrowIfNull(topic, paramName);
        if (FilterError(topic) is { } e) throw new ArgumentException($"구독 토픽 필터가 잘못되었습니다: {e}", paramName);
    }

    public static void CheckQos(MQQos qos, string paramName)
    {
        if (!IsRequestQos(qos))
            throw new ArgumentOutOfRangeException(paramName, qos, "QoS는 MostOnce, LeastOnce, ExactlyOnce 중 하나여야 합니다 (GrantedFailure는 브로커가 돌려주는 결과값).");
    }
}
