using System.Buffers.Binary;
using System.Collections.Concurrent;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Hsms;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Secs.Hsms;

/// 규칙을 어기는 상대(FakeHsmsPeer)와 바이트로 주고받으며 HSMS 세션 규칙을 확인한다
public class SecsHsmsPeerTests
{
    static byte[] Data(SecsMessage m, uint sys, int deviceId = 1) => HsmsFrame.Data(m.WithHeader(sys, deviceId));

    /// 이벤트는 디스패처 스레드에서 늦게 도착하므로 조건이 참이 될 때까지 기다린다
    static bool Has(ConcurrentQueue<string> errors, string text, int timeoutMs = 3000)
        => SpinWait.SpinUntil(() => errors.Any(e => e.Contains(text)), timeoutMs);

    /// 우리 Passive(역할 role)에 가짜 상대가 접속해 Select까지
    static (SecsHsms Us, FakeHsmsPeer Peer) PassiveSelected(SecsRole role, Action<SecsHsms>? setup = null)
    {
        var us = SecsHsmsTests.Passive(role);
        setup?.Invoke(us);
        us.Start();
        var peer = FakeHsmsPeer.Connect(us.BoundPort);
        Assert.Equal(HsmsFrame.SelectOk, peer.Select());
        SpinWait.SpinUntil(() => us.IsOpen, 3000);
        Assert.True(us.IsOpen);
        return (us, peer);
    }

    #region Select·T7·T6
    [Fact]
    public void Passive_NoSelectWithinT7_Closes()
    {
        using var us = SecsHsmsTests.Passive(SecsRole.Equipment);
        us.T7 = 300;
        us.Start();
        using var peer = FakeHsmsPeer.Connect(us.BoundPort);
        Assert.True(peer.WaitClosed(3000));
        SpinWait.SpinUntil(() => us.State == HsmsState.NotConnected, 3000);
        Assert.Equal(HsmsState.NotConnected, us.State);
    }

    [Fact]
    public void Passive_SecondConnection_GetsStatus1_FirstKept()
    {
        var (us, first) = PassiveSelected(SecsRole.Equipment, u => u.PrimaryReceived += SecsHsmsTests.Echo);
        using (us) using (first)
        {
            using var second = FakeHsmsPeer.Connect(us.BoundPort);
            Assert.Equal(HsmsFrame.SelectAlreadyActive, second.Select(7));
            Assert.True(second.WaitClosed(3000));
            first.Send(Data(new SecsMessage(1, 1, true), 5));
            var reply = first.ReadUntil(HsmsSType.Data);
            Assert.Equal(5u, reply.Header.SystemBytes);
            Assert.True(us.IsOpen);
        }
    }

    [Fact]
    public void SelectWhileSelected_Status1_KeepsLink()
    {
        var (us, peer) = PassiveSelected(SecsRole.Equipment);
        using (us) using (peer)
        {
            Assert.Equal(HsmsFrame.SelectAlreadyActive, peer.Select(9));
            Assert.True(us.IsOpen);
        }
    }

    [Fact]
    public void SelectRsp_EchoesRequestSessionId()
    {
        using var us = SecsHsmsTests.Passive(SecsRole.Equipment);
        us.Start();
        using var peer = FakeHsmsPeer.Connect(us.BoundPort);
        peer.Send(HsmsFrame.Control(HsmsSType.SelectReq, 3, session: 1));   // 잘못 장치 ID를 넣은 Select.req도 받는다
        var rsp = peer.ReadUntil(HsmsSType.SelectRsp).Header;
        Assert.Equal(((ushort)1, (byte)0), (rsp.SessionId, rsp.Byte3));
    }

    [Fact]
    public void Active_SelectUnanswered_ClosesAfterT6_ReconnectsAfterT5()
    {
        using var peer = FakeHsmsPeer.Listen();
        using var us = SecsHsmsTests.Active(SecsRole.Host, peer.Port);
        us.T6 = 1000;
        us.T5 = 300;
        us.Start();
        peer.Accept();
        var req = peer.ReadUntil(HsmsSType.SelectReq).Header;
        Assert.Equal(((ushort)0xFFFF, (byte)0, (byte)0), (req.SessionId, req.Byte2, req.Byte3));
        Assert.True(peer.WaitClosed(3000));
        peer.Accept();                         // T5 뒤 다시 접속
        peer.AnswerSelect();
        SpinWait.SpinUntil(() => us.IsOpen, 3000);
        Assert.True(us.IsOpen);
    }

    [Fact]
    public void Active_SelectRefused_Closes()
    {
        using var peer = FakeHsmsPeer.Listen();
        using var us = SecsHsmsTests.Active(SecsRole.Host, peer.Port);
        us.T5 = 5000;
        var errors = new ConcurrentQueue<string>();
        us.ProtocolError += (s, e) => errors.Enqueue(e.Reason);
        us.Start();
        peer.Accept();
        peer.AnswerSelect(status: 1);
        Assert.True(peer.WaitClosed(3000));
        Assert.True(Has(errors, "상태 1"));
        Assert.False(us.IsOpen);
    }
    #endregion

    #region Reject
    [Fact]
    public void DataBeforeSelect_Reject4()
    {
        using var us = SecsHsmsTests.Passive(SecsRole.Equipment);
        us.Start();
        using var peer = FakeHsmsPeer.Connect(us.BoundPort);
        peer.Send(Data(new SecsMessage(1, 1, true), 0x55));
        var rej = peer.ReadUntil(HsmsSType.RejectReq).Header;
        Assert.Equal(((ushort)0xFFFF, (byte)0, HsmsFrame.RejectNotSelected, 0x55u), (rej.SessionId, rej.Byte2, rej.Byte3, rej.SystemBytes));
    }

    [Fact]
    public void UnknownSType_PType_UnsolicitedResponses_AreRejected()
    {
        var (us, peer) = PassiveSelected(SecsRole.Equipment);
        using (us) using (peer)
        {
            peer.Send(HsmsFrame.Control((HsmsSType)8, 21));
            var r1 = peer.ReadUntil(HsmsSType.RejectReq).Header;
            Assert.Equal(((byte)8, HsmsFrame.RejectSType, 21u), (r1.Byte2, r1.Byte3, r1.SystemBytes));

            var ptype = HsmsFrame.Control(HsmsSType.LinktestReq, 22);
            ptype[8] = 3;                                         // PType 3
            peer.Send(ptype);
            var r2 = peer.ReadUntil(HsmsSType.RejectReq).Header;
            Assert.Equal(((byte)3, HsmsFrame.RejectPType, 22u), (r2.Byte2, r2.Byte3, r2.SystemBytes));

            peer.Send(HsmsFrame.Control(HsmsSType.LinktestRsp, 23));   // 보낸 적 없는 Linktest의 응답
            var r3 = peer.ReadUntil(HsmsSType.RejectReq).Header;
            Assert.Equal(((byte)6, HsmsFrame.RejectNoTransaction, 23u), (r3.Byte2, r3.Byte3, r3.SystemBytes));

            peer.Send(HsmsFrame.Control(HsmsSType.DeselectRsp, 24));   // 보낸 적 없는 Deselect의 응답
            var r4 = peer.ReadUntil(HsmsSType.RejectReq).Header;
            Assert.Equal(((byte)4, HsmsFrame.RejectNoTransaction, 24u), (r4.Byte2, r4.Byte3, r4.SystemBytes));
            Assert.True(us.IsOpen);
        }
    }

    [Fact]
    public void ReceivedReject_IsReportedOnly()
    {
        var errors = new ConcurrentQueue<string>();
        var (us, peer) = PassiveSelected(SecsRole.Equipment, u => u.ProtocolError += (s, e) => errors.Enqueue(e.Reason));
        using (us) using (peer)
        {
            peer.Send(HsmsFrame.Control(HsmsSType.RejectReq, 30, byte2: 0, byte3: 4));
            SpinWait.SpinUntil(() => errors.Any(e => e.Contains("Reject를 보냈습니다")), 3000);
            Assert.Contains(errors, e => e.Contains("이유 4"));
            Assert.True(us.IsOpen);
        }
    }
    #endregion

    #region Linktest·T8·깨진 프레임·Separate·Deselect
    [Fact]
    public void LinktestBeforeSelect_IsAnswered()
    {
        using var us = SecsHsmsTests.Passive(SecsRole.Equipment);
        us.Start();
        using var peer = FakeHsmsPeer.Connect(us.BoundPort);
        peer.Send(HsmsFrame.Control(HsmsSType.LinktestReq, 40));
        var rsp = peer.ReadUntil(HsmsSType.LinktestRsp).Header;
        Assert.Equal(((ushort)0xFFFF, 40u), (rsp.SessionId, rsp.SystemBytes));
    }

    [Fact]
    public void OurLinktest_Unanswered_ClosesAfterT6()
    {
        var (us, peer) = PassiveSelected(SecsRole.Equipment, u => { u.LinktestInterval = 100; u.T6 = 300; });
        using (us) using (peer)
        {
            var req = peer.ReadUntil(HsmsSType.LinktestReq).Header;
            Assert.Equal((ushort)0xFFFF, req.SessionId);
            Assert.True(peer.WaitClosed(3000));
        }
    }

    [Fact]
    public void OurLinktest_Answered_SentAgainEveryInterval()
    {
        var (us, peer) = PassiveSelected(SecsRole.Equipment, u => { u.LinktestInterval = 100; u.T6 = 1000; });
        using (us) using (peer)
        {
            for (int i = 0; i < 3; i++)
            {
                var req = peer.ReadUntil(HsmsSType.LinktestReq).Header;
                peer.Send(HsmsFrame.Control(HsmsSType.LinktestRsp, req.SystemBytes));
            }
            Assert.True(us.IsOpen);
        }
    }

    [Fact]
    public void PartialFrame_T8_Closes()
    {
        var (us, peer) = PassiveSelected(SecsRole.Equipment, u => u.T8 = 300);
        using (us) using (peer)
        {
            peer.Send(Convert.FromHexString("0000000C000081"));   // 길이·헤더 일부만
            Assert.True(peer.WaitClosed(3000));
            SpinWait.SpinUntil(() => us.State == HsmsState.NotConnected, 3000);
            Assert.Equal(HsmsState.NotConnected, us.State);
        }
    }

    [Fact]
    public void LengthBelow10_Closes()
    {
        var errors = new ConcurrentQueue<string>();
        var (us, peer) = PassiveSelected(SecsRole.Equipment, u => u.ProtocolError += (s, e) => errors.Enqueue(e.Reason));
        using (us) using (peer)
        {
            peer.Send(Convert.FromHexString("00000005FFFF000000"));
            Assert.True(peer.WaitClosed(3000));
            Assert.True(Has(errors, "깨진 프레임"));
        }
    }

    [Fact]
    public void PeerSeparate_Closes_RaisesDeviceClosed()
    {
        var closed = new ManualResetEventSlim();
        var (us, peer) = PassiveSelected(SecsRole.Equipment, u => u.DeviceClosed += (s, e) => closed.Set());
        using (us) using (peer)
        {
            peer.Send(HsmsFrame.Control(HsmsSType.SeparateReq, 50));
            Assert.True(closed.Wait(3000));
            Assert.True(peer.WaitClosed(3000));
            Assert.True(us.IsStart);   // Passive는 계속 기다린다
        }
    }

    [Fact]
    public void PeerDeselect_AnsweredThenClosed()
    {
        var (us, peer) = PassiveSelected(SecsRole.Equipment);
        using (us) using (peer)
        {
            peer.Send(HsmsFrame.Control(HsmsSType.DeselectReq, 60));
            var rsp = peer.ReadUntil(HsmsSType.DeselectRsp).Header;
            Assert.Equal((60u, (byte)0), (rsp.SystemBytes, rsp.Byte3));
            Assert.True(peer.WaitClosed(3000));
        }
    }

    [Fact]
    public void Stop_SendsSeparate()
    {
        var (us, peer) = PassiveSelected(SecsRole.Equipment);
        using (peer)
        {
            us.Stop();
            var sep = peer.ReadUntil(HsmsSType.SeparateReq).Header;
            Assert.Equal((ushort)0xFFFF, sep.SessionId);
            Assert.True(peer.WaitClosed(3000));
            us.Dispose();
        }
    }
    #endregion

    #region Equipment 자동 S9·짝 없는 응답·늦은 응답
    [Fact]
    public void Equipment_DeviceIdMismatch_S9F1WithMhead()
    {
        int events = 0;
        var (us, peer) = PassiveSelected(SecsRole.Equipment, u => u.PrimaryReceived += (s, e) => Interlocked.Increment(ref events));
        using (us) using (peer)
        {
            var frame = Data(new SecsMessage(1, 1, true), 0x70, deviceId: 2);
            peer.Send(frame);
            var s9 = peer.ReadUntil(HsmsSType.Data);
            var m = HsmsFrame.ToMessage(s9.Header, s9.Body);
            Assert.Equal((9, 1, false, 1), (m.Stream, m.Function, m.ReplyExpected, m.DeviceID));
            Assert.Equal(SecsItem.B(frame[4..14]), m.Item);   // MHEAD = 받은 헤더 10바이트
            Thread.Sleep(100);
            Assert.Equal(0, events);
        }
    }

    [Fact]
    public void Equipment_UndecodableBody_S9F7()
    {
        var (us, peer) = PassiveSelected(SecsRole.Equipment);
        using (us) using (peer)
        {
            byte[] frame = [.. Convert.FromHexString("0000000F00018103000000000007"), .. Convert.FromHexString("A903000102")];   // U2인데 3바이트
            peer.Send(frame);
            var s9 = peer.ReadUntil(HsmsSType.Data);
            var m = HsmsFrame.ToMessage(s9.Header, s9.Body);
            Assert.Equal((9, 7), (m.Stream, m.Function));
            Assert.Equal(SecsItem.B(frame[4..14]), m.Item);
            Assert.True(us.IsOpen);
        }
    }

    [Fact]
    public void Equipment_TooLong_S9F11_LinkKept()
    {
        var (us, peer) = PassiveSelected(SecsRole.Equipment, u => { u.MaxMessageSize = 1024; u.PrimaryReceived += SecsHsmsTests.Echo; });
        using (us) using (peer)
        {
            var frame = Data(new SecsMessage(7, 3, true, SecsItem.B(new byte[2000])), 0x80);
            peer.Send(frame);
            var s9 = peer.ReadUntil(HsmsSType.Data);
            var m = HsmsFrame.ToMessage(s9.Header, s9.Body);
            Assert.Equal((9, 11), (m.Stream, m.Function));
            Assert.Equal(SecsItem.B(frame[4..14]), m.Item);
            peer.Send(Data(new SecsMessage(1, 1, true), 0x81));           // 이어서 오는 메시지는 정상 처리
            Assert.Equal(0x81u, peer.ReadUntil(HsmsSType.Data).Header.SystemBytes);
        }
    }

    [Fact]
    public void Host_TooLongOrUndecodable_NoS9_Reported()
    {
        var errors = new ConcurrentQueue<string>();
        var (us, peer) = PassiveSelected(SecsRole.Host, u => { u.MaxMessageSize = 1024; u.ProtocolError += (s, e) => errors.Enqueue(e.Reason); });
        using (us) using (peer)
        {
            peer.Send(Data(new SecsMessage(7, 3, false, SecsItem.B(new byte[2000])), 1));
            peer.Send([.. Convert.FromHexString("0000000F00018103000000000002"), .. Convert.FromHexString("A903000102")]);
            SpinWait.SpinUntil(() => errors.Count >= 2, 3000);
            Assert.Null(peer.Read(300));
            Assert.Contains(errors, e => e.Contains("최대 크기"));
            Assert.Contains(errors, e => e.Contains("해석하지 못했습니다"));
        }
    }

    [Fact]
    public void UnmatchedReply_Reported_LinkKept()
    {
        var errors = new ConcurrentQueue<string>();
        var (us, peer) = PassiveSelected(SecsRole.Host, u => u.ProtocolError += (s, e) => errors.Enqueue(e.Reason));
        using (us) using (peer)
        {
            peer.Send(Data(new SecsMessage(1, 2, false), 0x999));
            SpinWait.SpinUntil(() => errors.Any(e => e.Contains("짝이 없는 응답")), 3000);
            Assert.Contains(errors, e => e.Contains("0x00000999"));
            Assert.True(us.IsOpen);
        }
    }

    [Fact]
    public async Task Equipment_PeerRepliesAfterT3_TimeoutAndS9F9_LateReplyDropped()
    {
        var errors = new ConcurrentQueue<string>();
        var (us, peer) = PassiveSelected(SecsRole.Equipment, u => { u.T3 = 300; u.ProtocolError += (s, e) => errors.Enqueue(e.Reason); });
        using (us) using (peer)
        {
            var task = us.SendAsync(new SecsMessage(6, 11, true, SecsItem.L()));
            var primary = peer.ReadUntil(HsmsSType.Data);
            await Assert.ThrowsAsync<SecsTimeoutException>(() => task);
            var s9 = peer.ReadUntil(HsmsSType.Data);
            var m = HsmsFrame.ToMessage(s9.Header, s9.Body);
            Assert.Equal((9, 9), (m.Stream, m.Function));
            Assert.Equal(SecsItem.B(primary.Header.ToArray()), m.Item);   // SHEAD = 내가 보낸 1차 메시지 헤더
            peer.Send(Data(new SecsMessage(6, 12, false, SecsItem.B(0)), primary.Header.SystemBytes));
            SpinWait.SpinUntil(() => errors.Any(e => e.Contains("짝이 없는 응답")), 3000);
            Assert.True(us.IsOpen);
        }
    }

    [Fact]
    public async Task DataMessage_SessionIdIsDeviceId_ReplyEchoesSystemBytes()
    {
        var (us, peer) = PassiveSelected(SecsRole.Host, u => { u.DeviceID = 0x123; u.PrimaryReceived += SecsHsmsTests.Echo; });
        using (us) using (peer)
        {
            var task = us.SendAsync(new SecsMessage(1, 1, true));
            var req = peer.ReadUntil(HsmsSType.Data).Header;
            Assert.Equal(((ushort)0x123, (byte)0x81, (byte)1), (req.SessionId, req.Byte2, req.Byte3));
            peer.Send(Data(new SecsMessage(1, 2, false, SecsItem.L()), req.SystemBytes, 0x123));
            Assert.Equal(SecsItem.L(), (await task)!.Item);

            peer.Send(Data(new SecsMessage(5, 1, true, SecsItem.U1(9)), 0xABCD, 0x123));
            var reply = peer.ReadUntil(HsmsSType.Data).Header;
            Assert.Equal((0xABCDu, (byte)0x05, (byte)2), (reply.SystemBytes, reply.Byte2, reply.Byte3));
            Assert.Equal((ushort)0x123, BinaryPrimitives.ReadUInt16BigEndian(reply.ToArray()));
        }
    }
    [Fact]
    public void PrimaryReceived_ReceivedHeader_IsHeaderBytesPeerSent()
    {
        var headers = new BlockingCollection<byte[]>();
        var (us, peer) = PassiveSelected(SecsRole.Host, u => u.PrimaryReceived += (s, e) => headers.Add(e.ReceivedHeader));
        using (us) using (peer)
        {
            var frame = Data(new SecsMessage(7, 19, true, SecsItem.A("PPID")), 0x01020304, 0x0155);
            peer.Send(frame);
            Assert.True(headers.TryTake(out var h, 3000));
            Assert.Equal(frame.AsSpan(4, 10).ToArray(), h);   // 길이 4바이트 뒤 헤더 10바이트 그대로
        }
    }
    #endregion
}
