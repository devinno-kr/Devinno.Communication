using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Serial;

namespace Devinno.Communication.Tests.Secs.Serial;

/// SECS-I 블록. D_ = 근거 조사 5.2의 파생 벡터(실장비 캡처 없음)와 규칙으로 계산한 값
public class SecsIBlockTests
{
    static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    [Fact]
    public void D_S1F1W_HostToEquipment_HeaderOnly()
    {
        var wire = new SecsMessage(1, 1, true).WithHeader(1, 1);
        var blocks = SecsIBlock.Split(wire, rbit: false);
        Assert.Single(blocks);
        Assert.Equal(Hex("0a | 00 01 81 01 80 01 00 00 00 01 | 01 05".Replace("|", "")), blocks[0]);
    }

    [Fact]
    public void D_S1F2_EquipmentToHost_RBit()
    {
        var wire = new SecsMessage(1, 2, false, SecsItem.L(SecsItem.A("MDLN"), SecsItem.A("1.0"))).WithHeader(1, 1);
        var blocks = SecsIBlock.Split(wire, rbit: true);
        Assert.Equal(Hex("17 80 01 01 02 80 01 00 00 00 01 01 02 41 04 4d 44 4c 4e 41 03 31 2e 30 03 4c"), blocks[0]);
    }

    [Fact]
    public void Header_ParseAndBits()
    {
        var h = SecsIHeader.Parse(Hex("80 01 81 0d 80 02 1e 3a 32 54"));
        Assert.Equal(new SecsIHeader(true, 1, true, 1, 13, true, 2, 0x1E3A3254), h);
        Assert.Equal(Hex("80 01 81 0d 80 02 1e 3a 32 54"), h.ToArray());
        var max = new SecsIHeader(false, 32767, false, 127, 255, false, 32767, uint.MaxValue);
        Assert.Equal(Hex("7f ff 7f ff 7f ff ff ff ff ff"), max.ToArray());
        Assert.Equal(max, SecsIHeader.Parse(max.ToArray()));
    }

    [Fact]
    public void Checksum_Is16BitSumOfHeaderAndData()
    {
        Assert.Equal(0x0105, SecsIBlock.Checksum(Hex("00 01 81 01 80 01 00 00 00 01")));
        Assert.Equal(254 * 255, SecsIBlock.Checksum(Enumerable.Repeat((byte)0xFF, 254).ToArray()));   // 넘치지 않는 최대 합
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(244, 1)]
    [InlineData(245, 2)]
    [InlineData(488, 2)]
    [InlineData(489, 3)]
    public void Split_244ByteBoundaries(int bodyLength, int blockCount)
    {
        // B 아이템 본문 = 머리(1 + 길이 바이트) + 데이터 → 원하는 본문 길이가 되게 데이터 길이를 맞춘다
        SecsItem? item = bodyLength == 0 ? null : SecsItem.B(new byte[bodyLength - (bodyLength - 2 > 255 ? 3 : 2)]);
        var wire = new SecsMessage(6, 11, true, item).WithHeader(7, 3);
        Assert.Equal(bodyLength, SecsCodec.Encode(item).Length);
        var blocks = SecsIBlock.Split(wire, rbit: true);
        Assert.Equal(blockCount, blocks.Count);
        for (int i = 0; i < blocks.Count; i++)
        {
            var h = SecsIHeader.Parse(blocks[i].AsSpan(1, 10));
            Assert.Equal((i + 1, i == blocks.Count - 1, 7u, true, 3), (h.BlockNumber, h.EBit, h.SystemBytes, h.RBit, h.DeviceID));
            Assert.Equal(blocks[i].Length - 3, blocks[i][0]);
            Assert.InRange(blocks[i][0], 10, 254);
            if (i < blocks.Count - 1) Assert.Equal(254, blocks[i][0]);
            if (i > 0) Assert.True(h.IsNextOf(SecsIHeader.Parse(blocks[i - 1].AsSpan(1, 10))));
        }
        var body = blocks.SelectMany(b => b.Skip(11).Take(b[0] - 10)).ToArray();
        Assert.Equal(SecsCodec.Encode(item), body);
    }

    [Fact]
    public void IsNextOf_RequiresSameHeaderAndNextNumber()
    {
        var a = new SecsIHeader(false, 1, true, 6, 11, false, 1, 9);
        Assert.True((a with { BlockNumber = 2, EBit = true }).IsNextOf(a));
        Assert.False((a with { BlockNumber = 3 }).IsNextOf(a));
        Assert.False((a with { BlockNumber = 2, SystemBytes = 10 }).IsNextOf(a));
        Assert.False((a with { BlockNumber = 2, Function = 12 }).IsNextOf(a));
        Assert.False((a with { BlockNumber = 2, RBit = true }).IsNextOf(a));
    }

    [Fact]
    public void Split_TooLarge_Throws()
    {
        var ok = SecsItem.B(new byte[SecsIBlock.MaxMessage - 4]);   // 머리 4바이트(길이 바이트 3개) + 데이터 = 최대
        Assert.Equal(SecsIBlock.MaxBlocks, SecsIBlock.Split(new SecsMessage(7, 3, true, ok).WithHeader(1, 0), false).Count);
        var big = SecsItem.B(new byte[SecsIBlock.MaxMessage - 3]);
        Assert.Throws<ArgumentException>(() => SecsIBlock.Split(new SecsMessage(7, 3, true, big).WithHeader(1, 0), false));
    }
}
