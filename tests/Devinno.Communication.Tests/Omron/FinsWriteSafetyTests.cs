using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Xml.Linq;
using Devinno.Communication.Omron.Data;
using Devinno.Communication.Omron.Ethernet;
using Devinno.Communication.Omron.Serial;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Omron;

// Plan 13 쓰기 안전(FINS): 끊긴 동안 쓰기 버림, Stop이 쓰기 비움, 실패 이벤트의 시도 정보, 블록 쓰기 실패의 WrittenCount.
// FinsUdp·FinsTcp는 루프를 실제로 돌려 시험하고, FinsSerial은 시리얼 포트를 열 수 없으므로 internal seam(SetLinkUp/SetLinkDown/Process)으로 시험한다.
// 블록은 MaxWordsPerRequest를 Chunk(10)로 줄여 25개를 10 + 10 + 5 조각으로 보낸다.
public class FinsWriteSafetyTests : IDisposable
{
    const int Chunk = 10, Total = 25;

    readonly List<IDisposable> owned = [];
    readonly FakeOmronPlc plc = new();

    public FinsWriteSafetyTests()
    {
        plc.Start();
        owned.Add(plc);
    }

    public void Dispose()
    {
        foreach (var d in owned) d.Dispose();
    }

    static List<(int Id, WriteDiscardReason Reason)> Record(Action<EventHandler<WriteDiscardedEventArgs>> subscribe)
    {
        var list = new List<(int, WriteDiscardReason)>();
        subscribe((s, e) => { lock (list) list.Add((e.MessageID, e.Reason)); });
        return list;
    }

    static List<(int, WriteDiscardReason)> Snap(List<(int Id, WriteDiscardReason Reason)> list)
    {
        lock (list) return [.. list];
    }

    FinsUdp Udp(int timeout = 500)
    {
        var m = new FinsUdp { RemoteIP = "127.0.0.1", RemotePort = plc.UdpPort, Timeout = timeout, Interval = 1, MaxWordsPerRequest = Chunk };
        owned.Add(m);
        return m;
    }

    FinsTcp Tcp(int timeout = 500, int? port = null)
    {
        var m = new FinsTcp { RemoteIP = "127.0.0.1", RemotePort = port ?? plc.TcpPort, Timeout = timeout, Interval = 1, MaxWordsPerRequest = Chunk };
        owned.Add(m);
        return m;
    }

    static FinsSerial Serial()
        => new() { MaxWordsPerRequest = Chunk };

    static long[] Words(int n = Total) => Enumerable.Range(0, n).Select(i => (long)(i + 1)).ToArray();

    static int ClosedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    // ---------------- FinsUdp ----------------

    [Fact]
    public void Udp_Write_BeforeStart_DiscardedSync_NotSent()
    {
        var m = Udp();
        var ev = Record(h => m.WriteDiscarded += h);
        m.ManualWrite(7, "D0", 1);
        m.ManualWriteBlock(8, "D0", [1, 2]);
        Assert.Equal([(7, WriteDiscardReason.NotConnected), (8, WriteDiscardReason.NotConnected)], Snap(ev));
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void Udp_Read_BeforeStart_Kept()
    {
        var m = Udp();
        var ev = Record(h => m.WriteDiscarded += h);
        m.ManualRead(8, ["D0"]);
        m.ManualReadBlock(9, "D0", 3);
        Assert.Empty(Snap(ev));
        Assert.Equal(8, m.TakeNextWork()!.MessageID);
        Assert.Equal(9, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void Udp_LinkDown_DiscardsWritesKeepsReads_InOrder()
    {
        var m = Udp();
        var ev = Record(h => m.WriteDiscarded += h);
        m.SetLinkUp();
        m.ManualWrite(1, "D0", 1);
        m.ManualRead(2, ["D0"]);
        m.ManualWriteBlock(3, "D0", [1]);
        Assert.Empty(Snap(ev));
        m.SetLinkDown(false);
        Assert.Equal([(1, WriteDiscardReason.Disconnected), (3, WriteDiscardReason.Disconnected)], Snap(ev));
        Assert.Equal(2, m.TakeNextWork()!.MessageID);
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void Udp_LinkDown_Stopping_ReasonStopped()
    {
        var m = Udp();
        var ev = Record(h => m.WriteDiscarded += h);
        m.SetLinkUp();
        m.ManualWrite(1, "D0", 1);
        m.ManualRead(2, ["D0"]);
        m.ManualWrite(3, "D1", 1);
        m.SetLinkDown(true);
        Assert.Equal([(1, WriteDiscardReason.Stopped), (3, WriteDiscardReason.Stopped)], Snap(ev));
        Assert.Equal(2, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void Udp_LinkDown_DiscardFalse_KeepsWrites()
    {
        var m = Udp();
        m.DiscardWritesWhileDisconnected = false;
        var ev = Record(h => m.WriteDiscarded += h);
        m.SetLinkUp();
        m.ManualWrite(1, "D0", 1);
        m.SetLinkDown(false);
        Assert.Empty(Snap(ev));
        Assert.Equal(1, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void Udp_Stop_Unstarted_DiscardFalse_RaisesStopped()
    {
        var m = Udp();
        m.DiscardWritesWhileDisconnected = false;
        var ev = Record(h => m.WriteDiscarded += h);
        m.ManualWrite(1, "D0", 1);
        m.ManualRead(2, ["D0"]);
        m.ManualWrite(3, "D1", 1);
        Assert.Empty(Snap(ev));
        m.Stop();
        Assert.Equal([(1, WriteDiscardReason.Stopped), (3, WriteDiscardReason.Stopped)], Snap(ev));
        Assert.Equal(2, m.TakeNextWork()!.MessageID);
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void Udp_NamedWrite_BeforeStart_Discarded()
    {
        var m = Udp();
        m.FromXml(XElement.Parse("""<FinsUdp><Write Name="A" Id="30" Address="D10" /><Write Name="B" Id="31" Address="D20" /></FinsUdp>"""));
        var ev = Record(h => m.WriteDiscarded += h);
        m.NamedWrite("A", 5);
        m.NamedWrite("B", [1, 2]);
        Assert.Equal([(30, WriteDiscardReason.NotConnected), (31, WriteDiscardReason.NotConnected)], Snap(ev));
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public async Task Udp_WriteRightAfterIsOpen_IsAccepted_AndWritten()
    {
        var m = Udp();
        var ev = Record(h => m.WriteDiscarded += h);
        var got = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.WriteReceived += (s, e) => got.TrySetResult(e.MessageID);
        m.Start();
        await TestUtil.Until(() => m.IsOpen);   // IsOpen이 true면 이미 연결로 본다(바인드 = 연결)
        m.ManualWrite(1, "D0", 1234);
        Assert.Equal(1, await got.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1234, plc.Get("D0"));
        Assert.Empty(Snap(ev));
    }

    [Fact]
    public async Task Udp_WriteInsideDeviceOpened_IsAccepted()
    {
        var m = Udp();
        var ev = Record(h => m.WriteDiscarded += h);
        var got = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.WriteReceived += (s, e) => got.TrySetResult(e.MessageID);
        m.DeviceOpened += (s, e) => m.ManualWrite(4, "D1", 77);
        m.Start();
        Assert.Equal(4, await got.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(77, plc.Get("D1"));
        Assert.Empty(Snap(ev));
    }

    [Fact]
    public async Task Udp_DiscardFalse_WriteBeforeStart_SentAfterOpen()
    {
        var m = Udp();
        m.DiscardWritesWhileDisconnected = false;
        var ev = Record(h => m.WriteDiscarded += h);
        var got = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.WriteReceived += (s, e) => got.TrySetResult(e.MessageID);
        m.ManualWrite(1, "D0", 1234);
        m.Start();
        Assert.Equal(1, await got.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1234, plc.Get("D0"));
        Assert.Empty(Snap(ev));
    }

    [Fact]
    public async Task Udp_Stop_DiscardFalse_DiscardsQueuedWrites_NotSentOnRestart()
    {
        var m = Udp();
        m.DiscardWritesWhileDisconnected = false;
        var ev = Record(h => m.WriteDiscarded += h);
        m.ManualWrite(1, "D0", 11);
        m.ManualWrite(2, "D1", 22);
        m.Stop();   // Start 전 Stop도 대기 쓰기를 비운다
        Assert.Equal([(1, WriteDiscardReason.Stopped), (2, WriteDiscardReason.Stopped)], Snap(ev));

        m.Start();
        await TestUtil.Until(() => m.IsOpen);
        await Task.Delay(300);
        Assert.Equal(0, plc.Get("D0"));
        Assert.Equal(0, plc.Get("D1"));
        Assert.Equal(2, Snap(ev).Count);
    }

    [Fact]
    public async Task Udp_Stop_WithLinkUp_EachWriteStoppedOnce()
    {
        plc.Silent = true;
        var m = Udp(timeout: 5000);
        var ev = Record(h => m.WriteDiscarded += h);
        var timeouts = new ConcurrentQueue<FinsUdp.AttemptFailedEventArgs>();
        m.TimeoutReceived += (s, e) => timeouts.Enqueue(e);
        m.Start();
        await TestUtil.Until(() => m.IsOpen);
        m.ManualWrite(1, "D0", 1);
        await TestUtil.Until(() => plc.Requests.Length >= 1);   // 쓰기 1은 응답을 기다리는 중
        m.ManualWrite(2, "D1", 2);
        m.ManualWrite(3, "D2", 3);
        m.Stop();

        await TestUtil.Until(() => !timeouts.IsEmpty);
        await Task.Delay(100);
        var t = Assert.Single(timeouts);
        Assert.Equal((1, 1, true, true), (t.MessageID, t.Attempt, t.IsFinal, t.RequestSent));
        Assert.Equal([(2, WriteDiscardReason.Stopped), (3, WriteDiscardReason.Stopped)], Snap(ev));
    }

    [Fact]
    public async Task Udp_Retry_AttemptNumbers()
    {
        plc.Silent = true;
        var m = Udp(timeout: 100);
        var timeouts = new ConcurrentQueue<FinsUdp.AttemptFailedEventArgs>();
        m.TimeoutReceived += (s, e) => timeouts.Enqueue(e);
        m.Start();
        await TestUtil.Until(() => m.IsOpen);
        m.ManualWrite(5, "D0", 1, repeatCount: 2);
        await TestUtil.Until(() => timeouts.Count >= 3);
        await Task.Delay(300);
        var list = timeouts.ToArray();
        Assert.Equal(3, list.Length);
        Assert.Equal([1, 2, 3], list.Select(e => e.Attempt));
        Assert.Equal([false, false, true], list.Select(e => e.IsFinal));
        Assert.All(list, e => Assert.True(e.RequestSent));
        Assert.All(list, e => Assert.Equal((5, FinsCommand.Write, 0), (e.MessageID, e.Command, e.WrittenCount)));
    }

    [Fact]
    public async Task Udp_RestartFromHandler_OldLoopLinkDown_DoesNotCloseNewLink()
    {
        var m = Udp();
        var ev = Record(h => m.WriteDiscarded += h);
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
        m.ManualWrite(9, "D2", 4321);
        Assert.Empty(Snap(ev));
        Assert.Equal(9, await got.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(4321, plc.Get("D2"));
    }

    [Fact]
    public void Udp_Xml_RoundTrip_DiscardWritesWhileDisconnected()
    {
        var m = new FinsUdp();
        m.FromXml(XElement.Parse("""<FinsUdp DiscardWritesWhileDisconnected="false" />"""));
        Assert.False(m.DiscardWritesWhileDisconnected);
        Assert.Equal("false", m.ToXml().Attribute("DiscardWritesWhileDisconnected")?.Value);

        m.FromXml(XElement.Parse("<FinsUdp />"));
        Assert.True(m.DiscardWritesWhileDisconnected);
    }

    // ---- 블록 쓰기 부분 반영 (WrittenCount) ----

    async Task<FinsUdp> OpenUdp(int timeout = 500)
    {
        var m = Udp(timeout);
        m.Start();
        await TestUtil.Until(() => m.IsOpen);
        return m;
    }

    [Fact]
    public async Task Udp_BlockWrite_SecondChunkNak_WrittenCountIsFirstChunk()
    {
        plc.EndCode = 0x2102;
        plc.EndCodeAfter = 1;
        var m = await OpenUdp();
        var naks = new ConcurrentQueue<FinsUdp.NAKEventArgs>();
        int written = 0, timeouts = 0;
        m.NAKReceived += (s, e) => naks.Enqueue(e);
        m.WriteReceived += (s, e) => Interlocked.Increment(ref written);
        m.TimeoutReceived += (s, e) => Interlocked.Increment(ref timeouts);
        m.ManualWriteBlock(2, "D0", Words(), repeatCount: 3);   // NAK은 재시도하지 않는다
        await TestUtil.Until(() => !naks.IsEmpty);
        await Task.Delay(100);
        var e = Assert.Single(naks);
        Assert.Equal((2, FinsCommand.WriteBlock, 0x2102, Chunk), (e.MessageID, e.Command, e.ErrorCode, e.WrittenCount));
        Assert.Equal((0, 0), (Volatile.Read(ref written), Volatile.Read(ref timeouts)));
        Assert.Equal(Words(Chunk).Select(v => (ushort)v), plc.GetWords("D0", Chunk));   // 앞 조각은 실제로 반영됨
        Assert.Equal(new ushort[Chunk], plc.GetWords($"D{Chunk}", Chunk));
    }

    [Fact]
    public async Task Udp_BlockWrite_ThirdChunkNak_WrittenCountIsTwoChunks()
    {
        plc.EndCode = 0x2102;
        plc.EndCodeAfter = 2;
        var m = await OpenUdp();
        var naks = new ConcurrentQueue<FinsUdp.NAKEventArgs>();
        m.NAKReceived += (s, e) => naks.Enqueue(e);
        m.ManualWriteBlock(2, "D0", Words());
        await TestUtil.Until(() => !naks.IsEmpty);
        Assert.Equal(2 * Chunk, naks.Single().WrittenCount);
    }

    [Fact]
    public async Task Udp_BlockWrite_FirstChunkNak_WrittenCountZero()
    {
        plc.EndCode = 0x2102;
        var m = await OpenUdp();
        var naks = new ConcurrentQueue<FinsUdp.NAKEventArgs>();
        m.NAKReceived += (s, e) => naks.Enqueue(e);
        m.ManualWriteBlock(2, "D0", Words());
        await TestUtil.Until(() => !naks.IsEmpty);
        await Task.Delay(100);
        Assert.Equal(0, Assert.Single(naks).WrittenCount);
    }

    [Fact]
    public async Task Udp_BitBlockWrite_SecondChunkNak_CountsBits()
    {
        plc.EndCode = 0x2102;
        plc.EndCodeAfter = 1;
        var m = await OpenUdp();
        var naks = new ConcurrentQueue<FinsUdp.NAKEventArgs>();
        m.NAKReceived += (s, e) => naks.Enqueue(e);
        m.ManualWriteBlock(2, "CIO0.00", new long[Total]);
        await TestUtil.Until(() => !naks.IsEmpty);
        Assert.Equal(Chunk, naks.Single().WrittenCount);
    }

    [Fact]
    public async Task Udp_Write_NonBlock_Nak_WrittenCountZero()
    {
        plc.EndCode = 0x2102;
        var m = await OpenUdp();
        var naks = new ConcurrentQueue<FinsUdp.NAKEventArgs>();
        m.NAKReceived += (s, e) => naks.Enqueue(e);
        m.ManualWrite(3, "D0", 5);
        await TestUtil.Until(() => !naks.IsEmpty);
        var e = Assert.Single(naks);
        Assert.Equal((3, FinsCommand.Write, 0), (e.MessageID, e.Command, e.WrittenCount));
    }

    [Fact]
    public async Task Udp_ReadBlock_SecondChunkNak_WrittenCountZero()
    {
        plc.EndCode = 0x1103;
        plc.EndCodeAfter = 1;
        var m = await OpenUdp();
        var naks = new ConcurrentQueue<FinsUdp.NAKEventArgs>();
        m.NAKReceived += (s, e) => naks.Enqueue(e);
        m.ManualReadBlock(4, "D0", Total);
        await TestUtil.Until(() => !naks.IsEmpty);
        var e = Assert.Single(naks);
        Assert.Equal((4, FinsCommand.ReadBlock, 0), (e.MessageID, e.Command, e.WrittenCount));   // 읽기는 쓴 것이 없다
    }

    [Fact]
    public async Task Udp_BlockWrite_SecondChunkSilent_TimeoutWrittenCountIsFirstChunk()
    {
        plc.SilentAt = 2;
        var m = await OpenUdp(timeout: 150);
        var timeouts = new ConcurrentQueue<FinsUdp.AttemptFailedEventArgs>();
        m.TimeoutReceived += (s, e) => timeouts.Enqueue(e);
        m.ManualWriteBlock(2, "D0", Words());
        await TestUtil.Until(() => !timeouts.IsEmpty);
        await Task.Delay(100);
        var e = Assert.Single(timeouts);
        Assert.Equal((2, FinsCommand.WriteBlock, 1, true, true, Chunk), (e.MessageID, e.Command, e.Attempt, e.IsFinal, e.RequestSent, e.WrittenCount));
    }

    [Fact]
    public async Task Udp_BlockWrite_Retry_RestartsFromFirstChunk_WrittenCountPerAttempt()
    {
        plc.SilentAt = 2;   // 1차 시도: 둘째 조각 무응답. 2차 시도는 첫 조각부터 다시 보내 모두 성공
        var m = await OpenUdp(timeout: 150);
        var log = new ConcurrentQueue<string>();
        m.TimeoutReceived += (s, e) => log.Enqueue($"Timeout({e.Attempt},{e.IsFinal},{e.WrittenCount})");
        m.NAKReceived += (s, e) => log.Enqueue($"Nak({e.WrittenCount})");
        m.WriteReceived += (s, e) => log.Enqueue("Write");
        m.ManualWriteBlock(2, "D0", Words(), repeatCount: 1);
        await TestUtil.Until(() => log.Count >= 2);
        await Task.Delay(100);
        Assert.Equal([$"Timeout(1,False,{Chunk})", "Write"], log.ToArray());
        Assert.Equal(5, plc.Requests.Length);   // 1차 2개(첫 조각, 무응답 둘째) + 2차 3개
        Assert.Equal(Words().Select(v => (ushort)v), plc.GetWords("D0", Total));
    }

    [Fact]
    public async Task Udp_BlockWrite_Success_NoFailureEvents()
    {
        var m = await OpenUdp();
        int written = 0, naks = 0, timeouts = 0;
        m.WriteReceived += (s, e) => Interlocked.Increment(ref written);
        m.NAKReceived += (s, e) => Interlocked.Increment(ref naks);
        m.TimeoutReceived += (s, e) => Interlocked.Increment(ref timeouts);
        m.ManualWriteBlock(2, "D0", Words());
        await TestUtil.Until(() => Volatile.Read(ref written) == 1);
        await Task.Delay(100);
        Assert.Equal((1, 0, 0), (written, naks, timeouts));
    }

    // ---------------- FinsTcp ----------------

    [Fact]
    public void Tcp_Write_BeforeStart_DiscardedSync_NotSent()
    {
        var m = Tcp();
        var ev = Record(h => m.WriteDiscarded += h);
        m.ManualWrite(7, "D0", 1);
        m.ManualWriteBlock(8, "D0", [1, 2]);
        m.ManualRead(9, ["D0"]);
        Assert.Equal([(7, WriteDiscardReason.NotConnected), (8, WriteDiscardReason.NotConnected)], Snap(ev));
        Assert.Equal(9, m.TakeNextWork()!.MessageID);
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void Tcp_NamedWrite_BeforeStart_Discarded()
    {
        var m = Tcp();
        m.FromXml(XElement.Parse("""<FinsTcp><Write Name="A" Id="30" Address="D10" /></FinsTcp>"""));
        var ev = Record(h => m.WriteDiscarded += h);
        m.NamedWrite("A", 5);
        Assert.Equal([(30, WriteDiscardReason.NotConnected)], Snap(ev));
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void Tcp_LinkDown_DiscardsWritesKeepsReads_InOrder()
    {
        var m = Tcp();
        var ev = Record(h => m.WriteDiscarded += h);
        m.SetLinkUp();
        m.ManualWrite(1, "D0", 1);
        m.ManualRead(2, ["D0"]);
        m.ManualWriteBlock(3, "D0", [1]);
        Assert.Empty(Snap(ev));
        m.SetLinkDown(false);
        Assert.Equal([(1, WriteDiscardReason.Disconnected), (3, WriteDiscardReason.Disconnected)], Snap(ev));
        Assert.Equal(2, m.TakeNextWork()!.MessageID);
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void Tcp_LinkDown_Stopping_ReasonStopped()
    {
        var m = Tcp();
        var ev = Record(h => m.WriteDiscarded += h);
        m.SetLinkUp();
        m.ManualWrite(1, "D0", 1);
        m.ManualRead(2, ["D0"]);
        m.SetLinkDown(true);
        Assert.Equal([(1, WriteDiscardReason.Stopped)], Snap(ev));
        Assert.Equal(2, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void Tcp_LinkDown_DiscardFalse_KeepsWrites()
    {
        var m = Tcp();
        m.DiscardWritesWhileDisconnected = false;
        var ev = Record(h => m.WriteDiscarded += h);
        m.SetLinkUp();
        m.ManualWrite(1, "D0", 1);
        m.SetLinkDown(false);
        Assert.Empty(Snap(ev));
        Assert.Equal(1, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void Tcp_Stop_Unstarted_DiscardFalse_RaisesStopped()
    {
        var m = Tcp();
        m.DiscardWritesWhileDisconnected = false;
        var ev = Record(h => m.WriteDiscarded += h);
        m.ManualWrite(1, "D0", 1);
        m.ManualRead(2, ["D0"]);
        m.ManualWrite(3, "D1", 1);
        m.Stop();
        Assert.Equal([(1, WriteDiscardReason.Stopped), (3, WriteDiscardReason.Stopped)], Snap(ev));
        Assert.Equal(2, m.TakeNextWork()!.MessageID);
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void Tcp_StartThenWrite_BeforeConnect_DiscardedSync()
    {
        var m = Tcp(port: ClosedPort());
        var ev = Record(h => m.WriteDiscarded += h);
        m.Start();
        m.ManualWrite(3, "D0", 5);
        Assert.Equal([(3, WriteDiscardReason.NotConnected)], Snap(ev));
    }

    [Fact]
    public async Task Tcp_WriteRightAfterIsOpen_IsAccepted_AndWritten()
    {
        var m = Tcp();
        var ev = Record(h => m.WriteDiscarded += h);
        var got = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.WriteReceived += (s, e) => got.TrySetResult(e.MessageID);
        m.Start();
        await TestUtil.Until(() => m.IsOpen);   // IsOpen이 true면 이미 연결로 본다(노드 교환까지 끝남)
        m.ManualWrite(1, "D0", 1234);
        Assert.Equal(1, await got.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1234, plc.Get("D0"));
        Assert.Empty(Snap(ev));
    }

    [Fact]
    public async Task Tcp_WriteInsideDeviceOpened_IsAccepted()
    {
        var m = Tcp();
        var ev = Record(h => m.WriteDiscarded += h);
        var got = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.WriteReceived += (s, e) => got.TrySetResult(e.MessageID);
        m.DeviceOpened += (s, e) => m.ManualWrite(4, "D1", 77);
        m.Start();
        Assert.Equal(4, await got.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(77, plc.Get("D1"));
        Assert.Empty(Snap(ev));
    }

    [Fact]
    public async Task Tcp_Stop_DiscardFalse_DiscardsQueuedWrites_NotSentOnRestart()
    {
        var m = Tcp(port: ClosedPort());
        m.DiscardWritesWhileDisconnected = false;
        var ev = Record(h => m.WriteDiscarded += h);
        int written = 0;
        m.WriteReceived += (s, e) => Interlocked.Increment(ref written);
        m.Start();
        m.ManualWrite(1, "D0", 11);
        m.ManualWrite(2, "D1", 22);
        Assert.Empty(Snap(ev));
        m.Stop();
        Assert.Equal([(1, WriteDiscardReason.Stopped), (2, WriteDiscardReason.Stopped)], Snap(ev));

        m.RemotePort = plc.TcpPort;
        m.Start();
        await TestUtil.Until(() => m.IsOpen);
        await Task.Delay(300);
        Assert.Equal(0, written);
        Assert.Equal(0, plc.Get("D0"));
        Assert.Equal(0, plc.Get("D1"));
        Assert.Equal(2, Snap(ev).Count);
    }

    [Fact]
    public async Task Tcp_Stop_WithLinkUp_EachWriteStoppedOnce()
    {
        plc.Silent = true;
        var m = Tcp(timeout: 5000);
        var ev = Record(h => m.WriteDiscarded += h);
        var timeouts = new ConcurrentQueue<FinsTcp.AttemptFailedEventArgs>();
        m.TimeoutReceived += (s, e) => timeouts.Enqueue(e);
        m.Start();
        await TestUtil.Until(() => m.IsOpen);
        m.ManualWrite(1, "D0", 1);
        await TestUtil.Until(() => plc.Requests.Length >= 1);
        m.ManualWrite(2, "D1", 2);
        m.ManualWrite(3, "D2", 3);
        m.Stop();

        await TestUtil.Until(() => !timeouts.IsEmpty);
        await Task.Delay(100);
        var t = Assert.Single(timeouts);
        Assert.Equal((1, 1, true, true), (t.MessageID, t.Attempt, t.IsFinal, t.RequestSent));
        Assert.Equal([(2, WriteDiscardReason.Stopped), (3, WriteDiscardReason.Stopped)], Snap(ev));
    }

    [Fact]
    public async Task Tcp_PeerClose_TimeoutThenDeviceClosedThenDisconnected()
    {
        plc.Silent = true;
        var m = Tcp(timeout: 5000);
        var log = new ConcurrentQueue<string>();
        m.TimeoutReceived += (s, e) => log.Enqueue($"Timeout({e.MessageID},{e.Attempt},{e.IsFinal},{e.RequestSent})");
        m.DeviceClosed += (s, e) => log.Enqueue("DeviceClosed");
        m.WriteDiscarded += (s, e) => log.Enqueue($"Discarded({e.MessageID},{e.Reason})");
        m.Start();
        await TestUtil.Until(() => m.IsOpen);
        m.ManualWrite(1, "D0", 1);
        await TestUtil.Until(() => plc.Requests.Length >= 1);
        m.ManualWrite(2, "D1", 2);
        m.ManualWrite(3, "D2", 3);
        plc.Kick();

        await TestUtil.Until(() => log.Count >= 4);
        Assert.Equal(
            ["Timeout(1,1,True,True)", "DeviceClosed", "Discarded(2,Disconnected)", "Discarded(3,Disconnected)"],
            log.Take(4).ToArray());
    }

    [Fact]
    public async Task Tcp_Retry_AttemptNumbers()
    {
        plc.Silent = true;
        var m = Tcp(timeout: 100);
        var timeouts = new ConcurrentQueue<FinsTcp.AttemptFailedEventArgs>();
        m.TimeoutReceived += (s, e) => timeouts.Enqueue(e);
        m.Start();
        await TestUtil.Until(() => m.IsOpen);
        m.ManualWrite(5, "D0", 1, repeatCount: 2);
        await TestUtil.Until(() => timeouts.Count >= 3);
        await Task.Delay(300);
        var list = timeouts.ToArray();
        Assert.Equal(3, list.Length);
        Assert.Equal([1, 2, 3], list.Select(e => e.Attempt));
        Assert.Equal([false, false, true], list.Select(e => e.IsFinal));
        Assert.All(list, e => Assert.True(e.RequestSent));
        Assert.All(list, e => Assert.Equal((5, FinsCommand.Write, 0), (e.MessageID, e.Command, e.WrittenCount)));
    }

    [Fact]
    public async Task Tcp_RestartFromHandler_OldLoopLinkDown_DoesNotCloseNewLink()
    {
        var m = Tcp();
        var ev = Record(h => m.WriteDiscarded += h);
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
        m.ManualWrite(9, "D2", 4321);
        Assert.Empty(Snap(ev));
        Assert.Equal(9, await got.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(4321, plc.Get("D2"));
    }

    [Fact]
    public void Tcp_Xml_RoundTrip_DiscardWritesWhileDisconnected()
    {
        var m = new FinsTcp();
        m.FromXml(XElement.Parse("""<FinsTcp DiscardWritesWhileDisconnected="false" />"""));
        Assert.False(m.DiscardWritesWhileDisconnected);
        Assert.Equal("false", m.ToXml().Attribute("DiscardWritesWhileDisconnected")?.Value);

        m.FromXml(XElement.Parse("<FinsTcp />"));
        Assert.True(m.DiscardWritesWhileDisconnected);
    }

    async Task<FinsTcp> OpenTcp(int timeout = 500)
    {
        var m = Tcp(timeout);
        m.Start();
        await TestUtil.Until(() => m.IsOpen);
        return m;
    }

    [Fact]
    public async Task Tcp_BlockWrite_SecondChunkNak_WrittenCountIsFirstChunk()
    {
        plc.EndCode = 0x2102;
        plc.EndCodeAfter = 1;
        var m = await OpenTcp();
        var naks = new ConcurrentQueue<FinsTcp.NAKEventArgs>();
        m.NAKReceived += (s, e) => naks.Enqueue(e);
        m.ManualWriteBlock(2, "D0", Words(), repeatCount: 3);
        await TestUtil.Until(() => !naks.IsEmpty);
        await Task.Delay(100);
        var e = Assert.Single(naks);
        Assert.Equal((2, FinsCommand.WriteBlock, 0x2102, Chunk), (e.MessageID, e.Command, e.ErrorCode, e.WrittenCount));
        Assert.Equal(Words(Chunk).Select(v => (ushort)v), plc.GetWords("D0", Chunk));
    }

    [Fact]
    public async Task Tcp_BlockWrite_SecondChunkSilent_TimeoutWrittenCountIsFirstChunk()
    {
        plc.SilentAt = 2;
        var m = await OpenTcp(timeout: 150);
        var timeouts = new ConcurrentQueue<FinsTcp.AttemptFailedEventArgs>();
        m.TimeoutReceived += (s, e) => timeouts.Enqueue(e);
        m.ManualWriteBlock(2, "D0", Words());
        await TestUtil.Until(() => !timeouts.IsEmpty);
        await Task.Delay(100);
        var e = Assert.Single(timeouts);
        Assert.Equal((2, FinsCommand.WriteBlock, 1, true, true, Chunk), (e.MessageID, e.Command, e.Attempt, e.IsFinal, e.RequestSent, e.WrittenCount));
    }

    [Fact]
    public async Task Tcp_BlockWrite_ConnectionLostMidBlock_FinalTimeoutCarriesWrittenCount()
    {
        plc.SilentAt = 2;
        var m = await OpenTcp(timeout: 5000);
        var timeouts = new ConcurrentQueue<FinsTcp.AttemptFailedEventArgs>();
        m.TimeoutReceived += (s, e) => timeouts.Enqueue(e);
        m.ManualWriteBlock(2, "D0", Words());
        await TestUtil.Until(() => plc.Requests.Length >= 2);   // 둘째 조각이 응답 없이 대기 중
        plc.Kick();
        await TestUtil.Until(() => !timeouts.IsEmpty);
        var e = timeouts.First();
        Assert.Equal((1, true, true, Chunk), (e.Attempt, e.IsFinal, e.RequestSent, e.WrittenCount));
    }

    [Fact]
    public async Task Tcp_BlockWrite_Retry_RestartsFromFirstChunk_WrittenCountPerAttempt()
    {
        plc.SilentAt = 2;
        var m = await OpenTcp(timeout: 150);
        var log = new ConcurrentQueue<string>();
        m.TimeoutReceived += (s, e) => log.Enqueue($"Timeout({e.Attempt},{e.IsFinal},{e.WrittenCount})");
        m.WriteReceived += (s, e) => log.Enqueue("Write");
        m.ManualWriteBlock(2, "D0", Words(), repeatCount: 1);
        await TestUtil.Until(() => log.Count >= 2);
        await Task.Delay(100);
        Assert.Equal([$"Timeout(1,False,{Chunk})", "Write"], log.ToArray());
        Assert.Equal(5, plc.Requests.Length);
    }

    // ---------------- FinsSerial ----------------

    [Fact]
    public void Serial_Write_BeforeStart_DiscardedSync_NotSent()
    {
        var m = Serial();
        var ev = Record(h => m.WriteDiscarded += h);
        m.ManualWrite(7, "D0", 1);
        m.ManualWriteBlock(8, "D0", [1, 2]);
        Assert.Equal([(7, WriteDiscardReason.NotConnected), (8, WriteDiscardReason.NotConnected)], Snap(ev));
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void Serial_NamedWrite_BeforeStart_Discarded()
    {
        var m = Serial();
        m.FromXml(XElement.Parse("""<FinsSerial><Write Name="A" Id="30" Address="D10" /></FinsSerial>"""));
        var ev = Record(h => m.WriteDiscarded += h);
        m.NamedWrite("A", 5);
        Assert.Equal([(30, WriteDiscardReason.NotConnected)], Snap(ev));
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void Serial_Read_BeforeStart_Kept()
    {
        var m = Serial();
        var ev = Record(h => m.WriteDiscarded += h);
        m.ManualRead(8, ["D0"]);
        Assert.Empty(Snap(ev));
        Assert.Equal(8, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void Serial_LinkDown_DiscardsWritesKeepsReads_InOrder()
    {
        var m = Serial();
        var ev = Record(h => m.WriteDiscarded += h);
        m.SetLinkUp();
        m.ManualWrite(1, "D0", 1);
        m.ManualRead(2, ["D0"]);
        m.ManualWriteBlock(3, "D0", [1]);
        Assert.Empty(Snap(ev));
        m.SetLinkDown(false);
        Assert.Equal([(1, WriteDiscardReason.Disconnected), (3, WriteDiscardReason.Disconnected)], Snap(ev));
        Assert.Equal(2, m.TakeNextWork()!.MessageID);
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void Serial_LinkDown_Stopping_ReasonStopped()
    {
        var m = Serial();
        var ev = Record(h => m.WriteDiscarded += h);
        m.SetLinkUp();
        m.ManualWrite(1, "D0", 1);
        m.ManualRead(2, ["D0"]);
        m.ManualWrite(3, "D1", 1);
        m.SetLinkDown(true);
        Assert.Equal([(1, WriteDiscardReason.Stopped), (3, WriteDiscardReason.Stopped)], Snap(ev));
        Assert.Equal(2, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void Serial_LinkDown_DiscardFalse_KeepsWrites()
    {
        var m = Serial();
        m.DiscardWritesWhileDisconnected = false;
        var ev = Record(h => m.WriteDiscarded += h);
        m.SetLinkUp();
        m.ManualWrite(1, "D0", 1);
        m.SetLinkDown(false);
        Assert.Empty(Snap(ev));
        Assert.Equal(1, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void Serial_Stop_Unstarted_DiscardFalse_RaisesStopped()
    {
        var m = Serial();
        m.DiscardWritesWhileDisconnected = false;
        var ev = Record(h => m.WriteDiscarded += h);
        m.ManualWrite(1, "D0", 1);
        m.ManualRead(2, ["D0"]);
        m.ManualWrite(3, "D1", 1);
        Assert.Empty(Snap(ev));
        m.Stop();
        Assert.Equal([(1, WriteDiscardReason.Stopped), (3, WriteDiscardReason.Stopped)], Snap(ev));
        Assert.Equal(2, m.TakeNextWork()!.MessageID);
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void Serial_LinkDown_FromOtherThread_DoesNotOverrideNewerLink()
    {
        var m = Serial();
        var ev = Record(h => m.WriteDiscarded += h);
        // 다른 스레드(옛 루프)가 올린 연결을 이 스레드(새 루프)가 이어받은 뒤, 옛 루프의 SetLinkDown이 오는 상황
        var old = new Thread(() => m.SetLinkUp());
        old.Start(); old.Join();
        m.SetLinkUp();
        var late = new Thread(() => m.SetLinkDown(false));
        late.Start(); late.Join();
        m.ManualWrite(9, "D0", 1);
        Assert.Empty(Snap(ev));
        Assert.Equal(9, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void Serial_Xml_RoundTrip_DiscardWritesWhileDisconnected()
    {
        var m = new FinsSerial();
        m.FromXml(XElement.Parse("""<FinsSerial DiscardWritesWhileDisconnected="false" />"""));
        Assert.False(m.DiscardWritesWhileDisconnected);
        Assert.Equal("false", m.ToXml().Attribute("DiscardWritesWhileDisconnected")?.Value);

        m.FromXml(XElement.Parse("<FinsSerial />"));
        Assert.True(m.DiscardWritesWhileDisconnected);
    }

    // 가상 PLC의 Host Link 처리기와 주고받는다: send가 응답을 정하고 receive가 그것을 돌려준다(null이면 무응답)
    (int Sent, FinsStatus Status) Run(FinsSerial m, FinsSerial.Work w,
        List<FinsSerial.AttemptFailedEventArgs> timeouts, List<FinsSerial.AttemptFailedEventArgs> fcss)
    {
        m.TimeoutReceived += (s, e) => timeouts.Add(e);
        m.FcsErrorReceived += (s, e) => fcss.Add(e);
        int sent = 0;
        byte[]? reply = null;
        var status = m.Process(w, req => { sent++; reply = plc.HandleHostLink(req); }, t => reply);
        return (sent, status);
    }

    static FinsSerial.Work Take(FinsSerial m) => m.TakeNextWork()!;

    static FinsSerial Manual()
        => new() { MaxWordsPerRequest = Chunk, DiscardWritesWhileDisconnected = false };

    [Fact]
    public void Serial_Process_Timeouts_AttemptAndIsFinal()
    {
        plc.Silent = true;
        var m = Manual();
        m.ManualWrite(5, "D0", 1, repeatCount: 2);
        var timeouts = new List<FinsSerial.AttemptFailedEventArgs>();
        var fcss = new List<FinsSerial.AttemptFailedEventArgs>();
        var (sent, _) = Run(m, Take(m), timeouts, fcss);
        Assert.Equal(3, sent);
        Assert.Equal([1, 2, 3], timeouts.Select(e => e.Attempt));
        Assert.Equal([false, false, true], timeouts.Select(e => e.IsFinal));
        Assert.All(timeouts, e => Assert.True(e.RequestSent));
        Assert.All(timeouts, e => Assert.Equal((5, FinsCommand.Write, 0), (e.MessageID, e.Command, e.WrittenCount)));
        Assert.Empty(fcss);
    }

    [Fact]
    public void Serial_Process_FcsErrorThenNoResponse_SeparateEvents()
    {
        plc.CorruptFcs = 1;
        var m = Manual();
        m.ManualWrite(5, "D0", 1, repeatCount: 1);
        var timeouts = new List<FinsSerial.AttemptFailedEventArgs>();
        var fcss = new List<FinsSerial.AttemptFailedEventArgs>();
        plc.SilentAt = 2;   // 2차(마지막) 시도는 무응답이므로 FCS 오류는 1차 시도에서 난다
        var (sent, status) = Run(m, Take(m), timeouts, fcss);
        Assert.Equal(2, sent);
        var c = Assert.Single(fcss);
        Assert.Equal((1, false, true, 5, 0), (c.Attempt, c.IsFinal, c.RequestSent, c.MessageID, c.WrittenCount));
        var t = Assert.Single(timeouts);
        Assert.Equal((2, true), (t.Attempt, t.IsFinal));
        Assert.Equal(FinsStatus.Invalid, status);
    }

    [Fact]
    public void Serial_Process_FcsErrorOnFinalAttempt_RaisesFcsErrorReceivedWithIsFinal()
    {
        plc.CorruptFcs = 2;
        var m = Manual();
        m.ManualWrite(5, "D0", 1, repeatCount: 1);
        var timeouts = new List<FinsSerial.AttemptFailedEventArgs>();
        var fcss = new List<FinsSerial.AttemptFailedEventArgs>();
        var (sent, status) = Run(m, Take(m), timeouts, fcss);
        Assert.Equal(2, sent);
        Assert.Equal([1, 2], fcss.Select(e => e.Attempt));
        Assert.Equal([false, true], fcss.Select(e => e.IsFinal));
        Assert.All(fcss, e => Assert.True(e.RequestSent));
        Assert.Empty(timeouts);
        Assert.Equal(FinsStatus.FcsError, status);
    }

    [Fact]
    public void Serial_Process_Success_NoFailureEvents()
    {
        var m = Manual();
        m.ManualWrite(5, "D0", 99);
        var timeouts = new List<FinsSerial.AttemptFailedEventArgs>();
        var fcss = new List<FinsSerial.AttemptFailedEventArgs>();
        int ok = 0;
        m.WriteReceived += (s, e) => ok++;
        var (_, status) = Run(m, Take(m), timeouts, fcss);
        Assert.Equal((FinsStatus.Ack, 1), (status, ok));
        Assert.Empty(timeouts);
        Assert.Empty(fcss);
        Assert.Equal(99, plc.Get("D0"));
    }

    [Fact]
    public void Serial_Process_Nak_NotRetried_NoFailureEvents()
    {
        plc.EndCode = 0x2102;
        var m = Manual();
        m.ManualWrite(5, "D0", 1, repeatCount: 2);
        var timeouts = new List<FinsSerial.AttemptFailedEventArgs>();
        var fcss = new List<FinsSerial.AttemptFailedEventArgs>();
        var naks = new List<FinsSerial.NAKEventArgs>();
        m.NAKReceived += (s, e) => naks.Add(e);
        var (sent, status) = Run(m, Take(m), timeouts, fcss);
        Assert.Equal((1, FinsStatus.Nak), (sent, status));
        Assert.Equal((0x2102, 0), (Assert.Single(naks).ErrorCode, naks[0].WrittenCount));
        Assert.Empty(timeouts);
        Assert.Empty(fcss);
    }

    [Fact]
    public void Serial_Process_SendThrows_FinalNotSent_Rethrown()
    {
        var m = Manual();
        m.ManualWrite(5, "D0", 1, repeatCount: 2);
        var timeouts = new List<FinsSerial.AttemptFailedEventArgs>();
        m.TimeoutReceived += (s, e) => timeouts.Add(e);
        Assert.Throws<IOException>(() => m.Process(Take(m), req => throw new IOException(), t => null));
        var e = Assert.Single(timeouts);
        Assert.Equal((1, true, false, 0), (e.Attempt, e.IsFinal, e.RequestSent, e.WrittenCount));
    }

    [Fact]
    public void Serial_Process_BlockWrite_SecondChunkNak_WrittenCountIsFirstChunk()
    {
        plc.EndCode = 0x2102;
        plc.EndCodeAfter = 1;
        var m = Manual();
        m.ManualWriteBlock(2, "D0", Words(), repeatCount: 3);   // NAK은 재시도하지 않는다
        var timeouts = new List<FinsSerial.AttemptFailedEventArgs>();
        var fcss = new List<FinsSerial.AttemptFailedEventArgs>();
        var naks = new List<FinsSerial.NAKEventArgs>();
        int written = 0;
        m.NAKReceived += (s, e) => naks.Add(e);
        m.WriteReceived += (s, e) => written++;
        var (sent, status) = Run(m, Take(m), timeouts, fcss);
        Assert.Equal((2, FinsStatus.Nak), (sent, status));
        var e = Assert.Single(naks);
        Assert.Equal((2, FinsCommand.WriteBlock, 0x2102, Chunk), (e.MessageID, e.Command, e.ErrorCode, e.WrittenCount));
        Assert.Equal(0, written);
        Assert.Empty(timeouts);
        Assert.Equal(Words(Chunk).Select(v => (ushort)v), plc.GetWords("D0", Chunk));
    }

    [Fact]
    public void Serial_Process_BlockWrite_FirstChunkNak_WrittenCountZero()
    {
        plc.EndCode = 0x2102;
        var m = Manual();
        m.ManualWriteBlock(2, "D0", Words());
        var naks = new List<FinsSerial.NAKEventArgs>();
        m.NAKReceived += (s, e) => naks.Add(e);
        Run(m, Take(m), [], []);
        Assert.Equal(0, Assert.Single(naks).WrittenCount);
    }

    [Fact]
    public void Serial_Process_BlockWrite_SecondChunkNoResponse_TimeoutWrittenCountIsFirstChunk()
    {
        plc.SilentAt = 2;
        var m = Manual();
        m.ManualWriteBlock(2, "D0", Words());
        var timeouts = new List<FinsSerial.AttemptFailedEventArgs>();
        Run(m, Take(m), timeouts, []);
        var e = Assert.Single(timeouts);
        Assert.Equal((2, FinsCommand.WriteBlock, 1, true, true, Chunk), (e.MessageID, e.Command, e.Attempt, e.IsFinal, e.RequestSent, e.WrittenCount));
    }

    [Fact]
    public void Serial_Process_BlockWrite_SecondChunkFcsError_FcsEventCarriesWrittenCount()
    {
        // 첫 조각 응답은 정상, 둘째 조각 응답의 FCS가 틀림
        var m = Manual();
        m.ManualWriteBlock(2, "D0", Words());
        var timeouts = new List<FinsSerial.AttemptFailedEventArgs>();
        var fcss = new List<FinsSerial.AttemptFailedEventArgs>();
        m.TimeoutReceived += (s, e) => timeouts.Add(e);
        m.FcsErrorReceived += (s, e) => fcss.Add(e);
        int n = 0;
        byte[]? reply = null;
        m.Process(Take(m), req =>
        {
            if (++n == 2) plc.CorruptFcs = 1;
            reply = plc.HandleHostLink(req);
        }, t => reply);
        var e = Assert.Single(fcss);
        Assert.Equal((1, true, true, Chunk), (e.Attempt, e.IsFinal, e.RequestSent, e.WrittenCount));
        Assert.Empty(timeouts);
    }

    [Fact]
    public void Serial_Process_BlockWrite_SendThrowsOnThirdChunk_FinalTimeoutCarriesWrittenCount()
    {
        var m = Manual();
        m.ManualWriteBlock(2, "D0", Words());
        var timeouts = new List<FinsSerial.AttemptFailedEventArgs>();
        m.TimeoutReceived += (s, e) => timeouts.Add(e);
        int n = 0;
        byte[]? reply = null;
        Assert.Throws<IOException>(() => m.Process(Take(m), req =>
        {
            if (++n == 3) throw new IOException();
            reply = plc.HandleHostLink(req);
        }, t => reply));
        var e = Assert.Single(timeouts);
        Assert.Equal((1, true, 2 * Chunk), (e.Attempt, e.IsFinal, e.WrittenCount));
    }

    [Fact]
    public void Serial_Process_BlockWrite_Retry_RestartsFromFirstChunk_WrittenCountPerAttempt()
    {
        plc.SilentAt = 2;   // 1차 시도: 둘째 조각 무응답. 2차 시도는 첫 조각부터 다시 보내 모두 성공
        var m = Manual();
        m.ManualWriteBlock(2, "D0", Words(), repeatCount: 1);
        var log = new List<string>();
        m.TimeoutReceived += (s, e) => log.Add($"Timeout({e.Attempt},{e.IsFinal},{e.WrittenCount})");
        m.WriteReceived += (s, e) => log.Add("Write");
        int sent = 0;
        byte[]? reply = null;
        m.Process(Take(m), req => { sent++; reply = plc.HandleHostLink(req); }, t => reply);
        Assert.Equal([$"Timeout(1,False,{Chunk})", "Write"], log);
        Assert.Equal(5, sent);
        Assert.Equal(Words().Select(v => (ushort)v), plc.GetWords("D0", Total));
    }

    [Fact]
    public void Serial_Process_ReadBlock_SecondChunkNak_WrittenCountZero()
    {
        plc.EndCode = 0x1103;
        plc.EndCodeAfter = 1;
        var m = Manual();
        m.ManualReadBlock(4, "D0", Total);
        var naks = new List<FinsSerial.NAKEventArgs>();
        m.NAKReceived += (s, e) => naks.Add(e);
        Run(m, Take(m), [], []);
        Assert.Equal((FinsCommand.ReadBlock, 0), (Assert.Single(naks).Command, naks[0].WrittenCount));
    }

    // 여러 Process 호출이 같은 객체를 쓰므로 시도마다 requestSent가 새로 시작하는지 확인: 첫 작업이 보낸 뒤 실패해도 다음 작업의 첫 송신 실패는 RequestSent false
    [Fact]
    public void Serial_Process_RequestSent_ResetsPerJob()
    {
        var m = Manual();
        m.ManualWrite(1, "D0", 1);
        m.ManualWrite(2, "D1", 2);
        var timeouts = new List<FinsSerial.AttemptFailedEventArgs>();
        m.TimeoutReceived += (s, e) => timeouts.Add(e);
        m.Process(Take(m), req => { }, t => null);
        Assert.Throws<IOException>(() => m.Process(Take(m), req => throw new IOException(), t => null));
        Assert.Equal([(1, true), (2, false)], timeouts.Select(e => (e.MessageID, e.RequestSent)));
    }
}
