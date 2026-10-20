using System.Buffers.Binary;
using System.Collections.Concurrent;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Secs.Hsms;
using Devinno.Communication.Secs.Serial;

namespace Devinno.Communication.Tests.Secs.Gem;

/// GemEquipment 골격: 만들기·수명, 설정 잠금, 지원하지 않는 메시지의 S9F3/S9F5(받은 헤더 그대로), Host S9 무시, 이벤트 스레드
public class GemEquipmentCoreTests
{
    [Fact]
    public void Constructor_SetsRoleEquipment_RejectsStartedTransport()
    {
        var t = GemKit.Transport();
        Assert.Equal(SecsRole.Host, t.Role);
        using var eq = new GemEquipment(t);
        Assert.Equal(SecsRole.Equipment, t.Role);
        Assert.Same(t, eq.Transport);
        var serial = new SecsSerial();
        using (var eq2 = new GemEquipment(serial)) Assert.Equal(SecsRole.Equipment, serial.Role);

        using var started = GemKit.Transport();
        started.Start();
        Assert.Throws<InvalidOperationException>(() => new GemEquipment(started));
        Assert.Throws<ArgumentNullException>(() => new GemEquipment((SecsSerial)null!));
    }

    [Fact]
    public void Start_InvalidDefinitions_Throws_TransportNotStarted()
    {
        using var eq = GemKit.Equipment(e => e.AddCollectionEvent(new GemCollectionEvent(5001, "E", [7777])));
        Assert.Throws<InvalidOperationException>(eq.Start);
        Assert.False(eq.IsStart);
        Assert.False(((SecsHsms)eq.Transport).IsStart);
    }

    [Fact]
    public async Task StartStopDispose_Lifecycle()
    {
        var eq = GemKit.Equipment(GemKit.Sample);
        eq.Start();
        eq.Start();                                                   // 두 번째는 아무것도 하지 않음
        Assert.True(eq.IsStart);
        Assert.True(((SecsHsms)eq.Transport).IsStart);
        using (var host = new Fakes.FakeGemHost(GemKit.Port(eq)))
        {
            host.Start();
            await TestUtil.Until(() => eq.IsOpen, 5000);
        }
        eq.Stop();
        eq.Stop();
        Assert.False(eq.IsStart);
        Assert.False(((SecsHsms)eq.Transport).IsStart);
        eq.Start();                                                   // 다시 시작
        Assert.True(eq.IsStart);
        eq.Dispose();
        eq.Dispose();
        Assert.True(eq.IsDisposed);
        Assert.False(eq.IsStart);
        Assert.Throws<ObjectDisposedException>(eq.Start);
        Assert.True(((SecsHsms)eq.Transport).IsDisposed);
    }

    [Fact]
    public async Task Stop_DropsQueuedMessages_Silently()
    {
        using var eq = GemKit.Equipment(e =>
        {
            GemKit.Sample(e);
            e.InitialControlState = GemControlState.OnlineRemote;
            e.EventsEnabledByDefault = true;
        });
        var log = GemKit.Log(eq);
        using var host = await GemKit.Communicating(eq, h => h.Hold(6, 11));          // Host가 S6F11에 답하지 않음
        for (int i = 0; i < 5; i++) Assert.True(eq.TriggerEvent(5002));
        host.Next(6, 11);                                                                  // 첫 번째는 응답 대기 중, 나머지는 대기열
        eq.Stop();
        await Task.Delay(500);
        Assert.DoesNotContain(log, l => l.StartsWith("SendFailed", StringComparison.Ordinal));
    }

    [Fact]
    public void Settings_AndDefinitions_LockedWhileRunning()
    {
        using var eq = GemKit.Equipment();
        Assert.Throws<ArgumentException>(() => eq.VidFormat = SecsFormat.F4);
        Assert.Throws<ArgumentException>(() => eq.ModelName = new string('M', 21));
        Assert.Throws<ArgumentException>(() => eq.SoftwareRevision = "버전");
        Assert.Throws<ArgumentOutOfRangeException>(() => eq.MaxPendingSends = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => eq.CommandReplyTimeout = 0);
        eq.VidFormat = SecsFormat.ASCII;
        eq.Start();
        Assert.Throws<InvalidOperationException>(() => eq.VidFormat = SecsFormat.U4);
        Assert.Throws<InvalidOperationException>(() => eq.ModelName = "X");
        Assert.Throws<InvalidOperationException>(() => eq.AddStatusVariable(new GemStatusVariable(1, "X", SecsFormat.U1)));
        Assert.Throws<InvalidOperationException>(eq.ClearDefinitions);
        Assert.Throws<InvalidOperationException>(() => eq.RegisterMessage(7, 1));
        eq.Stop();
        eq.AddStatusVariable(new GemStatusVariable(1, "X", SecsFormat.U1));
        Assert.Single(eq.StatusVariables);
        eq.ClearDefinitions();
        Assert.Empty(eq.StatusVariables);
    }

    [Fact]
    public void DefinitionLists_AreCopies()
    {
        using var eq = GemKit.Equipment(GemKit.Sample);
        eq.RegisterMessage(7, 1);
        Assert.Equal(2, eq.StatusVariables.Count);
        Assert.Single(eq.DataVariables);
        Assert.Single(eq.EquipmentConstants);
        Assert.Equal(2, eq.CollectionEvents.Count);
        Assert.Single(eq.Alarms);
        Assert.Equal(2, eq.RemoteCommands.Count);
        Assert.Equal([(7, 1)], eq.RegisteredMessages);
    }

    [Fact]
    public async Task UnknownStream_S9F3_WithExactReceivedHeader()
    {
        using var eq = GemKit.Equipment();
        var log = GemKit.Log(eq);
        using var host = await GemKit.Connect(eq);
        var ex = await Assert.ThrowsAsync<SecsErrorException>(() => host.SendAsync("S7F1 W <L <A \"PP1\"> <U4 10>>"));
        Assert.Equal(3, ex.Function);
        var mhead = ex.ErrorMessage.Item!.GetBytes();
        Assert.Equal(10, mhead.Length);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(mhead));                    // 장치 ID
        Assert.Equal(0x80 | 7, mhead[2]);                                                // W + Stream
        Assert.Equal(1, mhead[3]);
        Assert.Equal((byte)0, mhead[4]);                                                 // PType
        Assert.Equal((byte)0, mhead[5]);                                                 // SType(데이터)
        Assert.Equal(ex.Primary.SystemBytes, BinaryPrimitives.ReadUInt32BigEndian(mhead.AsSpan(6)));
        Assert.False(ex.ErrorMessage.ReplyExpected);
        await TestUtil.Until(() => log.Any(l => l.StartsWith("Error S9F3", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task UnknownFunction_InSupportedStream_S9F5_NoWBitToo()
    {
        using var eq = GemKit.Equipment();
        using var host = await GemKit.Connect(eq);
        var ex = await Assert.ThrowsAsync<SecsErrorException>(() => host.SendAsync("S1F65 W"));
        Assert.Equal(5, ex.Function);
        await host.SendAsync("S2F99");                                                  // W 없음: S9F5는 짝이 없어 Host의 1차 메시지로 옴
        var s9 = host.NextOf(9, 5);
        var mhead = s9.Item!.GetBytes();
        Assert.Equal((2, 99), (mhead[2], (int)mhead[3]));
    }

    [Fact]
    public async Task HostS9_IsIgnored_NoReply_OnlyProtocolError()
    {
        using var eq = GemKit.Equipment();
        var log = GemKit.Log(eq);
        using var host = await GemKit.Connect(eq);
        await host.SendAsync("S9F1 <B 0x00 0x01 0x81 0x01 0x00 0x00 0x00 0x00 0x00 0x05>");
        await TestUtil.Until(() => log.Any(l => l.StartsWith("Error Host가 S9F1", StringComparison.Ordinal)));
        host.NoneOf(9);                                                                 // S9로 되받지 않음(응답 고리 방지)
    }

    [Fact]
    public async Task Events_OnOneAppThread_InOrder_HandlerExceptionsSwallowed()
    {
        using var eq = GemKit.Equipment();
        var threads = new ConcurrentQueue<string?>();
        eq.ProtocolError += (s, e) => { threads.Enqueue(Thread.CurrentThread.Name); throw new InvalidOperationException("앱 오류"); };   // 던져도 다음 이벤트는 계속 발생
        using var host = await GemKit.Connect(eq);
        await Assert.ThrowsAsync<SecsErrorException>(() => host.SendAsync("S7F1 W"));
        await Assert.ThrowsAsync<SecsErrorException>(() => host.SendAsync("S8F1 W"));
        await TestUtil.Until(() => threads.Count >= 2);
        Assert.All(threads, n => Assert.Equal("GemEquipment.Events", n));
    }
}
