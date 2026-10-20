using System.Text;
using Devinno.Communication.Mqtt;

namespace Devinno.Communication.Tests.Mqtt;

/// 127.0.0.1:1883의 mosquitto와 통신하는 통합 테스트. 브로커가 없으면 건너뛴다.
public class MQClientBrokerTests
{
    /// 접속까지 마친 클라이언트 (alive 발행 끔)
    static async Task<MQClient> Started(int port = Mosquitto.Port)
    {
        var mq = new MQClient { BrokerPort = port, AliveTopic = null };
        mq.Start();
        await TestUtil.Until(() => mq.IsConnected, 5000);
        return mq;
    }

    [MosquittoFact]
    public async Task Connect_RaisesConnected_Stop_RaisesDisconnectedOnce()
    {
        var mq = new MQClient { AliveTopic = null };
        int connected = 0, disconnected = 0;
        mq.Connected += (s, e) => Interlocked.Increment(ref connected);
        mq.Disconnected += (s, e) => Interlocked.Increment(ref disconnected);
        mq.Start();
        await TestUtil.Until(() => connected == 1, 5000);
        Assert.True(mq.IsConnected);

        mq.Stop();
        Assert.False(mq.IsConnected);
        Assert.False(mq.IsStart);
        await Task.Delay(300);
        Assert.Equal((1, 1), (connected, disconnected));
        mq.Dispose();
        Assert.Equal((1, 1), (connected, disconnected));
    }

    [MosquittoFact]
    public async Task Received_ExactlyOnce_WithTopicAndPayload_OnDispatcherThread()
    {
        var root = Mosquitto.NewTopic();
        var probe = new Probe();
        var got = new List<(string Topic, byte[] Data)>();
        var threads = new HashSet<string?>();
        using var sub = new MQClient { AliveTopic = null };
        sub.Received += (s, e) =>
        {
            lock (got) threads.Add(Thread.CurrentThread.Name);
            if (probe.Handle(e)) return;
            lock (got) got.Add((e.Topic, e.Datas));
        };
        sub.Subscribe($"{root}/early/#", MQQos.LeastOnce);   // 접속 전 등록 → 접속할 때 요청
        sub.Start();
        await TestUtil.Until(() => sub.IsConnected, 5000);
        sub.Subscribe($"{root}/late");                       // 접속 중 등록 → 바로 요청

        using var pub = await Started();
        await probe.UntilDelivered(pub, $"{root}/early/x");
        await probe.UntilDelivered(pub, $"{root}/late");
        pub.Publish($"{root}/early/x", [1, 2, 3], MQQos.LeastOnce);
        pub.Publish($"{root}/late", "온도");
        await TestUtil.Until(() => { lock (got) return got.Count >= 2; });
        await Task.Delay(500);
        sub.Dispose();

        lock (got)
        {
            Assert.Equal(2, got.Count);
            var early = got.Single(x => x.Topic == $"{root}/early/x");
            var late = got.Single(x => x.Topic == $"{root}/late");
            Assert.Equal(new byte[] { 1, 2, 3 }, early.Data);
            Assert.Equal("온도", Encoding.UTF8.GetString(late.Data));
            Assert.Equal(new[] { "MQClient.Received" }, threads);   // 사용자 처리는 전용 디스패처 스레드에서
        }
    }

    [MosquittoFact]
    public async Task Unsubscribe_StopsDelivery()
    {
        var topic = Mosquitto.NewTopic();
        var control = topic + "-control";   // 계속 구독해 두는 대조 토픽
        var probe = new Probe();
        int n = 0;
        using var sub = await Started();
        sub.Received += (s, e) => { if (probe.Handle(e)) return; if (e.Topic == topic) Interlocked.Increment(ref n); };
        using var pub = await Started();
        sub.Subscribe(topic);
        sub.Subscribe(control);
        await probe.UntilDelivered(pub, topic);
        await probe.UntilDelivered(pub, control);

        pub.Publish(topic, [1]);
        await TestUtil.Until(() => n == 1);

        sub.Unsubscribe(topic);
        await probe.UntilNotDelivered(pub, topic, control);
        pub.Publish(topic, [2]);
        await probe.Flush(pub, control);   // [2] 다음에 보낸 대조 표식이 도착할 때까지
        Assert.Equal(1, n);

        sub.Subscribe(topic);
        await probe.UntilDelivered(pub, topic);
        sub.UnsubscribeClear();
        sub.Subscribe(control);            // 같은 연결의 요청은 순서대로 처리되므로 대조 표식 도착 = 해제 반영
        await probe.UntilNotDelivered(pub, topic, control);
        pub.Publish(topic, [3]);
        await probe.Flush(pub, control);
        Assert.Equal(1, n);
    }

    [MosquittoFact]
    public async Task Reconnect_ThroughProxy_ResubscribesAndDeliversExactlyOnce()
    {
        var topic = Mosquitto.NewTopic();
        var probe = new Probe();
        using var proxy = new TcpForwardProxy(Mosquitto.Port);
        using var sub = new MQClient { BrokerPort = proxy.Port, AliveTopic = null };
        int connected = 0, disconnected = 0, received = 0;
        sub.Connected += (s, e) => Interlocked.Increment(ref connected);
        sub.Disconnected += (s, e) => Interlocked.Increment(ref disconnected);
        sub.Received += (s, e) => { if (probe.Handle(e)) return; if (e.Topic == topic) Interlocked.Increment(ref received); };
        sub.Subscribe(topic, MQQos.LeastOnce);
        sub.Start();
        await TestUtil.Until(() => connected == 1 && sub.IsConnected, 5000);

        for (int i = 1; i <= 2; i++)
        {
            int k = i;
            proxy.KillAll();   // 브로커 쪽에서 끊긴 것처럼 연결을 강제로 닫는다
            await TestUtil.Until(() => disconnected == k, 5000);
            await TestUtil.Until(() => connected == k + 1 && sub.IsConnected, 5000);
        }
        Assert.Equal(3, proxy.Accepted);

        using var pub = await Started();
        await probe.UntilDelivered(pub, topic);   // 다시 요청한 SUBSCRIBE가 브로커에 반영되었는지
        pub.Publish(topic, [7]);
        await TestUtil.Until(() => received >= 1);
        await Task.Delay(700);
        Assert.Equal(1, received);
        Assert.Equal((3, 2), (connected, disconnected));
    }

    [MosquittoFact]
    public async Task HandlerExceptions_DoNotStopDispatcherOrLoop()
    {
        var topic = Mosquitto.NewTopic();
        var probe = new Probe();
        int n = 0;
        using var sub = new MQClient { AliveTopic = null };
        sub.Connected += (s, e) => throw new InvalidOperationException("connected handler");
        sub.Disconnected += (s, e) => throw new InvalidOperationException("disconnected handler");
        sub.Received += (s, e) =>
        {
            if (!probe.Handle(e)) Interlocked.Increment(ref n);
            throw new InvalidOperationException("received handler");
        };
        sub.Subscribe(topic);
        sub.Start();
        await TestUtil.Until(() => sub.IsConnected, 5000);

        using var pub = await Started();
        await probe.UntilDelivered(pub, topic);
        pub.Publish(topic, [1]);
        await TestUtil.Until(() => n == 1);
        pub.Publish(topic, [2]);
        await TestUtil.Until(() => n == 2);   // 디스패처 스레드가 살아 있다
        Assert.True(sub.IsConnected);
        Assert.Equal(0, sub.PendingCount);
    }

    [MosquittoFact]
    public void Test_LocalBroker_ReturnsTrue()
    {
        Assert.True(MQClient.Test(Mosquitto.Host, $"probe-{Guid.NewGuid():N}"));
    }
}
