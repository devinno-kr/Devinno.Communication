using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Devinno.Communication.Mqtt;

namespace Devinno.Communication.Tests.Mqtt;

/// 127.0.0.1:1883에 MQTT 브로커(mosquitto)가 있을 때만 실행하는 테스트. 없으면 건너뛴다(CI에서도 녹색 유지).
public sealed class MosquittoFactAttribute : FactAttribute
{
    public MosquittoFactAttribute()
    {
        if (!Mosquitto.IsAvailable) Skip = $"{Mosquitto.Host}:{Mosquitto.Port}에 MQTT 브로커(mosquitto)가 없어 건너뜀";
    }
}

internal static class Mosquitto
{
    public const string Host = "127.0.0.1";
    public const int Port = 1883;
    static readonly Lazy<bool> available = new(Probe);
    public static bool IsAvailable => available.Value;

    static bool Probe()
    {
        using var c = new TcpClient();
        try { return c.ConnectAsync(Host, Port).Wait(500) && c.Connected; }
        catch { return false; }
    }

    /// 테스트끼리 겹치지 않는 토픽 접두어
    public static string NewTopic() => $"devinno-test/{Guid.NewGuid():N}";

    /// 아무도 듣지 않는 127.0.0.1 포트 (접속하면 바로 거부된다)
    public static int ClosedPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}

/// 구독·구독 해제는 응답을 기다리지 않고 돌아오므로, 고정 지연 대신 표식(probe) 메시지로 브로커 반영을 확인한다.
/// 구독자의 Received 핸들러 첫 줄에서 <c>if (probe.Handle(e)) return;</c>으로 표식을 걸러 세고, 나머지만 테스트가 센다.
internal sealed class Probe
{
    public static readonly byte[] Payload = "devinno-probe"u8.ToArray();
    readonly ConcurrentDictionary<string, int> counts = new();

    /// 표식이면 토픽별로 세고 true
    public bool Handle(MQReceiveArgs e)
    {
        if (!e.Datas.AsSpan().SequenceEqual(Payload)) return false;
        counts.AddOrUpdate(e.Topic, 1, (_, n) => n + 1);
        return true;
    }

    public int Count(string topic) => counts.TryGetValue(topic, out var n) ? n : 0;

    /// 구독이 반영될 때까지: pub로 topic에 표식을 200ms마다 다시 보내 구독자가 하나라도 받으면 끝(최대 timeoutMs)
    public async Task UntilDelivered(MQClient pub, string topic, int timeoutMs = 5000)
    {
        int start = Count(topic);
        var sw = Stopwatch.StartNew();
        while (Count(topic) == start)
        {
            if (sw.ElapsedMilliseconds > timeoutMs) Assert.Fail($"{topic} 표식이 {timeoutMs}ms 안에 도착하지 않았습니다.");
            pub.Publish(topic, Payload);
            var next = sw.ElapsedMilliseconds + 200;
            while (Count(topic) == start && sw.ElapsedMilliseconds < next) await Task.Delay(10);
        }
    }

    /// 구독 해제가 반영될 때까지: topic 표식 다음에 control(구독 중인 토픽) 표식을 보내, control 표식이 도착한 뒤
    /// 200ms 창 안에 topic 표식이 오지 않으면 끝. 오면 다시 시도한다(최대 timeoutMs).
    public async Task UntilNotDelivered(MQClient pub, string topic, string control, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (sw.ElapsedMilliseconds > timeoutMs) Assert.Fail($"{topic} 구독 해제가 {timeoutMs}ms 안에 반영되지 않았습니다.");
            int t0 = Count(topic), c0 = Count(control);
            pub.Publish(topic, Payload);
            pub.Publish(control, Payload);
            var next = sw.ElapsedMilliseconds + 1000;
            while (Count(control) == c0 && sw.ElapsedMilliseconds < next) await Task.Delay(10);
            if (Count(control) == c0) continue;   // control 표식이 아직 안 옴 → 다시
            await Task.Delay(200);
            if (Count(topic) == t0) return;
        }
    }

    /// 앞서 보낸 메시지가 다 도착할 시간을 준다: control 표식을 보내 도착을 확인한 뒤 200ms 더 기다린다
    public async Task Flush(MQClient pub, string control)
    {
        await UntilDelivered(pub, control);
        await Task.Delay(200);
    }
}

/// 127.0.0.1의 임의 포트 → 대상 포트로 바이트를 그대로 넘기는 TCP 프록시. KillAll로 연결을 강제로 끊어 재접속을 시험하고,
/// Blackhole로 "TCP는 살아 있는데 아무 데이터도 오가지 않는" 연결(중간 장비 고장 등)을 흉내 낸다.
internal sealed class TcpForwardProxy : IDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly int targetPort;
    readonly List<Socket> sockets = [];
    readonly CancellationTokenSource cts = new();
    int accepted;
    volatile bool blackhole;

    public TcpForwardProxy(int targetPort)
    {
        this.targetPort = targetPort;
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = AcceptLoop();
    }

    public int Port { get; }
    /// 받은 연결 수
    public int Accepted => Volatile.Read(ref accepted);

    /// true: 열린 연결은 그대로 두고 오가는 데이터를 모두 버리며, 새 연결은 받자마자 닫는다. false로 돌리면 다시 정상 중계.
    public bool Blackhole { get => blackhole; set => blackhole = value; }

    async Task AcceptLoop()
    {
        try
        {
            while (true)
            {
                var client = await listener.AcceptSocketAsync(cts.Token);
                if (blackhole) { Close(client); continue; }
                var upstream = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await upstream.ConnectAsync(IPAddress.Loopback, targetPort, cts.Token);
                lock (sockets) { sockets.Add(client); sockets.Add(upstream); }
                Interlocked.Increment(ref accepted);
                _ = Pump(client, upstream);
                _ = Pump(upstream, client);
            }
        }
        catch { }
    }

    async Task Pump(Socket from, Socket to)
    {
        var buf = new byte[8192];
        try
        {
            using var output = new NetworkStream(to, ownsSocket: false);
            int n;
            while ((n = await from.ReceiveAsync(buf, SocketFlags.None)) > 0)
            {
                if (!blackhole) await output.WriteAsync(buf.AsMemory(0, n));
            }
        }
        catch { }
        finally { Close(from); Close(to); }
    }

    /// 지금 열린 연결을 모두 끊는다(양쪽 소켓 닫기)
    public void KillAll()
    {
        lock (sockets)
        {
            foreach (var s in sockets) Close(s);
            sockets.Clear();
        }
    }

    static void Close(Socket s)
    {
        try { s.Shutdown(SocketShutdown.Both); } catch { }
        s.Dispose();
    }

    public void Dispose()
    {
        cts.Cancel();
        listener.Stop();
        KillAll();
    }
}

/// 가짜 MQTT 브로커. Refusing: CONNECT를 받으면 CONNACK(거부 코드)을 보내고 연결을 닫는다. Silent: 접속만 받고 아무것도 보내지 않는다.
internal sealed class FakeMqttBroker : IDisposable
{
    // Silent 모드의 소켓은 닫지 않고 프로세스 끝까지 둔다(연결은 살아 있는데 CONNACK이 오지 않는 상태를 유지).
    static readonly List<Socket> silentSockets = [];

    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly byte? refuseCode;
    int accepted;

    FakeMqttBroker(byte? refuseCode)
    {
        this.refuseCode = refuseCode;
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = AcceptLoop();
    }

    public static FakeMqttBroker Refusing(byte returnCode) => new(returnCode);
    public static FakeMqttBroker Silent() => new(null);

    public int Port { get; }
    /// 받은 연결 수 (= 접속 시도 수)
    public int Accepted => Volatile.Read(ref accepted);

    async Task AcceptLoop()
    {
        try
        {
            while (true)
            {
                var s = await listener.AcceptSocketAsync();
                Interlocked.Increment(ref accepted);
                if (refuseCode is byte code) _ = Refuse(s, code);
                else lock (silentSockets) silentSockets.Add(s);
            }
        }
        catch { }
    }

    static async Task Refuse(Socket s, byte code)
    {
        try
        {
            var buf = new byte[1024];
            await s.ReceiveAsync(buf, SocketFlags.None);                              // CONNECT
            await s.SendAsync(new byte[] { 0x20, 0x02, 0x00, code }, SocketFlags.None); // CONNACK
            await Task.Delay(50);
        }
        catch { }
        finally
        {
            try { s.Shutdown(SocketShutdown.Both); } catch { }
            s.Dispose();
        }
    }

    public void Dispose() => listener.Stop();
}
