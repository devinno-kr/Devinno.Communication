using System.Collections.Concurrent;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Secs.Hsms;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 독립 상대역: 우리 GemHost ↔ secsgem GemEquipmentHandler(Python, 별도 프로세스, HSMS). Python·secsgem이 없으면 건너뛴다.
/// 우리 Host가 Passive(포트 0, BoundPort)로 기다리고 secsgem 장비가 Active로 접속한다(HSMS Active/Passive는 Host/Equipment와 무관).
/// secsgem 0.3.0의 알려진 차이(이 테스트가 피해 감): 링크하지 않은 CE를 S2F37로 켜면 ERACK 1(그래서 VID 있는 구독만),
/// 길이 0 ALID의 S5F3(모든 알람)은 처리기 예외로 S5F0(그래서 접속 시퀀스의 알람 켜기를 끄고 ALID를 하나씩 켬),
/// S5F5는 <L ALID…>만 해석해 E5 배열(우리 Host가 보내는 형식)에는 응답하지 않음(그래서 S5F5는 여기서 확인하지 않음). 초기 제어 상태는 HOST OFF-LINE으로 둔다(S1F17이 ONLACK 0이 되게).
[Collection(nameof(GemHostCollection))]
public class GemHostSecsgemTests
{
    const string EquipmentScript = """
        import sys
        import secsgem.common, secsgem.hsms, secsgem.gem, secsgem.secs

        settings = secsgem.hsms.HsmsSettings(
            address="127.0.0.1", port=int(sys.argv[1]),
            connect_mode=secsgem.hsms.HsmsConnectMode.ACTIVE,
            device_type=secsgem.common.DeviceType.EQUIPMENT)
        h = secsgem.gem.GemEquipmentHandler(settings, initial_control_state="HOST_OFFLINE")
        h._mdln = "SG-EQ"
        V = secsgem.secs.variables
        h.status_variables[2001] = secsgem.gem.StatusVariable(2001, "Temperature", "C", V.F4, False)
        h.status_variables[2001].value = 25.5
        h.data_values[3001] = secsgem.gem.DataValue(3001, "LotID", V.String, False)
        h.data_values[3001].value = "LOT-SG"
        h.equipment_constants[4001] = secsgem.gem.EquipmentConstant(4001, "MaxTemp", 0, 200, 80, "C", V.U4, False)
        h.collection_events[5001] = secsgem.gem.CollectionEvent(5001, "ProcessStarted", [3001])
        h.collection_events[9001] = secsgem.gem.CollectionEvent(9001, "AlarmSet", [])
        h.collection_events[9002] = secsgem.gem.CollectionEvent(9002, "AlarmCleared", [])
        h.alarms[1] = secsgem.gem.Alarm(1, "OverTemp", "Over temperature", 2, 9001, 9002)
        h.remote_commands["START"] = secsgem.gem.RemoteCommand("START", "Start", ["LOTID"], 5001)
        lots = []
        h.callbacks.rcmd_START = lambda LOTID: lots.append(LOTID)

        def say(*a):
            print(*a, flush=True)

        h.enable()
        say("READY")
        for line in sys.stdin:
            cmd = line.strip()
            if cmd == "event":
                h.trigger_collection_events([5001])
                say("EVENT_SENT")
            elif cmd == "alarm":
                h.set_alarm(1)
                say("ALARM_SENT")
            elif cmd == "lots":
                say("LOTS", lots)
        h.disable()
        """;

    [SecsgemFact]
    public async Task OurHost_SecsgemGemEquipment_FullScenario()
    {
        var transport = new SecsHsms { Mode = HsmsMode.Passive, LocalIP = "127.0.0.1", LocalPort = 0, DeviceID = 0, T3 = 10000 };
        using var host = new GemHost(transport) { ConnectEnableAlarms = false };
        host.Subscribe(5001, 2001, 3001);
        var results = new BlockingCollection<GemConnectResult>();
        var events = new BlockingCollection<GemEventReport>();
        var alarms = new BlockingCollection<GemAlarmEventArgs>();
        host.ConnectSequenceCompleted += (s, e) => results.Add(e);
        host.EventReceived += (s, e) => events.Add(e.Report);
        host.AlarmReceived += (s, e) => alarms.Add(e);
        host.Start();
        using var py = new SecsgemProcess(EquipmentScript, transport.BoundPort.ToString());
        py.WaitLine("READY");

        Assert.True(results.TryTake(out var r, 30000), "접속 시퀀스가 끝나지 않았습니다.");
        Assert.True(r!.Success, $"{r.FailedStep} {r.Ack} {r.Exception}");                 // S1F13 → S1F17 → S2F33/37/33/35/37
        Assert.True(host.IsCommunicating);
        Assert.Equal(("SG-EQ", "0.3.0"), await host.AreYouThereAsync());                   // S1F1

        Assert.Equal([SecsItem.F4(25.5f)], await host.ReadStatusAsync([2001]));            // S1F3
        var sv = (await host.ReadStatusNamesAsync([2001])).Single();                        // S1F11
        Assert.Equal(("Temperature", "C"), (sv.Name, sv.Units));
        Assert.Equal(80, (await host.ReadConstantsAsync([4001])).Single().GetInt64());     // S2F13
        Assert.Equal(GemAck.EacAccepted, await host.WriteConstantsAsync([(4001, SecsItem.U4(120))]));   // S2F15
        Assert.Equal(120, (await host.ReadConstantsAsync([4001])).Single().GetInt64());
        var ec = (await host.ReadConstantNamesAsync([4001])).Single();                     // S2F29
        Assert.Equal(("MaxTemp", 0L, 200L), (ec.Name, ec.Min.GetInt64(), ec.Max.GetInt64()));

        py.Send("event");                                                                   // S6F11 → 우리 해석
        py.WaitLine("EVENT_SENT");
        Assert.True(events.TryTake(out var report, 10000));
        Assert.Equal(new GemId(5001), report!.Ceid);
        Assert.Equal(SecsItem.F4(25.5f), report.GetValue(2001));
        Assert.Equal("LOT-SG", report.GetValue(3001)!.GetString());

        var command = await host.RemoteCommandAsync("START", [("LOTID", SecsItem.A("L1"))]);    // S2F41 → HCACK 4(secsgem은 늘 나중에 끝냄)
        Assert.Equal((GemAck.HcackAcceptedLater, true), (command.Hcack, command.IsAccepted));
        py.Send("lots");
        Assert.Equal("LOTS ['L1']", py.WaitLine("LOTS"));
        Assert.True(events.TryTake(out var done, 10000));                                  // 명령 완료 CE(5001)
        Assert.Equal(new GemId(5001), done!.Ceid);

        Assert.Equal(GemAck.Ackc5Accepted, await host.EnableAlarmAsync(1, true));           // S5F3
        py.Send("alarm");                                                                   // S5F1
        Assert.True(alarms.TryTake(out var alarm, 10000));
        Assert.Equal((new GemId(1), true, 2, "Over temperature"), (alarm!.Alid, alarm.IsSet, alarm.Category, alarm.Text));
        Assert.Equal([new GemId(1)], (await host.ListEnabledAlarmsAsync()).Select(a => a.Alid));   // S5F7

        Assert.Equal(GemAck.OflackAccepted, await host.RequestOfflineAsync());             // S1F15
    }
}
