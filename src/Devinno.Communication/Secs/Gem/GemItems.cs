using System.Globalization;
using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

// GEM 아이템 도우미(상태 없음): ID 포맷, ID ↔ 아이템(받을 때 관대하게), 길이 0 아이템, ack, 숫자 변환·비교, XML 값 텍스트.
internal static class GemItems
{
    // ID에 쓸 수 있는 포맷(설정 VidFormat·CeidFormat 등)
    public static readonly SecsFormat[] IdFormats =
        [SecsFormat.U1, SecsFormat.U2, SecsFormat.U4, SecsFormat.U8, SecsFormat.I1, SecsFormat.I2, SecsFormat.I4, SecsFormat.I8, SecsFormat.ASCII];

    static readonly (string Name, SecsFormat Format)[] Names =
    [
        ("L", SecsFormat.List), ("B", SecsFormat.Binary), ("BOOLEAN", SecsFormat.Boolean), ("A", SecsFormat.ASCII),
        ("J", SecsFormat.JIS8), ("C2", SecsFormat.C2), ("I1", SecsFormat.I1), ("I2", SecsFormat.I2), ("I4", SecsFormat.I4),
        ("I8", SecsFormat.I8), ("U1", SecsFormat.U1), ("U2", SecsFormat.U2), ("U4", SecsFormat.U4), ("U8", SecsFormat.U8),
        ("F4", SecsFormat.F4), ("F8", SecsFormat.F8),
    ];

    #region Format
    public static bool IsInteger(SecsFormat f) => f is SecsFormat.I1 or SecsFormat.I2 or SecsFormat.I4 or SecsFormat.I8
        or SecsFormat.U1 or SecsFormat.U2 or SecsFormat.U4 or SecsFormat.U8;

    public static bool IsNumeric(SecsFormat f) => IsInteger(f) || f is SecsFormat.F4 or SecsFormat.F8;

    // 정수 포맷의 범위 (U8의 최대는 long 범위로 자름: ID는 long)
    public static (long Min, long Max) IntegerRange(SecsFormat f) => f switch
    {
        SecsFormat.U1 => (0, byte.MaxValue),
        SecsFormat.U2 => (0, ushort.MaxValue),
        SecsFormat.U4 => (0, uint.MaxValue),
        SecsFormat.U8 => (0, long.MaxValue),
        SecsFormat.I1 => (sbyte.MinValue, sbyte.MaxValue),
        SecsFormat.I2 => (short.MinValue, short.MaxValue),
        SecsFormat.I4 => (int.MinValue, int.MaxValue),
        SecsFormat.I8 => (long.MinValue, long.MaxValue),
        _ => throw new ArgumentException($"{f}는 정수 포맷이 아닙니다."),
    };

    // ID 포맷 검사(설정 setter용)
    public static SecsFormat CheckIdFormat(SecsFormat f, string paramName)
        => Array.IndexOf(IdFormats, f) >= 0 ? f : throw new ArgumentException($"ID 포맷은 U1 U2 U4 U8 I1 I2 I4 I8 A 중 하나입니다. 현재: {f}", paramName);

    // SML 포맷 이름(L B BOOLEAN A J C2 I1 … F8)
    public static string FormatName(SecsFormat f) => Array.Find(Names, n => n.Format == f).Name;

    public static bool TryParseFormatName(string? name, out SecsFormat format)
    {
        foreach (var (n, f) in Names)
            if (n == name) { format = f; return true; }
        format = default;
        return false;
    }

    // 그 포맷의 길이 0 아이템(<L>, <A>, <U4> …)
    public static SecsItem Empty(SecsFormat f) => f == SecsFormat.List ? SecsItem.L() : SecsItem.Raw(f, Array.CreateInstance(SecsItem.ElementType(f), 0));
    #endregion

    #region Id
    // 표의 열쇠: A 포맷이면 숫자 ID도 10진 문자열로, 숫자 포맷이면 그대로(문자열 ID는 숫자 포맷에서 찾지 못함)
    public static GemId Key(GemId id, SecsFormat f) => f == SecsFormat.ASCII && !id.IsText ? new GemId(id.ToString()) : id;

    // ID가 그 포맷으로 표현되는지
    public static bool Fits(GemId id, SecsFormat f)
    {
        try { id.ToItem(f); return true; }
        catch (ArgumentException) { return false; }
    }

    // 받은 ID: 설정이 숫자 포맷이면 정수 포맷 8종 무엇이든, A면 A만. 아니면 FormatException
    public static GemId ReadId(SecsItem? item, SecsFormat f)
    {
        if (item == null) throw new FormatException("ID 아이템이 없습니다.");
        var id = GemId.FromItem(item);
        if (f == SecsFormat.ASCII ? !id.IsText : id.IsText)
            throw new FormatException($"ID 포맷이 {FormatName(f)}인데 {FormatName(item.Format)} 아이템을 받았습니다.");
        return id;
    }

    // <L ID…> 목록
    public static List<GemId> ReadIdList(SecsItem? item, SecsFormat f)
    {
        if (item is not { Format: SecsFormat.List }) throw new FormatException("ID 목록은 리스트여야 합니다.");
        return [.. item.Items.Select(i => ReadId(i, f))];
    }

    // ID 여러 개(S5F5 ALID): 숫자 포맷이면 정수 배열 아이템 하나, A면 A 하나, 또는 <L ID…>(secsgem 방식). 길이 0이면 빈 목록(= 전체)
    public static List<GemId> ReadIdVector(SecsItem? item, SecsFormat f)
    {
        if (item == null) throw new FormatException("ID 아이템이 없습니다.");
        if (item.Format == SecsFormat.List) return ReadIdList(item, f);
        if (item.Count == 0 && (item.Format == SecsFormat.ASCII || IsInteger(item.Format))) return [];
        if (f != SecsFormat.ASCII && IsInteger(item.Format))
        {
            var list = new List<GemId>();
            foreach (var v in item.RawValues)
            {
                if (v is ulong u && u > long.MaxValue) throw new FormatException($"ID {u}는 long 범위를 넘습니다.");
                list.Add(new GemId(Convert.ToInt64(v, CultureInfo.InvariantCulture)));
            }
            return list;
        }
        return [ReadId(item, f)];
    }

    // 보낼 ID 목록 값(EventsEnabled 등): 숫자 포맷이면 그 포맷의 배열 아이템 하나, A면 <L <A>…>
    public static SecsItem IdList(IEnumerable<GemId> ids, SecsFormat f)
    {
        if (f == SecsFormat.ASCII) return SecsItem.L([.. ids.Select(i => i.ToItem(f))]);
        var items = ids.Select(i => i.ToItem(f)).ToList();
        var array = Array.CreateInstance(SecsItem.ElementType(f), items.Count);
        for (int i = 0; i < items.Count; i++) array.SetValue(items[i].RawValues.GetValue(0), i);
        return SecsItem.Raw(f, array);
    }
    #endregion

    #region Ack
    public static SecsItem Ack(byte code) => SecsItem.B(code);

    // 받은 ack: 숫자 포맷(B 포함) 값 하나, 0~255. 아니면 FormatException
    public static byte ReadAck(SecsItem? item)
    {
        if (item == null || item.Format is SecsFormat.List or SecsFormat.ASCII or SecsFormat.JIS8 or SecsFormat.C2 or SecsFormat.Boolean || item.Count != 1)
            throw new FormatException($"ack는 숫자 아이템 하나여야 합니다. 받은 것: {Sml(item)}");
        long v;
        try { v = item.GetInt64(); }
        catch (OverflowException) { throw new FormatException($"ack 값이 범위 밖입니다: {Sml(item)}"); }
        if (v is < 0 or > 255) throw new FormatException($"ack 값은 0~255여야 합니다: {v}");
        return (byte)v;
    }
    #endregion

    #region Numeric
    // 숫자 아이템을 다른 숫자 포맷으로 바꾼다(모든 요소). 포맷이 같으면 그대로. 숫자가 아니거나, 정수 포맷인데 소수가 있거나, 범위를 넘으면 null
    public static SecsItem? ConvertNumeric(SecsItem value, SecsFormat target)
    {
        if (value.Format == target) return value;
        if (!IsNumeric(value.Format) || !IsNumeric(target)) return null;
        var src = value.RawValues;
        var dst = Array.CreateInstance(SecsItem.ElementType(target), src.Length);
        for (int i = 0; i < src.Length; i++)
        {
            var v = src.GetValue(i)!;
            object? converted;
            if (target is SecsFormat.F4 or SecsFormat.F8)
            {
                double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (target == SecsFormat.F4)
                {
                    float fl = (float)d;
                    if (float.IsInfinity(fl) && !double.IsInfinity(d)) return null;
                    converted = fl;
                }
                else converted = d;
            }
            else
            {
                decimal dec;
                if (v is float or double)
                {
                    double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                    if (double.IsNaN(d) || double.IsInfinity(d) || Math.Floor(d) != d || Math.Abs(d) > 1e20) return null;
                    dec = (decimal)d;
                }
                else dec = Convert.ToDecimal(v, CultureInfo.InvariantCulture);
                decimal min = target == SecsFormat.U8 ? 0 : IntegerRange(target).Min;
                decimal max = target == SecsFormat.U8 ? ulong.MaxValue : IntegerRange(target).Max;
                if (dec < min || dec > max) return null;
                converted = Convert.ChangeType(dec, SecsItem.ElementType(target), CultureInfo.InvariantCulture);
            }
            dst.SetValue(converted, i);
        }
        return SecsItem.Raw(target, dst);
    }

    // 값이 변수 정의에 맞는지(S2F15·SetConstant·SetValue·TriggerEvent가 함께 씀): 포맷이 같아야 하고,
    // EC(표준 포함)의 숫자·BOOLEAN 값은 요소 수도 기본값과 같아야 한다(A는 길이 자유). SV·DV는 배열·길이 0 값을 받으므로 포맷만 본다
    public static bool Matches(SecsItem value, GemVar v)
    {
        if (value.Format != v.Format) return false;
        if (v.Kind == GemVarKind.Constant && (IsNumeric(v.Format) || v.Format == SecsFormat.Boolean)) return value.Count == v.Initial.Count;
        return true;
    }

    // 숫자 아이템의 모든 요소가 [min, max] 안인지 (min·max는 null이면 검사 안 함, 첫 값 기준). Min이나 Max가 있으면 NaN은 늘 범위 밖
    public static bool InRange(SecsItem value, SecsItem? min, SecsItem? max)
    {
        bool bounded = min is { Count: > 0 } || max is { Count: > 0 };
        foreach (var v in value.RawValues)
        {
            if (bounded && (v is float f && float.IsNaN(f) || v is double d && double.IsNaN(d))) return false;
            if (min is { Count: > 0 } && Compare(v, min.RawValues.GetValue(0)!) < 0) return false;
            if (max is { Count: > 0 } && Compare(v, max.RawValues.GetValue(0)!) > 0) return false;
        }
        return true;
    }

    static int Compare(object a, object b)
    {
        if (a is float or double || b is float or double)
            return Convert.ToDouble(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(b, CultureInfo.InvariantCulture));
        return Convert.ToDecimal(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(b, CultureInfo.InvariantCulture));
    }
    #endregion

    #region Xml value text
    // XML 값 텍스트 → 아이템. 숫자·BOOLEAN은 값 0~1개(10진수, true/false), A는 그대로, B·J·C2는 0x 바이트 나열, L은 빈 텍스트만. 틀리면 FormatException
    public static SecsItem ParseValue(string text, SecsFormat f)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (f == SecsFormat.ASCII)
        {
            try { return SecsItem.A(text); }
            catch (ArgumentException ex) { throw new FormatException(ex.Message); }
        }
        if (f == SecsFormat.List)
            return text.Length == 0 ? SecsItem.L() : throw new FormatException("L 값은 XML로 쓸 수 없습니다(빈 텍스트만 됨).");
        if (text.Contains('<') || text.Contains('>')) throw new FormatException("값에 < > 문자를 쓸 수 없습니다.");
        SecsItem item;
        try { item = SecsItem.ParseSml($"<{FormatName(f)} {text}>"); }
        catch (FormatException ex) { throw new FormatException($"{FormatName(f)} 값으로 읽을 수 없습니다 ({ex.Message})"); }
        if (f is not (SecsFormat.Binary or SecsFormat.JIS8 or SecsFormat.C2) && item.Count > 1)
            throw new FormatException("배열 값은 XML로 쓸 수 없습니다(값은 1개).");
        return item;
    }

    // 아이템 → XML 값 텍스트. 배열·비어 있지 않은 L처럼 XML로 쓸 수 없으면 null
    public static string? ValueText(SecsItem item)
    {
        switch (item.Format)
        {
            case SecsFormat.ASCII: return item.GetString();
            case SecsFormat.List: return item.Count == 0 ? "" : null;
            case SecsFormat.Binary or SecsFormat.JIS8 or SecsFormat.C2:
                return string.Join(' ', ((byte[])item.RawValues).Select(b => $"0x{b:X2}"));
            case SecsFormat.Boolean:
                return item.Count switch { 0 => "", 1 => item.GetBooleans()[0] ? "true" : "false", _ => null };
            default:
                if (item.Count == 0) return "";
                if (item.Count > 1) return null;
                var sml = item.ToSml();                       // 예: <F4 80>
                return sml[(sml.IndexOf(' ') + 1)..^1];
        }
    }
    #endregion

    #region 진단 문장
    // 진단 문장(ProtocolError·FormatException)에 넣는 SML의 최대 글자 수. 장비가 보낸 큰 본문이 앱 이벤트·예외에 통째로 실리지 않게 한다
    public const int SmlLimit = 2048;

    // 진단 문장에 넣는 문자열 ID의 최대 글자 수
    public const int IdLimit = 128;

    // 앞 SmlLimit자까지만 그린 SML("…(생략: 전체 아이템 N개, 본문 M바이트)"를 붙임). 전체를 그린 뒤 자르지 않는다(큰 본문의 할당 폭증 방지)
    public static string Sml(SecsMessage m) => SecsSml.Message(m, SmlLimit);

    public static string Sml(SecsItem? item) => item == null ? "(본문 없음)" : SecsSml.Item(item, SmlLimit);

    // 진단 문장용 ID 표기: 긴 문자열 ID는 앞 IdLimit자만("…(전체 N자)"를 붙임)
    public static string IdText(GemId id)
    {
        var t = id.Text;
        return t == null || t.Length <= IdLimit ? id.ToString() : $"{t[..IdLimit]}…(전체 {t.Length}자)";
    }
    #endregion
}
