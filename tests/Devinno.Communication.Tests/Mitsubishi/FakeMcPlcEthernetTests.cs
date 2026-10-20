using System.Net.Sockets;
using Devinno.Communication.Mitsubishi.Data;
using Devinno.Communication.Mitsubishi.Ethernet;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Mitsubishi;

public class FakeMcPlcEthernetTests
{
    static byte[] H(string hex) => Convert.FromHexString(hex.Replace(" ", ""));
    static McDevice D3(string n) => McDevice.Parse(n, McTarget.Ethernet3E, false, "x");
    static McDevice D1(string n) => McDevice.Parse(n, McTarget.Ethernet1E, true, "x");

    [Fact]
    public void E3_ManualExamples_ReadWriteWords()
    {
        using var plc = new FakeMcPlc();
        plc.Set("D100", 0x1234); plc.Set("D101", 0x0002); plc.Set("D102", 0x1DEF);
        Assert.Equal(H("D0 00 00 FF FF 03 00 08 00 00 00 34 12 02 00 EF 1D"),
            plc.Handle3E(H("50 00 00 FF FF 03 00 0C 00 10 00 01 04 00 00 64 00 00 A8 03 00")));
        Assert.Equal(H("D0 00 00 FF FF 03 00 02 00 00 00"),
            plc.Handle3E(H("50 00 00 FF FF 03 00 12 00 10 00 01 14 00 00 64 00 00 A8 03 00 95 19 02 12 30 11")));
        Assert.Equal((0x1995L, 0x1202L, 0x1130L), (plc.Get("D100"), plc.Get("D101"), plc.Get("D102")));
    }

    [Fact]
    public void E3_ManualExample_ReadBits()
    {
        using var plc = new FakeMcPlc();
        plc.Set("M103", 1); plc.Set("M106", 1); plc.Set("M107", 1);
        Assert.Equal(H("D0 00 00 FF FF 03 00 06 00 00 00 00 01 00 11"),
            plc.Handle3E(H("50 00 00 FF FF 03 00 0C 00 10 00 01 04 01 00 64 00 00 90 08 00")));
    }

    [Fact]
    public void E3_Strict_UnknownCode_WrongSubcommand_BitOnWord()
    {
        using var plc = new FakeMcPlc();
        // 모르는 코드 00H
        Assert.Equal(H("D0 00 00 FF FF 03 00 0B 00 5C C0 00 FF FF 03 00 01 04 00 00"),
            plc.Handle3E(H("50 00 00 FF FF 03 00 0C 00 10 00 01 04 00 00 64 00 00 00 03 00")));
        // iQ-R 서브커맨드 0002 → C059
        Assert.Equal(0xC059, EndCode(plc.Handle3E(H("50 00 00 FF FF 03 00 0C 00 10 00 01 04 02 00 64 00 00 A8 03 00"))));
        // 비트 단위로 D 읽기 → C05C
        Assert.Equal(0xC05C, EndCode(plc.Handle3E(H("50 00 00 FF FF 03 00 0C 00 10 00 01 04 01 00 64 00 00 A8 03 00"))));
        // 길이 필드 불일치 → C05C
        Assert.Equal(0xC05C, EndCode(plc.Handle3E(H("50 00 00 FF FF 03 00 0D 00 10 00 01 04 00 00 64 00 00 A8 03 00"))));
        plc.ForceError = 0xC051;
        Assert.Equal(0xC051, EndCode(plc.Handle3E(Mc3EFrame.Request(McCommand.WR, D3("D0"), 1, null, 4))));
    }

    static int EndCode(byte[] res) => res[9] | res[10] << 8;

    [Fact]
    public void E3_MasterFrames_RoundTrip()
    {
        using var plc = new FakeMcPlc { XYOctal = false };
        var w = Mc3EFrame.Request(McCommand.BW, D3("X1F"), 3, [1, 0, 1], 4);
        Assert.Equal(McStatus.Ack, Mc3EFrame.Parse(plc.Handle3E(w)[9..], McCommand.BW, 3).Status);
        Assert.Equal((1L, 0L, 1L), (plc.Get("X1F"), plc.Get("X20"), plc.Get("X21")));
        var r = Mc3EFrame.Request(McCommand.WR, D3("X10"), 2, null, 4);
        Assert.Equal(new long[] { 0x8000, 0x0002 }, Mc3EFrame.Parse(plc.Handle3E(r)[9..], McCommand.WR, 2).Values);
    }

    [Fact]
    public void E1_ManualExamples()
    {
        using var plc = new FakeMcPlc();
        for (int i = 0; i < 12; i += 2) plc.Set($"M{100 + i}", 1);   // M100, M102 … = 1 (G1)
        Assert.Equal(H("80 00 10 10 10 10 10 10"), plc.Handle1E(H("00 FF 0A 00 64 00 00 00 20 4D 0C 00")));

        // G3: Y000~Y007 = 10011101 (Y007…Y000), Y010~Y017 = 00100010
        foreach (var y in new[] { "Y0", "Y2", "Y3", "Y4", "Y7", "Y11", "Y15" }) plc.Set(y, 1);
        Assert.Equal(H("81 00 9D 22"), plc.Handle1E(H("01 FF 0A 00 00 00 00 00 20 59 01 00")));

        Assert.Equal(H("82 00"), plc.Handle1E(H("02 FF 0A 00 32 00 00 00 20 4D 0C 00 01 11 01 00 00 01")));
        Assert.Equal(new long[] { 0, 1, 1, 1, 0, 1, 0, 0, 0, 0, 0, 1 }, Enumerable.Range(50, 12).Select(i => plc.Get($"M{i}")));
        Assert.Equal(H("83 00"), plc.Handle1E(H("03 FF 0A 00 64 00 00 00 20 44 03 00 34 12 76 98 09 01")));
        Assert.Equal((0x1234L, 0x9876L, 0x0109L), (plc.Get("D100"), plc.Get("D101"), plc.Get("D102")));
    }

    [Fact]
    public void E1_Errors_AreStrict()
    {
        using var plc = new FakeMcPlc();
        Assert.Equal(H("81 56"), plc.Handle1E(H("01 FF 0A 00 00 00 00 00 20 44 01 00").Select((b, i) => i == 9 ? (byte)0x99 : b).ToArray()));   // 모르는 코드
        Assert.Equal(H("80 58"), plc.Handle1E(H("00 FF 0A 00 00 00 00 00 20 44 01 00")));   // 비트 읽기에 D
        Assert.Equal(H("83 57"), plc.Handle1E(H("03 FF 0A 00 64 00 00 00 20 44 02 00 34 12")));   // 데이터 길이 부족
        Assert.Equal(H("85 50"), plc.Handle1E(H("05 FF 0A 00 00 00 00 00 20 44 01 00")));   // 지원 안 하는 서브헤더
        Assert.Equal(H("81 5B 10 00"), plc.Handle1E(H("01 01 0A 00 00 00 00 00 20 44 01 00")));   // PC 번호
        plc.AbnormalTrailingZero = false;
        plc.ForceError = 0x5B12;
        Assert.Equal(H("81 5B 12"), plc.Handle1E(Mc1EFrame.Request(McCommand.WR, D1("D0"), 1, null, 4)));
        plc.ForceError = 0x56;
        Assert.Equal(H("81 56"), plc.Handle1E(Mc1EFrame.Request(McCommand.WR, D1("D0"), 1, null, 4)));
    }

    // ---- 엄격성: 마스터의 잘못된 인코딩이 루프백에서 통과하지 못하게 ----

    // 3E 원시 요청 (명령 cmd, 서브커맨드 sub, 코드 byte, 번호 head, 점수 count, 데이터)
    static byte[] Raw3E(int cmd, int sub, byte code, int head, int count, byte[] data)
    {
        int len = 12 + data.Length;
        return [0x50, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00, (byte)len, (byte)(len >> 8), 0x10, 0x00, (byte)cmd, (byte)(cmd >> 8),
            (byte)sub, (byte)(sub >> 8), (byte)head, (byte)(head >> 8), (byte)(head >> 16), code, (byte)count, (byte)(count >> 8), .. data];
    }

    const byte M3 = 0x90, D3Code = 0xA8;
    static byte[] Raw3EBitWrite(int count, byte[] data) => Raw3E(0x1401, 1, M3, 0, count, data);
    static byte[] Raw3EWordWrite(int count, byte[] data) => Raw3E(0x1401, 0, D3Code, 0, count, data);

    [Fact]
    public void E3_BitWrite_RejectsBadNibbles()
    {
        using var plc = new FakeMcPlc();
        Assert.Equal(0xC05C, EndCode(plc.Handle3E(Raw3EBitWrite(2, [0x21]))));   // 앞 니블 2
        Assert.Equal(0xC05C, EndCode(plc.Handle3E(Raw3EBitWrite(2, [0x12]))));   // 뒤 니블 2
        Assert.Equal(0xC05C, EndCode(plc.Handle3E(Raw3EBitWrite(3, [0x11, 0x11]))));   // 홀수 점수의 패드 니블 != 0
        Assert.Equal((0L, 0L, 0L), (plc.Get("M0"), plc.Get("M1"), plc.Get("M2")));   // 거부된 요청은 메모리를 바꾸지 않는다
        Assert.Equal(0, EndCode(plc.Handle3E(Raw3EBitWrite(3, [0x11, 0x10]))));
        Assert.Equal((1L, 1L, 1L), (plc.Get("M0"), plc.Get("M1"), plc.Get("M2")));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(3584, true)]
    [InlineData(3585, false)]
    public void E3_BitPointLimits(int count, bool ok)
    {
        using var plc = new FakeMcPlc();
        Assert.Equal(ok ? 0 : 0xC05C, EndCode(plc.Handle3E(Raw3E(0x0401, 1, M3, 0, count, []))));
        Assert.Equal(ok ? 0 : 0xC05C, EndCode(plc.Handle3E(Raw3EBitWrite(count, new byte[(count + 1) / 2]))));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(960, true)]
    [InlineData(961, false)]
    public void E3_WordPointLimits(int count, bool ok)
    {
        using var plc = new FakeMcPlc();
        Assert.Equal(ok ? 0 : 0xC05C, EndCode(plc.Handle3E(Raw3E(0x0401, 0, D3Code, 0, count, []))));
        Assert.Equal(ok ? 0 : 0xC05C, EndCode(plc.Handle3E(Raw3EWordWrite(count, new byte[count * 2]))));
    }

    [Fact]
    public void E3_DataLengthMustMatchCount()
    {
        using var plc = new FakeMcPlc();
        Assert.Equal(0xC05C, EndCode(plc.Handle3E(Raw3EWordWrite(2, new byte[2]))));   // 부족
        Assert.Equal(0xC05C, EndCode(plc.Handle3E(Raw3EWordWrite(1, new byte[4]))));   // 초과
        Assert.Equal(0xC05C, EndCode(plc.Handle3E(Raw3EBitWrite(3, new byte[1]))));
        Assert.Equal(0xC05C, EndCode(plc.Handle3E(Raw3EBitWrite(3, new byte[3]))));
        Assert.Equal(0xC05C, EndCode(plc.Handle3E(Raw3E(0x0401, 0, D3Code, 0, 1, new byte[2]))));   // 읽기에 데이터
    }

    // 1E 원시 요청: 점수 바이트는 그대로(0 = 256), 데이터는 호출자가 준다
    static byte[] Raw1E(int sub, int head, int code, int countByte, byte[] data) =>
        [(byte)sub, 0xFF, 0x0A, 0x00, (byte)head, (byte)(head >> 8), (byte)(head >> 16), (byte)(head >> 24),
            (byte)code, (byte)(code >> 8), (byte)countByte, 0x00, .. data];

    const int M1 = 0x4D20, D1Code = 0x4420;
    static byte[] Raw1EBitWrite(int countByte, byte[] data) => Raw1E(2, 0, M1, countByte, data);
    static byte[] Raw1EWordWrite(int code, int countByte, byte[] data) => Raw1E(3, 0, code, countByte, data);

    [Fact]
    public void E1_BitWrite_RejectsBadNibbles()
    {
        using var plc = new FakeMcPlc();
        Assert.Equal(H("82 57"), plc.Handle1E(Raw1EBitWrite(2, [0x21])));
        Assert.Equal(H("82 57"), plc.Handle1E(Raw1EBitWrite(2, [0x12])));
        Assert.Equal(H("82 57"), plc.Handle1E(Raw1EBitWrite(3, [0x11, 0x11])));   // 패드 니블 != 0
        Assert.Equal((0L, 0L, 0L), (plc.Get("M0"), plc.Get("M1"), plc.Get("M2")));
        Assert.Equal(H("82 00"), plc.Handle1E(Raw1EBitWrite(3, [0x11, 0x10])));
        Assert.Equal((1L, 1L, 1L), (plc.Get("M0"), plc.Get("M1"), plc.Get("M2")));
    }

    [Theory]
    [InlineData(0, 256)]     // 점수 바이트 0 = 256
    [InlineData(1, 1)]
    [InlineData(255, 255)]
    public void E1_BitReadAccepts1To256(int countByte, int count)
    {
        using var plc = new FakeMcPlc();
        var res = plc.Handle1E(Raw1E(0, 0, M1, countByte, []));
        Assert.Equal(0, res[1]);
        Assert.Equal(2 + (count + 1) / 2, res.Length);
    }

    [Theory]
    [InlineData(M1, 33, false)]     // WR 비트 디바이스 1~32
    [InlineData(M1, 32, true)]
    [InlineData(D1Code, 65, false)]   // WR 워드 디바이스 1~64
    [InlineData(D1Code, 64, true)]
    [InlineData(D1Code, 0, false)]    // 0 = 256 은 BR만
    public void E1_WordReadLimits(int code, int countByte, bool ok)
    {
        using var plc = new FakeMcPlc();
        var res = plc.Handle1E(Raw1E(1, 0, code, countByte, []));
        if (ok) Assert.Equal(0, res[1]);
        else Assert.Equal(H("81 57"), res);
    }

    [Theory]
    [InlineData(160, true)]
    [InlineData(161, false)]
    [InlineData(0, false)]            // 0 = 256 은 BW 한계 초과
    public void E1_BitWriteLimits(int countByte, bool ok)
    {
        using var plc = new FakeMcPlc();
        int n = countByte == 0 ? 256 : countByte;
        Assert.Equal(ok ? H("82 00") : H("82 57"), plc.Handle1E(Raw1EBitWrite(countByte, new byte[(n + 1) / 2])));
    }

    [Theory]
    [InlineData(M1, 10, true)]        // WW 비트 디바이스 1~10
    [InlineData(M1, 11, false)]
    [InlineData(D1Code, 64, true)]    // WW 워드 디바이스 1~64
    [InlineData(D1Code, 65, false)]
    [InlineData(D1Code, 0, false)]
    public void E1_WordWriteLimits(int code, int countByte, bool ok)
    {
        using var plc = new FakeMcPlc();
        int n = countByte == 0 ? 256 : countByte;
        Assert.Equal(ok ? H("83 00") : H("83 57"), plc.Handle1E(Raw1EWordWrite(code, countByte, new byte[n * 2])));
    }

    [Fact]
    public void E1_DataLengthMustMatchCount()
    {
        using var plc = new FakeMcPlc();
        Assert.Equal(H("83 57"), plc.Handle1E(Raw1EWordWrite(D1Code, 2, new byte[2])));
        Assert.Equal(H("83 57"), plc.Handle1E(Raw1EWordWrite(D1Code, 1, new byte[4])));
        Assert.Equal(H("82 57"), plc.Handle1E(Raw1EBitWrite(3, new byte[1])));
        Assert.Equal(H("82 57"), plc.Handle1E(Raw1EBitWrite(3, new byte[3])));
        Assert.Equal(H("81 57"), plc.Handle1E(Raw1E(1, 0, D1Code, 1, new byte[2])));   // 읽기에 데이터
    }

    [Fact]
    public void E1_NegativeHeadDevice_IsRejected()
    {
        using var plc = new FakeMcPlc();
        Assert.Equal(H("81 56"), plc.Handle1E(Raw1E(1, -1, D1Code, 1, [])));
        Assert.Equal(H("81 56"), plc.Handle1E(Raw1E(1, int.MinValue, D1Code, 1, [])));
        Assert.Equal(H("81 00 00 00"), plc.Handle1E(Raw1E(1, 0, D1Code, 1, [])));
    }

    [Theory]
    [InlineData(McFrame.E3)]
    [InlineData(McFrame.E1)]
    public async Task TcpServer_AnswersOverSocket(McFrame frame)
    {
        using var plc = new FakeMcPlc();
        plc.Set("D7", 4321);
        plc.Start(frame);
        using var c = new TcpClient();
        await c.ConnectAsync("127.0.0.1", plc.Port);
        var s = c.GetStream();
        var req = frame == McFrame.E3
            ? Mc3EFrame.Request(McCommand.WR, D3("D7"), 1, null, 4)
            : Mc1EFrame.Request(McCommand.WR, D1("D7"), 1, null, 4);
        await s.WriteAsync(req);
        var res = new byte[frame == McFrame.E3 ? 13 : 4];
        await s.ReadExactlyAsync(res).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new byte[] { 0xE1, 0x10 }, res[^2..]);
    }
}
