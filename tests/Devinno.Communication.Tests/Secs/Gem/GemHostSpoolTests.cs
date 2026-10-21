using System.Collections.Concurrent;
using System.Net.Sockets;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Secs.Hsms;

namespace Devinno.Communication.Tests.Secs.Gem;

/// GemHost 스풀 명령(Plan 14 Task 6): SetSpoolStreams(S2F43)·TransmitSpool/PurgeSpool(S6F23) ↔ 우리 GemEquipment 루프백
[Collection(nameof(GemHostCollection))]
public class GemHostSpoolTests
{
    static string TempDir() => Path.Combine(Path.GetTempPath(), "devinno-spool-" + Guid.NewGuid());

    static void Delete(string dir)
    {
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task SetSpoolStreams_Accepted_And_UnsupportedStream()
    {
        var dir = TempDir();
        try
        {
            var (eq, host) = await GemHostKit.Loopback(e => { e.SpoolDirectory = dir; e.InitialControlState = GemControlState.OnlineRemote; });
            using (eq) using (host)
            {
                var ok = host.SetSpoolStreams([(6, [])]);
                Assert.Equal(0, ok.Rspack);
                Assert.Empty(ok.Errors);

                var bad = await host.SetSpoolStreamsAsync([(1, [])]);
                Assert.Equal(1, bad.Rspack);
                var err = Assert.Single(bad.Errors);
                Assert.Equal((1, 1), (err.Stream, err.Strack));
                Assert.Empty(err.Functions);
            }
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task PurgeSpool_WhileInactive_NoData()
    {
        var dir = TempDir();
        try
        {
            var (eq, host) = await GemHostKit.Loopback(e => { e.SpoolDirectory = dir; e.InitialControlState = GemControlState.OnlineRemote; });
            using (eq) using (host)
            {
                Assert.Equal(GemAck.RsdaNoData, host.PurgeSpool());
                Assert.Equal(GemAck.RsdaNoData, await host.PurgeSpoolAsync());
            }
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task TransmitSpool_AfterReconnect_ReceivesSpooledEventsThenDeactivated()
    {
        var dir = TempDir();
        try
        {
            var sockets = new ConcurrentQueue<Socket>();
            var (eq, host) = await GemHostKit.Loopback(e =>
            {
                e.SpoolDirectory = dir;
                e.InitialControlState = GemControlState.OnlineRemote;
                e.EventsEnabledByDefault = true;
                ((SecsHsms)e.Transport).AcceptHook = l => { var s = l.AcceptSocket(); sockets.Enqueue(s); return s; };
            });
            using (eq) using (host)
            {
                var ceids = new ConcurrentQueue<long>();
                host.EventReceived += (s, e) => ceids.Enqueue(e.Report.Ceid.Number);
                Assert.Equal(0, host.SetSpoolStreams([(6, [])]).Rspack);

                // 연결을 끊으면 장비는 NOT COMMUNICATING으로 가며 스풀 활성화. Host는 자동 재접속하고 장비가 S1F13으로 다시 수립
                sockets.Single().Close();
                await TestUtil.Until(() => sockets.Count >= 2 && eq.CommunicationState == GemCommunicationState.Communicating && host.IsCommunicating, 10000);
                await TestUtil.Until(() => eq.SpoolState == GemSpoolState.NoOutput, 5000);
                for (int i = 0; i < 3; i++) Assert.True(eq.TriggerEvent(5002));
                Assert.Equal(4, eq.SpoolCount);                       // SpoolingActivated + 이벤트 3개
                await Task.Delay(300);
                Assert.Empty(ceids);                                  // 자동으로 보내지 않음

                Assert.Equal(GemAck.RsdaAccepted, await host.TransmitSpoolAsync());
                await TestUtil.Until(() => ceids.Count >= 5, 5000);
                Assert.Equal([1307L, 5002, 5002, 5002, 1308], ceids.ToArray());
                await TestUtil.Until(() => eq.SpoolState == GemSpoolState.Inactive, 5000);
                Assert.Equal(0, eq.SpoolCount);
            }
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task PurgeSpool_WhileNoOutput_DiscardsAll()
    {
        var dir = TempDir();
        try
        {
            var sockets = new ConcurrentQueue<Socket>();
            var (eq, host) = await GemHostKit.Loopback(e =>
            {
                e.SpoolDirectory = dir;
                e.InitialControlState = GemControlState.OnlineRemote;
                e.EventsEnabledByDefault = true;
                ((SecsHsms)e.Transport).AcceptHook = l => { var s = l.AcceptSocket(); sockets.Enqueue(s); return s; };
            });
            using (eq) using (host)
            {
                Assert.Equal(0, host.SetSpoolStreams([(6, [])]).Rspack);
                sockets.Single().Close();
                await TestUtil.Until(() => sockets.Count >= 2 && eq.CommunicationState == GemCommunicationState.Communicating && host.IsCommunicating, 10000);
                await TestUtil.Until(() => eq.SpoolState == GemSpoolState.NoOutput, 5000);
                Assert.True(eq.TriggerEvent(5002));

                Assert.Equal(GemAck.RsdaAccepted, host.PurgeSpool());
                await TestUtil.Until(() => eq.SpoolState == GemSpoolState.Inactive && eq.SpoolCount == 0, 5000);
            }
        }
        finally { Delete(dir); }
    }
}
