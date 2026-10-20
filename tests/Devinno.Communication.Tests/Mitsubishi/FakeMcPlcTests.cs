using System.Text;
using Devinno.Communication.Mitsubishi.Data;
using Devinno.Communication.Mitsubishi.Serial;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Mitsubishi;

public class FakeMcPlcTests
{
    static byte[] A(string s) => Encoding.ASCII.GetBytes(s
        .Replace("<ENQ>", ((char)0x05).ToString()).Replace("<STX>", ((char)0x02).ToString())
        .Replace("<ETX>", ((char)0x03).ToString()).Replace("<ACK>", ((char)0x06).ToString())
        .Replace("<NAK>", ((char)0x15).ToString()).Replace("<CR>", "\r").Replace("<LF>", "\n"));
    static string S(byte[]? b) => b == null ? "(null)" : Encoding.ASCII.GetString(b)
        .Replace(((char)0x02).ToString(), "<STX>").Replace(((char)0x03).ToString(), "<ETX>")
        .Replace(((char)0x06).ToString(), "<ACK>").Replace(((char)0x15).ToString(), "<NAK>")
        .Replace("\r", "<CR>").Replace("\n", "<LF>");
    const McSerialFormat F1 = McSerialFormat.Format1, F4 = McSerialFormat.Format4;

    /// 8진수 번호의 X 이름 (FX)
    static string X(int n) => "X" + Convert.ToString(n, 8);

    [Fact]
    public void Memory_WordViewOfBitDevice_Bit0IsHead()
    {
        using var plc = new FakeMcPlc();
        plc.Set("M16", 1); plc.Set("M31", 1);
        plc.Set("D5", -1);
        Assert.Equal(0xFFFF, plc.Get("D5"));
        var r = McSerialFrame.Parse(plc.Handle1C(A("<ENQ>00FFWR0M001601"), F1, false)!, McCommand.WR, 0, 1, F1, false);
        Assert.Equal(new long[] { 0x8001 }, r.Values);
    }

    [Fact]
    public void Official_BR_ResponseAndHostAck()
    {
        using var plc = new FakeMcPlc { Station = 5 };
        plc.Set("X41", 1); plc.Set("X42", 1); plc.Set("X44", 1);
        // FX 데이터통신 §7.1 요청 그대로 → 공식 응답 바이트
        Assert.Equal("<STX>05FF01101<ETX>E7", S(plc.Handle1C(A("<ENQ>05FFBRAX00400547"), F1, true)));
        Assert.Null(plc.Handle1C(A("<ACK>05FF"), F1, true));
        Assert.Equal((1, 1), (plc.Requests, plc.HostAcks));
    }

    [Fact]
    public void Official_WR_Responses()
    {
        using var plc = new FakeMcPlc { Station = 5 };
        for (int b = 0; b < 16; b++)
        {
            plc.Set(X(32 + b), (0x1234 >> b) & 1);   // X40~X57 = 1234H
            plc.Set(X(48 + b), (0xABCD >> b) & 1);   // X60~X77 = ABCDH
        }
        Assert.Equal("<STX>05FF1234ABCD<ETX>C8", S(plc.Handle1C(A("<ENQ>05FFWR0X00400248"), F1, true)));

        plc.Set("TN123", 0x7BC9); plc.Set("TN124", 0x1234);
        Assert.Equal("<STX>05FF7BC91234<ETX>B3", S(plc.Handle1C(A("<ENQ>05FFWR0TN1230264"), F1, true)));
    }

    [Fact]
    public void Official_BW_WW_WriteMemory()
    {
        using var plc = new FakeMcPlc { Station = 0 };
        Assert.Equal("<ACK>00FF", S(plc.Handle1C(A("<ENQ>00FFBW0M0903050110126"), F1, true)));
        Assert.Equal(new long[] { 0, 1, 1, 0, 1 }, new[] { plc.Get("M903"), plc.Get("M904"), plc.Get("M905"), plc.Get("M906"), plc.Get("M907") });
        Assert.Equal("<ACK>00FF", S(plc.Handle1C(A("<ENQ>00FFWW0D0000021234ACD7F9"), F1, true)));
        Assert.Equal((0x1234L, 0xACD7L), (plc.Get("D0"), plc.Get("D1")));
    }

    [Fact]
    public void Format4_AddsCrLf_AndIgnoresWithoutIt()
    {
        using var plc = new FakeMcPlc { Station = 5 };
        plc.Set("X41", 1); plc.Set("X42", 1); plc.Set("X44", 1);
        Assert.Equal("<STX>05FF01101<ETX>E7<CR><LF>", S(plc.Handle1C(A("<ENQ>05FFBRAX00400547<CR><LF>"), F4, true)));
        Assert.Null(plc.Handle1C(A("<ENQ>05FFBRAX00400547"), F4, true));
    }

    [Fact]
    public void Errors_AreStrict()
    {
        using var plc = new FakeMcPlc { Station = 0 };
        Assert.Null(plc.Handle1C(A("<ENQ>01FFWR0D000001"), F1, false));                 // 다른 국번 → 무응답
        Assert.Equal("<NAK>00FF02", S(plc.Handle1C(A("<ENQ>00FFWR0D00000100"), F1, true)));  // 섬체크 틀림
        Assert.Equal("<NAK>00FF06", S(plc.Handle1C(A("<ENQ>00FFWR0Q000001"), F1, false)));   // 모르는 디바이스 코드
        Assert.Equal("<NAK>00FF06", S(plc.Handle1C(A("<ENQ>00FFBR0D000001"), F1, false)));   // 비트 명령에 워드 디바이스
        Assert.Equal("<NAK>00FF06", S(plc.Handle1C(A("<ENQ>00FFWR0X000801"), F1, false)));   // 8진수에 8
        Assert.Equal("<NAK>00FF06", S(plc.Handle1C(A("<ENQ>00FFBW0M0000021"), F1, false)));  // 데이터 길이 부족
        Assert.Equal("<NAK>00FF03", S(plc.Handle1C(A("<ENQ>00FFQR0D000001"), F1, false)));   // 지원 안 하는 명령
        Assert.Equal("<NAK>00FF10", S(plc.Handle1C(A("<ENQ>00FEWR0D000001"), F1, false)));   // PC 번호
    }

    [Fact]
    public void ForceError_Silent_BadSum()
    {
        using var plc = new FakeMcPlc { Station = 0, ForceError = 0x06 };
        Assert.Equal("<NAK>00FF06", S(plc.Handle1C(A("<ENQ>00FFWR0D000001"), F1, false)));
        plc.ForceError = null;
        plc.BadSum = true;
        var res = plc.Handle1C(A("<ENQ>00FFWR0D0000012A"), F1, true);
        Assert.Equal(McStatus.SumError, McSerialFrame.Parse(res!, McCommand.WR, 0, 1, F1, true).Status);
        plc.Silent = true;
        Assert.Null(plc.Handle1C(A("<ENQ>00FFWR0D000001"), F1, false));
        Assert.Equal(3, plc.Requests);
    }

    // ---- Q1: 엄격한 1C 요청 처리 ----

    [Theory]
    [InlineData("<ENQ>00FFWR0D00000a")]                 // 소문자 16진 점수
    [InlineData("<ENQ>00FFWR0D0000x1")]                 // 점수에 0x
    [InlineData("<ENQ>00FFWR0D000 01")]                 // 공백 (디바이스 번호)
    [InlineData("<ENQ>00ffWR0D000001")]                 // 소문자 PC 번호
    [InlineData("<ENQ>00FFWR0DA00001")]                 // 10진 디바이스 번호에 16진 문자
    [InlineData("<ENQ>00FFWW0D0000021234acd7")]         // 소문자 워드 데이터
    [InlineData("<ENQ>00FFWW0D00000212340xD7")]         // 0x 워드 데이터
    [InlineData("<ENQ>00FFWR0g000001")]                 // 소문자 디바이스 코드
    [InlineData("<ENQ>00FFWRaD000001")]                 // 소문자 메시지 대기
    [InlineData("<ENQ>00FFWRGD000001")]                 // 16진이 아닌 메시지 대기
    public void Strict_BadFields_AreNak(string request)
    {
        using var plc = new FakeMcPlc { Station = 0 };
        var res = S(plc.Handle1C(A(request), F1, false));
        Assert.StartsWith("<NAK>00FF", res);
    }

    [Fact]
    public void Strict_LowercaseSum_And_MalformedStation()
    {
        using var plc = new FakeMcPlc { Station = 0 };
        // 올바른 섬체크 F9 를 소문자로 → 섬체크 오류
        Assert.Equal("<NAK>00FF02", S(plc.Handle1C(A("<ENQ>00FFWW0D0000021234ACD7f9"), F1, true)));
        Assert.Equal("<ACK>00FF", S(plc.Handle1C(A("<ENQ>00FFWW0D0000021234ACD7F9"), F1, true)));
        Assert.Null(plc.Handle1C(A("<ENQ>0xFFWR0D000001"), F1, false));   // 국번 형식 오류 → 무응답
    }

    [Fact]
    public void Strict_ZeroCount_Means256OnlyForBR()
    {
        using var plc = new FakeMcPlc { Station = 0 };
        Assert.Equal("<NAK>00FF06", S(plc.Handle1C(A("<ENQ>00FFWR0D000000"), F1, false)));
        Assert.Equal("<NAK>00FF06", S(plc.Handle1C(A("<ENQ>00FFWW0D000000"), F1, false)));
        Assert.Equal("<NAK>00FF06", S(plc.Handle1C(A("<ENQ>00FFBW0M000000"), F1, false)));
        var br = S(plc.Handle1C(A("<ENQ>00FFBR0M000000"), F1, false));
        Assert.StartsWith("<STX>00FF", br);
        Assert.Equal(256, br.Length - "<STX>00FF".Length - "<ETX>".Length);
    }

    [Fact]
    public void Strict_PointLimits()
    {
        using var plc = new FakeMcPlc { Station = 0 };
        string Req(string cmd, string dev, int n, string data = "") => "<ENQ>00FF" + cmd + "0" + dev + n.ToString("X2") + data;
        string Words(int n) => string.Concat(Enumerable.Repeat("0001", n));
        const string Nak = "<NAK>00FF06", Ack = "<ACK>00FF";
        Assert.StartsWith("<STX>", S(plc.Handle1C(A(Req("BR", "M0000", 255)), F1, false)));
        // WR 워드 디바이스 64 / 65, 비트 디바이스 32 / 33
        Assert.StartsWith("<STX>", S(plc.Handle1C(A(Req("WR", "D0000", 64)), F1, false)));
        Assert.Equal(Nak, S(plc.Handle1C(A(Req("WR", "D0000", 65)), F1, false)));
        Assert.StartsWith("<STX>", S(plc.Handle1C(A(Req("WR", "M0000", 32)), F1, false)));
        Assert.Equal(Nak, S(plc.Handle1C(A(Req("WR", "M0000", 33)), F1, false)));
        // BW 160 / 161
        Assert.Equal(Ack, S(plc.Handle1C(A(Req("BW", "M0000", 160, new string('1', 160))), F1, false)));
        Assert.Equal(Nak, S(plc.Handle1C(A(Req("BW", "M0000", 161, new string('1', 161))), F1, false)));
        // WW 워드 디바이스 64 / 65, 비트 디바이스 10 / 11
        Assert.Equal(Ack, S(plc.Handle1C(A(Req("WW", "D0000", 64, Words(64))), F1, false)));
        Assert.Equal(Nak, S(plc.Handle1C(A(Req("WW", "D0000", 65, Words(65))), F1, false)));
        Assert.Equal(Ack, S(plc.Handle1C(A(Req("WW", "M0000", 10, Words(10))), F1, false)));
        Assert.Equal(Nak, S(plc.Handle1C(A(Req("WW", "M0000", 11, Words(11))), F1, false)));
    }

    [Fact]
    public void Strict_BitWriteChars_MustBe0Or1()
    {
        using var plc = new FakeMcPlc { Station = 0 };
        Assert.Equal("<NAK>00FF06", S(plc.Handle1C(A("<ENQ>00FFBW0M00000232"), F1, false)));   // '3','2'
        Assert.Equal("<NAK>00FF06", S(plc.Handle1C(A("<ENQ>00FFBW0M000001A"), F1, false)));   // 'A'
        Assert.Equal(0, plc.Get("M0"));
    }
}
