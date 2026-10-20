using System.Diagnostics;
using Devinno.Communication.Mqtt;

namespace Devinno.Communication.Tests.Mqtt;

/// 시간과 브로커 부하에 민감한 테스트는 다른 테스트와 겹치지 않게 따로 돈다(병렬 실행 단계가 끝난 뒤 실행됨).
[CollectionDefinition(nameof(MqttSerialCollection), DisableParallelization = true)]
public class MqttSerialCollection { }

/// 수신 대기열(느린 핸들러, 대기열 상한), alive 발행 주기와 끊김 감지. 127.0.0.1:1883의 mosquitto가 있어야 실행된다.
[Collection(nameof(MqttSerialCollection))]
public class MQClientReliabilityTests
{
    static async Task<MQClient> Started(int port = Mosquitto.Port)
    {
        var mq = new MQClient { BrokerPort = port, AliveTopic = null };
        mq.Start();
        await TestUtil.Until(() => mq.IsConnected, 5000);
        return mq;
    }

    /// 지정한 시간만큼 CPU를 써서 기다린다(Thread.Sleep은 Windows에서 약 15ms 단위라 쓰지 않음)
    static void Busy(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) Thread.SpinWait(100);
    }

    [MosquittoFact]
    public async Task SlowHandler_QoS1Burst_NoLoss_QueueAbsorbsAndDrains()
    {
        const int Count = 500;   // mosquitto 기본값(확인 대기 20건 + 큐 100건)을 크게 넘는 양
        var topic = Mosquitto.NewTopic();
        var probe = new Probe();
        var got = new List<int>();
        int maxPending = 0;
        using var sub = new MQClient { AliveTopic = null };
        sub.Received += (s, e) =>
        {
            if (probe.Handle(e)) return;
            maxPending = Math.Max(maxPending, sub.PendingCount);
            Busy(5);   // 느린 사용자 처리
            lock (got) got.Add(BitConverter.ToInt32(e.Datas));
        };
        sub.Subscribe(topic, MQQos.LeastOnce);
        sub.Start();
        await TestUtil.Until(() => sub.IsConnected, 5000);
        using var pub = await Started();
        await probe.UntilDelivered(pub, topic);

        // 약 600건/초로 보낸다. 핸들러(약 200건/초)보다 빠르고, 브로커 쪽 순간 한도(120건)는 넘지 않는 속도
        // (한꺼번에 500건을 쏟으면 PUBACK이 즉시 나가도 왕복 시간 동안 mosquitto 큐가 넘쳐 일부를 버린다)
        // 주의: "사용자 핸들러를 기다린 뒤 PUBACK" 회귀를 이 테스트가 잡아내는 것은 mosquitto 기본 한도
        // (max_inflight_messages 20, max_queued_messages 100)에 기대고 있다. 한도를 크게 올린 브로커에서는
        // 메시지가 버려지지 않아 그 회귀가 있어도 통과할 수 있다.
        for (int i = 0; i < Count; i++)
        {
            pub.Publish(topic, BitConverter.GetBytes(i), MQQos.LeastOnce);
            if (i % 10 == 9) await Task.Delay(10);
        }

        // 핸들러가 PUBACK을 막으면 브로커가 큐를 넘는 메시지를 버려 끝내 500건이 오지 않는다
        await TestUtil.Until(() => { lock (got) return got.Count >= Count; }, 20000);
        await Task.Delay(300);
        lock (got) Assert.Equal(Enumerable.Range(0, Count), got);   // 빠짐·중복 없이 보낸 순서대로
        Assert.True(maxPending >= 100, $"대기열 최대 {maxPending}건 (몰림을 대기열이 받아야 함)");
        Assert.Equal(0, sub.PendingCount);
        Assert.Equal(0, sub.DroppedCount);
        Assert.True(sub.IsConnected);
    }

    [MosquittoFact]
    public Task QueueFull_DropOldest_KeepsNewest_CountsDropped() => QueueFull(MQOverflowPolicy.DropOldest, [0, .. Enumerable.Range(41, 10)]);

    [MosquittoFact]
    public Task QueueFull_DropNewest_KeepsOldest_CountsDropped() => QueueFull(MQOverflowPolicy.DropNewest, [0, .. Enumerable.Range(1, 10)]);

    /// 첫 메시지(0)의 핸들러를 막아 둔 채 1~50을 보내면, 상한 10인 대기열에서 40건이 버려진다.
    static async Task QueueFull(MQOverflowPolicy policy, int[] expected)
    {
        var topic = Mosquitto.NewTopic();
        var probe = new Probe();
        var got = new List<int>();
        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        using var sub = new MQClient { AliveTopic = null, MaxPendingMessages = 10, OverflowPolicy = policy };
        sub.Received += (s, e) =>
        {
            if (probe.Handle(e)) return;
            int v = BitConverter.ToInt32(e.Datas);
            if (v == 0) { entered.Set(); gate.Wait(10000); }
            lock (got) got.Add(v);
        };
        sub.Subscribe(topic, MQQos.LeastOnce);
        sub.Start();
        await TestUtil.Until(() => sub.IsConnected, 5000);
        using var pub = await Started();
        await probe.UntilDelivered(pub, topic);

        pub.Publish(topic, BitConverter.GetBytes(0), MQQos.LeastOnce);
        await TestUtil.Until(() => entered.IsSet);
        for (int i = 1; i <= 50; i++) pub.Publish(topic, BitConverter.GetBytes(i), MQQos.LeastOnce);

        await TestUtil.Until(() => sub.DroppedCount == 40, 5000);
        Assert.Equal(10, sub.PendingCount);
        Assert.True(sub.IsConnected);   // 대기열이 가득 차도 연결은 유지된다

        gate.Set();
        await TestUtil.Until(() => { lock (got) return got.Count >= expected.Length; });
        await Task.Delay(300);
        lock (got) Assert.Equal(expected, got);
        Assert.Equal(40, sub.DroppedCount);

        sub.Stop();
        Assert.Equal((0, 40L), (sub.PendingCount, sub.DroppedCount));   // 버린 수는 Stop 뒤에도 남는다
        sub.Start();
        Assert.Equal(0, sub.DroppedCount);                              // Start할 때만 0
    }

    [MosquittoFact]
    public async Task AliveTopic_PublishesAboutEverySecond_NullStops()
    {
        var topic = Mosquitto.NewTopic();
        var probe = new Probe();
        int n = 0;
        byte[]? last = null;
        using var watcher = await Started();
        watcher.Received += (s, e) => { if (probe.Handle(e)) return; if (e.Topic == topic) { last = e.Datas; Interlocked.Increment(ref n); } };
        using var pub = await Started();
        watcher.Subscribe(topic);
        await probe.UntilDelivered(pub, topic);

        using var mq = new MQClient { AliveTopic = topic };
        mq.Start();
        await TestUtil.Until(() => n >= 1, 5000);
        int first = n;
        await Task.Delay(3200);
        Assert.InRange(n - first, 2, 5);   // 약 1초에 한 번
        Assert.Equal(new byte[] { 0 }, last);

        mq.AliveTopic = null;
        await Task.Delay(1500);
        int after = n;
        await Task.Delay(2500);
        Assert.Equal(after, n);
        Assert.True(mq.IsConnected);
    }

    [MosquittoFact]
    public async Task Blackhole_DetectedByAliveCheck_ThenReconnects()
    {
        using var proxy = new TcpForwardProxy(Mosquitto.Port);
        using var mq = new MQClient { BrokerPort = proxy.Port, AliveTopic = Mosquitto.NewTopic() };   // AliveInterval 1000, AliveTimeout 3000
        int connected = 0, disconnected = 0;
        mq.Connected += (s, e) => Interlocked.Increment(ref connected);
        mq.Disconnected += (s, e) => Interlocked.Increment(ref disconnected);
        mq.Start();
        await TestUtil.Until(() => connected == 1 && mq.IsConnected, 5000);
        await Task.Delay(1500);
        Assert.Equal(0, disconnected);

        var sw = Stopwatch.StartNew();
        proxy.Blackhole = true;   // TCP는 열려 있고 데이터만 사라진다 → 소켓 오류도, 연결 종료도 없다
        await TestUtil.Until(() => disconnected == 1, 8000);
        Assert.InRange(sw.ElapsedMilliseconds, 2500, 6500);   // alive 간격(최대 1초) + PUBACK 대기 3초
        Assert.False(mq.IsConnected);

        await Task.Delay(1500);
        Assert.Equal(1, connected);   // 막혀 있는 동안은 접속되지 않는다

        proxy.Blackhole = false;
        await TestUtil.Until(() => connected == 2 && mq.IsConnected, 5000);
        Assert.Equal(1, disconnected);
    }
}
