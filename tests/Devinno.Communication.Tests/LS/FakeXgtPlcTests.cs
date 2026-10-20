using System.Text;
using Devinno.Communication.LS.Data;
using Devinno.Communication.LS.CNet;
using Devinno.Communication.LS.FEnet;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.LS;

public class FakeXgtPlcTests
{
    static LsDevice D(string n) => LsDevice.Parse(n, "x");

    [Fact]
    public void Memory_WordAndByteViewsShareBytes()
    {
        using var plc = new FakeXgtPlc();
        plc.Set("%MW100", 0x1234);
        Assert.Equal(0x34, plc.Get("%MB200"));
        Assert.Equal(0x12, plc.Get("%MB201"));
        plc.Set("%MX3", 1);
        Assert.Equal(8, plc.Get("%MB0"));
    }

    [Fact]
    public void Cnet_RssRequest_ReturnsManualStyleAck()
    {
        using var plc = new FakeXgtPlc { Station = 0x20 };
        plc.Set("%MW100", 0xA9F3);
        var res = plc.HandleCnet(CNetFrame.RSS(0x20, [D("%MW100")]));
        Assert.Equal("\x06" + "20rSS0102A9F3" + "\x03" + "39", Encoding.ASCII.GetString(res!));
    }

    [Fact]
    public void Cnet_OtherStation_OrBadBcc_NoResponse()
    {
        using var plc = new FakeXgtPlc { Station = 1 };
        Assert.Null(plc.HandleCnet(CNetFrame.RSS(2, [D("%MW0")])));
        var bad = CNetFrame.RSS(1, [D("%MW0")]);
        bad[^1] = bad[^1] == (byte)'0' ? (byte)'1' : (byte)'0';   // 유효한 16진 문자로, 원래와 다르게
        Assert.Null(plc.HandleCnet(bad));
    }

    [Fact]
    public void Cnet_WsbThenRsb_RoundTrip()
    {
        using var plc = new FakeXgtPlc { Station = 1 };
        Assert.NotNull(plc.HandleCnet(CNetFrame.WSB(1, D("%MW10"), [1, 2, 3])));
        Assert.Equal(new long[] { 1, 2, 3 }, new[] { plc.Get("%MW10"), plc.Get("%MW11"), plc.Get("%MW12") });
        var req = CNetFrame.RSB(1, D("%MW10"), 3);
        var r = CNetFrame.Parse(plc.HandleCnet(req)!, req, 2, 3);
        Assert.Equal(new long[] { 1, 2, 3 }, r.Values);
    }

    [Fact]
    public void Cnet_ForceNak()
    {
        using var plc = new FakeXgtPlc { Station = 1, ForceNak = 0x1132 };
        var req = CNetFrame.RSS(1, [D("%MW0")]);
        var r = CNetFrame.Parse(plc.HandleCnet(req)!, req, 2, 1);
        Assert.Equal((LsStatus.Nak, 0x1132), (r.Status, r.ErrorCode));
    }

    [Fact]
    public void FEnet_IndividualReadWrite()
    {
        using var plc = new FakeXgtPlc();
        var w = FEnetFrame.WSS([(D("%MW100"), 0x1234), (D("%MW101"), 7)]);
        Assert.Equal(LsStatus.Ack, FEnetFrame.Parse(plc.HandleFEnetBody(w), w, 2, 2).Status);
        var r = FEnetFrame.RSS([D("%MW100"), D("%MW101")]);
        Assert.Equal(new long[] { 0x1234, 7 }, FEnetFrame.Parse(plc.HandleFEnetBody(r), r, 2, 2).Values);
    }

    [Fact]
    public void FEnet_ContinuousReadWrite_ByteDevice()
    {
        using var plc = new FakeXgtPlc();
        var w = FEnetFrame.WSB(D("%MB200"), [0x1111, 0x2222], 2);
        Assert.Equal(LsStatus.Ack, FEnetFrame.Parse(plc.HandleFEnetBody(w), w, 2, 2).Status);
        Assert.Equal((0x1111L, 0x2222L), (plc.Get("%MW100"), plc.Get("%MW101")));
        var r = FEnetFrame.RSB(D("%MB200"), 4);
        Assert.Equal(new long[] { 0x1111, 0x2222 }, FEnetFrame.Parse(plc.HandleFEnetBody(r), r, 2, 2).Values);
    }

    [Fact]
    public void FEnet_ForceNak()
    {
        using var plc = new FakeXgtPlc { ForceNak = 0x0004 };
        var r = FEnetFrame.RSS([D("%MW0")]);
        var res = FEnetFrame.Parse(plc.HandleFEnetBody(r), r, 2, 1);
        Assert.Equal((LsStatus.Nak, 4), (res.Status, res.ErrorCode));
    }
}
