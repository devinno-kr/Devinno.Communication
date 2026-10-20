using Devinno.Communication.Omron.Data;

namespace Devinno.Communication.Tests.Omron;

public class FinsRequestTests
{
    [Fact]
    public void Read_ParsesItems_UpToTransportLimit()
    {
        var r = FinsRequest.Read(["dm100", "0.05"], FinsFrame.MaxItems);
        Assert.Equal((FinsCommand.Read, 2), (r.Command, r.Count));
        Assert.Equal(new[] { "D100", "CIO0.05" }, r.Items.Select(a => a.Name));
        Assert.Empty(r.Values);

        var names = Enumerable.Range(0, FinsFrame.SerialMaxItems + 1).Select(i => $"D{i}").ToArray();
        Assert.Equal(FinsFrame.SerialMaxItems + 1, FinsRequest.Read(names, FinsFrame.MaxItems).Count);
        var ex = Assert.Throws<ArgumentException>(() => FinsRequest.Read(names, FinsFrame.SerialMaxItems));
        Assert.Equal("addresses", ex.ParamName);
        Assert.Contains("1~134", ex.Message);
        Assert.Equal("addresses", Assert.Throws<ArgumentException>(() => FinsRequest.Read([], FinsFrame.MaxItems)).ParamName);
        Assert.Equal("addresses", Assert.Throws<ArgumentNullException>(() => FinsRequest.Read(null!, FinsFrame.MaxItems)).ParamName);
        Assert.Equal("addresses", Assert.Throws<ArgumentException>(() => FinsRequest.Read(["D0", "T5"], FinsFrame.MaxItems)).ParamName);
    }

    [Fact]
    public void ReadBlock_CountAndAreaEnd_Checked()
    {
        var r = FinsRequest.ReadBlock("100.14", 4);
        Assert.Equal((FinsCommand.ReadBlock, 4, "CIO100.14"), (r.Command, r.Count, Assert.Single(r.Items).Name));
        Assert.Equal("count", Assert.Throws<ArgumentOutOfRangeException>(() => FinsRequest.ReadBlock("D0", 0)).ParamName);
        Assert.Equal("count", Assert.Throws<ArgumentOutOfRangeException>(() => FinsRequest.ReadBlock("D0", 65536)).ParamName);
        Assert.Equal("count", Assert.Throws<ArgumentOutOfRangeException>(() => FinsRequest.ReadBlock("D32767", 2)).ParamName);
        Assert.Equal(1, FinsRequest.ReadBlock("D32767", 1).Count);
        Assert.Equal("address", Assert.Throws<ArgumentException>(() => FinsRequest.ReadBlock("X0", 1)).ParamName);
    }

    [Theory]
    [InlineData("D0", -32768L, true)]
    [InlineData("D0", 65535L, true)]
    [InlineData("D0", -32769L, false)]
    [InlineData("D0", 65536L, false)]
    [InlineData("D0.00", 0L, true)]
    [InlineData("D0.00", 1L, true)]
    [InlineData("D0.00", 2L, false)]
    [InlineData("CIO0.15", -1L, false)]
    public void Write_ValueRange(string address, long value, bool ok)
    {
        if (ok)
        {
            var r = FinsRequest.Write(address, value);
            Assert.Equal((FinsCommand.Write, 1, value), (r.Command, r.Count, Assert.Single(r.Values)));
        }
        else Assert.Equal("value", Assert.Throws<ArgumentOutOfRangeException>(() => FinsRequest.Write(address, value)).ParamName);
    }

    [Fact]
    public void WriteBlock_ChecksEveryValue_CountAndArea_AndCopies()
    {
        Assert.Equal("values", Assert.Throws<ArgumentNullException>(() => FinsRequest.WriteBlock("D0", null!)).ParamName);
        Assert.Equal("values", Assert.Throws<ArgumentOutOfRangeException>(() => FinsRequest.WriteBlock("D0", [])).ParamName);
        Assert.Equal("values", Assert.Throws<ArgumentOutOfRangeException>(() => FinsRequest.WriteBlock("W511", [1, 2])).ParamName);
        Assert.Equal("values", Assert.Throws<ArgumentOutOfRangeException>(() => FinsRequest.WriteBlock("CIO0.00", [0, 1, 5])).ParamName);
        Assert.Equal("values", Assert.Throws<ArgumentOutOfRangeException>(() => FinsRequest.WriteBlock("D0", [1, 70000])).ParamName);
        Assert.Equal("address", Assert.Throws<ArgumentException>(() => FinsRequest.WriteBlock("Q0", [1])).ParamName);

        var values = new long[] { -1, 2, 3 };
        var r = FinsRequest.WriteBlock("H0", values);
        values[0] = 99;
        Assert.Equal((FinsCommand.WriteBlock, 3, "H0"), (r.Command, r.Count, Assert.Single(r.Items).Name));
        Assert.Equal(new long[] { -1, 2, 3 }, r.Values);
    }

    static FinsChunk[] Chunks(FinsRequest r, int max) => FinsFrame.Chunks(r.Command, r.Items, r.Count, r.Values, max);

    [Fact]
    public void Chunks_SplitBlocksAtLimit_SingleRequestOtherwise()
    {
        var bits = FinsRequest.WriteBlock("CIO0.10", Enumerable.Range(0, 40).Select(i => (long)(i % 2)).ToArray());
        var c = Chunks(bits, 16);
        Assert.Equal(new[] { (0, 10, 16), (1, 10, 16), (2, 10, 8) }, c.Select(x => (x.Items[0].Word, x.Items[0].Bit, x.Count)));
        Assert.All(c, x => Assert.Equal(new byte[] { 0x01, 0x02, 0x30 }, x.Body[..3]));

        var words = Chunks(FinsRequest.ReadBlock("D5", 21), 10);
        Assert.Equal(new[] { (5, 10), (15, 10), (25, 1) }, words.Select(x => (x.Items[0].Word, x.Count)));
        Assert.Single(Chunks(FinsRequest.ReadBlock("W0", 499), 499));

        var read = Assert.Single(Chunks(FinsRequest.Read(["D0", "W1.02"], FinsFrame.MaxItems), 1));
        Assert.Equal(Convert.FromHexString("010482000000 31000102".Replace(" ", "")), read.Body);
        var write = Assert.Single(Chunks(FinsRequest.Write("D100", -1), 1));
        Assert.Equal(Convert.FromHexString("01028200640000 01FFFF".Replace(" ", "")), write.Body);
    }
}
