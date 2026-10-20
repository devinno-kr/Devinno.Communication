using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Xml.Linq;
using Devinno.Communication.Modbus.TCP;

namespace Devinno.Communication.Tests.Modbus;

// Plan 13 쓰기 안전: 끊긴 동안 쓰기 버림, Stop이 쓰기 비움, 실패 이벤트의 시도 정보
public class ModbusTcpWriteSafetyTests : IDisposable
{
    readonly int[] hold = new int[100];
    readonly ModbusTCPSlave slave;
    readonly List<IDisposable> owned = [];

    public ModbusTcpWriteSafetyTests()
    {
        slave = new ModbusTCPSlave { LocalPort = 0, Slave = 1, HoldingRegisters = hold };
        slave.Start();
    }

    public void Dispose()
    {
        foreach (var d in owned) d.Dispose();
        slave.Dispose();
    }

    ModbusTCPMaster Master(int port, int timeout = 500)
    {
        var m = new ModbusTCPMaster { RemoteIP = "127.0.0.1", RemotePort = port, Timeout = timeout, Interval = 1 };
        owned.Add(m);
        return m;
    }

    static List<(int Id, WriteDiscardReason Reason)> Record(ModbusTCPMaster m)
    {
        var list = new List<(int, WriteDiscardReason)>();
        m.WriteDiscarded += (s, e) => { lock (list) list.Add((e.MessageID, e.Reason)); };
        return list;
    }

    static List<(int, WriteDiscardReason)> Snap(List<(int Id, WriteDiscardReason Reason)> list)
    {
        lock (list) return [.. list];
    }

    // 아무도 듣지 않는 포트 (연결 실패용)
    static int ClosedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    // accept만 하고 응답하지 않는 원시 상대
    sealed class SilentPeer : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly Task<TcpClient> accepted;
        public SilentPeer() { listener.Start(); accepted = listener.AcceptTcpClientAsync(); }
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public Task<TcpClient> Client => accepted.WaitAsync(TimeSpan.FromSeconds(3));

        // 요청 한 프레임(MBAP 6바이트 + 길이)을 다 받을 때까지 읽는다
        public async Task ReadFrame()
        {
            var stream = (await Client).GetStream();
            var head = new byte[6];
            await stream.ReadExactlyAsync(head).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            var body = new byte[head[4] << 8 | head[5]];
            await stream.ReadExactlyAsync(body).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }

        public void Dispose()
        {
            if (accepted.IsCompletedSuccessfully) accepted.Result.Dispose();
            listener.Stop();
        }
    }

    [Fact]
    public void Write_BeforeStart_DiscardedSync_NotSent()
    {
        var m = Master(slave.BoundPort);
        var ev = Record(m);
        m.ManualWordWrite_FC6(7, 1, 0, 5);
        Assert.Equal([(7, WriteDiscardReason.NotConnected)], Snap(ev));
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void Read_BeforeStart_Kept()
    {
        var m = Master(slave.BoundPort);
        var ev = Record(m);
        m.ManualWordRead_FC3(8, 1, 0, 1);
        Assert.Empty(Snap(ev));
        Assert.Equal(8, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public async Task DiscardFalse_WriteBeforeStart_SentAfterConnect()
    {
        var m = Master(slave.BoundPort);
        m.DiscardWritesWhileDisconnected = false;
        var ev = Record(m);
        var got = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.WordWriteReceived += (s, e) => got.TrySetResult(e.WriteValue);
        m.ManualWordWrite_FC6(1, 1, 0, 1234);
        m.Start();
        Assert.Equal(1234, await got.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1234, hold[0]);
        Assert.Empty(Snap(ev));
    }

    [Fact]
    public void StartThenWrite_BeforeConnect_DiscardedSync()
    {
        var m = Master(ClosedPort());
        var ev = Record(m);
        m.Start();
        m.ManualWordWrite_FC6(3, 1, 0, 5);
        Assert.Equal([(3, WriteDiscardReason.NotConnected)], Snap(ev));
    }

    [Fact]
    public void LinkDown_DiscardsWritesKeepsReads_InOrder()
    {
        var m = Master(slave.BoundPort);
        var ev = Record(m);
        m.SetLinkUp();
        m.ManualWordWrite_FC6(1, 1, 0, 1);
        m.ManualWordRead_FC3(2, 1, 0, 1);
        m.ManualWordWrite_FC6(3, 1, 0, 1);
        Assert.Empty(Snap(ev));
        m.SetLinkDown(false);
        Assert.Equal([(1, WriteDiscardReason.Disconnected), (3, WriteDiscardReason.Disconnected)], Snap(ev));
        Assert.Equal(2, m.TakeNextWork()!.MessageID);
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void LinkDown_Stopping_ReasonStopped()
    {
        var m = Master(slave.BoundPort);
        var ev = Record(m);
        m.SetLinkUp();
        m.ManualWordWrite_FC6(1, 1, 0, 1);
        m.ManualWordRead_FC3(2, 1, 0, 1);
        m.ManualWordWrite_FC6(3, 1, 0, 1);
        m.SetLinkDown(true);
        Assert.Equal([(1, WriteDiscardReason.Stopped), (3, WriteDiscardReason.Stopped)], Snap(ev));
        Assert.Equal(2, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void LinkDown_DiscardFalse_KeepsWrites()
    {
        var m = Master(slave.BoundPort);
        m.DiscardWritesWhileDisconnected = false;
        var ev = Record(m);
        m.SetLinkUp();
        m.ManualWordWrite_FC6(1, 1, 0, 1);
        m.SetLinkDown(false);
        Assert.Empty(Snap(ev));
        Assert.Equal(1, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public async Task Stop_DiscardFalse_DiscardsQueuedWrites_NotSentOnRestart()
    {
        var m = Master(ClosedPort());
        m.DiscardWritesWhileDisconnected = false;
        var ev = Record(m);
        int written = 0;
        m.WordWriteReceived += (s, e) => Interlocked.Increment(ref written);
        m.Start();
        m.ManualWordWrite_FC6(1, 1, 0, 11);
        m.ManualWordWrite_FC6(2, 1, 1, 22);
        Assert.Empty(Snap(ev));
        m.Stop();
        Assert.Equal([(1, WriteDiscardReason.Stopped), (2, WriteDiscardReason.Stopped)], Snap(ev));

        m.RemotePort = slave.BoundPort;
        m.Start();
        await TestUtil.Until(() => m.IsOpen);
        await Task.Delay(300);
        Assert.Equal(0, written);
        Assert.Equal(0, hold[0]);
        Assert.Equal(0, hold[1]);
        Assert.Equal(2, Snap(ev).Count);
    }

    [Fact]
    public async Task Stop_WithLinkUp_EachWriteStoppedOnce()
    {
        using var peer = new SilentPeer();
        var m = Master(peer.Port, timeout: 5000);
        var ev = Record(m);
        var timeouts = new ConcurrentQueue<ModbusTCPMaster.AttemptFailedEventArgs>();
        m.TimeoutReceived += (s, e) => timeouts.Enqueue(e);
        m.Start();
        await TestUtil.Until(() => m.IsOpen);
        m.ManualWordWrite_FC6(1, 1, 0, 1);
        await peer.ReadFrame();          // 쓰기 1은 응답을 기다리는 중
        m.ManualWordWrite_FC6(2, 1, 0, 2);
        m.ManualWordWrite_FC6(3, 1, 0, 3);
        m.Stop();

        await TestUtil.Until(() => !timeouts.IsEmpty);
        await Task.Delay(100);
        var t = Assert.Single(timeouts);
        Assert.Equal((1, 1, true, true), (t.MessageID, t.Attempt, t.IsFinal, t.RequestSent));
        Assert.Equal([(2, WriteDiscardReason.Stopped), (3, WriteDiscardReason.Stopped)], Snap(ev));
    }

    [Fact]
    public async Task PeerClose_TimeoutThenDeviceClosedThenDisconnected()
    {
        using var peer = new SilentPeer();
        var m = Master(peer.Port, timeout: 5000);
        var log = new ConcurrentQueue<string>();
        m.TimeoutReceived += (s, e) => log.Enqueue($"Timeout({e.MessageID},{e.Attempt},{e.IsFinal},{e.RequestSent})");
        m.DeviceClosed += (s, e) => log.Enqueue("DeviceClosed");
        m.WriteDiscarded += (s, e) => log.Enqueue($"Discarded({e.MessageID},{e.Reason})");
        m.Start();
        await TestUtil.Until(() => m.IsOpen);
        m.ManualWordWrite_FC6(1, 1, 0, 1);
        await peer.ReadFrame();
        m.ManualWordWrite_FC6(2, 1, 0, 2);
        m.ManualWordWrite_FC6(3, 1, 0, 3);
        (await peer.Client).Close();

        await TestUtil.Until(() => log.Count >= 4);
        Assert.Equal(
            ["Timeout(1,1,True,True)", "DeviceClosed", "Discarded(2,Disconnected)", "Discarded(3,Disconnected)"],
            log.Take(4).ToArray());
    }

    [Fact]
    public async Task Retry_AttemptNumbers()
    {
        using var peer = new SilentPeer();
        var m = Master(peer.Port, timeout: 100);
        var timeouts = new ConcurrentQueue<ModbusTCPMaster.AttemptFailedEventArgs>();
        m.TimeoutReceived += (s, e) => timeouts.Enqueue(e);
        m.Start();
        await TestUtil.Until(() => m.IsOpen);
        m.ManualWordWrite_FC6(5, 1, 0, 1, repeatCount: 2);
        await TestUtil.Until(() => timeouts.Count >= 3);
        await Task.Delay(300);
        var list = timeouts.ToArray();
        Assert.Equal(3, list.Length);
        Assert.Equal([1, 2, 3], list.Select(e => e.Attempt));
        Assert.Equal([false, false, true], list.Select(e => e.IsFinal));
        Assert.All(list, e => Assert.True(e.RequestSent));
        Assert.All(list, e => Assert.Equal(5, e.MessageID));
    }

    [Fact]
    public void Xml_RoundTrip_DiscardWritesWhileDisconnected()
    {
        var m = new ModbusTCPMaster();
        m.FromXml(XElement.Parse("""<ModbusTCPMaster DiscardWritesWhileDisconnected="false" />"""));
        Assert.False(m.DiscardWritesWhileDisconnected);
        Assert.Equal("false", m.ToXml().Attribute("DiscardWritesWhileDisconnected")?.Value);

        m.FromXml(XElement.Parse("<ModbusTCPMaster />"));
        Assert.True(m.DiscardWritesWhileDisconnected);
    }
}
