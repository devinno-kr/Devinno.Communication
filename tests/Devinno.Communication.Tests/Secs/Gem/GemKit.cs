using System.Collections.Concurrent;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Secs.Hsms;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Secs.Gem;

/// GEM 장비 테스트 도구: 루프백 HSMS 장비(Passive, 127.0.0.1, 포트 0), 표본 정의, 가짜 Host 접속, 이벤트 기록
internal static class GemKit
{
    public static SecsHsms Transport() => new()
    {
        Mode = HsmsMode.Passive, LocalIP = "127.0.0.1", LocalPort = 0, DeviceID = 1,
        T3 = 3000, T5 = 200, T6 = 1000, T7 = 2000, T8 = 1000, LinktestInterval = 0,
    };

    public static GemEquipment Equipment(Action<GemEquipment>? setup = null)
    {
        var eq = new GemEquipment(Transport()) { ModelName = "DV-100", SoftwareRevision = "1.0.0" };
        setup?.Invoke(eq);
        return eq;
    }

    /// 표본 정의: SV 2001 온도(F4, 25)·2002 개수(U4), DV 3001 LOT(A), EC 4001 최고 온도(F4 80, 0~200), CE 5001(2001, 3001)·5002, 알람 1(9001/9002), 명령 START(LOTID A)·STOP
    public static void Sample(GemEquipment eq)
    {
        eq.AddStatusVariable(new GemStatusVariable(2001, "Temperature", SecsFormat.F4, "C", SecsItem.F4(25)));
        eq.AddStatusVariable(new GemStatusVariable(2002, "Count", SecsFormat.U4));
        eq.AddDataVariable(new GemDataVariable(3001, "LotID", SecsFormat.ASCII));
        eq.AddEquipmentConstant(new GemEquipmentConstant(4001, "MaxTemp", SecsItem.F4(80), SecsItem.F4(0), SecsItem.F4(200), "C"));
        eq.AddCollectionEvent(new GemCollectionEvent(5001, "ProcessStarted", [2001, 3001]));
        eq.AddCollectionEvent(new GemCollectionEvent(5002, "ProcessCompleted"));
        eq.AddAlarm(new GemAlarm(1, "Over temperature", 2, 9001, 9002));
        eq.AddRemoteCommand(new GemRemoteCommand("START", [new GemCommandParameter("LOTID", SecsFormat.ASCII)]));
        eq.AddRemoteCommand(new GemRemoteCommand("STOP"));
    }

    public static int Port(GemEquipment eq) => ((SecsHsms)eq.Transport).BoundPort;

    /// 장비를 시작하고 가짜 Host를 Selected까지 접속한다
    public static async Task<FakeGemHost> Connect(GemEquipment eq, Action<FakeGemHost>? hostSetup = null)
    {
        eq.Start();
        var host = new FakeGemHost(Port(eq));
        hostSetup?.Invoke(host);
        host.Start();
        await TestUtil.Until(() => host.Hsms.IsOpen && eq.IsOpen, 5000);
        return host;
    }

    /// 장비를 시작하고 가짜 Host를 접속해 통신 수립(COMMUNICATING)까지 간다. 장비가 먼저 보낸 S1F13은 꺼내 둔다
    public static async Task<FakeGemHost> Communicating(GemEquipment eq, Action<FakeGemHost>? hostSetup = null)
    {
        var host = await Connect(eq, hostSetup);
        host.Next(1, 13);
        await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.Communicating, 5000);
        return host;
    }

    /// 공용 이벤트(SendFailed, ProtocolError, 그리고 상태 변화)를 받은 순서대로 기록한다
    public static ConcurrentQueue<string> Log(GemEquipment eq)
    {
        var log = new ConcurrentQueue<string>();
        eq.CommunicationStateChanged += (s, e) => log.Enqueue($"Comm {e.Previous}->{e.Current}");
        eq.ControlStateChanged += (s, e) => log.Enqueue($"Control {e.Previous}->{e.Current}");
        eq.SendFailed += (s, e) => log.Enqueue($"SendFailed S{e.Message.Stream}F{e.Message.Function} {e.Reason}{(e.Ack is { } a ? $" {a}" : "")}");
        eq.ProtocolError += (s, e) => log.Enqueue($"Error {e.Reason}");
        return log;
    }

    /// 기록 중 prefix로 시작하는 것만 (오류 알림을 뺀 상태 변화 비교용)
    public static string[] Only(ConcurrentQueue<string> log, string prefix) => [.. log.Where(l => l.StartsWith(prefix, StringComparison.Ordinal))];
}
