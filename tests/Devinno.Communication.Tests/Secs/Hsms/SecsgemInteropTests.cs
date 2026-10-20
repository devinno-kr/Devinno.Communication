using System.Collections.Concurrent;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Hsms;

namespace Devinno.Communication.Tests.Secs.Hsms;

/// secsgem(Python)을 상대로 HSMS 상호 운용 확인: 우리 Host ↔ secsgem Equipment, 우리 Equipment ↔ secsgem Host.
/// Python·secsgem이 없으면 건너뛴다(SecsgemFact). secsgem 기본 장치 ID(session_id)는 0이다.
public class SecsgemInteropTests
{
    const string EquipmentScript = """
        import sys
        import secsgem.common, secsgem.hsms, secsgem.gem

        settings = secsgem.hsms.HsmsSettings(
            address="127.0.0.1", port=int(sys.argv[1]),
            connect_mode=secsgem.hsms.HsmsConnectMode.PASSIVE,
            device_type=secsgem.common.DeviceType.EQUIPMENT)
        handler = secsgem.gem.GemEquipmentHandler(settings)
        handler.enable()
        print("READY", flush=True)
        if handler.waitfor_communicating(30):
            print("COMMUNICATING", flush=True)
        sys.stdin.readline()
        handler.disable()
        """;

    const string HostScript = """
        import sys
        import secsgem.common, secsgem.hsms, secsgem.gem

        settings = secsgem.hsms.HsmsSettings(
            address="127.0.0.1", port=int(sys.argv[1]),
            connect_mode=secsgem.hsms.HsmsConnectMode.ACTIVE,
            device_type=secsgem.common.DeviceType.HOST)
        handler = secsgem.gem.GemHostHandler(settings)
        handler.enable()
        if not handler.waitfor_communicating(30):
            print("FAIL not communicating", flush=True)
            sys.exit(1)
        print("COMMUNICATING", flush=True)
        reply = handler.send_and_waitfor_response(handler.stream_function(1, 1)())
        values = handler.settings.streams_functions.decode(reply).get()
        print("S1F2 " + " ".join(str(v) for v in values), flush=True)
        sys.stdin.readline()
        handler.disable()
        """;

    [SecsgemFact]
    public async Task OurHost_SecsgemEquipment()
    {
        int port = Secsgem.FreePort();
        using var py = new SecsgemProcess(EquipmentScript, port.ToString());
        py.WaitLine("READY");
        using var host = new SecsHsms
        {
            Role = SecsRole.Host, Mode = HsmsMode.Active, RemoteIP = "127.0.0.1", RemotePort = port, DeviceID = 0,
            T3 = 10000, T5 = 500, LinktestInterval = 1000,
        };
        host.PrimaryReceived += (s, e) =>
        {
            // 장비가 먼저 보내는 S1F13(통신 수립)에 COMMACK 0으로 답한다
            if (e.Message.Stream == 1 && e.Message.Function == 13) e.Reply(new SecsMessage(1, 14, false, SecsItem.L(SecsItem.B(0), SecsItem.L())));
        };
        host.Start();
        await TestUtil.Until(() => host.IsOpen, 15000);

        var s1f14 = await host.SendAsync(new SecsMessage(1, 13, true, SecsItem.L()));
        Assert.Equal(SecsItem.L(SecsItem.B(0), SecsItem.L(SecsItem.A("secsgem"), SecsItem.A("0.3.0"))), s1f14!.Item);
        py.WaitLine("COMMUNICATING");
        var s1f2 = await host.SendAsync(new SecsMessage(1, 1, true));
        Assert.Equal(SecsItem.L(SecsItem.A("secsgem"), SecsItem.A("0.3.0")), s1f2!.Item);

        await Task.Delay(2500);                 // Linktest 몇 번 주고받아도 연결 유지
        Assert.True(host.IsOpen);
        host.Stop();                            // Separate
    }

    [SecsgemFact]
    public async Task OurEquipment_SecsgemHost()
    {
        using var eq = new SecsHsms
        {
            Role = SecsRole.Equipment, Mode = HsmsMode.Passive, LocalIP = "127.0.0.1", LocalPort = 0, DeviceID = 0, T3 = 10000,
        };
        var mdln = SecsItem.L(SecsItem.A("DEVINNO"), SecsItem.A("1.0"));
        var received = new ConcurrentQueue<string>();
        eq.PrimaryReceived += (s, e) =>
        {
            received.Enqueue($"S{e.Message.Stream}F{e.Message.Function}");
            if (e.Message.Stream == 1 && e.Message.Function == 13) e.Reply(new SecsMessage(1, 14, false, SecsItem.L(SecsItem.B(0), mdln)));
            else if (e.Message.Stream == 1 && e.Message.Function == 1) e.Reply(new SecsMessage(1, 2, false, mdln));
        };
        eq.Start();
        using var py = new SecsgemProcess(HostScript, eq.BoundPort.ToString());
        py.WaitLine("COMMUNICATING");
        Assert.Equal("S1F2 DEVINNO 1.0", py.WaitLine("S1F2"));
        Assert.Contains("S1F13", received);

        var s6f12 = await eq.SendAsync(new SecsMessage(6, 11, true, SecsItem.L(SecsItem.U4(1), SecsItem.U4(100), SecsItem.L())));
        Assert.Equal(SecsItem.B(0), s6f12!.Item);
        var s5f2 = await eq.SendAsync(new SecsMessage(5, 1, true, SecsItem.L(SecsItem.B(0x81), SecsItem.U4(1001), SecsItem.A("DOOR OPEN"))));
        Assert.Equal(SecsFormat.Binary, s5f2!.Item!.Format);
    }
}
