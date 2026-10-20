using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Xml.Linq;
using Devinno.Communication.Mitsubishi.Data;
using Devinno.Communication.Mitsubishi.Ethernet;
using Devinno.Communication.Mitsubishi.Serial;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Mitsubishi;

// Plan 13 쓰기 안전: 끊긴 동안 쓰기 버림, Stop이 쓰기 비움, 실패 이벤트의 시도 정보.
// McSerial은 포트를 테스트에서 열 수 없으므로 연결 상태는 internal seam(SetLinkUp/SetLinkDown)으로, 송수신은 Process로 시험한다.
public class McWriteSafetyTests
{
    static List<(int Id, WriteDiscardReason Reason)> Snap(List<(int Id, WriteDiscardReason Reason)> list)
    {
        lock (list) return [.. list];
    }

    #region McSerial
    static List<(int Id, WriteDiscardReason Reason)> Record(McSerial m)
    {
        var list = new List<(int, WriteDiscardReason)>();
        m.WriteDiscarded += (s, e) => { lock (list) list.Add((e.MessageID, e.Reason)); };
        return list;
    }

    [Fact]
    public void Serial_Write_BeforeStart_DiscardedSync_NotSent()
    {
        var m = new McSerial();
        var ev = Record(m);
        m.ManualWW(7, 0, "D0", [5]);
        m.ManualBW(8, 0, "M0", [1]);
        Assert.Equal([(7, WriteDiscardReason.NotConnected), (8, WriteDiscardReason.NotConnected)], Snap(ev));
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void Serial_Read_BeforeStart_Kept()
    {
        var m = new McSerial();
        var ev = Record(m);
        m.ManualWR(8, 0, "D0", 1);
        Assert.Empty(Snap(ev));
        Assert.Equal(8, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void Serial_LinkDown_DiscardsWritesKeepsReads_InOrder()
    {
        var m = new McSerial();
        var ev = Record(m);
        m.SetLinkUp();
        m.ManualWW(1, 0, "D0", [1]);
        m.ManualBR(2, 0, "M0", 1);
        m.ManualBW(3, 0, "M0", [1]);
        Assert.Empty(Snap(ev));
        m.SetLinkDown(false);
        Assert.Equal([(1, WriteDiscardReason.Disconnected), (3, WriteDiscardReason.Disconnected)], Snap(ev));
        Assert.Equal(2, m.TakeNextWork()!.MessageID);
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void Serial_LinkDown_Stopping_ReasonStopped()
    {
        var m = new McSerial();
        var ev = Record(m);
        m.SetLinkUp();
        m.ManualWW(1, 0, "D0", [1]);
        m.ManualWR(2, 0, "D0", 1);
        m.SetLinkDown(true);
        Assert.Equal([(1, WriteDiscardReason.Stopped)], Snap(ev));
        Assert.Equal(2, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void Serial_LinkDown_DiscardFalse_KeepsWrites()
    {
        var m = new McSerial { DiscardWritesWhileDisconnected = false };
        var ev = Record(m);
        m.SetLinkUp();
        m.ManualWW(1, 0, "D0", [1]);
        m.SetLinkDown(false);
        Assert.Empty(Snap(ev));
        Assert.Equal(1, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void Serial_Stop_Unstarted_DiscardFalse_RaisesStopped()
    {
        var m = new McSerial { DiscardWritesWhileDisconnected = false };
        var ev = Record(m);
        m.ManualWW(1, 0, "D0", [1]);
        m.ManualWR(2, 0, "D0", 1);
        m.ManualBW(3, 0, "M0", [1]);
        Assert.Empty(Snap(ev));
        m.Stop();
        Assert.Equal([(1, WriteDiscardReason.Stopped), (3, WriteDiscardReason.Stopped)], Snap(ev));
        Assert.Equal(2, m.TakeNextWork()!.MessageID);
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void Serial_LinkDown_FromOtherThread_DoesNotOverrideNewerLink()
    {
        var m = new McSerial();
        var ev = Record(m);
        // 다른 스레드(옛 루프)가 올린 연결을 이 스레드(새 루프)가 이어받은 뒤, 옛 루프의 SetLinkDown이 오는 상황
        var old = new Thread(() => m.SetLinkUp());
        old.Start(); old.Join();
        m.SetLinkUp();
        var late = new Thread(() => m.SetLinkDown(false));
        late.Start(); late.Join();
        m.ManualWW(9, 0, "D0", [1]);
        Assert.Empty(Snap(ev));
        Assert.Equal(9, m.TakeNextWork()!.MessageID);
    }

    static McSerial NoDiscard() => new() { DiscardWritesWhileDisconnected = false };

    [Fact]
    public void Serial_Process_Timeouts_AttemptAndIsFinal()
    {
        var m = NoDiscard();
        m.ManualWW(5, 3, "D0", [1], repeatCount: 2);
        var w = m.TakeNextWork()!;
        var timeouts = new List<McSerial.AttemptFailedEventArgs>();
        var sums = new List<McSerial.AttemptFailedEventArgs>();
        m.TimeoutReceived += (s, e) => timeouts.Add(e);
        m.SumCheckErrorReceived += (s, e) => sums.Add(e);
        int sent = 0;
        var status = m.Process(w, (req, t) => { sent++; return null; }, ack => { });
        Assert.Equal(McStatus.Invalid, status);
        Assert.Equal(3, sent);
        Assert.Equal([1, 2, 3], timeouts.Select(e => e.Attempt));
        Assert.Equal([false, false, true], timeouts.Select(e => e.IsFinal));
        Assert.All(timeouts, e => Assert.True(e.RequestSent));
        Assert.All(timeouts, e => Assert.Equal((5, 3, McCommand.WW), (e.MessageID, e.Slave, e.Command)));
        Assert.Empty(sums);
    }

    [Fact]
    public void Serial_Process_LastAttemptSumError_IsFinalSumCheckEvent()
    {
        using var plc = new FakeMcPlc { BadSum = true };
        var m = NoDiscard();
        m.ManualWR(5, 0, "D0", 1, repeatCount: 1);
        var w = m.TakeNextWork()!;
        var timeouts = new List<McSerial.AttemptFailedEventArgs>();
        var sums = new List<McSerial.AttemptFailedEventArgs>();
        m.TimeoutReceived += (s, e) => timeouts.Add(e);
        m.SumCheckErrorReceived += (s, e) => sums.Add(e);
        m.Process(w, (req, t) => plc.Handle1C(req, w.Format, w.SumCheck), ack => { });
        Assert.Empty(timeouts);
        Assert.Equal([1, 2], sums.Select(e => e.Attempt));
        Assert.Equal([false, true], sums.Select(e => e.IsFinal));
        Assert.All(sums, e => Assert.True(e.RequestSent));
        Assert.All(sums, e => Assert.Equal(5, e.MessageID));
    }

    [Fact]
    public void Serial_Process_Success_NoFailureEvents()
    {
        using var plc = new FakeMcPlc();
        var m = NoDiscard();
        m.ManualWW(5, 0, "D0", [1]);
        var w = m.TakeNextWork()!;
        int ok = 0, failed = 0;
        m.WriteReceived += (s, e) => ok++;
        m.TimeoutReceived += (s, e) => failed++;
        m.SumCheckErrorReceived += (s, e) => failed++;
        Assert.Equal(McStatus.Ack, m.Process(w, (req, t) => plc.Handle1C(req, w.Format, w.SumCheck), ack => { }));
        Assert.Equal((1, 0), (ok, failed));
    }

    [Fact]
    public void Serial_Process_ExchangeThrows_FinalNotSent_Rethrown()
    {
        var m = NoDiscard();
        m.ManualWW(5, 0, "D0", [1], repeatCount: 2);
        var w = m.TakeNextWork()!;
        var timeouts = new List<McSerial.AttemptFailedEventArgs>();
        m.TimeoutReceived += (s, e) => timeouts.Add(e);
        Assert.Throws<IOException>(() => m.Process(w, (req, t) => throw new IOException(), ack => { }));
        var e = Assert.Single(timeouts);
        Assert.Equal((1, true, false), (e.Attempt, e.IsFinal, e.RequestSent));
    }

    [Fact]
    public void Serial_Xml_RoundTrip_DiscardWritesWhileDisconnected()
    {
        var m = new McSerial();
        m.FromXml(XElement.Parse("""<McSerial DiscardWritesWhileDisconnected="false" />"""));
        Assert.False(m.DiscardWritesWhileDisconnected);
        Assert.Equal("false", m.ToXml().Attribute("DiscardWritesWhileDisconnected")?.Value);

        m.FromXml(XElement.Parse("<McSerial />"));
        Assert.True(m.DiscardWritesWhileDisconnected);
    }
    #endregion

    #region McEthernet
    static List<(int Id, WriteDiscardReason Reason)> Record(McEthernet m)
    {
        var list = new List<(int, WriteDiscardReason)>();
        m.WriteDiscarded += (s, e) => { lock (list) list.Add((e.MessageID, e.Reason)); };
        return list;
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

        // 3E 요청 한 프레임(머리 9바이트 + 길이)을 다 받을 때까지 읽는다
        public async Task ReadFrame()
        {
            var stream = (await Client).GetStream();
            var head = new byte[9];
            await stream.ReadExactlyAsync(head).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            var body = new byte[head[7] | head[8] << 8];
            await stream.ReadExactlyAsync(body).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }

        public void Dispose()
        {
            if (accepted.IsCompletedSuccessfully) accepted.Result.Dispose();
            listener.Stop();
        }
    }

    // 3E(Q/L, X/Y 16진수) 가상 PLC에 붙는 Master
    McEthernet Master(int port, List<IDisposable> owned, int timeout = 500)
    {
        var m = new McEthernet { RemoteIP = "127.0.0.1", RemotePort = port, Timeout = timeout, Interval = 1 };
        owned.Add(m);
        return m;
    }

    [Fact]
    public void Eth_Write_BeforeStart_DiscardedSync_NotSent()
    {
        var m = new McEthernet();
        var ev = Record(m);
        m.ManualWW(7, "D0", [5]);
        m.ManualBW(8, "M0", [1]);
        Assert.Equal([(7, WriteDiscardReason.NotConnected), (8, WriteDiscardReason.NotConnected)], Snap(ev));
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void Eth_Read_BeforeStart_Kept()
    {
        var m = new McEthernet();
        var ev = Record(m);
        m.ManualWR(8, "D0", 1);
        Assert.Empty(Snap(ev));
        Assert.Equal(8, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public async Task Eth_DiscardFalse_WriteBeforeStart_SentAfterConnect()
    {
        using var plc = new FakeMcPlc { XYOctal = false };
        plc.Start(McFrame.E3);
        using var m = new McEthernet { RemoteIP = "127.0.0.1", RemotePort = plc.Port, Timeout = 500, Interval = 1, DiscardWritesWhileDisconnected = false };
        var ev = Record(m);
        var got = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.WriteReceived += (s, e) => got.TrySetResult(e.MessageID);
        m.ManualWW(1, "D200", [1234]);
        m.Start();
        Assert.Equal(1, await got.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1234L, plc.Get("D200"));
        Assert.Empty(Snap(ev));
    }

    [Fact]
    public void Eth_StartThenWrite_BeforeConnect_DiscardedSync()
    {
        using var m = new McEthernet { RemoteIP = "127.0.0.1", RemotePort = ClosedPort(), Timeout = 500, Interval = 1 };
        var ev = Record(m);
        m.Start();
        m.ManualWW(3, "D0", [5]);
        Assert.Equal([(3, WriteDiscardReason.NotConnected)], Snap(ev));
    }

    [Fact]
    public void Eth_LinkDown_DiscardsWritesKeepsReads_InOrder()
    {
        var m = new McEthernet();
        var ev = Record(m);
        m.SetLinkUp();
        m.ManualWW(1, "D0", [1]);
        m.ManualBR(2, "M0", 1);
        m.ManualBW(3, "M0", [1]);
        Assert.Empty(Snap(ev));
        m.SetLinkDown(false);
        Assert.Equal([(1, WriteDiscardReason.Disconnected), (3, WriteDiscardReason.Disconnected)], Snap(ev));
        Assert.Equal(2, m.TakeNextWork()!.MessageID);
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void Eth_LinkDown_Stopping_ReasonStopped()
    {
        var m = new McEthernet();
        var ev = Record(m);
        m.SetLinkUp();
        m.ManualWW(1, "D0", [1]);
        m.ManualWR(2, "D0", 1);
        m.SetLinkDown(true);
        Assert.Equal([(1, WriteDiscardReason.Stopped)], Snap(ev));
        Assert.Equal(2, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void Eth_LinkDown_DiscardFalse_KeepsWrites()
    {
        var m = new McEthernet { DiscardWritesWhileDisconnected = false };
        var ev = Record(m);
        m.SetLinkUp();
        m.ManualWW(1, "D0", [1]);
        m.SetLinkDown(false);
        Assert.Empty(Snap(ev));
        Assert.Equal(1, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public async Task Eth_Stop_DiscardFalse_DiscardsQueuedWrites_NotSentOnRestart()
    {
        using var plc = new FakeMcPlc { XYOctal = false };
        plc.Start(McFrame.E3);
        using var m = new McEthernet { RemoteIP = "127.0.0.1", RemotePort = ClosedPort(), Timeout = 500, Interval = 1, DiscardWritesWhileDisconnected = false };
        var ev = Record(m);
        int written = 0;
        m.WriteReceived += (s, e) => Interlocked.Increment(ref written);
        m.Start();
        m.ManualWW(1, "D200", [11]);
        m.ManualWW(2, "D201", [22]);
        Assert.Empty(Snap(ev));
        m.Stop();
        Assert.Equal([(1, WriteDiscardReason.Stopped), (2, WriteDiscardReason.Stopped)], Snap(ev));

        m.RemotePort = plc.Port;
        m.Start();
        await TestUtil.Until(() => m.IsOpen);
        await Task.Delay(300);
        Assert.Equal(0, written);
        Assert.Equal(0L, plc.Get("D200"));
        Assert.Equal(0L, plc.Get("D201"));
        Assert.Equal(2, Snap(ev).Count);
    }

    [Fact]
    public async Task Eth_Stop_WithLinkUp_EachWriteStoppedOnce()
    {
        using var peer = new SilentPeer();
        using var m = new McEthernet { RemoteIP = "127.0.0.1", RemotePort = peer.Port, Timeout = 5000, Interval = 1 };
        var ev = Record(m);
        var timeouts = new ConcurrentQueue<McEthernet.AttemptFailedEventArgs>();
        m.TimeoutReceived += (s, e) => timeouts.Enqueue(e);
        m.Start();
        await TestUtil.Until(() => m.IsOpen);
        m.ManualWW(1, "D0", [1]);
        await peer.ReadFrame();          // 쓰기 1은 응답을 기다리는 중
        m.ManualWW(2, "D0", [2]);
        m.ManualWW(3, "D0", [3]);
        m.Stop();

        await TestUtil.Until(() => !timeouts.IsEmpty);
        await Task.Delay(100);
        var t = Assert.Single(timeouts);
        Assert.Equal((1, 1, true, true), (t.MessageID, t.Attempt, t.IsFinal, t.RequestSent));
        Assert.Equal([(2, WriteDiscardReason.Stopped), (3, WriteDiscardReason.Stopped)], Snap(ev));
    }

    [Fact]
    public async Task Eth_PeerClose_TimeoutThenDeviceClosedThenDisconnected()
    {
        using var peer = new SilentPeer();
        using var m = new McEthernet { RemoteIP = "127.0.0.1", RemotePort = peer.Port, Timeout = 5000, Interval = 1 };
        var log = new ConcurrentQueue<string>();
        m.TimeoutReceived += (s, e) => log.Enqueue($"Timeout({e.MessageID},{e.Attempt},{e.IsFinal},{e.RequestSent})");
        m.DeviceClosed += (s, e) => log.Enqueue("DeviceClosed");
        m.WriteDiscarded += (s, e) => log.Enqueue($"Discarded({e.MessageID},{e.Reason})");
        m.Start();
        await TestUtil.Until(() => m.IsOpen);
        m.ManualWW(1, "D0", [1]);
        await peer.ReadFrame();
        m.ManualWW(2, "D0", [2]);
        m.ManualWW(3, "D0", [3]);
        (await peer.Client).Close();

        await TestUtil.Until(() => log.Count >= 4);
        Assert.Equal(
            ["Timeout(1,1,True,True)", "DeviceClosed", "Discarded(2,Disconnected)", "Discarded(3,Disconnected)"],
            log.Take(4).ToArray());
    }

    [Fact]
    public async Task Eth_Retry_AttemptNumbers()
    {
        using var peer = new SilentPeer();
        using var m = new McEthernet { RemoteIP = "127.0.0.1", RemotePort = peer.Port, Timeout = 100, Interval = 1 };
        var timeouts = new ConcurrentQueue<McEthernet.AttemptFailedEventArgs>();
        m.TimeoutReceived += (s, e) => timeouts.Enqueue(e);
        m.Start();
        await TestUtil.Until(() => m.IsOpen);
        m.ManualWW(5, "D0", [1], repeatCount: 2);
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
    public async Task Eth_RestartFromHandler_OldLoopLinkDown_DoesNotCloseNewLink()
    {
        using var plc = new FakeMcPlc { XYOctal = false };
        plc.Start(McFrame.E3);
        using var m = new McEthernet { RemoteIP = "127.0.0.1", RemotePort = plc.Port, Timeout = 500, Interval = 1 };
        var ev = Record(m);
        int opened = 0, closeDone = 0;
        var secondOpen = new ManualResetEventSlim();
        var got = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.WriteReceived += (s, e) => got.TrySetResult(e.MessageID);
        m.DeviceOpened += (s, e) =>
        {
            if (Interlocked.Increment(ref opened) == 1) { m.Stop(); m.Start(); }   // 핸들러 안 재시작: 새 루프가 바로 뜬다
            else secondOpen.Set();
        };
        m.DeviceClosed += (s, e) =>
        {
            // 옛 루프의 SetLinkDown이 새 루프의 SetLinkUp 뒤에 오게 붙잡는다
            if (Volatile.Read(ref closeDone) == 0) secondOpen.Wait(3000);
            Interlocked.Increment(ref closeDone);
        };
        m.Start();
        await TestUtil.Until(() => opened >= 2 && closeDone >= 1);
        await Task.Delay(100);   // 옛 루프가 finally(SetLinkDown)를 마칠 시간
        m.ManualWW(9, "D202", [4321]);
        Assert.Empty(Snap(ev));
        Assert.Equal(9, await got.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(4321L, plc.Get("D202"));
    }

    [Fact]
    public void Eth_Xml_RoundTrip_DiscardWritesWhileDisconnected()
    {
        var m = new McEthernet();
        m.FromXml(XElement.Parse("""<McEthernet DiscardWritesWhileDisconnected="false" />"""));
        Assert.False(m.DiscardWritesWhileDisconnected);
        Assert.Equal("false", m.ToXml().Attribute("DiscardWritesWhileDisconnected")?.Value);

        m.FromXml(XElement.Parse("<McEthernet />"));
        Assert.True(m.DiscardWritesWhileDisconnected);
    }

    [Fact]
    public void Eth_XYNotation_DefaultStaysAuto()
    {
        Assert.Equal(McXYNotation.Auto, new McEthernet().XYNotation);
        Assert.Equal(McXYNotation.Auto, new McEthernetConfig().XYNotation);
    }
    #endregion
}
