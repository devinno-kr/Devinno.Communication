using Devinno.Communication.LS.Data;
using Devinno.Communication.LS.CNet;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.LS.CNet;

public class LsCNetTests
{
    /// 등록된 작업 하나를 가상 PLC와 주고받는다 (시리얼 포트 없이 프레임 처리 검증)
    static LsStatus Exchange(LsCNet cnet, FakeXgtPlc plc)
    {
        var w = cnet.TakeNextWork()!;
        var res = plc.HandleCnet(w.Request);
        return res == null ? LsStatus.Invalid : cnet.HandleResponse(w, res);
    }

    [Fact]
    public void TakeNextWork_ManualFirst_ThenAuto()
    {
        var c = new LsCNet();
        c.AutoRSB(1, 1, "%MW0", 1);
        c.ManualWSS(2, 1, [new LsValue("%MW0", 1)]);
        Assert.Equal(2, c.TakeNextWork()!.MessageID);
        Assert.Equal(1, c.TakeNextWork()!.MessageID);
        Assert.Equal(1, c.TakeNextWork()!.MessageID);
    }

    [Theory]
    [InlineData("slave")]
    [InlineData("devices")]
    [InlineData("mixed")]
    [InlineData("rsbBit")]
    [InlineData("rsbCount")]
    [InlineData("wssValue")]
    public void Register_Invalid_Throws(string kind)
    {
        var c = new LsCNet();
        Action act = kind switch
        {
            "slave" => () => c.AutoRSS(1, 256, ["%MW0"]),
            "devices" => () => c.AutoRSS(1, 1, Enumerable.Range(0, 17).Select(i => $"%MW{i}").ToList()),
            "mixed" => () => c.AutoRSS(1, 1, ["%MW0", "%MD0"]),
            "rsbBit" => () => c.AutoRSB(1, 1, "%MX0", 1),
            "rsbCount" => () => c.AutoRSB(1, 1, "%MW0", 61),
            _ => () => c.ManualWSS(1, 1, [new LsValue("%MX0", 2)]),
        };
        Assert.ThrowsAny<ArgumentException>(act);
        Assert.Null(c.TakeNextWork());
    }

    [Fact]
    public void RSS_ReadsFakeMemory_RaisesData()
    {
        using var plc = new FakeXgtPlc { Station = 3 };
        plc.Set("%MW100", 0x1234); plc.Set("%MW101", 0xFFFF);
        var c = new LsCNet();
        LsCNet.DataEventArgs? got = null;
        c.DataReceived += (s, e) => got = e;
        c.ManualRSS(7, 3, ["%mw100", "%MW101"]);
        Assert.Equal(LsStatus.Ack, Exchange(c, plc));
        Assert.Equal((7, 3, LsCommand.RSS), (got!.MessageID, got.Slave, got.Command));
        Assert.Equal(new[] { "%MW100", "%MW101" }, got.Devices);
        Assert.Equal(new long[] { 0x1234, 0xFFFF }, got.Values);
    }

    [Fact]
    public void RSS_SixteenBlocks()
    {
        using var plc = new FakeXgtPlc();
        for (int i = 0; i < 16; i++) plc.Set($"%MW{i}", i * 10);
        var c = new LsCNet();
        long[]? values = null;
        c.DataReceived += (s, e) => values = e.Values;
        c.ManualRSS(1, 1, Enumerable.Range(0, 16).Select(i => $"%MW{i}").ToList());
        Assert.Equal(LsStatus.Ack, Exchange(c, plc));
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (long)i * 10), values!);
    }

    [Fact]
    public void RSS_DWordMax_AndLWordNegative()
    {
        using var plc = new FakeXgtPlc();
        plc.Set("%MD1", 0xFFFFFFFF); plc.Set("%ML1", -2);
        var c = new LsCNet();
        var got = new List<long>();
        c.DataReceived += (s, e) => got.AddRange(e.Values);
        c.ManualRSS(1, 1, ["%MD1"]);
        c.ManualRSS(2, 1, ["%ML1"]);
        Exchange(c, plc); Exchange(c, plc);
        Assert.Equal(new long[] { 4294967295, -2 }, got);
    }

    [Fact]
    public void RSB_And_WSB_RoundTrip()
    {
        using var plc = new FakeXgtPlc();
        var c = new LsCNet();
        int writes = 0; long[]? read = null;
        c.WriteReceived += (s, e) => { if (e.Command == LsCommand.WSB) writes++; };
        c.DataReceived += (s, e) => read = e.Values;
        c.ManualWSB(1, 1, "%DW10", [5, -1, 300]);
        c.ManualRSB(2, 1, "%DW10", 3);
        Assert.Equal(LsStatus.Ack, Exchange(c, plc));
        Assert.Equal(LsStatus.Ack, Exchange(c, plc));
        Assert.Equal(1, writes);
        Assert.Equal(new long[] { 5, 0xFFFF, 300 }, read!);
    }

    [Fact]
    public void WSS_WritesBitsAndWords()
    {
        using var plc = new FakeXgtPlc();
        var c = new LsCNet();
        c.ManualWSS(1, 1, [new LsValue("%MX5", 1), new LsValue("%MX6", 1)]);
        Assert.Equal(LsStatus.Ack, Exchange(c, plc));
        Assert.Equal((1L, 1L, 0L), (plc.Get("%MX5"), plc.Get("%MX6"), plc.Get("%MX4")));
    }

    [Fact]
    public void Nak_RaisesNakOnly()
    {
        using var plc = new FakeXgtPlc { ForceNak = 0x1132 };
        var c = new LsCNet();
        int code = 0, data = 0;
        c.NAKReceived += (s, e) => code = e.ErrorCode;
        c.DataReceived += (s, e) => data++;
        c.ManualRSS(1, 1, ["%MW0"]);
        Assert.Equal(LsStatus.Nak, Exchange(c, plc));
        Assert.Equal((0x1132, 0), (code, data));
    }

    [Fact]
    public void HandlerThrows_StillReturnsAck()
    {
        using var plc = new FakeXgtPlc();
        var c = new LsCNet();
        c.DataReceived += (s, e) => throw new InvalidOperationException();
        c.ManualRSS(1, 1, ["%MW0"]);
        Assert.Equal(LsStatus.Ack, Exchange(c, plc));
    }

    [Fact]
    public async Task Start_InvalidPort_IsStartBecomesFalse()
    {
        using var c = new LsCNet { Port = "COM_NOT_EXIST" };
        c.Start();
        await TestUtil.Until(() => !c.IsStart);
    }

    [Fact]
    public void Stop_ClearsIsStartImmediately()
    {
        using var c = new LsCNet { Port = "COM_NOT_EXIST", AutoReconnect = true };
        c.Start();
        Assert.True(c.IsStart);
        c.Stop();
        Assert.False(c.IsStart);
    }
}
