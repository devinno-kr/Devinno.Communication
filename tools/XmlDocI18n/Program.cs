// Shared with Devinno.UI (tools/XmlDocI18n) — keep both copies identical; announce changes in the shared chat.
using System.Text.Json;
using Devinno.UI.Tools;

// Korean IntelliSense store for the packages (i18n/ko/*.xml). The English XML comes from the build (source comments).
//   seed    <en.xml> <ko-original.xml> <store.xml>   first store from the Korean XML of the old Korean comments
//   check   <en.xml> <store.xml>                     list members that are missing, stale or have changed code tokens
//   pending <en.xml> <store.xml> <out.json>          export members to (re)translate: [{Name, English, OldKorean}]
//   apply   <en.xml> <store.xml> <in.json>           merge translations: {"member name": "<summary>...</summary>..."}
//   md-stamp <README.md> <README.ko.md>              write the English section hashes into the Korean file (after translating)
//   md-check <README.md> <README.ko.md>              list Korean sections whose English changed
if (args.Length < 3)
{
    Console.Error.WriteLine("usage: seed|check|pending|apply <en.xml> <store.xml> [file] | md-stamp|md-check <en.md> <ko.md>");
    return 2;
}

if (args[0] is "md-stamp" or "md-check")
{
    var enMd = File.ReadAllText(args[1]);
    var koMd = File.ReadAllText(args[2]);
    if (args[0] == "md-stamp")
    {
        File.WriteAllText(args[2], XmlDocI18n.MdStamp(enMd, koMd), new System.Text.UTF8Encoding(false));
        Console.WriteLine($"stamped {args[2]}");
        return 0;
    }
    var mdProblems = XmlDocI18n.MdCheck(enMd, koMd);
    foreach (var p in mdProblems)
        Console.WriteLine(p);
    Console.WriteLine($"{mdProblems.Count} problem(s)");
    return mdProblems.Count == 0 ? 0 : 1;
}

var en = XmlDocI18n.Load(args[1]);
switch (args[0])
{
    case "seed":
        XmlDocI18n.Save(XmlDocI18n.Seed(en, XmlDocI18n.Load(args[2])), args[3]);
        Console.WriteLine($"seeded {args[3]}");
        return 0;
    case "check":
    {
        var problems = XmlDocI18n.Check(en, XmlDocI18n.Load(args[2]));
        foreach (var p in problems)
            Console.WriteLine(p);
        Console.WriteLine($"{problems.Count} problem(s)");
        return problems.Count == 0 ? 0 : 1;
    }
    case "pending":
    {
        var list = XmlDocI18n.PendingList(en, XmlDocI18n.Load(args[2]));
        File.WriteAllText(args[3], XmlDocI18n.ToJson(list));
        Console.WriteLine($"{list.Count} member(s) to translate → {args[3]}");
        return 0;
    }
    case "apply":
    {
        var translated = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(args[3]))!;
        XmlDocI18n.Save(XmlDocI18n.Apply(en, XmlDocI18n.Load(args[2]), translated), args[2]);
        Console.WriteLine($"applied {translated.Count} member(s) to {args[2]}");
        return 0;
    }
    default:
        Console.Error.WriteLine("unknown command " + args[0]);
        return 2;
}
