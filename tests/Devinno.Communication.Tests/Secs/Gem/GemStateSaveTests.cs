using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 설정 영속화 켜기와 저장 지점(Plan 15 Task 2): StateDirectory, S2F33/35/37·S5F3·S2F15·SetConstant·스위치가 바뀌면 gem.state에 저장
public class GemStateSaveTests
{
    static string TempDir() => Path.Combine(Path.GetTempPath(), "devinno-state-" + Guid.NewGuid());

    static void Delete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { }
    }

    static GemEquipment Eq(string? dir, Action<GemEquipment>? more = null) => GemKit.Equipment(e =>
    {
        GemKit.Sample(e);
        e.InitialControlState = GemControlState.OnlineRemote;
        e.StateDirectory = dir;
        more?.Invoke(e);
    });

    // 장비를 멈춘 뒤 저장 파일을 읽는다
    static GemStateSnapshot? Saved(string dir)
    {
        using var s = GemStateStore.Open(dir);
        return s.State;
    }

    const string DefineReport = "S2F33 W <L <U4 1> <L <L <U4 7001> <L <U4 2001> <U4 3001>>>>>";
    const string LinkReport = "S2F35 W <L <U4 1> <L <L <U4 5001> <L <U4 7001>>>>>";

    [Fact]
    public async Task Off_NoFileWritten()
    {
        var dir = TempDir();
        try
        {
            using var eq = Eq(null);
            using var host = await GemKit.Communicating(eq);
            Assert.Equal("S2F34 <B 0x00>", await host.Ask(DefineReport));
            Assert.Equal("S2F36 <B 0x00>", await host.Ask(LinkReport));
            eq.Stop();
            Assert.False(Directory.Exists(dir));
            Assert.Null(eq.StateDirectory);
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task Host_ReportsLinksEnables_Saved()
    {
        var dir = TempDir();
        try
        {
            using (var eq = Eq(dir))
            {
                using var host = await GemKit.Communicating(eq);
                Assert.Equal("S2F34 <B 0x00>", await host.Ask(DefineReport));
                Assert.Equal("S2F36 <B 0x00>", await host.Ask(LinkReport));
                Assert.Equal("S2F38 <B 0x00>", await host.Ask("S2F37 W <L <BOOLEAN T> <L <U4 5001>>>"));
                eq.Stop();
            }
            var s = Saved(dir)!;
            Assert.Equal((GemId)7001, s.Reports.Single().Rptid);
            Assert.Equal(new GemId[] { 2001, 3001 }, s.Reports.Single().Vids);
            Assert.Equal((GemId)5001, s.Links.Single().Ceid);
            Assert.Equal(new GemId[] { 7001 }, s.Links.Single().Rptids);
            Assert.Contains(((GemId)5001, true), s.Events);
            Assert.Contains(((GemId)5002, false), s.Events);
            Assert.Contains(((GemId)9001, false), s.Events);
            Assert.Contains(((GemId)9002, false), s.Events);
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task RejectedRequest_NotSaved()
    {
        var dir = TempDir();
        try
        {
            using (var eq = Eq(dir))
            {
                using var host = await GemKit.Communicating(eq);
                Assert.Equal("S2F34 <B 0x04>", await host.Ask("S2F33 W <L <U4 1> <L <L <U4 7001> <L <U4 7777>>>>>"));
                eq.Stop();
            }
            Assert.False(File.Exists(Path.Combine(dir, "gem.state")));
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task AlarmEnable_Saved()
    {
        var dir = TempDir();
        try
        {
            using (var eq = Eq(dir))
            {
                using var host = await GemKit.Communicating(eq);
                Assert.Equal("S5F4 <B 0x00>", await host.Ask("S5F3 W <L <B 0x80> <U4 1>>"));
                eq.Stop();
            }
            Assert.Equal(new[] { ((GemId)1, true) }, Saved(dir)!.Alarms);
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task Constants_Saved_HostAndOperator()
    {
        var dir = TempDir();
        try
        {
            using (var eq = Eq(dir))
            {
                using var host = await GemKit.Communicating(eq);
                Assert.Equal("S2F16 <B 0x00>", await host.Ask("S2F15 W <L <L <U4 4001> <F4 90>>>"));
                eq.Stop();
            }
            var s = Saved(dir)!;
            Assert.Equal(SecsItem.F4(90), s.Constants.Single(c => c.Ecid == 4001).Value);
            Assert.Equal(SecsItem.U2(10), s.Constants.Single(c => c.Ecid == 1101).Value);
            Assert.Equal(SecsItem.U1(1), s.Constants.Single(c => c.Ecid == 1102).Value);

            using (var eq = Eq(dir))
            {
                eq.Start();
                eq.SetConstant(4001, SecsItem.F4(70));
                eq.Stop();
            }
            Assert.Equal(SecsItem.F4(70), Saved(dir)!.Constants.Single(c => c.Ecid == 4001).Value);
        }
        finally { Delete(dir); }
    }

    [Fact]
    public void Switch_Saved()
    {
        var dir = TempDir();
        try
        {
            using (var eq = Eq(dir))
            {
                eq.Start();
                Assert.True(eq.SwitchLocal());
                eq.Stop();
            }
            Assert.Equal(GemLocalRemote.Local, Saved(dir)!.Switch);
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task ManyChanges_FileHasLastState()
    {
        var dir = TempDir();
        try
        {
            using (var eq = Eq(dir))
            {
                using var host = await GemKit.Communicating(eq);
                for (var i = 0; i < 200; i++)
                    Assert.Equal("S2F38 <B 0x00>", await host.Ask($"S2F37 W <L <BOOLEAN {(i % 2 == 0 ? "T" : "F")}> <L <U4 5001>>>"));   // 마지막(i = 199) 끔
                eq.Stop();
            }
            Assert.Contains(((GemId)5001, false), Saved(dir)!.Events);
        }
        finally { Delete(dir); }
    }

    [Fact]
    public async Task Response_DoesNotWaitForDisk()
    {
        var dir = TempDir();
        var gate = new ManualResetEventSlim(false);
        try
        {
            using (var eq = Eq(dir))
            {
                using var host = await GemKit.Communicating(eq);
                eq.StateStore!.BeforeWrite = () => gate.Wait(20000);
                Assert.Equal("S2F38 <B 0x00>", await host.Ask("S2F37 W <L <BOOLEAN T> <L <U4 5001>>>"));
                Assert.Equal("S2F38 <B 0x00>", await host.Ask("S2F37 W <L <BOOLEAN F> <L <U4 5001>>>"));
                Assert.Equal("S2F38 <B 0x00>", await host.Ask("S2F37 W <L <BOOLEAN T> <L <U4 5002>>>"));   // 디스크가 막혀 있어도 응답은 T3(3000ms) 안에
                gate.Set();
                eq.Stop();
            }
            var s = Saved(dir)!;
            Assert.Contains(((GemId)5001, false), s.Events);
            Assert.Contains(((GemId)5002, true), s.Events);
        }
        finally { gate.Set(); Delete(dir); }
    }

    [Fact]
    public async Task Start_StateDirectoryInUse_Throws()
    {
        var dir = TempDir();
        try
        {
            using var a = Eq(dir);
            using var host = await GemKit.Communicating(a);
            using var b = Eq(dir);
            Assert.ThrowsAny<IOException>(() => b.Start());
            Assert.False(b.IsStart);
            Assert.StartsWith("S1F2", await host.Ask("S1F1 W"));     // 첫째는 계속 동작
        }
        finally { Delete(dir); }
    }

    [Fact]
    public void Start_SameFolderAsSpool_Works()
    {
        var dir = TempDir();
        try
        {
            using (var eq = Eq(dir, e => e.SpoolDirectory = dir))
            {
                eq.Start();
                Assert.True(eq.IsStart);
                eq.Stop();
            }
            Assert.True(File.Exists(Path.Combine(dir, "gem.lock")));
            Assert.True(File.Exists(Path.Combine(dir, "spool.lock")));
        }
        finally { Delete(dir); }
    }

    [Fact]
    public void StateDirectory_EmptyString_Throws()
    {
        using var eq = GemKit.Equipment();
        Assert.Throws<ArgumentException>(() => eq.StateDirectory = "");
    }

    [Fact]
    public void StateDirectory_ChangeWhileRunning_Throws()
    {
        using var eq = GemKit.Equipment();
        eq.Start();
        Assert.Throws<InvalidOperationException>(() => eq.StateDirectory = "x");
        Assert.Null(eq.StateDirectory);
    }
}
