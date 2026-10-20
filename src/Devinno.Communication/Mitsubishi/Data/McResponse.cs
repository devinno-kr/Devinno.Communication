namespace Devinno.Communication.Mitsubishi.Data;

internal enum McStatus { Ack, Nak, SumError, Invalid }

// 응답 해석 결과. Ack면 Values(읽기) 사용, Nak면 ErrorCode 사용.
internal readonly record struct McResponse(McStatus Status, long[] Values, int ErrorCode)
{
    public static McResponse Invalid => new(McStatus.Invalid, [], 0);
}
