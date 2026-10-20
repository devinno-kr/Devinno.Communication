using System.Collections.Concurrent;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Serial;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Secs.Serial;

/// 메모리 회선의 다른 끝을 테스트가 직접 움직여 SECS-I 회선 규칙(핸드셰이크·재시도·경합·중복·T1/T2/T4·인터리브)을 확인한다
public class SecsSerialPeerTests
{
    sealed class Ctx : IDisposable
    {
        public readonly SecsSerial Us;
        public readonly MemoryLine Peer;
        public readonly ConcurrentQueue<string> Errors = new();
        public readonly BlockingCollection<SecsMessage> Primaries = [];

        public Ctx(SecsRole role, Action<SecsSerial>? setup = null, bool echo = true)
        {
            var (a, b) = MemoryLine.Pair();
            Peer = b;
            Us = SecsSerialTests.Make(role, a);
            Us.ProtocolError += (s, e) => Errors.Enqueue(e.Reason);
            Us.PrimaryReceived += (s, e) =>
            {
                Primaries.Add(e.Message);
                if (echo) SecsSerialTests.Echo(s, e);
            };
            setup?.Invoke(Us);
            Us.Start();
            SpinWait.SpinUntil(() => Us.IsOpen, 3000);
        }

        /// 상대(테스트)가 보내는 메시지. 상대가 Equipment면 R비트 1
        public SecsMessage From(SecsMessage m, uint sys, int deviceId = 1) => m.WithHeader(sys, deviceId);

        public void Dispose() => Us.Dispose();
    }

    static void WaitError(Ctx c, string contains)
        => Assert.True(SpinWait.SpinUntil(() => c.Errors.Any(e => e.Contains(contains)), 3000), $"\"{contains}\" 알림이 없습니다: {string.Join(" | ", c.Errors)}");

    #region 보내기: 재시도·RetryLimit·경합
    [Fact]
    public async Task NoEot_RetriesFromEnq_ThenIOException_LinkKept()
    {
        using var c = new Ctx(SecsRole.Host, u => { u.T2 = 100; u.RetryLimit = 2; });
        var task = c.Us.SendAsync(new SecsMessage(1, 1, true));
        for (int i = 0; i < 3; i++) c.Peer.Expect(SecsIBlock.ENQ);   // 1 + RetryLimit번
        var ex = await Assert.ThrowsAsync<IOException>(() => task);
        Assert.Contains("재시도 2번", ex.Message);
        Assert.Equal(-1, c.Peer.ReadByte(300));                       // 더 보내지 않음
        Assert.True(c.Us.IsOpen);

        c.Us.T2 = 1000;
        var next = c.Us.SendAsync(new SecsMessage(1, 1, true));       // 회선은 그대로 → 다음 메시지는 된다
        var (h, _) = c.Peer.ReceiveBlock();
        c.Peer.SendMessage(c.From(new SecsMessage(1, 2, false), h.SystemBytes), rbit: true);
        Assert.NotNull(await next);
    }

    [Fact]
    public async Task NakEveryTime_IOExceptionAfterRetryLimit()
    {
        using var c = new Ctx(SecsRole.Host, u => { u.RetryLimit = 1; });
        var task = c.Us.SendAsync(new SecsMessage(1, 1, true));
        for (int i = 0; i < 2; i++)
        {
            c.Peer.Expect(SecsIBlock.ENQ);
            c.Peer.Write([SecsIBlock.EOT]);
            int len = c.Peer.ReadByte(1000);
            for (int k = 0; k < len + 2; k++) c.Peer.ReadByte(1000);
            c.Peer.Write([SecsIBlock.NAK]);
        }
        await Assert.ThrowsAsync<IOException>(() => task);
        WaitError(c, "0x15");
    }

    [Fact]
    public async Task Contention_HostIsSlave_YieldsThenResends()
    {
        using var c = new Ctx(SecsRole.Host);
        var task = c.Us.SendAsync(new SecsMessage(1, 1, true));
        c.Peer.Expect(SecsIBlock.ENQ);
        // 상대도 동시에 ENQ → slave(Host)는 EOT로 양보하고 상대 블록을 받는다
        var theirs = SecsIBlock.Split(c.From(new SecsMessage(6, 11, false, SecsItem.U4(7)), 0x500), rbit: true)[0];
        c.Peer.Write([SecsIBlock.ENQ]);
        c.Peer.Expect(SecsIBlock.EOT);
        c.Peer.Write(theirs);
        c.Peer.Expect(SecsIBlock.ACK);
        // 그 뒤 Host가 자기 메시지를 ENQ부터 다시
        var (h, _) = c.Peer.ReceiveBlock();
        Assert.Equal((1, 1, false), (h.Stream, h.Function, h.RBit));
        c.Peer.SendMessage(c.From(new SecsMessage(1, 2, false), h.SystemBytes), rbit: true);
        Assert.NotNull(await task);
        Assert.True(c.Primaries.TryTake(out var m, 3000));
        Assert.Equal((6, 11), (m!.Stream, m.Function));
    }

    [Fact]
    public async Task Contention_EquipmentIsMaster_IgnoresEnqKeepsWaiting()
    {
        using var c = new Ctx(SecsRole.Equipment, u => u.T2 = 5000);   // 부하가 있어도 T2 재시도가 끼어들지 않게
        var task = c.Us.SendAsync(new SecsMessage(6, 11, false, SecsItem.U4(1)));
        c.Peer.Expect(SecsIBlock.ENQ);
        c.Peer.Write([SecsIBlock.ENQ]);                       // 상대 ENQ는 무시돼야 함 (EOT가 오면 안 됨)
        Assert.Equal(-1, c.Peer.ReadByte(200));
        c.Peer.Write([SecsIBlock.EOT]);                       // 상대가 양보
        int len = c.Peer.ReadByte(1000);
        var buf = new byte[len + 2];
        for (int i = 0; i < buf.Length; i++) buf[i] = (byte)c.Peer.ReadByte(1000);
        c.Peer.Write([SecsIBlock.ACK]);
        var h = SecsIHeader.Parse(buf.AsSpan(0, 10));
        Assert.Equal((6, 11, true, 1), (h.Stream, h.Function, h.RBit, h.DeviceID));   // Equipment가 보낸 블록은 R비트 1
        Assert.Null(await task);
    }

    [Fact]
    public async Task IsMasterSetting_OverridesRole()
    {
        using var c = new Ctx(SecsRole.Host, u => { u.IsMaster = true; u.T2 = 5000; });
        var task = c.Us.SendAsync(new SecsMessage(6, 11, false));
        c.Peer.Expect(SecsIBlock.ENQ);
        c.Peer.Write([SecsIBlock.ENQ]);
        Assert.Equal(-1, c.Peer.ReadByte(200));                // master라서 양보하지 않음
        c.Peer.Write([SecsIBlock.EOT]);
        int len = c.Peer.ReadByte(1000);
        for (int i = 0; i < len + 2; i++) c.Peer.ReadByte(1000);
        c.Peer.Write([SecsIBlock.ACK]);
        Assert.Null(await task);
    }
    #endregion

    #region 받기: NAK·중복·T1/T2/T4·인터리브·블록 번호
    [Fact]
    public void DuplicateBlock_AckedAndDropped()
    {
        using var c = new Ctx(SecsRole.Host);
        var block = SecsIBlock.Split(c.From(new SecsMessage(6, 11, false, SecsItem.U4(1)), 0x10), rbit: true)[0];
        Assert.Equal(SecsIBlock.ACK, c.Peer.SendBlock(block));
        Assert.Equal(SecsIBlock.ACK, c.Peer.SendBlock(block));   // ACK가 사라져 상대가 다시 보낸 경우
        Assert.True(c.Primaries.TryTake(out _, 3000));
        Assert.False(c.Primaries.TryTake(out _, 300));
        WaitError(c, "중복 블록");
    }

    [Fact]
    public void DuplicateDetectionOff_DeliversTwice()
    {
        using var c = new Ctx(SecsRole.Host, u => u.DuplicateDetection = false);
        var block = SecsIBlock.Split(c.From(new SecsMessage(6, 11, false, SecsItem.U4(1)), 0x10), rbit: true)[0];
        c.Peer.SendBlock(block);
        c.Peer.SendBlock(block);
        Assert.True(c.Primaries.TryTake(out _, 3000));
        Assert.True(c.Primaries.TryTake(out _, 3000));
    }

    [Fact]
    public void BadChecksum_Nak()
    {
        using var c = new Ctx(SecsRole.Host);
        var block = SecsIBlock.Split(c.From(new SecsMessage(6, 11, false), 0x11), rbit: true)[0];
        block[^1] ^= 0x01;
        Assert.Equal(SecsIBlock.NAK, c.Peer.SendBlock(block));
        WaitError(c, "체크섬");
        Assert.False(c.Primaries.TryTake(out _, 200));
    }

    [Fact]
    public void LengthOutOfRange_DrainThenNak()
    {
        using var c = new Ctx(SecsRole.Host);
        c.Peer.Write([SecsIBlock.ENQ]);
        c.Peer.Expect(SecsIBlock.EOT);
        c.Peer.Write([5, 1, 2, 3]);
        c.Peer.Expect(SecsIBlock.NAK);
        WaitError(c, "10~254");
    }

    [Fact]
    public void NoLengthAfterEot_T2_Nak()
    {
        using var c = new Ctx(SecsRole.Host);
        c.Peer.Write([SecsIBlock.ENQ]);
        c.Peer.Expect(SecsIBlock.EOT);
        c.Peer.Expect(SecsIBlock.NAK, 3000);                     // T2 = 1000
        WaitError(c, "길이 바이트가 없습니다");
    }

    [Fact]
    public void InterCharacterGap_T1_Nak()
    {
        using var c = new Ctx(SecsRole.Host);
        var block = SecsIBlock.Split(c.From(new SecsMessage(6, 11, false), 0x12), rbit: true)[0];
        c.Peer.Write([SecsIBlock.ENQ]);
        c.Peer.Expect(SecsIBlock.EOT);
        c.Peer.Write(block[..5]);                                // 일부만 보내고 멈춤
        c.Peer.Expect(SecsIBlock.NAK, 3000);                     // T1 = 500
        WaitError(c, "T1");
    }

    [Fact]
    public void MultiBlock_T4Gap_PartialDropped_NextMessageFine()
    {
        using var c = new Ctx(SecsRole.Host, u => u.T4 = 300);
        var blocks = SecsIBlock.Split(c.From(new SecsMessage(6, 11, false, SecsItem.B(new byte[300])), 0x20), rbit: true);
        Assert.Equal(2, blocks.Count);
        Assert.Equal(SecsIBlock.ACK, c.Peer.SendBlock(blocks[0]));
        Thread.Sleep(600);
        WaitError(c, "T4");
        Assert.Equal(SecsIBlock.ACK, c.Peer.SendBlock(blocks[1]));   // 홀로 온 2번 블록은 첫 블록이 아니므로 버림
        WaitError(c, "첫 블록 번호가 2");
        c.Peer.SendMessage(c.From(new SecsMessage(6, 11, false, SecsItem.U4(5)), 0x21), rbit: true);
        Assert.True(c.Primaries.TryTake(out var m, 3000));
        Assert.Equal(SecsItem.U4(5), m!.Item);
    }

    [Fact]
    public void Interleave_OtherMessageBlock_AbandonsPartial()
    {
        using var c = new Ctx(SecsRole.Host);
        var a = SecsIBlock.Split(c.From(new SecsMessage(6, 11, false, SecsItem.B(new byte[300])), 0x30), rbit: true);
        c.Peer.SendBlock(a[0]);
        c.Peer.SendMessage(c.From(new SecsMessage(5, 1, false, SecsItem.U4(9)), 0x31), rbit: true);
        Assert.True(c.Primaries.TryTake(out var m, 3000));
        Assert.Equal((5, 1), (m!.Stream, m.Function));
        WaitError(c, "다른 메시지의 블록");
        Assert.False(c.Primaries.TryTake(out _, 200));
    }

    [Fact]
    public void SingleBlockNumberedZero_Accepted()
    {
        using var c = new Ctx(SecsRole.Host);
        var h = new SecsIHeader(true, 1, false, 6, 11, true, 0, 0x40);
        c.Peer.SendBlock(SecsIBlock.Block(h, SecsCodec.Encode(SecsItem.U4(3))));
        Assert.True(c.Primaries.TryTake(out var m, 3000));
        Assert.Equal(SecsItem.U4(3), m!.Item);
    }

    [Fact]
    public async Task MultiBlockReply_T3CoversFirstBlockOnly_RestUsesT4()
    {
        using var c = new Ctx(SecsRole.Host, u => { u.T3 = 1000; u.T4 = 3000; });
        var task = c.Us.SendAsync(new SecsMessage(7, 5, true));
        var (h, _) = c.Peer.ReceiveBlock();
        var reply = SecsIBlock.Split(c.From(new SecsMessage(7, 6, false, SecsItem.B(new byte[600])), h.SystemBytes), rbit: true);
        Assert.Equal(3, reply.Count);
        c.Peer.SendBlock(reply[0]);                       // T3 안에 첫 블록
        Thread.Sleep(1300);                               // T3는 넘지만 T4 안
        c.Peer.SendBlock(reply[1]);
        Thread.Sleep(1300);
        c.Peer.SendBlock(reply[2]);
        Assert.Equal(SecsItem.B(new byte[600]), (await task)!.Item);
    }

    [Fact]
    public async Task MultiBlockReply_T4Expires_SendTimesOut()
    {
        using var c = new Ctx(SecsRole.Host, u => { u.T3 = 2000; u.T4 = 300; });
        var task = c.Us.SendAsync(new SecsMessage(7, 5, true));
        var (h, _) = c.Peer.ReceiveBlock();
        var reply = SecsIBlock.Split(c.From(new SecsMessage(7, 6, false, SecsItem.B(new byte[300])), h.SystemBytes), rbit: true);
        c.Peer.SendBlock(reply[0]);
        var ex = await Assert.ThrowsAsync<SecsTimeoutException>(() => task);
        Assert.Contains("T4", ex.Message);
    }
    #endregion

    #region Equipment 자동 S9
    [Fact]
    public void Equipment_UndecodableBody_S9F7()
    {
        using var c = new Ctx(SecsRole.Equipment);
        var h = new SecsIHeader(false, 1, true, 1, 3, true, 1, 0x50);
        c.Peer.SendBlock(SecsIBlock.Block(h, Convert.FromHexString("A903000102")));
        var (s9h, s9) = c.Peer.ReceiveMessage();
        Assert.Equal((9, 7, true), (s9.Stream, s9.Function, s9h.RBit));
        Assert.Equal(SecsItem.B(h.ToArray()), s9.Item);
    }

    [Fact]
    public void Equipment_DeviceIdMismatch_S9F1()
    {
        using var c = new Ctx(SecsRole.Equipment);
        var wire = c.From(new SecsMessage(1, 1, true), 0x51, deviceId: 9);
        c.Peer.SendMessage(wire, rbit: false);
        var (_, s9) = c.Peer.ReceiveMessage();
        Assert.Equal((9, 1, 1), (s9.Stream, s9.Function, s9.DeviceID));
        Assert.Equal(SecsItem.B(SecsIBlock.FirstHeader(wire, false, 1).ToArray()), s9.Item);
        Assert.False(c.Primaries.TryTake(out _, 200));
    }

    [Fact]
    public async Task Equipment_T3_SendsS9F9WithFirstBlockHeader()
    {
        using var c = new Ctx(SecsRole.Equipment, u => u.T3 = 300);
        var task = c.Us.SendAsync(new SecsMessage(6, 11, true, SecsItem.L()));
        var (h, _) = c.Peer.ReceiveBlock();
        await Assert.ThrowsAsync<SecsTimeoutException>(() => task);
        var (_, s9) = c.Peer.ReceiveMessage();
        Assert.Equal((9, 9), (s9.Stream, s9.Function));
        Assert.Equal(SecsItem.B(h.ToArray()), s9.Item);
    }
    #endregion

    #region 리뷰 수정: 보내는 중 끊김·재연결 경계·응답 중단·drain 한계
    [Fact]
    public async Task Stop_WhileBlockInFlight_SendAsyncFaultsDisconnected()
    {
        using var c = new Ctx(SecsRole.Host, u => u.T2 = 5000);
        var task = c.Us.SendAsync(new SecsMessage(1, 1, true));
        c.Peer.Expect(SecsIBlock.ENQ);                       // EOT를 주지 않아 블록 송신이 진행 중
        c.Us.Stop();
        await Assert.ThrowsAsync<SecsDisconnectedException>(() => task).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task LineThrowsWhileSending_SendAsyncFaultsDisconnected()
    {
        volatile_fail = false;
        using var x = new SecsSerial { T2 = 5000, OpenLine = () => new SecsSerialLine(t => { if (volatile_fail) throw new IOException("끊김"); Thread.Sleep(Math.Min(t, 20)); return -1; }, _ => { }, () => { }) };
        x.Start();
        Assert.True(SpinWait.SpinUntil(() => x.IsOpen, 3000));
        var task = x.SendAsync(new SecsMessage(1, 1, true));
        volatile_fail = true;
        await Assert.ThrowsAsync<SecsDisconnectedException>(() => task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(SpinWait.SpinUntil(() => !x.IsOpen, 3000));
    }
    static volatile bool volatile_fail;

    [Fact]
    public async Task Transmit_AfterLinkDown_OrFromPreviousConnection_FailsImmediately()
    {
        using var c = new Ctx(SecsRole.Host);
        var m = typeof(SecsSerial).GetMethod("Transmit", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var wire = new SecsMessage(1, 1, true).WithHeader(1, 1);
        // 지난 연결 번호(0)로 들어온 메시지는 새 연결에서 보내지 않는다
        await Assert.ThrowsAsync<SecsDisconnectedException>(() => (Task)m.Invoke(c.Us, [wire, 0])!);
        c.Us.Stop();
        await Assert.ThrowsAsync<SecsDisconnectedException>(() => (Task)m.Invoke(c.Us, [wire, 1])!);
    }

    [Fact]
    public async Task MultiBlockReply_BodyUndecodable_SendEndsWithTimeout()
    {
        using var c = new Ctx(SecsRole.Host);
        var task = c.Us.SendAsync(new SecsMessage(7, 5, true));
        var (h, _) = c.Peer.ReceiveBlock();
        Assert.Equal(SecsIBlock.ACK, c.Peer.SendBlock(SecsIBlock.Block(new SecsIHeader(true, 1, false, 7, 6, false, 1, h.SystemBytes), Convert.FromHexString("A903"))));
        Assert.Equal(SecsIBlock.ACK, c.Peer.SendBlock(SecsIBlock.Block(new SecsIHeader(true, 1, false, 7, 6, true, 2, h.SystemBytes), Convert.FromHexString("000102"))));
        await Assert.ThrowsAsync<SecsTimeoutException>(() => task).WaitAsync(TimeSpan.FromSeconds(2));   // T3(3000)를 기다리지 않는다
    }

    [Fact]
    public async Task Equipment_MultiBlockReply_DeviceIdMismatch_SendEndsWithTimeout()
    {
        using var c = new Ctx(SecsRole.Equipment);
        var task = c.Us.SendAsync(new SecsMessage(6, 11, true, SecsItem.L()));
        var (h, _) = c.Peer.ReceiveBlock();
        c.Peer.SendMessage(c.From(new SecsMessage(6, 12, false, SecsItem.B(new byte[300])), h.SystemBytes, deviceId: 9), rbit: false);
        await Assert.ThrowsAsync<SecsTimeoutException>(() => task).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Drain_PeerKeepsStreaming_GivesUpAfterT2_ThenNak()
    {
        using var c = new Ctx(SecsRole.Host, u => u.T2 = 300);
        c.Peer.Write([SecsIBlock.ENQ]);
        c.Peer.Expect(SecsIBlock.EOT);
        c.Peer.Write([5, 1, 2, 3]);
        using var stop = new CancellationTokenSource();
        var streamer = Task.Run(() => { while (!stop.IsCancellationRequested) { c.Peer.Write([0]); Thread.Sleep(50); } });
        try { c.Peer.Expect(SecsIBlock.NAK, 1500); }
        finally { stop.Cancel(); await streamer; }
    }
    [Fact]
    public void PrimaryReceived_ReceivedHeader_IsFirstBlockHeaderPeerSent()
    {
        var headers = new BlockingCollection<byte[]>();
        using var c = new Ctx(SecsRole.Host, u => u.PrimaryReceived += (s, e) => headers.Add(e.ReceivedHeader), echo: false);
        var wire = c.From(new SecsMessage(6, 11, true, SecsItem.B(new byte[300])), 0x0A0B0C0D, deviceId: 1);   // 블록 2개
        var blocks = SecsIBlock.Split(wire, rbit: true);
        Assert.Equal(2, blocks.Count);
        c.Peer.SendMessage(wire, rbit: true);
        Assert.True(headers.TryTake(out var h, 3000));
        Assert.Equal(blocks[0].AsSpan(1, 10).ToArray(), h);   // 길이 바이트 뒤 첫 블록 헤더 10바이트 그대로
    }
    #endregion
}
