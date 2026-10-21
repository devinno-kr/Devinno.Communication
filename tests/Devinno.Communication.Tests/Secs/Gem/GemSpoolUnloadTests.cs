using System.Collections.Concurrent;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Secs.Hsms;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 스풀 내보내기(Plan 14 Task 5): S6F23·스풀 머리 송신·지우기 규칙·MaxSpoolTransmit·지우기(PURGE)·전이 3·6·6′·재시작 복원(전이 7·8)
public class GemSpoolUnloadTests
{
    const string Accepted = "S6F24 <B 0x00>", Busy = "S6F24 <B 0x01>", NoData = "S6F24 <B 0x02>";

    static string TempDir() => Path.Combine(Path.GetTempPath(), "devinno-spool-" + Guid.NewGuid());

    static void Delete(string dir)
    {
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    // 표본 정의 + ON-LINE REMOTE + CE·알람 보고 모두 켬 + 스풀링 켬
    static GemEquipment Eq(string dir, Action<GemEquipment>? more = null) => GemKit.Equipment(e =>
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

    static async Task SetTargets(FakeGemHost host, params int[] streams)
    {
        var body = string.Concat(streams.Select(s => $" <L <U1 {s}> <L>>"));
        Assert.Equal("S2F44 <L [2] <B 0x00> <L [0]>>", await host.Ask($"S2F43 W <L{body}>"));
    }

    static async Task SetEc(FakeGemHost host, string ec) => Assert.Equal("S2F16 <B 0x00>", await host.Ask($"S2F15 W <L <L {ec}>>"));

    static async Task Disconnect(GemEquipment eq, FakeGemHost host)
    {
        host.Dispose();
        await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.NotCommunicating, 5000);
    }

    static async Task<FakeGemHost> Reconnect(GemEquipment eq, Action<FakeGemHost>? setup = null)
    {
        var host = new FakeGemHost(GemKit.Port(eq));
        setup?.Invoke(host);
        host.Start();
        host.Next(1, 13, 5000);
        await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.Communicating, 5000);
        return host;
    }

    // 스풀에 n개를 모은 뒤(SpoolingActivated 1307 하나 + 5002 n-1개) 다시 수립한 Host를 돌려준다. beforeDisconnect는 첫 Host에서 끊기 전에
    static async Task<FakeGemHost> Spooled(GemEquipment eq, int n, Func<FakeGemHost, Task>? beforeDisconnect = null, Action<FakeGemHost>? setup = null)
    {
        var host = await GemKit.Communicating(eq);
        await SetTargets(host, 6);
        if (beforeDisconnect != null) await beforeDisconnect(host);
        await Disconnect(eq, host);
        await TestUtil.Until(() => eq.SpoolState == GemSpoolState.NoOutput, 5000);
        var host2 = await Reconnect(eq, setup);
        for (int i = 1; i < n; i++) Assert.True(eq.TriggerEvent(5002));
        Assert.Equal(n, eq.SpoolCount);
        return host2;
    }

    static ulong Ceid(SecsMessage s6f11) => s6f11.Item!.Items[1].GetUInt64();
    static ulong DataId(SecsMessage s6f11) => s6f11.Item!.Items[0].GetUInt64();
    static SecsMessage S6F12(byte ack) => new(6, 12, false, SecsItem.B(ack));

    [Fact]
    public async Task Transmit_OldestFirst_OneAtATime_ThenDeactivated()
    {
        var dir = TempDir();
        try
        {
            using var eq = Eq(dir);
            using var host = await Spooled(eq, 8, setup: h => h.Hold(6, 11));
            var log = Log(eq);
            Assert.Equal(Accepted, await host.Ask("S6F23 W <U1 0>"));
            var got = new List<SecsMessage>();
            for (int i = 0; i < 9; i++)
            {
                var m = host.Next(6, 11);
                Assert.True(host.Held.TryTake(out var held, 3000));
                host.NoMore(200);                                   // 응답하기 전에는 다음이 오지 않는다
                got.Add(m);
                held!.Reply(S6F12(0));
            }
            Assert.Equal(1307UL, Ceid(got[0]));
            var data = got.Take(8).Select(DataId).ToArray();
            Assert.Equal(data.Order().ToArray(), data);
            Assert.Equal(8, data.Distinct().Count());
            Assert.Equal(1308UL, Ceid(got[8]));                     // SpoolingDeactivated는 실시간(마지막)
            await TestUtil.Until(() => eq.SpoolState == GemSpoolState.Inactive, 3000);
            Assert.Equal(0, eq.SpoolCount);
            await TestUtil.Until(() => GemKit.Only(log, "Spool").Length >= 2, 3000);
            Assert.Equal(["Spool NoOutput->Transmit", "Spool Transmit->Inactive"], GemKit.Only(log, "Spool"));
            Assert.Empty(GemKit.Only(log, "SendFailed"));
            host.NoMore();
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task Transmit_NoReplyMessages_BackToBack()
    {
        var dir = TempDir();
        try
        {
            using var eq = Eq(dir, e => e.EventReplyExpected = false);
            using var host = await Spooled(eq, 8);
            Assert.Equal(Accepted, await host.Ask("S6F23 W <U1 0>"));
            var got = Enumerable.Range(0, 9).Select(_ => host.Next(6, 11)).ToArray();
            Assert.All(got, m => Assert.False(m.ReplyExpected));
            Assert.Equal(1308UL, Ceid(got[8]));
            await TestUtil.Until(() => eq.SpoolState == GemSpoolState.Inactive, 3000);
            Assert.Equal(0, eq.SpoolCount);
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task MaxSpoolTransmit_5of8()
    {
        var dir = TempDir();
        try
        {
            using var eq = Eq(dir);
            using var host = await Spooled(eq, 8, h => SetEc(h, "<U4 1104> <U4 5>"));
            var log = Log(eq);
            Assert.Equal(Accepted, await host.Ask("S6F23 W <U1 0>"));
            for (int i = 0; i < 5; i++) Assert.NotEqual(1308UL, Ceid(host.Next(6, 11)));
            host.NoMore(400);                                       // 개수 도달: CE 없음
            await TestUtil.Until(() => eq.SpoolState == GemSpoolState.NoOutput, 3000);
            Assert.Equal(3, eq.SpoolCount);
            Assert.Equal(Accepted, await host.Ask("S6F23 W <U1 0>"));
            for (int i = 0; i < 3; i++) Assert.NotEqual(1308UL, Ceid(host.Next(6, 11)));
            Assert.Equal(1308UL, Ceid(host.Next(6, 11)));
            await TestUtil.Until(() => eq.SpoolState == GemSpoolState.Inactive, 3000);
            await TestUtil.Until(() => GemKit.Only(log, "Spool").Length >= 4, 3000);
            Assert.Equal(["Spool NoOutput->Transmit", "Spool Transmit->NoOutput", "Spool NoOutput->Transmit", "Spool Transmit->Inactive"], GemKit.Only(log, "Spool"));
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task Transmit_Disconnect_HeadStays_TransmitFailureSpooled()
    {
        var dir = TempDir();
        try
        {
            using var eq = Eq(dir);
            var host = await Spooled(eq, 3, setup: h => h.Hold(6, 11));
            var log = Log(eq);
            Assert.Equal(Accepted, await host.Ask("S6F23 W <U1 0>"));
            var head = host.Next(6, 11);
            Assert.True(host.Held.TryTake(out _, 3000));
            await Disconnect(eq, host);
            await TestUtil.Until(() => eq.SpoolState == GemSpoolState.NoOutput, 3000);
            Assert.Equal(4, eq.SpoolCount);                         // 보내던 것 남음 + SpoolTransmitFailure
            using var host2 = await Reconnect(eq);
            Assert.Equal(Accepted, await host2.Ask("S6F23 W <U1 0>"));
            var got = Enumerable.Range(0, 5).Select(_ => host2.Next(6, 11)).ToArray();
            Assert.Equal(head.ToSml(), got[0].ToSml());             // 같은 바이트부터
            Assert.Equal(1309UL, Ceid(got[3]));                     // SpoolTransmitFailure는 스풀 끝
            Assert.Equal(1308UL, Ceid(got[4]));
            Assert.Empty(GemKit.Only(log, "SendFailed"));
            Assert.Contains("Spool Transmit->NoOutput", GemKit.Only(log, "Spool"));
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task Transmit_T3_SameAsDisconnect()
    {
        var dir = TempDir();
        try
        {
            using var eq = Eq(dir, e => ((SecsHsms)e.Transport).T3 = 600);
            using var host = await Spooled(eq, 3, setup: h => h.Hold(6, 11));
            var log = Log(eq);
            Assert.Equal(Accepted, await host.Ask("S6F23 W <U1 0>"));
            var head = host.Next(6, 11);
            Assert.True(host.Held.TryTake(out _, 3000));           // 응답하지 않음 → T3
            host.NextOf(1, 13, 5000);                               // 통신 실패 → 다시 S1F13(그 사이 전송 계층의 S9F9는 건너뜀)
            await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.Communicating, 5000);
            Assert.Equal(GemSpoolState.NoOutput, eq.SpoolState);
            Assert.Equal(4, eq.SpoolCount);
            host.NoneOf(6, 300);                                    // 다시 수립돼도 자동으로 보내지 않음
            host.Unhold(6, 11);
            Assert.Equal(Accepted, await host.Ask("S6F23 W <U1 0>"));
            var got = Enumerable.Range(0, 5).Select(_ => host.Next(6, 11)).ToArray();
            Assert.Equal(head.ToSml(), got[0].ToSml());
            Assert.Equal(1309UL, Ceid(got[3]));
            Assert.Equal(1308UL, Ceid(got[4]));
            Assert.Empty(GemKit.Only(log, "SendFailed"));
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task Transmit_AckNonzero_RemovedAndReported()
    {
        var dir = TempDir();
        try
        {
            using var eq = Eq(dir);
            using var host = await Spooled(eq, 2, setup: h => h.Reply(6, 11, "<B 1>"));
            var log = Log(eq);
            Assert.Equal(Accepted, await host.Ask("S6F23 W <U1 0>"));
            var got = Enumerable.Range(0, 3).Select(_ => host.Next(6, 11)).ToArray();
            Assert.Equal(1308UL, Ceid(got[2]));
            await TestUtil.Until(() => GemKit.Only(log, "SendFailed").Length >= 3, 3000);
            await Task.Delay(200);
            // 스풀 2개 + 실시간 1308 모두 ack 1
            Assert.Equal(["SendFailed S6F11 NotAcknowledged 1", "SendFailed S6F11 NotAcknowledged 1", "SendFailed S6F11 NotAcknowledged 1"], GemKit.Only(log, "SendFailed"));
            Assert.Equal(0, eq.SpoolCount);
            Assert.Equal(GemSpoolState.Inactive, eq.SpoolState);
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task Transmit_S6F0_Removed_Aborted()
    {
        var dir = TempDir();
        try
        {
            using var eq = Eq(dir);
            using var host = await Spooled(eq, 2, setup: h => h.Reply(6, 11, null));
            var log = Log(eq);
            Assert.Equal(Accepted, await host.Ask("S6F23 W <U1 0>"));
            for (int i = 0; i < 3; i++) host.Next(6, 11);
            await TestUtil.Until(() => GemKit.Only(log, "SendFailed").Length >= 3, 3000);
            await Task.Delay(200);
            Assert.Equal(["SendFailed S6F11 Aborted", "SendFailed S6F11 Aborted", "SendFailed S6F11 Aborted"], GemKit.Only(log, "SendFailed"));
            Assert.Equal(0, eq.SpoolCount);
            Assert.Equal(GemSpoolState.Inactive, eq.SpoolState);
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task Purge_ClearsAndDeactivates_NoPerMessageEvents()
    {
        var dir = TempDir();
        try
        {
            using var eq = Eq(dir);
            using var host = await Spooled(eq, 5);
            var log = Log(eq);
            Assert.Equal(Accepted, await host.Ask("S6F23 W <U1 1>"));
            Assert.Equal(1308UL, Ceid(host.Next(6, 11)));           // 실시간
            host.NoMore();
            Assert.Equal(0, eq.SpoolCount);
            Assert.Equal(GemSpoolState.Inactive, eq.SpoolState);
            await TestUtil.Until(() => GemKit.Only(log, "Spool").Length >= 1, 3000);
            await Task.Delay(200);
            Assert.Equal(["Spool NoOutput->Inactive"], GemKit.Only(log, "Spool"));
            Assert.Empty(GemKit.Only(log, "SendFailed"));
            Assert.Empty(Directory.GetFiles(dir, "*.msg"));
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task S6F23_Rsda()
    {
        var dir = TempDir();
        try
        {
            using var eq = Eq(dir);
            var host = await GemKit.Communicating(eq);
            Assert.Equal(NoData, await host.Ask("S6F23 W <U1 0>"));     // INACTIVE
            Assert.Equal(NoData, await host.Ask("S6F23 W <U1 1>"));
            Assert.Equal(Busy, await host.Ask("S6F23 W <U1 7>"));       // RSDC가 0·1이 아님
            // ACTIVE + 빈 스풀(S6가 대상 아님) + RSDC 0 → 2 그리고 INACTIVE
            await SetTargets(host, 5);
            await Disconnect(eq, host);
            await TestUtil.Until(() => eq.SpoolState == GemSpoolState.NoOutput, 5000);
            var host2 = await Reconnect(eq);
            Assert.Equal(0, eq.SpoolCount);
            Assert.Equal(NoData, await host2.Ask("S6F23 W <U1 0>"));
            await TestUtil.Until(() => eq.SpoolState == GemSpoolState.Inactive, 3000);
            Assert.Equal(1308UL, Ceid(host2.NextOf(6, 11)));
            // TRANSMIT 중 → 1(RSDC 무엇이든)
            await SetTargets(host2, 6);
            await Disconnect(eq, host2);
            await TestUtil.Until(() => eq.SpoolState == GemSpoolState.NoOutput, 5000);
            using var host3 = await Reconnect(eq, h => h.Hold(6, 11));
            Assert.Equal(Busy, await host3.Ask("S6F23 W <U1 7>"));      // NO OUTPUT이어도 잘못된 RSDC는 1, 동작 없음
            Assert.Equal(GemSpoolState.NoOutput, eq.SpoolState);
            Assert.Equal(Accepted, await host3.Ask("S6F23 W <U1 0>"));
            host3.Next(6, 11);
            Assert.True(host3.Held.TryTake(out var held, 3000));
            Assert.Equal(Busy, await host3.Ask("S6F23 W <U1 0>"));
            Assert.Equal(Busy, await host3.Ask("S6F23 W <U1 1>"));
            Assert.Equal(GemSpoolState.Transmit, eq.SpoolState);
            Assert.Equal(1, eq.SpoolCount);
            host3.Unhold(6, 11);
            held!.Reply(S6F12(0));
            Assert.Equal(1308UL, Ceid(host3.Next(6, 11)));
            await TestUtil.Until(() => eq.SpoolState == GemSpoolState.Inactive, 3000);
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task Overwrite_DuringTransmit_KeepsInFlightHead()
    {
        var dir = TempDir();
        try
        {
            using var eq = Eq(dir, e => e.MaxSpoolMessages = 3);
            var host = await GemKit.Communicating(eq);
            await SetTargets(host, 6);
            await SetEc(host, "<U4 1105> <BOOLEAN T>");
            await Disconnect(eq, host);
            await TestUtil.Until(() => eq.SpoolState == GemSpoolState.NoOutput, 5000);   // [1307]
            using var host2 = await Reconnect(eq, h => h.Hold(6, 11));
            Assert.True(eq.TriggerEvent(5001));                                           // [1307 5001]
            Assert.True(eq.TriggerEvent(5002));                                           // [1307 5001 5002]
            var failed = new ConcurrentQueue<GemSendFailedEventArgs>();
            eq.SendFailed += (s, e) => failed.Enqueue(e);
            Assert.Equal(Accepted, await host2.Ask("S6F23 W <U1 0>"));
            var head = host2.Next(6, 11);
            Assert.Equal(1307UL, Ceid(head));
            Assert.True(host2.Held.TryTake(out var held, 3000));
            Assert.True(eq.TriggerEvent(5002));                     // 머리(1307)는 보내는 중 → 그다음(5001)을 지움
            Assert.True(eq.TriggerEvent(5002));                     // 다시 그다음(처음 5002)을 지움
            Assert.Equal(3, eq.SpoolCount);
            await TestUtil.Until(() => failed.Count >= 2, 3000);
            Assert.Equal([5001UL, 5002UL], failed.Select(f => Ceid(f.Message)).ToArray());
            Assert.All(failed, f => Assert.Equal(GemSendFailure.SpoolFull, f.Reason));
            host2.Unhold(6, 11);
            held!.Reply(S6F12(0));                                  // 머리는 응답 뒤 정상 제거
            var rest = Enumerable.Range(0, 3).Select(_ => host2.Next(6, 11)).ToArray();
            Assert.Equal([5002UL, 5002UL, 1308UL], rest.Select(Ceid).ToArray());
            await TestUtil.Until(() => eq.SpoolState == GemSpoolState.Inactive, 3000);
            Assert.Equal(0, eq.SpoolCount);
            Assert.False(eq.IsSpoolFull);                           // INACTIVE를 거쳐 가득 참이 풀림
            Assert.Equal(2, failed.Count);
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task GoOffline_DuringTransmit_StopsUnload()
    {
        var dir = TempDir();
        try
        {
            using var eq = Eq(dir);
            using var host = await Spooled(eq, 3, setup: h => h.Hold(6, 11));
            var log = Log(eq);
            Assert.Equal(Accepted, await host.Ask("S6F23 W <U1 0>"));
            host.Next(6, 11);
            Assert.True(host.Held.TryTake(out var held, 3000));
            Assert.True(eq.GoOffline());                            // EquipmentOffline CE(ON-LINE일 때 만듦)는 스풀 끝에
            Assert.Equal(GemSpoolState.NoOutput, eq.SpoolState);
            Assert.Equal(4, eq.SpoolCount);
            host.Unhold(6, 11);
            held!.Reply(S6F12(0));                                  // 보내던 트랜잭션은 끝나면 지움
            await TestUtil.Until(() => eq.SpoolCount == 3, 3000);
            host.NoneOf(6, 400);                                    // 다음은 보내지 않음
            Assert.Equal(GemSpoolState.NoOutput, eq.SpoolState);
            await TestUtil.Until(() => GemKit.Only(log, "Spool").Length >= 2, 3000);
            Assert.Equal(["Spool NoOutput->Transmit", "Spool Transmit->NoOutput"], GemKit.Only(log, "Spool"));
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task DisableCommunication_DuringTransmit_StopsUnload()
    {
        var dir = TempDir();
        try
        {
            using var eq = Eq(dir);
            using var host = await Spooled(eq, 3, setup: h => h.Hold(6, 11));
            var log = Log(eq);
            Assert.Equal(Accepted, await host.Ask("S6F23 W <U1 0>"));
            var head = host.Next(6, 11);
            Assert.True(host.Held.TryTake(out var held, 3000));
            Assert.True(eq.DisableCommunication());
            Assert.Equal(GemSpoolState.NoOutput, eq.SpoolState);
            held!.Reply(S6F12(0));                                  // 결과는 무시: 머리는 남는다
            await Task.Delay(300);
            Assert.Equal(3, eq.SpoolCount);
            await TestUtil.Until(() => GemKit.Only(log, "Spool").Length >= 2, 3000);
            Assert.Equal(["Spool NoOutput->Transmit", "Spool Transmit->NoOutput"], GemKit.Only(log, "Spool"));
            Assert.Empty(GemKit.Only(log, "SendFailed"));
            // 다시 켜고 수립 → 같은 머리부터
            host.Unhold(6, 11);
            Assert.True(eq.EnableCommunication());
            host.Next(1, 13, 5000);
            await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.Communicating, 5000);
            Assert.Equal(Accepted, await host.Ask("S6F23 W <U1 0>"));
            Assert.Equal(head.ToSml(), host.Next(6, 11).ToSml());
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task Restart_RestoresActiveNoOutputAndBytes()
    {
        var dir = TempDir();
        try
        {
            string[] before;
            using (var eq = Eq(dir))
            {
                using var host = await Spooled(eq, 3);
                before = eq.SpoolSml;
                eq.Stop();
            }
            using var eq2 = Eq(dir);
            var log = Log(eq2);
            using var host2 = await GemKit.Communicating(eq2);
            Assert.Equal(GemSpoolState.NoOutput, eq2.SpoolState);
            Assert.Equal("S6", eq2.SpoolTargets);                   // 대상 표도 복원
            var sv = SecsMessage.ParseSml(await host2.Ask("S1F3 W <L <U4 1009> <U4 1010> <U4 1012>>"));
            Assert.Equal(3UL, sv.Item!.Items[0].GetUInt64());
            Assert.True(sv.Item.Items[1].GetUInt64() >= 3);
            Assert.NotEmpty(sv.Item.Items[2].GetString());          // SpoolStartTime 복원
            Assert.Equal(Accepted, await host2.Ask("S6F23 W <U1 0>"));
            var got = Enumerable.Range(0, 3).Select(_ => host2.Next(6, 11).ToSml()).ToArray();
            Assert.Equal(before, got);
            Assert.Equal(1308UL, Ceid(host2.Next(6, 11)));
            await TestUtil.Until(() => eq2.SpoolState == GemSpoolState.Inactive, 3000);
            // 새 메시지는 복원한 파일을 덮어쓰지 않는다(nextSeq = 마지막 + 1): 디스크도 비었다
            await TestUtil.Until(() => Directory.GetFiles(dir, "*.msg").Length == 0, 3000);
            Assert.Equal(["Spool NoOutput->Transmit", "Spool Transmit->Inactive"], GemKit.Only(log, "Spool"));   // 복원은 이벤트 없음
            Assert.Empty(GemKit.Only(log, "Error"));
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task Restart_NewMessagesAppendAfterRestored()
    {
        var dir = TempDir();
        try
        {
            using (var eq = Eq(dir))
            {
                using var host = await Spooled(eq, 2);
                eq.Stop();
            }
            using var eq2 = Eq(dir);
            using var host2 = await GemKit.Communicating(eq2);
            Assert.True(eq2.TriggerEvent(5001));                    // ACTIVE로 복원 → 스풀 끝에
            Assert.Equal(3, eq2.SpoolCount);
            await TestUtil.Until(() => Directory.GetFiles(dir, "*.msg").Length == 3, 3000);
            Assert.Equal(Accepted, await host2.Ask("S6F23 W <U1 0>"));
            Assert.Equal([1307UL, 5002UL, 5001UL, 1308UL], Enumerable.Range(0, 4).Select(_ => Ceid(host2.Next(6, 11))).ToArray());
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task StopDuringTransmit_RestartKeepsHeadAndNoOutput()
    {
        var dir = TempDir();
        try
        {
            SecsMessage head;
            using (var eq = Eq(dir))
            {
                using var host = await Spooled(eq, 3, setup: h => h.Hold(6, 11));
                Assert.Equal(Accepted, await host.Ask("S6F23 W <U1 0>"));
                head = host.Next(6, 11);
                Assert.True(host.Held.TryTake(out _, 3000));
                Assert.Equal(GemSpoolState.Transmit, eq.SpoolState);
                eq.Stop();
            }
            using var eq2 = Eq(dir);
            using var host2 = await GemKit.Communicating(eq2);
            Assert.Equal(GemSpoolState.NoOutput, eq2.SpoolState);   // UNLOAD는 늘 NO OUTPUT
            Assert.Equal(3, eq2.SpoolCount);
            host2.NoneOf(6, 300);
            Assert.Equal(Accepted, await host2.Ask("S6F23 W <U1 0>"));
            Assert.Equal(head.ToSml(), host2.Next(6, 11).ToSml());
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task Start_CorruptSpoolFile_ReportsOnce()
    {
        var dir = TempDir();
        try
        {
            using (var eq = Eq(dir))
            {
                using var host = await Spooled(eq, 2);
                eq.Stop();
            }
            File.WriteAllBytes(Path.Combine(dir, "00000000000000000099.msg"), [0xFF, 0xFF, 0xFF, 0xFF]);
            using var eq2 = Eq(dir);
            var log = Log(eq2);
            eq2.Start();
            await TestUtil.Until(() => GemKit.Only(log, "Error").Length >= 1, 3000);
            await Task.Delay(300);
            Assert.Single(GemKit.Only(log, "Error"));
            Assert.Equal(2, eq2.SpoolCount);
            Assert.Equal(GemSpoolState.NoOutput, eq2.SpoolState);
            Assert.False(File.Exists(Path.Combine(dir, "00000000000000000099.msg")));
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task Start_MessagesWithoutState_RestoredActive()
    {
        var dir = TempDir();
        try
        {
            using (var eq = Eq(dir))
            {
                using var host = await Spooled(eq, 2);
                eq.Stop();
            }
            File.Delete(Path.Combine(dir, "spool.state"));          // 상태 파일이 없거나 깨짐 → 메시지가 이긴다
            using var eq2 = Eq(dir);
            using var host2 = await GemKit.Communicating(eq2);
            Assert.Equal(GemSpoolState.NoOutput, eq2.SpoolState);
            Assert.Equal(2, eq2.SpoolCount);
            var sv = SecsMessage.ParseSml(await host2.Ask("S1F3 W <L <U4 1010>>"));
            Assert.Equal(2UL, sv.Item!.Items[0].GetUInt64());       // max(저장된 값 없음, 복원한 수)
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task StoreWrites_NeverOnDispatcherOrUnderGate()
    {
        var dir = TempDir();
        try
        {
            using var eq = Eq(dir);
            var bad = new ConcurrentQueue<string>();
            int writes = 0;
            eq.Start();
            eq.SpoolStoreForTest!.BeforeWrite = () =>
            {
                Interlocked.Increment(ref writes);
                if (eq.GateHeldByCurrentThread) bad.Enqueue("gate");
                if (eq.OnTransportDispatcherThread) bad.Enqueue("dispatcher");
            };
            var host = new FakeGemHost(GemKit.Port(eq));
            host.Start();
            host.Next(1, 13, 5000);
            await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.Communicating, 5000);
            await SetTargets(host, 6);
            await Disconnect(eq, host);
            await TestUtil.Until(() => eq.SpoolState == GemSpoolState.NoOutput, 5000);
            using var host2 = await Reconnect(eq);
            Assert.True(eq.TriggerEvent(5002));
            Assert.Equal(Accepted, await host2.Ask("S6F23 W <U1 0>"));
            for (int i = 0; i < 3; i++) host2.Next(6, 11);
            await TestUtil.Until(() => eq.SpoolState == GemSpoolState.Inactive, 3000);
            // S2F43 상태·활성화 상태·추가 2·지움 2·비활성화 상태 = 7
            await TestUtil.Until(() => Volatile.Read(ref writes) >= 7, 3000);
            Assert.Empty(bad);
        }
        finally { Delete(dir); }
    }

    // S6F24를 보낸 뒤 동작하기 전에 운전자가 OFF-LINE으로: 내보내기를 시작하지 않고, ON-LINE으로 돌아와도 S6F23 없이 보내지 않는다
    [Fact]
    public async Task S6F23_GoOfflineBetweenReplyAndAction_NoUnload()
    {
        var dir = TempDir();
        try
        {
            using var eq = Eq(dir);
            using var host = await Spooled(eq, 3);
            eq.AfterS6F24ForTest = () => { eq.AfterS6F24ForTest = null; eq.GoOffline(); };
            Assert.Equal(Accepted, await host.Ask("S6F23 W <U1 0>"));
            await TestUtil.Until(() => eq.ControlState == GemControlState.EquipmentOffline, 3000);   // 훅은 S6F24를 보낸 뒤 돈다
            await Task.Delay(200);
            Assert.Equal(GemSpoolState.NoOutput, eq.SpoolState);
            Assert.True(eq.GoOnline());
            await TestUtil.Until(() => eq.ControlState == GemControlState.OnlineRemote, 3000);
            host.NoneOf(6, 500);
            Assert.Equal(GemSpoolState.NoOutput, eq.SpoolState);
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task S6F23_DisableBetweenReplyAndAction_NoUnload()
    {
        var dir = TempDir();
        try
        {
            using var eq = Eq(dir);
            using var host = await Spooled(eq, 3);
            eq.AfterS6F24ForTest = () => { eq.AfterS6F24ForTest = null; eq.DisableCommunication(); };
            Assert.Equal(Accepted, await host.Ask("S6F23 W <U1 0>"));
            await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.Disabled, 3000);
            await Task.Delay(200);
            Assert.Equal(GemSpoolState.NoOutput, eq.SpoolState);
            Assert.True(eq.EnableCommunication());
            host.NextOf(1, 13, 5000);
            await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.Communicating, 5000);
            host.NoneOf(6, 500);
            Assert.Equal(GemSpoolState.NoOutput, eq.SpoolState);
            Assert.Equal(3, eq.SpoolCount);
        }
        finally { Delete(dir); }
    }

    // ACTIVE + 빈 스풀 + RSDC 0인데 그 사이 OFF-LINE: 전이 3도 하지 않는다(SpoolingDeactivated를 실시간으로 보낼 수 없으므로)
    [Fact]
    public async Task S6F23_EmptyGoOfflineBetween_StaysActive()
    {
        var dir = TempDir();
        try
        {
            using var eq = Eq(dir);
            var host = await GemKit.Communicating(eq);
            await SetTargets(host, 5);
            await Disconnect(eq, host);
            await TestUtil.Until(() => eq.SpoolState == GemSpoolState.NoOutput, 5000);
            using var host2 = await Reconnect(eq);
            eq.AfterS6F24ForTest = () => { eq.AfterS6F24ForTest = null; eq.GoOffline(); };
            Assert.Equal(NoData, await host2.Ask("S6F23 W <U1 0>"));
            await Task.Delay(200);
            Assert.Equal(GemSpoolState.NoOutput, eq.SpoolState);
        }
        finally { Delete(dir); }
    }

    // 스풀링 꺼짐: S6F23은 지금처럼 S9F5
    [Fact]
    public async Task Off_S6F23_S9F5()
    {
        using var eq = GemKit.Equipment(e => { GemKit.Sample(e); e.InitialControlState = GemControlState.OnlineRemote; });
        using var host = await GemKit.Communicating(eq);
        Assert.Equal(5, (await Assert.ThrowsAsync<SecsErrorException>(() => host.SendAsync("S6F23 W <U1 0>"))).Function);
    }
}
