using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 설정 영속화 저장소(Plan 15 Task 1): gem.state XML, gem.lock, 깨진 파일 → gem.state.bad, 최신 스냅숏 하나만 쓰는 전용 스레드
public class GemStateStoreTests
{
    static string NewDir() => Path.Combine(Path.GetTempPath(), "devinno-state-" + Guid.NewGuid());

    static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { }
    }

    static GemStateSnapshot Snap(GemLocalRemote sw = GemLocalRemote.Remote) => new(
        [(new GemId(7001), [new GemId(2001), new GemId(3001)]), (new GemId("RPT-A"), [new GemId("LOT")])],
        [(new GemId(5001), [new GemId(7001), new GemId("RPT-A")])],
        [(new GemId(5001), true), (new GemId(5002), false)],
        [(new GemId(1), false)],
        [(new GemId(4001), SecsItem.F4(90)), (new GemId(4002), SecsItem.U4(1, 2, 3)), (new GemId(4003), SecsItem.L(SecsItem.A("x"), SecsItem.U4(7)))],
        sw);

    static string StatePath(string dir) => Path.Combine(dir, "gem.state");

    [Fact]
    public void SaveThenReopen_RoundTrip()
    {
        var dir = NewDir();
        try
        {
            var s = GemStateStore.Open(dir);
            Assert.Null(s.State);
            s.Save(Snap(GemLocalRemote.Local));
            s.Close(3000);

            using var r = GemStateStore.Open(dir);
            Assert.False(r.WasCorrupt);
            var st = r.State!;
            var want = Snap(GemLocalRemote.Local);
            Assert.Equal(want.Reports.Count, st.Reports.Count);
            for (var i = 0; i < want.Reports.Count; i++)
            {
                Assert.Equal(want.Reports[i].Rptid, st.Reports[i].Rptid);
                Assert.Equal(want.Reports[i].Vids, st.Reports[i].Vids);
            }
            Assert.Equal(want.Links[0].Ceid, st.Links[0].Ceid);
            Assert.Equal(want.Links[0].Rptids, st.Links[0].Rptids);
            Assert.Equal(want.Events, st.Events);
            Assert.Equal(want.Alarms, st.Alarms);
            Assert.Equal(want.Constants.Count, st.Constants.Count);
            for (var i = 0; i < want.Constants.Count; i++)
            {
                Assert.Equal(want.Constants[i].Ecid, st.Constants[i].Ecid);
                Assert.Equal(want.Constants[i].Value, st.Constants[i].Value);
            }
            Assert.Equal(GemLocalRemote.Local, st.Switch);
            Assert.True(st.Reports[1].Rptid.IsText);
            Assert.False(st.Reports[0].Rptid.IsText);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Open_NoFile_StateNull()
    {
        var dir = NewDir();
        try
        {
            using var s = GemStateStore.Open(dir);
            Assert.Null(s.State);
            Assert.False(s.WasCorrupt);
            Assert.True(File.Exists(Path.Combine(dir, "gem.lock")));
        }
        finally { Cleanup(dir); }
    }

    [Theory]
    [InlineData("<GemState")]
    [InlineData("<GemState Version=\"2\" />")]
    [InlineData("<Other Version=\"1\" />")]
    [InlineData("<GemState Version=\"1\"><Event Enabled=\"true\" /></GemState>")]
    [InlineData("<GemState Version=\"1\"><Event Id=\"1\" IdText=\"a\" Enabled=\"true\" /></GemState>")]
    [InlineData("<GemState Version=\"1\"><Event Id=\"x\" Enabled=\"true\" /></GemState>")]
    [InlineData("<GemState Version=\"1\"><Event Id=\"1\" Enabled=\"maybe\" /></GemState>")]
    [InlineData("<GemState Version=\"1\"><Constant Id=\"1\" Value=\"&lt;F4\" /></GemState>")]
    [InlineData("<GemState Version=\"1\"><Switch Value=\"Middle\" /></GemState>")]
    public void Open_CorruptFile_RenamedToBad(string content)
    {
        var dir = NewDir();
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(StatePath(dir), content);
            using var s = GemStateStore.Open(dir);
            Assert.Null(s.State);
            Assert.True(s.WasCorrupt);
            Assert.False(File.Exists(StatePath(dir)));
            Assert.Equal(content, File.ReadAllText(StatePath(dir) + ".bad"));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Open_FileTemporarilyLocked_ThrowsAndKeepsFile()
    {
        var dir = NewDir();
        try
        {
            var s = GemStateStore.Open(dir);
            s.Save(Snap(GemLocalRemote.Local));
            s.Close(3000);
            using (new FileStream(StatePath(dir), FileMode.Open, FileAccess.Read, FileShare.Delete))   // 백신·백업 프로그램이 잠깐 잡고 있음(읽기는 막고 이름 바꾸기는 허용)
                Assert.ThrowsAny<IOException>(() => GemStateStore.Open(dir));
            Assert.False(File.Exists(StatePath(dir) + ".bad"));
            using var r = GemStateStore.Open(dir);                                                    // 풀리면 그대로 읽힘(설정을 잃지 않음)
            Assert.Equal(GemLocalRemote.Local, r.State!.Switch);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Save_TextIdWithControlCharacter_RoundTrips()
    {
        var dir = NewDir();
        try
        {
            var s = GemStateStore.Open(dir);
            s.Save(new GemStateSnapshot([(new GemId("R"), [new GemId(1)])], [], [], [], [], GemLocalRemote.Remote));
            s.Close(3000);
            using var r = GemStateStore.Open(dir);
            Assert.False(r.WasCorrupt);
            Assert.Equal(new GemId("R"), r.State!.Reports.Single().Rptid);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Open_LeftoverTmp_Deleted()
    {
        var dir = NewDir();
        try
        {
            var s = GemStateStore.Open(dir);
            s.Save(Snap(GemLocalRemote.Local));
            s.Close(3000);
            File.WriteAllText(StatePath(dir) + ".tmp", "half");
            using var r = GemStateStore.Open(dir);
            Assert.False(File.Exists(StatePath(dir) + ".tmp"));
            Assert.Equal(GemLocalRemote.Local, r.State!.Switch);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void SecondStoreOnSameDirectory_Throws()
    {
        var dir = NewDir();
        try
        {
            var s = GemStateStore.Open(dir);
            Assert.Throws<IOException>(() => GemStateStore.Open(dir));
            s.Close(3000);
            using var again = GemStateStore.Open(dir);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Save_Coalesces_LastSnapshotWins()
    {
        var dir = NewDir();
        var gate = new ManualResetEventSlim(false);
        var entered = new ManualResetEventSlim(false);
        var writes = 0;
        try
        {
            var s = GemStateStore.Open(dir);
            s.BeforeWrite = () => { Interlocked.Increment(ref writes); entered.Set(); gate.Wait(10000); };
            s.Save(Snap(GemLocalRemote.Local));
            Assert.True(entered.Wait(10000));
            for (var i = 0; i < 100; i++) s.Save(Snap(i % 2 == 0 ? GemLocalRemote.Local : GemLocalRemote.Remote));   // 마지막(i = 99) Remote
            gate.Set();
            s.Close(10000);
            Assert.True(Volatile.Read(ref writes) <= 3, $"쓰기 {writes}번");
            using var r = GemStateStore.Open(dir);
            Assert.Equal(GemLocalRemote.Remote, r.State!.Switch);
        }
        finally { gate.Set(); Cleanup(dir); }
    }

    [Fact]
    public void Save_DoesNoIoOnCallerThread()
    {
        var dir = NewDir();
        var gate = new ManualResetEventSlim(false);
        var entered = new ManualResetEventSlim(false);
        var onWriter = false;
        try
        {
            var s = GemStateStore.Open(dir);
            s.BeforeWrite = () => { onWriter = s.IsWriterThread; entered.Set(); gate.Wait(10000); };
            s.Save(Snap());                     // 쓰기 스레드가 막혀 있어도 바로 돌아온다
            Assert.True(entered.Wait(10000));
            Assert.True(onWriter);
            Assert.False(s.IsWriterThread);
            Assert.False(File.Exists(StatePath(dir)));
            gate.Set();
            s.Close(10000);
            Assert.True(File.Exists(StatePath(dir)));
        }
        finally { gate.Set(); Cleanup(dir); }
    }

    [Fact]
    public void Close_FlushesPendingWrite()
    {
        var dir = NewDir();
        try
        {
            var s = GemStateStore.Open(dir);
            s.Save(Snap(GemLocalRemote.Local));
            s.Close(3000);
            using var r = GemStateStore.Open(dir);
            Assert.Equal(GemLocalRemote.Local, r.State!.Switch);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void WriteFailure_ReportedOnce()
    {
        var dir = NewDir();
        try
        {
            var s = GemStateStore.Open(dir);
            var failures = 0;
            s.WriteFailed = _ => Interlocked.Increment(ref failures);
            Directory.CreateDirectory(StatePath(dir) + ".tmp");     // 임시 파일 자리에 폴더 → 쓰기 실패
            var gate = new ManualResetEventSlim(false);
            for (var i = 0; i < 3; i++)
            {
                gate.Reset();
                s.BeforeWrite = () => gate.Set();
                s.Save(Snap());
                Assert.True(gate.Wait(10000));
                Thread.Sleep(50);
            }
            s.Close(3000);
            Assert.Equal(1, Volatile.Read(ref failures));
        }
        finally { Cleanup(dir); }
    }
}
