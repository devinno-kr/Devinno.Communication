namespace Devinno.Communication.Secs.Data;

public sealed partial class SecsItem
{
    /// <summary>Converts the item to SML text. Example: <c>&lt;L [2] &lt;A "MDLN"&gt; &lt;U4 1 2&gt;&gt;</c></summary>
    /// <remarks>
    /// Only a list writes the <c>[n]</c> count. Double quotes, control characters, and bytes of 0x7F and above in an A item are written outside the quotes as <c>0xHH</c> (e.g., <c>&lt;A "ab" 0x0D 0x0A&gt;</c>).
    /// B, J, and C2 are written as a list of <c>0xHH</c> values, BOOLEAN as <c>T</c>/<c>F</c>, and floating-point values as the shortest decimal number that reads back as the same value.
    /// </remarks>
    /// <returns>The SML text (one line).</returns>
    public string ToSml() => SecsSml.Item(this);

    /// <summary>Creates an item from SML text.</summary>
    /// <remarks>
    /// <para>Format names: <c>L B BOOLEAN A J C2 I1 I2 I4 I8 U1 U2 U4 U8 F4 F8</c> (case-insensitive). <c>[n]</c> can be omitted; if given, it is checked against the actual count.</para>
    /// <para>Integers are decimal or <c>0x</c> hexadecimal (hexadecimal in a signed format is accepted only in the positive range), and BOOLEAN is <c>T F true false</c>;
    /// B, J, and C2 are a list of numbers 0 to 255, and A is a concatenation of <c>"…"</c> or <c>'…'</c> strings mixed with numbers 0 to 255 (no escapes inside quotes).</para>
    /// </remarks>
    /// <param name="text">The SML text (one item).</param>
    /// <returns>The item.</returns>
    /// <exception cref="ArgumentNullException">text is null.</exception>
    /// <exception cref="FormatException">The syntax is invalid (the message includes the character position).</exception>
    public static SecsItem ParseSml(string text) => SecsSml.ParseItem(text);

    /// <summary>Returns the SML text (same as <see cref="ToSml"/>).</summary>
    /// <returns>The SML text.</returns>
    public override string ToString() => ToSml();
}
