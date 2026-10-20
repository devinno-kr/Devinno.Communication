using System.Net.Sockets;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Hsms;
using Devinno.Communication.Secs.Serial;
using Devinno.Communication.Tests.Fakes;
using Devinno.Communication.Tests.Secs.Hsms;

namespace Devinno.Communication.Tests.Secs;

/// public Stop()이 스레드를 기다리는 사이 다른 스레드가 Start()를 부른 경우: Stop의 늦은 정리가 새 실행을 멈추면 안 된다.
/// 테스트 이음새(HSMS AcceptHook, SECS-I OpenLine)로 지난 실행의 스레드를 붙잡아 경합을 매번 같은 순서로 만든다.
public class SecsStopStartRaceTests
{
    [Fact]
    public async Task Hsms_StartDuringStop_NewRunKeepsWorking()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int calls = 0;
        using var eq = SecsHsmsTests.Passive(SecsRole.Equipment);
        eq.AcceptHook = l =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.Set();
                release.Wait(10000);                      // 지난 실행의 접속 대기 스레드를 붙잡는다(Stop이 Join에서 기다림)
                throw new SocketException((int)SocketError.Interrupted);
            }
            return l.AcceptSocket();
        };
        eq.PrimaryReceived += SecsHsmsTests.Echo;
        eq.Start();
        Assert.True(entered.Wait(3000));
        var stop = Task.Factory.StartNew(eq.Stop, TaskCreationOptions.LongRunning);
        await TestUtil.Until(() => !eq.IsStart);
        eq.Start();                                       // Stop이 아직 끝나지 않은 채 새 실행
        release.Set();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(eq.IsStart);

        using var host = SecsHsmsTests.Active(SecsRole.Host, eq.BoundPort);
        host.Start();
        await TestUtil.Until(() => host.IsOpen && eq.IsOpen, 5000);
        var reply = await host.SendAsync(new SecsMessage(1, 1, true, SecsItem.L()));   // 새 실행의 디스패처가 살아 있어야 응답
        Assert.Equal(2, reply!.Function);
    }

    [Fact]
    public async Task Serial_StartDuringStop_NewRunKeepsWorking()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var (ours, peer) = MemoryLine.Pair();
        int calls = 0;
        using var eq = new SecsSerial { Role = SecsRole.Equipment, DeviceID = 1, T1 = 200, T2 = 1000, T3 = 3000, T4 = 2000 };
        eq.OpenLine = () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.Set();
                release.Wait(10000);                      // 지난 실행의 회선 스레드를 붙잡는다
                throw new IOException("열지 못함");
            }
            return ours.ToLine();
        };
        eq.PrimaryReceived += SecsHsmsTests.Echo;
        eq.Start();
        Assert.True(entered.Wait(3000));
        var stop = Task.Factory.StartNew(eq.Stop, TaskCreationOptions.LongRunning);
        await TestUtil.Until(() => !eq.IsStart);
        eq.Start();
        await TestUtil.Until(() => eq.IsOpen);
        release.Set();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(eq.IsStart);
        Assert.True(eq.IsOpen);

        // 상대 → 우리: 새 실행의 디스패처가 응답한다
        peer.SendMessage(new SecsMessage(1, 1, true, SecsItem.L()).WithHeader(1, 1), rbit: false);
        var (_, reply) = peer.ReceiveMessage();
        Assert.Equal((1, 2, 1u), (reply.Stream, reply.Function, reply.SystemBytes));
        // 우리 → 상대: 새 실행의 송신 대기열이 살아 있다
        var send = eq.SendAsync(new SecsMessage(6, 11, false, SecsItem.L()));
        var (_, s6f11) = peer.ReceiveMessage();
        Assert.Equal((6, 11), (s6f11.Stream, s6f11.Function));
        Assert.Null(await send.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Hsms_OldLinkCleanupAfterRestart_DoesNotTouchNewRun()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int calls = 0, closed = 0;
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var eq = SecsHsmsTests.Passive(SecsRole.Equipment);
        eq.LinkEndHook = () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.Set();
                release.Wait(10000);                      // 지난 실행의 연결 스레드를 정리 직전에 붙잡는다(Stop의 Join보다 오래 살아남음)
            }
        };
        eq.PrimaryReceived += SecsHsmsTests.Echo;
        eq.DeviceClosed += (s, e) => Interlocked.Increment(ref closed);
        eq.ProtocolError += (s, e) => errors.Enqueue(e.Reason);
        eq.Start();
        using (var first = SecsHsmsTests.Active(SecsRole.Host, eq.BoundPort))
        {
            first.Start();
            await TestUtil.Until(() => first.IsOpen && eq.IsOpen, 5000);
        }                                                 // 끊김 → 지난 연결 스레드가 훅에서 멈춤
        Assert.True(entered.Wait(3000));
        var stop = Task.Factory.StartNew(eq.Stop, TaskCreationOptions.LongRunning);
        await TestUtil.Until(() => !eq.IsStart);
        eq.Start();                                       // Stop이 지난 연결 스레드를 기다리는 중에 새 실행
        using var host = SecsHsmsTests.Active(SecsRole.Host, eq.BoundPort);
        host.PrimaryReceived += SecsHsmsTests.Echo;
        host.Start();
        await TestUtil.Until(() => host.IsOpen && eq.IsOpen, 5000);
        Volatile.Write(ref closed, 0);
        while (errors.TryDequeue(out _)) { }
        release.Set();                                    // 지난 연결의 늦은 정리
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(300);
        Assert.True(eq.IsStart);
        Assert.Equal(HsmsState.Selected, eq.State);
        Assert.True(eq.IsOpen && host.IsOpen);
        var reply = await host.SendAsync(new SecsMessage(1, 1, true, SecsItem.L()));
        Assert.Equal(2, reply!.Function);
        var back = await eq.SendAsync(new SecsMessage(1, 1, true, SecsItem.L()));        // 새 실행의 세션 송신도 살아 있다
        Assert.Equal(2, back!.Function);
        Assert.Equal(0, Volatile.Read(ref closed));
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Serial_OldRunOpensLineAfterRestart_DoesNotTouchNewRun()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var (ours, peer) = MemoryLine.Pair();
        var (stale, _) = MemoryLine.Pair();
        int calls = 0, opened = 0, closed = 0;
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var eq = new SecsSerial { Role = SecsRole.Equipment, DeviceID = 1, T1 = 200, T2 = 1000, T3 = 3000, T4 = 2000 };
        eq.OpenLine = () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.Set();
                release.Wait(10000);                      // 지난 실행의 회선 스레드를 붙잡았다가
                return stale.ToLine();                    // 새 실행이 시작된 뒤에 회선을 연 것처럼 돌려준다
            }
            return ours.ToLine();
        };
        eq.PrimaryReceived += SecsHsmsTests.Echo;
        eq.DeviceOpened += (s, e) => Interlocked.Increment(ref opened);
        eq.DeviceClosed += (s, e) => Interlocked.Increment(ref closed);
        eq.ProtocolError += (s, e) => errors.Enqueue(e.Reason);
        eq.Start();
        Assert.True(entered.Wait(3000));
        var stop = Task.Factory.StartNew(eq.Stop, TaskCreationOptions.LongRunning);
        await TestUtil.Until(() => !eq.IsStart);
        eq.Start();
        await TestUtil.Until(() => eq.IsOpen);
        await TestUtil.Until(() => Volatile.Read(ref opened) == 1);
        release.Set();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(300);
        Assert.True(eq.IsStart);
        Assert.True(eq.IsOpen);
        Assert.Equal((1, 0), (Volatile.Read(ref opened), Volatile.Read(ref closed)));
        Assert.Empty(errors);

        // 상대 → 우리: 새 실행의 디스패처가 응답한다
        peer.SendMessage(new SecsMessage(1, 1, true, SecsItem.L()).WithHeader(1, 1), rbit: false);
        var (_, reply) = peer.ReceiveMessage();
        Assert.Equal((1, 2, 1u), (reply.Stream, reply.Function, reply.SystemBytes));
        // 우리 → 상대: 새 실행의 송신 대기열·회선이 살아 있다
        var send = eq.SendAsync(new SecsMessage(6, 11, false, SecsItem.L()));
        var (_, s6f11) = peer.ReceiveMessage();
        Assert.Equal((6, 11), (s6f11.Stream, s6f11.Function));
        Assert.Null(await send.WaitAsync(TimeSpan.FromSeconds(3)));
    }
}
