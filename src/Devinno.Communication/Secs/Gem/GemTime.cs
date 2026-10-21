using System.Globalization;
using System.Text.RegularExpressions;

namespace Devinno.Communication.Secs.Gem;

// GEM TIME 텍스트(TimeFormat EC) ↔ DateTime. 모두 로컬 시각이다.
// 0 = A[12] YYMMDDhhmmss, 1 = A[16] YYYYMMDDhhmmsscc(cc = 1/100초), 2 = YYYY-MM-DDThh:mm:ss.ssTZD(소수 2자리, TZD = +09:00 또는 Z).
// 받는 시각은 로컬 연도 1970~2999만(GEM 시계 = 시스템 시각 + 차이 계산이 DateTime 범위를 넘지 않게).
/// <summary>Converts between GEM TIME text (the TimeFormat equipment constant) and local <see cref="DateTime"/> values.</summary>
/// <remarks>The three forms are 0 = A[12] <c>YYMMDDhhmmss</c>, 1 = A[16] <c>YYYYMMDDhhmmsscc</c> (cc = hundredths of a second), and 2 = <c>YYYY-MM-DDThh:mm:ss.ssTZD</c> (TZD = <c>+09:00</c> or <c>Z</c>). All times are local.</remarks>
public static partial class GemTime
{
    [GeneratedRegex(@"^([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.([0-9]{1,7}))?(Z|[+-][0-9]{2}:[0-5][0-9])?\z")]
    private static partial Regex Extended();

    /// <summary>Formats a local time as GEM TIME text.</summary>
    /// <param name="local">The local time. A value whose <see cref="DateTime.Kind"/> is <see cref="DateTimeKind.Utc"/> is converted to local time first.</param>
    /// <param name="timeFormat">The TimeFormat value: 0 (12 digits), 1 (16 digits), or 2 (extended form with time zone).</param>
    /// <returns>The TIME text, for example <c>261021090807</c> for form 0.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeFormat"/> is not 0, 1, or 2.</exception>
    public static string Format(DateTime local, int timeFormat)
    {
        if (local.Kind == DateTimeKind.Utc) local = local.ToLocalTime();
        return timeFormat switch
        {
            0 => local.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture),
            1 => local.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + (local.Millisecond / 10).ToString("00", CultureInfo.InvariantCulture),
            2 => local.ToString("yyyy-MM-dd'T'HH:mm:ss.ff", CultureInfo.InvariantCulture) + Zone(TimeZoneInfo.Local.GetUtcOffset(local)),
            _ => throw new ArgumentOutOfRangeException(nameof(timeFormat), timeFormat, "TimeFormat은 0~2 입니다."),
        };
    }

    static string Zone(TimeSpan offset)
    {
        if (offset == TimeSpan.Zero) return "Z";
        var a = offset.Duration();
        return (offset < TimeSpan.Zero ? "-" : "+") + a.Hours.ToString("00", CultureInfo.InvariantCulture) + ":" + a.Minutes.ToString("00", CultureInfo.InvariantCulture);
    }

    internal const int MinYear = 1970, MaxYear = 2999;

    // 세 형식 모두 받는다(길이 12 / 16 / T 포함, ASCII 숫자만). 2자리 연도 00~69 → 20xx, 70~99 → 19xx. 실제 날짜가 아니거나 로컬 연도가 1970~2999 밖이면 false(예외 없음)
    /// <summary>Reads GEM TIME text in any of the three forms.</summary>
    /// <remarks>A two-digit year 00 to 69 means 20xx and 70 to 99 means 19xx. Text in form 2 with a time zone is converted to local time. Only local years 1970 to 2999 are accepted.</remarks>
    /// <param name="text">The TIME text (12 digits, 16 digits, or the extended form).</param>
    /// <param name="local">The local time read, or <c>default</c> if the text cannot be read.</param>
    /// <returns>true if the text is a valid date and time in range; otherwise false (no exception).</returns>
    public static bool TryParse(string? text, out DateTime local)
    {
        local = default;
        if (text == null) return false;
        if (!Parse(text, out var t) || t.Year is < MinYear or > MaxYear) return false;
        local = t;
        return true;
    }

    static bool Parse(string text, out DateTime local)
    {
        local = default;
        try
        {
            if (text.Length is 12 or 16 && text.All(char.IsAsciiDigit))
            {
                int i = 0;
                int Take(int n) { int v = int.Parse(text.AsSpan(i, n), CultureInfo.InvariantCulture); i += n; return v; }
                int year = text.Length == 12 ? Take(2) : Take(4);
                if (text.Length == 12) year += year < 70 ? 2000 : 1900;
                int month = Take(2), day = Take(2), hour = Take(2), minute = Take(2), second = Take(2);
                int ms = text.Length == 16 ? Take(2) * 10 : 0;
                local = new DateTime(year, month, day, hour, minute, second, ms, DateTimeKind.Local);
                return true;
            }
            var m = Extended().Match(text);
            if (!m.Success) return false;
            int G(int k) => int.Parse(m.Groups[k].Value, CultureInfo.InvariantCulture);
            var wall = new DateTime(G(1), G(2), G(3), G(4), G(5), G(6), DateTimeKind.Unspecified);
            if (m.Groups[7].Success)
                wall = wall.AddTicks(long.Parse(m.Groups[7].Value.PadRight(7, '0'), CultureInfo.InvariantCulture));
            if (!m.Groups[8].Success)
            {
                local = DateTime.SpecifyKind(wall, DateTimeKind.Local);
                return true;
            }
            var z = m.Groups[8].Value;
            var offset = z == "Z" ? TimeSpan.Zero
                : TimeSpan.FromMinutes((z[0] == '-' ? -1 : 1) * (int.Parse(z.AsSpan(1, 2), CultureInfo.InvariantCulture) * 60 + int.Parse(z.AsSpan(4, 2), CultureInfo.InvariantCulture)));
            local = new DateTimeOffset(wall, offset).LocalDateTime;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException) { return false; }
    }
}
