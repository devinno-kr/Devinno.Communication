using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

// SECS-I 다중 블록 문의 S6F5/F6(6J.6.2): 켰을 때만, 244바이트를 넘는 S6F11·S6F1 앞에. 허락이면 바로 그 메시지, 아니면 보내지 않음
public partial class GemEquipment
{
    const int SingleBlockLimit = 244;     // SECS-I 블록 하나의 데이터 상한

    bool multiBlockInquire;
    Outgoing? resumeNext;                 // gate: S6F5 허락을 받아 다음에 바로 보낼 원래 항목

    /// <summary>Gets or sets whether, over SECS-I, an S6F11 (event report) or S6F1 (trace report) whose body is larger than one block (244 bytes) is preceded by S6F5 Multi-block Data Send Inquire (default: false).</summary>
    /// <remarks>If the Host answers GRANT6 0 the message follows at once; otherwise it is not sent and <see cref="SendFailed"/> (<see cref="GemSendFailure.NotGranted"/>, Ack = GRANT6) is raised, without a retry. A spooled message refused this way is removed from the spool and the transmit goes on. Over HSMS nothing changes even if this is true (HSMS has no blocks).</remarks>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public bool MultiBlockInquire { get => multiBlockInquire; set => Setting(() => multiBlockInquire = value); }

    // 테스트용: 문의 판단이 예외를 내는 경우를 만든다
    internal Action? InquiryFailForTest;

    // Pump가 꺼낸 항목을 S6F5 문의 항목으로 바꿀지(gate 안). 바꾸지 않으면 null
    Outgoing? InquiryLocked(Outgoing o)
    {
        if (!multiBlockInquire || o.Inquired || o.InquiryFor != null || !transport.IsSerial) return null;
        if (o.Message is not { Stream: 6, Function: 1 or 11, Item: { } body }) return null;
        InquiryFailForTest?.Invoke();                 // S6F1이면 DATAID 자리에 TRID
        int length = SecsCodec.Encode(body).Length;
        if (length <= SingleBlockLimit) return null;
        return new Outgoing(GemMessages.S6F5(body.Items[0], (uint)length), InquiryDone) { InquiryFor = o, SpoolSeq = o.SpoolSeq, Run = o.Run };
    }

    // 테스트용: S6F6 완료가 gate에 들어가기 직전에 부른다
    internal Action? BeforeInquiryDecisionForTest;

    // S6F6을 받았다(Completed 안, gate 안, inFlight가 아직 이 문의). 허락이면 원래 항목을 다음 송신으로, 거절·잘못된 답이면 보내지 않음.
    // 처리했으면 true. 실패(T3·SxF0·S9·회선)는 false → InquiryDone이 원래 메시지의 완료처럼 처리한다(지금 규칙 그대로: SendFailed, 스풀 머리는 6I.5 표)
    bool InquiryDecidedLocked(Outgoing q, Task<SecsMessage?> t)
    {
        if (q.InquiryFor is not { } o || !t.IsCompletedSuccessfully) return false;
        byte grant;
        try { grant = GemMessages.ParseS6F6(t.Result!); }
        catch (Exception ex) when (ex is FormatException or NullReferenceException)
        {
            RefusedLocked(o, GemSendFailure.InvalidReply, null, ex);
            return true;
        }
        if (grant != GemAck.Grant6Granted)
        {
            RefusedLocked(o, GemSendFailure.NotGranted, grant, null);
            return true;
        }
        o.Inquired = true;
        resumeNext = o;
        return true;
    }

    void InquiryDone(Outgoing q, Task<SecsMessage?> t)
    {
        var o = q.InquiryFor!;
        o.Done(o, t);
    }

    // 보내지 않기로 한 원래 메시지(gate 안): 실시간 메시지는 SendFailed, 스풀 머리는 지우고 내보내기를 이어 간다(ack ≠ 0과 같게, CE 없음)
    void RefusedLocked(Outgoing o, GemSendFailure reason, byte? ack, Exception? ex)
    {
        var e = new GemSendFailedEventArgs(o.Message, reason, ack, ex);
        if (o.SpoolSeq != null) RefusedSpoolHeadLocked(o, e);
        else Raise(SendFailed, e);
    }
}
