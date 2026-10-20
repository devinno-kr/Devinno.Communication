using System.Text;
using Devinno.Communication.TextComm;
using Devinno.Communication.TextComm.RTU;

namespace Devinno.Communication.Tests.TextComm.RTU;

public class TextCommRTUMasterTests
{
    /// 가짜 회선: 보낸 요청을 기록하고, 요청마다 정해 둔 응답 조각들을 차례로 돌려준다(없으면 null = 시간 초과)
    sealed class Line
    {
        readonly Queue<byte[][]> replies = new();
        Queue<byte[]> current = new();
        public List<byte[]> Sent { get; } = [];
        public Line Reply(params byte[][] chunks) { replies.Enqueue(chunks); return this; }
        public void Send(byte[] request)
        {
            Sent.Add(request);
            current = new Queue<byte[]>(replies.Count > 0 ? replies.Dequeue() : []);
        }
        public byte[]? Read(int remain) => current.Count > 0 ? current.Dequeue() : null;
    }

    static TextCommStatus Exchange(TextCommRTUMaster tc, Line line)
    {
        var w = tc.TakeNextWork()!;
        return tc.Process(w, line.Send, line.Read);
    }

    [Fact]
    public void Defaults()
    {
        var tc = new TextCommRTUMaster();
        Assert.Equal(("COM1", 115200, System.IO.Ports.Parity.None, 8, System.IO.Ports.StopBits.One), (tc.Port, tc.Baudrate, tc.Parity, tc.DataBits, tc.StopBits));
        Assert.Equal((100, 10, false, 1024), (tc.Timeout, tc.Interval, tc.AutoReconnect, tc.BufferSize));
        Assert.Same(Encoding.ASCII, tc.MessageEncoding);
    }

    [Fact]
    public void TakeNextWork_ManualFirst_ThenAuto()
    {
        var tc = new TextCommRTUMaster();
        tc.AutoSend(1, 1, 0x41, "A");
        tc.ManualSend(2, 1, 0x42, "B");
        Assert.Equal(2, tc.TakeNextWork()!.MessageID);
        Assert.Equal(1, tc.TakeNextWork()!.MessageID);
        Assert.Equal(1, tc.TakeNextWork()!.MessageID);
    }

    [Theory]
    [InlineData(-1, 0, "slave")]
    [InlineData(256, 0, "slave")]
    [InlineData(0, -1, "command")]
    [InlineData(0, 256, "command")]
    public void Register_OutOfRange_Throws(int slave, int command, string param)
    {
        var tc = new TextCommRTUMaster();
        Assert.Equal(param, Assert.Throws<ArgumentOutOfRangeException>(() => tc.AutoSend(1, slave, command, "A")).ParamName);
        Assert.Equal(param, Assert.Throws<ArgumentOutOfRangeException>(() => tc.ManualSend(1, slave, command, "A")).ParamName);
        Assert.Null(tc.TakeNextWork());
    }

    [Fact]
    public void Register_NullMessage_Throws()
    {
        var tc = new TextCommRTUMaster();
        Assert.Equal("message", Assert.Throws<ArgumentNullException>(() => tc.AutoSend(1, 1, 1, null!)).ParamName);
        Assert.Equal("message", Assert.Throws<ArgumentNullException>(() => tc.ManualSend(1, 1, 1, null!)).ParamName);
        Assert.Null(tc.TakeNextWork());
    }

    [Fact]
    public void Request_IsGoingFrame_EdgesAccepted()
    {
        var tc = new TextCommRTUMaster();
        tc.ManualSend(1, 1, 0x41, "READ");
        tc.ManualSend(2, 255, 0, "");
        Assert.Equal(new byte[] { 0x02, 0x01, 0x41, 0x52, 0x45, 0x41, 0x44, 0x5E, 0x03 }, tc.TakeNextWork()!.Request);   // 기준 바이트
        Assert.Equal(TextCommPacket.Build(255, 0, []), tc.TakeNextWork()!.Request);
    }

    [Fact]
    public void Match_RaisesMessageReceived()
    {
        var tc = new TextCommRTUMaster();
        TextCommRTUMaster.MessageEventArgs? got = null;
        tc.MessageReceived += (s, e) => got = e;
        tc.ManualSend(7, 1, 0x41, "READ");
        var line = new Line().Reply(TextCommPacket.Build(1, 0x41, "OK 12"u8));
        Assert.Equal(TextCommStatus.Ok, Exchange(tc, line));
        Assert.Equal((7, 1, 0x41, "OK 12"), (got!.MessageID, got.Slave, got.Command, got.Message));
        Assert.Single(line.Sent);
    }

    [Fact]
    public void EmptyResponse_RaisesEmptyMessage()
    {
        var tc = new TextCommRTUMaster();
        string? got = null;
        tc.MessageReceived += (s, e) => got = e.Message;
        tc.ManualSend(1, 1, 0x41, "X");
        Assert.Equal(TextCommStatus.Ok, Exchange(tc, new Line().Reply(TextCommPacket.Build(1, 0x41, []))));
        Assert.Equal("", got);
    }

    [Fact]
    public void StaleAndOtherSlaveFrames_AreDiscarded_ThenMatch()
    {
        var tc = new TextCommRTUMaster();
        var got = new List<string>();
        int timeouts = 0;
        tc.MessageReceived += (s, e) => got.Add(e.Message);
        tc.TimeoutReceived += (s, e) => timeouts++;
        tc.ManualSend(1, 1, 0x41, "NOW");
        var stale = TextCommPacket.Build(1, 0x40, "OLD"u8);
        var other = TextCommPacket.Build(2, 0x41, "OTHER"u8);
        var match = TextCommPacket.Build(1, 0x41, "NEW"u8);
        Assert.Equal(TextCommStatus.Ok, Exchange(tc, new Line().Reply(stale, [.. other, .. match[..3]], match[3..])));
        Assert.Equal(new[] { "NEW" }, got);
        Assert.Equal(0, timeouts);
    }

    [Fact]
    public void OnlyStale_IsTimeout()
    {
        var tc = new TextCommRTUMaster();
        int timeouts = 0, received = 0;
        tc.TimeoutReceived += (s, e) => timeouts++;
        tc.MessageReceived += (s, e) => received++;
        tc.ManualSend(1, 1, 0x41, "NOW");
        Assert.Equal(TextCommStatus.Timeout, Exchange(tc, new Line().Reply(TextCommPacket.Build(1, 0x40, "OLD"u8))));
        Assert.Equal((1, 0), (timeouts, received));
    }

    [Fact]
    public void CheckSumError_RaisesEvent_AndRetries()
    {
        var tc = new TextCommRTUMaster();
        int sumErrors = 0, timeouts = 0;
        TextCommRTUMaster.ResultEventArgs? args = null;
        tc.CheckSumErrorReceived += (s, e) => { sumErrors++; args = e; };
        tc.TimeoutReceived += (s, e) => timeouts++;
        tc.ManualSend(3, 1, 0x41, "X", repeatCount: 1);
        byte[] bad = [0x02, 0x01, 0x41, 0x00, 0x03];
        var line = new Line().Reply(bad).Reply(bad);
        Assert.Equal(TextCommStatus.SumError, Exchange(tc, line));
        Assert.Equal((2, 0, 2), (sumErrors, timeouts, line.Sent.Count));
        Assert.Equal((3, 1, 0x41), (args!.MessageID, args.Slave, args.Command));
    }

    [Fact]
    public void CheckSumError_ThenGoodResponse_OnRetry()
    {
        var tc = new TextCommRTUMaster();
        int sumErrors = 0;
        string? got = null;
        tc.CheckSumErrorReceived += (s, e) => sumErrors++;
        tc.MessageReceived += (s, e) => got = e.Message;
        tc.ManualSend(1, 1, 0x41, "X", repeatCount: 1);
        var line = new Line().Reply([0x02, 0x01, 0x41, 0x00, 0x03]).Reply(TextCommPacket.Build(1, 0x41, "GOOD"u8));
        Assert.Equal(TextCommStatus.Ok, Exchange(tc, line));
        Assert.Equal((1, "GOOD", 2), (sumErrors, got, line.Sent.Count));
    }

    [Fact]
    public void Silent_RaisesTimeout_RepeatCountPlusOne()
    {
        var tc = new TextCommRTUMaster();
        int timeouts = 0;
        tc.TimeoutReceived += (s, e) => timeouts++;
        tc.ManualSend(1, 1, 0x41, "X", repeatCount: 2);
        var line = new Line();
        Assert.Equal(TextCommStatus.Timeout, Exchange(tc, line));
        Assert.Equal((3, 3), (timeouts, line.Sent.Count));
    }

    [Fact]
    public void Auto_NoRetry()
    {
        var tc = new TextCommRTUMaster();
        int timeouts = 0;
        tc.TimeoutReceived += (s, e) => timeouts++;
        tc.AutoSend(1, 1, 0x41, "X");
        var line = new Line();
        Exchange(tc, line);
        Assert.Equal((1, 1), (timeouts, line.Sent.Count));
    }

    [Fact]
    public void ResponseLongerThanBufferSize_IsDropped()
    {
        var tc = new TextCommRTUMaster { BufferSize = 16 };
        int received = 0;
        tc.MessageReceived += (s, e) => received++;
        tc.ManualSend(1, 1, 0x41, "X");
        var big = TextCommPacket.Build(1, 0x41, new byte[20]);
        Assert.Equal(TextCommStatus.Timeout, Exchange(tc, new Line().Reply(big)));
        Assert.Equal(0, received);
        tc.ManualSend(2, 1, 0x41, "X");
        Assert.Equal(TextCommStatus.Ok, Exchange(tc, new Line().Reply(TextCommPacket.Build(1, 0x41, new byte[11]))));
    }

    [Fact]
    public void Encoding_CapturedAtRegistration()
    {
        var tc = new TextCommRTUMaster { MessageEncoding = Encoding.UTF8 };
        tc.ManualSend(1, 1, 0x41, "온도");
        tc.MessageEncoding = Encoding.ASCII;
        string? got = null;
        tc.MessageReceived += (s, e) => got = e.Message;
        var w = tc.TakeNextWork()!;
        Assert.Equal(TextCommPacket.Build(1, 0x41, Encoding.UTF8.GetBytes("온도")), w.Request);
        var line = new Line().Reply(TextCommPacket.Build(1, 0x41, Encoding.UTF8.GetBytes("25도")));
        Assert.Equal(TextCommStatus.Ok, tc.Process(w, line.Send, line.Read));
        Assert.Equal("25도", got);   // 응답도 등록할 때의 인코딩(UTF-8)으로 읽는다
    }

    [Fact]
    public void HandlerThrows_StillOk()
    {
        var tc = new TextCommRTUMaster();
        bool called = false;
        tc.MessageReceived += (s, e) => { called = true; throw new InvalidOperationException(); };
        tc.ManualSend(1, 1, 0x41, "X");
        Assert.Equal(TextCommStatus.Ok, Exchange(tc, new Line().Reply(TextCommPacket.Build(1, 0x41, []))));
        Assert.True(called);
    }

    /// DecoderFallbackException이 아닌 일반 ArgumentException을 던지는 디코더 폴백
    sealed class ThrowingFallback : DecoderFallback
    {
        public override int MaxCharCount => 1;
        public override DecoderFallbackBuffer CreateFallbackBuffer() => new Buffer();
        sealed class Buffer : DecoderFallbackBuffer
        {
            public override int Remaining => 0;
            public override bool Fallback(byte[] bytesUnknown, int index) => throw new ArgumentException("custom fallback");
            public override bool MovePrevious() => false;
            public override char GetNextChar() => ' ';
        }
    }

    [Fact]
    public void DecodeFailureOtherThanDecoderFallbackException_IsTimeout_NotThrown()
    {
        var tc = new TextCommRTUMaster { MessageEncoding = Encoding.GetEncoding("us-ascii", EncoderFallback.ReplacementFallback, new ThrowingFallback()) };
        int received = 0;
        tc.MessageReceived += (s, e) => received++;
        tc.ManualSend(1, 1, 0x41, "X");
        var bad = TextCommPacket.Build(1, 0x41, [0x80]);
        Assert.Equal(TextCommStatus.Timeout, Exchange(tc, new Line().Reply(bad)));
        Assert.Equal(0, received);
        tc.ManualSend(2, 1, 0x41, "X");
        Assert.Equal(TextCommStatus.Ok, Exchange(tc, new Line().Reply(TextCommPacket.Build(1, 0x41, []))));
    }

    [Fact]
    public void RemoveAndClear()
    {
        var tc = new TextCommRTUMaster();
        tc.AutoSend(1, 1, 1, "A");
        tc.AutoSend(2, 1, 1, "B");
        tc.ManualSend(3, 1, 1, "C");
        Assert.True(tc.ContainAutoID(2));
        Assert.True(tc.RemoveAuto(2));
        Assert.False(tc.ContainAutoID(2));
        Assert.True(tc.RemoveManual(3));
        Assert.False(tc.RemoveManual(3));
        Assert.Equal(1, tc.TakeNextWork()!.MessageID);
        tc.ClearAuto();
        tc.ClearWorkSchedule();
        Assert.Null(tc.TakeNextWork());
    }

    [Fact]
    public async Task Start_InvalidPort_IsStartBecomesFalse()
    {
        using var tc = new TextCommRTUMaster { Port = "COM_NOT_EXIST" };
        tc.Start();
        await TestUtil.Until(() => !tc.IsStart);
        Assert.False(tc.IsOpen);
    }

    [Fact]
    public void Stop_ClearsIsStartImmediately_AndStartRestarts()
    {
        using var tc = new TextCommRTUMaster { Port = "COM_NOT_EXIST", AutoReconnect = true };
        tc.Start();
        Assert.True(tc.IsStart);
        tc.Stop();
        Assert.False(tc.IsStart);
        tc.Start();
        Assert.True(tc.IsStart);
        tc.Stop();
    }

    [Fact]
    public void Dispose_ThenStart_Throws()
    {
        var tc = new TextCommRTUMaster { Port = "COM_NOT_EXIST" };
        tc.Dispose();
        Assert.True(tc.IsDisposed);
        Assert.Throws<ObjectDisposedException>(tc.Start);
    }

    [Fact]
    public void UndecodableResponse_IsTreatedAsInvalid_AndWorkerStaysUsable()
    {
        var strict = Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var tc = new TextCommRTUMaster { MessageEncoding = strict };
        int received = 0, timeouts = 0;
        string? got = null;
        tc.MessageReceived += (s, e) => { received++; got = e.Message; };
        tc.TimeoutReceived += (s, e) => timeouts++;
        tc.ManualSend(1, 1, 0x41, "X");
        tc.ManualSend(2, 1, 0x41, "Y");
        Assert.Equal(TextCommStatus.Timeout, Exchange(tc, new Line().Reply(TextCommPacket.Build(1, 0x41, [0x80, 0x41]))));
        Assert.Equal((0, 1), (received, timeouts));
        Assert.Equal(TextCommStatus.Ok, Exchange(tc, new Line().Reply(TextCommPacket.Build(1, 0x41, "OK"u8))));
        Assert.Equal((1, "OK"), (received, got));
    }

    [Fact]
    public void UndecodableResponse_ThenGoodFrame_StillMatches()
    {
        var strict = Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var tc = new TextCommRTUMaster { MessageEncoding = strict };
        var got = new List<string>();
        tc.MessageReceived += (s, e) => got.Add(e.Message);
        tc.ManualSend(1, 1, 0x41, "X");
        Assert.Equal(TextCommStatus.Ok, Exchange(tc, new Line().Reply(TextCommPacket.Build(1, 0x41, [0x80]), TextCommPacket.Build(1, 0x41, "GOOD"u8))));
        Assert.Equal(new[] { "GOOD" }, got);
    }

    [Fact]
    public void ManualSend_UnencodableMessage_ThrowsToCaller()
    {
        var strict = Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var tc = new TextCommRTUMaster { MessageEncoding = strict };
        Assert.ThrowsAny<ArgumentException>(() => tc.ManualSend(1, 1, 1, "온도"));
        Assert.Null(tc.TakeNextWork());
    }
}
