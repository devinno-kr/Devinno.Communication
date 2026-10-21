using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// GemFormats(2층 ID 포맷 묶음)와 공개 GemTime
public class GemFormatsTests
{
    [Fact]
    public void Default_AllU4()
    {
        var d = GemFormats.Default;
        Assert.Equal(SecsFormat.U4, d.Vid);
        Assert.Equal(SecsFormat.U4, d.Ceid);
        Assert.Equal(SecsFormat.U4, d.Rptid);
        Assert.Equal(SecsFormat.U4, d.Alid);
        Assert.Equal(SecsFormat.U4, d.Dataid);
    }

    [Fact]
    public void Init_ChecksIdFormat()
    {
        Assert.Throws<ArgumentException>(() => new GemFormats { Ceid = SecsFormat.F4 });
        Assert.Throws<ArgumentException>(() => new GemFormats { Vid = SecsFormat.List });
        Assert.Equal(SecsFormat.ASCII, new GemFormats { Alid = SecsFormat.ASCII }.Alid);
    }

    [Fact]
    public void Of_SetsAllFive()
    {
        var f = GemFormats.Of(SecsFormat.U2, SecsFormat.I4, SecsFormat.ASCII, SecsFormat.U1, SecsFormat.U8);
        Assert.Equal((SecsFormat.U2, SecsFormat.I4, SecsFormat.ASCII, SecsFormat.U1, SecsFormat.U8), (f.Vid, f.Ceid, f.Rptid, f.Alid, f.Dataid));
        Assert.Throws<ArgumentException>(() => GemFormats.Of(SecsFormat.F8, SecsFormat.U4, SecsFormat.U4, SecsFormat.U4, SecsFormat.U4));
    }

    [Fact]
    public void GemTime_IsPublic()
    {
        Assert.True(typeof(GemTime).IsPublic);
        Assert.Equal("261021090807", GemTime.Format(new DateTime(2026, 10, 21, 9, 8, 7, DateTimeKind.Local), 0));
        Assert.True(GemTime.TryParse("2026102109080712", out var t));
        Assert.Equal(new DateTime(2026, 10, 21, 9, 8, 7, 120), t);
    }
}
