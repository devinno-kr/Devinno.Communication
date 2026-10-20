using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Tests.Secs;

/// SECS-II 아이템 인코딩. C_ = 캡처(Wireshark 이슈 12942 sample_hsms.pcapng)의 본문 바이트, D_ = 규칙으로 계산한 파생 벡터.
public class SecsCodecTests
{
    static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    static void Golden(string hex, SecsItem item)
    {
        Assert.Equal(Hex(hex), SecsCodec.Encode(item));
        Assert.Equal(item, SecsCodec.Decode(Hex(hex)));
    }

    // 캡처 S6F11 본문 공통 앞부분: <L [3] <U1 1> <U2 1337> <L [1] <L [2] <U2 1000> <L [2] X Y>>>>
    const string S6F11Prefix = "01 03 a5 01 01 a9 02 05 39 01 01 01 02 a9 02 03 e8 01 02";

    static SecsItem S6F11(SecsItem x, SecsItem y)
        => SecsItem.L(SecsItem.U1(1), SecsItem.U2(1337), SecsItem.L(SecsItem.L(SecsItem.U2(1000), SecsItem.L(x, y))));

    [Fact]
    public void C_HostS1F13_EmptyList() => Golden("01 00", SecsItem.L());

    [Fact]
    public void C_EquipmentS1F13_MdlnSoftrev()
        => Golden("01 02 41 07 67 65 6d 65 71 75 70 41 05 31 2e 30 2e 30", SecsItem.L(SecsItem.A("gemequp"), SecsItem.A("1.0.0")));

    [Fact]
    public void C_HostS1F14_CommackAndEmptyList()
        => Golden("01 02 21 01 00 01 00", SecsItem.L(SecsItem.B(0), SecsItem.L()));

    [Fact]
    public void C_S6F11_Binary() => Golden(S6F11Prefix + "21 01 01 21 02 01 02", S6F11(SecsItem.B(1), SecsItem.B(1, 2)));

    [Fact]
    public void C_S6F11_Boolean()
        => Golden(S6F11Prefix + "25 01 01 25 04 01 00 01 01", S6F11(SecsItem.Boolean(true), SecsItem.Boolean(true, false, true, true)));

    [Fact]
    public void C_S6F11_Ascii()
        => Golden(S6F11Prefix + "41 04 54 65 73 74 41 07 54 65 73 74 31 32 33", S6F11(SecsItem.A("Test"), SecsItem.A("Test123")));

    [Fact]
    public void C_S6F11_I8()
        => Golden(S6F11Prefix + "61 08 00 00 00 00 00 00 00 01 61 28 ff ff ff ff ff ff ff ff 00 00 00 00 00 00 00 01 00 00 00 00 00 00 00 0a 00 00 00 00 00 00 00 64 00 00 00 00 00 00 03 e8",
            S6F11(SecsItem.I8(1), SecsItem.I8(-1, 1, 10, 100, 1000)));

    [Fact]
    public void C_S6F11_I1() => Golden(S6F11Prefix + "65 01 01 65 04 ff 01 0a 64", S6F11(SecsItem.I1(1), SecsItem.I1(-1, 1, 10, 100)));

    [Fact]
    public void C_S6F11_I2()
        => Golden(S6F11Prefix + "69 02 00 01 69 0a ff ff 00 01 00 0a 00 64 03 e8", S6F11(SecsItem.I2(1), SecsItem.I2(-1, 1, 10, 100, 1000)));

    [Fact]
    public void C_S6F11_I4()
        => Golden(S6F11Prefix + "71 04 00 00 00 01 71 14 ff ff ff ff 00 00 00 01 00 00 00 0a 00 00 00 64 00 00 03 e8",
            S6F11(SecsItem.I4(1), SecsItem.I4(-1, 1, 10, 100, 1000)));

    [Fact]
    public void C_S6F11_F8()
        => Golden(S6F11Prefix + "81 08 3f f1 f7 ce d9 16 87 2b 81 28 bf f1 f7 ce d9 16 87 2b 3f f1 f7 ce d9 16 87 2b 40 24 77 ce d9 16 87 2b 40 59 16 14 7a e1 47 ae 40 8f 43 a5 e3 53 f7 cf",
            S6F11(SecsItem.F8(1.123), SecsItem.F8(-1.123, 1.123, 10.234, 100.345, 1000.456)));

    [Fact]
    public void C_S6F11_F4()
        => Golden(S6F11Prefix + "91 04 3f 8f be 77 91 14 bf 8f be 77 3f 8f be 77 41 23 be 77 42 c8 b0 a4 44 7a 1d 2f",
            S6F11(SecsItem.F4(1.123f), SecsItem.F4(-1.123f, 1.123f, 10.234f, 100.345f, 1000.456f)));

    [Fact]
    public void C_S6F11_U8()
        => Golden(S6F11Prefix + "a1 08 00 00 00 00 00 00 00 01 a1 20 00 00 00 00 00 00 00 01 00 00 00 00 00 00 00 0a 00 00 00 00 00 00 00 64 00 00 00 00 00 00 03 e8",
            S6F11(SecsItem.U8(1), SecsItem.U8(1, 10, 100, 1000)));

    [Fact]
    public void C_S6F11_U1() => Golden(S6F11Prefix + "a5 01 01 a5 03 01 0a 64", S6F11(SecsItem.U1(1), SecsItem.U1(1, 10, 100)));

    [Fact]
    public void C_S6F11_U2()
        => Golden(S6F11Prefix + "a9 02 00 01 a9 08 00 01 00 0a 00 64 03 e8", S6F11(SecsItem.U2(1), SecsItem.U2(1, 10, 100, 1000)));

    [Fact]
    public void C_S6F11_U4()
        => Golden(S6F11Prefix + "b1 04 00 00 00 01 b1 10 00 00 00 01 00 00 00 0a 00 00 00 64 00 00 03 e8",
            S6F11(SecsItem.U4(1), SecsItem.U4(1, 10, 100, 1000)));

    [Fact]
    public void D_JisAndC2_FormatBytes()
    {
        Golden("45 02 b1 b2", SecsItem.J([0xB1, 0xB2]));
        Golden("49 04 00 0a b0 a1", SecsItem.C2([0x00, 0x0A, 0xB0, 0xA1]));
    }

    [Fact]
    public void D_ZeroLengthItems()
    {
        Golden("41 00", SecsItem.A(""));
        Golden("b1 00", SecsItem.U4());
        Golden("21 00", SecsItem.B());
    }

    [Fact]
    public void D_LengthBytes_ShortestForm_Boundaries()
    {
        Assert.Equal(Hex("21 ff"), SecsCodec.Encode(SecsItem.B(new byte[255]))[..2]);
        Assert.Equal(Hex("22 01 00"), SecsCodec.Encode(SecsItem.B(new byte[256]))[..3]);
        Assert.Equal(Hex("22 ff ff"), SecsCodec.Encode(SecsItem.B(new byte[65535]))[..3]);
        Assert.Equal(Hex("23 01 00 00"), SecsCodec.Encode(SecsItem.B(new byte[65536]))[..4]);
        var bigList = SecsItem.L(Enumerable.Repeat(SecsItem.L(), 256).ToArray());
        Assert.Equal(Hex("02 01 00 01 00"), SecsCodec.Encode(bigList)[..5]);
        Assert.Equal(bigList, SecsCodec.Decode(SecsCodec.Encode(bigList)));
        var big = SecsItem.U2(Enumerable.Range(0, 40000).Select(i => (ushort)i).ToArray());
        Assert.Equal(big, SecsCodec.Decode(SecsCodec.Encode(big)));
    }

    [Fact]
    public void Decode_NonMinimalLengthBytes_Accepted()
        => Assert.Equal(SecsItem.U1(7), SecsCodec.Decode(Hex("a7 00 00 01 07")));

    [Fact]
    public void Decode_EmptyBody_IsNull()
    {
        Assert.Null(SecsCodec.Decode([]));
        Assert.Empty(SecsCodec.Encode(null));
    }

    [Theory]
    [InlineData("a4 01 01", "길이 바이트 수가 0")]          // 길이 바이트 수 0
    [InlineData("0d 00", "모르는 포맷")]                     // 포맷 코드 3
    [InlineData("a9 03 00 01 02", "배수가 아닙니다")]       // U2인데 3바이트
    [InlineData("41 05 41 42", "모자랍니다")]               // 데이터 부족
    [InlineData("a5 01 01 00", "남았습니다")]               // 뒤에 남는 바이트
    [InlineData("01 03 a5 01 01", "남은 바이트보다 많습니다")] // 자식 수가 불가능하게 큼
    [InlineData("a6", "길이 바이트가 모자랍니다")]
    public void Decode_Malformed_IsFormatException(string hex, string message)
    {
        var ex = Assert.Throws<FormatException>(() => SecsCodec.Decode(Hex(hex)));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Decode_DeepNesting_IsRejectedNotStackOverflow()
    {
        var body = new byte[2 * 100_000 + 2];
        for (int i = 0; i < 100_000; i++) { body[i * 2] = 0x01; body[i * 2 + 1] = 0x01; }
        body[^2] = 0x01; body[^1] = 0x00;
        var ex = Assert.Throws<FormatException>(() => SecsCodec.Decode(body));
        Assert.Contains("중첩", ex.Message);

        SecsItem ok = SecsItem.L();
        for (int i = 0; i < SecsCodec.MaxDepth; i++) ok = SecsItem.L(ok);
        Assert.Throws<FormatException>(() => SecsCodec.Decode(SecsCodec.Encode(ok)));
        SecsItem fits = SecsItem.L();
        for (int i = 0; i < SecsCodec.MaxDepth - 1; i++) fits = SecsItem.L(fits);
        Assert.Equal(fits, SecsCodec.Decode(SecsCodec.Encode(fits)));
    }

    [Fact]
    public void Boolean_NonZeroByte_IsTrue()
        => Assert.Equal(SecsItem.Boolean(true, false), SecsCodec.Decode(Hex("25 02 ff 00")));
}
