using System.Globalization;
using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

/// <summary>
/// A single GEM ID (SVID, ECID, DVID, CEID, RPTID, ALID, and so on). It holds either a number (<see cref="long"/>) or a string.
/// </summary>
/// <remarks>
/// <para>Because it converts implicitly from <c>int</c>, <c>long</c>, and <c>string</c>, you can write <c>eq.TriggerEvent(5001)</c> or <c>eq.TriggerEvent("LOT_START")</c>.</para>
/// <para>Equality: numbers are compared by value (U2 5, U4 5, and I4 5 are the same ID). Strings are compared case-sensitively, and a number never equals a string.</para>
/// <para>The item format used when sending is determined by settings such as <c>VidFormat</c> and <c>CeidFormat</c> on <c>GemEquipment</c>. If the format is A and the ID is numeric, it is sent as a decimal string.</para>
/// </remarks>
public readonly struct GemId : IEquatable<GemId>
{
    readonly long number;
    readonly string? text;

    /// <summary>Creates a numeric ID.</summary>
    /// <param name="number">The ID value.</param>
    public GemId(long number)
    {
        this.number = number;
        text = null;
    }

    /// <summary>Creates a string ID.</summary>
    /// <param name="text">The ID string (must not be empty).</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="text"/> is an empty string.</exception>
    public GemId(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) throw new ArgumentException("ID 문자열은 비어 있을 수 없습니다.", nameof(text));
        number = 0;
        this.text = text;
    }

    /// <summary>Gets a value indicating whether this is a string ID.</summary>
    public bool IsText => text != null;

    /// <summary>Gets the value of a numeric ID.</summary>
    /// <exception cref="InvalidOperationException">The ID is a string ID.</exception>
    public long Number => text == null ? number : throw new InvalidOperationException($"문자열 ID(\"{text}\")에는 숫자 값이 없습니다.");

    /// <summary>Gets the string of a string ID, or null for a numeric ID.</summary>
    public string? Text => text;

    /// <summary>Returns a number in decimal and a string as-is.</summary>
    /// <returns>The ID text.</returns>
    public override string ToString() => text ?? number.ToString(CultureInfo.InvariantCulture);

    /// <summary>Determines whether this is the same ID (numbers by value, strings case-sensitively).</summary>
    /// <param name="other">The ID to compare.</param>
    /// <returns>true if the IDs are equal.</returns>
    public bool Equals(GemId other) => text == null ? other.text == null && number == other.number : string.Equals(text, other.text, StringComparison.Ordinal);

    /// <summary>Determines whether this is the same ID.</summary>
    /// <param name="obj">The object to compare.</param>
    /// <returns>true if the IDs are equal.</returns>
    public override bool Equals(object? obj) => obj is GemId other && Equals(other);

    /// <summary>Returns the hash code.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode() => text == null ? number.GetHashCode() : StringComparer.Ordinal.GetHashCode(text);

    /// <summary>Determines whether two IDs are equal.</summary>
    /// <param name="a">The left operand.</param>
    /// <param name="b">The right operand.</param>
    /// <returns>true if the IDs are equal.</returns>
    public static bool operator ==(GemId a, GemId b) => a.Equals(b);

    /// <summary>Determines whether two IDs are different.</summary>
    /// <param name="a">The left operand.</param>
    /// <param name="b">The right operand.</param>
    /// <returns>true if the IDs are different.</returns>
    public static bool operator !=(GemId a, GemId b) => !a.Equals(b);

    /// <summary>Creates an ID from a number.</summary>
    /// <param name="number">The ID value.</param>
    public static implicit operator GemId(int number) => new(number);

    /// <summary>Creates an ID from a number.</summary>
    /// <param name="number">The ID value.</param>
    public static implicit operator GemId(long number) => new(number);

    /// <summary>Creates an ID from a string.</summary>
    /// <param name="text">The ID string.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="text"/> is an empty string.</exception>
    public static implicit operator GemId(string text) => new(text);

    // 보낼 아이템. 숫자 포맷이면 범위 검사(넘으면 ArgumentOutOfRangeException), A면 문자열(숫자는 10진 문자열).
    // 문자열 ID를 숫자 포맷으로, 또는 ID 포맷이 아닌 포맷으로 만들면 ArgumentException.
    internal SecsItem ToItem(SecsFormat format)
    {
        if (format == SecsFormat.ASCII) return SecsItem.A(ToString());
        if (!GemItems.IsInteger(format)) throw new ArgumentException($"ID 포맷은 U1 U2 U4 U8 I1 I2 I4 I8 A 중 하나입니다. 현재: {format}", nameof(format));
        if (text != null) throw new ArgumentException($"문자열 ID \"{text}\"는 숫자 포맷 {format}으로 보낼 수 없습니다.", nameof(format));
        var (min, max) = GemItems.IntegerRange(format);
        if (number < min || number > max)
            throw new ArgumentOutOfRangeException(nameof(format), number, $"ID {number}는 {format} 범위({min}~{max}) 밖입니다.");
        return format switch
        {
            SecsFormat.U1 => SecsItem.U1((byte)number),
            SecsFormat.U2 => SecsItem.U2((ushort)number),
            SecsFormat.U4 => SecsItem.U4((uint)number),
            SecsFormat.U8 => SecsItem.U8((ulong)number),
            SecsFormat.I1 => SecsItem.I1((sbyte)number),
            SecsFormat.I2 => SecsItem.I2((short)number),
            SecsFormat.I4 => SecsItem.I4((int)number),
            _ => SecsItem.I8(number),
        };
    }

    // 받은 아이템에서 ID를 읽는다. 정수 포맷 8종(값 1개)과 A(1글자 이상)만 받고, 그 밖(B·BOOLEAN·F·L·값 0개·2개 이상)은 FormatException.
    internal static GemId FromItem(SecsItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Format == SecsFormat.ASCII)
        {
            if (item.Count == 0) throw new FormatException("ID가 빈 A 아이템입니다.");
            return new GemId(item.GetString());
        }
        if (!GemItems.IsInteger(item.Format) || item.Count != 1)
            throw new FormatException($"ID는 정수 아이템(값 1개) 또는 A여야 합니다. 받은 것: {GemItems.Sml(item)}");
        if (item.Format == SecsFormat.U8)
        {
            ulong u = item.GetUInt64();
            if (u > long.MaxValue) throw new FormatException($"ID {u}는 long 범위를 넘습니다.");
            return new GemId((long)u);
        }
        return new GemId(item.GetInt64());
    }
}
