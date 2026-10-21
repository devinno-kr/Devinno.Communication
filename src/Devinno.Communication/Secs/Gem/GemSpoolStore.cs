using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

// 스풀 상태(저장 대상): 켜짐·가득 참·누적 개수·시작/가득 시각·대상 표(빈 함수 목록 = 그 스트림의 모든 함수)
internal sealed record GemSpoolSnapshot(bool Active, bool Full, uint CountTotal, DateTime? StartTime, DateTime? FullTime,
    IReadOnlyList<(byte Stream, IReadOnlyList<byte> Functions)> Selection);

// 비휘발 스풀 저장소(6I.10): 메시지당 파일 하나({seq:D20}.msg), spool.state, spool.lock, 전용 쓰기 스레드 하나.
// Add/Remove/Clear/SaveState는 대기열에만 넣고(호출 스레드 I/O 없음) 쓰기 스레드가 넣은 순서대로 실행한다.
// 쓰기는 *.tmp → Flush(true) → File.Move(overwrite)로 반쯤 쓴 파일이 남지 않게 한다. Close는 남은 일을 다 실행한 뒤 끝낸다(GemLoop.Stop과 다름).
// 시각은 UTC로 다룬다(저장은 ticks, 복원은 Utc).
internal sealed class GemSpoolStore : IDisposable
{
    const string MsgExt = ".msg";
    const string TmpExt = ".tmp";
    const string StateName = "spool.state";
    const string LockName = "spool.lock";
    const int StateVersion = 1;

    readonly string dir;
    readonly FileStream lockFile;
    readonly object sync = new();
    readonly Queue<Action> queue = new();
    readonly Thread thread;
    bool closing;
    bool closed;
    int failed;

    // 테스트용: 쓰기 스레드가 일 하나를 실행하기 직전에 부른다
    internal Action? BeforeWrite;

    GemSpoolStore(string dir, FileStream lockFile)
    {
        this.dir = dir;
        this.lockFile = lockFile;
        thread = new Thread(Run) { IsBackground = true, Name = "GemSpoolStore" };
    }

    public GemSpoolSnapshot? State { get; private set; }
    public IReadOnlyList<(long Seq, SecsMessage Message)> Messages { get; private set; } = [];
    public int SkippedFiles { get; private set; }
    public Action<Exception>? WriteFailed { get; set; }
    public bool IsWriterThread => Thread.CurrentThread == thread;

    // 디렉터리를 만들고 잠금 파일을 연 뒤(사용 중이면 IOException) 저장된 상태·메시지를 읽는다
    public static GemSpoolStore Open(string directory)
    {
        Directory.CreateDirectory(directory);
        var lf = new FileStream(Path.Combine(directory, LockName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var s = new GemSpoolStore(directory, lf);
            s.Load();
            s.thread.Start();
            return s;
        }
        catch
        {
            lf.Dispose();
            throw;
        }
    }

    void Load()
    {
        var list = new List<(long, SecsMessage)>();
        var skipped = 0;
        foreach (var path in Directory.GetFiles(dir))
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(TmpExt, StringComparison.OrdinalIgnoreCase)) { TryDelete(path); skipped++; }
            else if (name.EndsWith(MsgExt, StringComparison.OrdinalIgnoreCase))
            {
                if (TryReadMessage(path, out var seq, out var msg)) list.Add((seq, msg!));
                else { TryDelete(path); skipped++; }
            }
        }
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        Messages = list;

        var statePath = Path.Combine(dir, StateName);
        if (File.Exists(statePath))
        {
            State = TryReadState(statePath);
            if (State == null) { TryDelete(statePath); skipped++; }
        }
        SkippedFiles = skipped;
    }

    static bool TryReadMessage(string path, out long seq, out SecsMessage? msg)
    {
        msg = null;
        if (!long.TryParse(Path.GetFileNameWithoutExtension(path), out seq) || seq < 0) return false;
        try
        {
            var b = File.ReadAllBytes(path);
            if (b.Length < 3 || b[0] > 127 || b[2] > 1) return false;
            var item = SecsCodec.Decode(b.AsSpan(3));
            msg = new SecsMessage(b[0], b[1], b[2] == 1, item);
            return true;
        }
        catch (Exception) { return false; }
    }

    static GemSpoolSnapshot? TryReadState(string path)
    {
        try
        {
            using var r = new BinaryReader(new MemoryStream(File.ReadAllBytes(path)));
            if (r.ReadInt32() != StateVersion) return null;
            var active = r.ReadBoolean();
            var full = r.ReadBoolean();
            var total = r.ReadUInt32();
            var st = r.ReadInt64();
            var ft = r.ReadInt64();
            var n = r.ReadInt32();
            if (n < 0 || n > 256) return null;
            var sel = new List<(byte, IReadOnlyList<byte>)>(n);
            for (var i = 0; i < n; i++)
            {
                var stream = r.ReadByte();
                var fc = r.ReadInt32();
                if (fc < 0 || fc > 256) return null;
                sel.Add((stream, r.ReadBytes(fc)));
            }
            return new GemSpoolSnapshot(active, full, total, FromTicks(st), FromTicks(ft), sel);
        }
        catch (Exception) { return null; }
    }

    static DateTime? FromTicks(long t) => t < 0 ? null : new DateTime(t, DateTimeKind.Utc);

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception) { }
    }

    string MsgPath(long seq) => Path.Combine(dir, seq.ToString("D20") + MsgExt);

    // 대기열에 넣는다. 닫기 시작한 뒤에는 버린다
    void Post(Action work)
    {
        lock (sync)
        {
            if (closing) return;
            queue.Enqueue(work);
            Monitor.Pulse(sync);
        }
    }

    public void Add(long seq, SecsMessage message) => Post(() =>
    {
        var body = SecsCodec.Encode(message.Item);
        var data = new byte[3 + body.Length];
        data[0] = (byte)message.Stream;
        data[1] = (byte)message.Function;
        data[2] = (byte)(message.ReplyExpected ? 1 : 0);
        body.CopyTo(data, 3);
        WriteAtomic(MsgPath(seq), data);
    });

    public void Remove(long seq) => Post(() => File.Delete(MsgPath(seq)));

    // 하나를 못 지워도 나머지는 마저 지운다(첫 실패에서 멈추면 지운 메시지가 재시작 때 되살아남). 첫 예외는 끝에 다시 던져 WriteFailed로 알린다
    public void Clear() => Post(() =>
    {
        Exception? first = null;
        foreach (var p in Directory.GetFiles(dir, "*" + MsgExt))
        {
            try { File.Delete(p); }
            catch (Exception ex) { first ??= ex; }
        }
        if (first != null) throw first;
    });

    public void SaveState(GemSpoolSnapshot state)
    {
        // 직렬화(I/O 아님)는 호출 스레드에서 해 두어, 호출자가 목록을 나중에 바꿔도 영향이 없게 한다
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms))
        {
            w.Write(StateVersion);
            w.Write(state.Active);
            w.Write(state.Full);
            w.Write(state.CountTotal);
            w.Write(state.StartTime?.ToUniversalTime().Ticks ?? -1L);
            w.Write(state.FullTime?.ToUniversalTime().Ticks ?? -1L);
            w.Write(state.Selection.Count);
            foreach (var (stream, fns) in state.Selection)
            {
                w.Write(stream);
                w.Write(fns.Count);
                foreach (var f in fns) w.Write(f);
            }
        }
        var data = ms.ToArray();
        Post(() => WriteAtomic(Path.Combine(dir, StateName), data));
    }

    static void WriteAtomic(string path, byte[] data)
    {
        var tmp = path + TmpExt;
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(data, 0, data.Length);
                fs.Flush(true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    // 남은 일을 다 실행한 뒤(최대 waitMs) 스레드를 끝내고 잠금 파일을 닫는다. 쓰기 스레드 안에서는 기다리지 않는다
    public void Close(int waitMs)
    {
        lock (sync)
        {
            if (closed) return;
            closing = true;
            Monitor.PulseAll(sync);
        }
        // 시간 안에 못 끝나면 잠금은 스레드가 끝날 때 닫는다
        if (IsWriterThread || !thread.IsAlive || thread.Join(waitMs)) CloseLock();
    }

    public void Dispose() => Close(3000);

    void CloseLock()
    {
        lock (sync)
        {
            if (closed) return;
            closed = true;
        }
        lockFile.Dispose();
    }

    void Run()
    {
        while (true)
        {
            Action work;
            lock (sync)
            {
                while (queue.Count == 0 && !closing) Monitor.Wait(sync);
                if (queue.Count == 0) break;
                work = queue.Dequeue();
            }
            try
            {
                BeforeWrite?.Invoke();
                work();
            }
            catch (Exception ex)
            {
                // 첫 실패만 알린다(이후는 조용히)
                if (Interlocked.Exchange(ref failed, 1) == 0)
                {
                    try { WriteFailed?.Invoke(ex); } catch (Exception) { }
                }
            }
        }
        CloseLock();
    }
}
