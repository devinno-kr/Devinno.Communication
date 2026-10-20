using System.Globalization;
using System.Text;

namespace Devinno.Communication.Secs.Data;

// SML 텍스트 ↔ 아이템·메시지. 표기는 secs4net·secsgem 관례(SEMI 표준 없음).
internal static class SecsSml
{
    static readonly (string Name, SecsFormat Format)[] Names =
    [
        ("L", SecsFormat.List), ("B", SecsFormat.Binary), ("BOOLEAN", SecsFormat.Boolean), ("A", SecsFormat.ASCII),
        ("J", SecsFormat.JIS8), ("C2", SecsFormat.C2), ("I1", SecsFormat.I1), ("I2", SecsFormat.I2), ("I4", SecsFormat.I4),
        ("I8", SecsFormat.I8), ("U1", SecsFormat.U1), ("U2", SecsFormat.U2), ("U4", SecsFormat.U4), ("U8", SecsFormat.U8),
        ("F4", SecsFormat.F4), ("F8", SecsFormat.F8),
    ];

    static string NameOf(SecsFormat f) => Array.Find(Names, n => n.Format == f).Name;

    #region Write
    public static string Message(SecsMessage m)
    {
        var sb = new StringBuilder();
        sb.Append('S').Append(m.Stream).Append('F').Append(m.Function);
        if (m.ReplyExpected) sb.Append(" W");
        if (m.Item != null) { sb.Append(' '); Write(sb, m.Item); }
        return sb.ToString();
    }

    public static string Item(SecsItem item)
    {
        var sb = new StringBuilder();
        Write(sb, item);
        return sb.ToString();
    }

    static void Write(StringBuilder sb, SecsItem item)
    {
        sb.Append('<').Append(NameOf(item.Format));
        switch (item.Format)
        {
            case SecsFormat.List:
                sb.Append(" [").Append(item.Count).Append(']');
                foreach (var c in item.Items) { sb.Append(' '); Write(sb, c); }
                break;
            case SecsFormat.ASCII:
                sb.Append(' ');
                WriteAscii(sb, (byte[])item.RawValues);
                break;
            default:
                foreach (var v in item.RawValues) sb.Append(' ').Append(Value(item.Format, v));
                break;
        }
        sb.Append('>');
    }

    static string Value(SecsFormat format, object v) => format switch
    {
        SecsFormat.Binary or SecsFormat.JIS8 or SecsFormat.C2 => $"0x{(byte)v:X2}",
        SecsFormat.Boolean => (bool)v ? "T" : "F",
        SecsFormat.F4 => ((float)v).ToString("R", CultureInfo.InvariantCulture),
        SecsFormat.F8 => ((double)v).ToString("R", CultureInfo.InvariantCulture),
        _ => ((IFormattable)v).ToString(null, CultureInfo.InvariantCulture),
    };

    // 진단용(상한 있음): 앞 maxChars자까지만 그리고 멈춘다. 잘렸으면 "…(생략: 전체 아이템 N개, 본문 M바이트)"를 붙인다.
    // 장비가 보낸 큰 본문을 통째로 그린 뒤 자르지 않는다(값 하나당 몇 글자로 불어나 수백 MB가 될 수 있음). 개수는 그리지 않고 센다.
    public static string Message(SecsMessage m, int maxChars)
    {
        var w = new Bounded(maxChars);
        w.Sb.Append('S').Append(m.Stream).Append('F').Append(m.Function);
        if (m.ReplyExpected) w.Sb.Append(" W");
        if (m.Item != null) { w.Sb.Append(' '); w.Write(m.Item); }
        return w.Finish(m.Item);
    }

    // 진단용(상한 있음): 규칙은 Message(m, maxChars)와 같다
    public static string Item(SecsItem item, int maxChars)
    {
        var w = new Bounded(maxChars);
        w.Write(item);
        return w.Finish(item);
    }

    // 상한까지만 쓰는 Write. 리스트 자식 사이·값 사이·A 바이트 사이마다 상한을 본다(넘은 뒤로는 아무것도 쓰지 않음)
    sealed class Bounded(int max)
    {
        public readonly StringBuilder Sb = new(Math.Min(max, 4096) + 64);
        bool cut;

        bool Full()
        {
            if (Sb.Length >= max) cut = true;
            return cut;
        }

        public void Write(SecsItem item)
        {
            if (Full()) return;
            Sb.Append('<').Append(NameOf(item.Format));
            switch (item.Format)
            {
                case SecsFormat.List:
                    Sb.Append(" [").Append(item.Count).Append(']');
                    var children = item.Items;
                    for (int i = 0; i < children.Count; i++)
                    {
                        if (Full()) return;
                        Sb.Append(' ');
                        Write(children[i]);
                    }
                    break;
                case SecsFormat.ASCII:
                    Sb.Append(' ');
                    if (!Ascii((byte[])item.RawValues)) return;
                    break;
                default:
                    var values = item.RawValues;
                    for (int i = 0; i < values.Length; i++)
                    {
                        if (Full()) return;
                        Sb.Append(' ').Append(Value(item.Format, values.GetValue(i)!));
                    }
                    break;
            }
            if (Full()) return;
            Sb.Append('>');
        }

        // WriteAscii와 같은 표기. 다 썼으면 true
        bool Ascii(byte[] bytes)
        {
            if (bytes.Length == 0) { Sb.Append("\"\""); return true; }
            bool inQuote = false, first = true;
            foreach (var b in bytes)
            {
                if (Full()) return false;
                bool plain = b >= 0x20 && b <= 0x7E && b != (byte)'"';
                if (plain)
                {
                    if (!inQuote) { if (!first) Sb.Append(' '); Sb.Append('"'); inQuote = true; }
                    Sb.Append((char)b);
                }
                else
                {
                    if (inQuote) { Sb.Append('"'); inQuote = false; }
                    if (!first) Sb.Append(' ');
                    Sb.Append("0x").Append(b.ToString("X2", CultureInfo.InvariantCulture));
                }
                first = false;
            }
            if (inQuote) Sb.Append('"');
            return true;
        }

        public string Finish(SecsItem? root)
        {
            if (!cut && Sb.Length <= max) return Sb.ToString();
            Sb.Length = Math.Min(Sb.Length, max);
            long items = 0;
            if (root != null) Count(root, ref items);
            long bytes = root == null ? 0 : SecsCodec.EncodedLength(root);
            Sb.Append("…(생략: 전체 아이템 ").Append(items).Append("개, 본문 ").Append(bytes).Append("바이트)");
            return Sb.ToString();
        }

        static void Count(SecsItem item, ref long items)
        {
            items++;
            if (item.Format != SecsFormat.List) return;
            var children = item.Items;
            for (int i = 0; i < children.Count; i++) Count(children[i], ref items);
        }
    }

    // 출력 가능한 ASCII(0x20~0x7E, 큰따옴표 제외)는 "…"로 묶고 나머지는 0xHH로 쓴다. 빈 문자열은 ""
    static void WriteAscii(StringBuilder sb, byte[] bytes)
    {
        if (bytes.Length == 0) { sb.Append("\"\""); return; }
        bool inQuote = false, first = true;
        foreach (var b in bytes)
        {
            bool plain = b >= 0x20 && b <= 0x7E && b != (byte)'"';
            if (plain)
            {
                if (!inQuote) { if (!first) sb.Append(' '); sb.Append('"'); inQuote = true; }
                sb.Append((char)b);
            }
            else
            {
                if (inQuote) { sb.Append('"'); inQuote = false; }
                if (!first) sb.Append(' ');
                sb.Append($"0x{b:X2}");
            }
            first = false;
        }
        if (inQuote) sb.Append('"');
    }
    #endregion

    #region Parse
    public static SecsMessage ParseMessage(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var r = new Reader(text);
        r.SkipSpace();
        int start = r.Pos;
        if (!r.TryChar('S')) throw r.Error("S{Stream}F{Function}으로 시작해야 합니다");
        int stream = r.Digits("Stream");
        if (!r.TryChar('F')) throw r.Error("F{Function}이 필요합니다");
        int function = r.Digits("Function");
        if (stream > 127) throw new FormatException($"SML 위치 {start}: Stream은 0~127 입니다 ({stream})");
        if (function > 255) throw new FormatException($"SML 위치 {start}: Function은 0~255 입니다 ({function})");
        r.SkipSpace();
        bool w = false;
        if (r.Peek is 'W' or 'w' && !r.IsWordChar(1)) { r.Pos++; w = true; r.SkipSpace(); }
        SecsItem? item = null;
        if (r.Peek == '<') item = ReadItem(r, 0);
        r.SkipSpace();
        if (r.Peek == '.') { r.Pos++; r.SkipSpace(); }
        if (!r.End) throw r.Error("메시지 뒤에 알 수 없는 내용이 있습니다");
        return new SecsMessage(stream, function, w, item);
    }

    public static SecsItem ParseItem(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var r = new Reader(text);
        r.SkipSpace();
        var item = ReadItem(r, 0);
        r.SkipSpace();
        if (!r.End) throw r.Error("아이템 뒤에 알 수 없는 내용이 있습니다");
        return item;
    }

    static SecsItem ReadItem(Reader r, int depth)
    {
        int start = r.Pos;
        if (!r.TryChar('<')) throw r.Error("'<'가 필요합니다");
        if (depth >= SecsCodec.MaxDepth) throw r.Error($"리스트 중첩이 {SecsCodec.MaxDepth}단계를 넘습니다");
        r.SkipSpace();
        int nameAt = r.Pos;
        string name = r.Word();
        var found = Array.FindIndex(Names, n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
        if (found < 0) throw new FormatException($"SML 위치 {nameAt}: 모르는 포맷 이름 \"{name}\" (사용 가능: {string.Join(" ", Names.Select(n => n.Name))})");
        var format = Names[found].Format;
        r.SkipSpace();
        int? declared = null;
        int countAt = r.Pos;
        if (r.TryChar('['))
        {
            r.SkipSpace();
            declared = r.Digits("개수");
            r.SkipSpace();
            if (!r.TryChar(']')) throw r.Error("']'가 필요합니다");
        }

        SecsItem item;
        try
        {
            item = format switch
            {
                SecsFormat.List => SecsItem.L([.. ReadChildren(r, depth)]),
                SecsFormat.ASCII => SecsItem.A(Encoding.Latin1.GetString(ReadAscii(r))),
                SecsFormat.Boolean => SecsItem.Boolean([.. ReadTokens(r).Select(ParseBool)]),
                SecsFormat.F4 => SecsItem.F4([.. ReadTokens(r).Select(t => (float)ParseFloat(t, true))]),
                SecsFormat.F8 => SecsItem.F8([.. ReadTokens(r).Select(t => ParseFloat(t, false))]),
                _ => Integers(format, ReadTokens(r).Select(t => ParseInteger(t, format)).ToArray()),
            };
        }
        catch (ArgumentException ex)
        {
            throw new FormatException($"SML 위치 {start}: {ex.Message}", ex);
        }
        if (!r.TryChar('>')) throw r.Error("'>'가 필요합니다");
        if (declared != null && declared != item.Count)
            throw new FormatException($"SML 위치 {countAt}: [{declared}] 개수가 실제 {item.Count}과(와) 다릅니다");
        return item;
    }

    static List<SecsItem> ReadChildren(Reader r, int depth)
    {
        var list = new List<SecsItem>();
        while (true)
        {
            r.SkipSpace();
            if (r.Peek != '<') return list;
            list.Add(ReadItem(r, depth + 1));
        }
    }

    // '>' 앞까지 공백으로 나뉜 값 토큰 (값 위치, 텍스트)
    static List<(int At, string Text)> ReadTokens(Reader r)
    {
        var list = new List<(int, string)>();
        while (true)
        {
            r.SkipSpace();
            if (r.End || r.Peek is '>' or '<') return list;
            int at = r.Pos;
            while (!r.End && !char.IsWhiteSpace(r.Peek) && r.Peek is not ('>' or '<')) r.Pos++;
            list.Add((at, r.Text[at..r.Pos]));
        }
    }

    static byte[] ReadAscii(Reader r)
    {
        var bytes = new List<byte>();
        while (true)
        {
            r.SkipSpace();
            if (r.End || r.Peek == '>') return [.. bytes];
            if (r.Peek is '"' or '\'')
            {
                char q = r.Peek;
                int at = r.Pos++;
                int close = r.Text.IndexOf(q, r.Pos);
                if (close < 0) throw new FormatException($"SML 위치 {at}: 닫는 따옴표가 없습니다");
                foreach (var c in r.Text.AsSpan(r.Pos, close - r.Pos))
                {
                    if (c > 0xFF) throw new FormatException($"SML 위치 {at}: A 아이템에는 0~255 문자만 쓸 수 있습니다 ('{c}')");
                    bytes.Add((byte)c);
                }
                r.Pos = close + 1;
                continue;
            }
            int start = r.Pos;
            while (!r.End && !char.IsWhiteSpace(r.Peek) && r.Peek is not ('>' or '"' or '\'')) r.Pos++;
            bytes.Add((byte)ParseInteger((start, r.Text[start..r.Pos]), SecsFormat.Binary));
        }
    }

    static bool ParseBool((int At, string Text) t)
    {
        if (t.Text.Equals("T", StringComparison.OrdinalIgnoreCase) || t.Text.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Text.Equals("F", StringComparison.OrdinalIgnoreCase) || t.Text.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        throw new FormatException($"SML 위치 {t.At}: BOOLEAN 값은 T, F, true, false 입니다 (\"{t.Text}\")");
    }

    static double ParseFloat((int At, string Text) t, bool single)
    {
        if (single) { if (float.TryParse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) return f; }
        else if (double.TryParse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        throw new FormatException($"SML 위치 {t.At}: 실수가 아닙니다 (\"{t.Text}\")");
    }

    // 정수 값 하나. 결과는 포맷 범위 안의 값(부호 있는 포맷은 long, 부호 없는 포맷은 ulong을 담은 decimal)
    static decimal ParseInteger((int At, string Text) t, SecsFormat format)
    {
        var (min, max) = Range(format);
        decimal v;
        if (t.Text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && t.Text.Length > 2
            && ulong.TryParse(t.Text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var h)) v = h;
        else if (decimal.TryParse(t.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var dec)) v = dec;
        else throw new FormatException($"SML 위치 {t.At}: 정수가 아닙니다 (\"{t.Text}\")");
        if (v < min || v > max) throw new FormatException($"SML 위치 {t.At}: {NameOf(format)} 범위 {min}~{max} 밖입니다 ({t.Text})");
        return v;
    }

    static (decimal Min, decimal Max) Range(SecsFormat f) => f switch
    {
        SecsFormat.I1 => (sbyte.MinValue, sbyte.MaxValue),
        SecsFormat.I2 => (short.MinValue, short.MaxValue),
        SecsFormat.I4 => (int.MinValue, int.MaxValue),
        SecsFormat.I8 => (long.MinValue, long.MaxValue),
        SecsFormat.U2 => (0, ushort.MaxValue),
        SecsFormat.U4 => (0, uint.MaxValue),
        SecsFormat.U8 => (0, ulong.MaxValue),
        _ => (0, byte.MaxValue),   // B, J, C2, U1, A의 바이트
    };

    static SecsItem Integers(SecsFormat f, decimal[] v) => f switch
    {
        SecsFormat.Binary => SecsItem.B([.. v.Select(x => (byte)x)]),
        SecsFormat.JIS8 => SecsItem.J([.. v.Select(x => (byte)x)]),
        SecsFormat.C2 => SecsItem.C2([.. v.Select(x => (byte)x)]),
        SecsFormat.U1 => SecsItem.U1([.. v.Select(x => (byte)x)]),
        SecsFormat.U2 => SecsItem.U2([.. v.Select(x => (ushort)x)]),
        SecsFormat.U4 => SecsItem.U4([.. v.Select(x => (uint)x)]),
        SecsFormat.U8 => SecsItem.U8([.. v.Select(x => (ulong)x)]),
        SecsFormat.I1 => SecsItem.I1([.. v.Select(x => (sbyte)x)]),
        SecsFormat.I2 => SecsItem.I2([.. v.Select(x => (short)x)]),
        SecsFormat.I4 => SecsItem.I4([.. v.Select(x => (int)x)]),
        _ => SecsItem.I8([.. v.Select(x => (long)x)]),
    };

    sealed class Reader(string text)
    {
        public string Text { get; } = text;
        public int Pos;
        public bool End => Pos >= Text.Length;
        public char Peek => End ? '\0' : Text[Pos];
        public bool IsWordChar(int offset) => Pos + offset < Text.Length && char.IsLetterOrDigit(Text[Pos + offset]);

        public void SkipSpace() { while (!End && char.IsWhiteSpace(Text[Pos])) Pos++; }

        public bool TryChar(char c)
        {
            if (End || char.ToUpperInvariant(Text[Pos]) != char.ToUpperInvariant(c)) return false;
            Pos++;
            return true;
        }

        public string Word()
        {
            int s = Pos;
            while (!End && char.IsLetterOrDigit(Text[Pos])) Pos++;
            return Text[s..Pos];
        }

        public int Digits(string what)
        {
            int s = Pos;
            while (!End && char.IsAsciiDigit(Text[Pos])) Pos++;
            if (s == Pos || Pos - s > 9) throw new FormatException($"SML 위치 {s}: {what} 숫자가 필요합니다");
            return int.Parse(Text.AsSpan(s, Pos - s), CultureInfo.InvariantCulture);
        }

        public FormatException Error(string detail) => new($"SML 위치 {Pos}: {detail}");
    }
    #endregion
}
