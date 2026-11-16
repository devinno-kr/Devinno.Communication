using System.Collections.Concurrent;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 설정 영속화 복원(Plan 15 Task 3): 같은 폴더의 새 장비가 Start 때 저장된 설정을 지금 정의에 맞는 것만 복원
public class GemStateRestoreTests
{
    static string TempDir() => Path.Combine(Path.GetTempPath(), "devinno-state-" + Guid.NewGuid());

    static void Delete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { }
    }

    static GemEquipment Eq(string dir, Action<GemEquipment>? more = null) => GemKit.Equipment(e =>
    {
        GemKit.Sample(e);
        e.InitialControlState = GemControlState.OnlineRemote;
        e.StateDirectory = dir;
        more?.Invoke(e);
    });

    // 첫 장비를 띄워 Host가 설정하게 한 뒤 멈춘다
    static async Task Prepare(string dir, Func<GemEquipment, FakeGemHost, Task> host, Action<GemEquipment>? more = null)
    {
        using var eq = Eq(dir, more);
        using var h = await GemKit.Communicating(eq);
        await host(eq, h);
        eq.Stop();
    }

    static string[] Errors(ConcurrentQueue<string> log) => GemKit.Only(log, "Error");

    // 앱 이벤트 스레드가 Start 뒤 알림을 낼 시간을 준다(없어야 하는 경우의 확인용)
    static Task Settle() => Task.Delay(300);

    static async Task Ok(FakeGemHost h, string sml, string reply) => Assert.Equal(reply, await h.Ask(sml));

    const string Define7001 = "S2F33 W <L <U4 1> <L <L <U4 7001> <L <U4 2001> <U4 3001>>>>>";
    const string Link5001 = "S2F35 W <L <U4 1> <L <L <U4 5001> <L <U4 7001>>>>>";
    const string Enable5001 = "S2F37 W <L <BOOLEAN T> <L <U4 5001>>>";

    [Fact]
    public async Task Restore_Full_HostNeedNotReconfigure()
    {
        var dir = TempDir();
        try
        {
            string firstReports = "";
            await Prepare(dir, async (eq, h) =>
            {
                await Ok(h, Define7001, "S2F34 <B 0x00>");
                await Ok(h, Link5001, "S2F36 <B 0x00>");
                await Ok(h, Enable5001, "S2F38 <B 0x00>");
                await Ok(h, "S5F3 W <L <B 0x80> <U4 1>>", "S5F4 <B 0x00>");
                await Ok(h, "S2F15 W <L <L <U4 4001> <F4 90>>>", "S2F16 <B 0x00>");
                eq.TriggerEvent(5001);
                firstReports = h.Next(6, 11).Item!.Items[2].ToSml();
                Assert.True(eq.SwitchLocal());
            });

            using var eq2 = Eq(dir, e => e.InitialControlState = GemControlState.HostOffline);
            var log = GemKit.Log(eq2);
            using var h2 = await GemKit.Communicating(eq2);
            await Ok(h2, "S1F17 W", "S1F18 <B 0x00>");
            await TestUtil.Until(() => eq2.ControlState == GemControlState.OnlineLocal, 5000);
            eq2.TriggerEvent(5001);
            Assert.Equal(firstReports, h2.Next(6, 11).Item!.Items[2].ToSml());
            await Ok(h2, "S2F13 W <L <U4 4001>>", "S2F14 <L [1] <F4 90>>");
            eq2.SetAlarm(1);
            Assert.Equal(1UL, h2.NextOf(5, 1).Item!.Items[1].GetUInt64());
            await Settle();
            Assert.Empty(Errors(log));
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task Restore_OverridesDefaults()
    {
        var dir = TempDir();
        try
        {
            await Prepare(dir, (eq, h) => Ok(h, "S2F37 W <L <BOOLEAN F> <L <U4 5002>>>", "S2F38 <B 0x00>"), e => e.EventsEnabledByDefault = true);
            using var eq2 = Eq(dir, e => e.EventsEnabledByDefault = true);
            using var h2 = await GemKit.Communicating(eq2);
            Assert.True(eq2.TriggerEvent(5001));
            Assert.False(eq2.TriggerEvent(5002));
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task Restore_NewEventAfterUpdate_UsesDefault()
    {
        var dir = TempDir();
        try
        {
            await Prepare(dir, (eq, h) => Ok(h, "S2F37 W <L <BOOLEAN F> <L <U4 5002>>>", "S2F38 <B 0x00>"), e => e.EventsEnabledByDefault = true);
            using var eq2 = Eq(dir, e =>
            {
                e.EventsEnabledByDefault = true;
                e.AddCollectionEvent(new GemCollectionEvent(5003, "NewInUpdate"));
            });
            var log = GemKit.Log(eq2);
            using var h2 = await GemKit.Communicating(eq2);
            Assert.True(eq2.TriggerEvent(5003));     // 파일에 없음 → 기본값(켬)
            Assert.True(eq2.TriggerEvent(5001));
            Assert.False(eq2.TriggerEvent(5002));
            await Settle();
            Assert.Empty(Errors(log));
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task Restore_DefinitionChanged_DropsOnlyMissing()
    {
        var dir = TempDir();
        try
        {
            await Prepare(dir, async (eq, h) =>
            {
                await Ok(h, "S2F33 W <L <U4 1> <L <L <U4 7001> <L <U4 2001>>> <L <U4 7002> <L <U4 3001>>>>>", "S2F34 <B 0x00>");
                await Ok(h, "S2F35 W <L <U4 1> <L <L <U4 5001> <L <U4 7001> <U4 7002>>> <L <U4 5002> <L <U4 7002>>>>>", "S2F36 <B 0x00>");
                await Ok(h, "S2F37 W <L <BOOLEAN T> <L>>", "S2F38 <B 0x00>");
            });
            // 새 버전: DV 3001을 뺐다(CE 5001의 DV 목록에서도)
            using (var eq2 = GemKit.Equipment(e =>
            {
                e.InitialControlState = GemControlState.OnlineRemote;
                e.StateDirectory = dir;
                e.AddStatusVariable(new GemStatusVariable(2001, "Temperature", SecsFormat.F4, "C", SecsItem.F4(25)));
                e.AddStatusVariable(new GemStatusVariable(2002, "Count", SecsFormat.U4));
                e.AddEquipmentConstant(new GemEquipmentConstant(4001, "MaxTemp", SecsItem.F4(80), SecsItem.F4(0), SecsItem.F4(200), "C"));
                e.AddCollectionEvent(new GemCollectionEvent(5001, "ProcessStarted", [2001]));
                e.AddCollectionEvent(new GemCollectionEvent(5002, "ProcessCompleted"));
                e.AddAlarm(new GemAlarm(1, "Over temperature", 2, 9001, 9002));
            }))
            {
                var log = GemKit.Log(eq2);
                using var h2 = await GemKit.Communicating(eq2);
                await TestUtil.Until(() => Errors(log).Length > 0, 5000);
                eq2.TriggerEvent(5001);
                Assert.Equal("<L [1] <L [2] <U4 7001> <L [1] <F4 25>>>>", h2.Next(6, 11).Item!.Items[2].ToSml());
                eq2.TriggerEvent(5002);
                Assert.Equal("<L [0]>", h2.Next(6, 11).Item!.Items[2].ToSml());
                await Settle();
                var errors = Errors(log);
                Assert.Single(errors);
                Assert.Contains("1개", errors[0]);
                eq2.Stop();
            }
            using var s = GemStateStore.Open(dir);
            Assert.Equal((GemId)7001, s.State!.Reports.Single().Rptid);      // 파일도 지금 정의에 맞게 고쳐짐
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task Restore_RemovedAlarmAndConstant_Dropped()
    {
        var dir = TempDir();
        try
        {
            await Prepare(dir, (eq, h) => Ok(h, Define7001, "S2F34 <B 0x00>"));
            using var eq2 = GemKit.Equipment(e =>
            {
                e.InitialControlState = GemControlState.OnlineRemote;
                e.StateDirectory = dir;
                e.AddStatusVariable(new GemStatusVariable(2001, "Temperature", SecsFormat.F4, "C", SecsItem.F4(25)));
                e.AddDataVariable(new GemDataVariable(3001, "LotID", SecsFormat.ASCII));
                e.AddCollectionEvent(new GemCollectionEvent(5001, "ProcessStarted", [2001, 3001]));
                e.AddCollectionEvent(new GemCollectionEvent(5002, "ProcessCompleted"));
            });
            var log = GemKit.Log(eq2);
            using var h2 = await GemKit.Communicating(eq2);
            await TestUtil.Until(() => Errors(log).Length > 0, 5000);
            await Settle();
            var errors = Errors(log);
            Assert.Single(errors);
            Assert.Contains("4개", errors[0]);      // 알람 1, 알람 CE 9001·9002, EC 4001
            await Ok(h2, "S2F33 W <L <U4 1> <L <L <U4 7001> <L <U4 2001>>>>>", "S2F34 <B 0x03>");   // 리포트 7001은 복원됨(이미 있음)
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task Restore_ConstantOutOfNewRange_UsesDefault()
    {
        var dir = TempDir();
        try
        {
            await Prepare(dir, (eq, h) => Ok(h, "S2F15 W <L <L <U4 4001> <F4 150>>>", "S2F16 <B 0x00>"));
            using var eq2 = GemKit.Equipment(e =>
            {
                e.InitialControlState = GemControlState.OnlineRemote;
                e.StateDirectory = dir;
                e.AddEquipmentConstant(new GemEquipmentConstant(4001, "MaxTemp", SecsItem.F4(80), SecsItem.F4(0), SecsItem.F4(100), "C"));
            });
            var log = GemKit.Log(eq2);
            using var h2 = await GemKit.Communicating(eq2);
            await Ok(h2, "S2F13 W <L <U4 4001>>", "S2F14 <L [1] <F4 80>>");
            await TestUtil.Until(() => Errors(log).Length > 0, 5000);
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task Restore_IdFormatChanged_ConvertsOrDrops()
    {
        var dir = TempDir();
        try
        {
            // 숫자 → A: 바꿀 수 있으므로 살아남는다
            await Prepare(dir, async (eq, h) =>
            {
                await Ok(h, "S2F33 W <L <U4 1> <L <L <U4 7001> <L <U4 2001>>>>>", "S2F34 <B 0x00>");
                await Ok(h, Link5001, "S2F36 <B 0x00>");
                await Ok(h, Enable5001, "S2F38 <B 0x00>");
            });
            using (var eq2 = Eq(dir, e => e.VidFormat = e.CeidFormat = e.RptidFormat = SecsFormat.ASCII))
            {
                using var h2 = await GemKit.Communicating(eq2);
                Assert.True(eq2.TriggerEvent(5001));
                Assert.Equal("<L [1] <L [2] <A \"7001\"> <L [1] <F4 25>>>>", h2.Next(6, 11).Item!.Items[2].ToSml());
                eq2.Stop();
            }
        }
        finally { Delete(dir); }

        dir = TempDir();
        try
        {
            // 문자 RPTID → 숫자 포맷: 바꿀 수 없으므로 버린다
            await Prepare(dir, (eq, h) => Ok(h, "S2F33 W <L <U4 1> <L <L <A \"LOT\"> <L <U4 2001>>>>>", "S2F34 <B 0x00>"), e => e.RptidFormat = SecsFormat.ASCII);
            using var eq3 = Eq(dir);
            var log = GemKit.Log(eq3);
            using var h3 = await GemKit.Communicating(eq3);
            await TestUtil.Until(() => Errors(log).Length > 0, 5000);
            Assert.Contains("1개", Errors(log)[0]);
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task Restore_IdNoLongerFitsFormat_Dropped()
    {
        var dir = TempDir();
        try
        {
            await Prepare(dir, (eq, h) => Ok(h, "S2F33 W <L <U4 1> <L <L <U4 7001> <L <U4 2001>>>>>", "S2F34 <B 0x00>"));
            using var eq2 = Eq(dir, e => e.RptidFormat = SecsFormat.U1);     // 7001은 U1에 들어가지 않음
            var log = GemKit.Log(eq2);
            using var h2 = await GemKit.Communicating(eq2);
            await TestUtil.Until(() => Errors(log).Length > 0, 5000);
            Assert.Contains("1개", Errors(log)[0]);
            await Ok(h2, "S2F35 W <L <U4 1> <L <L <U4 5001> <L <U1 7>>>>>", "S2F36 <B 0x05>");
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task Restore_InitialControlStateOnline_WinsOverSwitch()
    {
        var dir = TempDir();
        try
        {
            await Prepare(dir, (eq, h) => { Assert.True(eq.SwitchLocal()); return Task.CompletedTask; });
            using var eq2 = Eq(dir);       // OnlineRemote
            var log = GemKit.Log(eq2);
            eq2.Start();
            Assert.Equal(GemLocalRemote.Remote, eq2.LocalRemoteSwitch);
            await Settle();
            Assert.Empty(Errors(log));
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task Start_CorruptStateFile_StartsWithDefaults()
    {
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "gem.state"), "<GemState");
            using var eq = Eq(dir);
            var log = GemKit.Log(eq);
            using var h = await GemKit.Communicating(eq);
            Assert.True(eq.IsStart);
            await Ok(h, Link5001, "S2F36 <B 0x05>");          // 리포트 없음
            await TestUtil.Until(() => Errors(log).Length > 0, 5000);
            await Settle();
            Assert.Single(Errors(log));
            Assert.True(File.Exists(Path.Combine(dir, "gem.state.bad")));
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task Restore_EmptyReportsSaved_StaysEmpty()
    {
        var dir = TempDir();
        try
        {
            await Prepare(dir, async (eq, h) =>
            {
                await Ok(h, Define7001, "S2F34 <B 0x00>");
                await Ok(h, "S2F33 W <L <U4 1> <L>>", "S2F34 <B 0x00>");
            });
            using var eq2 = Eq(dir);
            using var h2 = await GemKit.Communicating(eq2);
            await Ok(h2, Link5001, "S2F36 <B 0x05>");
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task Restore_DoesNotTouchClockOrAlarmState()
    {
        var dir = TempDir();
        try
        {
            await Prepare(dir, async (eq, h) =>
            {
                var ahead = DateTime.Now.AddHours(1).ToString("yyyyMMddHHmmss") + "00";
                await Ok(h, $"S2F31 W <A \"{ahead}\">", "S2F32 <B 0x00>");
                eq.SetAlarm(1);
            });
            using var eq2 = Eq(dir);
            eq2.Start();
            Assert.True(Math.Abs((eq2.Now - DateTime.Now).TotalMinutes) < 5);
            Assert.False(eq2.ClearAlarm(1));          // 알람 발생 상태는 저장하지 않음(발생 중이 아님)
        }
        finally { Delete(dir); }
    }
}
