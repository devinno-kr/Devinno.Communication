using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 스풀 비휘발 저장소: 메시지당 파일 하나, 상태 파일, 잠금 파일, 전용 쓰기 스레드
public class GemSpoolStoreTests
{
    static string NewDir() => Path.Combine(Path.GetTempPath(), "devinno-spool-" + Guid.NewGuid());

    static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { }
    }

    static SecsMessage Msg(int i, bool w) =>
        new(6, 11, w, SecsItem.L(SecsItem.U4((uint)i), SecsItem.A("EV" + i)));

    [Fact]
    public void AddThenReopen_RestoresInOrder()
    {
        var dir = NewDir();
        try
        {
            var sent = new[] { (5L, Msg(1, true)), (9L, Msg(2, false)), (12L, Msg(3, true)) };
            var s = GemSpoolStore.Open(dir);
            foreach (var (seq, m) in sent) s.Add(seq, m);
            s.Close(3000);

            using var r = GemSpoolStore.Open(dir);
            Assert.Equal(3, r.Messages.Count);
            for (var i = 0; i < 3; i++)
            {
                Assert.Equal(sent[i].Item1, r.Messages[i].Seq);
                Assert.Equal(sent[i].Item2.ToSml(), r.Messages[i].Message.ToSml());
                Assert.Equal(sent[i].Item2.ReplyExpected, r.Messages[i].Message.ReplyExpected);
            }
            Assert.Equal(0, r.SkippedFiles);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Remove_And_Clear_Persist()
    {
        var dir = NewDir();
        try
        {
            var s = GemSpoolStore.Open(dir);
            for (var i = 1; i <= 4; i++) s.Add(i, Msg(i, true));
            s.Remove(2);
            s.Close(3000);
            using (var r = GemSpoolStore.Open(dir))
            {
                Assert.Equal([1L, 3L, 4L], r.Messages.Select(x => x.Seq).ToArray());
                r.Clear();
                r.Close(3000);
            }
            using var r2 = GemSpoolStore.Open(dir);
            Assert.Empty(r2.Messages);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void SaveState_RoundTrip()
    {
        var dir = NewDir();
        try
        {
            var s = GemSpoolStore.Open(dir);
            Assert.Null(s.State);
            var t1 = new DateTime(2026, 10, 21, 1, 2, 3, DateTimeKind.Utc);
            var sel = new (byte, IReadOnlyList<byte>)[] { (6, new byte[] { 11, 13 }), (5, Array.Empty<byte>()) };
            var st = new GemSpoolSnapshot(true, false, 77, t1, null, sel);
            s.SaveState(st);
            s.Close(3000);

            using var r = GemSpoolStore.Open(dir);
            var g = r.State!;
            Assert.True(g.Active);
            Assert.False(g.Full);
            Assert.Equal(77u, g.CountTotal);
            Assert.Equal(t1, g.StartTime);
            Assert.Null(g.FullTime);
            Assert.Equal(2, g.Selection.Count);
            Assert.Equal(6, g.Selection[0].Stream);
            Assert.Equal([(byte)11, (byte)13], g.Selection[0].Functions.ToArray());
            Assert.Equal(5, g.Selection[1].Stream);
            Assert.Empty(g.Selection[1].Functions);

            var full = new GemSpoolSnapshot(false, true, 1, null, t1, []);
            r.SaveState(full);
            r.Close(3000);
            using var r2 = GemSpoolStore.Open(dir);
            Assert.Null(r2.State!.StartTime);
            Assert.Equal(t1, r2.State.FullTime);
            Assert.True(r2.State.Full);
            Assert.Empty(r2.State.Selection);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void SecondStoreOnSameDirectory_Throws()
    {
        var dir = NewDir();
        try
        {
            using var a = GemSpoolStore.Open(dir);
            Assert.Throws<IOException>(() => GemSpoolStore.Open(dir));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Load_SkipsCorruptAndTempFiles()
    {
        var dir = NewDir();
        try
        {
            var s = GemSpoolStore.Open(dir);
            s.Add(3, Msg(1, true));
            s.Close(3000);
            var bad = Path.Combine(dir, "00000000000000000007.msg");
            var tmp = Path.Combine(dir, "x.tmp");
            File.WriteAllBytes(bad, [1, 2]);
            File.WriteAllBytes(tmp, [1, 2, 3]);

            using var r = GemSpoolStore.Open(dir);
            Assert.Equal(2, r.SkippedFiles);
            Assert.Single(r.Messages);
            Assert.Equal(3, r.Messages[0].Seq);
            Assert.False(File.Exists(bad));
            Assert.False(File.Exists(tmp));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Close_FlushesPendingWrites()
    {
        var dir = NewDir();
        try
        {
            var s = GemSpoolStore.Open(dir);
            for (var i = 1; i <= 200; i++) s.Add(i, Msg(i, i % 2 == 0));
            s.Close(10000);
            using var r = GemSpoolStore.Open(dir);
            Assert.Equal(200, r.Messages.Count);
            Assert.Equal(200, r.Messages[^1].Seq);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Add_DoesNoIoOnCallerThread()
    {
        var dir = NewDir();
        var gate = new ManualResetEventSlim(false);
        var entered = new ManualResetEventSlim(false);
        try
        {
            var s = GemSpoolStore.Open(dir);
            s.BeforeWrite = () => { entered.Set(); gate.Wait(10000); };
            s.Add(0, Msg(0, true));
            Assert.True(entered.Wait(3000));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 1; i <= 1000; i++) s.Add(i, Msg(i, true));
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 50, $"Add 1000번에 {sw.ElapsedMilliseconds}ms");
            Assert.False(s.IsWriterThread);

            s.BeforeWrite = null;
            gate.Set();
            s.Close(10000);
            using var r = GemSpoolStore.Open(dir);
            Assert.Equal(1001, r.Messages.Count);
        }
        finally { gate.Set(); Cleanup(dir); }
    }

    [Fact]
    public void WriteFailure_ReportedOnce()
    {
        var dir = NewDir();
        try
        {
            var s = GemSpoolStore.Open(dir);
            var failures = new List<Exception>();
            s.WriteFailed = e => { lock (failures) failures.Add(e); };
            // .msg 자리에 같은 이름 디렉터리를 만들어 File.Move가 실패하게 한다
            Directory.CreateDirectory(Path.Combine(dir, 1L.ToString("D20") + ".msg"));
            Directory.CreateDirectory(Path.Combine(dir, 2L.ToString("D20") + ".msg"));
            s.Add(1, Msg(1, true));
            s.Add(2, Msg(2, true));
            s.Close(3000);
            lock (failures) Assert.Single(failures);
        }
        finally { Cleanup(dir); }
    }
}
