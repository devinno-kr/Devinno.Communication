using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 레시피 폴더 저장소(Plan 16 Task 2): PPID 하나 = 파일 하나, 대소문자·특수 문자에 안전한 이름, 원래 PPID·본문 포맷 그대로
public class GemProcessProgramFolderTests
{
    static string NewDir() => Path.Combine(Path.GetTempPath(), "devinno-pp-" + Guid.NewGuid());

    static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { }
    }

    [Fact]
    public void WriteReadDelete_RoundTrip()
    {
        var dir = NewDir();
        try
        {
            var f = new GemProcessProgramFolder(dir);
            f.Write(new GemProcessProgram("RCP-1", SecsItem.B(1, 2)));
            Assert.Equal(SecsItem.B(1, 2), f.Read("RCP-1")!.Body);
            Assert.Equal("RCP-1", f.Read("RCP-1")!.Ppid);
            Assert.Equal(new[] { "RCP-1" }, f.GetIds());
            Assert.True(f.Delete("RCP-1"));
            Assert.False(f.Delete("RCP-1"));
            Assert.Null(f.Read("RCP-1"));
            Assert.Empty(f.GetIds());
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Write_Existing_Replaces()
    {
        var dir = NewDir();
        try
        {
            var f = new GemProcessProgramFolder(dir);
            f.Write(new GemProcessProgram("R", SecsItem.B(1)));
            f.Write(new GemProcessProgram("R", SecsItem.A("two")));
            Assert.Equal(SecsItem.A("two"), f.Read("R")!.Body);
            Assert.Single(Directory.GetFiles(dir));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Folder_CaseOnlyDifferentPpids_AreSeparate()
    {
        var dir = NewDir();
        try
        {
            var f = new GemProcessProgramFolder(dir);
            f.Write(new GemProcessProgram("Recipe-A", SecsItem.B(1)));
            f.Write(new GemProcessProgram("recipe-a", SecsItem.B(2)));
            Assert.Equal(2, Directory.GetFiles(dir).Length);
            Assert.Equal(SecsItem.B(1), f.Read("Recipe-A")!.Body);
            Assert.Equal(SecsItem.B(2), f.Read("recipe-a")!.Body);
            Assert.Equal(new[] { "Recipe-A", "recipe-a" }, f.GetIds());
            Assert.True(File.Exists(Path.Combine(dir, "%52ecipe-%41.pp")));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Folder_UnsafeCharacters_RoundTrip()
    {
        var dir = NewDir();
        try
        {
            var f = new GemProcessProgramFolder(dir);
            const string ppid = "a/b:c*d?e\"f<g>h|i\\j k";
            f.Write(new GemProcessProgram(ppid, SecsItem.B(9)));
            Assert.Equal(ppid, f.Read(ppid)!.Ppid);
            Assert.Equal(new[] { ppid }, f.GetIds());
            Assert.Contains("%2F", Path.GetFileName(Directory.GetFiles(dir).Single()));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Folder_LongUpperCasePpid_RoundTrip()
    {
        var dir = NewDir();
        try
        {
            var f = new GemProcessProgramFolder(dir);
            var a = new string('A', 120);             // 인코딩하면 360자(파일 이름 한계 255자 넘음)
            var b = new string('B', 119) + "C";
            f.Write(new GemProcessProgram(a, SecsItem.B(1)));
            f.Write(new GemProcessProgram(b, SecsItem.B(2)));
            Assert.Equal(SecsItem.B(1), f.Read(a)!.Body);
            Assert.Equal(SecsItem.B(2), f.Read(b)!.Body);
            Assert.Equal(new[] { a, b }, f.GetIds());
            Assert.True(f.Delete(a));
            Assert.Equal(new[] { b }, f.GetIds());
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void BodyFormats_RoundTrip()
    {
        var dir = NewDir();
        try
        {
            var f = new GemProcessProgramFolder(dir);
            var bodies = new[] { SecsItem.A("x=1"), SecsItem.U4(1, 2, 3), SecsItem.B(), SecsItem.Boolean(true) };
            for (var i = 0; i < bodies.Length; i++) f.Write(new GemProcessProgram("R" + i, bodies[i]));
            for (var i = 0; i < bodies.Length; i++) Assert.Equal(bodies[i], f.Read("R" + i)!.Body);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void GetIds_SkipsForeignAndCorruptFiles()
    {
        var dir = NewDir();
        try
        {
            var f = new GemProcessProgramFolder(dir);
            f.Write(new GemProcessProgram("ok", SecsItem.B(1)));
            File.WriteAllText(Path.Combine(dir, "note.txt"), "hi");
            File.WriteAllBytes(Path.Combine(dir, "x.pp"), [0xFF, 0x00, 0x13]);
            File.WriteAllBytes(Path.Combine(dir, "y.pp.tmp"), [1]);
            File.WriteAllBytes(Path.Combine(dir, "z.pp"), SecsCodec.Encode(SecsItem.L(SecsItem.A("other"), SecsItem.B(1))));
            Assert.Equal(new[] { "ok" }, f.GetIds());
            Assert.Null(f.Read("x"));
            Assert.Null(f.Read("z"));
            Assert.Equal(5, Directory.GetFiles(dir).Length);       // 건드리지 않음
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Ctor_Validation()
    {
        Assert.Throws<ArgumentNullException>(() => new GemProcessProgramFolder(null!));
        Assert.Throws<ArgumentException>(() => new GemProcessProgramFolder(""));
        var dir = NewDir();
        var f = new GemProcessProgramFolder(dir);
        Assert.Equal(dir, f.Directory);
        Assert.Empty(f.GetIds());
        Assert.False(Directory.Exists(dir));
        Assert.Null(f.Read("x"));
        Assert.False(f.Delete("x"));
    }
}
