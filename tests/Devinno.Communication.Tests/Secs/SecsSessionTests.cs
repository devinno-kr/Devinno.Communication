using System.Buffers.Binary;
using System.Collections.Concurrent;
using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Tests.Secs;

/// SecsSession(트랜잭션·디스패처)을 가짜 송신 함수로 직접 시험한다. 전송 계층(HSMS·SECS-I) 없이 규칙만 본다.
public class SecsSessionTests
{
    sealed class Harness : IDisposable
    {
        public SecsRole Role = SecsRole.Host;
        public int DeviceID = 1;
        public int T3 = 5000;
        public readonly BlockingCollection<SecsMessage> Sent = [];
        public readonly ConcurrentQueue<string> Errors = new();
        public Action<SecsPrimaryEventArgs>? OnPrimary;
        public readonly SecsSession Session;
        readonly CancellationTokenSource ticker = new();

        public Harness(bool connect = true)
        {
            Session = new SecsSession(() => Role, () => DeviceID, () => T3, Header, e => OnPrimary?.Invoke(e), Errors.Enqueue, "test");
            Session.Start();
            if (connect) Session.Connected(Transmit);
            // I/O 스레드 대신 T3 검사를 주기적으로 부른다
            new Thread(() => { while (!ticker.IsCancellationRequested) { Session.CheckTimeouts(); Thread.Sleep(10); } }) { IsBackground = true }.Start();
        }

        public Task Transmit(SecsMessage wire, CancellationToken token) { Sent.Add(wire); return Task.CompletedTask; }

        /// 테스트용 10바이트 헤더: 장치 ID 2, W+Stream, Function, 0, 0, System Bytes 4 (HSMS 데이터 헤더와 같은 자리)
        public static byte[] Header(SecsMessage m)
        {
            var h = new byte[10];
            BinaryPrimitives.WriteUInt16BigEndian(h, (ushort)m.DeviceID);
            h[2] = (byte)((m.ReplyExpected ? 0x80 : 0) | m.Stream);
            h[3] = (byte)m.Function;
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(6), m.SystemBytes);
            return h;
        }

        public SecsMessage NextSent(int timeoutMs = 3000)
        {
            Assert.True(Sent.TryTake(out var m, timeoutMs), "보낸 메시지가 없습니다.");
            return m!;
        }

        public void AssertNothingSent(int waitMs = 100) => Assert.False(Sent.TryTake(out var m, waitMs), $"보내지 않아야 하는데 {m}");

        /// 상대가 보낸 메시지를 받은 것처럼 넣는다
        public void Receive(SecsMessage m, uint sys, int? deviceId = null)
        {
            var wire = m.WithHeader(sys, deviceId ?? DeviceID);
            Session.Received(wire, Header(wire));
        }

        public void Dispose()
        {
            ticker.Cancel();
            Session.Stop(3000);
        }
    }

    static SecsMessage S1F1 => new(1, 1, true);
    static SecsMessage S1F2 => new(1, 2, false, SecsItem.L(SecsItem.A("MDLN"), SecsItem.A("1.0")));

    [Fact]
    public void Stop_WithOldStartGeneration_DoesNotStopNewerRun()
    {
        var session = new SecsSession(() => SecsRole.Host, () => 0, () => 5000, Harness.Header, _ => { }, _ => { }, "test");
        int first = session.Start();
        session.Stop(0);
        int second = session.Start();
        Assert.NotEqual(first, second);
        session.Connected((w, t) => Task.CompletedTask);
        session.Stop(0, first);                       // 지난 실행의 늦은 정리: 새 실행은 그대로
        Assert.True(session.IsConnected);
        using var ran = new ManualResetEventSlim();
        session.Post(ran.Set);
        Assert.True(ran.Wait(3000), "새 실행의 디스패처가 멈췄습니다.");
        session.Stop(3000, second);
        Assert.False(session.IsConnected);
    }

    #region 보내기와 짝짓기
    [Fact]
    public async Task Send_ReplyMatchedBySystemBytes()
    {
        using var h = new Harness();
        var task = h.Session.SendAsync(S1F1, default);
        var wire = h.NextSent();
        Assert.Equal((1, 1, true, 1u, 1), (wire.Stream, wire.Function, wire.ReplyExpected, wire.SystemBytes, wire.DeviceID));
        h.Receive(S1F2, wire.SystemBytes);
        var reply = await task;
        Assert.Equal(S1F2.Item, reply!.Item);
        Assert.Equal(wire.SystemBytes, reply.SystemBytes);
        Assert.Equal(0, h.Session.PendingCount);
    }

    [Fact]
    public void Send_Sync_WaitsForReply()
    {
        using var h = new Harness();
        var peer = new Thread(() => { var w = h.NextSent(); h.Receive(S1F2, w.SystemBytes); });
        peer.Start();
        var r = h.Session.Send(S1F1);
        Assert.Equal(S1F2.Item, r!.Item);
        Assert.Null(h.Session.Send(new SecsMessage(6, 11, false)));
        h.Session.Connected((w, t) => Task.FromException(new IOException("재시도 한계")));
        Assert.Throws<IOException>(() => h.Session.Send(S1F1));
        Assert.Equal(0, h.Session.PendingCount);
    }

    [Fact]
    public async Task Send_NoReplyExpected_ReturnsNullAfterTransmit()
    {
        using var h = new Harness();
        Assert.Null(await h.Session.SendAsync(new SecsMessage(6, 11, false, SecsItem.U4(1)), default));
        Assert.Equal(0, h.Session.PendingCount);
        Assert.False(h.NextSent().ReplyExpected);
    }

    [Fact]
    public async Task Send_ArgumentAndStateChecks()
    {
        using var h = new Harness(connect: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Session.SendAsync(S1F1, default));
        h.Session.Connected(h.Transmit);
        await Assert.ThrowsAsync<ArgumentException>(() => h.Session.SendAsync(new SecsMessage(1, 2, false), default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => h.Session.SendAsync(null!, default));
    }

    [Fact]
    public async Task SystemBytes_IncrementPerSend_RestartPerConnection_OriginalUntouched()
    {
        using var h = new Harness();
        var msg = new SecsMessage(6, 11, false);
        await h.Session.SendAsync(msg, default);
        await h.Session.SendAsync(msg, default);
        Assert.Equal(1u, h.NextSent().SystemBytes);
        Assert.Equal(2u, h.NextSent().SystemBytes);
        Assert.Equal(0u, msg.SystemBytes);
        h.Session.Disconnected();
        h.Session.Connected(h.Transmit);
        await h.Session.SendAsync(msg, default);
        Assert.Equal(1u, h.NextSent().SystemBytes);
    }

    [Fact]
    public async Task Concurrent_Sends_AllMatched()
    {
        using var h = new Harness();
        var tasks = Enumerable.Range(0, 50).Select(i => Task.Run(() => h.Session.SendAsync(new SecsMessage(1, 3, true, SecsItem.U4((uint)i)), default))).ToList();
        var wires = Enumerable.Range(0, 50).Select(_ => h.NextSent()).ToList();
        Assert.Equal(50, wires.Select(w => w.SystemBytes).Distinct().Count());
        foreach (var w in wires) h.Receive(new SecsMessage(1, 4, false, w.Item), w.SystemBytes);
        var replies = await Task.WhenAll(tasks);
        for (int i = 0; i < 50; i++) Assert.Equal(SecsItem.U4((uint)i), replies[i]!.Item);
    }

    [Fact]
    public async Task UnmatchedReply_IsReported_AndIgnored()
    {
        using var h = new Harness();
        var task = h.Session.SendAsync(S1F1, default);
        var wire = h.NextSent();
        h.Receive(S1F2, wire.SystemBytes + 100);                       // 다른 System Bytes
        h.Receive(new SecsMessage(2, 2, false), wire.SystemBytes);       // 같은 System Bytes, 다른 Stream
        h.Receive(new SecsMessage(1, 4, false), wire.SystemBytes);       // 같은 Stream, 1차 + 1이 아님
        Assert.Equal(3, h.Errors.Count(e => e.Contains("짝이 없는 응답")));
        Assert.False(task.IsCompleted);
        h.Receive(S1F2, wire.SystemBytes);
        Assert.NotNull(await task);
    }

    [Fact]
    public async Task F0Reply_IsAbortException()
    {
        using var h = new Harness();
        var task = h.Session.SendAsync(S1F1, default);
        var wire = h.NextSent();
        h.Receive(new SecsMessage(1, 0, false), wire.SystemBytes);
        var ex = await Assert.ThrowsAsync<SecsAbortException>(() => task);
        Assert.Equal(0, ex.Reply.Function);
        Assert.Equal(wire.SystemBytes, ex.Primary.SystemBytes);
    }

    [Fact]
    public async Task Cancel_RemovesPending()
    {
        using var h = new Harness();
        using var cts = new CancellationTokenSource();
        var task = h.Session.SendAsync(S1F1, cts.Token);
        var wire = h.NextSent();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, h.Session.PendingCount);
        h.Receive(S1F2, wire.SystemBytes);
        Assert.Contains(h.Errors, e => e.Contains("짝이 없는 응답"));
    }

    [Fact]
    public async Task Disconnected_FailsPending()
    {
        using var h = new Harness();
        var task = h.Session.SendAsync(S1F1, default);
        h.NextSent();
        h.Session.Disconnected();
        await Assert.ThrowsAsync<SecsDisconnectedException>(() => task);
        Assert.False(h.Session.IsConnected);
    }

    [Fact]
    public async Task TransmitFailure_RemovesPending_AndPropagates()
    {
        using var h = new Harness(connect: false);
        h.Session.Connected((w, t) => Task.FromException(new IOException("재시도 한계")));
        await Assert.ThrowsAsync<IOException>(() => h.Session.SendAsync(S1F1, default));
        Assert.Equal(0, h.Session.PendingCount);
    }
    #endregion

    #region T3
    [Fact]
    public async Task T3_Host_TimeoutException_NoS9_LinkStays()
    {
        using var h = new Harness { T3 = 100 };
        var task = h.Session.SendAsync(S1F1, default);
        h.NextSent();
        var ex = await Assert.ThrowsAsync<SecsTimeoutException>(() => task);
        Assert.Contains("T3(100ms)", ex.Message);
        h.AssertNothingSent();
        Assert.True(h.Session.IsConnected);
    }

    [Fact]
    public async Task T3_Equipment_SendsS9F9WithPrimaryHeader()
    {
        using var h = new Harness { T3 = 100, Role = SecsRole.Equipment };
        var task = h.Session.SendAsync(new SecsMessage(6, 11, true, SecsItem.U4(7)), default);
        var primary = h.NextSent();
        await Assert.ThrowsAsync<SecsTimeoutException>(() => task);
        var s9 = h.NextSent();
        Assert.Equal((9, 9, false), (s9.Stream, s9.Function, s9.ReplyExpected));
        Assert.Equal(SecsItem.B(Harness.Header(primary)), s9.Item);
        Assert.NotEqual(primary.SystemBytes, s9.SystemBytes);
    }

    [Fact]
    public async Task T3_StartsAfterTransmitCompletes()
    {
        using var h = new Harness(connect: false) { T3 = 300 };
        var sent = new TaskCompletionSource();
        SecsMessage? wire = null;
        h.Session.Connected((w, t) => { wire = w; return sent.Task; });
        var task = h.Session.SendAsync(S1F1, default);
        await Task.Delay(700);                   // 보내는 중(SECS-I 재시도 등)에는 T3가 흐르지 않는다
        Assert.False(task.IsCompleted);
        sent.SetResult();
        h.Receive(S1F2, wire!.SystemBytes);
        Assert.NotNull(await task);
    }

    [Fact]
    public async Task ReplyStarted_StopsT3_ReplyAbandoned_TimesOut()
    {
        using var h = new Harness { T3 = 500, Role = SecsRole.Equipment };
        var task = h.Session.SendAsync(S1F1, default);
        var wire = h.NextSent();
        Assert.False(h.Session.ReplyStarted(wire.SystemBytes, 2, 2));     // Stream 다름
        Assert.False(h.Session.ReplyStarted(wire.SystemBytes + 1, 1, 2)); // 없는 트랜잭션
        Assert.True(h.Session.ReplyStarted(wire.SystemBytes, 1, 2));
        await Task.Delay(900);
        Assert.False(task.IsCompleted);
        h.Session.ReplyAbandoned(wire.SystemBytes, "응답 블록 사이 간격이 T4를 넘었습니다");
        var ex = await Assert.ThrowsAsync<SecsTimeoutException>(() => task);
        Assert.Contains("T4", ex.Message);
        Assert.Equal(9, h.NextSent().Function);   // Equipment는 S9F9
    }
    #endregion

    #region S9
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(11)]
    public async Task S9WithMatchingMhead_FailsTransaction(int function)
    {
        using var h = new Harness();
        SecsPrimaryEventArgs? got = null;
        h.OnPrimary = e => got = e;
        var task = h.Session.SendAsync(new SecsMessage(2, 41, true, SecsItem.L()), default);
        var wire = h.NextSent();
        h.Receive(new SecsMessage(9, function, false, SecsItem.B(Harness.Header(wire))), 77);
        var ex = await Assert.ThrowsAsync<SecsErrorException>(() => task);
        Assert.Equal(function, ex.Function);
        Assert.Equal((9, function), (ex.ErrorMessage.Stream, ex.ErrorMessage.Function));
        await Task.Delay(100);
        Assert.Null(got);
    }

    [Fact]
    public async Task S9WithoutMatch_OrS9F9_GoesToPrimaryReceived()
    {
        using var h = new Harness();
        var got = new BlockingCollection<SecsMessage>();
        h.OnPrimary = e => got.Add(e.Message);
        var task = h.Session.SendAsync(S1F1, default);
        var wire = h.NextSent();
        // S9F9의 SHEAD는 상대 자신의 트랜잭션 → System Bytes가 같아도 내 트랜잭션과 짝짓지 않는다
        h.Receive(new SecsMessage(9, 9, false, SecsItem.B(Harness.Header(wire))), 5);
        // MHEAD의 Stream/Function이 다르면 짝짓지 않는다
        var other = new SecsMessage(6, 11, true).WithHeader(wire.SystemBytes, 1);
        h.Receive(new SecsMessage(9, 7, false, SecsItem.B(Harness.Header(other))), 6);
        Assert.True(got.TryTake(out var a, 3000));
        Assert.True(got.TryTake(out var b, 3000));
        Assert.Equal((9, 9), (a!.Stream, a.Function));
        Assert.Equal((9, 7), (b!.Stream, b.Function));
        Assert.False(task.IsCompleted);
    }

    [Fact]
    public async Task Equipment_DeviceIdMismatch_SendsS9F1_NoEvent()
    {
        using var h = new Harness { Role = SecsRole.Equipment, DeviceID = 5 };
        bool raised = false;
        h.OnPrimary = _ => raised = true;
        var m = new SecsMessage(1, 1, true).WithHeader(9, 6);
        h.Session.Received(m, Harness.Header(m));
        var s9 = h.NextSent();
        Assert.Equal((9, 1, 5), (s9.Stream, s9.Function, s9.DeviceID));
        Assert.Equal(SecsItem.B(Harness.Header(m)), s9.Item);
        Assert.Contains(h.Errors, e => e.Contains("S9F1"));
        await Task.Delay(100);
        Assert.False(raised);
        h.AssertNothingSent();   // SxF0도 보내지 않는다
    }

    [Fact]
    public void Host_DoesNotCheckDeviceId()
    {
        using var h = new Harness { Role = SecsRole.Host, DeviceID = 5 };
        var got = new BlockingCollection<SecsMessage>();
        h.OnPrimary = e => { got.Add(e.Message); e.Reply(new SecsMessage(6, 12, false, SecsItem.B(0))); };
        h.Receive(new SecsMessage(6, 11, true), 3, deviceId: 6);
        Assert.True(got.TryTake(out var m, 3000));
        Assert.Equal(6, m!.DeviceID);
        var reply = h.NextSent();
        Assert.Equal((6, 12, 3u, 6), (reply.Stream, reply.Function, reply.SystemBytes, reply.DeviceID));   // 응답은 1차 메시지의 장치 ID
    }

    [Fact]
    public void DecodeFailed_And_TooLong_EquipmentSendsS9()
    {
        using var eq = new Harness { Role = SecsRole.Equipment };
        var header = Harness.Header(new SecsMessage(1, 3, true).WithHeader(4, 1));
        eq.Session.DecodeFailed(header, "깨진 본문");
        var s9f7 = eq.NextSent();
        Assert.Equal((9, 7), (s9f7.Stream, s9f7.Function));
        Assert.Equal(SecsItem.B(header), s9f7.Item);
        eq.Session.TooLong(header, 999999);
        Assert.Equal(11, eq.NextSent().Function);
        Assert.Contains(eq.Errors, e => e.Contains("S9F7"));

        using var host = new Harness { Role = SecsRole.Host };
        host.Session.DecodeFailed(header, "깨진 본문");
        host.Session.TooLong(header, 999999);
        host.AssertNothingSent();
        Assert.Equal(2, host.Errors.Count);
    }
    #endregion

    #region 1차 메시지와 응답
    [Fact]
    public void Primary_ReplyFromHandler_OnDispatcherThread()
    {
        using var h = new Harness();
        int handlerThread = 0;
        h.OnPrimary = e =>
        {
            handlerThread = Environment.CurrentManagedThreadId;
            e.Reply(new SecsMessage(1, 14, false, SecsItem.L(SecsItem.B(0), SecsItem.L())));
        };
        h.Receive(new SecsMessage(1, 13, true, SecsItem.L()), 0x732C90E5);
        var r = h.NextSent();
        Assert.Equal((1, 14, false, 0x732C90E5u), (r.Stream, r.Function, r.ReplyExpected, r.SystemBytes));
        Assert.NotEqual(Environment.CurrentManagedThreadId, handlerThread);
        h.AssertNothingSent();   // 응답했으므로 SxF0 없음
    }

    [Fact]
    public void Primary_NoReply_OrHandlerThrows_SendsSxF0()
    {
        using var h = new Harness();
        h.OnPrimary = e => { if (e.Message.Stream == 2) throw new InvalidOperationException("사용자 오류"); };
        h.Receive(new SecsMessage(1, 3, true), 10);
        var f0 = h.NextSent();
        Assert.Equal((1, 0, 10u, false), (f0.Stream, f0.Function, f0.SystemBytes, f0.ReplyExpected));
        Assert.Null(f0.Item);
        h.Receive(new SecsMessage(2, 41, true), 11);
        var f0b = h.NextSent();
        Assert.Equal((2, 0, 11u), (f0b.Stream, f0b.Function, f0b.SystemBytes));
        h.Receive(new SecsMessage(6, 11, false), 12);   // W비트 없음 → SxF0 없음
        h.AssertNothingSent(200);
    }

    [Fact]
    public void Primary_NoHandler_SendsSxF0()
    {
        using var h = new Harness();
        h.Receive(new SecsMessage(5, 1, true), 3);
        var f0 = h.NextSent();
        Assert.Equal((5, 0, 3u), (f0.Stream, f0.Function, f0.SystemBytes));
    }

    [Fact]
    public void Reply_Rules()
    {
        using var h = new Harness();
        var errors = new ConcurrentQueue<Exception>();
        SecsPrimaryEventArgs? saved = null;
        h.OnPrimary = e =>
        {
            saved = e;
            if (e.Message.ReplyExpected)
            {
                errors.Enqueue(Assert.Throws<ArgumentException>(() => e.Reply(new SecsMessage(1, 3, false))));        // 1차 + 1 아님
                errors.Enqueue(Assert.Throws<ArgumentException>(() => e.Reply(new SecsMessage(2, 2, false))));        // Stream 다름
                errors.Enqueue(Assert.Throws<ArgumentException>(() => e.Reply(new SecsMessage(1, 2, true))));         // W비트
                e.Reply(new SecsMessage(1, 2, false));
                errors.Enqueue(Assert.Throws<InvalidOperationException>(() => e.Reply(new SecsMessage(1, 2, false)))); // 두 번
            }
            else errors.Enqueue(Assert.Throws<InvalidOperationException>(() => e.Reply(new SecsMessage(6, 12, false)))); // W 없는 1차
        };
        h.Receive(S1F1, 1);
        Assert.Equal(2, h.NextSent().Function);
        h.Receive(new SecsMessage(6, 11, false), 2);
        SpinWait.SpinUntil(() => errors.Count == 5, 3000);
        Assert.Equal(5, errors.Count);
        Assert.Throws<InvalidOperationException>(() => saved!.Reply(new SecsMessage(6, 12, false)));   // 핸들러가 끝난 뒤
        h.AssertNothingSent();
    }

    [Fact]
    public async Task Reply_AfterReconnect_IsDisconnectedException()
    {
        using var h = new Harness();
        var gate = new ManualResetEventSlim();
        var result = new TaskCompletionSource<Exception?>();
        h.OnPrimary = e =>
        {
            gate.Wait(3000);
            try { e.Reply(new SecsMessage(1, 2, false)); result.SetResult(null); }
            catch (Exception ex) { result.SetResult(ex); }
        };
        h.Receive(S1F1, 1);
        h.Session.Disconnected();
        h.Session.Connected(h.Transmit);
        gate.Set();
        Assert.IsType<SecsDisconnectedException>(await result.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        h.AssertNothingSent();   // 새 연결로는 응답도 SxF0도 보내지 않는다
    }

    [Fact]
    public async Task Handler_CanSend_WithoutDeadlock()
    {
        using var h = new Harness();
        var inner = new TaskCompletionSource<SecsMessage?>();
        h.OnPrimary = e =>
        {
            if (e.Message.Stream != 6) return;
            // 핸들러 안에서 Send(동기) → 응답은 다른 스레드(여기서는 테스트)가 넣는다
            inner.SetResult(h.Session.SendAsync(new SecsMessage(1, 1, true), default).GetAwaiter().GetResult());
            e.Reply(new SecsMessage(6, 12, false, SecsItem.B(0)));
        };
        h.Receive(new SecsMessage(6, 11, true), 50);
        var s1f1 = h.NextSent();
        h.Receive(S1F2, s1f1.SystemBytes);
        Assert.NotNull(await inner.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        var r = h.NextSent();
        Assert.Equal((6, 12, 50u), (r.Stream, r.Function, r.SystemBytes));
    }

    [Fact]
    public void Primaries_DeliveredInOrder()
    {
        using var h = new Harness();
        var got = new BlockingCollection<uint>();
        h.OnPrimary = e => got.Add(e.Message.SystemBytes);
        for (uint i = 1; i <= 100; i++) h.Receive(new SecsMessage(6, 11, false), i);
        for (uint i = 1; i <= 100; i++)
        {
            Assert.True(got.TryTake(out var s, 3000));
            Assert.Equal(i, s);
        }
    }

    [Fact]
    public async Task Stop_FailsPending_AndStopsDispatcher()
    {
        var h = new Harness();
        var task = h.Session.SendAsync(S1F1, default);
        h.NextSent();
        h.Dispose();
        await Assert.ThrowsAsync<SecsDisconnectedException>(() => task);
        bool raised = false;
        h.OnPrimary = _ => raised = true;
        h.Receive(new SecsMessage(6, 11, false), 1);
        Thread.Sleep(100);
        Assert.False(raised);
    }
    #endregion

    [Fact]
    public void Post_RunsOnDispatcherThread_InOrderWithPrimaries()
    {
        using var h = new Harness();
        var log = new BlockingCollection<(string, int)>();
        h.OnPrimary = e => log.Add(("primary" + e.Message.Function, Environment.CurrentManagedThreadId));
        h.Receive(new SecsMessage(1, 1, false), 1);
        h.Session.Post(() => log.Add(("post1", Environment.CurrentManagedThreadId)));
        h.Session.Post(() => throw new InvalidOperationException("무시돼야 함"));
        h.Receive(new SecsMessage(1, 3, false), 2);
        h.Session.Post(() => log.Add(("post2", Environment.CurrentManagedThreadId)));
        var got = new List<(string, int)>();
        for (int i = 0; i < 4; i++) { Assert.True(log.TryTake(out var x, 3000)); got.Add(x); }
        Assert.Equal(new[] { "primary1", "post1", "primary3", "post2" }, got.Select(g => g.Item1));
        Assert.Single(got.Select(g => g.Item2).Distinct());
        Assert.NotEqual(Environment.CurrentManagedThreadId, got[0].Item2);
    }
}
