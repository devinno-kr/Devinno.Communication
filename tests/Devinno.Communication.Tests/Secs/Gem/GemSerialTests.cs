using System.Collections.Concurrent;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Secs.Serial;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Secs.Gem;

/// SECS-I 위의 GemEquipment: 메모리 회선 쌍의 한쪽은 우리 장비, 다른 쪽은 Plan 7 SecsSerial(Host)
public class GemSerialTests
{
    [Fact]
    public async Task OverSecsI_EstablishesCommunication_AnswersS1F3_SendsS6F11()
    {
        var (a, b) = MemoryLine.Pair();
        var eqLine = new SecsSerial { DeviceID = 1, T1 = 200, T2 = 1000, T3 = 3000, T4 = 2000 };
        eqLine.OpenLine = a.ToLine;
        using var eq = new GemEquipment(eqLine) { ModelName = "DV-100", SoftwareRevision = "1.0.0", InitialControlState = GemControlState.OnlineRemote, EventsEnabledByDefault = true };
        GemKit.Sample(eq);
        using var host = new SecsSerial { Role = SecsRole.Host, DeviceID = 1, T1 = 200, T2 = 1000, T3 = 3000, T4 = 2000 };
        host.OpenLine = b.ToLine;
        var got = new BlockingCollection<SecsMessage>();
        host.PrimaryReceived += (s, e) =>
        {
            got.Add(e.Message);
            if (e.Message.Stream == 1 && e.Message.Function == 13) e.Reply(new SecsMessage(1, 14, false, SecsItem.L(SecsItem.B(0), SecsItem.L())));
            if (e.Message.Stream == 6 && e.Message.Function == 11) e.Reply(new SecsMessage(6, 12, false, SecsItem.B(0)));
        };
        host.Start();
        eq.Start();
        await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.Communicating, 5000);
        var reply = await host.SendAsync(new SecsMessage(1, 3, true, SecsItem.L(SecsItem.U4(2001))));
        Assert.Equal("<L [1] <F4 25>>", reply!.Item!.ToSml());
        Assert.True(eq.TriggerEvent(5002));
        await TestUtil.Until(() => got.Any(m => m.Stream == 6 && m.Function == 11), 5000);
    }
}
