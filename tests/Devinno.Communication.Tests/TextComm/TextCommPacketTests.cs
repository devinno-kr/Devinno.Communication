using System.Text;
using Devinno.Communication.TextComm;

namespace Devinno.Communication.Tests.TextComm;

public class TextCommPacketTests
{
    // 기대 바이트는 프레임 규칙을 그대로 옮긴 별도 스크립트로 계산한 기준값이다.
    public static TheoryData<byte, byte, byte[], byte[]> Golden => new()
    {
        // 일반: 1, 0x41, "READ"
        { 0x01, 0x41, "READ"u8.ToArray(), new byte[] { 0x02, 0x01, 0x41, 0x52, 0x45, 0x41, 0x44, 0x5E, 0x03 } },
        // 명령 0x03 이스케이프: 1, 3, "READ"
        { 0x01, 0x03, "READ"u8.ToArray(), new byte[] { 0x02, 0x01, 0x10, 0x13, 0x52, 0x45, 0x41, 0x44, 0x20, 0x03 } },
        // 빈 메시지
        { 0x01, 0x41, Array.Empty<byte>(), new byte[] { 0x02, 0x01, 0x41, 0x42, 0x03 } },
        // slave 0x02 이스케이프
        { 0x02, 0x41, "A"u8.ToArray(), new byte[] { 0x02, 0x10, 0x12, 0x41, 0x41, 0x84, 0x03 } },
        // slave 0x10 이스케이프
        { 0x10, 0x41, "A"u8.ToArray(), new byte[] { 0x02, 0x10, 0x20, 0x41, 0x41, 0x92, 0x03 } },
        // 메시지 안의 0x02, 0x03, 0x10
        { 0x01, 0x41, new byte[] { 0x41, 0x02, 0x03, 0x10, 0x42 }, new byte[] { 0x02, 0x01, 0x41, 0x41, 0x10, 0x12, 0x10, 0x13, 0x10, 0x20, 0x42, 0xDA, 0x03 } },
        // 합계 바이트 0x10, 0x03, 0x02 이스케이프
        { 0x01, 0x0F, Array.Empty<byte>(), new byte[] { 0x02, 0x01, 0x0F, 0x10, 0x20, 0x03 } },
        { 0xFF, 0x04, Array.Empty<byte>(), new byte[] { 0x02, 0xFF, 0x04, 0x10, 0x13, 0x03 } },
        { 0x80, 0x82, Array.Empty<byte>(), new byte[] { 0x02, 0x80, 0x82, 0x10, 0x12, 0x03 } },
        // UTF-8 다바이트 메시지 "한글"
        { 0x01, 0x41, Encoding.UTF8.GetBytes("한글"), new byte[] { 0x02, 0x01, 0x41, 0xED, 0x95, 0x9C, 0xEA, 0xB8, 0x80, 0x82, 0x03 } },
    };

    [Theory]
    [MemberData(nameof(Golden))]
    public void Build_MatchesGoingMakePacket(byte slave, byte command, byte[] message, byte[] expected)
    {
        Assert.Equal(expected, TextCommPacket.Build(slave, command, message));
    }

    [Theory]
    [MemberData(nameof(Golden))]
    public void Find_DecodesGoldenFrames(byte slave, byte command, byte[] message, byte[] frame)
    {
        var f = TextCommPacket.Find(frame, 1024);
        Assert.Equal((TextCommStatus.Ok, frame.Length, slave, command), (f.Status, f.Consumed, f.Slave, f.Command));
        Assert.Equal(message, f.Message);
    }

    [Fact]
    public void Find_GarbageBeforeStx_IsSkipped()
    {
        byte[] frame = [0x02, 0x01, 0x41, 0x42, 0x03];
        var f = TextCommPacket.Find([0x55, 0xAA, .. frame], 1024);
        Assert.Equal((TextCommStatus.Ok, 7), (f.Status, f.Consumed));
    }

    [Fact]
    public void Find_NoStx_DropsEverything()
    {
        var f = TextCommPacket.Find([0x41, 0x42, 0x03], 1024);
        Assert.Equal((TextCommStatus.Incomplete, 3), (f.Status, f.Consumed));
    }

    [Fact]
    public void Find_MissingEtx_IsIncomplete_KeepsFromStx()
    {
        var f = TextCommPacket.Find([0x55, 0x02, 0x01, 0x41], 1024);
        Assert.Equal((TextCommStatus.Incomplete, 1), (f.Status, f.Consumed));
    }

    [Fact]
    public void Find_DleAtEnd_WaitsForNextByte()
    {
        var f = TextCommPacket.Find([0x02, 0x01, 0x10], 1024);
        Assert.Equal((TextCommStatus.Incomplete, 0), (f.Status, f.Consumed));
    }

    [Fact]
    public void Find_StxInsideFrame_Resyncs()
    {
        // 앞 프레임이 ETX 없이 끊기고 새 프레임이 시작된 경우(새 STX에서 다시 시작)
        var f = TextCommPacket.Find([0x02, 0x01, 0x41, 0x02, 0x01, 0x41, 0x42, 0x03], 1024);
        Assert.Equal((TextCommStatus.Ok, 8, (byte)1, (byte)0x41), (f.Status, f.Consumed, f.Slave, f.Command));
        Assert.Empty(f.Message);
    }

    [Theory]
    [InlineData(new byte[] { 0x02, 0x01, 0x41, 0x10, 0x41, 0x42, 0x03 })]   // DLE 다음 0x41
    [InlineData(new byte[] { 0x02, 0x01, 0x41, 0x10, 0x10, 0x20, 0x03 })]   // DLE 다음 DLE
    [InlineData(new byte[] { 0x02, 0x01, 0x41, 0x10, 0x03 })]               // DLE 다음 ETX
    public void Find_BadDlePair_IsInvalid(byte[] frame)
    {
        var f = TextCommPacket.Find(frame, 1024);
        Assert.Equal((TextCommStatus.Invalid, frame.Length), (f.Status, f.Consumed));
    }

    [Fact]
    public void Find_BadDleThenStx_ResyncsToNextFrame()
    {
        var f = TextCommPacket.Find([0x02, 0x01, 0x10, 0x02, 0x01, 0x41, 0x42, 0x03], 1024);
        Assert.Equal((TextCommStatus.Ok, 8), (f.Status, f.Consumed));
    }

    [Theory]
    [InlineData(new byte[] { 0x02, 0x03 })]
    [InlineData(new byte[] { 0x02, 0x01, 0x03 })]
    [InlineData(new byte[] { 0x02, 0x01, 0x41, 0x03 })]
    public void Find_ShortPayload_IsInvalid(byte[] frame)
    {
        var f = TextCommPacket.Find(frame, 1024);
        Assert.Equal((TextCommStatus.Invalid, frame.Length), (f.Status, f.Consumed));
    }

    [Fact]
    public void Find_SumMismatch_IsSumError_NotInvalid()
    {
        var f = TextCommPacket.Find([0x02, 0x01, 0x41, 0x52, 0x45, 0x41, 0x44, 0x5F, 0x03], 1024);
        Assert.Equal((TextCommStatus.SumError, 9, (byte)1, (byte)0x41), (f.Status, f.Consumed, f.Slave, f.Command));
        Assert.Empty(f.Message);
    }

    [Fact]
    public void Find_LongerThanMax_IsInvalid()
    {
        var frame = TextCommPacket.Build(1, 0x41, new byte[10]);   // 15바이트
        Assert.Equal(TextCommStatus.Ok, TextCommPacket.Find(frame, 15).Status);
        var f = TextCommPacket.Find(frame, 14);
        Assert.Equal((TextCommStatus.Invalid, 14), (f.Status, f.Consumed));
        var unterminated = TextCommPacket.Find(frame.AsSpan(0, 14), 14);
        Assert.Equal((TextCommStatus.Incomplete, 0), (unterminated.Status, unterminated.Consumed));
    }

    [Fact]
    public void Extract_TwoFramesAndPartial_LeavesPartial()
    {
        var a = TextCommPacket.Build(1, 0x41, "A"u8);
        var b = TextCommPacket.Build(2, 0x42, "B"u8);
        var c = TextCommPacket.Build(3, 0x43, "C"u8);
        var pending = new List<byte>([.. a, 0x55, .. b, .. c[..3]]);
        var frames = TextCommPacket.Extract(pending, 1024);
        Assert.Equal(new byte[] { 1, 2 }, frames.Select(f => f.Slave));
        Assert.Equal(c[..3], pending);
        pending.AddRange(c[3..]);
        Assert.Equal((byte)3, TextCommPacket.Extract(pending, 1024).Single().Slave);
        Assert.Empty(pending);
    }

    static Func<int, byte[]?> Script(params byte[][] chunks)
    {
        var q = new Queue<byte[]>(chunks);
        return _ => q.Count > 0 ? q.Dequeue() : null;
    }

    [Fact]
    public void Receive_SkipsStaleAndOtherSlave_UntilMatch()
    {
        var stale = TextCommPacket.Build(1, 0x40, "OLD"u8);
        var other = TextCommPacket.Build(2, 0x41, "X"u8);
        var match = TextCommPacket.Build(1, 0x41, "NEW"u8);
        var f = TextCommPacket.Receive(Script(stale, [.. other, .. match[..4]], match[4..]), 1, 0x41, 1000, 1024);
        Assert.Equal(TextCommStatus.Ok, f.Status);
        Assert.Equal("NEW"u8.ToArray(), f.Message);
    }

    [Fact]
    public void Receive_OnlyStale_TimesOut()
    {
        var f = TextCommPacket.Receive(Script(TextCommPacket.Build(1, 0x40, "OLD"u8)), 1, 0x41, 1000, 1024);
        Assert.Equal(TextCommStatus.Timeout, f.Status);
    }

    [Fact]
    public void Receive_SumError_ReturnsAtOnce()
    {
        byte[] bad = [0x02, 0x01, 0x41, 0x00, 0x03];
        var f = TextCommPacket.Receive(Script(bad, TextCommPacket.Build(1, 0x41, ""u8)), 1, 0x41, 1000, 1024);
        Assert.Equal(TextCommStatus.SumError, f.Status);
    }

    [Fact]
    public void Receive_StopsAtDeadline_WhileDataKeepsComing()
    {
        int calls = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var f = TextCommPacket.Receive(_ => { calls++; Thread.Sleep(5); return [0x55]; }, 1, 0x41, 100, 1024);
        Assert.Equal(TextCommStatus.Timeout, f.Status);
        Assert.InRange(sw.ElapsedMilliseconds, 90, 1000);
        Assert.True(calls > 1);
    }

    [Theory]
    [InlineData(-1, 0, "slave")]
    [InlineData(256, 0, "slave")]
    [InlineData(0, -1, "command")]
    [InlineData(0, 256, "command")]
    public void BuildRequest_OutOfRange_Throws(int slave, int command, string param)
        => Assert.Equal(param, Assert.Throws<ArgumentOutOfRangeException>(() => TextCommPacket.BuildRequest(Encoding.ASCII, slave, command, "A")).ParamName);

    [Fact]
    public void BuildRequest_NullMessage_Throws()
        => Assert.Equal("message", Assert.Throws<ArgumentNullException>(() => TextCommPacket.BuildRequest(Encoding.ASCII, 1, 1, null!)).ParamName);

    [Theory]
    [InlineData(0, 0, "")]
    [InlineData(1, 0x41, "READ")]
    [InlineData(255, 255, "A")]
    public void BuildRequest_Valid_EqualsBuild(int slave, int command, string message)
        => Assert.Equal(TextCommPacket.Build((byte)slave, (byte)command, Encoding.ASCII.GetBytes(message)), TextCommPacket.BuildRequest(Encoding.ASCII, slave, command, message));

    [Fact]
    public void BuildResponse_Null_NoReply()
        => Assert.Null(TextCommPacket.BuildResponse(Encoding.ASCII, 1, 0x41, null));

    [Fact]
    public void BuildResponse_Empty_FrameWithoutMessage()
        => Assert.Equal(new byte[] { 0x02, 0x01, 0x41, 0x42, 0x03 }, TextCommPacket.BuildResponse(Encoding.ASCII, 1, 0x41, ""));

    [Fact]
    public void BuildResponse_Message_EqualsBuild()
        => Assert.Equal(TextCommPacket.Build(1, 0x41, "OK"u8), TextCommPacket.BuildResponse(Encoding.ASCII, 1, 0x41, "OK"));

    [Fact]
    public void BuildResponse_EncodingFailure_Throws()
    {
        var strict = Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        Assert.ThrowsAny<ArgumentException>(() => TextCommPacket.BuildResponse(strict, 1, 0x41, "온도"));
    }
}
