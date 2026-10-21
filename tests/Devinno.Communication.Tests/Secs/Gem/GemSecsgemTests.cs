using System.Collections.Concurrent;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Secs.Hsms;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 독립 상대역: 우리 GemEquipment ↔ secsgem GemHostHandler(Python, 별도 프로세스, HSMS). Python·secsgem이 없으면 건너뛴다.
/// secsgem의 알려진 차이(WAIT DELAY 규칙 무시, 제어 모델 미완)는 여기서 단정하지 않는다. secsgem 기본 장치 ID(session_id)는 0이다.
/// secsgem 0.3.0은 S5F3·S10F3(E5 "응답 선택")을 W비트 없이 보내고도 응답을 기다려 T3까지 멈추므로(enable_alarm 등), 이 테스트는 W비트를 켜서 보낸다.
public class GemSecsgemTests
{
    const string HostScript = """
        import sys, time
        import secsgem.common, secsgem.hsms, secsgem.gem

        settings = secsgem.hsms.HsmsSettings(
            address="127.0.0.1", port=int(sys.argv[1]),
            connect_mode=secsgem.hsms.HsmsConnectMode.ACTIVE,
            device_type=secsgem.common.DeviceType.HOST)
        h = secsgem.gem.GemHostHandler(settings)
        got = {"event": [], "alarm": [], "terminal": []}
        h.events.collection_event_received += lambda d: got["event"].append(d)
        h.events.alarm_received += lambda d: got["alarm"].append(d)
        h.events.terminal_received += lambda d: got["terminal"].append(d)

        def say(*a):
            print(*a, flush=True)

        def val(x):
            return x.get() if hasattr(x, "get") else x

        def call(s, f, data=None, w=False):
            fn = h.stream_function(s, f)
            msg = fn(data) if data is not None else fn()
            if w:
                msg.__dict__["is_reply_required"] = True   # secsgem은 S5F3·S10F3을 W 없이 보내고도 응답을 기다린다(T3까지 멈춤) → W를 켜서 보냄
            return h.settings.streams_functions.decode(h.send_and_waitfor_response(msg))

        def wait(key):
            for _ in range(200):
                if got[key]:
                    return got[key].pop(0)
                time.sleep(0.05)
            return None

        h.enable()
        if not h.waitfor_communicating(30):
            say("FAIL not communicating")
            sys.exit(1)
        say("COMMUNICATING")
        say("ONLINE", h.go_online())
        say("SV", call(1, 3, [2001, 1002]).get())
        say("EC", call(2, 13, [4001]).get())
        say("EAC", call(2, 15, [{"ECID": 4001, "ECV": 120}]).get())
        say("EC2", call(2, 13, [4001]).get())
        h.subscribe_collection_event(5001, [2001, 3001])
        say("SUBSCRIBED")
        e = wait("event")
        say("EVENT", val(e["ceid"]), [val(v["value"]) for v in e["values"]]) if e else say("EVENT none")
        say("HCACK", val(h.send_remote_command("START", [["LOTID", "LOT-1"]]).HCACK))
        say("ACKC5", call(5, 3, {"ALED": h.settings.data_items.ALED.ENABLE, "ALID": 1}, w=True).get())
        say("ALARM_ENABLED")
        a = wait("alarm")
        say("ALARM", val(a["alid"]), val(a["code"]), val(a["text"])) if a else say("ALARM none")
        say("ALARMS", [[val(x["ALID"]), val(x["ALTX"])] for x in h.list_alarms([1])])
        say("ENABLED", [val(x["ALID"]) for x in h.list_enabled_alarms()])
        say("ACKC10", call(10, 3, {"TID": 1, "TEXT": "hello"}, w=True).get())
        say("TERMINAL_WAIT")
        t = wait("terminal")
        say("TERMINAL", val(t["terminal"]), val(t["text"])) if t else say("TERMINAL none")
        say("OFFLINE", h.go_offline())
        say("DONE")
        sys.stdin.readline()
        h.disable()
        """;

    [SecsgemFact]
    public async Task OurEquipment_SecsgemGemHost_FullScenario()
    {
        var transport = new SecsHsms { Mode = HsmsMode.Passive, LocalIP = "127.0.0.1", LocalPort = 0, DeviceID = 0, T3 = 10000 };
        using var eq = new GemEquipment(transport) { ModelName = "DV-100", SoftwareRevision = "1.0.0" };
        GemKit.Sample(eq);
        var commands = new ConcurrentQueue<string>();
        var terminals = new ConcurrentQueue<string>();
        eq.RemoteCommandReceived += (s, e) => commands.Enqueue($"{e.Command} {e.Parameters[0].Name}={e.Parameters[0].Value.GetString()}");
        eq.TerminalReceived += (s, e) => terminals.Enqueue($"{e.Tid}:{e.Lines[0]}");
        eq.Start();
        using var py = new SecsgemProcess(HostScript, transport.BoundPort.ToString());

        py.WaitLine("COMMUNICATING");
        await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.Communicating, 5000);
        Assert.Equal("ONLINE 0", py.WaitLine("ONLINE"));                                   // S1F17 → ONLACK 0
        await TestUtil.Until(() => eq.ControlState == GemControlState.OnlineRemote);
        Assert.Equal("SV [25.0, 5]", py.WaitLine("SV"));                                    // S1F3
        Assert.Equal("EC [80.0]", py.WaitLine("EC "));
        Assert.Equal("EAC 0", py.WaitLine("EAC"));                                          // S2F15 (정수 → F4 변환)
        Assert.Equal("EC2 [120.0]", py.WaitLine("EC2"));

        py.WaitLine("SUBSCRIBED");                                                          // S2F33 → S2F35 → S2F37
        eq.SetValue(2001, SecsItem.F4(36.5f));
        Assert.True(eq.TriggerEvent(5001, new Dictionary<GemId, SecsItem> { [3001] = SecsItem.A("LOT-7") }));
        Assert.Equal("EVENT 5001 [36.5, 'LOT-7']", py.WaitLine("EVENT"));                  // S6F11 해석(리포트 값)

        Assert.Equal("HCACK 0", py.WaitLine("HCACK"));                                      // S2F41
        Assert.Equal(["START LOTID=LOT-1"], commands);

        Assert.Equal("ACKC5 0", py.WaitLine("ACKC5"));                                      // S5F3
        py.WaitLine("ALARM_ENABLED");
        Assert.True(eq.SetAlarm(1));
        Assert.StartsWith("ALARM 1 ", py.WaitLine("ALARM "));                               // S5F1
        Assert.Equal("ALARMS [[1, 'Over temperature']]", py.WaitLine("ALARMS"));          // S5F5(secsgem은 <L ALID…>)
        Assert.Equal("ENABLED [1]", py.WaitLine("ENABLED"));                                // S5F7

        Assert.Equal("ACKC10 0", py.WaitLine("ACKC10"));                                    // S10F3
        await TestUtil.Until(() => terminals.Count == 1);
        Assert.Equal("1:hello", terminals.Single());
        py.WaitLine("TERMINAL_WAIT");
        Assert.True(eq.SendTerminalMessage(2, "from equipment"));                          // S10F1
        Assert.Equal("TERMINAL 2 from equipment", py.WaitLine("TERMINAL "));

        Assert.Equal("OFFLINE 0", py.WaitLine("OFFLINE"));                                  // S1F15 → OFLACK 0
        await TestUtil.Until(() => eq.ControlState == GemControlState.HostOffline);
        py.WaitLine("DONE");
    }

    const string SpoolHostScript = """
        import sys, time
        import secsgem.common, secsgem.hsms, secsgem.gem

        settings = secsgem.hsms.HsmsSettings(
            address="127.0.0.1", port=int(sys.argv[1]),
            connect_mode=secsgem.hsms.HsmsConnectMode.ACTIVE,
            device_type=secsgem.common.DeviceType.HOST)
        h = secsgem.gem.GemHostHandler(settings)
        got = []

        def on_s6f11(handler, message):                      # 보고서가 없는 S6F11(스풀된 이벤트)도 CEID만 모은다
            f = h.settings.streams_functions.decode(message)
            got.append(f.CEID.get())
            return h.stream_function(6, 12)(0)

        h.register_stream_function(6, 11, on_s6f11)

        def say(*a):
            print(*a, flush=True)

        def val(x):
            return x.get() if hasattr(x, "get") else x

        def ask(s, f, data):
            fn = h.stream_function(s, f)
            return h.settings.streams_functions.decode(h.send_and_waitfor_response(fn(data)))

        h.enable()
        if not h.waitfor_communicating(30):
            say("FAIL not communicating")
            sys.exit(1)
        say("COMMUNICATING")
        say("S2F44", ask(2, 43, [{"STRID": 6, "FCNID": []}]).get())
        sys.stdin.readline()
        h.disable()
        say("DROPPED")
        sys.stdin.readline()
        h.enable()
        if not h.waitfor_communicating(30):
            say("FAIL not communicating again")
            sys.exit(1)
        say("COMMUNICATING2")
        sys.stdin.readline()
        say("S6F24", ask(6, 23, 0).get())
        for _ in range(100):
            if len(got) >= 5:
                break
            time.sleep(0.05)
        say("EVENTS", got)
        sys.stdin.readline()
        h.disable()
        """;

    [SecsgemFact]
    public async Task OurEquipment_SecsgemGemHost_SpoolRequestAndTransmit()
    {
        var dir = Path.Combine(Path.GetTempPath(), "devinno-spool-" + Guid.NewGuid());
        try
        {
            var transport = new SecsHsms { Mode = HsmsMode.Passive, LocalIP = "127.0.0.1", LocalPort = 0, DeviceID = 0, T3 = 10000 };
            using var eq = new GemEquipment(transport) { ModelName = "DV-100", SoftwareRevision = "1.0.0", SpoolDirectory = dir, EventsEnabledByDefault = true, InitialControlState = GemControlState.OnlineRemote };
            GemKit.Sample(eq);
            eq.Start();
            using var py = new SecsgemProcess(SpoolHostScript, transport.BoundPort.ToString());

            py.WaitLine("COMMUNICATING");
            await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.Communicating, 5000);
            var s2f44 = py.WaitLine("S2F44");                                                  // S2F43 → RSPACK 0, 오류 없음
            Assert.Contains("0", s2f44);

            py.Send("drop");                                                                   // 연결을 끊어 스풀 활성화
            py.WaitLine("DROPPED");
            await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.NotCommunicating, 5000);
            py.Send("back");
            py.WaitLine("COMMUNICATING2");
            await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.Communicating && eq.SpoolState == GemSpoolState.NoOutput, 5000);
            for (int i = 0; i < 3; i++) Assert.True(eq.TriggerEvent(5002));

            py.Send("transmit");                                                               // S6F23 RSDC 0 → RSDA 0, 스풀된 S6F11을 받음
            Assert.Equal("S6F24 0", py.WaitLine("S6F24"));
            Assert.Equal("EVENTS [1307, 5002, 5002, 5002, 1308]", py.WaitLine("EVENTS"));
            await TestUtil.Until(() => eq.SpoolState == GemSpoolState.Inactive, 5000);
            py.Send("end");
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
