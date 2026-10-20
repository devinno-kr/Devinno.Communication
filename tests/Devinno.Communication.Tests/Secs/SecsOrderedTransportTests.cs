using System.Collections.Concurrent;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Hsms;
using Devinno.Communication.Secs.Serial;
using Devinno.Communication.Tests.Fakes;
using Devinno.Communication.Tests.Secs.Hsms;
using Devinno.Communication.Tests.Secs.Serial;

namespace Devinno.Communication.Tests.Secs;

/// 순서 있는 완료(SendOrdered)를 실제 전송(SecsHsms·SecsSerial)에 연결했을 때: 완료가 와이어 순서로 디스패처에 오고,
/// W비트 없는 송신·SECS-I 송신 실패(재시도 초과·회선 닫힘·링크 없음)도 정확히 한 번 끝난다 (6G C1·C2)
public class SecsOrderedTransportTests
{
    static string Describe(Task<SecsMessage?> t)
        => t.IsCompletedSuccessfully ? (t.Result == null ? "null" : $"S{t.Result.Stream}F{t.Result.Function}") : t.Exception!.InnerException!.GetType().Name;

    #region HSMS
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
    public void Hsms_ReplyAndNextPrimaryInOneSegment_CompletionFirst()
    {
        var log = new ConcurrentQueue<(string What, bool OnDispatcher)>();
        SecsHsms? self = null;
        var (us, peer) = HsmsSelected(u => u.PrimaryReceived += (s, e) => log.Enqueue(("primary", self!.IsDispatcherThread)));
        self = us;
        using (us) using (peer)
        {
            us.SendOrdered(new SecsMessage(1, 1, true), t => log.Enqueue(("completed " + Describe(t), us.IsDispatcherThread)));
            var req = peer.ReadData();
            // 응답과 다음 1차 메시지를 한 세그먼트로: 수신 스레드는 둘을 연달아 처리한다
            peer.SendData(new SecsMessage(1, 2, false).WithHeader(req.SystemBytes, 1), new SecsMessage(1, 3, true).WithHeader(100, 1));
            TestUtil.Wait(() => log.Count == 2);
            Assert.Equal([("completed S1F2", true), ("primary", true)], log.ToArray());
        }
    }

    [Fact]
    public void Hsms_NoWBit_CompletesNullAfterWrite()
    {
        var (us, peer) = HsmsSelected();
        using (us) using (peer)
        {
            var done = new ConcurrentQueue<string>();
            for (int i = 0; i < 3; i++) us.SendOrdered(new SecsMessage(6, 11, false, SecsItem.U4((uint)i)), t => done.Enqueue(Describe(t)));
            for (int i = 0; i < 3; i++)
            {
                var m = peer.ReadData();
                Assert.Equal((6, 11, false), (m.Stream, m.Function, m.ReplyExpected));
            }
            TestUtil.Wait(() => done.Count == 3);
            Assert.Equal(["null", "null", "null"], done.ToArray());
        }
    }

    [Fact]
    public void Hsms_LinkDrop_CompletionBeforeDeviceClosed()
    {
        var log = new ConcurrentQueue<string>();
        var (us, peer) = HsmsSelected(u => u.DeviceClosed += (s, e) => log.Enqueue("DeviceClosed"));
        using (us) using (peer)
        {
            us.SendOrdered(new SecsMessage(1, 1, true), t => log.Enqueue("completed " + Describe(t)));
            peer.ReadData();
            peer.Close();
            TestUtil.Wait(() => log.Count == 2);
            Assert.Equal(["completed SecsDisconnectedException", "DeviceClosed"], log.ToArray());
        }
    }
    #endregion

    #region SECS-I
    /// 우리 SecsSerial(Host) ↔ 테스트가 직접 움직이는 상대 끝(Peer). Line = 우리 쪽 끝(닫아서 회선 끊김을 흉내)
    sealed class SerialCtx : IDisposable
    {
        public readonly SecsSerial Us;
        public readonly MemoryLine Peer;
        public readonly MemoryLine Line;
        public readonly ConcurrentQueue<string> Log = new();

        public SerialCtx(Action<SecsSerial>? setup = null)
        {
            (Line, Peer) = MemoryLine.Pair();
            Us = SecsSerialTests.Make(SecsRole.Host, Line);
            Us.DeviceClosed += (s, e) => Log.Enqueue("DeviceClosed");
            setup?.Invoke(Us);
            Us.Start();
            TestUtil.Wait(() => Us.IsOpen);
        }

        public void Dispose() => Us.Dispose();
    }

    static int AcksWritten(MemoryLine end) => end.Written.Count(w => w is [SecsIBlock.ACK]);

    [Fact]
    public void Serial_NoWBit_CompletesAtLastAck()
    {
        using var c = new SerialCtx();
        int acksAtCompletion = -1;
        var done = new ConcurrentQueue<string>();
        var msg = new SecsMessage(6, 11, false, SecsItem.B(new byte[600]));   // 블록 3개
        int blocks = SecsIBlock.Split(msg.WithHeader(1, 1), false).Count;
        Assert.True(blocks >= 3);
        c.Us.SendOrdered(msg, t => { acksAtCompletion = AcksWritten(c.Peer); done.Enqueue(Describe(t)); });
        var (_, got) = c.Peer.ReceiveMessage();
        Assert.Equal((6, 11), (got.Stream, got.Function));
        TestUtil.Wait(() => done.Count == 1);
        Assert.Equal(["null"], done.ToArray());
        Assert.Equal(blocks, acksAtCompletion);
    }

    [Fact]
    public void Serial_RetryExceeded_CompletesIOException_LineKept()
    {
        using var c = new SerialCtx(u => u.RetryLimit = 1);
        var done = new ConcurrentQueue<string>();
        c.Peer.Tap = bytes => bytes is [SecsIBlock.ACK] ? [SecsIBlock.NAK] : bytes;   // 상대가 ACK 대신 NAK
        c.Us.SendOrdered(new SecsMessage(1, 1, true), t => done.Enqueue(Describe(t)));
        for (int i = 0; i < 2; i++) c.Peer.ReceiveBlock();   // 1 + RetryLimit번
        TestUtil.Wait(() => !done.IsEmpty);
        Assert.Equal(-1, c.Peer.ReadByte(300));               // 더 보내지 않음
        Assert.Equal(["IOException"], done.ToArray());
        Assert.True(c.Us.IsOpen);

        c.Peer.Tap = null;
        c.Us.SendOrdered(new SecsMessage(1, 1, true), t => done.Enqueue(Describe(t)));   // 회선은 그대로 → 다음 메시지는 된다
        var (h, _) = c.Peer.ReceiveMessage();
        c.Peer.SendMessage(new SecsMessage(1, 2, false).WithHeader(h.SystemBytes, 1), rbit: true);
        TestUtil.Wait(() => done.Count == 2);
        Assert.Equal(["IOException", "S1F2"], done.ToArray());
    }

    [Fact]
    public void Serial_LineClosed_QueuedAndInFlightCompleteDisconnected_BeforeDeviceClosed()
    {
        using var c = new SerialCtx();   // 상대가 EOT를 주지 않음: 닫힘은 T2(1000ms) 안에 알아채고, 재시도 초과(4초)보다 먼저
        for (int i = 0; i < 3; i++) c.Us.SendOrdered(new SecsMessage(6, 11, false, SecsItem.U4((uint)i)), t => c.Log.Enqueue("completed " + Describe(t)));
        c.Peer.Expect(SecsIBlock.ENQ);   // 첫 메시지는 보내는 중(EOT 대기), 나머지 둘은 대기열
        c.Line.Close();
        TestUtil.Wait(() => c.Log.Count == 4, 5000);
        Assert.Equal(["completed SecsDisconnectedException", "completed SecsDisconnectedException", "completed SecsDisconnectedException", "DeviceClosed"], c.Log.ToArray());
    }

    [Fact]
    public void Serial_SendWhileLinkDown_CompletesDisconnected_Once()
    {
        using var c = new SerialCtx();
        var counts = new ConcurrentDictionary<int, int>();
        var kinds = new ConcurrentQueue<string>();
        int sent = 0;
        var sender = new Thread(() =>
        {
            // 회선이 닫히는 동안 계속 보낸다: 각 호출은 동기 InvalidOperationException이거나 완료가 정확히 한 번
            for (int i = 0; ; i++)
            {
                int id = i;
                try { c.Us.SendOrdered(new SecsMessage(6, 11, false, SecsItem.U4((uint)id)), t => { counts.AddOrUpdate(id, 1, (_, n) => n + 1); kinds.Enqueue(Describe(t)); }); }
                catch (InvalidOperationException) { break; }
                sent = i + 1;
                if (i == 10) c.Line.Close();
            }
        }) { IsBackground = true };
        sender.Start();
        Assert.True(sender.Join(5000));
        TestUtil.Wait(() => counts.Count == sent && c.Log.Contains("DeviceClosed"), 5000);
        Thread.Sleep(100);   // 늦은 두 번째 완료가 없는지
        Assert.True(sent > 10);
        Assert.All(counts.Values, n => Assert.Equal(1, n));
        Assert.Equal(sent, counts.Count);
        Assert.All(kinds, k => Assert.Equal("SecsDisconnectedException", k));
    }
    #endregion
}
