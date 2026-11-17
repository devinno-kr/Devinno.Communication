using System.Collections.Concurrent;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// GemHost.EnhancedRemoteCommand(S2F49, Plan 17 Task 3): 우리 GemEquipment와 루프백
[Collection(nameof(GemHostCollection))]
public class GemHostEnhancedCommandTests
{
    [Fact]
    public async Task Enhanced_RoundTrip()
    {
        var got = new ConcurrentQueue<string>();
        var (eq, host) = await GemHostKit.Loopback(e =>
        {
            e.InitialControlState = GemControlState.OnlineRemote;
            e.RemoteCommandReceived += (s, a) => got.Enqueue($"{a.ObjectSpec} {a.Command} {a.Parameters[0].Value.GetString()}");
        });
        using (eq) using (host)
        {
            var r = host.EnhancedRemoteCommand("CH1", "START", ("LOTID", SecsItem.A("L1")));
            Assert.Equal(0, r.Hcack);
            Assert.True(r.IsAccepted);
            Assert.Empty(r.ParameterAcks);
            Assert.Equal(new[] { "CH1 START L1" }, got);
        }
    }

    [Fact]
    public async Task Enhanced_ListCepack()
    {
        var (eq, host) = await GemHostKit.Loopback(e =>
        {
            e.InitialControlState = GemControlState.OnlineRemote;
            e.AddRemoteCommand(new GemRemoteCommand("SETUP", [new GemCommandParameter("STEPS")]));
            e.RemoteCommandReceived += (s, a) => a.SetParameterAck("STEPS", SecsItem.L(SecsItem.U1(0), SecsItem.U1(2)));
        });
        using (eq) using (host)
        {
            var r = host.EnhancedRemoteCommand("", "SETUP", ("STEPS", SecsItem.L(SecsItem.U4(1), SecsItem.U4(2))));
            Assert.Equal(GemAck.HcackParameterInvalid, r.Hcack);
            Assert.Equal("STEPS", r.ParameterAcks[0].Name);
            Assert.Equal(SecsItem.L(SecsItem.U1(0), SecsItem.U1(2)), r.ParameterAcks[0].Cepack);
        }
    }

    [Fact]
    public async Task Enhanced_DataIdIncrements()
    {
        var ids = new ConcurrentQueue<string>();
        var (eq, host) = await GemHostKit.Loopback(e =>
        {
            e.InitialControlState = GemControlState.OnlineRemote;
            e.RegisterMessage(2, 49);
            e.MessageReceived += (s, a) =>
            {
                ids.Enqueue(a.Message.Item!.Items[0].ToSml());
                a.Reply(new SecsMessage(2, 50, false, SecsItem.L(SecsItem.B(0), SecsItem.L())));
            };
        });
        using (eq) using (host)
        {
            host.EnhancedRemoteCommand("", "STOP");
            host.EnhancedRemoteCommand("", "STOP");
            Assert.Equal(2, ids.Count);
            var a = ids.ToArray();
            Assert.NotEqual(a[0], a[1]);
            Assert.Equal(a[0].Replace("1", "2"), a[1].Replace("1", "2"));      // 같은 포맷, 다음 번호
        }
    }

    [Fact]
    public async Task Enhanced_Async_SameAsSync()
    {
        var (eq, host) = await GemHostKit.Loopback(e =>
        {
            e.InitialControlState = GemControlState.OnlineRemote;
            e.RemoteCommandReceived += (s, a) => a.Hcack = GemAck.HcackAcceptedLater;
        });
        using (eq) using (host)
        {
            var r = await host.EnhancedRemoteCommandAsync("CH2", "STOP");
            Assert.Equal(GemAck.HcackAcceptedLater, r.Hcack);
            Assert.True(r.IsAccepted);
        }
    }
}
