using Devinno.Communication.LS.Data;
using Devinno.Communication.LS.FEnet;

namespace Devinno.Communication.Tests.LS.FEnet;

public class FEnetFrameTests
{
    static LsDevice D(string n) => LsDevice.Parse(n, "x");
    static byte[] H(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    [Fact]
    public void Header_ReadMW100_Invoke1()
    {
        var body = FEnetFrame.RSS([D("%MW100")]);
        Assert.Equal(H("54 00 02 00 00 00 01 00 06 00 25 4D 57 31 30 30"), body);
        var f = FEnetFrame.Request(1, body);
        Assert.Equal(H("4C 53 49 53 2D 58 47 54 00 00 00 00 00 33 01 00 10 00 00 9F"), f[..20]);
        Assert.Equal(body, f[20..]);
    }

    [Fact]
    public void Write_And_Continuous_Bodies()
    {
        var w = FEnetFrame.WSS([(D("%MW100"), 0x1234)]);
        Assert.Equal(H("58 00 02 00 00 00 01 00 06 00 25 4D 57 31 30 30 02 00 34 12"), w);
        Assert.Equal(0xA4, FEnetFrame.Request(2, w)[19]);

        var c = FEnetFrame.RSB(D("%MB100"), 6);
        Assert.Equal(H("54 00 14 00 00 00 01 00 06 00 25 4D 42 31 30 30 06 00"), c);
        Assert.Equal(0xA3, FEnetFrame.Request(3, c)[19]);

        Assert.Equal(H("58 00 14 00 00 00 01 00 06 00 25 4D 42 32 30 30 04 00 01 00 FF FF"),
            FEnetFrame.WSB(D("%MB200"), [1, -1], 2));
    }

    [Fact]
    public void WSS_MultipleBlocks_NamesFirstThenData()
    {
        var w = FEnetFrame.WSS([(D("%MX1"), 1), (D("%MX2"), 0)]);
        Assert.Equal(H("58 00 00 00 00 00 02 00 04 00 25 4D 58 31 04 00 25 4D 58 32 01 00 01 01 00 00"), w);
    }

    [Fact]
    public void ParseHeader_ChecksCompanyAndSource()
    {
        var h = H("4C 53 49 53 2D 58 47 54 00 00 00 00 A0 11 05 00 0C 00 00 00");
        Assert.Equal((12, 5), FEnetFrame.ParseHeader(h)!.Value);
        h[13] = 0x33;
        Assert.Null(FEnetFrame.ParseHeader(h));
        h[13] = 0x11; h[0] = 0x00;
        Assert.Null(FEnetFrame.ParseHeader(h));
    }

    [Fact]
    public void Parse_ReadOk_ManualExample()
    {
        var req = FEnetFrame.RSS([D("%MW100")]);
        var r = FEnetFrame.Parse(H("55 00 02 00 00 00 00 00 01 00 02 00 34 12"), req, 2, 1);
        Assert.Equal(LsStatus.Ack, r.Status);
        Assert.Equal(new long[] { 0x1234 }, r.Values);
    }

    [Fact]
    public void Parse_ContinuousRead_LittleEndianWords()
    {
        var req = FEnetFrame.RSB(D("%MB200"), 4);
        var r = FEnetFrame.Parse(H("55 00 14 00 00 00 00 00 01 00 04 00 34 12 FF FF"), req, 2, 2);
        Assert.Equal(new long[] { 0x1234, 0xFFFF }, r.Values);
    }

    [Fact]
    public void Parse_WriteOk_And_Errors()
    {
        var w = FEnetFrame.WSS([(D("%MW100"), 1)]);
        Assert.Equal(LsStatus.Ack, FEnetFrame.Parse(H("59 00 02 00 00 00 00 00 01 00"), w, 2, 1).Status);

        var req = FEnetFrame.RSS([D("%MW100")]);
        var e2 = FEnetFrame.Parse(H("55 00 02 00 00 00 FF FF 04 00"), req, 2, 1);
        Assert.Equal((LsStatus.Nak, 4), (e2.Status, e2.ErrorCode));
        var e1 = FEnetFrame.Parse(H("55 00 02 00 00 00 FF FF 21"), req, 2, 1);
        Assert.Equal((LsStatus.Nak, 0x21), (e1.Status, e1.ErrorCode));
    }

    [Fact]
    public void Parse_WrongCommandOrSize_Invalid()
    {
        var req = FEnetFrame.RSS([D("%MW100")]);
        Assert.Equal(LsStatus.Invalid, FEnetFrame.Parse(H("59 00 02 00 00 00 00 00 01 00"), req, 2, 1).Status);
        Assert.Equal(LsStatus.Invalid, FEnetFrame.Parse(H("55 00 02 00 00 00 00 00 01 00 04 00 34 12 00 00"), req, 2, 1).Status);
        Assert.Equal(LsStatus.Invalid, FEnetFrame.Parse(H("55 00"), req, 2, 1).Status);
    }
}
