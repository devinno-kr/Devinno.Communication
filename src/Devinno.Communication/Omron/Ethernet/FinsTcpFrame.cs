using System.Buffers.Binary;
using Devinno.Communication.Omron.Data;

namespace Devinno.Communication.Omron.Ethernet;

// FINS/TCP 머리 16바이트 = "FINS" | 길이 4 | 명령 4 | 오류 코드 4 (모두 빅엔디언). 길이 = 명령부터 끝까지의 바이트 수 (W421 7-4-2).
internal static class FinsTcpFrame
{
    public const int HeaderLength = 16;
    // 노드 주소 보내기(PC→PLC), 노드 주소 응답(PLC→PC), FINS 프레임, 오류 통지, 연결 확인
    public const uint CmdNodeRequest = 0, CmdNodeReply = 1, CmdFrame = 2, CmdErrorNotice = 3, CmdConfirmation = 6;
    // 노드 응답 전체 길이 (머리 16 + 클라이언트 노드 4 + 서버 노드 4)
    public const int NodeReplyLength = 24;
    // FINS 프레임 최대 길이 (W421 7-1-3: 2012바이트)
    public const int MaxFinsLength = 2012;

    static readonly byte[] Magic = "FINS"u8.ToArray();

    static byte[] Header(int length, uint command, uint error)
    {
        var h = new byte[HeaderLength];
        Magic.CopyTo(h, 0);
        BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(4), length);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(8), command);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(12), error);
        return h;
    }

    // 접속 직후 보내는 노드 주소 요청. clientNode 0이면 PLC가 자동으로 정한다.
    public static byte[] NodeRequest(int clientNode) => [.. Header(12, CmdNodeRequest, 0), 0, 0, 0, (byte)clientNode];

    // FINS 프레임 보내기 (명령 2)
    public static byte[] Frame(ReadOnlySpan<byte> fins) => [.. Header(8 + fins.Length, CmdFrame, 0), .. fins];

    // 머리 16바이트 해석: (명령, 오류 코드, 머리 뒤에 오는 바이트 수). "FINS"가 아니거나 길이가 8 미만·너무 크면 IOException(스트림이 깨짐 → 재접속).
    public static (uint Command, uint Error, int PayloadLength) ParseHeader(ReadOnlySpan<byte> header)
    {
        if (!header[..4].SequenceEqual(Magic)) throw new IOException("FINS/TCP 머리가 \"FINS\"로 시작하지 않습니다.");
        long length = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        if (length < 8 || length > 8 + MaxFinsLength) throw new IOException($"FINS/TCP 길이가 올바르지 않습니다: {length}");
        return (BinaryPrimitives.ReadUInt32BigEndian(header[8..]), BinaryPrimitives.ReadUInt32BigEndian(header[12..]), (int)length - 8);
    }

    // 노드 주소 응답 24바이트 해석: (클라이언트 노드, 서버 노드). 명령·길이·오류 코드가 틀리거나 노드가 1~254 밖이면 null.
    public static (int Client, int Server)? ParseNodeReply(ReadOnlySpan<byte> reply)
    {
        if (reply.Length != NodeReplyLength || !reply[..4].SequenceEqual(Magic)) return null;
        if (BinaryPrimitives.ReadUInt32BigEndian(reply[4..]) != 16 || BinaryPrimitives.ReadUInt32BigEndian(reply[8..]) != CmdNodeReply
            || BinaryPrimitives.ReadUInt32BigEndian(reply[12..]) != 0) return null;
        uint client = BinaryPrimitives.ReadUInt32BigEndian(reply[16..]), server = BinaryPrimitives.ReadUInt32BigEndian(reply[20..]);
        return client is >= 1 and <= 254 && server is >= 1 and <= 254 ? ((int)client, (int)server) : null;
    }

    // FINS/TCP에서 쓰는 FINS 헤더 주소: 목적지 = 서버 노드, 출발지 = 클라이언트 노드 (네트워크·유닛은 0)
    public static FinsNodes Nodes(int client, int server) => new(0, (byte)server, 0, 0, (byte)client, 0);
}
