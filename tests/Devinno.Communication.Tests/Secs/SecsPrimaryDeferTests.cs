using System.Buffers.Binary;
using System.Collections.Concurrent;
using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Tests.Secs;

/// SecsPrimaryEventArgs의 internal 응답 미루기(Defer)와 인자 검사(Validate). GEM 계층이 앱 승인·기한 응답·S9 뒤 무응답에 쓴다.
public class SecsPrimaryDeferTests
{
    sealed class Harness : IDisposable
    {
        public readonly BlockingCollection<SecsMessage> Sent = [];
        public readonly BlockingCollection<SecsPrimaryEventArgs> Args = [];
        public Action<SecsPrimaryEventArgs>? OnPrimary;
        public readonly SecsSession Session;

        public Harness()
        {
            Session = new SecsSession(() => SecsRole.Equipment, () => 1, () => 5000, Header, e => { OnPrimary?.Invoke(e); Args.Add(e); }, _ => { }, "defer");
            Session.Start();
            Session.Connected(Transmit);
        }

        public Task Transmit(SecsMessage wire, CancellationToken token) { Sent.Add(wire); return Task.CompletedTask; }

        static byte[] Header(SecsMessage m)
        {
            var h = new byte[10];
            BinaryPrimitives.WriteUInt16BigEndian(h, (ushort)m.DeviceID);
            h[2] = (byte)((m.ReplyExpected ? 0x80 : 0) | m.Stream);
            h[3] = (byte)m.Function;
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(6), m.SystemBytes);
            return h;
        }

        public void Receive(SecsMessage m, uint sys)
        {
            var wire = m.WithHeader(sys, 1);
            Session.Received(wire, Header(wire));
        }

        /// 핸들러가 끝난 뒤의 인자(디스패처가 Close까지 마친 것)
        public SecsPrimaryEventArgs Delivered()
        {
            Assert.True(Args.TryTake(out var e, 3000), "1차 메시지가 전달되지 않았습니다.");
            using var done = new ManualResetEventSlim();
            Session.Post(done.Set);   // 같은 디스패처 대기열: 앞 메시지의 Close가 끝났음을 보장
            Assert.True(done.Wait(3000));
            return e!;
        }

        public void Dispose() => Session.Stop(3000);
    }

    [Fact]
    public void Deferred_NoAutoSxF0_ReplyLaterFromAnotherThread()
    {
        using var h = new Harness { OnPrimary = e => e.Defer() };
        h.Receive(new SecsMessage(2, 41, true, SecsItem.L()), 0x77);
        var e = h.Delivered();
        Assert.False(h.Sent.TryTake(out var early, 200), $"미뤘는데 자동 응답이 나갔습니다: {early}");
        var t = new Thread(() => e.Reply(new SecsMessage(2, 42, false, SecsItem.L(SecsItem.B(4), SecsItem.L()))));
        t.Start();
        t.Join();
        Assert.True(h.Sent.TryTake(out var reply, 3000));
        Assert.Equal((2, 42, 0x77u, 1), (reply!.Stream, reply.Function, reply.SystemBytes, reply.DeviceID));
        Assert.Throws<InvalidOperationException>(() => e.Reply(new SecsMessage(2, 42, false)));   // 한 번만
    }

    [Fact]
    public void Deferred_NeverReplied_NothingSent()
    {
        using var h = new Harness { OnPrimary = e => e.Defer() };
        h.Receive(new SecsMessage(7, 1, true), 5);
        h.Delivered();
        Assert.False(h.Sent.TryTake(out var m, 300), $"보내지 않아야 하는데 {m}");
    }

    [Fact]
    public void NotDeferred_StillAutoSxF0_AndReplyAfterHandlerIsRejected()
    {
        using var h = new Harness();
        h.Receive(new SecsMessage(7, 1, true), 6);
        var e = h.Delivered();
        Assert.True(h.Sent.TryTake(out var f0, 3000));
        Assert.Equal((7, 0, 6u), (f0!.Stream, f0.Function, f0.SystemBytes));
        Assert.Throws<InvalidOperationException>(() => e.Reply(new SecsMessage(7, 2, false)));
    }

    [Fact]
    public void DeferAfterReply_HasNoEffect()
    {
        using var h = new Harness { OnPrimary = e => { e.Reply(new SecsMessage(1, 2, false, SecsItem.L())); e.Defer(); } };
        h.Receive(new SecsMessage(1, 1, true), 9);
        var e = h.Delivered();
        Assert.True(h.Sent.TryTake(out var r, 3000));
        Assert.Equal(2, r!.Function);
        Assert.False(h.Sent.TryTake(out var extra, 200), $"응답이 하나 더 나갔습니다: {extra}");
        Assert.Throws<InvalidOperationException>(() => e.Reply(new SecsMessage(1, 2, false)));
    }

    [Fact]
    public void Validate_ChecksWithoutConsumingTheReply()
    {
        using var h = new Harness { OnPrimary = e => e.Defer() };
        h.Receive(new SecsMessage(1, 3, true, SecsItem.L()), 11);
        var e = h.Delivered();
        Assert.Throws<ArgumentException>(() => e.Validate(new SecsMessage(1, 5, false)));
        Assert.Throws<ArgumentException>(() => e.Reply(new SecsMessage(2, 4, false)));
        Assert.Throws<ArgumentNullException>(() => e.Validate(null!));
        e.Validate(new SecsMessage(1, 4, false));                    // 검사만: 응답 권리는 남아 있음
        e.Reply(new SecsMessage(1, 4, false, SecsItem.L()));
        Assert.True(h.Sent.TryTake(out var r, 3000));
        Assert.Equal((1, 4, 11u), (r!.Stream, r.Function, r.SystemBytes));
    }

    [Fact]
    public void Validate_NoWBit_IsInvalidOperation()
    {
        using var h = new Harness { OnPrimary = e => e.Defer() };
        h.Receive(new SecsMessage(6, 11, false, SecsItem.L()), 12);
        var e = h.Delivered();
        Assert.Throws<InvalidOperationException>(() => e.Validate(new SecsMessage(6, 12, false)));
    }

    [Fact]
    public void Deferred_ReplyAfterReconnect_IsDisconnectedException()
    {
        using var h = new Harness { OnPrimary = e => e.Defer() };
        h.Receive(new SecsMessage(2, 41, true, SecsItem.L()), 13);
        var e = h.Delivered();
        h.Session.Disconnected();
        h.Session.Connected(h.Transmit);
        Assert.Throws<SecsDisconnectedException>(() => e.Reply(new SecsMessage(2, 42, false)));
        Assert.False(h.Sent.TryTake(out var m, 200), $"새 연결로 나가면 안 됩니다: {m}");
    }
}
