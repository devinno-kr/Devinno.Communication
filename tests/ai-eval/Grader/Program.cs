// AI-evaluation grader for Devinno.Communication.
// Usage: dotnet run --project tests/ai-eval/Grader -- <map-name> <xml-path> [<cs-path>]
// Exit code 0 only if every check passes. Every check prints one PASS/FAIL line.

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

if (args.Length < 2 || args.Length > 3)
{
    Console.WriteLine("Usage: Grader <map-name> <xml-path> [<cs-path>]");
    return 2;
}

Console.OutputEncoding = Encoding.UTF8;
string mapName = args[0];
string xmlPath = args[1];
string? csPath = args.Length == 3 ? args[2] : null;

string repo = FindRepoRoot();
string expectedPath = Path.Combine(repo, "tests", "ai-eval", "expected", mapName + ".json");
if (!File.Exists(expectedPath)) { Console.WriteLine($"Unknown map '{mapName}': {expectedPath} not found."); return 2; }
if (!File.Exists(xmlPath)) { Console.WriteLine($"XML file not found: {xmlPath}"); return 2; }
if (csPath != null && !File.Exists(csPath)) { Console.WriteLine($"C# file not found: {csPath}"); return 2; }

using var expectedDoc = JsonDocument.Parse(File.ReadAllText(expectedPath));
JsonElement exp = expectedDoc.RootElement;
string expRoot = exp.GetProperty("root").GetString()!;

var rep = new Report();
Console.WriteLine($"Grading map '{mapName}' (expected class {expRoot}), XML: {xmlPath}" + (csPath != null ? $", C#: {csPath}" : ""));
Console.WriteLine();

// ---------- 1. root + FromXml ----------
Console.WriteLine("[1] Root element and FromXml");
XDocument? doc = null;
try { doc = XDocument.Load(xmlPath, LoadOptions.SetLineInfo); }
catch (Exception ex) { rep.Check("1.1", false, "XML is well-formed", ex.Message); }
if (doc?.Root == null)
{
    if (doc != null) rep.Check("1.1", false, "XML has a root element");
    return rep.Finish();
}
rep.Check("1.1", true, "XML is well-formed");
XElement root = doc.Root;
rep.Check("1.2", root.Name.LocalName == expRoot && root.Name.NamespaceName == "",
    $"Root element is <{expRoot}>", $"found <{root.Name}>");

{
    string? err = null;
    Type? t = typeof(Devinno.Communication.ConfigSchema).Assembly.GetTypes()
        .FirstOrDefault(x => x.IsPublic && x.Name == expRoot);
    if (t == null) err = $"class {expRoot} not found in the library";
    else
    {
        object? inst = null;
        try
        {
            inst = Activator.CreateInstance(t);
            t.GetMethod("FromXml", new[] { typeof(XElement) })!.Invoke(inst, new object[] { root });
        }
        catch (TargetInvocationException tex) { err = tex.InnerException?.GetType().Name + ": " + tex.InnerException?.Message; }
        catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; }
        finally { (inst as IDisposable)?.Dispose(); }
    }
    rep.Check("1.3", err == null, $"{expRoot}.FromXml succeeds", err);
}

// ---------- 2. XSD ----------
Console.WriteLine("[2] XSD validation");
{
    string xsd = Path.Combine(repo, "schemas", expRoot + ".xsd");
    var errors = new List<string>();
    try
    {
        var set = new XmlSchemaSet();
        using (var r = XmlReader.Create(xsd)) set.Add(null, r);
        doc.Validate(set, (s, e) => errors.Add($"line {e.Exception?.LineNumber}: {e.Message}"));
    }
    catch (Exception ex) { errors.Add(ex.Message); }
    rep.Check("2.1", errors.Count == 0, $"Valid against schemas/{expRoot}.xsd", string.Join(" | ", errors.Take(5)));
}

// ---------- protocol adapter ----------
Proto proto = expRoot switch
{
    "McEthernet" => new McProto(),
    "ModbusTCPMaster" => new ModbusProto(),
    "S7Ethernet" => new S7Proto(),
    _ => throw new NotSupportedException(expRoot),
};

var readEntries = proto.ReadEntries(root);
var writeEls = root.Elements("Write").ToList();

// ---------- 3. coverage + limits ----------
Console.WriteLine("[3] Read coverage and request limits");
{
    var limits = exp.GetProperty("limits");
    foreach (var e in readEntries)
    {
        string? bad = proto.LimitError(e, limits);
        rep.Check("3.L", bad == null, $"Request limit respected: {e.Label}", bad);
    }
    foreach (var s in exp.GetProperty("reads").EnumerateArray())
    {
        string name = s.GetProperty("name").GetString()!;
        bool ok = proto.Covered(readEntries, s, exp);
        rep.Check("3.C", ok, $"Read signal covered: {name} ({Describe(s)})",
            ok ? null : "no read entry covers it (check area/function, slave, range and entry kind)");
    }
}

// ---------- 4. writes ----------
Console.WriteLine("[4] Named writes");
foreach (var w in exp.GetProperty("writes").EnumerateArray())
{
    string name = w.GetProperty("name").GetString()!;
    var el = writeEls.FirstOrDefault(x => (string?)x.Attribute("Name") == name);
    if (el == null)
    {
        string near = writeEls.Select(x => (string?)x.Attribute("Name") ?? "")
            .FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) ?? "";
        rep.Check("4.W", false, $"Write exists: {name}", near.Length > 0 ? $"only a different-case name exists: '{near}'" : "no <Write> with this exact Name");
        continue;
    }
    string? err = proto.WriteError(el, w, exp, root);
    rep.Check("4.W", err == null, $"Write correct: {name}", err);
}

// ---------- 5. ids ----------
Console.WriteLine("[5] Ids");
{
    var ids = new List<(string label, string? id)>();
    foreach (var e in root.Elements().Where(x => x.Name.LocalName != "Write" && proto.IsReadElement(x)))
        ids.Add(($"<{e.Name.LocalName}>", (string?)e.Attribute("Id")));
    foreach (var e in writeEls) ids.Add(($"<Write Name=\"{(string?)e.Attribute("Name")}\">", (string?)e.Attribute("Id")));
    var dup = ids.Where(i => i.id != null).GroupBy(i => i.id).Where(g => g.Count() > 1).ToList();
    rep.Check("5.1", dup.Count == 0, "No duplicate Id across read and Write entries",
        string.Join("; ", dup.Select(g => $"Id {g.Key} used by {string.Join(", ", g.Select(x => x.label))}")));
    rep.Check("5.2", ids.All(i => int.TryParse(i.id, out _)), "Every entry has an integer Id");
}

// ---------- 6. connection ----------
Console.WriteLine("[6] Connection values");
{
    var defaults = exp.GetProperty("connectionDefaults");
    foreach (var p in exp.GetProperty("connection").EnumerateObject())
    {
        string want = p.Value.GetString()!;
        string? got = (string?)root.Attribute(p.Name);
        if (got == null && defaults.TryGetProperty(p.Name, out var d)) got = d.GetString();
        rep.Check("6.A", got == want, $"{p.Name} = {want}", $"effective value is '{got ?? "(none)"}'");
    }
}

// ---------- 7. C# ----------
if (csPath != null)
{
    Console.WriteLine("[7] C# file");
    string code = File.ReadAllText(csPath);
    string stripped = StripComments(code);

    // variable names declared with the expected class type: "Root x", "Root x = new...", "var x = new Root..."
    var names = new HashSet<string>();
    foreach (Match m in Regex.Matches(stripped, @"\b" + expRoot + @"\s+(?<v>\w+)\s*(?=[=;,)])")) names.Add(m.Groups["v"].Value);
    foreach (Match m in Regex.Matches(stripped, @"\bvar\s+(?<v>\w+)\s*=\s*new\s+" + expRoot + @"\b")) names.Add(m.Groups["v"].Value);
    string nameAlt = names.Count == 0 ? "(?!)" : "(?:" + string.Join("|", names.Select(Regex.Escape)) + ")";
    rep.Check("7.1", names.Count > 0 && Regex.IsMatch(stripped, @"\bnew\s+" + expRoot + @"\b|\bnew\s*\("),
        $"Creates a {expRoot} object held in a variable", "no variable of type " + expRoot + " created with new found");
    rep.Check("7.2", Regex.IsMatch(stripped, @"\b" + nameAlt + @"\s*\.\s*(LoadXml|FromXml)\s*\("), "Loads the XML on that object (LoadXml/FromXml)");
    var sub = Regex.Match(stripped, @"\b" + nameAlt + @"\s*\.\s*(DataReceived|BlockReceived|WordReadReceived|BitReadReceived)\s*\+=");
    rep.Check("7.3", sub.Success, "Subscribes to a read-data event on that object (DataReceived/BlockReceived/WordReadReceived/BitReadReceived)");
    var start = Regex.Match(stripped, @"\b" + nameAlt + @"\s*\.\s*Start\s*\(");
    rep.Check("7.4", start.Success, "Calls Start() on that object");
    rep.Check("7.5", sub.Success && start.Success && sub.Index < start.Index, "Subscribes to the data event before Start()");
    var nw = Regex.Matches(stripped, @"\.\s*NamedWrite\s*\(\s*(?:""(?<n>[^""]*)"")?");
    rep.Check("7.6", nw.Count > 0, "Calls NamedWrite");
    var wNames = writeEls.Select(x => (string?)x.Attribute("Name")).ToHashSet();
    var unknown = nw.Select(m => m.Groups["n"].Success ? m.Groups["n"].Value : null).Where(n => n != null && !wNames.Contains(n)).Distinct().ToList();
    rep.Check("7.7", unknown.Count == 0, "Every literal NamedWrite name exists as <Write Name> in the XML", string.Join(", ", unknown));
    bool nonLiteral = nw.Any(m => !m.Groups["n"].Success);
    var notUsed = exp.GetProperty("writes").EnumerateArray().Select(w => w.GetProperty("name").GetString()!)
        .Where(n => !stripped.Contains("\"" + n + "\"")).ToList();
    rep.Check("7.8", notUsed.Count == 0 || nonLiteral, "Every write signal name appears as a string literal in the code (or NamedWrite gets a non-literal name)",
        "not found in code: " + string.Join(", ", notUsed));
    var (ok, detail) = BuildCs(repo, csPath);
    rep.Check("7.9", ok, "dotnet build succeeds with 0 errors", detail);
}

return rep.Finish();

// ======================================================================
static string FindRepoRoot()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        var d = new DirectoryInfo(start);
        while (d != null)
        {
            if (File.Exists(Path.Combine(d.FullName, "Devinno.Communication.sln"))) return d.FullName;
            d = d.Parent;
        }
    }
    throw new InvalidOperationException("Repository root (Devinno.Communication.sln) not found.");
}

static string Describe(JsonElement s)
{
    string area = s.GetProperty("area").GetString()!;
    return $"{area} start {s.GetProperty("start").GetInt64()} count {s.GetProperty("count").GetInt64()}";
}

// Removes // and /* */ comments but keeps string and char literals intact.
static string StripComments(string c)
{
    var sb = new StringBuilder();
    int i = 0;
    while (i < c.Length)
    {
        char ch = c[i];
        if (ch == '/' && i + 1 < c.Length && c[i + 1] == '/') { while (i < c.Length && c[i] != (char)10) i++; continue; }
        if (ch == '/' && i + 1 < c.Length && c[i + 1] == '*') { int e = c.IndexOf("*/", i + 2, StringComparison.Ordinal); i = e < 0 ? c.Length : e + 2; sb.Append(' '); continue; }
        if (ch == '"' || ch == (char)39)
        {
            bool verbatim = ch == '"' && i > 0 && (c[i - 1] == '@' || (i > 1 && c[i - 1] == '$' && c[i - 2] == '@') || (i > 1 && c[i - 1] == '@' && c[i - 2] == '$'));
            sb.Append(ch); i++;
            while (i < c.Length)
            {
                char d = c[i];
                if (verbatim) { sb.Append(d); i++; if (d == '"') { if (i < c.Length && c[i] == '"') { sb.Append('"'); i++; continue; } break; } continue; }
                if (d == (char)92 && i + 1 < c.Length) { sb.Append(d).Append(c[i + 1]); i += 2; continue; }
                sb.Append(d); i++;
                if (d == ch || d == (char)10) break;
            }
            continue;
        }
        sb.Append(ch); i++;
    }
    return sb.ToString();
}

static (bool, string?) BuildCs(string repo, string csPath)
{
    string dir = Path.Combine(Path.GetTempPath(), "ai-eval-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        string lib = Path.Combine(repo, "src", "Devinno.Communication", "Devinno.Communication.csproj");
        File.WriteAllText(Path.Combine(dir, "EvalApp.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework>" +
            "<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>" +
            $"<ItemGroup><ProjectReference Include=\"{lib}\" /></ItemGroup></Project>");
        File.Copy(csPath, Path.Combine(dir, "Candidate.cs"));
        var psi = new ProcessStartInfo("dotnet", "build -nologo -v q")
        {
            WorkingDirectory = dir, Environment = { ["DOTNET_CLI_UI_LANGUAGE"] = "en", ["VSLANG"] = "1033" }, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } return (false, "dotnet build timed out after 5 minutes"); }
        p.WaitForExit();
        string output = so.Result + se.Result;
        bool zero = !Regex.IsMatch(output, @": error [A-Za-z]+\d+");
        bool ok = p.ExitCode == 0 && zero;
        string? detail = null;
        if (!ok)
        {
            var errs = output.Split('\n').Where(l => l.Contains(": error ")).Select(l => l.Trim()).Distinct().Take(8);
            detail = string.Join(" | ", errs);
            if (string.IsNullOrEmpty(detail)) detail = "exit code " + p.ExitCode + "; " + output[..Math.Min(400, output.Length)];
        }
        return (ok, detail);
    }
    finally { try { Directory.Delete(dir, true); } catch { } }
}

sealed class Report
{
    int pass, total;
    public void Check(string id, bool ok, string desc, string? detail = null)
    {
        total++; if (ok) pass++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {id} {desc}" + (!ok && !string.IsNullOrEmpty(detail) ? $"  -> {detail}" : ""));
    }
    public int Finish()
    {
        Console.WriteLine();
        Console.WriteLine($"{pass}/{total} checks passed");
        Console.WriteLine(pass == total ? "RESULT: PASS" : "RESULT: FAIL");
        return pass == total ? 0 : 1;
    }
}

sealed record Entry(XElement El, string Label, string Kind, string Area, long Start, long End, long Count, int Slave, string[] Addrs, bool Valid);

abstract class Proto
{
    public abstract bool IsReadElement(XElement e);
    public abstract List<Entry> ReadEntries(XElement root);
    public abstract string? LimitError(Entry e, JsonElement limits);

    // A signal is covered when the union of the matching entries (same area/function/slave) contains its whole range,
    // so a long range split into several entries is accepted.
    public virtual bool Covered(List<Entry> entries, JsonElement sig, JsonElement exp)
    {
        long s = sig.GetProperty("start").GetInt64(), c = sig.GetProperty("count").GetInt64();
        long need = s;
        foreach (var e in entries.Where(x => Matches(x, sig, exp)).OrderBy(x => x.Start))
        {
            if (e.Start <= need && e.End >= need) need = e.End + 1;
            if (need >= s + c) return true;
        }
        return need >= s + c;
    }
    protected abstract bool Matches(Entry e, JsonElement sig, JsonElement exp);
    public abstract string? WriteError(XElement w, JsonElement exp, JsonElement all, XElement root);

    protected static bool Int(XElement e, string a, out long v)
    {
        v = 0;
        string? s = (string?)e.Attribute(a);
        return s != null && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
    }
    protected static string Lbl(XElement e)
    {
        string id = (string?)e.Attribute("Id") ?? "?";
        return $"<{e.Name.LocalName} Id=\"{id}\">";
    }
}

// ---------------- Mitsubishi MC (3E) ----------------
sealed class McProto : Proto
{
    static readonly string[] Areas = { "SM", "SD", "SB", "SW", "SS", "SC", "SN", "TS", "TC", "TN", "CS", "CC", "CN", "ZR", "X", "Y", "M", "L", "F", "V", "B", "D", "W", "S", "Z", "R" };
    static readonly HashSet<string> BitAreas = new() { "X", "Y", "M", "L", "F", "V", "B", "S", "SM", "SB", "SS", "SC", "TS", "TC", "CS", "CC" };
    static readonly HashSet<string> HexAreas = new() { "B", "W", "SB", "SW", "ZR" };

    public override bool IsReadElement(XElement e) => e.Name.LocalName is "BR" or "WR";

    public static bool TryDevice(string? text, XElement root, out string area, out long num)
    {
        area = ""; num = 0;
        if (text == null) return false;
        string t = text.Trim().ToUpperInvariant();
        foreach (var a in Areas.OrderByDescending(x => x.Length))
        {
            if (!t.StartsWith(a) || t.Length == a.Length) continue;
            string rest = t[a.Length..];
            string notation = (string?)root.Attribute("XYNotation") ?? "Auto";
            string frame = (string?)root.Attribute("Frame") ?? "E3";
            int radix = 10;
            if (a is "X" or "Y") radix = notation == "Octal" ? 8 : notation == "Hex" ? 16 : (frame == "E1" ? 8 : 16);
            else if (HexAreas.Contains(a)) radix = 16;
            try { num = Convert.ToInt64(rest, radix); area = a; return true; } catch { }
        }
        return false;
    }

    public override List<Entry> ReadEntries(XElement root)
    {
        var list = new List<Entry>();
        foreach (var e in root.Elements().Where(IsReadElement))
        {
            bool ok = TryDevice((string?)e.Attribute("Device"), root, out var area, out var n) & Int(e, "Count", out var cnt);
            long end = n + cnt - 1;
            if (e.Name.LocalName == "WR" && BitAreas.Contains(area)) end = n + 16 * cnt - 1;
            list.Add(new Entry(e, Lbl(e) + " " + (string?)e.Attribute("Device"), e.Name.LocalName, area, n, end, cnt, 0, Array.Empty<string>(), ok));
        }
        return list;
    }

    public override string? LimitError(Entry e, JsonElement limits)
    {
        if (!e.Valid) return "Device or Count is not valid";
        long lim = limits.GetProperty(e.Kind).GetInt64();
        if (e.Count < 1 || e.Count > lim) return $"Count {e.Count} is outside 1..{lim}";
        if (e.Kind == "BR" && !BitAreas.Contains(e.Area)) return $"BR needs a bit device, {e.Area} is a word device";
        return null;
    }

    protected override bool Matches(Entry e, JsonElement sig, JsonElement exp)
    {
        if (!e.Valid || e.Area != sig.GetProperty("area").GetString()) return false;
        return !(e.Kind == "BR" && !BitAreas.Contains(e.Area));
    }

    public override string? WriteError(XElement w, JsonElement want, JsonElement exp, XElement root)
    {
        string a = want.GetProperty("address").GetString()!;
        TryDevice(a, root, out var wa, out var wn);
        if (!TryDevice((string?)w.Attribute("Device"), root, out var ga, out var gn)) return $"Device '{(string?)w.Attribute("Device")}' is not valid";
        return ga == wa && gn == wn ? null : $"Device is {(string?)w.Attribute("Device")}, expected {a}";
    }
}

// ---------------- Modbus TCP master ----------------
sealed class ModbusProto : Proto
{
    public override bool IsReadElement(XElement e) => e.Name.LocalName == "Auto";

    public override List<Entry> ReadEntries(XElement root)
    {
        var list = new List<Entry>();
        foreach (var e in root.Elements("Auto"))
        {
            bool ok = Int(e, "Start", out var st) | ((string?)e.Attribute("Start") == null);
            ok &= Int(e, "Length", out var len);
            ok &= Int(e, "Slave", out var sl);
            string fn = (string?)e.Attribute("Func") ?? "";
            list.Add(new Entry(e, Lbl(e) + $" {fn} Start {st} Length {len}", "Auto", fn, st, st + len - 1, len, (int)sl, Array.Empty<string>(), ok));
        }
        return list;
    }

    public override string? LimitError(Entry e, JsonElement limits)
    {
        if (!e.Valid) return "Func/Slave/Start/Length is not valid";
        if (!limits.TryGetProperty(e.Area, out var l)) return $"Func {e.Area} is not a read function (FC1..FC4)";
        return e.Count is >= 1 && e.Count <= l.GetInt64() ? null : $"Length {e.Count} is outside 1..{l.GetInt64()}";
    }

    protected override bool Matches(Entry e, JsonElement sig, JsonElement exp)
        => e.Valid && e.Area == sig.GetProperty("area").GetString() && e.Slave == exp.GetProperty("slave").GetInt32();

    public override string? WriteError(XElement w, JsonElement want, JsonElement exp, XElement root)
    {
        string func = (string?)w.Attribute("Func") ?? "";
        var funcs = want.GetProperty("funcs").EnumerateArray().Select(x => x.GetString()!).ToList();
        var errs = new List<string>();
        if (!funcs.Contains(func)) errs.Add($"Func is {func}, accepted: {string.Join("/", funcs)}");
        long start = 0;
        if ((string?)w.Attribute("Start") != null && !Int(w, "Start", out start)) errs.Add("Start is not an integer");
        else if (start != want.GetProperty("start").GetInt64()) errs.Add($"Start is {start}, expected {want.GetProperty("start").GetInt64()}");
        if (!Int(w, "Slave", out var sl) || sl != exp.GetProperty("slave").GetInt64()) errs.Add($"Slave is {(string?)w.Attribute("Slave")}, expected {exp.GetProperty("slave").GetInt64()}");
        return errs.Count == 0 ? null : string.Join("; ", errs);
    }
}

// ---------------- Siemens S7 ----------------
sealed class S7Proto : Proto
{
    static readonly Regex Rx = new(@"^(?:DB(?<db>\d+)\.DB(?<sz>[XBWD])(?<by>\d+)(?:\.(?<bit>[0-7]))?|(?<ar>[MIQEA])(?<sz2>[XBWD])?(?<by2>\d+)(?:\.(?<bit2>[0-7]))?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // returns canonical form + parts
    public static bool TryAddr(string? text, out string canon, out string area, out char size, out long byteNo)
    {
        canon = area = ""; size = ' '; byteNo = 0;
        if (text == null) return false;
        var m = Rx.Match(text.Trim());
        if (!m.Success) return false;
        string bit;
        if (m.Groups["db"].Success)
        {
            area = "DB" + long.Parse(m.Groups["db"].Value);
            size = char.ToUpperInvariant(m.Groups["sz"].Value[0]);
            byteNo = long.Parse(m.Groups["by"].Value);
            bit = m.Groups["bit"].Value;
        }
        else
        {
            char ar = char.ToUpperInvariant(m.Groups["ar"].Value[0]);
            area = ar == 'E' ? "I" : ar == 'A' ? "Q" : ar.ToString();
            bit = m.Groups["bit2"].Value;
            size = m.Groups["sz2"].Success ? char.ToUpperInvariant(m.Groups["sz2"].Value[0]) : 'X';
            byteNo = long.Parse(m.Groups["by2"].Value);
        }
        if (size == 'X' && bit == "") return false;
        if (size != 'X' && bit != "") return false;
        canon = $"{area}.{size}{byteNo}" + (bit != "" ? "." + bit : "");
        return true;
    }

    public override bool IsReadElement(XElement e) => e.Name.LocalName is "Read" or "ReadBlock";

    public override List<Entry> ReadEntries(XElement root)
    {
        var list = new List<Entry>();
        foreach (var e in root.Elements().Where(IsReadElement))
        {
            if (e.Name.LocalName == "Read")
            {
                var parts = ((string?)e.Attribute("Addresses") ?? "").Split(',').Select(s => s.Trim()).ToArray();
                var canon = new List<string>();
                bool ok = parts.Length > 0;
                foreach (var p in parts) { if (TryAddr(p, out var c, out _, out _, out _)) canon.Add(c); else ok = false; }
                list.Add(new Entry(e, Lbl(e) + $" ({parts.Length} addresses)", "Read", "", 0, 0, parts.Length, 0, canon.ToArray(), ok));
            }
            else
            {
                bool ok = TryAddr((string?)e.Attribute("Address"), out _, out var area, out var size, out var by) & Int(e, "Count", out var cnt);
                ok &= size == 'B';
                list.Add(new Entry(e, Lbl(e) + " " + (string?)e.Attribute("Address") + " Count " + (string?)e.Attribute("Count"), "ReadBlock", area, by, by + cnt - 1, cnt, 0, Array.Empty<string>(), ok));
            }
        }
        return list;
    }

    public override string? LimitError(Entry e, JsonElement limits)
    {
        if (!e.Valid) return e.Kind == "Read" ? "an address is not valid" : "Address must be a valid byte address (DBn.DBB / MB / IB / QB) and Count an integer";
        long lim = limits.GetProperty(e.Kind).GetInt64();
        return e.Count >= 1 && e.Count <= lim ? null : $"{(e.Kind == "Read" ? "address count" : "Count")} {e.Count} is outside 1..{lim}";
    }

    protected override bool Matches(Entry e, JsonElement sig, JsonElement exp) => false;

    // Byte-level coverage: every byte of the signal must be inside a ReadBlock of the area or inside a Read item of
    // size B/W/D of the area. A bit signal is also covered by a Read of exactly its bit address.
    public override bool Covered(List<Entry> entries, JsonElement sig, JsonElement exp)
    {
        string area = sig.GetProperty("area").GetString()!;
        long s0 = sig.GetProperty("start").GetInt64(), c = sig.GetProperty("count").GetInt64();
        TryAddr(sig.GetProperty("address").GetString(), out var canon, out _, out _, out _);
        if (entries.Any(e => e.Valid && e.Kind == "Read" && e.Addrs.Contains(canon))) return true;
        var spans = new List<(long s, long e)>();
        foreach (var e in entries.Where(x => x.Valid))
        {
            if (e.Kind == "ReadBlock") { if (e.Area == area) spans.Add((e.Start, e.End)); continue; }
            foreach (var a in e.Addrs)
            {
                var m = Regex.Match(a, @"^(?<a>[^.]+)\.(?<s>[BWD])(?<b>\d+)$");
                if (!m.Success || m.Groups["a"].Value != area) continue;
                long b = long.Parse(m.Groups["b"].Value);
                long len = m.Groups["s"].Value == "B" ? 1 : m.Groups["s"].Value == "W" ? 2 : 4;
                spans.Add((b, b + len - 1));
            }
        }
        for (long b = s0; b < s0 + c; b++) if (!spans.Any(x => x.s <= b && b <= x.e)) return false;
        return true;
    }

    public override string? WriteError(XElement w, JsonElement want, JsonElement exp, XElement root)
    {
        TryAddr(want.GetProperty("address").GetString(), out var wc, out _, out _, out _);
        string? got = (string?)w.Attribute("Address");
        if (!TryAddr(got, out var gc, out _, out _, out _)) return $"Address '{got}' is not valid";
        return gc == wc ? null : $"Address is {got}, expected {want.GetProperty("address").GetString()}";
    }
}
