using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// TIME 형식 0/1/2 (TimeFormat EC)
public class GemTimeTests
{
    static readonly DateTime T = new(2026, 10, 14, 9, 5, 7, 345, DateTimeKind.Local);

    static string LocalZone(DateTime t)
    {
        var o = TimeZoneInfo.Local.GetUtcOffset(t);
        return o == TimeSpan.Zero ? "Z" : (o < TimeSpan.Zero ? "-" : "+") + o.Duration().ToString(@"hh\:mm");
    }

    [Fact]
    public void Format_ThreeForms()
    {
        Assert.Equal("261014090507", GemTime.Format(T, 0));
        Assert.Equal("2026101409050734", GemTime.Format(T, 1));
        Assert.Equal("2026-10-14T09:05:07.34" + LocalZone(T), GemTime.Format(T, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => GemTime.Format(T, 3));
    }

    [Theory]
    [InlineData("261014090507", 2026)]
    [InlineData("691231235959", 2069)]
    [InlineData("700101000000", 1970)]
    [InlineData("991231235959", 1999)]
    public void Parse_TwoDigitYear_PivotAt70(string text, int year)
    {
        Assert.True(GemTime.TryParse(text, out var t));
        Assert.Equal(year, t.Year);
    }

    [Fact]
    public void Parse_SixteenDigits_Centiseconds()
    {
        Assert.True(GemTime.TryParse("2026101409050734", out var t));
        Assert.Equal(new DateTime(2026, 10, 14, 9, 5, 7, 340), t);
        Assert.Equal(DateTimeKind.Local, t.Kind);
    }

    [Fact]
    public void Parse_Extended_WithZoneConvertsToLocal_WithoutZoneIsLocal()
    {
        Assert.True(GemTime.TryParse("2026-10-14T00:05:07.34Z", out var utc));
        Assert.Equal(new DateTime(2026, 10, 14, 0, 5, 7, 340, DateTimeKind.Utc).ToLocalTime(), utc);
        Assert.True(GemTime.TryParse("2026-10-14T09:05:07+09:00", out var kst));
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 9, 5, 7, TimeSpan.FromHours(9)).LocalDateTime, kst);
        Assert.True(GemTime.TryParse("2026-10-14T09:05:07.5", out var wall));
        Assert.Equal(new DateTime(2026, 10, 14, 9, 5, 7, 500), wall);
    }

    [Fact]
    public void FormatThenParse_RoundTrip_EachForm()
    {
        Assert.True(GemTime.TryParse(GemTime.Format(T, 0), out var a));
        Assert.Equal(new DateTime(2026, 10, 14, 9, 5, 7), a);
        Assert.True(GemTime.TryParse(GemTime.Format(T, 1), out var b));
        Assert.Equal(new DateTime(2026, 10, 14, 9, 5, 7, 340), b);
        Assert.True(GemTime.TryParse(GemTime.Format(T, 2), out var c));
        Assert.Equal(new DateTime(2026, 10, 14, 9, 5, 7, 340), c);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2610140905")]
    [InlineData("261314090507")]
    [InlineData("260230090507")]
    [InlineData("2026101425000000")]
    [InlineData("26101409050x")]
    [InlineData("2026-10-14 09:05:07")]
    [InlineData("2026-10-14T09:05:07+15:00")]
    [InlineData("2026-13-14T09:05:07Z")]
    [InlineData("2026-10-14T09:05:07+09:99")]                 // 오프셋 분 > 59
    [InlineData("2026-10-14T09:05:07\n")]                      // $는 끝 줄바꿈을 받아 줌 → \z
    [InlineData("2026-10-14T09:05:07Z\n")]
    [InlineData("٢٠٢٦-10-14T09:05:07")]      // ASCII가 아닌 숫자(아랍 숫자)
    [InlineData("2026-10-14T09:05:0٧")]
    [InlineData("2026-10-14T09:05:07.١")]
    [InlineData("9999-12-31T23:59:59")]                        // 연도 1970~2999 밖
    [InlineData("9999123123595999")]
    [InlineData("3000-01-01T00:00:00")]
    [InlineData("1969-12-31T23:59:59")]
    [InlineData("0001-01-01T00:00:00Z")]
    [InlineData("9999-12-31T23:59:59-14:00")]                  // 오프셋 변환이 넘침 → 예외 없이 false
    public void Parse_Invalid_IsFalse(string? text) => Assert.False(GemTime.TryParse(text, out _));
}
