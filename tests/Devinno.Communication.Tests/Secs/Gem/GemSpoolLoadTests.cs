using System.Collections.Concurrent;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 스풀에 모으기(Plan 14 Task 4): 활성화(전이 1)·버림 경로 연결·ACTIVE 중 새 대상 메시지·가득 참(전이 2)·S2F43
public class GemSpoolLoadTests
{
    static string TempDir() => Path.Combine(Path.GetTempPath(), "devinno-spool-" + Guid.NewGuid());

    static void Delete(string dir)
    {
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    // 표본 정의 + ON-LINE REMOTE + CE·알람 보고 모두 켬 + 스풀링 켬(dir이 null이면 끔)
    static GemEquipment Eq(string? dir, Action<GemEquipment>? more = null) => GemKit.Equipment(e =>
    {
        GemKit.Sample(e);
        e.InitialControlState = GemControlState.OnlineRemote;
        e.EventsEnabledByDefault = e.AlarmsEnabledByDefault = true;
        e.SpoolDirectory = dir;
        more?.Invoke(e);
    });

    // GemKit.Log + 스풀 상태 변화("Spool 이전->지금[ full]")
    static ConcurrentQueue<string> Log(GemEquipment eq)
    {
        var log = GemKit.Log(eq);
        eq.SpoolStateChanged += (s, e) => log.Enqueue($"Spool {e.Previous}->{e.Current}{(e.IsFull ? " full" : "")}");
        return log;
    }

    // Host가 S2F43으로 스풀 대상 Stream을 정한다(각 Stream의 모든 Function). 받아들여야 한다
    static async Task SetTargets(FakeGemHost host, params int[] streams)
    {
        var body = string.Concat(streams.Select(s => $" <L <U1 {s}> <L>>"));
        Assert.Equal("S2F44 <L [2] <B 0x00> <L [0]>>", await host.Ask($"S2F43 W <L{body}>"));
    }

    // 연결을 끊고 NOT COMMUNICATING까지 기다린다
    static async Task Disconnect(GemEquipment eq, FakeGemHost host)
    {
        host.Dispose();
        await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.NotCommunicating, 5000);
    }

    // 새 가짜 Host로 다시 접속해 장비 S1F13으로 다시 수립한다
    static async Task<FakeGemHost> Reconnect(GemEquipment eq)
    {
        var host = new FakeGemHost(GemKit.Port(eq));
        host.Start();
        host.Next(1, 13, 5000);
        await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.Communicating, 5000);
        return host;
    }

    static ulong Ceid(SecsMessage s6f11) => s6f11.Item!.Items[1].GetUInt64();

    [Fact]
    public async Task Disconnect_QueuedEventsAreSpooled_NoSendFailed()
    {
        var dir = TempDir();
        try
        {
            using var eq = Eq(dir);
            var host = await GemKit.Communicating(eq, h => h.Hold(6, 11));
            await SetTargets(host, 6);
            var log = Log(eq);
            for (int i = 0; i < 3; i++) Assert.True(eq.TriggerEvent(5002));
            Assert.True(host.Held.TryTake(out _, 3000));      // 하나는 응답 대기, 둘은 대기열
            await Disconnect(eq, host);
            await TestUtil.Until(() => GemKit.Only(log, "Spool").Length >= 1, 5000);
            await Task.Delay(300);
            Assert.Empty(GemKit.Only(log, "SendFailed"));
            Assert.Equal(4, eq.SpoolCount);                   // 3 + SpoolingActivated
            Assert.Equal(GemSpoolState.NoOutput, eq.SpoolState);
            Assert.False(eq.IsSpoolFull);
            Assert.Equal(["Comm Communicating->NotCommunicating", "Spool Inactive->NoOutput"], [.. log.Where(l => !l.StartsWith("Error", StringComparison.Ordinal))]);
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task S1F13Failure_Activates()
    {
        var dir = TempDir();
        try
        {
            using var eq = Eq(dir);
            var log = Log(eq);
            using var host = await GemKit.Connect(eq, h => h.Reply(1, 13, "<L <B 1> <L>>"));
            host.Next(1, 13);
            await TestUtil.Until(() => eq.SpoolState == GemSpoolState.NoOutput, 5000);
            Assert.Equal(GemCommunicationState.NotCommunicating, eq.CommunicationState);
            await TestUtil.Until(() => GemKit.Only(log, "Spool").Length >= 1, 3000);
            Assert.Equal(["Spool Inactive->NoOutput"], GemKit.Only(log, "Spool"));
            Assert.Empty(GemKit.Only(log, "Comm"));
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task NotTarget_IsDiscardedAsBefore()
    {
        var dir = TempDir();
        try
        {
            using var eq = Eq(dir);
            var host = await GemKit.Communicating(eq);
            await SetTargets(host, 5);
            var log = Log(eq);
            await Disconnect(eq, host);
            await TestUtil.Until(() => eq.SpoolState == GemSpoolState.NoOutput, 5000);
            Assert.False(eq.TriggerEvent(5002));
            Assert.True(eq.SetAlarm(1));                      // S5F1은 스풀, 알람 CE(S6F11)는 버림
            await TestUtil.Until(() => GemKit.Only(log, "SendFailed").Length >= 3, 3000);
            await Task.Delay(200);
            // SpoolingActivated CE·5002·알람 CE: 대상 아님 → 지금처럼 NotCommunicating
            Assert.Equal(["SendFailed S6F11 NotCommunicating", "SendFailed S6F11 NotCommunicating", "SendFailed S6F11 NotCommunicating"], GemKit.Only(log, "SendFailed"));
            Assert.Equal(1, eq.SpoolCount);
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task EnableSpoolingFalse_NoActivation()
    {
        var dir = TempDir();
        try
        {
            using var eq = Eq(dir);
            var host = await GemKit.Communicating(eq);
            await SetTargets(host, 6);
            Assert.Equal("S2F16 <B 0x00>", await host.Ask("S2F15 W <L <L <U4 1103> <BOOLEAN F>>>"));
            var log = Log(eq);
            await Disconnect(eq, host);
            await Task.Delay(300);
            Assert.Equal(GemSpoolState.Inactive, eq.SpoolState);
            Assert.False(eq.TriggerEvent(5002));
            await TestUtil.Until(() => GemKit.Only(log, "SendFailed").Length >= 1, 3000);
            Assert.Equal(["SendFailed S6F11 NotCommunicating"], GemKit.Only(log, "SendFailed"));
            Assert.Equal(0, eq.SpoolCount);
            Assert.Empty(GemKit.Only(log, "Spool"));
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task EnableSpoolingTurnedOffWhileActive_StaysActive()
    {
        var dir = TempDir();
        try
        {
            using var eq = Eq(dir);
            var host = await GemKit.Communicating(eq);
            await SetTargets(host, 6);
            await Disconnect(eq, host);
            await TestUtil.Until(() => eq.SpoolState == GemSpoolState.NoOutput, 5000);
            using var host2 = await Reconnect(eq);
            Assert.Equal("S2F16 <B 0x00>", await host2.Ask("S2F15 W <L <L <U4 1103> <BOOLEAN F>>>"));
            Assert.Equal(GemSpoolState.NoOutput, eq.SpoolState);
            int before = eq.SpoolCount;
            Assert.True(eq.TriggerEvent(5002));               // 여전히 ACTIVE → 스풀 끝에
            Assert.Equal(before + 1, eq.SpoolCount);
            host2.NoneOf(6);
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task Offline_NothingSpooled()
    {
        var dir = TempDir();
        try
        {
            using var eq = Eq(dir);
            var host = await GemKit.Communicating(eq);
            await SetTargets(host, 5, 6);
            await Disconnect(eq, host);
            await TestUtil.Until(() => eq.SpoolState == GemSpoolState.NoOutput, 5000);
            Assert.True(eq.GoOffline());                      // EquipmentOffline CE는 OFF-LINE이 되기 전에 만든다
            int count = eq.SpoolCount;
            Assert.False(eq.TriggerEvent(5002));
            Assert.True(eq.SetAlarm(1));                      // 상태만 바뀌고 보내지 않음
            Assert.False(eq.SendTerminalMessage(1, "hi"));
            Assert.Equal(count, eq.SpoolCount);
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task Active_AfterReestablish_NewEventGoesToSpoolNotWire()
    {
        var dir = TempDir();
        try
        {
            using var eq = Eq(dir);
            var host = await GemKit.Communicating(eq);
            await SetTargets(host, 6);
            await Disconnect(eq, host);
            await TestUtil.Until(() => eq.SpoolState == GemSpoolState.NoOutput, 5000);
            using var host2 = await Reconnect(eq);
            int count = eq.SpoolCount;
            Assert.True(eq.TriggerEvent(5002));
            Assert.True(eq.SetAlarm(1));                      // S5F1은 대상 아님 → 실시간, 알람 CE는 스풀
            host2.Next(5, 1);
            host2.NoneOf(6);
            Assert.Equal(count + 2, eq.SpoolCount);
            Assert.Equal(GemSpoolState.NoOutput, eq.SpoolState);
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task Full_DiscardNew()
    {
        var dir = TempDir();
        try
        {
            using var eq = Eq(dir, e => e.MaxSpoolMessages = 3);
            var host = await GemKit.Communicating(eq);
            await SetTargets(host, 6);
            var log = Log(eq);
            var failed = new ConcurrentQueue<GemSendFailedEventArgs>();
            eq.SendFailed += (s, e) => failed.Enqueue(e);
            await Disconnect(eq, host);
            await TestUtil.Until(() => eq.SpoolState == GemSpoolState.NoOutput, 5000);   // SpoolingActivated = 1개째
            for (int i = 0; i < 4; i++) eq.TriggerEvent(5002);                            // 2·3개째는 들어가고 4·5개째는 버림
            Assert.Equal(3, eq.SpoolCount);
            Assert.True(eq.IsSpoolFull);
            await TestUtil.Until(() => failed.Count >= 2, 3000);
            await Task.Delay(200);
            Assert.Equal(2, failed.Count);
            Assert.All(failed, f => Assert.Equal(GemSendFailure.SpoolFull, f.Reason));
            Assert.All(failed, f => Assert.Equal(5002UL, Ceid(f.Message)));             // 버린 것은 새 것
            Assert.Equal(["Spool Inactive->NoOutput", "Spool NoOutput->NoOutput full"], GemKit.Only(log, "Spool"));

            using var host2 = await Reconnect(eq);
            var sv = SecsMessage.ParseSml(await host2.Ask("S1F3 W <L <U4 1009> <U4 1010> <U4 1011>>"));
            Assert.Equal(3UL, sv.Item!.Items[0].GetUInt64());
            Assert.Equal(1UL + 4UL, sv.Item.Items[1].GetUInt64());                       // SpoolingActivated CE + 4번 시도(버린 것 포함)
            Assert.NotEmpty(sv.Item.Items[2].GetString());
            Assert.True(eq.IsSpoolFull);                                                   // 가득 참은 다시 수립돼도 그대로
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task Full_Overwrite_DropsOldest()
    {
        var dir = TempDir();
        try
        {
            using var eq = Eq(dir, e => e.MaxSpoolMessages = 3);
            var host = await GemKit.Communicating(eq);
            await SetTargets(host, 6);
            Assert.Equal("S2F16 <B 0x00>", await host.Ask("S2F15 W <L <L <U4 1105> <BOOLEAN T>>>"));
            var failed = new ConcurrentQueue<GemSendFailedEventArgs>();
            eq.SendFailed += (s, e) => failed.Enqueue(e);
            await Disconnect(eq, host);
            await TestUtil.Until(() => eq.SpoolState == GemSpoolState.NoOutput, 5000);   // [1307]
            Assert.True(eq.TriggerEvent(5001));                                           // [1307 5001]
            Assert.True(eq.TriggerEvent(5002));                                           // [1307 5001 5002]
            Assert.True(eq.TriggerEvent(5002));                                           // 1307을 지움
            Assert.True(eq.TriggerEvent(5002));                                           // 5001을 지움
            Assert.Equal(3, eq.SpoolCount);
            Assert.True(eq.IsSpoolFull);
            await TestUtil.Until(() => failed.Count >= 2, 3000);
            await Task.Delay(200);
            Assert.Equal([1307UL, 5001UL], failed.Select(f => Ceid(f.Message)).ToArray());
            Assert.All(failed, f => Assert.Equal(GemSendFailure.SpoolFull, f.Reason));
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task S2F43_Table()
    {
        var dir = TempDir();
        try
        {
            using var eq = Eq(dir);
            var host = await GemKit.Communicating(eq);
            Assert.Equal("", eq.SpoolTargets);                                           // 시작 때 대상 없음
            // 교체: S5 전부 + S6F11만(n = 0 = 전부)
            Assert.Equal("S2F44 <L [2] <B 0x00> <L [0]>>", await host.Ask("S2F43 W <L <L <U1 5> <L>> <L <U1 6> <L <U1 11>>>>"));
            Assert.Equal("S5 S6F11", eq.SpoolTargets);
            Assert.Equal("S2F44 <L [2] <B 0x00> <L [0]>>", await host.Ask("S2F43 W <L <L <U1 10> <L <U1 1>>>>"));
            Assert.Equal("S10F1", eq.SpoolTargets);                                      // 전체를 바꿈
            // 오류 행렬: S1 → 1, S9 → 1, S3(지원 안 함) → 2, 짝수 Function → 4. 아무것도 바꾸지 않음
            Assert.Equal(
                "S2F44 <L [2] <B 0x01> <L [4] <L [3] <U1 1> <B 0x01> <L [0]>> <L [3] <U1 9> <B 0x01> <L [0]>> <L [3] <U1 3> <B 0x02> <L [0]>> <L [3] <U1 6> <B 0x04> <L [1] <U1 12>>>>>",
                await host.Ask("S2F43 W <L <L <U1 1> <L>> <L <U1 9> <L>> <L <U1 3> <L>> <L <U1 6> <L <U1 11> <U1 12>>> <L <U1 5> <L>>>"));
            Assert.Equal("S10F1", eq.SpoolTargets);
            // m = 0: 모두 끔
            Assert.Equal("S2F44 <L [2] <B 0x00> <L [0]>>", await host.Ask("S2F43 W <L>"));
            Assert.Equal("", eq.SpoolTargets);
            // 숫자 포맷은 무엇이든(I4) 받음
            Assert.Equal("S2F44 <L [2] <B 0x00> <L [0]>>", await host.Ask("S2F43 W <L <L <I4 6> <L <I4 11>>>>"));
            Assert.Equal("S6F11", eq.SpoolTargets);
            // ACTIVE 중에도 받아들임
            await Disconnect(eq, host);
            await TestUtil.Until(() => eq.SpoolState == GemSpoolState.NoOutput, 5000);
            using var host2 = await Reconnect(eq);
            Assert.Equal("S2F44 <L [2] <B 0x00> <L [0]>>", await host2.Ask("S2F43 W <L <L <U1 5> <L>>>"));
            Assert.Equal("S5", eq.SpoolTargets);
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task S2F43_BadStructure_S9F7()
    {
        var dir = TempDir();
        try
        {
            using var eq = Eq(dir);
            using var host = await GemKit.Communicating(eq);
            await SetTargets(host, 6);
            Assert.Equal(7, (await Assert.ThrowsAsync<SecsErrorException>(() => host.SendAsync("S2F43 W <A \"x\">"))).Function);
            Assert.Equal("S6", eq.SpoolTargets);
        }
        finally { Delete(dir); }
    }

    // 스풀링 꺼짐: 같은 끊김 시나리오가 6G.6 E2a 순서 그대로(버림 알림 → 상태 변화), 스풀 알림 없음
    [Fact]
    public async Task Off_RegressionOrder_UnchangedE2a()
    {
        using var eq = Eq(null);
        var host = await GemKit.Communicating(eq, h => h.Hold(6, 11));
        var log = Log(eq);
        for (int i = 0; i < 3; i++) Assert.True(eq.TriggerEvent(5002));
        Assert.True(host.Held.TryTake(out _, 3000));
        host.Dispose();
        await TestUtil.Until(() => GemKit.Only(log, "Comm").Length >= 1, 5000);
        await Task.Delay(500);
        Assert.Equal(["SendFailed S6F11 NotCommunicating", "SendFailed S6F11 NotCommunicating", "SendFailed S6F11 NotCommunicating", "Comm Communicating->NotCommunicating"], [.. log]);
        Assert.Equal(GemSpoolState.Inactive, eq.SpoolState);
        Assert.Equal(0, eq.SpoolCount);
    }
}
