using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Xml.Linq;
using Devinno.Communication.LS.CNet;
using Devinno.Communication.LS.Data;
using Devinno.Communication.LS.FEnet;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.LS;

// Plan 13 쓰기 안전(LS): 끊긴 동안 쓰기 버림, Stop이 쓰기 비움, 실패 이벤트의 시도 정보.
// FEnet은 TCP 루프를 실제로 돌려 시험하고, CNet은 시리얼 포트를 열 수 없으므로 internal seam(SetLinkUp/SetLinkDown/Process)으로 시험한다.
public class LsWriteSafetyTests : IDisposable
{
    readonly List<IDisposable> owned = [];

    public void Dispose()
    {
        foreach (var d in owned) d.Dispose();
    }

    static List<(int Id, WriteDiscardReason Reason)> RecordF(LsFEnet m)
    {
        var list = new List<(int, WriteDiscardReason)>();
        m.WriteDiscarded += (s, e) => { lock (list) list.Add((e.MessageID, e.Reason)); };
        return list;
    }

    static List<(int Id, WriteDiscardReason Reason)> RecordC(LsCNet m)
    {
        var list = new List<(int, WriteDiscardReason)>();
        m.WriteDiscarded += (s, e) => { lock (list) list.Add((e.MessageID, e.Reason)); };
        return list;
    }

    static List<(int, WriteDiscardReason)> Snap(List<(int Id, WriteDiscardReason Reason)> list)
    {
        lock (list) return [.. list];
    }

    static LsValue[] V(int v = 1) => [new LsValue("%MW0", v)];

    LsFEnet Fenet(int port, int timeout = 500)
    {
        var m = new LsFEnet { RemoteIP = "127.0.0.1", RemotePort = port, Timeout = timeout, Interval = 1 };
        owned.Add(m);
        return m;
    }

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

        // 요청 한 프레임(헤더 20바이트 + 본문)을 다 받을 때까지 읽는다
        public async Task ReadFrame()
        {
            var stream = (await Client).GetStream();
            var head = new byte[20];
            await stream.ReadExactlyAsync(head).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            var body = new byte[head[16] | head[17] << 8];
            await stream.ReadExactlyAsync(body).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }

        public void Dispose()
        {
            if (accepted.IsCompletedSuccessfully) accepted.Result.Dispose();
            listener.Stop();
        }
    }

    // ---------------- LsFEnet ----------------

    [Fact]
    public void FEnet_Write_BeforeStart_DiscardedSync_NotSent()
    {
        var m = Fenet(1);
        var ev = RecordF(m);
        m.ManualWSS(7, V());
        m.ManualWSB(8, "%MW0", [1, 2]);
        Assert.Equal([(7, WriteDiscardReason.NotConnected), (8, WriteDiscardReason.NotConnected)], Snap(ev));
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void FEnet_Read_BeforeStart_Kept()
    {
        var m = Fenet(1);
        var ev = RecordF(m);
        m.ManualRSS(8, ["%MW0"]);
        Assert.Empty(Snap(ev));
        Assert.Equal(8, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void FEnet_LinkDown_DiscardsWritesKeepsReads_InOrder()
    {
        var m = Fenet(1);
        var ev = RecordF(m);
        m.SetLinkUp();
        m.ManualWSS(1, V());
        m.ManualRSS(2, ["%MW0"]);
        m.ManualWSB(3, "%MW0", [1]);
        Assert.Empty(Snap(ev));
        m.SetLinkDown(false);
        Assert.Equal([(1, WriteDiscardReason.Disconnected), (3, WriteDiscardReason.Disconnected)], Snap(ev));
        Assert.Equal(2, m.TakeNextWork()!.MessageID);
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void FEnet_LinkDown_Stopping_ReasonStopped()
    {
        var m = Fenet(1);
        var ev = RecordF(m);
        m.SetLinkUp();
        m.ManualWSS(1, V());
        m.ManualRSS(2, ["%MW0"]);
        m.ManualWSS(3, V());
        m.SetLinkDown(true);
        Assert.Equal([(1, WriteDiscardReason.Stopped), (3, WriteDiscardReason.Stopped)], Snap(ev));
        Assert.Equal(2, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void FEnet_LinkDown_DiscardFalse_KeepsWrites()
    {
        var m = Fenet(1);
        m.DiscardWritesWhileDisconnected = false;
        var ev = RecordF(m);
        m.SetLinkUp();
        m.ManualWSS(1, V());
        m.SetLinkDown(false);
        Assert.Empty(Snap(ev));
        Assert.Equal(1, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void FEnet_Stop_Unstarted_DiscardFalse_RaisesStopped()
    {
        var m = Fenet(1);
        m.DiscardWritesWhileDisconnected = false;
        var ev = RecordF(m);
        m.ManualWSS(1, V());
        m.ManualRSS(2, ["%MW0"]);
        m.ManualWSS(3, V());
        Assert.Empty(Snap(ev));
        m.Stop();
        Assert.Equal([(1, WriteDiscardReason.Stopped), (3, WriteDiscardReason.Stopped)], Snap(ev));
        Assert.Equal(2, m.TakeNextWork()!.MessageID);
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void FEnet_NamedWrite_BeforeStart_Discarded()
    {
        var m = Fenet(1);
        m.FromXml(XElement.Parse("""<LsFEnet><Write Name="A" Id="30" Device="%MW10" /></LsFEnet>"""));
        var ev = RecordF(m);
        m.NamedWrite("A", 5);
        Assert.Equal([(30, WriteDiscardReason.NotConnected)], Snap(ev));
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public async Task FEnet_DiscardFalse_WriteBeforeStart_SentAfterConnect()
    {
        using var plc = new FakeXgtPlc();
        plc.Start();
        var m = Fenet(plc.Port);
        m.DiscardWritesWhileDisconnected = false;
        var ev = RecordF(m);
        var got = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.WriteReceived += (s, e) => got.TrySetResult(e.MessageID);
        m.ManualWSS(1, V(1234));
        m.Start();
        Assert.Equal(1, await got.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1234, plc.Get("%MW0"));
        Assert.Empty(Snap(ev));
    }

    [Fact]
    public void FEnet_StartThenWrite_BeforeConnect_DiscardedSync()
    {
        var m = Fenet(ClosedPort());
        var ev = RecordF(m);
        m.Start();
        m.ManualWSS(3, V(5));
        Assert.Equal([(3, WriteDiscardReason.NotConnected)], Snap(ev));
    }

    [Fact]
    public async Task FEnet_Stop_DiscardFalse_DiscardsQueuedWrites_NotSentOnRestart()
    {
        using var plc = new FakeXgtPlc();
        plc.Start();
        var m = Fenet(ClosedPort());
        m.DiscardWritesWhileDisconnected = false;
        var ev = RecordF(m);
        int written = 0;
        m.WriteReceived += (s, e) => Interlocked.Increment(ref written);
        m.Start();
        m.ManualWSS(1, V(11));
        m.ManualWSS(2, [new LsValue("%MW1", 22)]);
        Assert.Empty(Snap(ev));
        m.Stop();
        Assert.Equal([(1, WriteDiscardReason.Stopped), (2, WriteDiscardReason.Stopped)], Snap(ev));

        m.RemotePort = plc.Port;
        m.Start();
        await TestUtil.Until(() => m.IsOpen);
        await Task.Delay(300);
        Assert.Equal(0, written);
        Assert.Equal(0, plc.Get("%MW0"));
        Assert.Equal(0, plc.Get("%MW1"));
        Assert.Equal(2, Snap(ev).Count);
    }

    [Fact]
    public async Task FEnet_Stop_WithLinkUp_EachWriteStoppedOnce()
    {
        using var peer = new SilentPeer();
        var m = Fenet(peer.Port, timeout: 5000);
        var ev = RecordF(m);
        var timeouts = new ConcurrentQueue<LsFEnet.AttemptFailedEventArgs>();
        m.TimeoutReceived += (s, e) => timeouts.Enqueue(e);
        m.Start();
        await TestUtil.Until(() => m.IsOpen);
        m.ManualWSS(1, V(1));
        await peer.ReadFrame();          // 쓰기 1은 응답을 기다리는 중
        m.ManualWSS(2, V(2));
        m.ManualWSS(3, V(3));
        m.Stop();

        await TestUtil.Until(() => !timeouts.IsEmpty);
        await Task.Delay(100);
        var t = Assert.Single(timeouts);
        Assert.Equal((1, 1, true, true), (t.MessageID, t.Attempt, t.IsFinal, t.RequestSent));
        Assert.Equal([(2, WriteDiscardReason.Stopped), (3, WriteDiscardReason.Stopped)], Snap(ev));
    }

    [Fact]
    public async Task FEnet_PeerClose_TimeoutThenDeviceClosedThenDisconnected()
    {
        using var peer = new SilentPeer();
        var m = Fenet(peer.Port, timeout: 5000);
        var log = new ConcurrentQueue<string>();
        m.TimeoutReceived += (s, e) => log.Enqueue($"Timeout({e.MessageID},{e.Attempt},{e.IsFinal},{e.RequestSent})");
        m.DeviceClosed += (s, e) => log.Enqueue("DeviceClosed");
        m.WriteDiscarded += (s, e) => log.Enqueue($"Discarded({e.MessageID},{e.Reason})");
        m.Start();
        await TestUtil.Until(() => m.IsOpen);
        m.ManualWSS(1, V(1));
        await peer.ReadFrame();
        m.ManualWSS(2, V(2));
        m.ManualWSS(3, V(3));
        (await peer.Client).Close();

        await TestUtil.Until(() => log.Count >= 4);
        Assert.Equal(
            ["Timeout(1,1,True,True)", "DeviceClosed", "Discarded(2,Disconnected)", "Discarded(3,Disconnected)"],
            log.Take(4).ToArray());
    }

    [Fact]
    public async Task FEnet_Retry_AttemptNumbers()
    {
        using var peer = new SilentPeer();
        var m = Fenet(peer.Port, timeout: 100);
        var timeouts = new ConcurrentQueue<LsFEnet.AttemptFailedEventArgs>();
        m.TimeoutReceived += (s, e) => timeouts.Enqueue(e);
        m.Start();
        await TestUtil.Until(() => m.IsOpen);
        m.ManualWSS(5, V(1), repeatCount: 2);
        await TestUtil.Until(() => timeouts.Count >= 3);
        await Task.Delay(300);
        var list = timeouts.ToArray();
        Assert.Equal(3, list.Length);
        Assert.Equal([1, 2, 3], list.Select(e => e.Attempt));
        Assert.Equal([false, false, true], list.Select(e => e.IsFinal));
        Assert.All(list, e => Assert.True(e.RequestSent));
        Assert.All(list, e => Assert.Equal(5, e.MessageID));
        Assert.All(list, e => Assert.Equal(LsCommand.WSS, e.Command));
    }

    [Fact]
    public async Task FEnet_RestartFromHandler_OldLoopLinkDown_DoesNotCloseNewLink()
    {
        using var plc = new FakeXgtPlc();
        plc.Start();
        var m = Fenet(plc.Port);
        var ev = RecordF(m);
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
        m.ManualWSS(9, [new LsValue("%MW2", 4321)]);
        Assert.Empty(Snap(ev));
        Assert.Equal(9, await got.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(4321, plc.Get("%MW2"));
    }

    [Fact]
    public void FEnet_Xml_RoundTrip_DiscardWritesWhileDisconnected()
    {
        var m = new LsFEnet();
        m.FromXml(XElement.Parse("""<LsFEnet DiscardWritesWhileDisconnected="false" />"""));
        Assert.False(m.DiscardWritesWhileDisconnected);
        Assert.Equal("false", m.ToXml().Attribute("DiscardWritesWhileDisconnected")?.Value);

        m.FromXml(XElement.Parse("<LsFEnet />"));
        Assert.True(m.DiscardWritesWhileDisconnected);
    }

    // ---------------- LsCNet ----------------

    [Fact]
    public void CNet_Write_BeforeStart_DiscardedSync_NotSent()
    {
        var m = new LsCNet();
        var ev = RecordC(m);
        m.ManualWSS(7, 1, V());
        m.ManualWSB(8, 1, "%MW0", [1, 2]);
        Assert.Equal([(7, WriteDiscardReason.NotConnected), (8, WriteDiscardReason.NotConnected)], Snap(ev));
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void CNet_NamedWrite_BeforeStart_Discarded()
    {
        var m = new LsCNet();
        m.FromXml(XElement.Parse("""<LsCNet><Write Name="A" Id="30" Slave="1" Device="%MW10" /></LsCNet>"""));
        var ev = RecordC(m);
        m.NamedWrite("A", 5);
        Assert.Equal([(30, WriteDiscardReason.NotConnected)], Snap(ev));
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void CNet_Read_BeforeStart_Kept()
    {
        var m = new LsCNet();
        var ev = RecordC(m);
        m.ManualRSS(8, 1, ["%MW0"]);
        Assert.Empty(Snap(ev));
        Assert.Equal(8, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void CNet_LinkDown_DiscardsWritesKeepsReads_InOrder()
    {
        var m = new LsCNet();
        var ev = RecordC(m);
        m.SetLinkUp();
        m.ManualWSS(1, 1, V());
        m.ManualRSS(2, 1, ["%MW0"]);
        m.ManualWSB(3, 1, "%MW0", [1]);
        Assert.Empty(Snap(ev));
        m.SetLinkDown(false);
        Assert.Equal([(1, WriteDiscardReason.Disconnected), (3, WriteDiscardReason.Disconnected)], Snap(ev));
        Assert.Equal(2, m.TakeNextWork()!.MessageID);
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void CNet_LinkDown_Stopping_ReasonStopped()
    {
        var m = new LsCNet();
        var ev = RecordC(m);
        m.SetLinkUp();
        m.ManualWSS(1, 1, V());
        m.ManualRSS(2, 1, ["%MW0"]);
        m.ManualWSS(3, 1, V());
        m.SetLinkDown(true);
        Assert.Equal([(1, WriteDiscardReason.Stopped), (3, WriteDiscardReason.Stopped)], Snap(ev));
        Assert.Equal(2, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void CNet_LinkDown_DiscardFalse_KeepsWrites()
    {
        var m = new LsCNet { DiscardWritesWhileDisconnected = false };
        var ev = RecordC(m);
        m.SetLinkUp();
        m.ManualWSS(1, 1, V());
        m.SetLinkDown(false);
        Assert.Empty(Snap(ev));
        Assert.Equal(1, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void CNet_Stop_Unstarted_DiscardFalse_RaisesStopped()
    {
        var m = new LsCNet { DiscardWritesWhileDisconnected = false };
        var ev = RecordC(m);
        m.ManualWSS(1, 1, V());
        m.ManualRSS(2, 1, ["%MW0"]);
        m.ManualWSS(3, 1, V());
        Assert.Empty(Snap(ev));
        m.Stop();
        Assert.Equal([(1, WriteDiscardReason.Stopped), (3, WriteDiscardReason.Stopped)], Snap(ev));
        Assert.Equal(2, m.TakeNextWork()!.MessageID);
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void CNet_LinkDown_FromOtherThread_DoesNotOverrideNewerLink()
    {
        var m = new LsCNet();
        var ev = RecordC(m);
        // 다른 스레드(옛 루프)가 올린 연결을 이 스레드(새 루프)가 이어받은 뒤, 옛 루프의 SetLinkDown이 오는 상황
        var old = new Thread(() => m.SetLinkUp());
        old.Start(); old.Join();
        m.SetLinkUp();
        var late = new Thread(() => m.SetLinkDown(false));
        late.Start(); late.Join();
        m.ManualWSS(9, 1, V());
        Assert.Empty(Snap(ev));
        Assert.Equal(9, m.TakeNextWork()!.MessageID);
    }

    // send는 보낸 횟수를 세고, receive가 응답 프레임(없으면 null)을 돌려준다
    static int Run(LsCNet m, LsCNet.Work w, Func<byte[]?> receive,
        List<LsCNet.AttemptFailedEventArgs> timeouts, List<LsCNet.AttemptFailedEventArgs> bccs)
    {
        m.TimeoutReceived += (s, e) => timeouts.Add(e);
        m.BCCErrorReceived += (s, e) => bccs.Add(e);
        int sent = 0;
        m.Process(w, req => sent++, t => receive());
        return sent;
    }

    static byte[] AckFor(LsCNet.Work w)
    {
        using var plc = new FakeXgtPlc { Station = w.Slave };
        return plc.HandleCnet(w.Request)!;
    }

    [Fact]
    public void CNet_Process_Timeouts_AttemptAndIsFinal()
    {
        var m = new LsCNet { DiscardWritesWhileDisconnected = false };
        m.ManualWSS(5, 1, V(), repeatCount: 2);
        var w = m.TakeNextWork()!;
        var timeouts = new List<LsCNet.AttemptFailedEventArgs>();
        var bccs = new List<LsCNet.AttemptFailedEventArgs>();
        int sent = Run(m, w, () => null, timeouts, bccs);
        Assert.Equal(3, sent);
        Assert.Equal([1, 2, 3], timeouts.Select(e => e.Attempt));
        Assert.Equal([false, false, true], timeouts.Select(e => e.IsFinal));
        Assert.All(timeouts, e => Assert.True(e.RequestSent));
        Assert.All(timeouts, e => Assert.Equal((5, 1, LsCommand.WSS), (e.MessageID, e.Slave, e.Command)));
        Assert.Empty(bccs);
    }

    [Fact]
    public void CNet_Process_LastAttemptBccError_IsFinalBccEvent()
    {
        var m = new LsCNet { DiscardWritesWhileDisconnected = false };
        m.ManualWSS(5, 1, V(), repeatCount: 1);
        var w = m.TakeNextWork()!;
        var timeouts = new List<LsCNet.AttemptFailedEventArgs>();
        var bccs = new List<LsCNet.AttemptFailedEventArgs>();
        int n = 0;
        var bad = AckFor(w);
        bad[^1] = bad[^1] == (byte)'0' ? (byte)'1' : (byte)'0';
        Run(m, w, () => n++ == 0 ? null : bad, timeouts, bccs);
        var t0 = Assert.Single(timeouts);
        Assert.Equal((1, false), (t0.Attempt, t0.IsFinal));
        var c = Assert.Single(bccs);
        Assert.Equal((2, true, true, 5), (c.Attempt, c.IsFinal, c.RequestSent, c.MessageID));
    }

    [Fact]
    public void CNet_Process_Success_NoFailureEvents()
    {
        var m = new LsCNet { DiscardWritesWhileDisconnected = false };
        m.ManualWSS(5, 1, V());
        var w = m.TakeNextWork()!;
        var timeouts = new List<LsCNet.AttemptFailedEventArgs>();
        var bccs = new List<LsCNet.AttemptFailedEventArgs>();
        int ok = 0;
        m.WriteReceived += (s, e) => ok++;
        Run(m, w, () => AckFor(w), timeouts, bccs);
        Assert.Equal(1, ok);
        Assert.Empty(timeouts);
        Assert.Empty(bccs);
    }

    [Fact]
    public void CNet_Process_Nak_NotRetried_NoFailureEvents()
    {
        var m = new LsCNet { DiscardWritesWhileDisconnected = false };
        m.ManualWSS(5, 1, V(), repeatCount: 2);
        var w = m.TakeNextWork()!;
        var timeouts = new List<LsCNet.AttemptFailedEventArgs>();
        var bccs = new List<LsCNet.AttemptFailedEventArgs>();
        int naks = 0;
        m.NAKReceived += (s, e) => naks++;
        using var plc = new FakeXgtPlc { Station = 1, ForceNak = 0x1132 };
        int sent = Run(m, w, () => plc.HandleCnet(w.Request), timeouts, bccs);
        Assert.Equal((1, 1), (sent, naks));
        Assert.Empty(timeouts);
        Assert.Empty(bccs);
    }

    [Fact]
    public void CNet_Process_SendThrows_FinalNotSent_Rethrown()
    {
        var m = new LsCNet { DiscardWritesWhileDisconnected = false };
        m.ManualWSS(5, 1, V(), repeatCount: 2);
        var w = m.TakeNextWork()!;
        var timeouts = new List<LsCNet.AttemptFailedEventArgs>();
        m.TimeoutReceived += (s, e) => timeouts.Add(e);
        Assert.Throws<IOException>(() => m.Process(w, req => throw new IOException(), t => null));
        var e = Assert.Single(timeouts);
        Assert.Equal((1, true, false), (e.Attempt, e.IsFinal, e.RequestSent));
    }

    [Fact]
    public void CNet_Xml_RoundTrip_DiscardWritesWhileDisconnected()
    {
        var m = new LsCNet();
        m.FromXml(XElement.Parse("""<LsCNet DiscardWritesWhileDisconnected="false" />"""));
        Assert.False(m.DiscardWritesWhileDisconnected);
        Assert.Equal("false", m.ToXml().Attribute("DiscardWritesWhileDisconnected")?.Value);

        m.FromXml(XElement.Parse("<LsCNet />"));
        Assert.True(m.DiscardWritesWhileDisconnected);
    }
}
