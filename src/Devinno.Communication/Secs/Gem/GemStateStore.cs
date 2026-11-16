using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

// 영속하는 설정(6J.4.1): Host가 정한 리포트·링크, 정의된 모든 CE·알람의 켜짐 여부, 모든 EC 값, LOCAL/REMOTE 스위치. ID는 표의 열쇠 그대로
internal sealed record GemStateSnapshot(
    IReadOnlyList<(GemId Rptid, IReadOnlyList<GemId> Vids)> Reports,
    IReadOnlyList<(GemId Ceid, IReadOnlyList<GemId> Rptids)> Links,
    IReadOnlyList<(GemId Ceid, bool Enabled)> Events,
    IReadOnlyList<(GemId Alid, bool Enabled)> Alarms,
    IReadOnlyList<(GemId Ecid, SecsItem Value)> Constants,
    GemLocalRemote Switch,
    IReadOnlyList<(GemId Vid, byte LimitId, SecsItem Upper, SecsItem Lower)>? Limits = null);

// 설정 영속화 저장소(6J.4.3): gem.state(XML) 하나, gem.lock, 전용 쓰기 스레드 하나.
// Save는 직렬화만 호출 스레드에서 하고(I/O 없음) "다음에 쓸 내용" 칸 하나를 덮어쓴다 — 밀린 쓰기는 최신 것 하나만 남는다.
// 쓰기는 *.tmp → Flush(true) → File.Move(overwrite). 깨진 파일은 gem.state.bad로 옮기고 State = null. Close는 남은 쓰기를 마친 뒤 끝낸다
internal sealed class GemStateStore : IDisposable
{
    const string StateName = "gem.state";
    const string LockName = "gem.lock";
    const string TmpExt = ".tmp";
    const string BadExt = ".bad";
    const string Version = "1";

    readonly string path;
    readonly FileStream lockFile;
    readonly object sync = new();
    readonly Thread thread;
    byte[]? pending;
    bool closing;
    bool closed;
    int failed;

    // 테스트용: 쓰기 스레드가 파일을 쓰기 직전에 부른다
    internal Action? BeforeWrite;

    GemStateStore(string dir, FileStream lockFile)
    {
        path = Path.Combine(dir, StateName);
        this.lockFile = lockFile;
        thread = new Thread(Run) { IsBackground = true, Name = "GemStateStore" };
    }

    public GemStateSnapshot? State { get; private set; }
    public bool WasCorrupt { get; private set; }
    public Action<Exception>? WriteFailed { get; set; }
    public bool IsWriterThread => Thread.CurrentThread == thread;

    // 폴더를 만들고 잠금 파일을 연 뒤(사용 중이면 IOException) gem.state를 읽고 쓰기 스레드를 시작한다
    public static GemStateStore Open(string directory)
    {
        Directory.CreateDirectory(directory);
        var lf = new FileStream(Path.Combine(directory, LockName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var s = new GemStateStore(directory, lf);
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
        TryDelete(path + TmpExt);
        if (!File.Exists(path)) return;
        // 내용이 깨진 경우만 "깨짐"으로 본다. 읽지 못한 경우(다른 프로그램이 잠깐 잡음 등)는 IOException을 그대로 던진다 — 옮기면 설정을 잃는다
        try { State = Parse(XDocument.Load(path)); }
        catch (Exception ex) when (ex is System.Xml.XmlException or FormatException or ArgumentException or OverflowException) { State = null; }
        if (State != null) return;
        WasCorrupt = true;
        File.Move(path, path + BadExt, overwrite: true);
    }

    #region XML
    static GemStateSnapshot Parse(XDocument doc)
    {
        var root = doc.Root;
        if (root == null || root.Name.LocalName != "GemState" || (string?)root.Attribute("Version") != Version) throw new FormatException("gem.state 루트·버전");
        var reports = root.Elements("Report").Select(r => (ReadId(r), (IReadOnlyList<GemId>)[.. r.Elements("Vid").Select(ReadId)])).ToList();
        var links = root.Elements("Link").Select(l => (ReadId(l), (IReadOnlyList<GemId>)[.. l.Elements("Report").Select(ReadId)])).ToList();
        var events = root.Elements("Event").Select(e => (ReadId(e), ReadBool(e))).ToList();
        var alarms = root.Elements("Alarm").Select(a => (ReadId(a), ReadBool(a))).ToList();
        var constants = root.Elements("Constant").Select(c => (ReadId(c), SecsSml.ParseItem((string?)c.Attribute("Value") ?? throw new FormatException("Value 없음")))).ToList();
        var sw = GemLocalRemote.Remote;
        if (root.Element("Switch") is { } s && !Enum.TryParse((string?)s.Attribute("Value"), false, out sw) || !Enum.IsDefined(sw)) throw new FormatException("Switch");
        var limits = root.Elements("Limit").Select(l => (ReadId(l), byte.Parse((string?)l.Attribute("LimitId") ?? throw new FormatException("LimitId 없음"), CultureInfo.InvariantCulture),
            SecsSml.ParseItem((string?)l.Attribute("Upper") ?? throw new FormatException("Upper 없음")), SecsSml.ParseItem((string?)l.Attribute("Lower") ?? throw new FormatException("Lower 없음")))).ToList();
        return new GemStateSnapshot(reports, links, events, alarms, constants, sw, limits);
    }

    // Id(10진 long), IdText(문자열), IdSml(XML에 쓸 수 없는 문자가 든 문자열의 A 아이템 SML) 중 정확히 하나
    static GemId ReadId(XElement e)
    {
        var id = (string?)e.Attribute("Id");
        var text = (string?)e.Attribute("IdText");
        var sml = (string?)e.Attribute("IdSml");
        if ((id != null ? 1 : 0) + (text != null ? 1 : 0) + (sml != null ? 1 : 0) != 1) throw new FormatException("Id/IdText/IdSml");
        if (text != null) return new GemId(text);
        if (sml != null)
        {
            var item = SecsSml.ParseItem(sml);
            return item.Format == SecsFormat.ASCII && item.GetString() is { Length: > 0 } t ? new GemId(t) : throw new FormatException("IdSml");
        }
        return long.TryParse(id, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? new GemId(n) : throw new FormatException("Id");
    }

    static bool ReadBool(XElement e) => (string?)e.Attribute("Enabled") switch
    {
        "true" => true,
        "false" => false,
        _ => throw new FormatException("Enabled"),
    };

    static XAttribute IdAttr(GemId id) => !id.IsText ? new XAttribute("Id", id.Number.ToString(CultureInfo.InvariantCulture))
        : IsXmlText(id.Text!) ? new XAttribute("IdText", id.Text!) : new XAttribute("IdSml", SecsItem.A(id.Text!).ToSml());

    static bool IsXmlText(string s)
    {
        try { System.Xml.XmlConvert.VerifyXmlChars(s); return true; }
        catch (System.Xml.XmlException) { return false; }
    }

    static XElement Enabled(string name, GemId id, bool on) => new(name, IdAttr(id), new XAttribute("Enabled", on ? "true" : "false"));

    static byte[] Serialize(GemStateSnapshot s)
    {
        var root = new XElement("GemState", new XAttribute("Version", Version),
            s.Reports.Select(r => new XElement("Report", IdAttr(r.Rptid), r.Vids.Select(v => new XElement("Vid", IdAttr(v))))),
            s.Links.Select(l => new XElement("Link", IdAttr(l.Ceid), l.Rptids.Select(r => new XElement("Report", IdAttr(r))))),
            s.Events.Select(e => Enabled("Event", e.Ceid, e.Enabled)),
            s.Alarms.Select(a => Enabled("Alarm", a.Alid, a.Enabled)),
            s.Constants.Select(c => new XElement("Constant", IdAttr(c.Ecid), new XAttribute("Value", c.Value.ToSml()))),
            new XElement("Switch", new XAttribute("Value", s.Switch.ToString())),
            (s.Limits ?? []).Select(l => new XElement("Limit", IdAttr(l.Vid), new XAttribute("LimitId", l.LimitId.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("Upper", l.Upper.ToSml()), new XAttribute("Lower", l.Lower.ToSml()))));
        var ms = new MemoryStream();
        using (var w = new StreamWriter(ms, new UTF8Encoding(false))) new XDocument(root).Save(w);
        return ms.ToArray();
    }
    #endregion

    // 다음에 쓸 내용을 바꿔 둔다(밀린 것은 버림). 닫기 시작한 뒤에는 버린다
    public void Save(GemStateSnapshot state)
    {
        // 직렬화 실패는 상태를 바꾼 쪽(gate 안)으로 새어 나가지 않게 쓰기 실패와 같이 한 번만 알린다
        byte[] data;
        try { data = Serialize(state); }
        catch (Exception ex)
        {
            Failed(ex);
            return;
        }
        lock (sync)
        {
            if (closing) return;
            pending = data;
            Monitor.Pulse(sync);
        }
    }

    void Run()
    {
        while (true)
        {
            byte[] data;
            lock (sync)
            {
                while (pending == null && !closing) Monitor.Wait(sync);
                if (pending == null) break;
                data = pending;
                pending = null;
            }
            try
            {
                BeforeWrite?.Invoke();
                WriteAtomic(path, data);
            }
            catch (Exception ex) { Failed(ex); }
        }
        CloseLock();
    }

    // 첫 실패만 알린다(이후는 조용히)
    void Failed(Exception ex)
    {
        if (Interlocked.Exchange(ref failed, 1) != 0) return;
        try { WriteFailed?.Invoke(ex); } catch (Exception) { }
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

    static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch (Exception) { }
    }

    // 남은 쓰기를 마친 뒤(최대 waitMs) 스레드를 끝내고 잠금 파일을 닫는다. 쓰기 스레드 안에서는 기다리지 않는다
    public void Close(int waitMs)
    {
        lock (sync)
        {
            if (closed) return;
            closing = true;
            Monitor.PulseAll(sync);
        }
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
}
