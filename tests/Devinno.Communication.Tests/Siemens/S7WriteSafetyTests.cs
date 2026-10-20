using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Xml.Linq;
using Devinno.Communication.Siemens;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Siemens;

// Plan 13 쓰기 안전(S7Ethernet): 끊긴 동안 쓰기 버림, Stop이 쓰기 비움, 실패 이벤트의 시도 정보, 블록 쓰기 실패의 WrittenBytes.
// TCP 루프를 실제로 돌려 FakeS7Plc 루프백으로 시험하고, 연결 상태 전이는 internal seam(SetLinkUp/SetLinkDown)으로도 시험한다.
public class S7WriteSafetyTests : IDisposable
{
    readonly List<IDisposable> owned = [];
    readonly FakeS7Plc plc = new();

    public S7WriteSafetyTests() => plc.Start();

    public void Dispose()
    {
        foreach (var d in owned) d.Dispose();
        plc.Dispose();
    }

    static List<(int Id, WriteDiscardReason Reason)> Record(S7Ethernet m)
    {
        var list = new List<(int, WriteDiscardReason)>();
        m.WriteDiscarded += (s, e) => { lock (list) list.Add((e.MessageID, e.Reason)); };
        return list;
    }

    static List<(int, WriteDiscardReason)> Snap(List<(int Id, WriteDiscardReason Reason)> list)
    {
        lock (list) return [.. list];
    }

    static S7Value[] V(long v = 1) => [new S7Value("MW0", v)];

    S7Ethernet Master(int? port = null, int timeout = 500)
    {
        var m = new S7Ethernet { RemoteIP = "127.0.0.1", RemotePort = port ?? plc.Port, Timeout = timeout, Interval = 1 };
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

    // ---------------- 끊김 버림 / Stop ----------------

    [Fact]
    public void Write_BeforeStart_DiscardedSync_NotSent()
    {
        var m = Master();
        var ev = Record(m);
        m.ManualWrite(7, V());
        m.ManualWriteBlock(8, "MB0", [1, 2]);
        Assert.Equal([(7, WriteDiscardReason.NotConnected), (8, WriteDiscardReason.NotConnected)], Snap(ev));
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void Read_BeforeStart_Kept()
    {
        var m = Master();
        var ev = Record(m);
        m.ManualRead(8, ["MW0"]);
        m.ManualReadBlock(9, "MB0", 10);
        Assert.Empty(Snap(ev));
        Assert.Equal(8, m.TakeNextWork()!.MessageID);
        Assert.Equal(9, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void NamedWrite_BeforeStart_Discarded_SingleAndMulti()
    {
        var m = Master();
        m.FromXml(XElement.Parse("""<S7Ethernet><Write Name="A" Id="30" Address="DB1.DBW10" /></S7Ethernet>"""));
        var ev = Record(m);
        m.NamedWrite("A", 5);
        m.NamedWrite("A", [1, 2]);
        Assert.Equal([(30, WriteDiscardReason.NotConnected), (30, WriteDiscardReason.NotConnected)], Snap(ev));
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void LinkDown_DiscardsWritesKeepsReads_InOrder()
    {
        var m = Master();
        var ev = Record(m);
        m.SetLinkUp();
        m.ManualWrite(1, V());
        m.ManualRead(2, ["MW0"]);
        m.ManualWriteBlock(3, "MB0", [1]);
        Assert.Empty(Snap(ev));
        m.SetLinkDown(false);
        Assert.Equal([(1, WriteDiscardReason.Disconnected), (3, WriteDiscardReason.Disconnected)], Snap(ev));
        Assert.Equal(2, m.TakeNextWork()!.MessageID);
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void LinkDown_Stopping_ReasonStopped()
    {
        var m = Master();
        var ev = Record(m);
        m.SetLinkUp();
        m.ManualWrite(1, V());
        m.ManualRead(2, ["MW0"]);
        m.ManualWrite(3, V());
        m.SetLinkDown(true);
        Assert.Equal([(1, WriteDiscardReason.Stopped), (3, WriteDiscardReason.Stopped)], Snap(ev));
        Assert.Equal(2, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void LinkDown_DiscardFalse_KeepsWrites()
    {
        var m = Master();
        m.DiscardWritesWhileDisconnected = false;
        var ev = Record(m);
        m.SetLinkUp();
        m.ManualWrite(1, V());
        m.SetLinkDown(false);
        Assert.Empty(Snap(ev));
        Assert.Equal(1, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void Stop_Unstarted_DiscardFalse_RaisesStopped()
    {
        var m = Master();
        m.DiscardWritesWhileDisconnected = false;
        var ev = Record(m);
        m.ManualWrite(1, V());
        m.ManualRead(2, ["MW0"]);
        m.ManualWriteBlock(3, "MB0", [1]);
        Assert.Empty(Snap(ev));
        m.Stop();
        Assert.Equal([(1, WriteDiscardReason.Stopped), (3, WriteDiscardReason.Stopped)], Snap(ev));
        Assert.Equal(2, m.TakeNextWork()!.MessageID);
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public async Task DiscardFalse_WriteBeforeStart_SentAfterConnect()
    {
        var m = Master();
        m.DiscardWritesWhileDisconnected = false;
        var ev = Record(m);
        var got = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.WriteReceived += (s, e) => got.TrySetResult(e.MessageID);
        m.ManualWrite(1, V(1234));
        m.Start();
        Assert.Equal(1, await got.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1234, plc.Get("MW0"));
        Assert.Empty(Snap(ev));
    }

    [Fact]
    public void StartThenWrite_BeforeConnect_DiscardedSync()
    {
        var m = Master(ClosedPort());
        var ev = Record(m);
        m.Start();
        m.ManualWrite(3, V(5));
        Assert.Equal([(3, WriteDiscardReason.NotConnected)], Snap(ev));
    }

    [Fact]
    public async Task IsOpen_ImpliesWritesAccepted()
    {
        var m = Master();
        var ev = Record(m);
        var got = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.WriteReceived += (s, e) => got.TrySetResult(e.MessageID);
        m.Start();
        await TestUtil.Until(() => m.IsOpen);
        m.ManualWrite(4, V(77));
        Assert.Equal(4, await got.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(77, plc.Get("MW0"));
        Assert.Empty(Snap(ev));
    }

    [Fact]
    public async Task Stop_DiscardFalse_DiscardsQueuedWrites_NotSentOnRestart()
    {
        var m = Master(ClosedPort());
        m.DiscardWritesWhileDisconnected = false;
        var ev = Record(m);
        int written = 0;
        m.WriteReceived += (s, e) => Interlocked.Increment(ref written);
        m.Start();
        m.ManualWrite(1, V(11));
        m.ManualWrite(2, [new S7Value("MW2", 22)]);
        Assert.Empty(Snap(ev));
        m.Stop();
        Assert.Equal([(1, WriteDiscardReason.Stopped), (2, WriteDiscardReason.Stopped)], Snap(ev));

        m.RemotePort = plc.Port;
        m.Start();
        await TestUtil.Until(() => m.IsOpen);
        await Task.Delay(300);
        Assert.Equal(0, written);
        Assert.Equal(0, plc.Get("MW0"));
        Assert.Equal(0, plc.Get("MW2"));
        Assert.Equal(2, Snap(ev).Count);
    }

    [Fact]
    public async Task Stop_WithLinkUp_EachWriteStoppedOnce()
    {
        plc.Silent = true;
        var m = Master(timeout: 5000);
        var ev = Record(m);
        var timeouts = new ConcurrentQueue<S7Ethernet.AttemptFailedEventArgs>();
        m.TimeoutReceived += (s, e) => timeouts.Enqueue(e);
        m.Start();
        await TestUtil.Until(() => m.IsOpen);
        m.ManualWrite(1, V(1));
        await TestUtil.Until(() => plc.JobRequests >= 1);   // 쓰기 1은 응답을 기다리는 중
        m.ManualWrite(2, V(2));
        m.ManualWrite(3, V(3));
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
        plc.Silent = true;
        var m = Master(timeout: 5000);
        var log = new ConcurrentQueue<string>();
        m.TimeoutReceived += (s, e) => log.Enqueue($"Timeout({e.MessageID},{e.Attempt},{e.IsFinal},{e.RequestSent})");
        m.DeviceClosed += (s, e) => log.Enqueue("DeviceClosed");
        m.WriteDiscarded += (s, e) => log.Enqueue($"Discarded({e.MessageID},{e.Reason})");
        m.Start();
        await TestUtil.Until(() => m.IsOpen);
        m.ManualWrite(1, V(1));
        await TestUtil.Until(() => plc.JobRequests >= 1);
        m.ManualWrite(2, V(2));
        m.ManualWrite(3, V(3));
        plc.Kick();

        await TestUtil.Until(() => log.Count >= 4);
        Assert.Equal(
            ["Timeout(1,1,True,True)", "DeviceClosed", "Discarded(2,Disconnected)", "Discarded(3,Disconnected)"],
            log.Take(4).ToArray());
    }

    [Fact]
    public async Task Retry_AttemptNumbers()
    {
        plc.Silent = true;
        var m = Master(timeout: 100);
        var timeouts = new ConcurrentQueue<S7Ethernet.AttemptFailedEventArgs>();
        m.TimeoutReceived += (s, e) => timeouts.Enqueue(e);
        m.Start();
        await TestUtil.Until(() => m.IsOpen);
        m.ManualWrite(5, V(1), repeatCount: 2);
        await TestUtil.Until(() => timeouts.Count >= 3);
        await Task.Delay(300);
        var list = timeouts.ToArray();
        Assert.Equal(3, list.Length);
        Assert.Equal([1, 2, 3], list.Select(e => e.Attempt));
        Assert.Equal([false, false, true], list.Select(e => e.IsFinal));
        Assert.All(list, e => Assert.True(e.RequestSent));
        Assert.All(list, e => Assert.Equal(5, e.MessageID));
        Assert.All(list, e => Assert.Equal(S7Command.Write, e.Command));
        Assert.All(list, e => Assert.Equal(0, e.WrittenBytes));
    }

    [Fact]
    public async Task RestartFromHandler_OldLoopLinkDown_DoesNotCloseNewLink()
    {
        var m = Master();
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
        m.ManualWrite(9, [new S7Value("MW2", 4321)]);
        Assert.Empty(Snap(ev));
        Assert.Equal(9, await got.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(4321, plc.Get("MW2"));
    }

    [Fact]
    public void Xml_RoundTrip_DiscardWritesWhileDisconnected()
    {
        var m = new S7Ethernet();
        m.FromXml(XElement.Parse("""<S7Ethernet DiscardWritesWhileDisconnected="false" />"""));
        Assert.False(m.DiscardWritesWhileDisconnected);
        Assert.Equal("false", m.ToXml().Attribute("DiscardWritesWhileDisconnected")?.Value);

        m.FromXml(XElement.Parse("<S7Ethernet />"));
        Assert.True(m.DiscardWritesWhileDisconnected);
    }

    // ---------------- 블록 쓰기 부분 반영 (WrittenBytes) ----------------

    // PDU 240이면 쓰기 조각은 212바이트: 500바이트 = 212 + 212 + 76
    const int Chunk = 212;

    async Task<S7Ethernet> OpenSmallPdu(int timeout = 500)
    {
        plc.PduSize = 240;
        plc.AddDb(1, 600);
        var m = Master(timeout: timeout);
        m.Start();
        await TestUtil.Until(() => m.IsOpen);
        return m;
    }

    [Fact]
    public async Task BlockWrite_SecondChunkNak_WrittenBytesIsFirstChunk()
    {
        plc.NakWriteAt = 2;
        var m = await OpenSmallPdu();
        var naks = new ConcurrentQueue<S7Ethernet.NAKEventArgs>();
        int written = 0, timeouts = 0;
        m.NAKReceived += (s, e) => naks.Enqueue(e);
        m.WriteReceived += (s, e) => Interlocked.Increment(ref written);
        m.TimeoutReceived += (s, e) => Interlocked.Increment(ref timeouts);
        var data = Enumerable.Range(0, 500).Select(i => (byte)(i % 250 + 1)).ToArray();
        m.ManualWriteBlock(2, "DB1.DBB0", data, repeatCount: 3);   // NAK은 재시도하지 않는다
        await TestUtil.Until(() => !naks.IsEmpty);
        await Task.Delay(100);
        var e = Assert.Single(naks);
        Assert.Equal((2, S7Command.WriteBlock, 0x03, 0, Chunk), (e.MessageID, e.Command, e.ErrorCode, e.ItemIndex, e.WrittenBytes));
        Assert.Equal(0, Volatile.Read(ref written));
        Assert.Equal(0, Volatile.Read(ref timeouts));
        Assert.Equal(data[..Chunk], plc.GetBytes("DB1.DBB0", Chunk));   // 앞 조각은 실제로 반영됨
        Assert.Equal(new byte[Chunk], plc.GetBytes($"DB1.DBB{Chunk}", Chunk));
    }

    [Fact]
    public async Task BlockWrite_ThirdChunkNak_WrittenBytesIsTwoChunks()
    {
        plc.NakWriteAt = 3;
        var m = await OpenSmallPdu();
        var naks = new ConcurrentQueue<S7Ethernet.NAKEventArgs>();
        m.NAKReceived += (s, e) => naks.Enqueue(e);
        m.ManualWriteBlock(2, "DB1.DBB0", new byte[500]);
        await TestUtil.Until(() => !naks.IsEmpty);
        Assert.Equal(2 * Chunk, naks.Single().WrittenBytes);
    }

    [Fact]
    public async Task BlockWrite_FirstChunkNak_WrittenBytesZero()
    {
        plc.NakWriteAt = 1;
        var m = await OpenSmallPdu();
        var naks = new ConcurrentQueue<S7Ethernet.NAKEventArgs>();
        m.NAKReceived += (s, e) => naks.Enqueue(e);
        m.ManualWriteBlock(2, "DB1.DBB0", new byte[500]);
        await TestUtil.Until(() => !naks.IsEmpty);
        await Task.Delay(100);
        Assert.Equal(0, Assert.Single(naks).WrittenBytes);
    }

    [Fact]
    public async Task Write_NonBlock_Nak_WrittenBytesZero()
    {
        plc.NakWriteAt = 1;
        var m = await OpenSmallPdu();
        var naks = new ConcurrentQueue<S7Ethernet.NAKEventArgs>();
        m.NAKReceived += (s, e) => naks.Enqueue(e);
        m.ManualWrite(3, V(5));
        await TestUtil.Until(() => !naks.IsEmpty);
        var e = Assert.Single(naks);
        Assert.Equal((3, S7Command.Write, 0), (e.MessageID, e.Command, e.WrittenBytes));
    }

    [Fact]
    public async Task BlockWrite_SecondChunkSilent_TimeoutWrittenBytesIsFirstChunk()
    {
        plc.SilentWriteAt = 2;
        var m = await OpenSmallPdu(timeout: 150);
        var timeouts = new ConcurrentQueue<S7Ethernet.AttemptFailedEventArgs>();
        m.TimeoutReceived += (s, e) => timeouts.Enqueue(e);
        m.ManualWriteBlock(2, "DB1.DBB0", new byte[500]);
        await TestUtil.Until(() => !timeouts.IsEmpty);
        await Task.Delay(100);
        var e = Assert.Single(timeouts);
        Assert.Equal((2, S7Command.WriteBlock, 1, true, true, Chunk), (e.MessageID, e.Command, e.Attempt, e.IsFinal, e.RequestSent, e.WrittenBytes));
    }

    [Fact]
    public async Task BlockWrite_FirstChunkSilent_TimeoutWrittenBytesZero()
    {
        plc.SilentWriteAt = 1;
        var m = await OpenSmallPdu(timeout: 150);
        var timeouts = new ConcurrentQueue<S7Ethernet.AttemptFailedEventArgs>();
        m.TimeoutReceived += (s, e) => timeouts.Enqueue(e);
        m.ManualWriteBlock(2, "DB1.DBB0", new byte[500]);
        await TestUtil.Until(() => !timeouts.IsEmpty);
        Assert.Equal(0, timeouts.Single().WrittenBytes);
    }

    [Fact]
    public async Task BlockWrite_ConnectionLostMidBlock_FinalTimeoutCarriesWrittenBytes()
    {
        plc.SilentWriteAt = 2;
        var m = await OpenSmallPdu(timeout: 5000);
        var timeouts = new ConcurrentQueue<S7Ethernet.AttemptFailedEventArgs>();
        m.TimeoutReceived += (s, e) => timeouts.Enqueue(e);
        m.ManualWriteBlock(2, "DB1.DBB0", new byte[500]);
        await TestUtil.Until(() => plc.JobRequests >= 2);   // 둘째 조각이 응답 없이 대기 중
        plc.Kick();
        await TestUtil.Until(() => !timeouts.IsEmpty);
        var e = timeouts.First();
        Assert.Equal((1, true, true, Chunk), (e.Attempt, e.IsFinal, e.RequestSent, e.WrittenBytes));
    }

    [Fact]
    public async Task BlockWrite_Retry_RestartsFromFirstChunk_WrittenBytesPerAttempt()
    {
        plc.SilentWriteAt = 2;   // 1차 시도: 둘째 조각 무응답
        plc.NakWriteAt = 3;      // 2차 시도: 첫 조각(전체 3번째 쓰기)부터 다시 보내지고 거기서 NAK
        var m = await OpenSmallPdu(timeout: 150);
        var log = new ConcurrentQueue<string>();
        m.TimeoutReceived += (s, e) => log.Enqueue($"Timeout({e.Attempt},{e.IsFinal},{e.WrittenBytes})");
        m.NAKReceived += (s, e) => log.Enqueue($"Nak({e.WrittenBytes})");
        m.ManualWriteBlock(2, "DB1.DBB0", new byte[500], repeatCount: 1);
        await TestUtil.Until(() => log.Count >= 2);
        await Task.Delay(100);
        // 첫 시도에서 Ack된 212바이트가 둘째 시도의 NAK에 이월되지 않는다
        Assert.Equal([$"Timeout(1,False,{Chunk})", "Nak(0)"], log.ToArray());
        Assert.Equal(new (byte, int)[] { (5, Chunk), (5, Chunk), (5, Chunk) }, plc.Jobs);
    }

    [Fact]
    public async Task BlockWrite_Success_NoWrittenBytesEvents()
    {
        var m = await OpenSmallPdu();
        var got = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        int bad = 0;
        m.WriteReceived += (s, e) => got.TrySetResult(e.MessageID);
        m.NAKReceived += (s, e) => Interlocked.Increment(ref bad);
        m.TimeoutReceived += (s, e) => Interlocked.Increment(ref bad);
        var data = Enumerable.Range(0, 500).Select(i => (byte)(i % 250 + 1)).ToArray();
        m.ManualWriteBlock(2, "DB1.DBB0", data);
        Assert.Equal(2, await got.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(0, Volatile.Read(ref bad));
        Assert.Equal(data, plc.GetBytes("DB1.DBB0", 500));
    }
}
