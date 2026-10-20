using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Xml.Linq;
using Devinno.Communication.TextComm;
using Devinno.Communication.TextComm.RTU;
using Devinno.Communication.TextComm.TCP;

namespace Devinno.Communication.Tests.TextComm;

// Plan 13 쓰기 안전(D5: TextComm은 ManualSend 전부를 쓰기로 본다): 끊긴 동안 버림, Stop이 비움, 실패 이벤트의 시도 정보.
public class TextCommRtuWriteSafetyTests
{
    // 시리얼 포트는 테스트에서 열 수 없으므로 연결 상태는 internal seam(SetLinkUp/SetLinkDown)으로, 송수신은 Process로 시험한다.
    static List<(int Id, WriteDiscardReason Reason)> Record(TextCommRTUMaster m)
    {
        var list = new List<(int, WriteDiscardReason)>();
        m.WriteDiscarded += (s, e) => { lock (list) list.Add((e.MessageID, e.Reason)); };
        return list;
    }

    static List<(int, WriteDiscardReason)> Snap(List<(int Id, WriteDiscardReason Reason)> list)
    {
        lock (list) return [.. list];
    }

    [Fact]
    public void Manual_BeforeStart_DiscardedSync_NotQueued()
    {
        var m = new TextCommRTUMaster();
        var ev = Record(m);
        m.ManualSend(7, 1, 0x41, "SET");
        Assert.Equal([(7, WriteDiscardReason.NotConnected)], Snap(ev));
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void Auto_Unaffected_ByLinkDownAndStop()
    {
        var m = new TextCommRTUMaster();
        var ev = Record(m);
        m.AutoSend(1, 1, 0x41, "READ");
        Assert.Empty(Snap(ev));
        m.SetLinkUp();
        m.SetLinkDown(false);
        m.Stop();
        Assert.Empty(Snap(ev));
        Assert.True(m.ContainAutoID(1));
        Assert.Equal(1, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void LinkDown_DiscardsAllManual_KeepsAuto_InOrder()
    {
        var m = new TextCommRTUMaster();
        var ev = Record(m);
        m.AutoSend(1, 1, 0x41, "A");
        m.SetLinkUp();
        m.ManualSend(2, 1, 0x41, "X");
        m.ManualSend(3, 1, 0x41, "Y");
        Assert.Empty(Snap(ev));
        m.SetLinkDown(false);
        Assert.Equal([(2, WriteDiscardReason.Disconnected), (3, WriteDiscardReason.Disconnected)], Snap(ev));
        Assert.Equal(1, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void LinkDown_Stopping_ReasonStopped()
    {
        var m = new TextCommRTUMaster();
        var ev = Record(m);
        m.SetLinkUp();
        m.ManualSend(2, 1, 0x41, "X");
        m.SetLinkDown(true);
        Assert.Equal([(2, WriteDiscardReason.Stopped)], Snap(ev));
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void LinkDown_DiscardFalse_KeepsManual()
    {
        var m = new TextCommRTUMaster { DiscardWritesWhileDisconnected = false };
        var ev = Record(m);
        m.SetLinkUp();
        m.ManualSend(2, 1, 0x41, "X");
        m.SetLinkDown(false);
        Assert.Empty(Snap(ev));
        Assert.Equal(2, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void Stop_Unstarted_DiscardFalse_RaisesStopped()
    {
        var m = new TextCommRTUMaster { DiscardWritesWhileDisconnected = false };
        var ev = Record(m);
        m.ManualSend(1, 1, 0x41, "X");
        m.ManualSend(2, 1, 0x41, "Y");
        Assert.Empty(Snap(ev));
        m.Stop();
        Assert.Equal([(1, WriteDiscardReason.Stopped), (2, WriteDiscardReason.Stopped)], Snap(ev));
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void LinkDown_FromOtherThread_DoesNotOverrideNewerLink()
    {
        var m = new TextCommRTUMaster();
        var ev = Record(m);
        // 다른 스레드(옛 루프)가 올린 연결을 이 스레드(새 루프)가 이어받은 뒤, 옛 루프의 SetLinkDown이 오는 상황
        var old = new Thread(() => m.SetLinkUp());
        old.Start(); old.Join();
        m.SetLinkUp();
        var late = new Thread(() => m.SetLinkDown(false));
        late.Start(); late.Join();
        m.ManualSend(9, 1, 0x41, "X");
        Assert.Empty(Snap(ev));
        Assert.Equal(9, m.TakeNextWork()!.MessageID);
    }

    // send는 보낸 횟수를 세고, read가 응답 조각(없으면 null)을 돌려준다
    static int Run(TextCommRTUMaster m, Func<byte[]?> read,
        List<TextCommRTUMaster.AttemptFailedEventArgs> timeouts, List<TextCommRTUMaster.AttemptFailedEventArgs> sums)
    {
        m.TimeoutReceived += (s, e) => timeouts.Add(e);
        m.CheckSumErrorReceived += (s, e) => sums.Add(e);
        int sent = 0;
        m.Process(m.TakeNextWork()!, req => sent++, t => read());
        return sent;
    }

    [Fact]
    public void Process_Timeouts_AttemptAndIsFinal()
    {
        var m = new TextCommRTUMaster { DiscardWritesWhileDisconnected = false };
        m.ManualSend(5, 1, 0x41, "X", repeatCount: 2);
        var timeouts = new List<TextCommRTUMaster.AttemptFailedEventArgs>();
        var sums = new List<TextCommRTUMaster.AttemptFailedEventArgs>();
        int sent = Run(m, () => null, timeouts, sums);
        Assert.Equal(3, sent);
        Assert.Equal([1, 2, 3], timeouts.Select(e => e.Attempt));
        Assert.Equal([false, false, true], timeouts.Select(e => e.IsFinal));
        Assert.All(timeouts, e => Assert.True(e.RequestSent));
        Assert.All(timeouts, e => Assert.Equal((5, 1, 0x41), (e.MessageID, e.Slave, e.Command)));
        Assert.Empty(sums);
    }

    [Fact]
    public void Process_SumErrorThenTimeout_FinalIsTimeout()
    {
        var m = new TextCommRTUMaster { DiscardWritesWhileDisconnected = false };
        m.ManualSend(5, 1, 0x41, "X", repeatCount: 1);
        var timeouts = new List<TextCommRTUMaster.AttemptFailedEventArgs>();
        var sums = new List<TextCommRTUMaster.AttemptFailedEventArgs>();
        int n = 0;
        // 합계가 틀린 프레임(슬레이브·명령이 요청과 달라도 합계 오류) 뒤에는 응답 없음
        Run(m, () => n++ == 0 ? [0x02, 0x01, 0x41, 0x00, 0x03] : null, timeouts, sums);
        var s0 = Assert.Single(sums);
        Assert.Equal((1, false, true, 5), (s0.Attempt, s0.IsFinal, s0.RequestSent, s0.MessageID));
        var t0 = Assert.Single(timeouts);
        Assert.Equal((2, true), (t0.Attempt, t0.IsFinal));
    }

    [Fact]
    public void Process_Success_NoFailureEvents()
    {
        var m = new TextCommRTUMaster { DiscardWritesWhileDisconnected = false };
        m.ManualSend(5, 1, 0x41, "X");
        var timeouts = new List<TextCommRTUMaster.AttemptFailedEventArgs>();
        var sums = new List<TextCommRTUMaster.AttemptFailedEventArgs>();
        int ok = 0;
        m.MessageReceived += (s, e) => ok++;
        Run(m, () => TextCommPacket.Build(1, 0x41, "OK"u8), timeouts, sums);
        Assert.Equal(1, ok);
        Assert.Empty(timeouts);
        Assert.Empty(sums);
    }

    [Fact]
    public void Process_SendThrows_FinalNotSent_Rethrown()
    {
        var m = new TextCommRTUMaster { DiscardWritesWhileDisconnected = false };
        m.ManualSend(5, 1, 0x41, "X", repeatCount: 2);
        var timeouts = new List<TextCommRTUMaster.AttemptFailedEventArgs>();
        m.TimeoutReceived += (s, e) => timeouts.Add(e);
        Assert.Throws<IOException>(() => m.Process(m.TakeNextWork()!, req => throw new IOException(), t => null));
        var e = Assert.Single(timeouts);
        Assert.Equal((1, true, false), (e.Attempt, e.IsFinal, e.RequestSent));
    }

    [Fact]
    public void Process_ReadThrows_FinalSent_Rethrown()
    {
        var m = new TextCommRTUMaster { DiscardWritesWhileDisconnected = false };
        m.ManualSend(5, 1, 0x41, "X", repeatCount: 2);
        var timeouts = new List<TextCommRTUMaster.AttemptFailedEventArgs>();
        m.TimeoutReceived += (s, e) => timeouts.Add(e);
        Assert.Throws<IOException>(() => m.Process(m.TakeNextWork()!, req => { }, t => throw new IOException()));
        var e = Assert.Single(timeouts);
        Assert.Equal((1, true, true), (e.Attempt, e.IsFinal, e.RequestSent));
    }

    [Fact]
    public void Xml_RoundTrip_DiscardWritesWhileDisconnected()
    {
        var m = new TextCommRTUMaster();
        m.FromXml(XElement.Parse("""<TextCommRTUMaster DiscardWritesWhileDisconnected="false" />"""));
        Assert.False(m.DiscardWritesWhileDisconnected);
        Assert.Equal("false", m.ToXml().Attribute("DiscardWritesWhileDisconnected")?.Value);

        m.FromXml(XElement.Parse("<TextCommRTUMaster />"));
        Assert.True(m.DiscardWritesWhileDisconnected);
    }
}

public class TextCommTcpWriteSafetyTests : IDisposable
{
    readonly TextCommTCPSlave slave;
    readonly List<IDisposable> owned = [];
    int requests;

    public TextCommTcpWriteSafetyTests()
    {
        slave = new TextCommTCPSlave { LocalPort = 0 };
        // 명령 0x41: "OK " + 요청, 0x43: 응답 안 함(ResponseMessage = null)
        slave.MessageRequest += (s, e) =>
        {
            Interlocked.Increment(ref requests);
            if (e.Command == 0x41) e.ResponseMessage = "OK " + e.RequestMessage;
            else e.ResponseMessage = null;
        };
        slave.Start();
    }

    public void Dispose()
    {
        foreach (var d in owned) d.Dispose();
        slave.Dispose();
    }

    TextCommTCPMaster Master(int port, int timeout = 500)
    {
        var m = new TextCommTCPMaster { RemoteIP = "127.0.0.1", RemotePort = port, Timeout = timeout, Interval = 1 };
        owned.Add(m);
        return m;
    }

    static List<(int Id, WriteDiscardReason Reason)> Record(TextCommTCPMaster m)
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

        // 요청(STX...ETX)이 도착할 때까지 읽는다
        public async Task ReadFrame()
        {
            var stream = (await Client).GetStream();
            var buf = new byte[256];
            while (true)
            {
                int n = await stream.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                if (n == 0 || buf[n - 1] == 0x03) return;
            }
        }

        public void Dispose()
        {
            if (accepted.IsCompletedSuccessfully) accepted.Result.Dispose();
            listener.Stop();
        }
    }

    [Fact]
    public void Manual_BeforeStart_DiscardedSync_NotQueued()
    {
        var m = Master(slave.BoundPort);
        var ev = Record(m);
        m.ManualSend(7, 1, 0x41, "SET");
        Assert.Equal([(7, WriteDiscardReason.NotConnected)], Snap(ev));
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void Auto_Unaffected_ByLinkDownAndStop()
    {
        var m = Master(slave.BoundPort);
        var ev = Record(m);
        m.AutoSend(1, 1, 0x41, "READ");
        m.SetLinkUp();
        m.SetLinkDown(false);
        m.Stop();
        Assert.Empty(Snap(ev));
        Assert.True(m.ContainAutoID(1));
        Assert.Equal(1, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public async Task DiscardFalse_WriteBeforeStart_SentAfterConnect()
    {
        var m = Master(slave.BoundPort);
        m.DiscardWritesWhileDisconnected = false;
        var ev = Record(m);
        var got = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.MessageReceived += (s, e) => got.TrySetResult(e.Message);
        m.ManualSend(1, 1, 0x41, "SET 100");
        m.Start();
        Assert.Equal("OK SET 100", await got.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Empty(Snap(ev));
    }

    [Fact]
    public void StartThenWrite_BeforeConnect_DiscardedSync()
    {
        var m = Master(ClosedPort());
        var ev = Record(m);
        m.Start();
        m.ManualSend(3, 1, 0x41, "X");
        Assert.Equal([(3, WriteDiscardReason.NotConnected)], Snap(ev));
    }

    [Fact]
    public void LinkDown_DiscardsAllManual_KeepsAuto_InOrder()
    {
        var m = Master(slave.BoundPort);
        var ev = Record(m);
        m.AutoSend(9, 1, 0x41, "A");
        m.SetLinkUp();
        m.ManualSend(1, 1, 0x41, "X");
        m.ManualSend(2, 1, 0x41, "Y");
        Assert.Empty(Snap(ev));
        m.SetLinkDown(false);
        Assert.Equal([(1, WriteDiscardReason.Disconnected), (2, WriteDiscardReason.Disconnected)], Snap(ev));
        Assert.Equal(9, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void LinkDown_Stopping_ReasonStopped()
    {
        var m = Master(slave.BoundPort);
        var ev = Record(m);
        m.SetLinkUp();
        m.ManualSend(1, 1, 0x41, "X");
        m.SetLinkDown(true);
        Assert.Equal([(1, WriteDiscardReason.Stopped)], Snap(ev));
    }

    [Fact]
    public void LinkDown_DiscardFalse_KeepsManual()
    {
        var m = Master(slave.BoundPort);
        m.DiscardWritesWhileDisconnected = false;
        var ev = Record(m);
        m.SetLinkUp();
        m.ManualSend(1, 1, 0x41, "X");
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
        int received = 0;
        m.MessageReceived += (s, e) => Interlocked.Increment(ref received);
        m.Start();
        m.ManualSend(1, 1, 0x41, "A");
        m.ManualSend(2, 1, 0x41, "B");
        Assert.Empty(Snap(ev));
        m.Stop();
        Assert.Equal([(1, WriteDiscardReason.Stopped), (2, WriteDiscardReason.Stopped)], Snap(ev));

        m.RemotePort = slave.BoundPort;
        m.Start();
        await TestUtil.Until(() => m.IsOpen);
        await Task.Delay(300);
        Assert.Equal(0, received);
        Assert.Equal(0, Volatile.Read(ref requests));
        Assert.Equal(2, Snap(ev).Count);
    }

    [Fact]
    public async Task Stop_WithLinkUp_EachWriteStoppedOnce()
    {
        using var peer = new SilentPeer();
        var m = Master(peer.Port, timeout: 5000);
        var ev = Record(m);
        var timeouts = new ConcurrentQueue<TextCommTCPMaster.AttemptFailedEventArgs>();
        m.TimeoutReceived += (s, e) => timeouts.Enqueue(e);
        m.Start();
        await TestUtil.Until(() => m.IsOpen);
        m.ManualSend(1, 1, 0x41, "A");
        await peer.ReadFrame();          // 1은 응답을 기다리는 중
        m.ManualSend(2, 1, 0x41, "B");
        m.ManualSend(3, 1, 0x41, "C");
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
        m.ManualSend(1, 1, 0x41, "A");
        await peer.ReadFrame();
        m.ManualSend(2, 1, 0x41, "B");
        m.ManualSend(3, 1, 0x41, "C");
        (await peer.Client).Close();

        await TestUtil.Until(() => log.Count >= 4);
        Assert.Equal(
            ["Timeout(1,1,True,True)", "DeviceClosed", "Discarded(2,Disconnected)", "Discarded(3,Disconnected)"],
            log.Take(4).ToArray());
    }

    [Fact]
    public async Task Retry_AttemptNumbers()
    {
        var m = Master(slave.BoundPort, timeout: 100);
        var timeouts = new ConcurrentQueue<TextCommTCPMaster.AttemptFailedEventArgs>();
        m.TimeoutReceived += (s, e) => timeouts.Enqueue(e);
        m.Start();
        await TestUtil.Until(() => m.IsOpen);
        m.ManualSend(5, 1, 0x43, "X", repeatCount: 2);   // 0x43: slave가 응답하지 않음
        await TestUtil.Until(() => timeouts.Count >= 3);
        await Task.Delay(300);
        var list = timeouts.ToArray();
        Assert.Equal(3, list.Length);
        Assert.Equal([1, 2, 3], list.Select(e => e.Attempt));
        Assert.Equal([false, false, true], list.Select(e => e.IsFinal));
        Assert.All(list, e => Assert.True(e.RequestSent));
        Assert.All(list, e => Assert.Equal((5, 1, 0x43), (e.MessageID, e.Slave, e.Command)));
        Assert.Equal(3, Volatile.Read(ref requests));
    }

    [Fact]
    public async Task RestartFromHandler_OldLoopLinkDown_DoesNotCloseNewLink()
    {
        var m = Master(slave.BoundPort);
        var ev = Record(m);
        int opened = 0, closeDone = 0;
        var secondOpen = new ManualResetEventSlim();
        var got = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.MessageReceived += (s, e) => got.TrySetResult(e.Message);
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
        m.ManualSend(9, 1, 0x41, "AFTER");
        Assert.Empty(Snap(ev));
        Assert.Equal("OK AFTER", await got.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Xml_RoundTrip_DiscardWritesWhileDisconnected()
    {
        var m = new TextCommTCPMaster();
        m.FromXml(XElement.Parse("""<TextCommTCPMaster DiscardWritesWhileDisconnected="false" />"""));
        Assert.False(m.DiscardWritesWhileDisconnected);
        Assert.Equal("false", m.ToXml().Attribute("DiscardWritesWhileDisconnected")?.Value);

        m.FromXml(XElement.Parse("<TextCommTCPMaster />"));
        Assert.True(m.DiscardWritesWhileDisconnected);
    }
}
