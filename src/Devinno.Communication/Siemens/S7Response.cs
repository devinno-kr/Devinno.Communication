namespace Devinno.Communication.Siemens;

internal enum S7Status { Ack, Nak, Invalid }

// 응답 해석 결과. Ack면 Items(읽기 항목별 데이터 바이트), Nak면 ErrorCode와 ItemIndex(-1 = 헤더 오류).
internal readonly record struct S7Response(S7Status Status, byte[][] Items, int ErrorCode, int ItemIndex)
{
    public static S7Response Invalid => new(S7Status.Invalid, [], 0, -1);
    public static S7Response Nak(int errorCode, int itemIndex) => new(S7Status.Nak, [], errorCode, itemIndex);
    public static S7Response Ack(byte[][] items) => new(S7Status.Ack, items, 0, -1);
}
