using System.Globalization;

namespace Devinno.Communication.Internal;

// 이름 있는 쓰기 목록. 연결 전 검증을 거친 복사본을 XML 순서대로 들고 있다.
internal sealed class NamedWriteTable<T> where T : NamedWriteConfig
{
    public static readonly NamedWriteTable<T> Empty = new([]);

    readonly List<T> items;
    readonly Dictionary<string, T> byName;

    NamedWriteTable(List<T> items)
    {
        this.items = items;
        byName = items.ToDictionary(i => i.Name, StringComparer.Ordinal);
    }

    // 오류 메시지용 표시. 예: Write Name="Run" (Name이 비면 Write Id="3")
    public static string Label(NamedWriteConfig c)
        => string.IsNullOrEmpty(c.Name) ? $"Write Id=\"{c.Id.ToString(CultureInfo.InvariantCulture)}\"" : $"Write Name=\"{c.Name}\"";

    // 항목을 검증하고 복사본으로 표를 만든다. validateAddress는 프로토콜별 주소 검사(실패 시 FormatException).
    public static NamedWriteTable<T> Build(IEnumerable<T>? items, Action<T, string> validateAddress)
    {
        var list = new List<T>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items ?? [])
        {
            var label = Label(item);
            var name = item.Name ?? "";
            if (name.Length == 0 || name.Length > 64 || name != name.Trim())
                throw XmlConfig.Invalid(label, "Name", name, "1~64자, 앞뒤 공백 없음");
            if (!names.Add(name)) throw XmlConfig.Invalid(label, "Name", name, "이름이 겹침");
            if (item.TimeoutSpecified) XmlConfig.Range(label, "Timeout", item.Timeout, 1, 60000);
            if (item.RepeatCountSpecified) XmlConfig.Range(label, "RepeatCount", item.RepeatCount, 0, 100);
            validateAddress(item, label);
            list.Add((T)item.Copy());
        }
        return new NamedWriteTable<T>(list);
    }

    // 이름으로 항목을 꺼낸다 (복사본 아님 — 내부 전용).
    public T Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return byName.TryGetValue(name, out var item)
            ? item
            : throw new ArgumentException($"이름 있는 쓰기 \"{name}\"이(가) 없습니다.", nameof(name));
    }

    public bool Contains(string name) => name != null && byName.ContainsKey(name);

    // XML 순서대로 복사본 목록 (ToConfig용)
    public List<T> Snapshot() => items.Select(i => (T)i.Copy()).ToList();

    // 인자 우선, 없으면 XML 값, 둘 다 없으면 null
    public (int? RepeatCount, int? Timeout) Resolve(T item, int? repeatCount, int? timeout)
        => (repeatCount ?? (item.RepeatCountSpecified ? item.RepeatCount : null),
            timeout ?? (item.TimeoutSpecified ? item.Timeout : null));
}
