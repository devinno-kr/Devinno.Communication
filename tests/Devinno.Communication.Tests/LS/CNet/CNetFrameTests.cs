using System.Text;
using Devinno.Communication.LS.Data;
using Devinno.Communication.LS.CNet;

namespace Devinno.Communication.Tests.LS.CNet;

public class CNetFrameTests
{
    static byte[] A(string s) => Encoding.ASCII.GetBytes(s.Replace("<ENQ>", "\x05").Replace("<EOT>", "\x04").Replace("<ACK>", "\x06").Replace("<NAK>", "\x15").Replace("<ETX>", "\x03"));
    static LsDevice D(string n) => LsDevice.Parse(n, "x");

    [Fact]
    public void RSS_ManualExample_BccA4()
    {
        // 공식 매뉴얼 7.2.1: 05 32 30 72 53 53 30 31 30 36 25 4D 57 31 30 30 04 41 34
        Assert.Equal(A("<ENQ>20rSS0106%MW100<EOT>A4"), CNetFrame.RSS(0x20, [D("%MW100")]));
    }

    [Fact]
    public void Requests_Encoding()
    {
        Assert.Equal(A("<ENQ>10rSB06%MW10005<EOT>96"), CNetFrame.RSB(0x10, D("%MW100"), 5));
        Assert.Equal(A("<ENQ>01wSS0106%MW23000FF<EOT>98"), CNetFrame.WSS(0x01, [(D("%MW230"), 0xFF)]));
        Assert.Equal(A("<ENQ>10wSB06%MW1000211112222<EOT>24"), CNetFrame.WSB(0x10, D("%MW100"), [0x1111, 0x2222]));
    }

    [Fact]
    public void WSS_ValueWidthAndTwosComplement()
    {
        var f = Encoding.ASCII.GetString(CNetFrame.WSS(1, [(D("%MD0"), -1), (D("%MD2"), 0x12)]));
        Assert.Contains("04%MD0FFFFFFFF04%MD200000012", f);
        var b = Encoding.ASCII.GetString(CNetFrame.WSS(1, [(D("%MX3"), 1)]));
        Assert.Contains("04%MX301", b);
    }

    [Fact]
    public void RSS_SixteenBlocks_CountIsHex()
    {
        var ds = Enumerable.Range(0, 16).Select(i => D($"%MW{i}")).ToList();
        Assert.StartsWith("\x05" + "01rSS10", Encoding.ASCII.GetString(CNetFrame.RSS(1, ds)));
    }

    [Fact]
    public void FrameLength_WaitsForEtxAndBcc()
    {
        Assert.Equal(-1, CNetFrame.FrameLength(A("<ACK>20rSS0102A9F3")));
        Assert.Equal(-1, CNetFrame.FrameLength(A("<ACK>20rSS0102A9F3<ETX>3")));
        Assert.Equal(17, CNetFrame.FrameLength(A("<ACK>20rSS0102A9F3<ETX>39")));
    }

    [Fact]
    public void Parse_RssAck()
    {
        var req = CNetFrame.RSS(0x20, [D("%MW100")]);
        var r = CNetFrame.Parse(A("<ACK>20rSS0102A9F3<ETX>39"), req, 2, 1);
        Assert.Equal(LsStatus.Ack, r.Status);
        Assert.Equal(new long[] { 0xA9F3 }, r.Values);
    }

    [Fact]
    public void Parse_RsbAck_ManualExample()
    {
        var req = CNetFrame.RSB(0x10, D("%MW100"), 2);
        var r = CNetFrame.Parse(A("<ACK>10rSB010412345678<ETX>DA"), req, 2, 2);
        Assert.Equal(LsStatus.Ack, r.Status);
        Assert.Equal(new long[] { 0x1234, 0x5678 }, r.Values);
    }

    [Fact]
    public void Parse_WriteAck_AndNak()
    {
        var req = CNetFrame.WSS(0x20, [(D("%MW100"), 0xE2)]);
        Assert.Equal(LsStatus.Ack, CNetFrame.Parse(A("<ACK>20wSS<ETX>88"), req, 2, 1).Status);

        var rreq = CNetFrame.RSS(0x20, [D("%MW100")]);
        var nak = CNetFrame.Parse(A("<NAK>20rSS1132<ETX>59"), rreq, 2, 1);
        Assert.Equal((LsStatus.Nak, 0x1132), (nak.Status, nak.ErrorCode));
    }

    [Fact]
    public void Parse_BadBcc_StationMismatch_WrongWidth()
    {
        var req = CNetFrame.RSS(0x20, [D("%MW100")]);
        Assert.Equal(LsStatus.BccError, CNetFrame.Parse(A("<ACK>20rSS0102A9F3<ETX>38"), req, 2, 1).Status);
        Assert.Equal(LsStatus.Invalid, CNetFrame.Parse(A("<ACK>21rSS0102A9F3<ETX>3A"), req, 2, 1).Status);
        Assert.Equal(LsStatus.Invalid, CNetFrame.Parse(A("<ACK>20rSS0104A9F3A9F3<ETX>" + Bcc("<ACK>20rSS0104A9F3A9F3<ETX>")), req, 2, 1).Status);
        Assert.Equal(LsStatus.Invalid, CNetFrame.Parse(A("<ACK>20rS"), req, 2, 1).Status);
    }

    static string Bcc(string s) => CNetFrame.Bcc(A(s)).ToString("X2");
}
