using Devinno.Communication.Omron.Data;
using Devinno.Communication.Omron.Ethernet;

namespace Devinno.Communication.Tests.Omron.Ethernet;

/// 벡터 출처(근거 조사 문서 7장): V2 = W421 7-4-3 FINS/TCP 예제, C = 실장비 CP1L-EL20DR-D 캡처(cisagov/icsnpp-omron-fins, BSD-3), D = 파생.
public class FinsTcpFrameTests
{
    static byte[] H(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    [Fact]
    public void V2_NodeRequest_AutoAllocate()
        => Assert.Equal(H("46 49 4E 53 00 00 00 0C 00 00 00 00 00 00 00 00 00 00 00 00"), FinsTcpFrame.NodeRequest(0));

    [Fact]
    public void NodeRequest_FixedNode_InLastByte()
        => Assert.Equal(H("46 49 4E 53 00 00 00 0C 00 00 00 00 00 00 00 00 00 00 00 FE"), FinsTcpFrame.NodeRequest(254));

    [Fact]
    public void V2_FrameHeader_LengthIs8PlusFins()
    {
        var fins = FinsFrame.Command(new FinsNodes(0, 0x64, 0, 0, 0x32, 0), 1, FinsFrame.ReadBody(FinsAddress.Parse("D100", "p"), 150));
        var f = FinsTcpFrame.Frame(fins);
        Assert.Equal(H("46 49 4E 53 00 00 00 1A 00 00 00 02 00 00 00 00"), f[..16]);
        Assert.Equal(fins, f[16..]);
    }

    [Fact]
    public void C_Cp1lCapture_NodeReply_Client251_Server200()
        => Assert.Equal((0xFB, 0xC8), FinsTcpFrame.ParseNodeReply(H("46 49 4E 53 00 00 00 10 00 00 00 01 00 00 00 00 00 00 00 FB 00 00 00 C8")));

    [Fact]
    public void C_Cp1lCapture_FrameSend0501()
        => Assert.Equal(H("46 49 4E 53 00 00 00 15 00 00 00 02 00 00 00 00 80 00 02 00 C8 00 00 00 EF 05 05 01 00"),
            FinsTcpFrame.Frame(FinsFrame.Command(new FinsNodes(0, 0xC8, 0, 0, 0x00, 0xEF), 5, H("05 01 00"))));

    [Fact]
    public void C_Cp1lCapture_ResponseHeader_AndFinsAddresses()
    {
        Assert.Equal((2u, 0u, 0x72 - 8), FinsTcpFrame.ParseHeader(H("46 49 4E 53 00 00 00 72 00 00 00 02 00 00 00 00")));
        // 응답 FINS 헤더: 요청의 DNA/DA1/DA2(00 C8 00)가 응답의 SNA/SA1/SA2로, SID 05
        Assert.True(FinsFrame.IsResponseTo(H("C0 00 02 00 FB EF 00 C8 00 05 05 01 00 00"), new FinsNodes(0, 0xC8, 0, 0, 0x00, 0xEF), 5));
    }

    [Fact]
    public void D_FrameSend_ReadD100x2_Client251_Server200()
        => Assert.Equal(H("46 49 4E 53 00 00 00 1A 00 00 00 02 00 00 00 00 80 00 02 00 C8 00 00 FB 00 01 01 01 82 00 64 00 00 02"),
            FinsTcpFrame.Frame(FinsFrame.Command(FinsTcpFrame.Nodes(0xFB, 0xC8), 1, FinsFrame.ReadBody(FinsAddress.Parse("D100", "p"), 2))));

    [Fact]
    public void D_ErrorNotice_NotFins()
        => Assert.Equal((3u, 1u, 0), FinsTcpFrame.ParseHeader(H("46 49 4E 53 00 00 00 08 00 00 00 03 00 00 00 01")));

    [Theory]
    [InlineData("46 49 4E 58 00 00 00 08 00 00 00 02 00 00 00 00")]
    [InlineData("46 49 4E 53 00 00 00 07 00 00 00 02 00 00 00 00")]
    [InlineData("46 49 4E 53 00 00 07 E5 00 00 00 02 00 00 00 00")]
    [InlineData("46 49 4E 53 FF FF FF FF 00 00 00 02 00 00 00 00")]
    public void ParseHeader_Broken_ThrowsIOException(string hex)
        => Assert.Throws<IOException>(() => FinsTcpFrame.ParseHeader(H(hex)));

    [Fact]
    public void ParseHeader_MaxLength_IsAccepted()
        => Assert.Equal(2012, FinsTcpFrame.ParseHeader(H("46 49 4E 53 00 00 07 E4 00 00 00 02 00 00 00 00")).PayloadLength);

    [Theory]
    [InlineData("46 49 4E 53 00 00 00 10 00 00 00 01 00 00 00 21 00 00 00 FB 00 00 00 C8", "오류 코드 0x21")]
    [InlineData("46 49 4E 53 00 00 00 10 00 00 00 03 00 00 00 00 00 00 00 FB 00 00 00 C8", "명령 3")]
    [InlineData("46 49 4E 53 00 00 00 0C 00 00 00 01 00 00 00 00 00 00 00 FB 00 00 00 C8", "길이 필드")]
    [InlineData("46 49 4E 53 00 00 00 10 00 00 00 01 00 00 00 00 00 00 00 00 00 00 00 C8", "클라이언트 노드 0")]
    [InlineData("46 49 4E 53 00 00 00 10 00 00 00 01 00 00 00 00 00 00 00 FB 00 00 00 FF", "서버 노드 255")]
    [InlineData("46 49 4E 53 00 00 00 10 00 00 00 01 00 00 00 00 00 00 00 FB 00 00 00", "짧음")]
    public void ParseNodeReply_Invalid_IsNull(string hex, string why)
        => Assert.True(FinsTcpFrame.ParseNodeReply(H(hex)) == null, why);
}
