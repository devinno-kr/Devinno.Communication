using System.Buffers.Binary;
using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Hsms;

// HSMS 메시지 종류(SType)
internal enum HsmsSType : byte
{
    Data = 0,
    SelectReq = 1,
    SelectRsp = 2,
    DeselectReq = 3,
    DeselectRsp = 4,
    LinktestReq = 5,
    LinktestRsp = 6,
    RejectReq = 7,
    SeparateReq = 9,
}

// HSMS 헤더 10바이트: Session ID 2, 바이트2(데이터: W + Stream), 바이트3(데이터: Function, 제어: 상태·이유), PType, SType, System Bytes 4 (빅엔디언)
internal readonly record struct HsmsHeader(ushort SessionId, byte Byte2, byte Byte3, byte PType, byte SType, uint SystemBytes)
{
    public const int Length = 10;

    public static HsmsHeader Parse(ReadOnlySpan<byte> h)
        => new(BinaryPrimitives.ReadUInt16BigEndian(h), h[2], h[3], h[4], h[5], BinaryPrimitives.ReadUInt32BigEndian(h[6..]));

    public byte[] ToArray()
    {
        var h = new byte[Length];
        BinaryPrimitives.WriteUInt16BigEndian(h, SessionId);
        h[2] = Byte2;
        h[3] = Byte3;
        h[4] = PType;
        h[5] = SType;
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(6), SystemBytes);
        return h;
    }
}

// HSMS-SS 프레임 = 길이 4바이트(뒤따르는 바이트 수, 최소 10) + 헤더 10바이트 + 본문(데이터 메시지만)
internal static class HsmsFrame
{
    // SS 제어 메시지의 Session ID
    public const ushort ControlSession = 0xFFFF;
    // Select.rsp 상태: 0 성공, 1 이미 활성
    public const byte SelectOk = 0, SelectAlreadyActive = 1;
    // Reject 이유: 1 SType 미지원, 2 PType 미지원, 3 열린 트랜잭션 없음, 4 Selected 아님
    public const byte RejectSType = 1, RejectPType = 2, RejectNoTransaction = 3, RejectNotSelected = 4;

    // 데이터 메시지 헤더 (Session ID = 장치 ID, 바이트2 = W + Stream, 바이트3 = Function)
    public static HsmsHeader DataHeader(SecsMessage wire)
        => new((ushort)wire.DeviceID, (byte)((wire.ReplyExpected ? 0x80 : 0) | wire.Stream), (byte)wire.Function, 0, (byte)HsmsSType.Data, wire.SystemBytes);

    // 데이터 메시지 프레임 (System Bytes·장치 ID가 채워진 메시지)
    public static byte[] Data(SecsMessage wire) => Frame(DataHeader(wire), SecsCodec.Encode(wire.Item));

    // 제어 메시지 프레임 (본문 없음)
    public static byte[] Control(HsmsSType type, uint systemBytes, ushort session = ControlSession, byte byte2 = 0, byte byte3 = 0)
        => Frame(new HsmsHeader(session, byte2, byte3, 0, (byte)type, systemBytes), []);

    static byte[] Frame(HsmsHeader h, byte[] body)
    {
        var f = new byte[4 + HsmsHeader.Length + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(f, (uint)(HsmsHeader.Length + body.Length));
        h.ToArray().CopyTo(f, 4);
        body.CopyTo(f, 4 + HsmsHeader.Length);
        return f;
    }

    // 데이터 메시지 헤더 + 본문 → 메시지. 본문을 해석하지 못하면 FormatException
    public static SecsMessage ToMessage(HsmsHeader h, ReadOnlySpan<byte> body)
        => new SecsMessage(h.Byte2 & 0x7F, h.Byte3, (h.Byte2 & 0x80) != 0, SecsCodec.Decode(body)).WithHeader(h.SystemBytes, h.SessionId & 0x7FFF);
}

// 받은 바이트를 HSMS 프레임으로 자른다(나뉘어 온 조각, 한 번에 온 여러 프레임 모두 처리).
// 길이 필드(헤더 + 본문)가 maxMessageSize를 넘으면 헤더만 남기고 본문은 받는 대로 버린 뒤 TooLongLength를 채워 내보낸다.
internal sealed class HsmsFrameReader(Func<int> maxMessageSize)
{
    // 받은 프레임. TooLongLength가 0보다 크면 최대 크기를 넘어 본문을 버린 것(Body는 빈 배열)
    public readonly record struct Frame(HsmsHeader Header, byte[] Body, long TooLongLength);

    readonly byte[] lengthBytes = new byte[4];
    readonly byte[] headerBytes = new byte[HsmsHeader.Length];
    int lengthFill, headerFill;
    long length, bodyFill;
    byte[]? body;
    bool skipping;

    // 프레임 중간까지 받은 상태인지(T8 검사용)
    public bool InFrame => lengthFill > 0;

    // 받은 바이트를 넣는다. 완성된 프레임은 output에 추가. 길이 필드가 10보다 작으면 FormatException(연결을 끊어야 함)
    public void Feed(ReadOnlySpan<byte> data, List<Frame> output)
    {
        while (!data.IsEmpty)
        {
            if (lengthFill < 4)
            {
                int n = Math.Min(4 - lengthFill, data.Length);
                data[..n].CopyTo(lengthBytes.AsSpan(lengthFill));
                lengthFill += n;
                data = data[n..];
                if (lengthFill < 4) return;
                length = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
                if (length < HsmsHeader.Length) throw new FormatException($"HSMS 길이 필드 {length}이(가) 10보다 작습니다.");
                headerFill = 0;
                continue;
            }
            if (headerFill < HsmsHeader.Length)
            {
                int n = Math.Min(HsmsHeader.Length - headerFill, data.Length);
                data[..n].CopyTo(headerBytes.AsSpan(headerFill));
                headerFill += n;
                data = data[n..];
                if (headerFill < HsmsHeader.Length) return;
                skipping = length > maxMessageSize();
                body = skipping ? null : new byte[length - HsmsHeader.Length];
                bodyFill = 0;
                if (length == HsmsHeader.Length) Complete(output);
                continue;
            }
            long need = length - HsmsHeader.Length - bodyFill;
            int take = (int)Math.Min(need, data.Length);
            if (!skipping) data[..take].CopyTo(body.AsSpan((int)bodyFill));
            bodyFill += take;
            data = data[take..];
            if (bodyFill == length - HsmsHeader.Length) Complete(output);
        }
    }

    void Complete(List<Frame> output)
    {
        var header = HsmsHeader.Parse(headerBytes);
        output.Add(skipping ? new Frame(header, [], length) : new Frame(header, body!, 0));
        lengthFill = 0;
        headerFill = 0;
        body = null;
        skipping = false;
    }
}
