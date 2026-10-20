using System.Runtime.CompilerServices;
using Devinno.UI.Tools;

namespace Devinno.Communication.Tests;

// English is the original (source comments, README.md); Korean is the translation (i18n/ko/*.xml, README.ko.md).
// These tests fail when an English text changed and its Korean translation was not updated, so a stale translation can't be
// published. Fix: run tools/XmlDocI18n (pending → translate → apply, or md-stamp after editing README.ko.md).
public class XmlDocI18nTests
{
    private static string RepoRoot([CallerFilePath] string source = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));

    [Fact]
    public void Korean_IntelliSense_matches_the_English_comments()
    {
        // The English XML the test project's build copied next to the test assembly (same build of src/Devinno.Communication).
        var english = Path.Combine(AppContext.BaseDirectory, "Devinno.Communication.xml");
        Assert.True(File.Exists(english), $"missing {english}");
        var store = Path.Combine(RepoRoot(), "i18n", "ko", "Devinno.Communication.xml");
        Assert.True(File.Exists(store), $"missing {store}");
        var problems = XmlDocI18n.Check(XmlDocI18n.Load(english), XmlDocI18n.Load(store));
        Assert.True(problems.Count == 0,
            $"{problems.Count} member(s) out of sync in i18n/ko/Devinno.Communication.xml:\n" + string.Join("\n", problems.Take(30).Select(p => p.ToString())));
    }

    [Fact]
    public void Korean_README_matches_the_English_README()
    {
        var problems = XmlDocI18n.MdCheck(File.ReadAllText(Path.Combine(RepoRoot(), "README.md")),
            File.ReadAllText(Path.Combine(RepoRoot(), "README.ko.md")));
        Assert.True(problems.Count == 0, "README.ko.md out of sync:\n" + string.Join("\n", problems));
    }
}
