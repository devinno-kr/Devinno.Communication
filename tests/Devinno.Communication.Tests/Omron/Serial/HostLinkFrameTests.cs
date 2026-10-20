using System.Text;
using Devinno.Communication.Omron.Data;
using Devinno.Communication.Omron.Serial;

namespace Devinno.Communication.Tests.Omron.Serial;

/// 벡터 출처(근거 조사 문서 7.3): W342 3-5-3 필드 순서와 FCS 규칙으로 계산한 파생 값(D). FCS 규칙은 제3자 실프레임(HLA: "@00XZ42*", "@00MS5E*")으로 교차 확인.
public class HostLinkFrameTests
{
    static byte[] A(string s) => Encoding.ASCII.GetBytes(s);
    static FinsChunk ReadChunk(string start, int count) => FinsFrame.Chunks(FinsCommand.ReadBlock, [FinsAddress.Parse(start, "p")], count, [], 266)[0];

    [Theory]
    [InlineData("@00XZ", "42")]
    [InlineData("@00MS", "5E")]
    [InlineData("@00RD01000002", "55")]
    public void Fcs_XorOfAllCharacters(string text, string fcs) => Assert.Equal(fcs, HostLinkFrame.Fcs(A(text)));

    [Fact]
    public void D_ReadD100x2_Unit0_Wait0()
        => Assert.Equal("@00FA00000000001018200640000027D*\r", Encoding.ASCII.GetString(HostLinkFrame.Command(0, 0, ReadChunk("D100", 2).Body)));

    [Fact]
    public void D_WriteD100_0x1234()
    {
        var body = FinsFrame.Chunks(FinsCommand.Write, [FinsAddress.Parse("D100", "p")], 1, [0x1234], 266)[0].Body;
        Assert.Equal("@00FA0000000000102820064000001123479*\r", Encoding.ASCII.GetString(HostLinkFrame.Command(0, 0, body)));
    }

    [Fact]
    public void D_Unit31_Wait5_ReadCio0()
        => Assert.Equal("@31FA5000000000101B0000000000103*\r", Encoding.ASCII.GetString(HostLinkFrame.Command(31, 5, ReadChunk("CIO0", 1).Body)));

    [Theory]
    [InlineData("@00FA004000000001010000123456784B*\r")]   // ICF 앞에 Host Link 종료 코드 "00"
    [InlineData("@00FA4000000001010000123456784B*\r")]     // 종료 코드 없음
    [InlineData("@00FA004000000001010000123456784b*\r")]   // FCS 소문자
    public void Parse_BothResponseLayouts(string frame)
    {
        var r = HostLinkFrame.Parse(A(frame), 0, ReadChunk("D100", 2));
        Assert.Equal(FinsStatus.Ack, r.Status);
        Assert.Equal(new long[] { 0x1234, 0x5678 }, r.Values);
    }

    [Theory]
    [InlineData("C0", "")]
    [InlineData("C0", "00")]
    public void Parse_IcfC0_AcceptedLikeIcf40(string icf, string endCode)
    {
        var text = "@00FA" + endCode + icf + "000000010100001234";
        var r = HostLinkFrame.Parse(A(text + HostLinkFrame.Fcs(A(text)) + "*\r"), 0, ReadChunk("D100", 1));
        Assert.Equal(FinsStatus.Ack, r.Status);
        Assert.Equal(new long[] { 0x1234 }, r.Values);
    }

    [Fact]
    public void Parse_FcsMismatch_IsFcsError()
        => Assert.Equal(FinsStatus.FcsError, HostLinkFrame.Parse(A("@00FA004000000001010000123456784C*\r"), 0, ReadChunk("D100", 2)).Status);

    [Fact]
    public void Parse_EndCode_IsNak()
    {
        var r = HostLinkFrame.Parse(A("@00FA00400000000101110340*\r"), 0, ReadChunk("D100", 2));
        Assert.Equal((FinsStatus.Nak, 0x1103), (r.Status, r.ErrorCode));
    }

    [Fact]
    public void Parse_UnitNumber_MustMatch()
    {
        var frame = A("@05FA004000000001010000ABCD42*\r");
        Assert.Equal(new long[] { 0xABCD }, HostLinkFrame.Parse(frame, 5, ReadChunk("D100", 1)).Values);
        Assert.Equal(FinsStatus.Invalid, HostLinkFrame.Parse(frame, 0, ReadChunk("D100", 1)).Status);
    }

    [Theory]
    [InlineData("@00FA1442*\r", "Invalid", "Host Link 오류 코드만")]
    [InlineData("@00FA80000000010100001234567847*\r", "Invalid", "ICF 80")]
    [InlineData("@00RD00123452*\r", "Invalid", "FA 아님")]
    [InlineData("@00FA004000000001010000123456784B*", "Invalid", "CR 없음")]
    [InlineData("@00FA00400000000101000012345678X*\r", "FcsError", "FCS 자리 부족")]
    [InlineData("@00FA0040000000010100001234567*\r", "FcsError", "짝이 맞지 않는 16진수")]
    public void Parse_Malformed_HasExactStatus(string frame, string expected, string why)
        => Assert.True(expected == HostLinkFrame.Parse(A(frame), 0, ReadChunk("D100", 2)).Status.ToString(), why);

    [Fact]
    public void Parse_OddHex_WithValidFcs_IsInvalid()
    {
        var text = "@00FA0040000000010100001234567";
        Assert.Equal(FinsStatus.Invalid, HostLinkFrame.Parse(A(text + HostLinkFrame.Fcs(A(text)) + "*\r"), 0, ReadChunk("D100", 2)).Status);
    }

    [Fact]
    public void Find_SkipsGarbage_WaitsForTerminator()
    {
        Assert.Equal((0, 0), HostLinkFrame.Find(A("xx@00FA0040")));
        Assert.Equal((2, 27), HostLinkFrame.Find(A("xx@00FA00400000000102000040*\r")));
        Assert.Equal((0, 0), HostLinkFrame.Find(A("garbage*\r")));
    }
}
