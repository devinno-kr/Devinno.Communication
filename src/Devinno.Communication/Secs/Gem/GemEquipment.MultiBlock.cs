using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Serial;

namespace Devinno.Communication.Secs.Gem;

// SECS-I 다중 블록 문의 S6F5/F6(6J.6.2): 켰을 때만, 244바이트를 넘는 S6F11 앞에. 허락이면 바로 그 메시지, 아니면 보내지 않음
public partial class GemEquipment
{
    const int SingleBlockLimit = 244;     // SECS-I 블록 하나의 데이터 상한

    bool multiBlockInquire;
    Outgoing? resumeNext;                 // gate: S6F5 허락을 받아 다음에 바로 보낼 원래 항목

    /// <summary>Gets or sets whether, over SECS-I, an S6F11 whose body is larger than one block (244 bytes) is preceded by S6F5 Multi-block Data Send Inquire (default: false).</summary>
    /// <remarks>If the Host answers GRANT6 0 the S6F11 follows at once; otherwise it is not sent and <see cref="SendFailed"/> (<see cref="GemSendFailure.NotGranted"/>, Ack = GRANT6) is raised, without a retry. A spooled S6F11 refused this way is removed from the spool and the transmit goes on. Over HSMS nothing changes even if this is true (HSMS has no blocks).</remarks>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public bool MultiBlockInquire { get => multiBlockInquire; set => Setting(() => multiBlockInquire = value); }

    // Pump가 꺼낸 항목을 S6F5 문의 항목으로 바꿀지(gate 안). 바꾸지 않으면 null
    Outgoing? InquiryLocked(Outgoing o)
    {
        if (!multiBlockInquire || o.Inquired || o.InquiryFor != null || transport.Target is not SecsSerial) return null;
        if (o.Message is not { Stream: 6, Function: 11, Item: { } body }) return null;
        int length = SecsCodec.Encode(body).Length;
        if (length <= SingleBlockLimit) return null;
        return new Outgoing(GemMessages.S6F5(body.Items[0], (uint)length), InquiryDone) { InquiryFor = o, SpoolSeq = o.SpoolSeq, Run = o.Run };
    }

    // S6F6을 받았다(Completed 뒤, gate 밖). 실패(T3·SxF0·S9·회선)는 원래 메시지의 완료처럼 처리한다(지금 규칙 그대로: SendFailed, 스풀 머리는 6I.5 표)
    void InquiryDone(Outgoing q, Task<SecsMessage?> t)
    {
        var o = q.InquiryFor!;
        if (!t.IsCompletedSuccessfully)
        {
            o.Done(o, t);
            return;
        }
        byte grant;
        try { grant = GemMessages.ParseS6F6(t.Result!); }
        catch (Exception ex) when (ex is FormatException or NullReferenceException)
        {
            Refused(o, GemSendFailure.InvalidReply, null, ex);
            return;
        }
        if (grant != GemAck.Grant6Granted)
        {
            Refused(o, GemSendFailure.NotGranted, grant, null);
            return;
        }
        GemLoop? w;
        lock (gate)
        {
            if (!running || o.SpoolSeq != null && o.Run != run) return;
            o.Inquired = true;
            resumeNext = o;
            w = worker;
        }
        w?.Post(Pump);
    }

    // 보내지 않기로 한 원래 메시지: 실시간 메시지는 SendFailed, 스풀 머리는 지우고 내보내기를 이어 간다(ack ≠ 0과 같게, CE 없음)
    void Refused(Outgoing o, GemSendFailure reason, byte? ack, Exception? ex)
    {
        lock (gate)
        {
            if (!running) return;
            if (o.SpoolSeq != null)
            {
                RefusedSpoolHeadLocked(o, new GemSendFailedEventArgs(o.Message, reason, ack, ex));
                return;
            }
            Raise(SendFailed, new GemSendFailedEventArgs(o.Message, reason, ack, ex));
        }
    }
}
