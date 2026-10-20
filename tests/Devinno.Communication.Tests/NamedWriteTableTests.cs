using Devinno.Communication.Internal;

namespace Devinno.Communication.Tests;

public class NamedWriteTableTests
{
    sealed class TestWrite : NamedWriteConfig { }

    static TestWrite W(string name, int id = 1) => new() { Name = name, Id = id };
    static NamedWriteTable<TestWrite> Build(params TestWrite[] items) => NamedWriteTable<TestWrite>.Build(items, (_, _) => { });

    [Fact]
    public void Build_DuplicateName_Throws()
    {
        var ex = Assert.Throws<FormatException>(() => Build(W("A", 1), W("A", 2)));
        Assert.Contains("Write Name=\"A\"", ex.Message);
        Assert.Contains("Name", ex.Message);
    }

    [Fact]
    public void Build_CaseDiffers_AreDistinct()
    {
        var t = Build(W("Run", 1), W("run", 2));
        Assert.True(t.Contains("Run"));
        Assert.True(t.Contains("run"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" A")]
    [InlineData("A ")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123X")]
    public void Build_InvalidName_Throws(string name)
        => Assert.Throws<FormatException>(() => Build(W(name)));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(60001, 0)]
    [InlineData(500, -1)]
    [InlineData(500, 101)]
    public void Build_TimeoutOrRepeatOutOfRange_Throws(int timeout, int repeat)
    {
        var w = W("A");
        w.Timeout = timeout;
        w.RepeatCount = repeat;
        Assert.Throws<FormatException>(() => Build(w));
    }

    [Fact]
    public void Build_StoresCopies()
    {
        var src = W("A", 7);
        var t = Build(src);
        src.Name = "B";
        src.Id = 99;
        Assert.Equal(7, t.Get("A").Id);
        var snap = t.Snapshot();
        snap[0].Id = 55;
        Assert.Equal(7, t.Get("A").Id);
        Assert.Equal(7, t.Snapshot()[0].Id);
    }

    [Fact]
    public void Get_Unknown_ThrowsArgumentException_WithName()
    {
        var ex = Assert.Throws<ArgumentException>(() => Build(W("A")).Get("Nope"));
        Assert.Contains("Nope", ex.Message);
        Assert.Equal("name", ex.ParamName);
    }

    [Fact]
    public void Get_Null_ThrowsArgumentNull()
        => Assert.Throws<ArgumentNullException>(() => Build(W("A")).Get(null!));

    [Fact]
    public void Resolve_ArgumentWins_ThenXml_ThenNull()
    {
        var spec = W("A");
        spec.Timeout = 500;
        spec.RepeatCount = 2;
        var plain = W("B", 2);
        var t = Build(spec, plain);
        Assert.Equal((2, 500), t.Resolve(t.Get("A"), null, null));
        Assert.Equal((0, 100), t.Resolve(t.Get("A"), 0, 100));
        Assert.Equal(((int?)null, (int?)null), t.Resolve(t.Get("B"), null, null));
    }

    [Fact]
    public void Build_CallsValidateAddress_WithLabel()
    {
        string? label = null;
        NamedWriteTable<TestWrite>.Build([W("A")], (_, l) => label = l);
        Assert.Equal("Write Name=\"A\"", label);
    }
}
