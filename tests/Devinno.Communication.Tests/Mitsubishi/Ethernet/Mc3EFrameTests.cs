using Devinno.Communication.Mitsubishi.Data;
using Devinno.Communication.Mitsubishi.Ethernet;

namespace Devinno.Communication.Tests.Mitsubishi.Ethernet;

public class Mc3EFrameTests
{
    static byte[] H(string hex) => Convert.FromHexString(hex.Replace(" ", ""));
    static McDevice D(string n, bool octal = false) => McDevice.Parse(n, McTarget.Ethernet3E, octal, "x");

    [Fact]
    public void ReadWords_D100_ManualExample()
    {
        // SH-080008 예제의 요청 데이터 + 3E 헤더 (근거 조사 §4.2, 감시 타이머 10H)
        Assert.Equal(H("50 00 00 FF FF 03 00 0C 00 10 00 01 04 00 00 64 00 00 A8 03 00"),
            Mc3EFrame.Request(McCommand.WR, D("D100"), 3, null, 0x10));
        Assert.Equal(8, Mc3EFrame.BodyLength(H("D0 00 00 FF FF 03 00 08 00")));
        var r = Mc3EFrame.Parse(H("00 00 34 12 02 00 EF 1D"), McCommand.WR, 3);
        Assert.Equal(McStatus.Ack, r.Status);
        Assert.Equal(new long[] { 0x1234, 0x0002, 0x1DEF }, r.Values);
    }

    [Fact]
    public void WriteWords_D100_ManualExample()
    {
        Assert.Equal(H("50 00 00 FF FF 03 00 12 00 10 00 01 14 00 00 64 00 00 A8 03 00 95 19 02 12 30 11"),
            Mc3EFrame.Request(McCommand.WW, D("D100"), 3, [0x1995, 0x1202, 0x1130], 0x10));
        Assert.Equal(2, Mc3EFrame.BodyLength(H("D0 00 00 FF FF 03 00 02 00")));
        Assert.Equal(McStatus.Ack, Mc3EFrame.Parse(H("00 00"), McCommand.WW, 3).Status);
    }

    [Fact]
    public void ReadBits_M100_ManualExample()
    {
        // 요청 데이터 01 04 01 00 64 00 00 90 08 00, 응답 데이터 00 01 00 11 (1점 4비트, 앞 점이 상위 니블)
        var req = Mc3EFrame.Request(McCommand.BR, D("M100"), 8, null, 0x10);
        Assert.Equal(H("01 04 01 00 64 00 00 90 08 00"), req[11..]);
        var r = Mc3EFrame.Parse(H("00 00 00 01 00 11"), McCommand.BR, 8);
        Assert.Equal(new long[] { 0, 0, 0, 1, 0, 0, 1, 1 }, r.Values);
    }

    [Fact]
    public void Derived_BitWriteOdd_HexX_NegativeWord()
    {
        // derived: M100~M104 = 1,1,0,1,1 → 11 01 10 (홀수 패딩), 길이 0F
        Assert.Equal(H("50 00 00 FF FF 03 00 0F 00 10 00 01 14 01 00 64 00 00 90 05 00 11 01 10"),
            Mc3EFrame.Request(McCommand.BW, D("M100"), 5, [1, 1, 0, 1, 1], 0x10));
        // derived: X1F(16진) = 1FH, 코드 9C
        Assert.Equal(H("50 00 00 FF FF 03 00 0C 00 04 00 01 04 01 00 1F 00 00 9C 01 00"),
            Mc3EFrame.Request(McCommand.BR, D("X1F"), 1, null, 4));
        // derived: 3바이트 번호(ZR 16진 123456), WW 음수는 2의 보수
        var big = Mc3EFrame.Request(McCommand.WW, D("ZR123456"), 1, [-2], 1);
        Assert.Equal(H("56 34 12 B0 01 00 FE FF"), big[15..]);
    }

    [Fact]
    public void ErrorResponse_EndCodeIsErrorCode()
    {
        // derived: 종료코드 C051 + 오류 정보(경로 5 + 명령 2 + 서브커맨드 2) → 길이 0B
        Assert.Equal(11, Mc3EFrame.BodyLength(H("D0 00 00 FF FF 03 00 0B 00")));
        var r = Mc3EFrame.Parse(H("51 C0 00 FF FF 03 00 01 04 00 00"), McCommand.WR, 3);
        Assert.Equal((McStatus.Nak, 0xC051), (r.Status, r.ErrorCode));
    }

    [Fact]
    public void BadHeader_And_WrongDataLength()
    {
        Assert.Equal(-1, Mc3EFrame.BodyLength(H("D4 00 00 FF FF 03 00 02 00")));   // 4E 서브헤더
        Assert.Equal(-1, Mc3EFrame.BodyLength(H("D0 00 01 FF FF 03 00 02 00")));   // 경로 다름
        Assert.Equal(-1, Mc3EFrame.BodyLength(H("D0 00 00 FF FF 03 00 02")));      // 짧음
        Assert.Equal(McStatus.Invalid, Mc3EFrame.Parse(H("00 00 34 12"), McCommand.WR, 3).Status);
        Assert.Equal(McStatus.Invalid, Mc3EFrame.Parse(H("00"), McCommand.WR, 1).Status);
    }
}
