using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Hsms;

namespace Devinno.Communication.Tests.Secs.Hsms;

/// HSMS 프레임. C_ = 캡처(Wireshark 이슈 12942 sample_hsms.pcapng, secsgem끼리 루프백)의 프레임 번호, D_ = 파생.
public class HsmsFrameTests
{
    static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    /// 프레임 → (헤더, 본문)
    static (HsmsHeader Header, byte[] Body) Split(byte[] frame)
    {
        Assert.Equal(frame.Length - 4, (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(frame));
        return (HsmsHeader.Parse(frame.AsSpan(4, 10)), frame[14..]);
    }

    [Fact]
    public void C4_SelectReq()
    {
        var f = Hex("00 00 00 0a ff ff 00 00 00 01 1e 3a 32 53");
        Assert.Equal(f, HsmsFrame.Control(HsmsSType.SelectReq, 0x1E3A3253));
        Assert.Equal(new HsmsHeader(0xFFFF, 0, 0, 0, 1, 0x1E3A3253), Split(f).Header);
    }

    [Fact]
    public void C6_SelectRsp_Status0_EchoesSystemBytes()
        => Assert.Equal(Hex("00 00 00 0a ff ff 00 00 00 02 1e 3a 32 53"),
            HsmsFrame.Control(HsmsSType.SelectRsp, 0x1E3A3253, byte3: HsmsFrame.SelectOk));

    [Fact]
    public void C8_HostS1F13_EmptyList()
    {
        var f = Hex("00 00 00 0c 00 00 81 0d 00 00 1e 3a 32 54 01 00");
        var m = new SecsMessage(1, 13, true, SecsItem.L()).WithHeader(0x1E3A3254, 0);
        Assert.Equal(f, HsmsFrame.Data(m));
        var (h, body) = Split(f);
        var back = HsmsFrame.ToMessage(h, body);
        Assert.Equal((1, 13, true, 0x1E3A3254u, 0), (back.Stream, back.Function, back.ReplyExpected, back.SystemBytes, back.DeviceID));
        Assert.Equal(SecsItem.L(), back.Item);
    }

    [Fact]
    public void C9_EquipmentS1F13()
    {
        var f = Hex("00 00 00 1c 00 00 81 0d 00 00 73 2c 90 e5 01 02 41 07 67 65 6d 65 71 75 70 41 05 31 2e 30 2e 30");
        var m = new SecsMessage(1, 13, true, SecsItem.L(SecsItem.A("gemequp"), SecsItem.A("1.0.0"))).WithHeader(0x732C90E5, 0);
        Assert.Equal(f, HsmsFrame.Data(m));
        Assert.Equal("S1F13 W <L [2] <A \"gemequp\"> <A \"1.0.0\">>", HsmsFrame.ToMessage(Split(f).Header, Split(f).Body).ToSml());
    }

    [Fact]
    public void C10_HostS1F14_ReplyHasNoWBit_SameSystemBytes()
    {
        var f = Hex("00 00 00 11 00 00 01 0e 00 00 73 2c 90 e5 01 02 21 01 00 01 00");
        var m = new SecsMessage(1, 14, false, SecsItem.L(SecsItem.B(0), SecsItem.L())).WithHeader(0x732C90E5, 0);
        Assert.Equal(f, HsmsFrame.Data(m));
    }

    [Fact]
    public void C11_EquipmentS1F14()
    {
        var f = Hex("00 00 00 21 00 00 01 0e 00 00 1e 3a 32 54 01 02 21 01 00 01 02 41 07 67 65 6d 65 71 75 70 41 05 31 2e 30 2e 30");
        var m = new SecsMessage(1, 14, false, SecsItem.L(SecsItem.B(0), SecsItem.L(SecsItem.A("gemequp"), SecsItem.A("1.0.0")))).WithHeader(0x1E3A3254, 0);
        Assert.Equal(f, HsmsFrame.Data(m));
    }

    [Fact]
    public void C13_S6F11_WBitStream6()
    {
        var f = Hex("00 00 00 24 00 00 86 0b 00 00 73 2c 90 e6 01 03 a5 01 01 a9 02 05 39 01 01 01 02 a9 02 03 e8 01 02 21 01 01 21 02 01 02");
        var (h, body) = Split(f);
        var m = HsmsFrame.ToMessage(h, body);
        Assert.Equal((6, 11, true, 0x732C90E6u), (m.Stream, m.Function, m.ReplyExpected, m.SystemBytes));
        Assert.Equal(f, HsmsFrame.Data(m));
    }

    [Fact]
    public void C39_C42_SeparateReq_BothDirections()
    {
        Assert.Equal(Hex("00 00 00 0a ff ff 00 00 00 09 73 2c 90 f3"), HsmsFrame.Control(HsmsSType.SeparateReq, 0x732C90F3));
        Assert.Equal(Hex("00 00 00 0a ff ff 00 00 00 09 1e 3a 32 55"), HsmsFrame.Control(HsmsSType.SeparateReq, 0x1E3A3255));
    }

    [Fact]
    public void D_HeaderOnlyData_DeviceIdInSessionId()
    {
        var m = new SecsMessage(1, 1, true).WithHeader(1, 0x1234);
        Assert.Equal(Hex("00 00 00 0a 12 34 81 01 00 00 00 00 00 01"), HsmsFrame.Data(m));
        var back = HsmsFrame.ToMessage(HsmsHeader.Parse(Hex("12 34 81 01 00 00 00 00 00 01")), []);
        Assert.Null(back.Item);
        Assert.Equal(0x1234, back.DeviceID);
    }

    [Fact]
    public void D_ControlMessages()
    {
        Assert.Equal(Hex("00 00 00 0a ff ff 00 00 00 05 00 00 00 07"), HsmsFrame.Control(HsmsSType.LinktestReq, 7));
        Assert.Equal(Hex("00 00 00 0a ff ff 00 00 00 06 00 00 00 07"), HsmsFrame.Control(HsmsSType.LinktestRsp, 7));
        // Select.rsp는 요청의 Session ID를 돌려준다
        Assert.Equal(Hex("00 00 00 0a 00 05 00 01 00 02 00 00 00 08"), HsmsFrame.Control(HsmsSType.SelectRsp, 8, session: 5, byte3: HsmsFrame.SelectAlreadyActive));
        Assert.Equal(Hex("00 00 00 0a ff ff 00 00 00 04 00 00 00 09"), HsmsFrame.Control(HsmsSType.DeselectRsp, 9));
        // Reject: 바이트2 = 거부한 SType(이유 2면 PType), 바이트3 = 이유
        Assert.Equal(Hex("00 00 00 0a ff ff 00 04 00 07 00 00 00 0a"), HsmsFrame.Control(HsmsSType.RejectReq, 10, byte2: 0, byte3: HsmsFrame.RejectNotSelected));
        Assert.Equal(Hex("00 00 00 0a ff ff 0b 01 00 07 00 00 00 0b"), HsmsFrame.Control(HsmsSType.RejectReq, 11, byte2: 11, byte3: HsmsFrame.RejectSType));
    }

    [Fact]
    public void ToMessage_BrokenBody_IsFormatException()
        => Assert.Throws<FormatException>(() => HsmsFrame.ToMessage(HsmsHeader.Parse(Hex("00 00 81 01 00 00 00 00 00 01")), Hex("a9 03 00 01 02")));
}

public class HsmsFrameReaderTests
{
    static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    static readonly byte[] Select = Hex("00 00 00 0a ff ff 00 00 00 01 1e 3a 32 53");
    static readonly byte[] S1F13 = Hex("00 00 00 0c 00 00 81 0d 00 00 1e 3a 32 54 01 00");

    [Fact]
    public void ByteByByte_AndManyFramesAtOnce()
    {
        var r = new HsmsFrameReader(() => 1024);
        var output = new List<HsmsFrameReader.Frame>();
        foreach (var b in S1F13)
        {
            r.Feed([b], output);
            if (output.Count == 0) Assert.True(r.InFrame);
        }
        Assert.False(r.InFrame);
        Assert.Single(output);
        Assert.Equal(Hex("01 00"), output[0].Body);
        Assert.Equal(0x1E3A3254u, output[0].Header.SystemBytes);

        output.Clear();
        r.Feed([.. Select, .. S1F13, .. Select], output);
        Assert.Equal(new byte[] { 1, 0, 1 }, output.Select(f => f.Header.SType).ToArray());
        Assert.Empty(output[0].Body);
    }

    [Fact]
    public void LengthBelow10_IsFormatException()
    {
        var r = new HsmsFrameReader(() => 1024);
        Assert.Throws<FormatException>(() => r.Feed(Hex("00 00 00 09"), []));
    }

    [Fact]
    public void TooLong_HeaderKept_BodySkipped_NextFrameFine()
    {
        var r = new HsmsFrameReader(() => 1024);
        var output = new List<HsmsFrameReader.Frame>();
        var big = new byte[4 + 10 + 2000];
        big[2] = 0x07; big[3] = 0xDA;                     // 길이 2010
        Hex("00 01 81 03 00 00 00 00 00 09").CopyTo(big, 4);
        r.Feed(big.AsSpan(0, 700), output);
        r.Feed(big.AsSpan(700), output);
        r.Feed(Select, output);
        Assert.Equal(2, output.Count);
        Assert.Equal(2010, output[0].TooLongLength);
        Assert.Equal((ushort)1, output[0].Header.SessionId);
        Assert.Equal(9u, output[0].Header.SystemBytes);
        Assert.Equal(0, output[1].TooLongLength);
    }

    [Fact]
    public void ExactlyMax_IsAccepted()
    {
        var r = new HsmsFrameReader(() => 12);
        var output = new List<HsmsFrameReader.Frame>();
        r.Feed(S1F13, output);   // 길이 12
        Assert.Equal(0, output[0].TooLongLength);
        Assert.Equal(2, output[0].Body.Length);
    }
}
