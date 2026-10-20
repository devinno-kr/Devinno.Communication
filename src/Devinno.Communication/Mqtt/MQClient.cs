using System.Buffers;
using System.Text;
using System.Threading.Channels;
using System.Xml.Linq;
using Devinno.Communication.Internal;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace Devinno.Communication.Mqtt;

/// <summary>
/// Client that connects to an MQTT broker to publish (Publish) and subscribe (Subscribe) to messages. It wraps MQTTnet 5.x.
/// </summary>
/// <remarks>
/// <para>Calling <see cref="Start()"/> makes a dedicated background thread connect to <see cref="BrokerHostName"/>:<see cref="BrokerPort"/>, and if the connection is lost it reconnects at 1-second intervals (a rejected authentication is also retried at the same interval).
/// On every connection, all subscriptions in <see cref="Subscribes"/> are requested again. It uses MQTT 3.1.1, CleanSession = true, and a Keep Alive of 15 seconds, and does not use TLS.
/// It waits up to 10 seconds for the connection (CONNACK) and subscription responses. Credentials are sent only when both a user name and a password are present.</para>
/// <para><b>Receive queue</b>: In the MQTTnet receive handler, a received message is copied (topic and payload only) into an internal queue (up to <see cref="MaxPendingMessages"/> items) and the handler returns immediately.
/// So PUBACK and Keep Alive are sent right away regardless of user processing. <see cref="Received"/> is raised one message at a time, in the order received, by a dedicated dispatcher thread ("MQClient.Received").
/// The QoS 1/2 delivery guarantee extends only as far as "arrival at this process". The acknowledgment is sent before <see cref="Received"/> is handled, so if the app exits during handling, that message is not delivered again.
/// Keep handlers short. The queue only absorbs momentary bursts; when it is full, messages are discarded according to <see cref="OverflowPolicy"/> and <see cref="DroppedCount"/> is incremented.
/// When <see cref="Stop"/> is called, messages that have not been raised yet are discarded.</para>
/// <para><b>Disconnection detection (alive)</b>: While connected, a 1-byte (0) message is published to <see cref="AliveTopic"/> (default "/test-alive") with QoS 1 every <see cref="AliveInterval"/>,
/// and if the broker's PUBACK does not arrive within <see cref="AliveTimeout"/>, the connection is considered dead, is closed, and is reconnected. A connection whose TCP is alive but that gets no MQTT response (e.g., a failure of an intermediate device) is also noticed within about AliveInterval + AliveTimeout.
/// If AliveTopic is null or an empty string, no alive message is sent, and the client then relies only on the MQTTnet Keep Alive (a PINGREQ if nothing has been sent for 15 seconds, and a disconnect if no PINGRESP arrives within 7.5 seconds).
/// If the broker closes the connection or a socket error occurs, it is detected immediately.</para>
/// <para><see cref="Connected"/> and <see cref="Disconnected"/> are raised as a pair, once per connection, on the dedicated MQClient thread ("MQClient") (also when disconnecting through <see cref="Stop"/>).
/// Exceptions thrown in any event handler are ignored (the loop and the dispatcher keep running). To update the UI, marshal asynchronously, as with BeginInvoke (WinForms) or Dispatcher.BeginInvoke (WPF).</para>
/// <para><see cref="Publish(string, byte[], MQQos, bool)"/>, <see cref="Subscribe(MQSubscribe)"/>, and <see cref="Unsubscribe"/> return immediately without waiting for a response (they give up after at most 10 seconds, and failures are ignored).
/// A Publish while not connected is discarded without being sent. The topic and QoS are validated at call time and throw exceptions.</para>
/// <para>If two clients connect with the same ClientID, the broker disconnects the one that connected first, so they keep disconnecting each other and reconnecting. Give each client a different ClientID.</para>
/// <para>The password is received only through <see cref="Start(string, string, string)"/> and is kept internally only for use in reconnection. It cannot be read as a property and is not saved to XML.</para>
/// </remarks>
/// <example>
/// <code>
/// using Devinno.Communication.Mqtt;
///
/// var mq = new MQClient { BrokerHostName = "192.168.0.10", BrokerPort = 1883 };
/// mq.Subscribe("sensor/#", MQQos.LeastOnce);
/// mq.Connected += (s, e) =&gt; Console.WriteLine("Connected");
/// mq.Disconnected += (s, e) =&gt; Console.WriteLine("Disconnected");
/// mq.Received += (s, e) =&gt; Console.WriteLine($"{e.Topic}: {System.Text.Encoding.UTF8.GetString(e.Datas)}");
/// mq.Start("dev01", "user", "password");
///
/// mq.Publish("cmd/run", "1", MQQos.LeastOnce);
/// Console.WriteLine($"Pending {mq.PendingCount}, dropped {mq.DroppedCount}");
///
/// mq.Dispose();
/// </code>
/// </example>
public class MQClient : IDisposable
{
    #region Properties
    /// <summary>Host name or IP of the MQTT broker (default: "127.0.0.1"). It is read on every connection attempt.</summary>
    public string BrokerHostName { get; set; } = "127.0.0.1";
    /// <summary>Port of the MQTT broker (default: 1883). It is read on every connection attempt.</summary>
    public int BrokerPort { get; set; } = 1883;

    /// <summary>
    /// While connected, the topic to which a 1-byte (0) message is published with QoS 1 every <see cref="AliveInterval"/> to check that the connection is alive (default: "/test-alive"). If null or an empty string, no check is made.
    /// </summary>
    /// <remarks>A change made while running takes effect from the next check. See the class description for what is detected.</remarks>
    /// <exception cref="ArgumentException">Contains a wildcard (#, +) or a NUL character, or exceeds 65535 bytes in UTF-8.</exception>
    public string? AliveTopic
    {
        get => Volatile.Read(ref aliveTopic);
        set
        {
            if (!string.IsNullOrEmpty(value)) MQTopic.CheckName(value, nameof(AliveTopic));
            Volatile.Write(ref aliveTopic, value);
        }
    }

    /// <summary>Alive check interval in ms, 100 to 60000 (default: 1000). After the previous check finishes, it rests this long before the next check. May be changed while running.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not within 100 to 60000.</exception>
    public int AliveInterval
    {
        get => Volatile.Read(ref aliveInterval);
        set => Volatile.Write(ref aliveInterval, CheckMs(value, nameof(AliveInterval)));
    }

    /// <summary>Time to wait for the PUBACK of the alive message in ms, 100 to 60000 (default: 3000). If exceeded, the connection is considered dead and is reconnected. May be changed while running.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not within 100 to 60000.</exception>
    public int AliveTimeout
    {
        get => Volatile.Read(ref aliveTimeout);
        set => Volatile.Write(ref aliveTimeout, CheckMs(value, nameof(AliveTimeout)));
    }

    /// <summary>Number of messages that the receive queue can hold, 1 to 10000000 (default: 100000). Applied on <see cref="Start()"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not within 1 to 10000000.</exception>
    public int MaxPendingMessages
    {
        get => maxPendingMessages;
        set
        {
            if (value is < 1 or > 10_000_000) throw new ArgumentOutOfRangeException(nameof(MaxPendingMessages), value, "1~10000000이어야 합니다.");
            maxPendingMessages = value;
        }
    }

    /// <summary>Which message to discard when the receive queue is full (default: <see cref="MQOverflowPolicy.DropOldest"/>). Applied on <see cref="Start()"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">An undefined value.</exception>
    public MQOverflowPolicy OverflowPolicy
    {
        get => overflowPolicy;
        set
        {
            if (value is not (MQOverflowPolicy.DropOldest or MQOverflowPolicy.DropNewest)) throw new ArgumentOutOfRangeException(nameof(OverflowPolicy), value, "DropOldest 또는 DropNewest여야 합니다.");
            overflowPolicy = value;
        }
    }

    /// <summary>Gets the number of messages waiting for <see cref="Received"/> (the receive queue length). 0 while stopped.</summary>
    public int PendingCount => Volatile.Read(ref queue)?.Reader.Count ?? 0;
    /// <summary>Gets the number of messages discarded because the receive queue was full. It is kept across reconnections and reset to 0 only on <see cref="Start()"/>.</summary>
    public long DroppedCount => Interlocked.Read(ref dropped);

    /// <summary>Gets whether the client has been started. It is true as soon as <see cref="Start()"/> returns.</summary>
    public bool IsStart { get; private set; }
    /// <summary>Gets whether the client is connected to the MQTT broker.</summary>
    public bool IsConnected => Volatile.Read(ref current)?.IsConnected ?? false;
    /// <summary>Gets whether the object has been disposed.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>MQTT client identifier. It is set by Start or loaded from the configuration (XML); if neither is given, a GUID is generated on Start (the generated GUID is not saved to the configuration).</summary>
    public string? ClientID => string.IsNullOrEmpty(ConfiguredClientID) ? generatedClientID : ConfiguredClientID;
    // 사용자가 Start나 설정(XML)으로 정한 ClientID. 자동으로 만든 GUID는 들어가지 않는다(설정 저장 대상).
    internal string? ConfiguredClientID { get; set; }
    /// <summary>Authentication user name. It is set by Start or loaded from the configuration (XML). Credentials are sent only if a password is also present.</summary>
    public string? UserName { get; private set; }
    /// <summary>Gets a copy of the current subscription list. There is only one entry per topic; subscribing again changes its QoS.</summary>
    public IReadOnlyList<MQSubscribe> Subscribes { get { lock (subLock) return [.. subs]; } }

    /// <summary>Gets or sets a user-defined tag.</summary>
    public object? Tag { get; set; }
    #endregion

    #region Member Variable
    const int ReconnectDelay = 1000;
    const int StopWait = 3000;
    static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);
    // CONNACK·SUBACK 대기와, 기다리지 않고 보낸 Publish/Subscribe/Unsubscribe를 포기하는 시간
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    static readonly MqttClientFactory Factory = new();
    static readonly byte[] AlivePayload = [0];

    readonly object lifeLock = new();
    readonly object subLock = new();
    readonly List<MQSubscribe> subs = [];
    // 연결이 끊겼을 때 루프를 바로 깨운다. MQTTnet 이벤트가 늦게 Set해도 예외가 나지 않도록 Dispose하지 않는다.
    readonly AutoResetEvent wake = new(false);
    IMqttClient? current;
    Channel<MQReceiveArgs>? queue;
    long dropped;
    string? aliveTopic = "/test-alive";
    int aliveInterval = 1000;
    int aliveTimeout = 3000;
    int maxPendingMessages = 100_000;
    MQOverflowPolicy overflowPolicy = MQOverflowPolicy.DropOldest;
    string? password;
    // ConfiguredClientID 없이 Start했을 때 만든 GUID. 다시 Start해도 같은 값을 쓴다.
    string? generatedClientID;
    Thread? thread;
    Thread? dispatcher;
    CancellationTokenSource? cts;
    int loopThreadCount;
    int dispatcherThreadCount;
    #endregion

    #region Test Hooks
    // 테스트 전용: 실행 중인 루프 스레드("MQClient") 수
    internal int LoopThreadCount => Volatile.Read(ref loopThreadCount);
    // 테스트 전용: 실행 중인 디스패처 스레드("MQClient.Received") 수
    internal int DispatcherThreadCount => Volatile.Read(ref dispatcherThreadCount);
    // 테스트 전용: 루프가 접속을 시도하기 전마다 부른다(예외를 던져 루프가 예상하지 못하게 끝나는 경우를 흉내 낸다)
    internal Action? LoopIterationHook { get; set; }
    #endregion

    #region Event
    /// <summary>Raised after connecting to the MQTT broker and re-registering the subscription list. Raised on the dedicated MQClient thread.</summary>
    public event EventHandler? Connected;
    /// <summary>Raised once per connection when the connection is lost (including Stop and no alive response). Raised on the dedicated MQClient thread.</summary>
    public event EventHandler? Disconnected;
    /// <summary>Raised in the order received when a message of a subscribed topic is received. Raised on the dispatcher thread ("MQClient.Received").</summary>
    public event EventHandler<MQReceiveArgs>? Received;
    #endregion

    #region Start / Stop
    /// <summary>Starts connecting to the broker with the configured ClientID (a GUID if none). Does nothing if it is already started.</summary>
    /// <exception cref="ObjectDisposedException">The object has been disposed.</exception>
    public void Start() => Start(null, null, null);

    /// <summary>Starts connecting to the broker with the specified client ID. Does nothing if it is already started.</summary>
    /// <param name="clientID">MQTT client identifier. If null or an empty string, the configured <see cref="ClientID"/> is used, and if there is none, a GUID is generated.</param>
    /// <exception cref="ObjectDisposedException">The object has been disposed.</exception>
    public void Start(string? clientID) => Start(clientID, null, null);

    /// <summary>Starts connecting to the broker with the specified client ID and credentials. If the connection is lost, it reconnects at 1-second intervals. Does nothing if it is already started.</summary>
    /// <remarks>Creates a new receive queue and resets <see cref="DroppedCount"/> to 0. <see cref="MaxPendingMessages"/> and <see cref="OverflowPolicy"/> are applied at this time.</remarks>
    /// <param name="clientID">MQTT client identifier. If null or an empty string, the configured <see cref="ClientID"/> is used, and if there is none, a GUID is generated.</param>
    /// <param name="userName">Authentication user name. If null, the configured <see cref="UserName"/> is used.</param>
    /// <param name="password">Authentication password. If null, the value received in the previous Start is used. It is kept only internally and is not saved to XML.</param>
    /// <exception cref="ObjectDisposedException">The object has been disposed.</exception>
    public void Start(string? clientID, string? userName, string? password)
    {
        lock (lifeLock)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (IsStart) return;
            if (!string.IsNullOrEmpty(clientID)) ConfiguredClientID = clientID;
            if (string.IsNullOrEmpty(ConfiguredClientID)) generatedClientID ??= Guid.NewGuid().ToString();
            if (userName != null) UserName = userName;
            if (password != null) this.password = password;

            Interlocked.Exchange(ref dropped, 0);
            var options = new BoundedChannelOptions(MaxPendingMessages)
            {
                FullMode = OverflowPolicy == MQOverflowPolicy.DropOldest ? BoundedChannelFullMode.DropOldest : BoundedChannelFullMode.DropWrite,
                SingleReader = true,
            };
            var q = Channel.CreateBounded<MQReceiveArgs>(options, _ => Interlocked.Increment(ref dropped));
            Volatile.Write(ref queue, q);

            // 루프가 스스로 끝난 뒤(예상하지 못한 예외)에는 이전 cts가 남아 있다
            cts?.Cancel();
            cts?.Dispose();
            cts = new CancellationTokenSource();
            var token = cts.Token;
            IsStart = true;
            dispatcher = new Thread(() => Dispatch(q.Reader, token)) { IsBackground = true, Name = nameof(MQClient) + "." + nameof(Received) };
            thread = new Thread(() => Run(token)) { IsBackground = true, Name = nameof(MQClient) };
            dispatcher.Start();
            thread.Start();
        }
    }

    /// <summary>Disconnects and stops the client. Waits up to 3 seconds for the dedicated threads to end. May be called multiple times.</summary>
    /// <remarks>
    /// <para>Messages that have not yet been passed to <see cref="Received"/> are discarded. Even if the threads do not end within 3 seconds (for example, when a handler takes a long time), the connection is closed and the call returns.</para>
    /// <para>May be called from inside an event handler. The thread that is running that handler is not waited for.</para>
    /// <para>If <c>Start</c> is called again while a <see cref="Received"/> handler has been running for more than 3 seconds, the old and new dispatchers may briefly raise <see cref="Received"/> at the same time until the old handler finishes.</para>
    /// </remarks>
    public void Stop()
    {
        Thread? t, d;
        CancellationTokenSource? c;
        IMqttClient? old;
        lock (lifeLock)
        {
            c = cts; cts = null;
            t = thread; thread = null;
            d = dispatcher; dispatcher = null;
            IsStart = false;
            Interlocked.Exchange(ref queue, null)?.Writer.TryComplete();
            c?.Cancel();
            // 이 시점의 접속만 끊는다. 대기 중 다른 스레드가 Start해 만든 새 접속은 건드리지 않는다(lifeLock → subLock 순서)
            lock (subLock) old = Volatile.Read(ref current);
        }
        var until = Environment.TickCount64 + StopWait;
        if (t != null && t != Thread.CurrentThread) t.Join(StopWait);
        if (old != null) Detach(old);   // 루프가 시간 안에 끝나지 않았어도 접속은 끊는다
        if (d != null && d != Thread.CurrentThread) d.Join((int)Math.Max(0, until - Environment.TickCount64));
        if (t is not { IsAlive: true } && d is not { IsAlive: true }) c?.Dispose();
    }

    /// <summary>Stops the client and releases its resources. May be called multiple times.</summary>
    public void Dispose()
    {
        // Start와 같은 lock에서 표시해, Dispose가 시작된 뒤의 Start는 ObjectDisposedException을 던진다
        lock (lifeLock)
        {
            if (IsDisposed) return;
            IsDisposed = true;
        }
        Stop();
        GC.SuppressFinalize(this);
    }
    #endregion

    #region Publish
    /// <summary>Publishes a byte array message to the specified topic. If not connected, it returns without sending.</summary>
    /// <remarks>The data is copied and sent, and the call does not wait for the send or for the QoS 1/2 acknowledgment (messages go out in the order called from the same thread). Failures, or no confirmation within 10 seconds, are not reported.</remarks>
    /// <param name="Topic">Topic to publish to (wildcards not allowed).</param>
    /// <param name="Data">Message byte array.</param>
    /// <param name="qos">QoS (default: MostOnce).</param>
    /// <param name="retain">Whether the broker retains it as the last message.</param>
    /// <exception cref="ArgumentNullException">Topic or Data is null.</exception>
    /// <exception cref="ArgumentException">The topic is empty, contains a wildcard (#, +) or a NUL character, or exceeds 65535 bytes in UTF-8.</exception>
    /// <exception cref="ArgumentOutOfRangeException">qos is not MostOnce, LeastOnce, or ExactlyOnce.</exception>
    public void Publish(string Topic, byte[] Data, MQQos qos = MQQos.MostOnce, bool retain = false)
    {
        MQTopic.CheckName(Topic, nameof(Topic));
        ArgumentNullException.ThrowIfNull(Data);
        MQTopic.CheckQos(qos, nameof(qos));
        var c = Volatile.Read(ref current);
        if (c == null || !c.IsConnected) return;
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(Topic)
            .WithPayload((byte[])Data.Clone())
            .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)qos)
            .WithRetainFlag(retain)
            .Build();
        Forget(token => c.PublishAsync(message, token));
    }

    /// <summary>Publishes a string message, converted to UTF-8, to the specified topic. If not connected, it returns without sending.</summary>
    /// <param name="Topic">Topic to publish to (wildcards not allowed).</param>
    /// <param name="Data">Message string.</param>
    /// <param name="qos">QoS (default: MostOnce).</param>
    /// <param name="retain">Whether the broker retains it as the last message.</param>
    /// <exception cref="ArgumentNullException">Topic or Data is null.</exception>
    /// <exception cref="ArgumentException">The topic is empty, contains a wildcard (#, +) or a NUL character, or exceeds 65535 bytes in UTF-8.</exception>
    /// <exception cref="ArgumentOutOfRangeException">qos is not MostOnce, LeastOnce, or ExactlyOnce.</exception>
    public void Publish(string Topic, string Data, MQQos qos = MQQos.MostOnce, bool retain = false)
    {
        ArgumentNullException.ThrowIfNull(Data);
        Publish(Topic, Encoding.UTF8.GetBytes(Data), qos, retain);
    }
    #endregion

    #region Subscribe
    /// <summary>Adds a subscription to the list and, if connected, requests it immediately (without waiting for the response). If the topic already exists, its QoS is changed.</summary>
    /// <remarks>The whole list is requested again on every reconnection.</remarks>
    /// <param name="sub">Subscription information.</param>
    /// <exception cref="ArgumentNullException">sub is null.</exception>
    public void Subscribe(MQSubscribe sub)
    {
        ArgumentNullException.ThrowIfNull(sub);
        lock (subLock)
        {
            subs.RemoveAll(x => x.Topic == sub.Topic);
            subs.Add(sub);
            var c = Volatile.Read(ref current);
            if (c != null && c.IsConnected)
            {
                var options = new MqttClientSubscribeOptionsBuilder().WithTopicFilter(sub.Topic, (MqttQualityOfServiceLevel)sub.Qos).Build();
                Forget(token => c.SubscribeAsync(options, token));
            }
        }
    }

    /// <summary>Subscribes to the topic with QoS 0 (MostOnce).</summary>
    /// <param name="Topic">Topic filter to subscribe to.</param>
    /// <exception cref="ArgumentNullException">Topic is null.</exception>
    /// <exception cref="ArgumentException">The topic filter format is invalid.</exception>
    public void Subscribe(string Topic) => Subscribe(new MQSubscribe(Topic));

    /// <summary>Subscribes to the topic with the specified QoS.</summary>
    /// <param name="Topic">Topic filter to subscribe to.</param>
    /// <param name="Qos">QoS.</param>
    /// <exception cref="ArgumentNullException">Topic is null.</exception>
    /// <exception cref="ArgumentException">The topic filter format is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Qos is not MostOnce, LeastOnce, or ExactlyOnce.</exception>
    public void Subscribe(string Topic, MQQos Qos) => Subscribe(new MQSubscribe(Topic, Qos));
    #endregion

    #region Unsubscribe
    /// <summary>Removes the topic subscription from the list and, if connected, requests the unsubscription (without waiting for the response). Does nothing if it is not in the list.</summary>
    /// <param name="Topic">Topic filter to unsubscribe from (the same string used to subscribe).</param>
    /// <exception cref="ArgumentNullException">Topic is null.</exception>
    public void Unsubscribe(string Topic)
    {
        ArgumentNullException.ThrowIfNull(Topic);
        lock (subLock)
        {
            if (subs.RemoveAll(x => x.Topic == Topic) == 0) return;
            SendUnsubscribe([Topic]);
        }
    }

    /// <summary>Removes all subscriptions from the list and, if connected, requests the unsubscription (without waiting for the response).</summary>
    public void UnsubscribeClear()
    {
        lock (subLock)
        {
            if (subs.Count == 0) return;
            var topics = subs.Select(x => x.Topic).ToList();
            subs.Clear();
            SendUnsubscribe(topics);
        }
    }

    void SendUnsubscribe(List<string> topics)
    {
        var c = Volatile.Read(ref current);
        if (c == null || !c.IsConnected) return;
        var b = new MqttClientUnsubscribeOptionsBuilder();
        foreach (var t in topics) b.WithTopicFilter(t);
        var options = b.Build();
        Forget(token => c.UnsubscribeAsync(options, token));
    }
    #endregion

    #region Config / Xml
    /// <summary>Creates a configuration object from the current connection settings and subscription list. The password is not included.</summary>
    /// <remarks>If <see cref="AliveTopic"/> is null, it is saved as an empty string ("") (alive check is off when loaded). A generated GUID is not saved as the ClientID.</remarks>
    /// <returns>The configuration object.</returns>
    public MQClientConfig ToConfig() => new()
    {
        BrokerHostName = BrokerHostName,
        BrokerPort = BrokerPort,
        ClientID = ConfiguredClientID,
        UserName = UserName,
        AliveTopic = AliveTopic ?? "",
        AliveInterval = AliveInterval,
        AliveTimeout = AliveTimeout,
        MaxPendingMessages = MaxPendingMessages,
        OverflowPolicy = OverflowPolicy,
        Subscribes = [.. Subscribes.Select(s => new MQSubscribeConfig { Topic = s.Topic, Qos = s.Qos })],
    };

    /// <summary>Applies the connection settings and subscription list of a configuration object. The existing subscription list is replaced by that of the configuration.</summary>
    /// <remarks>
    /// <para>Everything is validated first and then applied, so if any value is invalid, nothing is changed.</para>
    /// <para>If ClientID or UserName is missing or an empty string, it becomes null. The password received through Start is not changed.</para>
    /// </remarks>
    /// <param name="config">The configuration to apply.</param>
    /// <exception cref="InvalidOperationException">Called while running (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">A value is invalid (the message includes the element and attribute names).</exception>
    public void ApplyConfig(MQClientConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (IsStart) throw new InvalidOperationException("실행 중에는 설정을 바꿀 수 없습니다. Stop() 후 호출하세요.");
        config.Validate();
        var list = new List<MQSubscribe>();
        foreach (var s in config.Subscribes ?? [])
        {
            list.RemoveAll(x => x.Topic == s.Topic);
            list.Add(new MQSubscribe(s.Topic, s.Qos));
        }

        BrokerHostName = config.BrokerHostName;
        BrokerPort = config.BrokerPort;
        ConfiguredClientID = string.IsNullOrEmpty(config.ClientID) ? null : config.ClientID;
        UserName = string.IsNullOrEmpty(config.UserName) ? null : config.UserName;
        AliveTopic = config.AliveTopic;
        AliveInterval = config.AliveInterval;
        AliveTimeout = config.AliveTimeout;
        MaxPendingMessages = config.MaxPendingMessages;
        OverflowPolicy = config.OverflowPolicy;
        lock (subLock)
        {
            subs.Clear();
            subs.AddRange(list);
        }
    }

    /// <summary>Creates an XML element from the current connection settings and subscription list. The password is not included.</summary>
    /// <returns>The &lt;MQClient&gt; element.</returns>
    public XElement ToXml() => XmlConfig.Serialize(ToConfig());

    /// <summary>Loads the connection settings and subscription list from an XML element. The existing subscription list is replaced by the XML content.</summary>
    /// <remarks>
    /// <para>Omitted attributes use their default values. An unknown attribute or element is an error (to catch typos). The Password attribute is also an unknown attribute.</para>
    /// <para>Everything is validated first and then applied, so if any value is invalid, nothing is changed.</para>
    /// <para>You can check the format by generating the XSD with <see cref="ConfigSchema.Get{T}"/> for <see cref="MQClientConfig"/>.</para>
    /// </remarks>
    /// <param name="element">The &lt;MQClient&gt; element.</param>
    /// <exception cref="InvalidOperationException">Called while running (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">The root name differs, an unknown attribute or element is present, or a value is invalid.</exception>
    public void FromXml(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (IsStart) throw new InvalidOperationException("실행 중에는 설정을 불러올 수 없습니다. Stop() 후 호출하세요.");
        ApplyConfig(XmlConfig.Deserialize<MQClientConfig>(element));
    }

    /// <summary>Saves the connection settings and subscription list to an XML file. The password is not saved.</summary>
    /// <param name="path">File path.</param>
    public void SaveXml(string path) => ToXml().Save(path);

    /// <summary>Loads the connection settings and subscription list from an XML file. The rules are the same as <see cref="FromXml"/>.</summary>
    /// <param name="path">File path.</param>
    public void LoadXml(string path) => FromXml(XElement.Load(path));
    #endregion

    #region Loop
    void Run(CancellationToken token)
    {
        Interlocked.Increment(ref loopThreadCount);
        try
        {
            while (!token.IsCancellationRequested)
            {
                LoopIterationHook?.Invoke();
                var c = Connect(token);
                if (c != null)
                {
                    try
                    {
                        Raise(Connected);
                        Watch(c, token);
                    }
                    finally
                    {
                        Detach(c);
                        Raise(Disconnected);
                    }
                }
                if (token.IsCancellationRequested) break;
                token.WaitHandle.WaitOne(ReconnectDelay);
            }
        }
        catch (Exception) { }
        finally
        {
            lock (lifeLock)
            {
                // Stop을 거치지 않고 끝났으면(예상하지 못한 예외) 디스패처도 끝낸다. Stop이 이미 정리했으면 thread가 다르다.
                if (thread == Thread.CurrentThread)
                {
                    IsStart = false;
                    thread = null;
                    Interlocked.Exchange(ref queue, null)?.Writer.TryComplete();
                    cts?.Cancel();
                }
            }
            Interlocked.Decrement(ref loopThreadCount);
        }
    }

    // 새 MQTTnet 클라이언트로 접속한다. 성공하면 current로 등록하고 구독 목록을 다시 등록(SUBACK까지 대기)한다. 실패하거나 그 사이 Stop되었으면 null.
    IMqttClient? Connect(CancellationToken token)
    {
        IMqttClient? c = null;
        try
        {
            // 접속 시도마다 새 클라이언트를 만든다(이전 연결의 상태·핸들러가 섞이지 않게)
            c = Factory.CreateMqttClient();
            c.ApplicationMessageReceivedAsync += OnMessage;
            c.DisconnectedAsync += OnDisconnected;

            var b = new MqttClientOptionsBuilder()
                .WithTcpServer(BrokerHostName, BrokerPort)
                .WithClientId(ClientID)
                .WithCleanSession(true)
                .WithProtocolVersion(MqttProtocolVersion.V311)
                .WithKeepAlivePeriod(KeepAlive)
                .WithTimeout(RequestTimeout);
            var user = UserName;
            var pass = password;
            if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(pass)) b = b.WithCredentials(user, pass);

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(RequestTimeout);
                var result = c.ConnectAsync(b.Build(), timeout.Token).GetAwaiter().GetResult();
                if (result.ResultCode != MqttClientConnectResultCode.Success) { Close(c); return null; }
            }

            // current 등록과 재구독 요청 송신을 같은 lock 안에서 시작해, 다른 스레드의 Subscribe/Unsubscribe 요청과 순서가 뒤바뀌지 않게 한다
            using var subTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            subTimeout.CancelAfter(RequestTimeout);
            Task? resubscribe = null;
            lock (subLock)
            {
                if (token.IsCancellationRequested) { Close(c); return null; }
                Volatile.Write(ref current, c);
                if (subs.Count > 0)
                {
                    var sb = new MqttClientSubscribeOptionsBuilder();
                    foreach (var s in subs) sb.WithTopicFilter(s.Topic, (MqttQualityOfServiceLevel)s.Qos);
                    resubscribe = c.SubscribeAsync(sb.Build(), subTimeout.Token);
                }
            }
            resubscribe?.GetAwaiter().GetResult();
            return c;
        }
        catch (Exception)
        {
            if (c != null)
            {
                Interlocked.CompareExchange(ref current, null, c);
                Close(c);
            }
            return null;
        }
    }

    // 접속해 있는 동안 alive를 확인한다. 끊김을 알아채거나 Stop되면 돌아온다.
    void Watch(IMqttClient c, CancellationToken token)
    {
        while (!token.IsCancellationRequested && c.IsConnected)
        {
            var topic = AliveTopic;
            if (!string.IsNullOrEmpty(topic) && !Alive(c, topic, token)) return;
            WaitHandle.WaitAny([token.WaitHandle, wake], AliveInterval);
        }
    }

    // alive 메시지를 QoS 1로 보내고 PUBACK을 AliveTimeout까지 기다린다. 받지 못하면 false(죽은 연결).
    bool Alive(IMqttClient c, string topic, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(AliveTimeout);
        try
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(AlivePayload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            c.PublishAsync(message, timeout.Token).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception)
        {
            return token.IsCancellationRequested;
        }
    }

    // current에서 떼어 내고 접속을 끊는다. 루프와 Stop이 같이 불러도 한 번만 끊는다.
    void Detach(IMqttClient c)
    {
        if (Interlocked.CompareExchange(ref current, null, c) != c) return;
        Close(c);
        try { wake.Set(); } catch { }
    }

    // DISCONNECT를 보내 보고(최대 0.5초) MQTTnet 클라이언트를 해제한다.
    static void Close(IMqttClient c)
    {
        try
        {
            if (c.IsConnected)
            {
                using var timeout = new CancellationTokenSource(500);
                c.DisconnectAsync(new MqttClientDisconnectOptions(), timeout.Token).GetAwaiter().GetResult();
            }
        }
        catch (Exception) { }
        try { c.Dispose(); } catch (Exception) { }
    }

    // MQTTnet 수신 처리. MQTTnet은 이 핸들러가 끝난 뒤에 PUBACK을 보내므로, 사용자 처리를 여기서 기다리면
    // 느린 핸들러가 확인 응답을 늦춰 브로커가 메시지를 버린다. 복사해서 대기열에 넣고 바로 돌아간다.
    Task OnMessage(MqttApplicationMessageReceivedEventArgs e)
    {
        var q = Volatile.Read(ref queue);
        q?.Writer.TryWrite(new MQReceiveArgs(e.ApplicationMessage.Topic, e.ApplicationMessage.Payload.ToArray()));
        return Task.CompletedTask;
    }

    Task OnDisconnected(MqttClientDisconnectedEventArgs e)
    {
        try { wake.Set(); } catch { }
        return Task.CompletedTask;
    }

    // 디스패처 스레드: 대기열의 메시지를 순서대로 Received로 넘긴다. Stop(취소)되면 남은 메시지는 버리고 끝난다.
    void Dispatch(ChannelReader<MQReceiveArgs> reader, CancellationToken token)
    {
        Interlocked.Increment(ref dispatcherThreadCount);
        try
        {
            while (reader.WaitToReadAsync(token).AsTask().GetAwaiter().GetResult())
            {
                while (!token.IsCancellationRequested && reader.TryRead(out var m)) Raise(Received, m);
            }
        }
        catch (Exception) { }
        finally { Interlocked.Decrement(ref dispatcherThreadCount); }
    }

    // MQTTnet 비동기 요청을 기다리지 않고 보낸다. RequestTimeout이 지나면 취소하고, 결과와 예외는 관찰만 하고 버린다.
    static void Forget(Func<CancellationToken, Task> request)
    {
        var timeout = new CancellationTokenSource(RequestTimeout);
        Task task;
        try { task = request(timeout.Token); }
        catch (Exception) { timeout.Dispose(); return; }
        task.ContinueWith(static (t, s) => { _ = t.Exception; ((IDisposable)s!).Dispose(); }, timeout,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    static int CheckMs(int value, string name)
    {
        if (value is < 100 or > 60000) throw new ArgumentOutOfRangeException(name, value, "100~60000ms여야 합니다.");
        return value;
    }

    void Raise(EventHandler? handler)
    {
        if (handler == null) return;
        try { handler(this, EventArgs.Empty); } catch { }
    }

    void Raise<T>(EventHandler<T>? handler, T args)
    {
        if (handler == null) return;
        try { handler(this, args); } catch { }
    }
    #endregion

    #region Static Method
    /// <summary>Test-connects to the broker (up to 10 seconds). If the connection succeeds, it disconnects after 0.5 seconds and returns true.</summary>
    /// <param name="BrokerIP">Broker IP or host name.</param>
    /// <param name="ClientID">Client identifier to use for the test.</param>
    /// <param name="BrokerPort">Broker port (default: 1883).</param>
    /// <returns>true if the connection succeeds, false if it fails or is rejected.</returns>
    public static bool Test(string BrokerIP, string ClientID, int BrokerPort = 1883)
    {
        var c = Factory.CreateMqttClient();
        try
        {
            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(BrokerIP, BrokerPort)
                .WithClientId(ClientID)
                .WithCleanSession(true)
                .WithProtocolVersion(MqttProtocolVersion.V311)
                .WithTimeout(RequestTimeout)
                .Build();
            using var timeout = new CancellationTokenSource(RequestTimeout);
            if (c.ConnectAsync(options, timeout.Token).GetAwaiter().GetResult().ResultCode != MqttClientConnectResultCode.Success) return false;
            Thread.Sleep(500);
            return true;
        }
        catch (Exception) { return false; }
        finally { Close(c); }
    }
    #endregion
}
