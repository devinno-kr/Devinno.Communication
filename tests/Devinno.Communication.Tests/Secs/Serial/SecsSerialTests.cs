using System.Collections.Concurrent;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Serial;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Secs.Serial;

/// 우리 SecsSerial끼리 메모리 회선 쌍으로 Host ↔ Equipment
public class SecsSerialTests
{
    internal static SecsSerial Make(SecsRole role, MemoryLine end, int deviceId = 1) => new()
    {
        Role = role, DeviceID = deviceId, T1 = 500, T2 = 1000, T3 = 3000, T4 = 2000, RetryLimit = 3,
        OpenLine = end.ToLine,
    };

    internal static void Echo(object? sender, SecsPrimaryEventArgs e)
    {
        if (e.Message.ReplyExpected) e.Reply(new SecsMessage(e.Message.Stream, e.Message.Function + 1, false, e.Message.Item));
    }

    static async Task<(SecsSerial Eq, SecsSerial Host, MemoryLine EqEnd, MemoryLine HostEnd)> Pair(Action<SecsSerial>? eqSetup = null, Action<SecsSerial>? hostSetup = null)
    {
        var (a, b) = MemoryLine.Pair();
        var eq = Make(SecsRole.Equipment, a);
        var host = Make(SecsRole.Host, b);
        eqSetup?.Invoke(eq);
        hostSetup?.Invoke(host);
        eq.Start();
        host.Start();
        await TestUtil.Until(() => eq.IsOpen && host.IsOpen);
        return (eq, host, a, b);
    }

    [Fact]
    public async Task Host_SendAndSendAsync_EquipmentReplies()
    {
        var (eq, host, _, _) = await Pair(e => e.PrimaryReceived += Echo);
        using (eq) using (host)
        {
            var item = SecsItem.L(SecsItem.A("MDLN"), SecsItem.A("1.0"));
            var r = host.Send(new SecsMessage(1, 13, true, item));
            Assert.Equal((1, 14, false, 1), (r!.Stream, r.Function, r.ReplyExpected, r.DeviceID));
            Assert.Equal(item, r.Item);
            Assert.Equal(2, (await host.SendAsync(new SecsMessage(1, 1, true)))!.Function);
            Assert.Null(host.Send(new SecsMessage(10, 3, false, SecsItem.A("terminal"))));
        }
    }

    [Fact]
    public async Task Equipment_SendsEventReport_HostReplies()
    {
        var hostGot = new BlockingCollection<SecsMessage>();
        var (eq, host, _, _) = await Pair(null, h => h.PrimaryReceived += (s, e) => { hostGot.Add(e.Message); e.Reply(new SecsMessage(6, 12, false, SecsItem.B(0))); });
        using (eq) using (host)
        {
            var ack = await eq.SendAsync(new SecsMessage(6, 11, true, SecsItem.L(SecsItem.U4(1), SecsItem.U4(100), SecsItem.L())));
            Assert.Equal(SecsItem.B(0), ack!.Item);
            Assert.True(hostGot.TryTake(out var m, 3000));
            Assert.Equal((6, 11), (m!.Stream, m.Function));
        }
    }

    [Theory]
    [InlineData(244, 1)]
    [InlineData(245, 2)]
    [InlineData(3000, 13)]
    public async Task MultiBlock_SplitAndReassemble_BothWays(int bodyLength, int blocks)
    {
        var (eq, host, eqEnd, hostEnd) = await Pair(e => e.PrimaryReceived += Echo);
        using (eq) using (host)
        {
            var item = SecsItem.B(Enumerable.Range(0, bodyLength - (bodyLength - 2 > 255 ? 3 : 2)).Select(i => (byte)i).ToArray());
            Assert.Equal(bodyLength, SecsCodec.Encode(item).Length);
            int hostBefore = hostEnd.BlocksWritten, eqBefore = eqEnd.BlocksWritten;
            var r = await host.SendAsync(new SecsMessage(7, 3, true, item));
            Assert.Equal(item, r!.Item);
            Assert.Equal(blocks, hostEnd.BlocksWritten - hostBefore);
            Assert.Equal(blocks, eqEnd.BlocksWritten - eqBefore);
        }
    }

    [Fact]
    public async Task HandlerDoesNotReply_SxF0()
    {
        var (eq, host, _, _) = await Pair();
        using (eq) using (host)
        {
            var ex = await Assert.ThrowsAsync<SecsAbortException>(() => host.SendAsync(new SecsMessage(2, 41, true)));
            Assert.Equal((2, 0), (ex.Reply.Stream, ex.Reply.Function));
        }
    }

    [Fact]
    public async Task DeviceIdMismatch_S9F1_HostSendFails()
    {
        var (eq, host, _, _) = await Pair(e => e.PrimaryReceived += Echo, h => h.DeviceID = 5);
        using (eq) using (host)
        {
            var ex = await Assert.ThrowsAsync<SecsErrorException>(() => host.SendAsync(new SecsMessage(1, 1, true)));
            Assert.Equal(1, ex.Function);
        }
    }

    [Fact]
    public async Task T3_Timeout_LinkKept()
    {
        var (eq, host, _, _) = await Pair(e => e.PrimaryReceived += (s, a) => { if (a.Message.Stream == 5) Thread.Sleep(600); Echo(s, a); }, h => h.T3 = 300);
        using (eq) using (host)
        {
            await Assert.ThrowsAsync<SecsTimeoutException>(() => host.SendAsync(new SecsMessage(5, 5, true)));
            Assert.True(host.IsOpen);
            await Task.Delay(500);
            Assert.NotNull(await host.SendAsync(new SecsMessage(1, 1, true)));
        }
    }

    [Fact]
    public async Task CorruptedBlockOnce_NakThenRetrySucceeds()
    {
        var eqErrors = new ConcurrentQueue<string>();
        var hostErrors = new ConcurrentQueue<string>();
        var (eq, host, _, hostEnd) = await Pair(
            e => { e.PrimaryReceived += Echo; e.ProtocolError += (s, a) => eqErrors.Enqueue(a.Reason); },
            h => h.ProtocolError += (s, a) => hostErrors.Enqueue(a.Reason));
        using (eq) using (host)
        {
            int corrupt = 1;
            hostEnd.Tap = bytes =>
            {
                if (bytes.Length > 1 && Interlocked.Exchange(ref corrupt, 0) == 1)
                {
                    var c = (byte[])bytes.Clone();
                    c[^1] ^= 0xFF;   // 체크섬 깨뜨림
                    return c;
                }
                return bytes;
            };
            var r = await host.SendAsync(new SecsMessage(1, 1, true));
            Assert.NotNull(r);
            Assert.Contains(eqErrors, e => e.Contains("체크섬"));
            Assert.Contains(hostErrors, e => e.Contains("0x15"));
        }
    }

    [Fact]
    public async Task ConcurrentSends_SentOneAtATime_AllMatched()
    {
        var (eq, host, _, _) = await Pair(e => e.PrimaryReceived += Echo);
        using (eq) using (host)
        {
            var tasks = Enumerable.Range(0, 4).Select(t => Task.Run(() =>
            {
                for (int i = 0; i < 5; i++)
                {
                    var item = SecsItem.B(new byte[300 + t * 10 + i]);   // 다중 블록
                    Assert.Equal(item, host.Send(new SecsMessage(7, 3, true, item))!.Item);
                }
            })).ToArray();
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public async Task BothSendAtOnce_ContentionResolved()
    {
        var (eq, host, _, _) = await Pair(e => e.PrimaryReceived += Echo, h => h.PrimaryReceived += Echo);
        using (eq) using (host)
        {
            for (int i = 0; i < 10; i++)
            {
                var a = Task.Run(() => eq.Send(new SecsMessage(6, 11, true, SecsItem.U4((uint)i))));
                var b = Task.Run(() => host.Send(new SecsMessage(2, 17, true, SecsItem.U4((uint)i))));
                var (ra, rb) = (await a.WaitAsync(TimeSpan.FromSeconds(10)), await b.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal(SecsItem.U4((uint)i), ra!.Item);
                Assert.Equal(SecsItem.U4((uint)i), rb!.Item);
            }
        }
    }

    [Fact]
    public async Task Stop_FailsWaitingSend_SendWhenClosedThrows()
    {
        var entered = new ManualResetEventSlim();
        var (eq, host, _, _) = await Pair(e => e.PrimaryReceived += (s, a) => { entered.Set(); Thread.Sleep(1000); });
        using (eq)
        {
            var task = host.SendAsync(new SecsMessage(1, 3, true));
            Assert.True(entered.Wait(3000));
            host.Stop();
            await Assert.ThrowsAsync<SecsDisconnectedException>(() => task);
            Assert.False(host.IsOpen);
            Assert.Throws<InvalidOperationException>(() => host.Send(new SecsMessage(1, 1, true)));
            host.Dispose();
        }
    }

    [Fact]
    public async Task OpenFails_AutoReconnectFalse_Ends_EventsPaired()
    {
        using var x = new SecsSerial { OpenLine = () => throw new IOException("포트 없음") };
        x.Start();
        await TestUtil.Until(() => !x.IsStart);

        var (a, _) = MemoryLine.Pair();
        var log = new ConcurrentQueue<string>();
        using var y = Make(SecsRole.Host, a);
        y.DeviceOpened += (s, e) => log.Enqueue("opened");
        y.DeviceClosed += (s, e) => log.Enqueue("closed");
        y.Start();
        await TestUtil.Until(() => y.IsOpen && log.Count == 1);   // DeviceOpened는 디스패처 스레드에서 오므로 도착을 기다린다
        y.Stop();
        Assert.Equal(new[] { "opened", "closed" }, log.ToArray());
    }

    [Fact]
    public async Task Host_SyncSendInsideDeviceOpened_GetsReply()
    {
        var (a, b) = MemoryLine.Pair();
        using var eq = Make(SecsRole.Equipment, a);
        eq.PrimaryReceived += Echo;
        using var host = Make(SecsRole.Host, b);
        var got = new TaskCompletionSource<SecsMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.DeviceOpened += (s, e) =>
        {
            try { got.TrySetResult(host.Send(new SecsMessage(1, 1, true))); }
            catch (Exception ex) { got.TrySetException(ex); }
        };
        eq.Start();
        host.Start();
        var r = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((1, 2), (r!.Stream, r.Function));
    }

    [Fact]
    public void Properties_DefaultsAndRanges()
    {
        var x = new SecsSerial();
        Assert.Equal((SecsRole.Host, 0, 45000, "COM1", 9600, System.IO.Ports.Parity.None, 8, System.IO.Ports.StopBits.One, 500, 10000, 45000, 3, (bool?)null, true, false),
            (x.Role, x.DeviceID, x.T3, x.Port, x.Baudrate, x.Parity, x.DataBits, x.StopBits, x.T1, x.T2, x.T4, x.RetryLimit, x.IsMaster, x.DuplicateDetection, x.AutoReconnect));
        Assert.Equal("RetryLimit", Assert.Throws<ArgumentOutOfRangeException>(() => x.RetryLimit = 32).ParamName);
        Assert.Equal("T1", Assert.Throws<ArgumentOutOfRangeException>(() => x.T1 = 0).ParamName);
        Assert.Equal("DeviceID", Assert.Throws<ArgumentOutOfRangeException>(() => x.DeviceID = -1).ParamName);
    }
}
