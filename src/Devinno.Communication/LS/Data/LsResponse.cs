namespace Devinno.Communication.LS.Data;

internal enum LsStatus { Ack, Nak, BccError, Invalid }

// 응답 해석 결과. Ack면 Values(읽기) 사용, Nak면 ErrorCode 사용.
internal readonly record struct LsResponse(LsStatus Status, long[] Values, int ErrorCode)
{
    public static LsResponse Invalid => new(LsStatus.Invalid, [], 0);
}
