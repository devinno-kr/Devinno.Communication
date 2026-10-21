using System.Collections.Concurrent;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Hsms;
using Devinno.Communication.Secs.Serial;
using Devinno.Communication.Tests.Fakes;
using Devinno.Communication.Tests.Secs.Hsms;
using Devinno.Communication.Tests.Secs.Serial;

namespace Devinno.Communication.Tests.Secs;

/// 공개 SendOrdered(SecsSendResult 콜백, 6G.7.6): 완료가 디스패처에서 와이어 순서로 정확히 한 번 오고,
/// 결과 형식(Primary = wire, Reply, Exception)이 Send와 같으며, 콜백 규칙(동기 Send 가능·예외 무시)이 지켜진다
public class SecsSendOrderedPublicTests
{
    static string Describe(SecsSendResult r)
        => r.IsSuccess ? (r.Reply == null ? "null" : $"S{r.Reply.Stream}F{r.Reply.Function}") : r.Exception!.GetType().Name;

    /// 우리 Passive Equipment에 가짜 상대가 접속해 Select까지
    static (SecsHsms Us, FakeHsmsPeer Peer) HsmsSelected(Action<SecsHsms>? setup = null)
    {
        var us = SecsHsmsTests.Passive(SecsRole.Equipment);
        setup?.Invoke(us);
        us.Start();
        var peer = FakeHsmsPeer.Connect(us.BoundPort);
        Assert.Equal(HsmsFrame.SelectOk, peer.Select());
        TestUtil.Wait(() => us.IsOpen);
        return (us, peer);
    }

    [Fact]
    public void Reply_BeforeNextPrimary_OnDispatcher()
    {
        var log = new ConcurrentQueue<(string What, bool OnDispatcher)>();
        SecsSendResult? got = null;
        SecsHsms? self = null;
        var (us, peer) = HsmsSelected(u => u.PrimaryReceived += (s, e) => log.Enqueue(($"primary S{e.Message.Stream}F{e.Message.Function}", self!.IsDispatcherThread)));
        self = us;
        using (us) using (peer)
        {
            var primary = new SecsMessage(1, 1, true);
            us.SendOrdered(primary, r => { got = r; log.Enqueue(("completed " + Describe(r), us.IsDispatcherThread)); });
            var req = peer.ReadData();
            // 응답과 다음 1차 메시지를 한 세그먼트로
            peer.SendData(new SecsMessage(1, 2, false).WithHeader(req.SystemBytes, 1), new SecsMessage(1, 17, true).WithHeader(100, 1));
            TestUtil.Wait(() => log.Count == 2);
            Assert.Equal([("completed S1F2", true), ("primary S1F17", true)], log.ToArray());
            Assert.True(got!.IsSuccess);
            Assert.Null(got.Exception);
            Assert.NotEqual(0u, got.Primary.SystemBytes);
            Assert.Equal(req.SystemBytes, got.Primary.SystemBytes);
            Assert.Equal((1, 1, true), (got.Primary.Stream, got.Primary.Function, got.Primary.ReplyExpected));
            Assert.Equal(0u, primary.SystemBytes);   // 넘긴 객체는 그대로
            Assert.Equal((1, 2), (got.Reply!.Stream, got.Reply.Function));
        }
    }

    [Fact]
    public void NoWbit_CompletesWithNullReply_Hsms()
    {
        var (us, peer) = HsmsSelected();
        using (us) using (peer)
        {
            var done = new ConcurrentQueue<SecsSendResult>();
            for (int i = 0; i < 3; i++) us.SendOrdered(new SecsMessage(6, 11, false, SecsItem.U4((uint)i)), done.Enqueue);
            for (int i = 0; i < 3; i++) peer.ReadData();
            TestUtil.Wait(() => done.Count == 3);
            var all = done.ToArray();
            Assert.All(all, r => { Assert.True(r.IsSuccess); Assert.Null(r.Reply); });
            Assert.Equal([0ul, 1ul, 2ul], all.Select(r => r.Primary.Item!.GetUInt64()));
        }
    }

    [Fact]
    public void NoWbit_CompletesWithNullReply_Serial()
    {
        var (line, peerEnd) = MemoryLine.Pair();
        using var us = SecsSerialTests.Make(SecsRole.Host, line);
        us.Start();
        TestUtil.Wait(() => us.IsOpen);
        var done = new ConcurrentQueue<SecsSendResult>();
        for (int i = 0; i < 3; i++) us.SendOrdered(new SecsMessage(6, 11, false, SecsItem.U4((uint)i)), done.Enqueue);
        for (int i = 0; i < 3; i++) peerEnd.ReceiveMessage();
        TestUtil.Wait(() => done.Count == 3);
        var all = done.ToArray();
        Assert.All(all, r => { Assert.True(r.IsSuccess); Assert.Null(r.Reply); Assert.NotEqual(0u, r.Primary.SystemBytes); });
        Assert.Equal([0ul, 1ul, 2ul], all.Select(r => r.Primary.Item!.GetUInt64()));
    }

    [Fact]
    public void Timeout_IsSecsTimeoutException()
    {
        var (us, peer) = HsmsSelected(u => u.T3 = 300);
        using (us) using (peer)
        {
            var done = new ConcurrentQueue<SecsSendResult>();
            us.SendOrdered(new SecsMessage(1, 1, true), done.Enqueue);
            peer.ReadData();   // 응답하지 않음
            TestUtil.Wait(() => done.Count == 1);
            var r = done.Single();
            Assert.False(r.IsSuccess);
            Assert.IsType<SecsTimeoutException>(r.Exception);
            Assert.Null(r.Reply);
        }
    }

    [Fact]
    public void Abort_IsSecsAbortException()
    {
        var (us, peer) = HsmsSelected();
        using (us) using (peer)
        {
            var done = new ConcurrentQueue<SecsSendResult>();
            us.SendOrdered(new SecsMessage(1, 1, true), done.Enqueue);
            var req = peer.ReadData();
            peer.SendData(new SecsMessage(1, 0, false).WithHeader(req.SystemBytes, 1));
            TestUtil.Wait(() => done.Count == 1);
            Assert.IsType<SecsAbortException>(done.Single().Exception);
        }
    }

    [Fact]
    public void Disconnect_IsSecsDisconnectedException_Once()
    {
        var (us, peer) = HsmsSelected();
        using (us) using (peer)
        {
            var done = new ConcurrentQueue<SecsSendResult>();
            us.SendOrdered(new SecsMessage(1, 1, true), done.Enqueue);
            peer.ReadData();
            peer.Close();
            TestUtil.Wait(() => done.Count == 1);
            Thread.Sleep(200);   // 늦은 두 번째 완료가 없는지
            Assert.IsType<SecsDisconnectedException>(Assert.Single(done).Exception);
        }
    }

    [Fact]
    public void NotConnected_ThrowsSync_NoCallback()
    {
        int calls = 0;
        using var us = SecsHsmsTests.Passive(SecsRole.Equipment);
        Assert.Throws<InvalidOperationException>(() => us.SendOrdered(new SecsMessage(1, 1, true), (SecsSendResult _) => calls++));
        Assert.Throws<ArgumentException>(() => us.SendOrdered(new SecsMessage(1, 2, false), (SecsSendResult _) => calls++));
        Assert.Throws<ArgumentNullException>(() => us.SendOrdered(null!, (SecsSendResult _) => calls++));
        Assert.Throws<ArgumentNullException>(() => us.SendOrdered(new SecsMessage(1, 1, true), (Action<SecsSendResult>)null!));

        var (line, _) = MemoryLine.Pair();
        using var serial = SecsSerialTests.Make(SecsRole.Host, line);
        Assert.Throws<InvalidOperationException>(() => serial.SendOrdered(new SecsMessage(1, 1, true), (SecsSendResult _) => calls++));
        Assert.Throws<ArgumentException>(() => serial.SendOrdered(new SecsMessage(1, 2, false), (SecsSendResult _) => calls++));
        Thread.Sleep(100);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void SyncSendInsideCallback_DoesNotDeadlock()
    {
        var (us, peer) = HsmsSelected();
        using (us) using (peer)
        {
            var inner = new ConcurrentQueue<string>();
            us.SendOrdered(new SecsMessage(1, 1, true), (SecsSendResult r) =>
            {
                try { var m = us.Send(new SecsMessage(1, 1, true)); inner.Enqueue($"S{m!.Stream}F{m.Function}"); }
                catch (Exception ex) { inner.Enqueue(ex.GetType().Name); }
            });
            var req = peer.ReadData();
            peer.SendData(new SecsMessage(1, 2, false).WithHeader(req.SystemBytes, 1));
            var req2 = peer.ReadData(10000);   // 콜백 안의 동기 Send
            peer.SendData(new SecsMessage(1, 2, false).WithHeader(req2.SystemBytes, 1));
            TestUtil.Wait(() => inner.Count == 1, 10000);
            Assert.Equal(["S1F2"], inner.ToArray());
        }
    }

    [Fact]
    public void CallbackThrows_DispatcherContinues()
    {
        var primaries = new ConcurrentQueue<string>();
        var (us, peer) = HsmsSelected(u => u.PrimaryReceived += (s, e) => primaries.Enqueue($"S{e.Message.Stream}F{e.Message.Function}"));
        using (us) using (peer)
        {
            int calls = 0;
            us.SendOrdered(new SecsMessage(1, 1, true), (SecsSendResult r) => { Interlocked.Increment(ref calls); throw new InvalidOperationException("콜백 예외"); });
            var req = peer.ReadData();
            peer.SendData(new SecsMessage(1, 2, false).WithHeader(req.SystemBytes, 1), new SecsMessage(1, 17, true).WithHeader(100, 1));
            TestUtil.Wait(() => primaries.Count == 1);
            Assert.Equal(["S1F17"], primaries.ToArray());
            Assert.Equal(1, calls);
        }
    }

    [Fact]
    public void StopDuringWait_CompletesOnce()
    {
        // 테스트 스레드에서 Stop: 끊김 실패는 Stop이 돌아오기 전에 정확히 한 번(디스패처가 아직 돌면 디스패처, 멈췄으면 Stop 스레드)
        // (Stop이 디스패처 번호를 먼저 지우면 콜백 안의 IsDispatcherThread는 false일 수 있어, 디스패처 스레드는 DeviceOpened에서 잡아 둔다)
        Thread? dispatcher = null;
        var (us, peer) = HsmsSelected(u => u.DeviceOpened += (s, e) => dispatcher = Thread.CurrentThread);
        using (us) using (peer)
        {
            TestUtil.Wait(() => dispatcher != null);
            var done = new ConcurrentQueue<(SecsSendResult R, Thread T)>();
            us.SendOrdered(new SecsMessage(1, 1, true), (SecsSendResult r) => done.Enqueue((r, Thread.CurrentThread)));
            peer.ReadData();
            us.Stop();
            Assert.Single(done);   // Stop이 돌아오기 전에 끝남
            Thread.Sleep(200);
            var (r, t) = Assert.Single(done);
            Assert.IsType<SecsDisconnectedException>(r.Exception);
            Assert.True(t == Thread.CurrentThread || t == dispatcher);
        }
    }

    [Fact]
    public void StopDuringWait_DispatcherBusy_CompletesOnStopThread()
    {
        // 디스패처 안(핸들러)에서 Stop: 완료는 대기열에 남고 Stop이 그 스레드에서 마저 실행한다 → 정확히 Stop을 부른 스레드에서 한 번
        SecsHsms? self = null;
        var done = new ConcurrentQueue<(SecsSendResult R, Thread T)>();
        Thread? stopThread = null;
        var stopped = new ManualResetEventSlim();
        var (us, peer) = HsmsSelected(u => u.PrimaryReceived += (s, e) =>
        {
            if (e.Message.Function != 17) return;
            stopThread = Thread.CurrentThread;
            self!.Stop();
            stopped.Set();
        });
        self = us;
        using (us) using (peer)
        {
            us.SendOrdered(new SecsMessage(1, 1, true), r => done.Enqueue((r, Thread.CurrentThread)));
            peer.ReadData();
            peer.SendData(new SecsMessage(1, 17, true).WithHeader(100, 1));
            Assert.True(stopped.Wait(10000));
            var (r, t) = Assert.Single(done);
            Assert.IsType<SecsDisconnectedException>(r.Exception);
            Assert.Same(stopThread, t);
        }
    }
}
