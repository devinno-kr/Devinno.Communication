using System.IO.Ports;
using System.Text;
using Devinno.Communication.Omron.Data;
using Devinno.Communication.Omron.Serial;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Omron.Serial;

public class FinsSerialTests
{
    readonly FakeOmronPlc plc = new();
    readonly FinsSerial fins = new() { DiscardWritesWhileDisconnected = false };
    readonly List<string> sent = [];

    /// 등록된 작업 하나를 가상 PLC의 Host Link 처리기와 주고받는다 (시리얼 포트 없이 송수신·조각·재시도 검증)
    FinsStatus Exchange()
    {
        var w = fins.TakeNextWork()!;
        byte[]? reply = null;
        return fins.Process(w, req => { sent.Add(Encoding.ASCII.GetString(req)); reply = plc.HandleHostLink(req); }, t => reply);
    }

    [Fact]
    public void Defaults_AreHostLinkDefaults()
        => Assert.Equal(("COM1", 9600, Parity.Even, 7, StopBits.Two, 0, 0, 266, 1000, 10, false),
            (fins.Port, fins.Baudrate, fins.Parity, fins.DataBits, fins.StopBits, fins.UnitNumber, fins.ResponseWait, fins.MaxWordsPerRequest, fins.Timeout, fins.Interval, fins.AutoReconnect));

    [Fact]
    public void Properties_RangeChecked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => fins.UnitNumber = 32);
        Assert.Throws<ArgumentOutOfRangeException>(() => fins.ResponseWait = 16);
        Assert.Throws<ArgumentOutOfRangeException>(() => fins.MaxWordsPerRequest = 267);
        Assert.Throws<ArgumentOutOfRangeException>(() => fins.MaxWordsPerRequest = 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Read_BothResponseLayouts(bool hostLinkEndCode)
    {
        plc.HostLinkEndCode = hostLinkEndCode;
        plc.Set("D100", 0x1234); plc.Set("CIO0.05", 1); plc.Set("E0_7", 7);
        FinsSerial.DataEventArgs? got = null;
        fins.DataReceived += (s, e) => got = e;
        fins.ManualRead(1, ["d100", "0.05", "em0_7"]);
        Assert.Equal(FinsStatus.Ack, Exchange());
        Assert.Equal((1, FinsCommand.Read), (got!.MessageID, got.Command));
        Assert.Equal(new[] { "D100", "CIO0.05", "E0_7" }, got.Addresses);
        Assert.Equal(new long[] { 0x1234, 1, 7 }, got.Values);
    }

    [Fact]
    public void UnitNumberAndResponseWait_InFrame()
    {
        plc.UnitNumber = 31;
        fins.UnitNumber = 31;
        fins.ResponseWait = 5;
        fins.ManualReadBlock(1, "CIO0", 1);
        Assert.Equal(FinsStatus.Ack, Exchange());
        Assert.Equal("@31FA5000000000101B0000000000103*\r", sent[0]);
    }

    [Fact]
    public void WrongUnitNumber_NoReply_Timeout()
    {
        plc.UnitNumber = 1;
        int timeouts = 0;
        fins.TimeoutReceived += (s, e) => timeouts++;
        fins.ManualRead(1, ["D0"], repeatCount: 1);
        Assert.Equal(FinsStatus.Invalid, Exchange());
        Assert.Equal(2, timeouts);
    }

    [Fact]
    public void WriteWordAndBit_UpdatesPlc()
    {
        int writes = 0;
        fins.WriteReceived += (s, e) => writes++;
        fins.ManualWrite(1, "D10", -2);
        fins.ManualWrite(2, "H5.15", 1);
        Assert.Equal(FinsStatus.Ack, Exchange());
        Assert.Equal(FinsStatus.Ack, Exchange());
        Assert.Equal((0xFFFEL, 0x8000L, 2), (plc.Get("D10"), plc.Get("H5"), writes));
    }

    [Fact]
    public void WriteBlock_SplitAt266_ThenReadBlockBack()
    {
        var values = Enumerable.Range(0, 600).Select(i => (long)(i * 11 % 65536)).ToArray();
        fins.ManualWriteBlock(1, "D1000", values);
        Assert.Equal(FinsStatus.Ack, Exchange());
        Assert.Equal(new (byte, byte, int, int, int)[] { (2, 0x82, 1000, 0, 266), (2, 0x82, 1266, 0, 266), (2, 0x82, 1532, 0, 68) }, plc.Requests);
        Assert.All(sent, f => Assert.True(f.Length <= 1114, $"요청 {f.Length}글자"));

        plc.ClearRequests();
        long[]? got = null;
        fins.DataReceived += (s, e) => got = e.Values;
        fins.ManualReadBlock(2, "D1000", 600);
        Assert.Equal(FinsStatus.Ack, Exchange());
        Assert.Equal(values, got);
        Assert.Equal(3, plc.Requests.Length);
    }

    [Fact]
    public void MultipleRead_134Max()
    {
        var names = Enumerable.Range(0, 135).Select(i => $"D{i}").ToArray();
        Assert.Contains("1~134", Assert.Throws<ArgumentException>(() => fins.AutoRead(1, names)).Message);
        fins.ManualRead(2, names[..134]);
        Assert.Equal(FinsStatus.Ack, Exchange());
        Assert.True(sent[0].Length <= 1114);
    }

    [Fact]
    public void FcsError_RaisesEvent_AndRetries()
    {
        plc.CorruptFcs = 1;
        plc.Set("D0", 42);
        int fcsErrors = 0, timeouts = 0;
        long? value = null;
        fins.FcsErrorReceived += (s, e) => fcsErrors++;
        fins.TimeoutReceived += (s, e) => timeouts++;
        fins.DataReceived += (s, e) => value = e.Values[0];
        fins.ManualRead(1, ["D0"], repeatCount: 1);
        Assert.Equal(FinsStatus.Ack, Exchange());
        Assert.Equal((1, 0, 42L), (fcsErrors, timeouts, value));
        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public void FcsError_NoRepeat_NoData()
    {
        plc.CorruptFcs = 5;
        int fcsErrors = 0, data = 0;
        fins.FcsErrorReceived += (s, e) => fcsErrors++;
        fins.DataReceived += (s, e) => data++;
        fins.ManualRead(1, ["D0"]);
        Assert.Equal(FinsStatus.FcsError, Exchange());
        Assert.Equal((1, 0), (fcsErrors, data));
    }

    [Fact]
    public void EndCode_RaisesNak_NoRetry()
    {
        plc.EndCode = 0x1101;
        int naks = 0, timeouts = 0, code = 0;
        fins.NAKReceived += (s, e) => { naks++; code = e.ErrorCode; };
        fins.TimeoutReceived += (s, e) => timeouts++;
        fins.ManualRead(1, ["D0"], repeatCount: 3);
        Assert.Equal(FinsStatus.Nak, Exchange());
        Assert.Equal((1, 0x1101, 0, 1), (naks, code, timeouts, sent.Count));
    }

    [Fact]
    public void NonFatalFlag_IsNotError()
    {
        plc.FlagBits = 0x0040;
        fins.ManualWrite(1, "D0", 3);
        Assert.Equal(FinsStatus.Ack, Exchange());
        Assert.Equal(3L, plc.Get("D0"));
    }

    [Fact]
    public void Silent_TimeoutRepeatCountPlusOne()
    {
        plc.Silent = true;
        int timeouts = 0;
        fins.TimeoutReceived += (s, e) => timeouts++;
        fins.ManualReadBlock(1, "D0", 10, repeatCount: 2);
        Assert.Equal(FinsStatus.Invalid, Exchange());
        Assert.Equal((3, 3), (timeouts, sent.Count));
    }

    [Fact]
    public void TakeNextWork_ManualFirst_ThenAuto()
    {
        fins.AutoRead(1, ["D0"]);
        fins.ManualWrite(2, "D0", 1);
        Assert.Equal(2, fins.TakeNextWork()!.MessageID);
        Assert.Equal(1, fins.TakeNextWork()!.MessageID);
        Assert.Equal(1, fins.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void Register_Invalid_Throws()
    {
        Assert.Throws<ArgumentException>(() => fins.AutoRead(1, ["TIM0"]));
        Assert.Throws<ArgumentOutOfRangeException>(() => fins.AutoReadBlock(1, "H511", 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => fins.ManualWrite(1, "W0.00", -1));
        Assert.Throws<ArgumentNullException>(() => fins.ManualWriteBlock(1, "D0", null!));
        Assert.Null(fins.TakeNextWork());
    }

    [Fact]
    public async Task PortOpenFails_NoAutoReconnect_LoopEnds()
    {
        using var f = new FinsSerial { Port = "COM_DEVINNO_NONE" };
        int opened = 0;
        f.DeviceOpened += (s, e) => opened++;
        f.Start();
        await TestUtil.Until(() => !f.IsStart, 3000);
        Assert.Equal(0, opened);
        Assert.False(f.IsOpen);
    }
}
