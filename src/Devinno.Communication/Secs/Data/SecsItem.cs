using System.Text;

namespace Devinno.Communication.Secs.Data;

/// <summary>
/// A SECS-II item (a list or an array of values). It does not change after it is created.
/// </summary>
/// <remarks>
/// <para>Create items with the static factories: <see cref="L"/>, <see cref="B"/>, <see cref="Boolean"/>, <see cref="A"/>, <see cref="J"/>, <see cref="C2"/>,
/// <see cref="I1"/> to <see cref="I8"/>, <see cref="U1"/> to <see cref="U8"/>, <see cref="F4"/>, and <see cref="F8"/>. The arrays passed in are copied and kept.</para>
/// <para>A (ASCII) is stored as Latin-1, so only characters 0 to 255 can be used and 8-bit bytes are preserved as they are. J (JIS-8) and C2 (2-byte character) are handled only as raw bytes (no character conversion).</para>
/// <para>The length of one item is at most 16,777,215 (the number of children for a list, the number of bytes otherwise). A zero-length item is allowed.</para>
/// <para><see cref="Equals(object?)"/> compares the format and the values (including the children for a list).</para>
/// </remarks>
/// <example>
/// <code>
/// var item = SecsItem.L(SecsItem.A("MDLN"), SecsItem.U4(1, 2));
/// string mdln = item.Items[0].GetString();      // "MDLN"
/// long id = item.Items[1].GetInt64();           // 1 (any numeric format works)
/// uint[] ids = item.Items[1].GetValues&lt;uint&gt;(); // [1, 2]
/// </code>
/// </example>
public sealed partial class SecsItem : IEquatable<SecsItem>
{
    /// <summary>Maximum length of one item (3 length bytes): 16,777,215.</summary>
    public const int MaxLength = 0xFFFFFF;

    readonly SecsItem[]? items;
    readonly Array? values;

    SecsItem(SecsFormat format, SecsItem[]? items, Array? values)
    {
        Format = format;
        this.items = items;
        this.values = values;
    }

    #region Properties
    /// <summary>The item format.</summary>
    public SecsFormat Format { get; }

    /// <summary>For a list, the number of child items; otherwise the number of elements (the number of bytes for A, B, J, and C2).</summary>
    public int Count => items?.Length ?? values!.Length;

    /// <summary>The child items of a list.</summary>
    /// <exception cref="InvalidOperationException">The item is not a list.</exception>
    public IReadOnlyList<SecsItem> Items => items ?? throw new InvalidOperationException($"{Format} 아이템은 리스트가 아닙니다.");
    #endregion

    #region Factory
    /// <summary>Creates a list item.</summary>
    /// <param name="items">The child items (0 to 16,777,215).</param>
    /// <returns>The list item.</returns>
    /// <exception cref="ArgumentNullException">The array or a child is null.</exception>
    /// <exception cref="ArgumentException">There are more than 16,777,215 children.</exception>
    public static SecsItem L(params SecsItem[] items)
    {
        ArgumentNullException.ThrowIfNull(items);
        foreach (var i in items) ArgumentNullException.ThrowIfNull(i, nameof(items));
        CheckLength(items.Length, nameof(items));
        return new(SecsFormat.List, [.. items], null);
    }

    /// <summary>Creates a binary (B) item.</summary>
    /// <param name="values">The byte values.</param>
    /// <returns>The B item.</returns>
    /// <exception cref="ArgumentNullException">The array is null.</exception>
    /// <exception cref="ArgumentException">Exceeds 16,777,215 bytes.</exception>
    public static SecsItem B(params byte[] values) => Make(SecsFormat.Binary, values);

    /// <summary>Creates a boolean (BOOLEAN) item.</summary>
    /// <param name="values">The values.</param>
    /// <returns>The BOOLEAN item.</returns>
    /// <exception cref="ArgumentNullException">The array is null.</exception>
    /// <exception cref="ArgumentException">Exceeds 16,777,215 elements.</exception>
    public static SecsItem Boolean(params bool[] values) => Make(SecsFormat.Boolean, values);

    /// <summary>Creates an ASCII (A) item. Characters are stored as Latin-1 (0 to 255).</summary>
    /// <param name="value">The string (may be empty).</param>
    /// <returns>The A item.</returns>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    /// <exception cref="ArgumentException">A character is greater than 255, or the string exceeds 16,777,215 characters.</exception>
    public static SecsItem A(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        foreach (var c in value)
            if (c > 0xFF) throw new ArgumentException($"A 아이템에는 0~255 문자만 쓸 수 있습니다: '{c}' (U+{(int)c:X4})", nameof(value));
        CheckLength(value.Length, nameof(value));
        return new(SecsFormat.ASCII, null, Encoding.Latin1.GetBytes(value));
    }

    /// <summary>Creates a JIS-8 (J) item from raw bytes.</summary>
    /// <param name="bytes">The raw bytes.</param>
    /// <returns>The J item.</returns>
    /// <exception cref="ArgumentNullException">The array is null.</exception>
    /// <exception cref="ArgumentException">Exceeds 16,777,215 bytes.</exception>
    public static SecsItem J(byte[] bytes) => Make(SecsFormat.JIS8, bytes);

    /// <summary>Creates a 2-byte character (C2) item from raw bytes.</summary>
    /// <param name="bytes">The raw bytes (no character conversion is done).</param>
    /// <returns>The C2 item.</returns>
    /// <exception cref="ArgumentNullException">The array is null.</exception>
    /// <exception cref="ArgumentException">Exceeds 16,777,215 bytes.</exception>
    public static SecsItem C2(byte[] bytes) => Make(SecsFormat.C2, bytes);

    /// <summary>Creates an I1 (1-byte signed integer) item.</summary>
    /// <param name="values">The values.</param>
    /// <returns>The I1 item.</returns>
    /// <exception cref="ArgumentNullException">The array is null.</exception>
    /// <exception cref="ArgumentException">Exceeds 16,777,215 bytes.</exception>
    public static SecsItem I1(params sbyte[] values) => Make(SecsFormat.I1, values);
    /// <summary>Creates an I2 (2-byte signed integer) item.</summary>
    /// <param name="values">The values.</param>
    /// <returns>The I2 item.</returns>
    /// <exception cref="ArgumentNullException">The array is null.</exception>
    /// <exception cref="ArgumentException">Exceeds 16,777,215 bytes.</exception>
    public static SecsItem I2(params short[] values) => Make(SecsFormat.I2, values);
    /// <summary>Creates an I4 (4-byte signed integer) item.</summary>
    /// <param name="values">The values.</param>
    /// <returns>The I4 item.</returns>
    /// <exception cref="ArgumentNullException">The array is null.</exception>
    /// <exception cref="ArgumentException">Exceeds 16,777,215 bytes.</exception>
    public static SecsItem I4(params int[] values) => Make(SecsFormat.I4, values);
    /// <summary>Creates an I8 (8-byte signed integer) item.</summary>
    /// <param name="values">The values.</param>
    /// <returns>The I8 item.</returns>
    /// <exception cref="ArgumentNullException">The array is null.</exception>
    /// <exception cref="ArgumentException">Exceeds 16,777,215 bytes.</exception>
    public static SecsItem I8(params long[] values) => Make(SecsFormat.I8, values);
    /// <summary>Creates a U1 (1-byte unsigned integer) item.</summary>
    /// <param name="values">The values.</param>
    /// <returns>The U1 item.</returns>
    /// <exception cref="ArgumentNullException">The array is null.</exception>
    /// <exception cref="ArgumentException">Exceeds 16,777,215 bytes.</exception>
    public static SecsItem U1(params byte[] values) => Make(SecsFormat.U1, values);
    /// <summary>Creates a U2 (2-byte unsigned integer) item.</summary>
    /// <param name="values">The values.</param>
    /// <returns>The U2 item.</returns>
    /// <exception cref="ArgumentNullException">The array is null.</exception>
    /// <exception cref="ArgumentException">Exceeds 16,777,215 bytes.</exception>
    public static SecsItem U2(params ushort[] values) => Make(SecsFormat.U2, values);
    /// <summary>Creates a U4 (4-byte unsigned integer) item.</summary>
    /// <param name="values">The values.</param>
    /// <returns>The U4 item.</returns>
    /// <exception cref="ArgumentNullException">The array is null.</exception>
    /// <exception cref="ArgumentException">Exceeds 16,777,215 bytes.</exception>
    public static SecsItem U4(params uint[] values) => Make(SecsFormat.U4, values);
    /// <summary>Creates a U8 (8-byte unsigned integer) item.</summary>
    /// <param name="values">The values.</param>
    /// <returns>The U8 item.</returns>
    /// <exception cref="ArgumentNullException">The array is null.</exception>
    /// <exception cref="ArgumentException">Exceeds 16,777,215 bytes.</exception>
    public static SecsItem U8(params ulong[] values) => Make(SecsFormat.U8, values);
    /// <summary>Creates an F4 (4-byte floating point) item.</summary>
    /// <param name="values">The values.</param>
    /// <returns>The F4 item.</returns>
    /// <exception cref="ArgumentNullException">The array is null.</exception>
    /// <exception cref="ArgumentException">Exceeds 16,777,215 bytes.</exception>
    public static SecsItem F4(params float[] values) => Make(SecsFormat.F4, values);
    /// <summary>Creates an F8 (8-byte floating point) item.</summary>
    /// <param name="values">The values.</param>
    /// <returns>The F8 item.</returns>
    /// <exception cref="ArgumentNullException">The array is null.</exception>
    /// <exception cref="ArgumentException">Exceeds 16,777,215 bytes.</exception>
    public static SecsItem F8(params double[] values) => Make(SecsFormat.F8, values);

    static SecsItem Make<T>(SecsFormat format, T[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        CheckLength((long)values.Length * ElementSize(format), nameof(values));
        return new(format, null, (T[])values.Clone());
    }

    static void CheckLength(long length, string paramName)
    {
        if (length > MaxLength) throw new ArgumentException($"아이템 길이는 최대 {MaxLength}입니다. 현재: {length}", paramName);
    }

    // 디코더가 쓰는 생성자(배열을 복사하지 않음)
    internal static SecsItem Raw(SecsFormat format, Array values) => new(format, null, values);
    internal static SecsItem RawList(SecsItem[] items) => new(SecsFormat.List, items, null);
    #endregion

    #region Format info
    // 요소 하나의 바이트 수 (리스트는 0)
    internal static int ElementSize(SecsFormat format) => format switch
    {
        SecsFormat.List => 0,
        SecsFormat.I2 or SecsFormat.U2 => 2,
        SecsFormat.I4 or SecsFormat.U4 or SecsFormat.F4 => 4,
        SecsFormat.I8 or SecsFormat.U8 or SecsFormat.F8 => 8,
        _ => 1,
    };

    // 포맷별 요소 형식
    internal static Type ElementType(SecsFormat format) => format switch
    {
        SecsFormat.Boolean => typeof(bool),
        SecsFormat.I1 => typeof(sbyte),
        SecsFormat.I2 => typeof(short),
        SecsFormat.I4 => typeof(int),
        SecsFormat.I8 => typeof(long),
        SecsFormat.U2 => typeof(ushort),
        SecsFormat.U4 => typeof(uint),
        SecsFormat.U8 => typeof(ulong),
        SecsFormat.F4 => typeof(float),
        SecsFormat.F8 => typeof(double),
        SecsFormat.List => typeof(SecsItem),
        _ => typeof(byte),   // B, A, J, C2, U1
    };

    // 내부 값 배열 (복사 안 함, 코덱·SML용)
    internal Array RawValues => values ?? throw new InvalidOperationException("리스트 아이템입니다.");
    #endregion

    #region Getters
    /// <summary>Returns the string of an A (ASCII) item (Latin-1).</summary>
    /// <returns>The string.</returns>
    /// <exception cref="InvalidOperationException">The item is not an A item.</exception>
    public string GetString()
    {
        if (Format != SecsFormat.ASCII) throw WrongFormat("A");
        return Encoding.Latin1.GetString((byte[])values!);
    }

    /// <summary>Returns a copy of the raw bytes of a B, A, J, or C2 item.</summary>
    /// <returns>The byte array (a copy).</returns>
    /// <exception cref="InvalidOperationException">The item is not a B, A, J, or C2 item.</exception>
    public byte[] GetBytes()
    {
        if (Format is not (SecsFormat.Binary or SecsFormat.ASCII or SecsFormat.JIS8 or SecsFormat.C2)) throw WrongFormat("B, A, J, C2");
        return (byte[])values!.Clone();
    }

    /// <summary>Returns a copy of the values of a BOOLEAN item.</summary>
    /// <returns>The value array (a copy).</returns>
    /// <exception cref="InvalidOperationException">The item is not a BOOLEAN item.</exception>
    public bool[] GetBooleans()
    {
        if (Format != SecsFormat.Boolean) throw WrongFormat("BOOLEAN");
        return (bool[])values!.Clone();
    }

    /// <summary>Returns a copy of the value array in its element type.</summary>
    /// <remarks>T must match the format: B and U1 = byte, BOOLEAN = bool, I1 = sbyte, I2 = short, I4 = int, I8 = long, U2 = ushort, U4 = uint, U8 = ulong, F4 = float, F8 = double.</remarks>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>The value array (a copy).</returns>
    /// <exception cref="InvalidOperationException">The item is a list, A, J, or C2, or T differs from the format.</exception>
    public T[] GetValues<T>()
    {
        if (Format is SecsFormat.List or SecsFormat.ASCII or SecsFormat.JIS8 or SecsFormat.C2 || ElementType(Format) != typeof(T))
            throw new InvalidOperationException($"{Format} 아이템은 {typeof(T).Name} 값으로 읽을 수 없습니다.");
        return (T[])values!.Clone();
    }

    /// <summary>Returns the first value of a numeric item (I1 to I8, U1 to U8, F4, F8, B) as a long. A floating-point value is truncated toward zero.</summary>
    /// <remarks>This lets an ID be read regardless of which integer format the peer sends it in.</remarks>
    /// <returns>The first value.</returns>
    /// <exception cref="InvalidOperationException">The item is not numeric or is empty.</exception>
    /// <exception cref="OverflowException">The value is outside the range of long.</exception>
    public long GetInt64() => First() switch
    {
        ulong u => checked((long)u),
        float f => checked((long)f),
        double d => checked((long)d),
        var v => Convert.ToInt64(v),
    };

    /// <summary>Returns the first value of a numeric item (I1 to I8, U1 to U8, F4, F8, B) as a ulong. A floating-point value is truncated toward zero.</summary>
    /// <returns>The first value.</returns>
    /// <exception cref="InvalidOperationException">The item is not numeric or is empty.</exception>
    /// <exception cref="OverflowException">The value is negative or outside the range of ulong.</exception>
    public ulong GetUInt64() => First() switch
    {
        sbyte v => checked((ulong)v),
        short v => checked((ulong)v),
        int v => checked((ulong)v),
        long v => checked((ulong)v),
        float f => checked((ulong)f),
        double d => checked((ulong)d),
        var v => Convert.ToUInt64(v),
    };

    /// <summary>Returns the first value of a numeric item (I1 to I8, U1 to U8, F4, F8, B) as a double.</summary>
    /// <returns>The first value.</returns>
    /// <exception cref="InvalidOperationException">The item is not numeric or is empty.</exception>
    public double GetDouble() => Convert.ToDouble(First());

    object First()
    {
        if (Format is SecsFormat.List or SecsFormat.Boolean or SecsFormat.ASCII or SecsFormat.JIS8 or SecsFormat.C2) throw WrongFormat("숫자");
        if (values!.Length == 0) throw new InvalidOperationException($"{Format} 아이템이 비어 있습니다.");
        return values.GetValue(0)!;
    }

    InvalidOperationException WrongFormat(string expected) => new($"{Format} 아이템입니다. 이 메서드는 {expected} 아이템에만 쓸 수 있습니다.");
    #endregion

    #region Equality
    /// <summary>Compares whether the format and the values (including the children for a list) are equal.</summary>
    /// <param name="other">The item to compare with.</param>
    /// <returns>true if they are equal.</returns>
    public bool Equals(SecsItem? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null || other.Format != Format || other.Count != Count) return false;
        if (items != null)
        {
            for (int i = 0; i < items.Length; i++)
                if (!items[i].Equals(other.items![i])) return false;
            return true;
        }
        for (int i = 0; i < values!.Length; i++)
            if (!Equals(values.GetValue(i), other.values!.GetValue(i))) return false;
        return true;
    }

    /// <summary>Compares whether the format and the values (including the children for a list) are equal.</summary>
    /// <param name="obj">The object to compare with.</param>
    /// <returns>true if they are equal.</returns>
    public override bool Equals(object? obj) => Equals(obj as SecsItem);

    /// <summary>A hash code built from the format and the values.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Format);
        h.Add(Count);
        if (items != null) foreach (var i in items.Take(8)) h.Add(i);
        else for (int i = 0; i < Math.Min(values!.Length, 8); i++) h.Add(values.GetValue(i));
        return h.ToHashCode();
    }
    #endregion
}
