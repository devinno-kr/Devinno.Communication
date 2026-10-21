using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Secs.Hsms;

namespace Devinno.Communication.Tests.Secs.Gem;

/// GemMessages 클래스 문서의 <example> 코드가 실제로 컴파일되고 동작하는지: 같은 코드로 루프백 한 번 왕복
public class GemMessagesExampleTests
{
    static long generation;
    static (byte Commack, string? Model, string? Rev)? established;
    static GemEventReport? received;

    // 문서의 장비 앱 예제 (포트만 0)
    static void EquipmentApp(SecsHsms hsms)
    {
        generation = 0;
        hsms.DeviceOpened += (s, e) =>
        {
            long mine = Interlocked.Increment(ref generation);   // which connection this result belongs to
            hsms.SendOrdered(GemMessages.S1F13("EQ", "1.0"), r =>
            {
                if (!r.IsSuccess || mine != Interlocked.Read(ref generation)) return;
                var (commack, modelName, softwareRevision) = GemMessages.ParseS1F14(r.Reply!);
                // commack 0 = communicating
                established = (commack, modelName, softwareRevision);
            });
        };
        hsms.PrimaryReceived += (s, e) =>
        {
            if (e.Message is { Stream: 1, Function: 3 })
            {
                var svids = GemMessages.ParseS1F3(e.Message);
                e.Reply(GemMessages.S1F4([.. svids.Select(id => SecsItem.F4(25))]));
            }
        };
        hsms.Start();
    }

    // 문서의 Host 앱 예제
    static void HostApp(SecsHsms hsms)
    {
        var table = new Dictionary<GemId, IReadOnlyList<GemId>> { [1000] = new GemId[] { 2001, 3001 } };
        var formats = new GemFormats();   // the same ID formats as the equipment
        hsms.PrimaryReceived += (s, e) =>
        {
            if (e.Message is { Stream: 6, Function: 11 })
            {
                GemEventReport report = GemMessages.ParseS6F11(e.Message, table, formats);
                e.Reply(GemMessages.S6F12(GemAck.Ackc6Accepted));
                received = report;
            }
        };
    }

    [Fact]
    public async Task Examples_RoundTripOverLoopback()
    {
        established = null; received = null;
        var eq = new SecsHsms { Role = SecsRole.Equipment, Mode = HsmsMode.Passive, LocalIP = "127.0.0.1", LocalPort = 0, DeviceID = 1, T3 = 3000, T5 = 200, LinktestInterval = 0 };
        var host = new SecsHsms { Role = SecsRole.Host, Mode = HsmsMode.Active, RemoteIP = "127.0.0.1", DeviceID = 1, T3 = 3000, T5 = 200, LinktestInterval = 0 };
        using (eq) using (host)
        {
            EquipmentApp(eq);
            host.RemotePort = eq.BoundPort;
            host.PrimaryReceived += (s, e) =>
            {
                if (e.Message is { Stream: 1, Function: 13 }) e.Reply(GemMessages.S1F14(0, "HOST", "9"));
            };
            HostApp(host);
            host.Start();
            await TestUtil.Until(() => established != null, 5000);
            Assert.Equal((0, "HOST", "9"), (established!.Value.Commack, established.Value.Model, established.Value.Rev));

            var s1f4 = await host.SendAsync(GemMessages.S1F3([2001, 2002]));
            Assert.Equal(2, GemMessages.ParseS1F4(s1f4!).Count);

            var ack = await eq.SendAsync(GemMessages.S6F11(1, 5001, [(1000, [SecsItem.F4(1), SecsItem.A("LOT")])]));
            Assert.Equal(GemAck.Ackc6Accepted, GemMessages.ParseS6F12(ack!));
            Assert.Equal(new GemId(5001), received!.Ceid);
            Assert.Equal(new GemId(2001), received.Reports[0].Values[0].Vid);
            Assert.Equal(new GemId(3001), received.Reports[0].Values[1].Vid);
        }
    }
}
