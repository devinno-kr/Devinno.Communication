// Shared with Devinno.UI (tools/XmlDocI18n) — keep both copies identical; announce changes in the shared chat.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Devinno.UI.Tools;

/// <summary>
/// Localized IntelliSense for the packages. The compiler's XML (English, from the source comments) is the original;
/// i18n/ko/{Assembly}.xml holds the Korean translation. Every Korean member carries <c>en-hash</c> = hash of the English
/// member it was translated from, so a changed English comment is detected until its Korean text is updated.
/// Code tokens (cref, param/typeparam names, paramref, &lt;c&gt;) must be identical in both languages.
/// </summary>
public static class XmlDocI18n
{
    public const string HashAttr = "en-hash";

    public sealed record Problem(string Member, string Kind, string Detail)
    {
        public override string ToString() => $"{Kind}: {Member} — {Detail}";
    }

    public static Dictionary<string, XElement> Members(XDocument doc) =>
        doc.Root!.Element("members")!.Elements("member").ToDictionary(m => (string)m.Attribute("name")!, m => m);

    /// <summary>Hash of a member's content (whitespace collapsed) — the English original a translation belongs to.</summary>
    public static string Hash(XElement member)
    {
        var text = string.Concat(member.Nodes().Select(n => n.ToString(SaveOptions.DisableFormatting)));
        var norm = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(norm)))[..16].ToLowerInvariant();
    }

    /// <summary>Code tokens that must not change in translation.</summary>
    public static List<string> Tokens(XElement member)
    {
        var list = new List<string>();
        foreach (var e in member.Descendants().Prepend(member))
        {
            foreach (var a in e.Attributes())
                if (a.Name.LocalName is "cref" or "name" or "langword")
                    list.Add($"{e.Name.LocalName}@{a.Name.LocalName}={a.Value}");
            if (e.Name.LocalName == "c")
                list.Add("c=" + e.Value);
        }
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    /// <summary>Problems between the English (compiler) XML and the Korean store. Empty = in sync.</summary>
    public static List<Problem> Check(XDocument en, XDocument ko)
    {
        var problems = new List<Problem>();
        var e = Members(en);
        var k = Members(ko);
        foreach (var (name, em) in e)
        {
            if (!k.TryGetValue(name, out var km))
            {
                problems.Add(new Problem(name, "missing", "no Korean translation"));
                continue;
            }
            if ((string?)km.Attribute(HashAttr) != Hash(em))
                problems.Add(new Problem(name, "stale", "English changed after the Korean translation"));
            else if (!Tokens(em).SequenceEqual(Tokens(km)))
                problems.Add(new Problem(name, "tokens", "cref/name/<c> differ: en [" + string.Join(", ", Tokens(em).Except(Tokens(km))) +
                                                         "] ko [" + string.Join(", ", Tokens(km).Except(Tokens(em))) + "]"));
        }
        foreach (var name in k.Keys.Where(n => !e.ContainsKey(n)))
            problems.Add(new Problem(name, "extra", "member no longer exists in English"));
        return problems;
    }

    /// <summary>
    /// First Korean store: the Korean XML the compiler produced before the source comments became English, stamped with
    /// the hashes of the English members now. Members only in English are left out (they show up as missing).
    /// </summary>
    public static XDocument Seed(XDocument en, XDocument koOriginal)
    {
        var e = Members(en);
        var ko = new XDocument(koOriginal);
        foreach (var m in Members(ko).Values.ToList())
        {
            var name = (string)m.Attribute("name")!;
            if (e.TryGetValue(name, out var em))
                m.SetAttributeValue(HashAttr, Hash(em));
            else
                m.Remove();
        }
        return ko;
    }

    public sealed record Pending(string Name, string English, string? OldKorean);

    /// <summary>Members that need (re)translation, with the English content and the previous Korean (if any) for context.</summary>
    public static List<Pending> PendingList(XDocument en, XDocument ko)
    {
        var k = Members(ko);
        var bad = Check(en, ko).Where(p => p.Kind is "missing" or "stale" or "tokens").Select(p => p.Member).ToHashSet();
        return Members(en).Where(p => bad.Contains(p.Key))
            .Select(p => new Pending(p.Key, Inner(p.Value), k.TryGetValue(p.Key, out var km) ? Inner(km) : null)).ToList();
    }

    /// <summary>Writes translated members (name → Korean inner XML) into the store in English member order, drops removed members.</summary>
    public static XDocument Apply(XDocument en, XDocument ko, IReadOnlyDictionary<string, string> translated)
    {
        var k = Members(ko);
        var members = new XElement("members");
        foreach (var (name, em) in Members(en))
        {
            XElement m;
            if (translated.TryGetValue(name, out var inner))
                m = XElement.Parse($"<member name=\"{System.Security.SecurityElement.Escape(name)}\">{inner}</member>", LoadOptions.PreserveWhitespace);
            else if (k.TryGetValue(name, out var old))
                m = new XElement(old);
            else
                continue; // still missing — Check reports it
            m.SetAttributeValue(HashAttr, translated.ContainsKey(name) ? Hash(em) : (string?)m.Attribute(HashAttr));
            members.Add(m);
        }
        var doc = new XDocument(ko);
        doc.Root!.Element("members")!.ReplaceWith(members);
        return doc;
    }

    public static string Inner(XElement m) => string.Concat(m.Nodes().Select(n => n.ToString(SaveOptions.DisableFormatting)));

    public static XDocument Load(string path) => XDocument.Load(path, LoadOptions.PreserveWhitespace);

    public static void Save(XDocument doc, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        doc.Save(w, SaveOptions.DisableFormatting);
    }

    // ── Markdown pair (README.md English original ↔ README.ko.md Korean translation) ──
    // Sections are split at headings (outside code fences); section 0 is the text before the first heading.
    // The Korean file has a "<!-- en-hash: … -->" line at the top and right after each heading = hash of the English section.

    private const string MdHashPrefix = "<!-- en-hash: ";

    public static List<string> MdSections(string text)
    {
        var sections = new List<string> { "" };
        var fence = false;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
                fence = !fence;
            if (!fence && line.StartsWith('#'))
                sections.Add("");
            sections[^1] += line + "\n";
        }
        return sections;
    }

    private static string HashText(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))))[..16].ToLowerInvariant();

    private static List<string> CodeBlocks(string text)
    {
        var blocks = new List<string>();
        var cur = (StringBuilder?)null;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                if (cur is null)
                    cur = new StringBuilder();
                else
                {
                    blocks.Add(cur.ToString());
                    cur = null;
                }
                continue;
            }
            cur?.Append(line).Append('\n');
        }
        return blocks;
    }

    /// <summary>Korean markdown with the English section hashes stamped (existing hash lines are replaced).</summary>
    public static string MdStamp(string en, string ko)
    {
        var e = MdSections(en);
        var k = MdSections(StripHashes(ko));
        if (e.Count != k.Count)
            throw new InvalidOperationException($"section count differs: en {e.Count}, ko {k.Count}");
        var sb = new StringBuilder();
        for (var i = 0; i < k.Count; i++)
        {
            var lines = k[i].Split('\n');
            var hashLine = MdHashPrefix + HashText(e[i]) + " -->";
            if (i == 0)
                sb.Append(hashLine).Append('\n').Append(k[i]);
            else
                sb.Append(lines[0]).Append('\n').Append(hashLine).Append('\n').Append(string.Join('\n', lines.Skip(1)));
        }
        return sb.ToString();
    }

    private static string StripHashes(string ko) =>
        string.Join('\n', ko.Replace("\r\n", "\n").Split('\n').Where(l => !l.StartsWith(MdHashPrefix, StringComparison.Ordinal)));

    public static List<string> MdCheck(string en, string ko)
    {
        var problems = new List<string>();
        var e = MdSections(en);
        var k = MdSections(ko);
        if (e.Count != k.Count)
            return [$"section count differs: en {e.Count}, ko {k.Count}"];
        for (var i = 0; i < e.Count; i++)
        {
            var stamped = k[i].Split('\n').FirstOrDefault(l => l.StartsWith(MdHashPrefix, StringComparison.Ordinal));
            var title = i == 0 ? "(top)" : e[i].Split('\n')[0];
            if (stamped is null)
                problems.Add($"missing hash: {title}");
            else if (stamped != MdHashPrefix + HashText(e[i]) + " -->")
                problems.Add($"stale: {title} — English changed after the Korean translation");
        }
        if (!CodeBlocks(en).SequenceEqual(CodeBlocks(ko)))
            problems.Add("code blocks differ between the English and Korean files");
        return problems;
    }

    public static string ToJson(List<Pending> list) =>
        JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
