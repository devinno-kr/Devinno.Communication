using Devinno.Communication.Mitsubishi.Data;
using Devinno.Communication.Mitsubishi.Ethernet;

namespace Devinno.Communication.Tests.Mitsubishi.Ethernet;

public class Mc1EFrameTests
{
    static byte[] H(string hex) => Convert.FromHexString(hex.Replace(" ", ""));
    static McDevice D(string n, bool octal = true) => McDevice.Parse(n, McTarget.Ethernet1E, octal, "x");

    [Fact]
    public void G1_G2_BitRead_M100()
    {
        // FX3U-ENET-ADP §7.5.2 / §7.3.3 (감시 타이머 0AH)
        Assert.Equal(H("00 FF 0A 00 64 00 00 00 20 4D 0C 00"), Mc1EFrame.Request(McCommand.BR, D("M100"), 12, null, 0x0A));
        Assert.Equal(6, Mc1EFrame.DataLength(McCommand.BR, 12));
        Assert.Equal(new long[] { 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0 }, Mc1EFrame.Parse(H("80 00 10 10 10 10 10 10"), McCommand.BR, 12).Values);

        Assert.Equal(H("00 FF 0A 00 64 00 00 00 20 4D 08 00"), Mc1EFrame.Request(McCommand.BR, D("M100"), 8, null, 0x0A));
        Assert.Equal(new long[] { 1, 0, 1, 0, 0, 1, 1, 0 }, Mc1EFrame.Parse(H("80 00 10 10 01 10"), McCommand.BR, 8).Values);
    }

    [Fact]
    public void G3_G4_G5_WordRead()
    {
        // G3: Y000부터 1워드 (FX 8진) → 229DH
        Assert.Equal(H("01 FF 0A 00 00 00 00 00 20 59 01 00"), Mc1EFrame.Request(McCommand.WR, D("Y0"), 1, null, 0x0A));
        Assert.Equal(new long[] { 0x229D }, Mc1EFrame.Parse(H("81 00 9D 22"), McCommand.WR, 1).Values);
        // G4: A 시리즈 Y40(16진) 2워드 → 829DH, 553EH (요청은 뒷부분만 공식 예제)
        Assert.Equal(H("40 00 00 00 20 59 02 00"), Mc1EFrame.Request(McCommand.WR, D("Y40", octal: false), 2, null, 0x0A)[4..]);
        Assert.Equal(new long[] { 0x829D, 0x553E }, Mc1EFrame.Parse(H("81 00 9D 82 3E 55"), McCommand.WR, 2).Values);
        // G5: CN220부터 64점
        Assert.Equal(H("01 FF 0A 00 DC 00 00 00 4E 43 40 00"), Mc1EFrame.Request(McCommand.WR, D("CN220"), 64, null, 0x0A));
    }

    [Fact]
    public void G6_G7_Writes()
    {
        Assert.Equal(H("02 FF 0A 00 32 00 00 00 20 4D 0C 00 01 11 01 00 00 01"),
            Mc1EFrame.Request(McCommand.BW, D("M50"), 12, [0, 1, 1, 1, 0, 1, 0, 0, 0, 0, 0, 1], 0x0A));
        Assert.Equal(McStatus.Ack, Mc1EFrame.Parse(H("82 00"), McCommand.BW, 12).Status);
        Assert.Equal(H("03 FF 0A 00 64 00 00 00 20 44 03 00 34 12 76 98 09 01"),
            Mc1EFrame.Request(McCommand.WW, D("D100"), 3, [0x1234, 0x9876, 0x0109], 0x0A));
        Assert.Equal(McStatus.Ack, Mc1EFrame.Parse(H("83 00"), McCommand.WW, 3).Status);
        Assert.Equal(0, Mc1EFrame.DataLength(McCommand.WW, 3));
    }

    [Fact]
    public void Derived_OctalX17_And_256Points()
    {
        // derived: X17(8진) = 0FH, 코드 20 58 / 256점은 점수 바이트 00
        Assert.Equal(H("0F 00 00 00 20 58"), Mc1EFrame.Request(McCommand.BR, D("X17"), 1, null, 0x0A)[4..10]);
        Assert.Equal(H("00 FF 0A 00 00 00 00 00 20 4D 00 00"), Mc1EFrame.Request(McCommand.BR, D("M0"), 256, null, 0x0A));
        Assert.Equal(128, Mc1EFrame.DataLength(McCommand.BR, 256));
    }

    [Fact]
    public void G8_G9_AbnormalResponse_ThreeOrFourBytes()
    {
        // G8: FX3 형식 80 5B 12 00, G9: A/FX5 형식 81 5B 10
        var r8 = Mc1EFrame.Parse(H("80 5B 12 00"), McCommand.BR, 1);
        Assert.Equal((McStatus.Nak, 0x5B12), (r8.Status, r8.ErrorCode));
        var r9 = Mc1EFrame.Parse(H("81 5B 10"), McCommand.WR, 1);
        Assert.Equal((McStatus.Nak, 0x5B10), (r9.Status, r9.ErrorCode));
        var e = Mc1EFrame.Parse(H("81 56"), McCommand.WR, 1);
        Assert.Equal((McStatus.Nak, 0x56), (e.Status, e.ErrorCode));
    }

    [Fact]
    public void Invalid_Responses()
    {
        Assert.Equal(McStatus.Invalid, Mc1EFrame.Parse(H("81 00 9D 22"), McCommand.BR, 1).Status);   // 다른 명령의 서브헤더
        Assert.Equal(McStatus.Invalid, Mc1EFrame.Parse(H("81 00 9D"), McCommand.WR, 1).Status);      // 데이터 부족
        Assert.Equal(McStatus.Invalid, Mc1EFrame.Parse(H("81 5B 10 01"), McCommand.WR, 1).Status);   // 4번째 바이트가 00이 아님
        Assert.Equal(McStatus.Invalid, Mc1EFrame.Parse(H("81 56 00"), McCommand.WR, 1).Status);
        Assert.Equal(McStatus.Invalid, Mc1EFrame.Parse(H("80"), McCommand.BR, 1).Status);
    }
}
