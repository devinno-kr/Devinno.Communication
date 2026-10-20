using System.Collections.Concurrent;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Hsms;

namespace Devinno.Communication.Tests.Secs.Hsms;

/// 우리 SecsHsms끼리 127.0.0.1 루프백으로 Host ↔ Equipment (Active/Passive 두 조합)
public class SecsHsmsTests
{
    internal static SecsHsms Passive(SecsRole role, int deviceId = 1) => new()
    {
        Role = role, Mode = HsmsMode.Passive, LocalIP = "127.0.0.1", LocalPort = 0, DeviceID = deviceId,
        T3 = 3000, T5 = 200, T6 = 1000, T7 = 2000, T8 = 1000, LinktestInterval = 0,
    };

    internal static SecsHsms Active(SecsRole role, int port, int deviceId = 1) => new()
    {
        Role = role, Mode = HsmsMode.Active, RemoteIP = "127.0.0.1", RemotePort = port, DeviceID = deviceId,
        T3 = 3000, T5 = 200, T6 = 1000, T7 = 2000, T8 = 1000, LinktestInterval = 0,
    };

    /// 1차 메시지에 같은 아이템으로 응답(Function + 1)
    internal static void Echo(object? sender, SecsPrimaryEventArgs e)
    {
        if (e.Message.ReplyExpected) e.Reply(new SecsMessage(e.Message.Stream, e.Message.Function + 1, false, e.Message.Item));
    }

    /// Equipment(Passive) + Host(Active) 한 쌍을 Selected까지
    internal static async Task<(SecsHsms Eq, SecsHsms Host)> Pair(Action<SecsHsms>? eqSetup = null, Action<SecsHsms>? hostSetup = null)
    {
        var eq = Passive(SecsRole.Equipment);
        eqSetup?.Invoke(eq);
        eq.Start();
        var host = Active(SecsRole.Host, eq.BoundPort);
        hostSetup?.Invoke(host);
        host.Start();
        await TestUtil.Until(() => eq.IsOpen && host.IsOpen, 5000);
        return (eq, host);
    }

    [Fact]
    public async Task ActiveHost_PassiveEquipment_SendAndSendAsync()
    {
        var (eq, host) = await Pair(e => e.PrimaryReceived += Echo);
        using (eq) using (host)
        {
            var item = SecsItem.L(SecsItem.A("MDLN"), SecsItem.U4(1, 2));
            var r1 = host.Send(new SecsMessage(1, 13, true, item));
            Assert.Equal((1, 14, false), (r1!.Stream, r1.Function, r1.ReplyExpected));
            Assert.Equal(item, r1.Item);
            var r2 = await host.SendAsync(new SecsMessage(1, 1, true));
            Assert.Equal(2, r2!.Function);
            Assert.Null(r2.Item);
            Assert.NotEqual(r1.SystemBytes, r2.SystemBytes);
            Assert.Null(host.Send(new SecsMessage(10, 1, false, SecsItem.A("no reply"))));
            Assert.Equal((HsmsState.Selected, HsmsState.Selected), (eq.State, host.State));
        }
    }

    [Fact]
    public async Task PassiveHost_ActiveEquipment_EquipmentSendsEventReport()
    {
        using var host = Passive(SecsRole.Host);
        var got = new BlockingCollection<SecsMessage>();
        host.PrimaryReceived += (s, e) =>
        {
            got.Add(e.Message);
            e.Reply(new SecsMessage(6, 12, false, SecsItem.B(0)));
        };
        host.Start();
        using var eq = Active(SecsRole.Equipment, host.BoundPort);
        eq.Start();
        await TestUtil.Until(() => eq.IsOpen && host.IsOpen, 5000);

        var report = SecsItem.L(SecsItem.U4(1), SecsItem.U4(100), SecsItem.L());
        var ack = await eq.SendAsync(new SecsMessage(6, 11, true, report));
        Assert.Equal(SecsItem.B(0), ack!.Item);
        Assert.True(got.TryTake(out var m, 3000));
        Assert.Equal(report, m!.Item);
        Assert.Equal(1, m.DeviceID);
    }

    [Fact]
    public async Task HandlerDoesNotReply_OrThrows_HostGetsAbort()
    {
        var (eq, host) = await Pair(e => e.PrimaryReceived += (s, a) => { if (a.Message.Stream == 2) throw new InvalidOperationException("핸들러 오류"); });
        using (eq) using (host)
        {
            var ex = await Assert.ThrowsAsync<SecsAbortException>(() => host.SendAsync(new SecsMessage(7, 1, true)));
            Assert.Equal((7, 0), (ex.Reply.Stream, ex.Reply.Function));
            await Assert.ThrowsAsync<SecsAbortException>(() => host.SendAsync(new SecsMessage(2, 41, true)));
        }
    }

    [Fact]
    public async Task HandlerCanSend_WithoutDeadlock()
    {
        SecsHsms? eqRef = null;
        var (eq, host) = await Pair(
            e => e.PrimaryReceived += (s, a) =>
            {
                if (a.Message.Stream != 1 || a.Message.Function != 13) return;
                var r = eqRef!.Send(new SecsMessage(1, 1, true));      // 핸들러 안에서 상대에게 Send
                a.Reply(new SecsMessage(1, 14, false, r!.Item));
            },
            h => h.PrimaryReceived += (s, a) => a.Reply(new SecsMessage(1, 2, false, SecsItem.L(SecsItem.A("HOST"), SecsItem.A("9")))));
        eqRef = eq;
        using (eq) using (host)
        {
            var reply = await host.SendAsync(new SecsMessage(1, 13, true, SecsItem.L())).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(SecsItem.L(SecsItem.A("HOST"), SecsItem.A("9")), reply!.Item);
        }
    }

    [Fact]
    public async Task ConcurrentSends_FromManyThreads_AllMatched()
    {
        var (eq, host) = await Pair(e => e.PrimaryReceived += Echo);
        using (eq) using (host)
        {
            var tasks = Enumerable.Range(0, 8).Select(t => Task.Run(() =>
            {
                for (int i = 0; i < 20; i++)
                {
                    var item = SecsItem.U4((uint)(t * 1000 + i));
                    var r = host.Send(new SecsMessage(2, 13, true, item));
                    Assert.Equal(item, r!.Item);
                }
            })).ToArray();
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(20));
        }
    }

    [Fact]
    public async Task T3_Timeout_KeepsLink_LateReplyReported()
    {
        var errors = new ConcurrentQueue<string>();
        var (eq, host) = await Pair(
            e => e.PrimaryReceived += (s, a) => { if (a.Message.Stream == 5) Thread.Sleep(700); Echo(s, a); },
            h => { h.T3 = 300; h.ProtocolError += (s, a) => errors.Enqueue(a.Reason); });
        using (eq) using (host)
        {
            var ex = await Assert.ThrowsAsync<SecsTimeoutException>(() => host.SendAsync(new SecsMessage(5, 5, true)));
            Assert.Equal((5, 5), (ex.Primary.Stream, ex.Primary.Function));
            Assert.True(host.IsOpen);
            await TestUtil.Until(() => errors.Any(r => r.Contains("짝이 없는 응답")), 3000);
            Assert.NotNull(await host.SendAsync(new SecsMessage(1, 1, true)));   // 연결은 그대로
        }
    }

    [Fact]
    public async Task EquipmentT3_SendsS9F9_HostReceivesItAsPrimary()
    {
        var hostGot = new BlockingCollection<SecsMessage>();
        var (eq, host) = await Pair(
            e => e.T3 = 300,
            h => h.PrimaryReceived += (s, a) => { if (a.Message.Stream == 6) Thread.Sleep(800); else hostGot.Add(a.Message); });
        using (eq) using (host)
        {
            await Assert.ThrowsAsync<SecsTimeoutException>(() => eq.SendAsync(new SecsMessage(6, 11, true, SecsItem.L())));
            Assert.True(hostGot.TryTake(out var s9, 5000));
            Assert.Equal((9, 9, false), (s9!.Stream, s9.Function, s9.ReplyExpected));
            var shead = s9.Item!.GetBytes();
            Assert.Equal(10, shead.Length);
            Assert.Equal((0x86, 11), (shead[2], (int)shead[3]));   // W + S6, F11
        }
    }

    [Fact]
    public async Task DeviceIdMismatch_EquipmentAnswersS9F1_HostSendFails()
    {
        var eqEvents = 0;
        var (eq, host) = await Pair(e => e.PrimaryReceived += (s, a) => Interlocked.Increment(ref eqEvents), h => h.DeviceID = 2);
        using (eq) using (host)
        {
            var ex = await Assert.ThrowsAsync<SecsErrorException>(() => host.SendAsync(new SecsMessage(1, 1, true)));
            Assert.Equal(1, ex.Function);
            Assert.Equal(0, eqEvents);
        }
    }

    [Fact]
    public async Task Send_Checks()
    {
        using var host = Active(SecsRole.Host, 1);
        Assert.Throws<InvalidOperationException>(() => host.Send(new SecsMessage(1, 1, true)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.SendAsync(new SecsMessage(1, 1, true)));
        var (eq, h2) = await Pair();
        using (eq) using (h2)
        {
            Assert.Throws<ArgumentException>(() => h2.Send(new SecsMessage(1, 2, false)));
            Assert.Throws<ArgumentNullException>(() => h2.Send(null!));
        }
    }

    [Fact]
    public async Task PeerStopsWhileWaiting_IsDisconnectedException()
    {
        var entered = new ManualResetEventSlim();
        var (eq, host) = await Pair(e => e.PrimaryReceived += (s, a) => { entered.Set(); Thread.Sleep(1500); });
        using (eq) using (host)
        {
            var task = host.SendAsync(new SecsMessage(1, 3, true));
            Assert.True(entered.Wait(3000));
            eq.Stop();
            await Assert.ThrowsAsync<SecsDisconnectedException>(() => task);
        }
    }

    [Fact]
    public async Task HostStop_SendsSeparate_EquipmentClosesAndAcceptsAgain()
    {
        var eqOpened = 0;
        var eqClosed = 0;
        var (eq, host) = await Pair(e =>
        {
            e.DeviceOpened += (s, a) => Interlocked.Increment(ref eqOpened);
            e.DeviceClosed += (s, a) => Interlocked.Increment(ref eqClosed);
            e.PrimaryReceived += Echo;
        });
        using (eq)
        {
            host.Stop();
            Assert.False(host.IsStart);
            Assert.Equal(HsmsState.NotConnected, host.State);
            await TestUtil.Until(() => eqClosed == 1 && eq.State == HsmsState.NotConnected, 3000);
            host.Start();
            await TestUtil.Until(() => eqOpened == 2 && host.IsOpen, 5000);
            Assert.NotNull(await host.SendAsync(new SecsMessage(1, 1, true)));
            host.Dispose();
        }
    }

    [Fact]
    public async Task Active_ReconnectsAfterT5_WhenPassiveRestarts()
    {
        var hostOpened = 0;
        var (eq, host) = await Pair(e => e.PrimaryReceived += Echo, h => h.DeviceOpened += (s, a) => Interlocked.Increment(ref hostOpened));
        using (host)
        {
            int port = eq.BoundPort;
            eq.Dispose();
            await TestUtil.Until(() => host.State != HsmsState.Selected, 3000);
            using var eq2 = Passive(SecsRole.Equipment);
            eq2.LocalPort = port;
            eq2.PrimaryReceived += Echo;
            eq2.Start();
            await TestUtil.Until(() => hostOpened == 2 && host.IsOpen, 5000);
            Assert.NotNull(await host.SendAsync(new SecsMessage(1, 1, true)));
        }
    }

    [Fact]
    public async Task Active_AutoReconnectFalse_EndsAfterFailedConnect()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        int closedPort = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var host = Active(SecsRole.Host, closedPort);
        host.AutoReconnect = false;
        host.Start();
        await TestUtil.Until(() => !host.IsStart, 3000);
        Assert.Equal(HsmsState.NotConnected, host.State);
    }

    [Fact]
    public async Task Linktest_BothSidesAnswer_LinkStays()
    {
        var errors = new ConcurrentQueue<string>();
        var (eq, host) = await Pair(
            e => { e.LinktestInterval = 100; e.ProtocolError += (s, a) => errors.Enqueue(a.Reason); },
            h => { h.LinktestInterval = 100; h.ProtocolError += (s, a) => errors.Enqueue(a.Reason); });
        using (eq) using (host)
        {
            await Task.Delay(1000);
            Assert.True(eq.IsOpen && host.IsOpen);
            Assert.Empty(errors);
        }
    }

    [Fact]
    public async Task LargeMessage_BothWays()
    {
        var (eq, host) = await Pair(e => { e.PrimaryReceived += Echo; e.MaxMessageSize = 4_000_000; }, h => h.MaxMessageSize = 4_000_000);
        using (eq) using (host)
        {
            var big = SecsItem.L(SecsItem.B(new byte[1_000_000]), SecsItem.U4(Enumerable.Range(0, 100_000).Select(i => (uint)i).ToArray()));
            var r = await host.SendAsync(new SecsMessage(7, 3, true, big));
            Assert.Equal(big, r!.Item);
        }
    }

    [Fact]
    public async Task SynchronousSend_InsideDeviceOpened_Works()
    {
        var eq = Passive(SecsRole.Equipment);
        eq.PrimaryReceived += Echo;
        eq.Start();
        using (eq)
        using (var host = Active(SecsRole.Host, eq.BoundPort))
        {
            var reply = new TaskCompletionSource<SecsMessage?>();
            host.DeviceOpened += (s, a) =>
            {
                try { reply.TrySetResult(host.Send(new SecsMessage(1, 1, true))); }
                catch (Exception ex) { reply.TrySetException(ex); }
            };
            host.Start();
            var r = await reply.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal((1, 2), (r!.Stream, r.Function));
        }
    }

    [Fact]
    public async Task Events_AndStates()
    {
        var log = new ConcurrentQueue<string>();
        var (eq, host) = await Pair(null, h =>
        {
            h.DeviceOpened += (s, a) => log.Enqueue("opened");
            h.DeviceClosed += (s, a) => log.Enqueue("closed");
        });
        using (host)
        {
            Assert.True(host.IsStart);
            eq.Dispose();
            await TestUtil.Until(() => log.Contains("closed"), 3000);
            Assert.Equal(new[] { "opened", "closed" }, log.ToArray());
        }
    }

    [Fact]
    public void Dispose_Twice_StartAfterDispose_Throws()
    {
        var x = Passive(SecsRole.Equipment);
        x.Start();
        x.Dispose();
        x.Dispose();
        Assert.True(x.IsDisposed);
        Assert.False(x.IsStart);
        Assert.Throws<ObjectDisposedException>(() => x.Start());
    }

    [Fact]
    public void Properties_RangeChecks()
    {
        var x = new SecsHsms();
        Assert.Equal((SecsRole.Host, HsmsMode.Active, 0, 45000, 10000, 5000, 10000, 5000, 60000, 16_777_216, true),
            (x.Role, x.Mode, x.DeviceID, x.T3, x.T5, x.T6, x.T7, x.T8, x.LinktestInterval, x.MaxMessageSize, x.AutoReconnect));
        Assert.Equal(("127.0.0.1", 5000, "0.0.0.0", 5000), (x.RemoteIP, x.RemotePort, x.LocalIP, x.LocalPort));
        Assert.Equal("DeviceID", Assert.Throws<ArgumentOutOfRangeException>(() => x.DeviceID = 32768).ParamName);
        Assert.Equal("T3", Assert.Throws<ArgumentOutOfRangeException>(() => x.T3 = 0).ParamName);
        Assert.Equal("LinktestInterval", Assert.Throws<ArgumentOutOfRangeException>(() => x.LinktestInterval = -1).ParamName);
        Assert.Equal("MaxMessageSize", Assert.Throws<ArgumentOutOfRangeException>(() => x.MaxMessageSize = 1023).ParamName);
        x.T6 = 1;   // 코드에서는 짧은 값도 된다(XML은 1000 이상)
        Assert.Equal(1, x.T6);
        Assert.Equal("RemotePort", Assert.Throws<ArgumentOutOfRangeException>(() => x.RemotePort = 0).ParamName);
        Assert.Equal("RemotePort", Assert.Throws<ArgumentOutOfRangeException>(() => x.RemotePort = 65536).ParamName);
        Assert.Equal("LocalPort", Assert.Throws<ArgumentOutOfRangeException>(() => x.LocalPort = -1).ParamName);
        Assert.Equal("LocalPort", Assert.Throws<ArgumentOutOfRangeException>(() => x.LocalPort = 65536).ParamName);
        x.RemotePort = 65535;
        x.LocalPort = 0;
        Assert.Equal((65535, 0), (x.RemotePort, x.LocalPort));
    }

    [Fact]
    public async Task Passive_AcceptError_ReportsAndKeepsAccepting()
    {
        var errors = new ConcurrentQueue<string>();
        int calls = 0;
        using var eq = Passive(SecsRole.Equipment);
        eq.PrimaryReceived += Echo;
        eq.ProtocolError += (s, a) => errors.Enqueue(a.Reason);
        // 첫 접속 대기는 연결 리셋(WSAECONNRESET)처럼 실패시킨다
        eq.AcceptHook = l => Interlocked.Increment(ref calls) == 1 ? throw new System.Net.Sockets.SocketException(10054) : l.AcceptSocket();
        eq.Start();
        await TestUtil.Until(() => errors.Any(e => e.Contains("접속을 받지 못했습니다")), 3000);
        Assert.True(eq.IsStart);

        using var host = Active(SecsRole.Host, eq.BoundPort);
        host.Start();
        await TestUtil.Until(() => eq.IsOpen && host.IsOpen, 5000);
        Assert.NotNull(await host.SendAsync(new SecsMessage(1, 1, true)));
        Assert.True(calls >= 2);
    }

    [Fact]
    public async Task Passive_AcceptLoopEndsUnexpectedly_TearsDownAndReports()
    {
        var errors = new ConcurrentQueue<string>();
        var fail = false;
        var (eq, host) = await Pair(e =>
        {
            e.ProtocolError += (s, a) => errors.Enqueue(a.Reason);
            e.AcceptHook = l => Volatile.Read(ref fail) ? throw new InvalidOperationException("시험용 접속 대기 오류") : l.AcceptSocket();
        });
        using (eq) using (host)
        {
            int port = eq.BoundPort;
            Volatile.Write(ref fail, true);
            // 접속 대기 스레드는 이미 AcceptSocket에서 기다리므로, 연결 하나를 더 받게 해 다음 호출에서 오류를 낸다
            using (var extra = new System.Net.Sockets.TcpClient()) extra.Connect(System.Net.IPAddress.Loopback, port);
            await TestUtil.Until(() => !eq.IsStart, 3000);
            await TestUtil.Until(() => errors.Any(e => e.Contains("시험용 접속 대기 오류")), 3000);
            await TestUtil.Until(() => eq.State == HsmsState.NotConnected && !host.IsOpen, 3000);   // 지금 연결도 닫았다

            // 리스너도 닫혔으므로 같은 포트로 다시 시작할 수 있다
            Volatile.Write(ref fail, false);
            eq.LocalPort = port;
            eq.PrimaryReceived += Echo;
            eq.Start();
            await TestUtil.Until(() => eq.IsOpen && host.IsOpen, 5000);
            Assert.NotNull(await host.SendAsync(new SecsMessage(1, 1, true)));
        }
    }

    [Fact]
    public void Passive_BadLocalIP_StartThrows()
    {
        using var x = new SecsHsms { Mode = HsmsMode.Passive, LocalIP = "not-ip", LocalPort = 0 };
        Assert.Throws<FormatException>(() => x.Start());
        Assert.False(x.IsStart);
    }
}
