using System.Text;
using Devinno.Communication.Mitsubishi.Data;
using Devinno.Communication.Mitsubishi.Serial;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Mitsubishi.Serial;

public class McSerialTests
{
    /// 등록된 작업 하나를 가상 PLC와 주고받는다 (시리얼 포트 없이 송수신·재시도·ACK 순서 검증)
    static McStatus Exchange(McSerial mc, FakeMcPlc plc)
    {
        var w = mc.TakeNextWork()!;
        return mc.Process(w, (req, t) => plc.Handle1C(req, w.Format, w.SumCheck), ack => plc.Handle1C(ack, w.Format, w.SumCheck));
    }

    [Fact]
    public void TakeNextWork_ManualFirst_ThenAuto()
    {
        var mc = new McSerial();
        mc.AutoWR(1, 0, "D0", 1);
        mc.ManualWW(2, 0, "D0", [1]);
        Assert.Equal(2, mc.TakeNextWork()!.MessageID);
        Assert.Equal(1, mc.TakeNextWork()!.MessageID);
        Assert.Equal(1, mc.TakeNextWork()!.MessageID);
    }

    [Theory]
    [InlineData("slave")]
    [InlineData("device")]
    [InlineData("brOnWord")]
    [InlineData("brCount")]
    [InlineData("wrBitCount")]
    [InlineData("bwCount")]
    [InlineData("wwBitCount")]
    [InlineData("bwValue")]
    [InlineData("wwValue")]
    [InlineData("octal")]
    [InlineData("field")]
    [InlineData("wait")]
    public void Register_Invalid_Throws(string kind)
    {
        var mc = new McSerial();
        Action act = kind switch
        {
            "slave" => () => mc.AutoWR(1, 32, "D0", 1),
            "device" => () => mc.AutoWR(1, 0, "SM400", 1),
            "brOnWord" => () => mc.AutoBR(1, 0, "D0", 1),
            "brCount" => () => mc.AutoBR(1, 0, "M0", 257),
            "wrBitCount" => () => mc.AutoWR(1, 0, "M0", 33),
            "bwCount" => () => mc.ManualBW(1, 0, "M0", new long[161]),
            "wwBitCount" => () => mc.ManualWW(1, 0, "M0", new long[11]),
            "bwValue" => () => mc.ManualBW(1, 0, "M0", [2]),
            "wwValue" => () => mc.ManualWW(1, 0, "D0", [65536]),
            "octal" => () => mc.AutoBR(1, 0, "X8", 1),
            "field" => () => mc.AutoWR(1, 0, "D10000", 1),
            _ => () => { mc.MessageWait = 16; mc.AutoWR(1, 0, "D0", 1); },
        };
        Assert.ThrowsAny<ArgumentException>(act);
        Assert.Null(mc.TakeNextWork());
    }

    [Fact]
    public void Limits_AcceptMaximum()
    {
        var mc = new McSerial();
        mc.AutoBR(1, 31, "M0", 256);
        mc.AutoWR(2, 0, "D0", 64);
        mc.ManualBW(3, 0, "M0", new long[160]);
        mc.ManualWW(4, 0, "M0", new long[10]);
        mc.ManualWW(5, 0, "D0", new long[64]);
        Assert.Equal(5, Enumerable.Range(0, 5).Select(_ => mc.TakeNextWork()).Count(w => w != null));
    }

    [Fact]
    public void XYNotation_AutoIsOctal_HexAllowsHexDigits()
    {
        var mc = new McSerial();
        Assert.ThrowsAny<ArgumentException>(() => mc.AutoBR(1, 0, "X1F", 1));
        mc.XYNotation = McXYNotation.Hex;
        mc.AutoBR(1, 0, "X1F", 1);
        Assert.Contains("BR0X001F01", Encoding.ASCII.GetString(mc.TakeNextWork()!.Request));
    }

    [Fact]
    public void SettingsAreCapturedAtRegistration()
    {
        var mc = new McSerial();
        mc.AutoWR(1, 0, "D0", 1);
        mc.Format = McSerialFormat.Format4;
        mc.SumCheck = false;
        mc.MessageWait = 5;
        mc.AutoWR(2, 0, "D0", 1);
        var a = mc.TakeNextWork()!;
        var b = mc.TakeNextWork()!;
        Assert.Equal((McSerialFormat.Format1, true), (a.Format, a.SumCheck));
        Assert.Equal((McSerialFormat.Format4, false), (b.Format, b.SumCheck));
        Assert.Equal("00FFWR5D000001\r\n", Encoding.ASCII.GetString(b.Request[1..]));
    }

    [Fact]
    public void BR_ReadsFake_SendsHostAck_ThenRaisesData()
    {
        using var plc = new FakeMcPlc { Station = 5 };
        plc.Set("X41", 1); plc.Set("X42", 1); plc.Set("X44", 1);
        var mc = new McSerial();
        McSerial.DataEventArgs? got = null;
        int acksAtEvent = -1;
        mc.DataReceived += (s, e) => { got = e; acksAtEvent = plc.HostAcks; };
        mc.ManualBR(7, 5, "x40", 5);
        Assert.Equal(McStatus.Ack, Exchange(mc, plc));
        Assert.Equal((7, 5, McCommand.BR, "X40"), (got!.MessageID, got.Slave, got.Command, got.Device));
        Assert.Equal(new long[] { 0, 1, 1, 0, 1 }, got.Values);
        Assert.Equal(1, acksAtEvent);   // 이벤트 전에 ACK를 보냈다
    }

    [Fact]
    public void WR_WordsUnsigned_AndBitDeviceWordUnits()
    {
        using var plc = new FakeMcPlc();
        plc.Set("D100", 0xFFFF); plc.Set("D101", 7);
        plc.Set("M16", 1); plc.Set("M31", 1);
        var mc = new McSerial();
        var got = new List<long[]>();
        mc.DataReceived += (s, e) => got.Add(e.Values);
        mc.ManualWR(1, 0, "D100", 2);
        mc.ManualWR(2, 0, "M16", 1);
        Assert.Equal(McStatus.Ack, Exchange(mc, plc));
        Assert.Equal(McStatus.Ack, Exchange(mc, plc));
        Assert.Equal(new long[] { 0xFFFF, 7 }, got[0]);
        Assert.Equal(new long[] { 0x8001 }, got[1]);
        Assert.Equal(2, plc.HostAcks);
    }

    [Fact]
    public void BW_WW_WriteFake_NoHostAck()
    {
        using var plc = new FakeMcPlc();
        var mc = new McSerial();
        int writes = 0;
        mc.WriteReceived += (s, e) => writes++;
        mc.ManualBW(1, 0, "M10", [1, 0, 1]);
        mc.ManualWW(2, 0, "D0", [-1, 300]);
        Assert.Equal(McStatus.Ack, Exchange(mc, plc));
        Assert.Equal(McStatus.Ack, Exchange(mc, plc));
        Assert.Equal(2, writes);
        Assert.Equal((1L, 0L, 1L), (plc.Get("M10"), plc.Get("M11"), plc.Get("M12")));
        Assert.Equal((0xFFFFL, 300L), (plc.Get("D0"), plc.Get("D1")));
        Assert.Equal(0, plc.HostAcks);
    }

    [Fact]
    public void Format4_NoSum_RoundTrip()
    {
        using var plc = new FakeMcPlc { Station = 3 };
        var mc = new McSerial { Format = McSerialFormat.Format4, SumCheck = false };
        long[]? read = null;
        mc.DataReceived += (s, e) => read = e.Values;
        mc.ManualWW(1, 3, "D10", [0x1234, 0xABCD]);
        mc.ManualWR(2, 3, "D10", 2);
        Assert.Equal(McStatus.Ack, Exchange(mc, plc));
        Assert.Equal(McStatus.Ack, Exchange(mc, plc));
        Assert.Equal(new long[] { 0x1234, 0xABCD }, read!);
        Assert.Equal(1, plc.HostAcks);
    }

    [Fact]
    public void Nak_RaisesNakOnly_NoRetry_NoHostAck()
    {
        using var plc = new FakeMcPlc { ForceError = 0x06 };
        var mc = new McSerial();
        int code = 0, data = 0, timeouts = 0;
        mc.NAKReceived += (s, e) => code = e.ErrorCode;
        mc.DataReceived += (s, e) => data++;
        mc.TimeoutReceived += (s, e) => timeouts++;
        mc.ManualWR(1, 0, "D0", 1, repeatCount: 2);
        Assert.Equal(McStatus.Nak, Exchange(mc, plc));
        Assert.Equal((0x06, 0, 0), (code, data, timeouts));
        Assert.Equal((1, 0), (plc.Requests, plc.HostAcks));
    }

    [Fact]
    public void SumError_RaisesSumCheckError_AndRetries()
    {
        using var plc = new FakeMcPlc { BadSum = true };
        var mc = new McSerial();
        int sumErrors = 0, data = 0;
        mc.SumCheckErrorReceived += (s, e) => sumErrors++;
        mc.DataReceived += (s, e) => data++;
        mc.ManualWR(1, 0, "D0", 1, repeatCount: 1);
        Assert.Equal(McStatus.SumError, Exchange(mc, plc));
        Assert.Equal((2, 0), (sumErrors, data));
        Assert.Equal((2, 0), (plc.Requests, plc.HostAcks));
    }

    [Fact]
    public void Silent_RaisesTimeout_RepeatCountPlusOne()
    {
        using var plc = new FakeMcPlc { Silent = true };
        var mc = new McSerial();
        int timeouts = 0;
        mc.TimeoutReceived += (s, e) => timeouts++;
        mc.ManualBR(1, 0, "M0", 1, repeatCount: 2);
        Assert.Equal(McStatus.Invalid, Exchange(mc, plc));
        Assert.Equal((3, 3), (timeouts, plc.Requests));
    }

    [Fact]
    public void HandlerThrows_StillReturnsAck()
    {
        using var plc = new FakeMcPlc();
        var mc = new McSerial();
        bool called = false;
        mc.DataReceived += (s, e) => { called = true; throw new InvalidOperationException(); };
        mc.ManualWR(1, 0, "D0", 1);
        Assert.Equal(McStatus.Ack, Exchange(mc, plc));
        Assert.True(called);
    }

    [Fact]
    public async Task Start_InvalidPort_IsStartBecomesFalse()
    {
        using var mc = new McSerial { Port = "COM_NOT_EXIST" };
        mc.Start();
        await TestUtil.Until(() => !mc.IsStart);
    }

    [Fact]
    public void Stop_ClearsIsStartImmediately()
    {
        using var mc = new McSerial { Port = "COM_NOT_EXIST", AutoReconnect = true };
        mc.Start();
        Assert.True(mc.IsStart);
        mc.Stop();
        Assert.False(mc.IsStart);
    }
}
