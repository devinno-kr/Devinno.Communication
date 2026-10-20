using System.Text;
using Devinno.Communication.Mitsubishi.Data;
using Devinno.Communication.Mitsubishi.Serial;

namespace Devinno.Communication.Tests.Mitsubishi.Serial;

public class McSerialFrameTests
{
    /// "<ENQ>05FF…" 같은 표기를 바이트로. 제어 문자는 (char) 변환으로 넣는다.
    static byte[] A(string s) => Encoding.ASCII.GetBytes(s
        .Replace("<ENQ>", ((char)0x05).ToString()).Replace("<STX>", ((char)0x02).ToString())
        .Replace("<ETX>", ((char)0x03).ToString()).Replace("<ACK>", ((char)0x06).ToString())
        .Replace("<NAK>", ((char)0x15).ToString()).Replace("<CR>", "\r").Replace("<LF>", "\n"));
    static byte[] H(string hex) => Convert.FromHexString(hex.Replace(" ", ""));
    static McDevice Oct(string n) => McDevice.Parse(n, McTarget.Serial1C, true, "x");

    const McSerialFormat F1 = McSerialFormat.Format1, F4 = McSerialFormat.Format4;

    [Fact]
    public void Sum_OfficialExamples()
    {
        // 공식 예: "00FFBR3M0000" → C0 (SH-080008), "00FFBR3ABCD" → BD (FX 데이터통신 D-52)
        Assert.Equal(0xC0, McSerialFrame.Sum(A("00FFBR3M0000")));
        Assert.Equal(0xBD, McSerialFrame.Sum(A("00FFBR3ABCD")));
    }

    [Fact]
    public void BR_OfficialExample_Request_Response_HostAck()
    {
        // FX 데이터통신 §7.1: 국번 05, 대기 A(100ms), X040부터 5점
        var req = McSerialFrame.Request(5, McCommand.BR, 10, Oct("X40"), 5, null, F1, true);
        Assert.Equal(H("05 30 35 46 46 42 52 41 58 30 30 34 30 30 35 34 37"), req);
        Assert.Equal(A("<ENQ>05FFBRAX00400547"), req);

        var r = McSerialFrame.Parse(H("02 30 35 46 46 30 31 31 30 31 03 45 37"), McCommand.BR, 5, 5, F1, true);
        Assert.Equal(McStatus.Ack, r.Status);
        Assert.Equal(new long[] { 0, 1, 1, 0, 1 }, r.Values);

        Assert.Equal(H("06 30 35 46 46"), McSerialFrame.HostAck(5, F1));
    }

    [Fact]
    public void WR_OfficialExamples()
    {
        // §7.2 예 1: X040부터 2워드 → 1234H, ABCDH
        Assert.Equal(A("<ENQ>05FFWR0X00400248"), McSerialFrame.Request(5, McCommand.WR, 0, Oct("X40"), 2, null, F1, true));
        var r1 = McSerialFrame.Parse(A("<STX>05FF1234ABCD<ETX>C8"), McCommand.WR, 5, 2, F1, true);
        Assert.Equal(new long[] { 0x1234, 0xABCD }, r1.Values);

        // §7.2 예 2: TN123부터 2점 → 7BC9H, 1234H (2문자 코드 + 3자리)
        Assert.Equal(A("<ENQ>05FFWR0TN1230264"), McSerialFrame.Request(5, McCommand.WR, 0, Oct("TN123"), 2, null, F1, true));
        var r2 = McSerialFrame.Parse(A("<STX>05FF7BC91234<ETX>B3"), McCommand.WR, 5, 2, F1, true);
        Assert.Equal(new long[] { 0x7BC9, 0x1234 }, r2.Values);
    }

    [Fact]
    public void BW_WW_OfficialExamples_AckFiveBytes()
    {
        // §7.4: M903부터 5점 = 0,1,1,0,1 / §7.5: D0=1234H, D1=ACD7H
        Assert.Equal(A("<ENQ>00FFBW0M0903050110126"), McSerialFrame.Request(0, McCommand.BW, 0, Oct("M903"), 5, [0, 1, 1, 0, 1], F1, true));
        Assert.Equal(A("<ENQ>00FFWW0D0000021234ACD7F9"), McSerialFrame.Request(0, McCommand.WW, 0, Oct("D0"), 2, [0x1234, 0xACD7], F1, true));
        Assert.Equal(McStatus.Ack, McSerialFrame.Parse(H("06 30 30 46 46"), McCommand.BW, 0, 5, F1, true).Status);
        Assert.Equal(McStatus.Ack, McSerialFrame.Parse(H("06 30 30 46 46"), McCommand.WW, 0, 2, F1, true).Status);
    }

    [Fact]
    public void Request_Variants_Derived()
    {
        // derived: 같은 BR 예제의 형식 4(CR LF), 섬체크 없음
        Assert.Equal(A("<ENQ>05FFBRAX00400547<CR><LF>"), McSerialFrame.Request(5, McCommand.BR, 10, Oct("X40"), 5, null, F4, true));
        Assert.Equal(A("<ENQ>05FFBRAX004005"), McSerialFrame.Request(5, McCommand.BR, 10, Oct("X40"), 5, null, F1, false));
        Assert.Equal(A("<ACK>05FF<CR><LF>"), McSerialFrame.HostAck(5, F4));
        // derived: 256점은 "00", WW 음수는 2의 보수 16비트, 국번 1F
        Assert.StartsWith("1FFFBR0M000000", Encoding.ASCII.GetString(McSerialFrame.Request(31, McCommand.BR, 0, Oct("M0"), 256, null, F1, false)[1..]));
        Assert.EndsWith("FFFF8000", Encoding.ASCII.GetString(McSerialFrame.Request(0, McCommand.WW, 0, Oct("D0"), 2, [-1, -32768], F1, false)));
        // derived: 16진 B/W, 8진 X의 필드
        Assert.Equal("B001F", McSerialFrame.DeviceField(Oct("B1F")));
        Assert.Equal("X0017", McSerialFrame.DeviceField(Oct("X17")));
        Assert.Equal("CN007", McSerialFrame.DeviceField(Oct("CN7")));
    }

    [Fact]
    public void Find_AckAndNak_FixedLength_H1()
    {
        // 옛 구현 H1: 5바이트 ACK·7바이트 NAK를 응답으로 인식하지 못함
        Assert.Equal((0, 5), McSerialFrame.Find(A("<ACK>00FF"), F1, true));
        Assert.Equal((0, -1), McSerialFrame.Find(A("<ACK>00F"), F1, true));
        Assert.Equal((0, 7), McSerialFrame.Find(A("<NAK>00FF06"), F1, true));
        Assert.Equal((0, 9), McSerialFrame.Find(A("<NAK>00FF06<CR><LF>"), F4, true));
        Assert.Equal((0, 7), McSerialFrame.Find(A("<ACK>00FF<CR><LF>"), F4, false));
    }

    [Fact]
    public void Find_Format4_WaitsForCrLf_H2()
    {
        // 옛 구현 H2: 형식 4에서 ETX 위치를 잘못 봐서 정상 응답을 타임아웃 처리
        Assert.Equal((0, -1), McSerialFrame.Find(A("<STX>05FF01101<ETX>E7"), F4, true));
        Assert.Equal((0, -1), McSerialFrame.Find(A("<STX>05FF01101<ETX>E7<CR>"), F4, true));
        Assert.Equal((0, 15), McSerialFrame.Find(A("<STX>05FF01101<ETX>E7<CR><LF>"), F4, true));
        Assert.Equal((0, 11), McSerialFrame.Find(A("<STX>05FF01101<ETX>"), F1, false));
        Assert.Equal((0, -1), McSerialFrame.Find(A("<STX>05FF01101<ETX>E"), F1, true));
    }

    [Fact]
    public void Find_SkipsNoiseBeforeFrame()
    {
        Assert.Equal((2, 5), McSerialFrame.Find([0x00, 0xFF, .. A("<ACK>00FF")], F1, true));
        Assert.Equal((-1, -1), McSerialFrame.Find(A("00FF"), F1, true));
    }

    [Fact]
    public void Parse_NakOnRead_H3()
    {
        // 옛 구현 H3: 읽기 명령에 NAK가 오면 ETX를 기다리다 타임아웃
        var r = McSerialFrame.Parse(A("<NAK>05FF06"), McCommand.BR, 5, 5, F1, true);
        Assert.Equal((McStatus.Nak, 0x06), (r.Status, r.ErrorCode));
        var r4 = McSerialFrame.Parse(A("<NAK>05FF02<CR><LF>"), McCommand.WR, 5, 1, F4, true);
        Assert.Equal((McStatus.Nak, 0x02), (r4.Status, r4.ErrorCode));
    }

    [Fact]
    public void Parse_Format4_And_NoSum()
    {
        var r = McSerialFrame.Parse(A("<STX>05FF01101<ETX>E7<CR><LF>"), McCommand.BR, 5, 5, F4, true);
        Assert.Equal(new long[] { 0, 1, 1, 0, 1 }, r.Values);
        var r2 = McSerialFrame.Parse(A("<STX>05FF1234abcd<ETX>"), McCommand.WR, 5, 2, F1, false);
        Assert.Equal(new long[] { 0x1234, 0xABCD }, r2.Values);
        Assert.Equal(McStatus.Ack, McSerialFrame.Parse(A("<ACK>00FF<CR><LF>"), McCommand.WW, 0, 1, F4, true).Status);
    }

    [Fact]
    public void Parse_SumError_And_Invalid()
    {
        Assert.Equal(McStatus.SumError, McSerialFrame.Parse(A("<STX>05FF01101<ETX>E8"), McCommand.BR, 5, 5, F1, true).Status);
        Assert.Equal(McStatus.Ack, McSerialFrame.Parse(A("<STX>05FF01101<ETX>e7"), McCommand.BR, 5, 5, F1, true).Status);   // 소문자 섬체크 허용
        Assert.Equal(McStatus.Invalid, McSerialFrame.Parse(A("<STX>06FF01101<ETX>E8"), McCommand.BR, 5, 5, F1, true).Status); // 국번 다름 (섬 맞음)
        Assert.Equal(McStatus.Invalid, McSerialFrame.Parse(A("<STX>05FF0110<ETX>B6"), McCommand.BR, 5, 5, F1, true).Status);  // 점수 부족
        Assert.Equal(McStatus.Invalid, McSerialFrame.Parse(A("<STX>05FF01201<ETX>E8"), McCommand.BR, 5, 5, F1, true).Status);  // 비트에 '2'
        Assert.Equal(McStatus.Invalid, McSerialFrame.Parse(A("<ACK>05FF"), McCommand.BR, 5, 5, F1, true).Status);              // 읽기에 ACK
        Assert.Equal(McStatus.Invalid, McSerialFrame.Parse(A("<STX>05FF01101<ETX>E7"), McCommand.BR, 5, 5, F4, true).Status);  // 형식 4인데 CR LF 없음
        Assert.Equal(McStatus.Invalid, McSerialFrame.Parse(A("<ACK>00FE"), McCommand.BW, 0, 1, F1, true).Status);              // PC 번호 다름
    }
}
