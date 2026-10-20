using System.Text;
using Devinno.Communication.TextComm;
using Devinno.Communication.TextComm.RTU;

namespace Devinno.Communication.Tests.TextComm.RTU;

public class TextCommRTUSlaveTests
{
    /// 받은 바이트를 프레임으로 해석해 슬레이브에 넘긴다
    static byte[]? Handle(TextCommRTUSlave slave, byte[] raw) => slave.HandleFrame(TextCommPacket.Find(raw, TextCommPacket.MaxRequestLength));

    [Fact]
    public void Defaults()
    {
        var slave = new TextCommRTUSlave();
        Assert.Equal(("COM1", 115200, System.IO.Ports.Parity.None, 8, System.IO.Ports.StopBits.One, false),
            (slave.Port, slave.Baudrate, slave.Parity, slave.DataBits, slave.StopBits, slave.AutoReconnect));
        Assert.Same(Encoding.ASCII, slave.MessageEncoding);
    }

    [Fact]
    public void TextResponse_EchoesSlaveAndCommand()
    {
        var slave = new TextCommRTUSlave();
        TextCommRTUSlave.MessageRequestEventArgs? got = null;
        slave.MessageRequest += (s, e) => { got = e; e.ResponseMessage = "OK " + e.RequestMessage; };
        var res = Handle(slave, TextCommPacket.Build(0x10, 0x03, "READ"u8));
        Assert.Equal((0x10, 0x03, "READ"), (got!.Slave, got.Command, got.RequestMessage));
        Assert.Equal(TextCommPacket.Build(0x10, 0x03, "OK READ"u8), res);
    }

    [Fact]
    public void NullResponse_NoReply()
    {
        var slave = new TextCommRTUSlave();
        int calls = 0;
        slave.MessageRequest += (s, e) => calls++;
        Assert.Null(Handle(slave, TextCommPacket.Build(1, 0x41, "A"u8)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void EmptyResponse_RepliesFrameWithoutMessage()
    {
        var slave = new TextCommRTUSlave();
        slave.MessageRequest += (s, e) => e.ResponseMessage = "";
        var res = Handle(slave, TextCommPacket.Build(1, 0x41, "A"u8));
        Assert.Equal(new byte[] { 0x02, 0x01, 0x41, 0x42, 0x03 }, res);   // 기준 바이트 (1, 0x41, "")
    }

    [Fact]
    public void NoHandler_NoReply()
    {
        Assert.Null(Handle(new TextCommRTUSlave(), TextCommPacket.Build(1, 0x41, "A"u8)));
    }

    [Fact]
    public void HandlerThrows_NoReply()
    {
        var slave = new TextCommRTUSlave();
        slave.MessageRequest += (s, e) => { e.ResponseMessage = "X"; throw new InvalidOperationException(); };
        Assert.Null(Handle(slave, TextCommPacket.Build(1, 0x41, "A"u8)));
    }

    [Theory]
    [InlineData(new byte[] { 0x02, 0x01, 0x41, 0x41, 0x00, 0x03 })]   // 합계 오류
    [InlineData(new byte[] { 0x02, 0x01, 0x41, 0x10, 0x41, 0x83, 0x03 })]   // 잘못된 DLE 조합
    [InlineData(new byte[] { 0x02, 0x01, 0x03 })]   // 너무 짧음
    public void BrokenFrame_NoEvent_NoReply(byte[] raw)
    {
        var slave = new TextCommRTUSlave();
        int calls = 0;
        slave.MessageRequest += (s, e) => { calls++; e.ResponseMessage = "X"; };
        Assert.Null(Handle(slave, raw));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void AnySlaveAddress_RaisesEvent()
    {
        var slave = new TextCommRTUSlave();
        var seen = new List<int>();
        slave.MessageRequest += (s, e) => { seen.Add(e.Slave); if (e.Slave == 7) e.ResponseMessage = "ME"; };
        Assert.Null(Handle(slave, TextCommPacket.Build(0, 0x41, ""u8)));
        Assert.NotNull(Handle(slave, TextCommPacket.Build(7, 0x41, ""u8)));
        Assert.Null(Handle(slave, TextCommPacket.Build(255, 0x41, ""u8)));
        Assert.Equal(new[] { 0, 7, 255 }, seen);
    }

    [Fact]
    public void Utf8_RequestAndResponse()
    {
        var slave = new TextCommRTUSlave { MessageEncoding = Encoding.UTF8 };
        slave.MessageRequest += (s, e) => e.ResponseMessage = e.RequestMessage + "!";
        var res = Handle(slave, TextCommPacket.Build(1, 0x41, Encoding.UTF8.GetBytes("온도")));
        Assert.Equal(TextCommPacket.Build(1, 0x41, Encoding.UTF8.GetBytes("온도!")), res);
    }

    [Fact]
    public async Task Start_InvalidPort_IsStartBecomesFalse()
    {
        using var slave = new TextCommRTUSlave { Port = "COM_NOT_EXIST" };
        slave.Start();
        await TestUtil.Until(() => !slave.IsStart);
        Assert.False(slave.IsOpen);
    }

    [Fact]
    public void Stop_ClearsIsStartImmediately_AndStartRestarts()
    {
        using var slave = new TextCommRTUSlave { Port = "COM_NOT_EXIST", AutoReconnect = true };
        slave.Start();
        Assert.True(slave.IsStart);
        slave.Stop();
        Assert.False(slave.IsStart);
        slave.Start();
        Assert.True(slave.IsStart);
        slave.Stop();
    }

    static readonly Encoding StrictAscii = Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    [Fact]
    public void ResponseEncodingThrows_NoReply_NextRequestStillHandled()
    {
        var slave = new TextCommRTUSlave { MessageEncoding = StrictAscii };
        slave.MessageRequest += (s, e) => e.ResponseMessage = e.RequestMessage == "BAD" ? "온도" : "OK";
        Assert.Null(Handle(slave, TextCommPacket.Build(1, 0x41, "BAD"u8)));
        Assert.Equal(TextCommPacket.Build(1, 0x41, "OK"u8), Handle(slave, TextCommPacket.Build(1, 0x41, "GOOD"u8)));
    }

    [Fact]
    public void RequestDecodeThrows_NoEvent_NoReply_NextRequestStillHandled()
    {
        var slave = new TextCommRTUSlave { MessageEncoding = StrictAscii };
        int calls = 0;
        slave.MessageRequest += (s, e) => { calls++; e.ResponseMessage = "OK"; };
        Assert.Null(Handle(slave, TextCommPacket.Build(1, 0x41, new byte[] { 0xC0, 0xC1 })));
        Assert.Equal(0, calls);
        Assert.NotNull(Handle(slave, TextCommPacket.Build(1, 0x41, "A"u8)));
        Assert.Equal(1, calls);
    }
}
