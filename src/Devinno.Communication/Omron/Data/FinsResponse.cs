namespace Devinno.Communication.Omron.Data;

// Ack 정상, Nak PLC 오류(종료 코드), Invalid 응답 없음·형식 불일치(타임아웃 경로), FcsError Host Link FCS 불일치(FinsSerial)
internal enum FinsStatus { Ack, Nak, Invalid, FcsError }

// 응답 해석 결과. Ack면 Values(읽은 워드 0~65535 / 비트 0/1), Nak면 ErrorCode(CPU 오류 플래그·중계 비트를 뺀 종료 코드).
internal readonly record struct FinsResponse(FinsStatus Status, long[] Values, int ErrorCode)
{
    public static FinsResponse Invalid => new(FinsStatus.Invalid, [], 0);
    public static FinsResponse FcsError => new(FinsStatus.FcsError, [], 0);
    public static FinsResponse Nak(int errorCode) => new(FinsStatus.Nak, [], errorCode);
    public static FinsResponse Ack(long[] values) => new(FinsStatus.Ack, values, 0);
}

// 요청 하나(조각 하나)의 FINS 본문(MRC부터)과 응답 해석에 필요한 정보.
// Items: 0104는 요청한 주소들, 0101/0102는 이 조각의 시작 주소 하나. Count: 읽거나 쓸 요소 수(0104는 주소 수).
internal sealed record FinsChunk(byte[] Body, FinsAddress[] Items, int Count);
