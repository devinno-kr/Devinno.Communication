using System.Xml.Linq;
using Devinno.Communication.Modbus;
using Devinno.Communication.Modbus.RTU;

namespace Devinno.Communication.Tests.Modbus;

// Plan 13 쓰기 안전: 끊긴 동안 쓰기 버림, Stop이 쓰기 비움, 실패 이벤트의 시도 정보.
// 시리얼 포트는 테스트에서 열 수 없으므로 연결 상태는 internal seam(SetLinkUp/SetLinkDown)으로, 송수신은 Process로 시험한다.
public class ModbusRtuWriteSafetyTests
{
    static List<(int Id, WriteDiscardReason Reason)> Record(ModbusRTUMaster m)
    {
        var list = new List<(int, WriteDiscardReason)>();
        m.WriteDiscarded += (s, e) => { lock (list) list.Add((e.MessageID, e.Reason)); };
        return list;
    }

    static List<(int, WriteDiscardReason)> Snap(List<(int Id, WriteDiscardReason Reason)> list)
    {
        lock (list) return [.. list];
    }

    [Fact]
    public void Write_BeforeStart_DiscardedSync_NotSent()
    {
        var m = new ModbusRTUMaster();
        var ev = Record(m);
        m.ManualWordWrite_FC6(7, 1, 0, 5);
        Assert.Equal([(7, WriteDiscardReason.NotConnected)], Snap(ev));
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void Broadcast_BeforeStart_Discarded()
    {
        var m = new ModbusRTUMaster();
        var ev = Record(m);
        m.ManualWordWrite_FC6(7, 0, 0, 5);
        Assert.Equal([(7, WriteDiscardReason.NotConnected)], Snap(ev));
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void Read_BeforeStart_Kept()
    {
        var m = new ModbusRTUMaster();
        var ev = Record(m);
        m.ManualWordRead_FC3(8, 1, 0, 1);
        Assert.Empty(Snap(ev));
        Assert.Equal(8, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void LinkDown_DiscardsWritesKeepsReads_InOrder()
    {
        var m = new ModbusRTUMaster();
        var ev = Record(m);
        m.SetLinkUp();
        m.ManualWordWrite_FC6(1, 1, 0, 1);
        m.ManualWordRead_FC3(2, 1, 0, 1);
        m.ManualWordWrite_FC6(3, 1, 0, 1);
        Assert.Empty(Snap(ev));
        m.SetLinkDown(false);
        Assert.Equal([(1, WriteDiscardReason.Disconnected), (3, WriteDiscardReason.Disconnected)], Snap(ev));
        Assert.Equal(2, m.TakeNextWork()!.MessageID);
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void LinkDown_Stopping_ReasonStopped()
    {
        var m = new ModbusRTUMaster();
        var ev = Record(m);
        m.SetLinkUp();
        m.ManualWordWrite_FC6(1, 1, 0, 1);
        m.ManualWordRead_FC3(2, 1, 0, 1);
        m.ManualWordWrite_FC6(3, 1, 0, 1);
        m.SetLinkDown(true);
        Assert.Equal([(1, WriteDiscardReason.Stopped), (3, WriteDiscardReason.Stopped)], Snap(ev));
        Assert.Equal(2, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void LinkDown_DiscardFalse_KeepsWrites()
    {
        var m = new ModbusRTUMaster { DiscardWritesWhileDisconnected = false };
        var ev = Record(m);
        m.SetLinkUp();
        m.ManualWordWrite_FC6(1, 1, 0, 1);
        m.SetLinkDown(false);
        Assert.Empty(Snap(ev));
        Assert.Equal(1, m.TakeNextWork()!.MessageID);
    }

    [Fact]
    public void Stop_Unstarted_DiscardFalse_RaisesStopped()
    {
        var m = new ModbusRTUMaster { DiscardWritesWhileDisconnected = false };
        var ev = Record(m);
        m.ManualWordWrite_FC6(1, 1, 0, 1);
        m.ManualWordRead_FC3(2, 1, 0, 1);
        m.ManualWordWrite_FC6(3, 1, 0, 1);
        Assert.Empty(Snap(ev));
        m.Stop();
        Assert.Equal([(1, WriteDiscardReason.Stopped), (3, WriteDiscardReason.Stopped)], Snap(ev));
        Assert.Equal(2, m.TakeNextWork()!.MessageID);
        Assert.Null(m.TakeNextWork());
    }

    [Fact]
    public void LinkDown_FromOtherThread_DoesNotOverrideNewerLink()
    {
        var m = new ModbusRTUMaster();
        var ev = Record(m);
        // 다른 스레드(옛 루프)가 올린 연결을 이 스레드(새 루프)가 이어받은 뒤, 옛 루프의 SetLinkDown이 오는 상황
        var old = new Thread(() => m.SetLinkUp());
        old.Start(); old.Join();
        m.SetLinkUp();
        var late = new Thread(() => m.SetLinkDown(false));
        late.Start(); late.Join();
        m.ManualWordWrite_FC6(9, 1, 0, 1);
        Assert.Empty(Snap(ev));
        Assert.Equal(9, m.TakeNextWork()!.MessageID);
    }

    // send는 보낸 횟수를 세고, receive가 응답 프레임(없으면 null)을 돌려준다
    static int Run(ModbusRTUMaster m, ModbusRTUMaster.Work w, Func<byte[]?> receive,
        List<ModbusRTUMaster.AttemptFailedEventArgs> timeouts, List<ModbusRTUMaster.AttemptFailedEventArgs> crcs)
    {
        m.TimeoutReceived += (s, e) => timeouts.Add(e);
        m.CRCErrorReceived += (s, e) => crcs.Add(e);
        int sent = 0;
        m.Process(w, req => sent++, t => receive());
        return sent;
    }

    [Fact]
    public void Process_Timeouts_AttemptAndIsFinal()
    {
        var m = new ModbusRTUMaster { DiscardWritesWhileDisconnected = false };
        m.ManualWordWrite_FC6(5, 1, 0, 1, repeatCount: 2);
        var w = m.TakeNextWork()!;
        var timeouts = new List<ModbusRTUMaster.AttemptFailedEventArgs>();
        var crcs = new List<ModbusRTUMaster.AttemptFailedEventArgs>();
        int sent = Run(m, w, () => null, timeouts, crcs);
        Assert.Equal(3, sent);
        Assert.Equal([1, 2, 3], timeouts.Select(e => e.Attempt));
        Assert.Equal([false, false, true], timeouts.Select(e => e.IsFinal));
        Assert.All(timeouts, e => Assert.True(e.RequestSent));
        Assert.All(timeouts, e => Assert.Equal(5, e.MessageID));
        Assert.Empty(crcs);
    }

    [Fact]
    public void Process_LastAttemptCrcError_IsFinalCrcEvent()
    {
        var m = new ModbusRTUMaster { DiscardWritesWhileDisconnected = false };
        m.ManualWordWrite_FC6(5, 1, 0, 1, repeatCount: 1);
        var w = m.TakeNextWork()!;
        var timeouts = new List<ModbusRTUMaster.AttemptFailedEventArgs>();
        var crcs = new List<ModbusRTUMaster.AttemptFailedEventArgs>();
        int n = 0;
        var bad = ModbusRtuFrame.Build(1, new byte[] { 6, 0, 0, 0, 1 });
        bad[^1] ^= 0xFF;
        Run(m, w, () => n++ == 0 ? null : bad, timeouts, crcs);
        var t0 = Assert.Single(timeouts);
        Assert.Equal((1, false), (t0.Attempt, t0.IsFinal));
        var c = Assert.Single(crcs);
        Assert.Equal((2, true, true, 5), (c.Attempt, c.IsFinal, c.RequestSent, c.MessageID));
    }

    [Fact]
    public void Process_Success_NoFailureEvents()
    {
        var m = new ModbusRTUMaster { DiscardWritesWhileDisconnected = false };
        m.ManualWordWrite_FC6(5, 1, 0, 1);
        var w = m.TakeNextWork()!;
        var timeouts = new List<ModbusRTUMaster.AttemptFailedEventArgs>();
        var crcs = new List<ModbusRTUMaster.AttemptFailedEventArgs>();
        int ok = 0;
        m.WordWriteReceived += (s, e) => ok++;
        Run(m, w, () => ModbusRtuFrame.Build(1, new byte[] { 6, 0, 0, 0, 1 }), timeouts, crcs);
        Assert.Equal(1, ok);
        Assert.Empty(timeouts);
        Assert.Empty(crcs);
    }

    [Fact]
    public void Process_Broadcast_ReturnsAfterSend_NoEvents()
    {
        var m = new ModbusRTUMaster { DiscardWritesWhileDisconnected = false };
        m.ManualWordWrite_FC6(5, 0, 0, 1, repeatCount: 2);
        var w = m.TakeNextWork()!;
        var timeouts = new List<ModbusRTUMaster.AttemptFailedEventArgs>();
        var crcs = new List<ModbusRTUMaster.AttemptFailedEventArgs>();
        bool received = false;
        int sent = Run(m, w, () => { received = true; return null; }, timeouts, crcs);
        Assert.Equal(1, sent);
        Assert.False(received);
        Assert.Empty(timeouts);
        Assert.Empty(crcs);
    }

    [Fact]
    public void Process_SendThrows_FinalNotSent_Rethrown()
    {
        var m = new ModbusRTUMaster { DiscardWritesWhileDisconnected = false };
        m.ManualWordWrite_FC6(5, 1, 0, 1, repeatCount: 2);
        var w = m.TakeNextWork()!;
        var timeouts = new List<ModbusRTUMaster.AttemptFailedEventArgs>();
        m.TimeoutReceived += (s, e) => timeouts.Add(e);
        Assert.Throws<IOException>(() => m.Process(w, req => throw new IOException(), t => null));
        var e = Assert.Single(timeouts);
        Assert.Equal((1, true, false), (e.Attempt, e.IsFinal, e.RequestSent));
    }

    [Fact]
    public void Xml_RoundTrip_DiscardWritesWhileDisconnected()
    {
        var m = new ModbusRTUMaster();
        m.FromXml(XElement.Parse("""<ModbusRTUMaster DiscardWritesWhileDisconnected="false" />"""));
        Assert.False(m.DiscardWritesWhileDisconnected);
        Assert.Equal("false", m.ToXml().Attribute("DiscardWritesWhileDisconnected")?.Value);

        m.FromXml(XElement.Parse("<ModbusRTUMaster />"));
        Assert.True(m.DiscardWritesWhileDisconnected);
    }
}
