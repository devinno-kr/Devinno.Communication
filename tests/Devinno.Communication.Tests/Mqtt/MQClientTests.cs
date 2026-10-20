using System.Diagnostics;
using Devinno.Communication.Mqtt;

namespace Devinno.Communication.Tests.Mqtt;

/// 브로커 없이 도는 테스트 (닫힌 포트, 가짜 브로커)
public class MQClientTests
{
    [Fact]
    public void Defaults()
    {
        using var mq = new MQClient();
        Assert.Equal(("127.0.0.1", 1883, "/test-alive", 1000, 3000), (mq.BrokerHostName, mq.BrokerPort, mq.AliveTopic, mq.AliveInterval, mq.AliveTimeout));
        Assert.Equal((100_000, MQOverflowPolicy.DropOldest, 0, 0L), (mq.MaxPendingMessages, mq.OverflowPolicy, mq.PendingCount, mq.DroppedCount));
        Assert.Equal((false, false, false), (mq.IsStart, mq.IsConnected, mq.IsDisposed));
        Assert.Null(mq.ClientID);
        Assert.Null(mq.UserName);
        Assert.Empty(mq.Subscribes);
        Assert.Null(typeof(MQClient).GetProperty("Password"));   // 비밀번호는 공개 속성으로 노출하지 않는다
    }

    [Fact]
    public void Publish_NotConnected_RaisesPublishDiscardedSync()
    {
        using var mq = new MQClient();
        var got = new List<(string, MQQos, int)>();
        mq.PublishDiscarded += (s, e) => got.Add((e.Topic, e.Qos, Environment.CurrentManagedThreadId));
        mq.Publish("a/b", "x", MQQos.LeastOnce);
        Assert.Equal([("a/b", MQQos.LeastOnce, Environment.CurrentManagedThreadId)], got);
    }

    [Fact]
    public void Publish_InvalidTopic_ThrowsNoEvent()
    {
        using var mq = new MQClient();
        var n = 0;
        mq.PublishDiscarded += (s, e) => n++;
        Assert.Throws<ArgumentException>(() => mq.Publish("a/#", "x"));
        Assert.Equal(0, n);
    }

    [Fact]
    public void Settings_Validation()
    {
        using var mq = new MQClient();
        Assert.Equal("AliveTopic", Assert.Throws<ArgumentException>(() => mq.AliveTopic = "alive/#").ParamName);
        Assert.Equal("/test-alive", mq.AliveTopic);
        mq.AliveTopic = null;
        Assert.Null(mq.AliveTopic);
        mq.AliveTopic = "";
        Assert.Equal("", mq.AliveTopic);
        mq.AliveTopic = "my/alive";
        Assert.Equal("my/alive", mq.AliveTopic);

        Assert.Equal("AliveInterval", Assert.Throws<ArgumentOutOfRangeException>(() => mq.AliveInterval = 99).ParamName);
        Assert.Equal("AliveInterval", Assert.Throws<ArgumentOutOfRangeException>(() => mq.AliveInterval = 60001).ParamName);
        Assert.Equal("AliveTimeout", Assert.Throws<ArgumentOutOfRangeException>(() => mq.AliveTimeout = 99).ParamName);
        Assert.Equal("AliveTimeout", Assert.Throws<ArgumentOutOfRangeException>(() => mq.AliveTimeout = 60001).ParamName);
        Assert.Equal("MaxPendingMessages", Assert.Throws<ArgumentOutOfRangeException>(() => mq.MaxPendingMessages = 0).ParamName);
        Assert.Equal("MaxPendingMessages", Assert.Throws<ArgumentOutOfRangeException>(() => mq.MaxPendingMessages = 10_000_001).ParamName);
        Assert.Equal("OverflowPolicy", Assert.Throws<ArgumentOutOfRangeException>(() => mq.OverflowPolicy = (MQOverflowPolicy)2).ParamName);
        mq.AliveInterval = 100;
        mq.AliveTimeout = 60000;
        mq.MaxPendingMessages = 1;
        mq.OverflowPolicy = MQOverflowPolicy.DropNewest;
        Assert.Equal((100, 60000, 1, MQOverflowPolicy.DropNewest), (mq.AliveInterval, mq.AliveTimeout, mq.MaxPendingMessages, mq.OverflowPolicy));
    }

    [Fact]
    public void Start_NullArguments_FallBackToPreviousValues()
    {
        using var mq = new MQClient { BrokerPort = Mosquitto.ClosedPort(), AliveTopic = null };
        mq.Start("dev01", "user", "secret");
        mq.Stop();
        Assert.Equal(("dev01", "user"), (mq.ClientID, mq.UserName));
        mq.Start();
        mq.Stop();
        Assert.Equal(("dev01", "user"), (mq.ClientID, mq.UserName));
        mq.Start("dev02");
        mq.Stop();
        Assert.Equal(("dev02", "user"), (mq.ClientID, mq.UserName));
        mq.Start("", null, null);
        mq.Stop();
        Assert.Equal("dev02", mq.ClientID);
    }

    [Fact]
    public void Start_WithoutClientID_GeneratedGuidIsNotConfigured()
    {
        using var mq = new MQClient { BrokerPort = Mosquitto.ClosedPort(), AliveTopic = null };
        mq.Start();
        mq.Stop();
        Assert.True(Guid.TryParse(mq.ClientID, out _));   // 이번 세션에 쓴 ClientID는 GUID
        Assert.Null(mq.ConfiguredClientID);               // 사용자가 정한 값은 여전히 없음(설정 저장 대상 아님)
        var generated = mq.ClientID;
        mq.Start();
        mq.Stop();
        Assert.Equal(generated, mq.ClientID);             // 다시 Start해도 같은 GUID

        mq.Start("dev01");
        mq.Stop();
        Assert.Equal(("dev01", "dev01"), (mq.ClientID, mq.ConfiguredClientID));
    }

    [Fact]
    public async Task Threads_OneLoopAndOneDispatcherWhileStarted_NoneAfterStopOrLoopExit()
    {
        using var mq = new MQClient { BrokerPort = Mosquitto.ClosedPort(), AliveTopic = null };
        for (int i = 0; i < 5; i++)
        {
            mq.Start();
            await TestUtil.Until(() => (mq.LoopThreadCount, mq.DispatcherThreadCount) == (1, 1));
            await Task.Delay(100);
            Assert.Equal((1, 1), (mq.LoopThreadCount, mq.DispatcherThreadCount));
            mq.Stop();
            Assert.Equal((0, 0), (mq.LoopThreadCount, mq.DispatcherThreadCount));
        }

        // 루프가 예상하지 못한 예외로 끝나면 디스패처도 같이 끝나야 한다
        mq.LoopIterationHook = () => throw new InvalidOperationException("loop fault");
        mq.Start();
        await TestUtil.Until(() => !mq.IsStart && (mq.LoopThreadCount, mq.DispatcherThreadCount) == (0, 0));

        mq.LoopIterationHook = null;
        mq.Start();
        await TestUtil.Until(() => (mq.LoopThreadCount, mq.DispatcherThreadCount) == (1, 1));
        await Task.Delay(100);
        Assert.Equal((1, 1), (mq.LoopThreadCount, mq.DispatcherThreadCount));
        mq.Stop();
        Assert.Equal((0, 0), (mq.LoopThreadCount, mq.DispatcherThreadCount));
    }

    [Fact]
    public void Publish_InvalidArguments_Throw_ValidNotConnected_IsIgnored()
    {
        using var mq = new MQClient();
        Assert.Equal("Topic", Assert.Throws<ArgumentException>(() => mq.Publish("a/#", [1])).ParamName);
        Assert.Equal("Topic", Assert.Throws<ArgumentException>(() => mq.Publish("a/+/b", "x")).ParamName);
        Assert.Equal("Topic", Assert.Throws<ArgumentException>(() => mq.Publish("", [1])).ParamName);
        Assert.Equal("Topic", Assert.Throws<ArgumentNullException>(() => mq.Publish(null!, [1])).ParamName);
        Assert.Equal("Data", Assert.Throws<ArgumentNullException>(() => mq.Publish("a", (byte[])null!)).ParamName);
        Assert.Equal("Data", Assert.Throws<ArgumentNullException>(() => mq.Publish("a", (string)null!)).ParamName);
        Assert.Equal("qos", Assert.Throws<ArgumentOutOfRangeException>(() => mq.Publish("a", [1], MQQos.GrantedFailure)).ParamName);
        mq.Publish("a/b", [1], MQQos.ExactlyOnce, retain: true);   // 접속 전: 보내지 않고 돌아온다
        mq.Publish("a/b", "text");
    }

    [Fact]
    public void Subscribe_SameTopicReplacesQos_SnapshotIsIndependent_Unsubscribe()
    {
        using var mq = new MQClient();
        mq.Subscribe("a/#");
        mq.Subscribe("b", MQQos.LeastOnce);
        mq.Subscribe("a/#", MQQos.ExactlyOnce);
        var snap = mq.Subscribes;
        Assert.Equal(new[] { ("b", MQQos.LeastOnce), ("a/#", MQQos.ExactlyOnce) }, snap.Select(x => (x.Topic, x.Qos)));

        mq.Unsubscribe("b");
        mq.Unsubscribe("not-there");
        Assert.Equal(2, snap.Count);                      // 앞서 받은 복사본은 바뀌지 않는다
        Assert.Equal(new[] { "a/#" }, mq.Subscribes.Select(x => x.Topic));
        mq.UnsubscribeClear();
        Assert.Empty(mq.Subscribes);
        mq.UnsubscribeClear();

        Assert.Throws<ArgumentNullException>(() => mq.Subscribe((MQSubscribe)null!));
        Assert.Throws<ArgumentException>(() => mq.Subscribe("a/#/b"));
        Assert.Throws<ArgumentOutOfRangeException>(() => mq.Subscribe("a", MQQos.GrantedFailure));
        Assert.Throws<ArgumentNullException>(() => mq.Unsubscribe(null!));
    }

    [Fact]
    public void Subscribe_FromManyThreads_KeepsEveryTopic()
    {
        using var mq = new MQClient();
        Parallel.For(0, 8, t =>
        {
            for (int i = 0; i < 500; i++)
            {
                mq.Subscribe($"t{t}/{i}");
                mq.Subscribe($"tmp{t}/{i}");
                mq.Unsubscribe($"tmp{t}/{i}");
                _ = mq.Subscribes.Count;
            }
        });
        var topics = mq.Subscribes.Select(x => x.Topic).ToList();
        Assert.Equal(4000, topics.Count);
        Assert.Equal(4000, topics.Distinct().Count());
        Assert.DoesNotContain(topics, x => x.StartsWith("tmp"));
    }

    [Fact]
    public void Test_ClosedPort_ReturnsFalse()
    {
        Assert.False(MQClient.Test("127.0.0.1", "probe", Mosquitto.ClosedPort()));
    }
}


/// 타이밍에 민감한 브로커 없는 테스트 (병렬 실행을 끈 컬렉션)
[Collection(nameof(MqttSerialCollection))]
public class MQClientTimingTests
{
    [Fact]
    public async Task Start_ClosedPort_IsStartAtOnce_NoEvents_StopIsFastAndIdempotent()
    {
        var mq = new MQClient { BrokerPort = Mosquitto.ClosedPort(), AliveTopic = null };
        int connected = 0, disconnected = 0;
        mq.Connected += (s, e) => Interlocked.Increment(ref connected);
        mq.Disconnected += (s, e) => Interlocked.Increment(ref disconnected);

        mq.Start();
        Assert.True(mq.IsStart);                       // Start가 돌아오면 바로 true
        Assert.True(Guid.TryParse(mq.ClientID, out _)); // ClientID가 없으면 GUID
        await Task.Delay(1500);
        Assert.True(mq.IsStart);                       // 접속 실패는 루프를 끝내지 않는다(재시도)
        Assert.False(mq.IsConnected);

        var sw = Stopwatch.StartNew();
        mq.Stop();
        Assert.True(sw.ElapsedMilliseconds < 1000, $"Stop {sw.ElapsedMilliseconds}ms");
        Assert.False(mq.IsStart);
        mq.Stop();
        mq.Dispose();
        mq.Dispose();
        Assert.True(mq.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => mq.Start());
        Assert.Equal((0, 0), (connected, disconnected));
    }

    [Fact]
    public async Task SilentBroker_StopIsFast_NoConnected()
    {
        using var broker = FakeMqttBroker.Silent();   // TCP만 받고 CONNACK을 보내지 않음 → 접속 시도가 최대 10초 걸림
        using var mq = new MQClient { BrokerPort = broker.Port, AliveTopic = null };
        int connected = 0;
        mq.Connected += (s, e) => Interlocked.Increment(ref connected);
        mq.Start();
        await TestUtil.Until(() => broker.Accepted == 1);
        await Task.Delay(300);

        var sw = Stopwatch.StartNew();
        mq.Stop();   // 접속 시도를 취소하므로 3초 상한까지 기다리지 않는다
        Assert.True(sw.ElapsedMilliseconds < 1500, $"Stop {sw.ElapsedMilliseconds}ms");
        Assert.False(mq.IsStart);
        Assert.Equal(0, connected);
    }

    [Fact]
    public async Task RefusedConnack_RetriesEverySecond_OneLoop()
    {
        using var broker = FakeMqttBroker.Refusing(5);   // 5 = 인증 거부
        using var mq = new MQClient { BrokerPort = broker.Port, AliveTopic = null };
        int connected = 0;
        mq.Connected += (s, e) => Interlocked.Increment(ref connected);

        mq.Start();
        mq.Start();   // 두 번 불러도 루프는 하나
        await Task.Delay(2500);
        mq.Stop();
        Assert.InRange(broker.Accepted, 2, 4);   // 0초, 1초, 2초 → 3번 (루프가 둘이면 6번)
        Assert.Equal(0, connected);
        Assert.False(MQClient.Test("127.0.0.1", "probe", broker.Port));
    }
}
