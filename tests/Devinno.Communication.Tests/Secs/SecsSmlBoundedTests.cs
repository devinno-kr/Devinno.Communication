using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs;

/// 진단용 상한 SML(SecsSml.Item/Message(…, maxChars)): 상한까지만 그리고 멈춘다. 큰 본문을 통째로 그린 뒤 자르지 않는다(할당 폭증 방지)
public class SecsSmlBoundedTests
{
    static string Suffix(string s) => s[s.IndexOf("…(생략: ", StringComparison.Ordinal)..];

    [Fact]
    public void SmallItems_SameAsToSml()
    {
        var items = new[]
        {
            SecsItem.L(SecsItem.A("MDLN"), SecsItem.U4(1, 2)),
            SecsItem.A("ab\"c\r\n"), SecsItem.A(""), SecsItem.B(0, 0xFF), SecsItem.Boolean(true, false),
            SecsItem.F4(1.5f), SecsItem.F8(-0.1), SecsItem.I8(long.MinValue), SecsItem.L(),
        };
        foreach (var item in items) Assert.Equal(item.ToSml(), SecsSml.Item(item, 1000));
        var m = new SecsMessage(6, 11, true, items[0]);
        Assert.Equal(m.ToSml(), SecsSml.Message(m, 1000));
        Assert.Equal("S1F1 W", SecsSml.Message(new SecsMessage(1, 1, true), 1000));
        var exact = SecsItem.U1(1, 2, 3);
        Assert.Equal("<U1 1 2 3>", SecsSml.Item(exact, "<U1 1 2 3>".Length));             // 딱 맞으면 자르지 않음
    }

    [Fact]
    public void HugeArray_StopsAtLimit_SuffixWithCheapCounts()
    {
        var item = SecsItem.U1(new byte[1_000_000]);
        var s = SecsSml.Item(item, 100);
        Assert.StartsWith("<U1 0 0 0", s);
        Assert.Equal($"…(생략: 전체 아이템 1개, 본문 {SecsCodec.Encode(item).Length}바이트)", Suffix(s));
        Assert.True(s.Length <= 100 + Suffix(s).Length, $"{s.Length}자");
        Assert.Equal(100, s.Length - Suffix(s).Length);
        Assert.StartsWith(s[..100], item.ToSml());                                          // 잘린 앞부분은 전체 SML의 앞부분과 같다
    }

    [Fact]
    public void HugeNestedLists_StopsAtLimit()
    {
        var inner = SecsItem.U4(new uint[1000]);
        var item = SecsItem.L([.. Enumerable.Range(0, 1000).Select(_ => SecsItem.L(inner))]);   // 아이템 2001개, 값 100만 개
        var s = SecsSml.Item(item, 2048);
        Assert.Equal($"…(생략: 전체 아이템 2001개, 본문 {SecsCodec.Encode(item).Length}바이트)", Suffix(s));
        Assert.Equal(2048, s.Length - Suffix(s).Length);
        Assert.StartsWith(s[..2048], item.ToSml());

        var deep = SecsItem.U1(1);
        for (int i = 0; i < 2000; i++) deep = SecsItem.L(deep);                             // 깊은 중첩(코덱 한계 64를 넘는 앱 아이템도)
        var d = SecsSml.Item(deep, 500);
        Assert.Equal(500, d.Length - Suffix(d).Length);
        Assert.Contains("전체 아이템 2001개", d);
    }

    [Fact]
    public void HugeAsciiAndMessage_StopsAtLimit()
    {
        var text = string.Concat(Enumerable.Repeat("ab\r\n\"", 50_000));
        var item = SecsItem.A(text);
        var s = SecsSml.Item(item, 300);
        Assert.Equal(300, s.Length - Suffix(s).Length);
        Assert.StartsWith(s[..300], item.ToSml());

        var m = new SecsMessage(6, 11, true, SecsItem.L(SecsItem.U4(1), SecsItem.B(new byte[500_000])));
        var ms = SecsSml.Message(m, 200);
        Assert.StartsWith("S6F11 W <L [2] <U4 1> <B 0x00", ms);
        Assert.Equal(200, ms.Length - Suffix(ms).Length);
        Assert.Contains("전체 아이템 3개", ms);
    }

    [Fact]
    public void Diagnostics_16MbItem_AllocatesLittle()
    {
        var item = SecsItem.B(new byte[16_000_000]);
        var m = new SecsMessage(6, 11, true, SecsItem.L(SecsItem.U4(1), item));
        GemItems.Sml(item);                                                                // JIT 등 첫 호출 비용 제외
        long before = GC.GetAllocatedBytesForCurrentThread();
        var a = GemItems.Sml(item);
        var b = GemItems.Sml(m);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1_000_000, $"{allocated}바이트 할당");
        Assert.True(a.Length < GemItems.SmlLimit + 100 && b.Length < GemItems.SmlLimit + 100, $"{a.Length}/{b.Length}자");
    }

    [Fact]
    public void ReadAckAndIdErrors_AreBounded()
    {
        var big = SecsItem.U1(new byte[200_000]);
        var ack = Assert.Throws<FormatException>(() => GemItems.ReadAck(big));
        Assert.True(ack.Message.Length < GemItems.SmlLimit + 200, $"{ack.Message.Length}자");
        var id = Assert.Throws<FormatException>(() => GemId.FromItem(SecsItem.B(new byte[200_000])));
        Assert.True(id.Message.Length < GemItems.SmlLimit + 200, $"{id.Message.Length}자");
    }

    [Fact]
    public void IdText_CapsLongTextIds()
    {
        Assert.Equal("5001", GemItems.IdText(5001));
        Assert.Equal("LOT_START", GemItems.IdText("LOT_START"));
        var s = GemItems.IdText(new string('z', 20_000));
        Assert.True(s.Length < GemItems.IdLimit + 30, $"{s.Length}자");
        Assert.EndsWith("…(전체 20000자)", s);
    }
}
