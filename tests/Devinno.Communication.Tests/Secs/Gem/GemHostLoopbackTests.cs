using System.Collections.Concurrent;
using System.Net.Sockets;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Secs.Hsms;
using Devinno.Communication.Secs.Serial;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 우리 GemHost ↔ 우리 GemEquipment 전체 시나리오(HSMS, SECS-I 메모리 회선). 대칭 버그는 서로 가려지므로 GemHostSecsgemTests와 짝으로 본다
[Collection(nameof(GemHostCollection))]
public class GemHostLoopbackTests
{
    sealed class Recorder
    {
        public readonly BlockingCollection<GemConnectResult> Results = [];
        public readonly BlockingCollection<GemEventReport> Events = [];
        public readonly BlockingCollection<GemAlarmEventArgs> Alarms = [];
        public readonly BlockingCollection<GemTerminalEventArgs> Terminals = [];

        public void Attach(GemHost h)
        {
            h.ConnectSequenceCompleted += (s, e) => Results.Add(e);
            h.EventReceived += (s, e) => Events.Add(e.Report);
            h.AlarmReceived += (s, e) => Alarms.Add(e);
            h.TerminalReceived += (s, e) => Terminals.Add(e);
        }
    }

    static T Take<T>(BlockingCollection<T> c, int timeoutMs = 5000)
    {
        Assert.True(c.TryTake(out var v, timeoutMs), $"{typeof(T).Name}이(가) {timeoutMs}ms 안에 오지 않았습니다.");
        return v!;
    }

    static void Configure(GemEquipment e)
    {
        GemKit.Sample(e);
        e.RemoteCommandReceived += (s, a) => a.Hcack = a.Parameters.Any(p => p.Name == "LOTID") ? GemAck.HcackAcceptedLater : GemAck.HcackAccepted;
    }

    static void Subscribe(GemHost h)
    {
        h.AutoConnectSequence = true;
        h.ConnectSetClock = true;
        h.Subscribe(5001, 2001, 3001);
        h.Subscribe(9001);                                                                   // 알람 Set CE(값 없음)
    }

    [Fact]
    public async Task Hsms_FullScenario_ThenReconnectReconfigures()
    {
        var sockets = new ConcurrentQueue<Socket>();
        using var eq = GemKit.Equipment(Configure);                                       // 기본: HOST OFF-LINE, CE·알람 꺼짐
        ((SecsHsms)eq.Transport).AcceptHook = l => { var s = l.AcceptSocket(); sockets.Enqueue(s); return s; };
        var eqTerminals = new BlockingCollection<string>();
        eq.TerminalReceived += (s, e) => eqTerminals.Add(e.Lines.Single());
        eq.Start();
        var rec = new Recorder();
        using var host = GemHostKit.Host(GemKit.Port(eq), h => { Subscribe(h); rec.Attach(h); });
        host.Start();

        // 접속 시퀀스: S1F13 → S1F17 → S2F31 → 리포트 → S5F3
        var first = Take(rec.Results);
        Assert.True(first.Success, $"{first.FailedStep} {first.Ack} {first.Exception}");
        Assert.Equal(GemControlState.OnlineRemote, eq.ControlState);
        Assert.Equal(("DV-100", "1.0.0"), (host.ModelName, host.SoftwareRevision));
        Assert.Equal("<U4 5001 9001>", eq.GetValue(1004).ToSml());

        // 이벤트 해석
        eq.SetValue(2001, SecsItem.F4(36.5f));
        Assert.True(eq.TriggerEvent(5001, new Dictionary<GemId, SecsItem> { [3001] = SecsItem.A("LOT-7") }));
        var ev = Take(rec.Events);
        Assert.Equal((SecsItem.F4(36.5f), "LOT-7"), (ev.GetValue(2001), ev.GetValue(3001)!.GetString()));

        // 알람: S5F1 + 알람 CE 9001(값 없는 구독)
        Assert.True(eq.SetAlarm(1));
        var alarm = Take(rec.Alarms);
        Assert.Equal((new GemId(1), true), (alarm.Alid, alarm.IsSet));
        Assert.Equal(new GemId(9001), Take(rec.Events).Ceid);

        // 리모트 명령, EC, 상태, 시계
        Assert.Equal(GemAck.HcackAcceptedLater, (await host.RemoteCommandAsync("START", [("LOTID", SecsItem.A("L1"))])).Hcack);
        Assert.Equal(GemAck.HcackAccepted, host.RemoteCommand("STOP").Hcack);
        Assert.Equal(GemAck.EacAccepted, await host.WriteConstantsAsync([(4001, SecsItem.F4(150))]));
        Assert.Equal(SecsItem.F4(150), (await host.ReadConstantsAsync([4001])).Single());
        Assert.Equal(SecsItem.F4(36.5f), (await host.ReadStatusAsync([2001])).Single());
        Assert.True(Math.Abs((await host.ReadEquipmentTimeAsync() - DateTime.Now).TotalSeconds) < 5);

        // 터미널 양방향
        Assert.Equal(GemAck.Ackc10Accepted, await host.SendTerminalAsync(1, "to equipment"));
        Assert.Equal("to equipment", Take(eqTerminals));
        Assert.True(eq.SendTerminalMessage(2, "to host"));
        Assert.Equal("to host", Take(rec.Terminals).Lines.Single());

        // 재접속: 연결이 끊기면 Host가 다시 접속하고 접속 시퀀스를 처음부터(리포트 다시 정의) 실행한다
        foreach (var s in sockets) s.Close();
        var again = Take(rec.Results, 10000);
        Assert.True(again.Success, $"{again.FailedStep} {again.Ack} {again.Exception}");
        Assert.True(eq.TriggerEvent(5001));
        Assert.Equal(new GemId(1000), Take(rec.Events).Reports.Single().Rptid);
    }

    [Fact]
    public async Task RapidReconnects_LiveLinkSuccessIsLast_NoStaleResultsAfterIt()
    {
        var sockets = new ConcurrentQueue<Socket>();
        using var eq = GemKit.Equipment(Configure);
        ((SecsHsms)eq.Transport).AcceptHook = l => { var s = l.AcceptSocket(); sockets.Enqueue(s); return s; };
        eq.Start();
        var results = new ConcurrentQueue<GemConnectResult>();
        using var host = GemHostKit.Host(GemKit.Port(eq), h => { Subscribe(h); h.ConnectSequenceCompleted += (s, e) => results.Enqueue(e); });
        host.Start();

        // 연결 4개를 받자마자 끊는다(시퀀스 시작 전·도중). 5번째 연결은 그대로 둔다
        for (int i = 1; i <= 4; i++)
        {
            await TestUtil.Until(() => sockets.Count >= i, 5000);
            sockets.ElementAt(i - 1).Close();
        }
        await TestUtil.Until(() => sockets.Count >= 5, 5000);
        await TestUtil.Until(() => !results.IsEmpty && results.Last().Success && host.IsCommunicating, 15000);
        int count;
        do
        {
            count = results.Count;
            await Task.Delay(1500);                                                         // 결과가 더 오지 않을 때까지
        }
        while (results.Count != count);

        Assert.Equal(5, sockets.Count);
        var all = results.ToArray();
        Assert.True(all[^1].Success, string.Join(" | ", all.Select(r => $"{r.FailedStep} {r.Exception?.GetType().Name}")));
        Assert.All(all.Where(r => !r.Success), r => Assert.IsType<SecsDisconnectedException>(r.Exception));
        Assert.True(all.Length <= 5, $"결과 {all.Length}개");                              // 연결마다 많아야 하나(시작 전에 끊긴 연결은 알리지 않음)
        Assert.True(eq.TriggerEvent(5001));
        Assert.Equal(GemControlState.OnlineRemote, eq.ControlState);
    }

    [Fact]
    public async Task OverSecsI_ConnectSequence_Event_Status_Alarm()
    {
        var (a, b) = MemoryLine.Pair();
        var eqLine = new SecsSerial { DeviceID = 1, T1 = 200, T2 = 1000, T3 = 3000, T4 = 2000 };
        eqLine.OpenLine = a.ToLine;
        using var eq = new GemEquipment(eqLine) { ModelName = "DV-100", SoftwareRevision = "1.0.0" };
        Configure(eq);
        var hostLine = new SecsSerial { DeviceID = 1, T1 = 200, T2 = 1000, T3 = 3000, T4 = 2000 };
        hostLine.OpenLine = b.ToLine;
        var rec = new Recorder();
        using var host = new GemHost(hostLine);
        Subscribe(host);
        rec.Attach(host);
        eq.Start();
        host.Start();

        var result = Take(rec.Results, 10000);
        Assert.True(result.Success, $"{result.FailedStep} {result.Ack} {result.Exception}");
        Assert.Equal(GemControlState.OnlineRemote, eq.ControlState);
        Assert.Equal(SecsItem.F4(25), (await host.ReadStatusAsync([2001])).Single());
        Assert.True(eq.TriggerEvent(5001));
        Assert.Equal(SecsItem.F4(25), Take(rec.Events).GetValue(2001));
        Assert.True(eq.SetAlarm(1));
        Assert.True(Take(rec.Alarms).IsSet);
    }
}
