using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

// S1: 통신 수립·온라인·상태 값·이름(S1F1~S1F18), 3층용 internal S1F21~S1F24
public static partial class GemMessages
{
    #region S1F1 / S1F2
    /// <summary>Builds S1F1 Are You There (header only, W-bit on).</summary>
    /// <returns>The S1F1 message.</returns>
    public static SecsMessage S1F1() => new(1, 1, true);

    /// <summary>Builds S1F2 On Line Data &lt;L MDLN SOFTREV&gt;, or &lt;L&gt; when both names are null (the Host form).</summary>
    /// <param name="modelName">MDLN, or null together with <paramref name="softwareRevision"/>.</param>
    /// <param name="softwareRevision">SOFTREV, or null together with <paramref name="modelName"/>.</param>
    /// <returns>The S1F2 message.</returns>
    /// <exception cref="ArgumentException">Only one of the names is null, or a name has a character above 255.</exception>
    public static SecsMessage S1F2(string? modelName = null, string? softwareRevision = null) => new(1, 2, false, Model(modelName, softwareRevision));

    /// <summary>Reads S1F2 On Line Data.</summary>
    /// <param name="message">The received S1F2.</param>
    /// <returns>(MDLN, SOFTREV). Both are "" for an empty &lt;L&gt;.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S1F2.</exception>
    /// <exception cref="FormatException">The body is neither &lt;L A A&gt; nor &lt;L&gt;.</exception>
    public static (string ModelName, string SoftwareRevision) ParseS1F2(SecsMessage message)
    {
        Check(message, 1, 2);
        if (message.Item is { Format: SecsFormat.List, Count: 0 }) return ("", "");
        return ModelOf(message.Item) ?? throw Fail(message, "<L MDLN SOFTREV> 또는 <L>");
    }
    #endregion

    #region S1F3 / S1F4
    /// <summary>Builds S1F3 Selected Equipment Status Request &lt;L SVID…&gt;.</summary>
    /// <param name="svids">The SVIDs (null or empty = all SVs).</param>
    /// <param name="formats">The ID formats (null = <see cref="GemFormats.Default"/>); SVIDs use <see cref="GemFormats.Vid"/>.</param>
    /// <returns>The S1F3 message.</returns>
    /// <exception cref="ArgumentException">A text SVID cannot be sent in a numeric format.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric SVID is out of the range of the format.</exception>
    public static SecsMessage S1F3(IReadOnlyList<GemId>? svids, GemFormats? formats = null) => new(1, 3, true, IdList(svids, F(formats).Vid));

    /// <summary>Reads S1F3 Selected Equipment Status Request.</summary>
    /// <remarks>Accepts &lt;L SVID…&gt;, an integer array item, or a single A item, with any integer format or A for each ID (the configured format is not checked). A header-only message or a zero-length item is an empty list (= all SVs).</remarks>
    /// <param name="message">The received S1F3.</param>
    /// <returns>The requested SVIDs.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S1F3.</exception>
    /// <exception cref="FormatException">The body is not an ID list.</exception>
    public static IReadOnlyList<GemId> ParseS1F3(SecsMessage message)
    {
        Check(message, 1, 3);
        return IdVector(message.Item, message, "<L SVID…>");
    }

    /// <summary>Builds S1F4 Selected Equipment Status Data &lt;L SV…&gt;.</summary>
    /// <param name="values">The values in the requested order.</param>
    /// <returns>The S1F4 message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> or a value is null.</exception>
    public static SecsMessage S1F4(IReadOnlyList<SecsItem> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new(1, 4, false, SecsItem.L([.. values]));
    }

    /// <summary>Reads S1F4 Selected Equipment Status Data.</summary>
    /// <param name="message">The received S1F4.</param>
    /// <returns>The values in the order the equipment sent them (an empty list for a header-only message).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S1F4.</exception>
    /// <exception cref="FormatException">The body is not a list.</exception>
    public static IReadOnlyList<SecsItem> ParseS1F4(SecsMessage message)
    {
        Check(message, 1, 4);
        return Entries(message, "<L SV…>");
    }
    #endregion

    #region S1F11 / S1F12
    /// <summary>Builds S1F11 Status Variable Namelist Request &lt;L SVID…&gt;.</summary>
    /// <param name="svids">The SVIDs (null or empty = all SVs).</param>
    /// <param name="formats">The ID formats (null = <see cref="GemFormats.Default"/>); SVIDs use <see cref="GemFormats.Vid"/>.</param>
    /// <returns>The S1F11 message.</returns>
    /// <exception cref="ArgumentException">A text SVID cannot be sent in a numeric format.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric SVID is out of the range of the format.</exception>
    public static SecsMessage S1F11(IReadOnlyList<GemId>? svids, GemFormats? formats = null) => new(1, 11, true, IdList(svids, F(formats).Vid));

    /// <summary>Reads S1F11 Status Variable Namelist Request.</summary>
    /// <remarks>The accepted shapes are the same as for <see cref="ParseS1F3"/>.</remarks>
    /// <param name="message">The received S1F11.</param>
    /// <returns>The requested SVIDs (empty = all SVs).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S1F11.</exception>
    /// <exception cref="FormatException">The body is not an ID list.</exception>
    public static IReadOnlyList<GemId> ParseS1F11(SecsMessage message)
    {
        Check(message, 1, 11);
        return IdVector(message.Item, message, "<L SVID…>");
    }

    /// <summary>Builds S1F12 Status Variable Namelist Reply &lt;L &lt;L SVID SVNAME UNITS&gt;…&gt;.</summary>
    /// <param name="items">The entries in the requested order (an unknown SVID has an empty name and units).</param>
    /// <param name="formats">The ID formats (null = <see cref="GemFormats.Default"/>); SVIDs use <see cref="GemFormats.Vid"/>.</param>
    /// <returns>The S1F12 message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> or an entry is null.</exception>
    /// <exception cref="ArgumentException">A text SVID cannot be sent in a numeric format, or a name has a character above 255.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric SVID is out of the range of the format.</exception>
    public static SecsMessage S1F12(IReadOnlyList<GemVariableInfo> items, GemFormats? formats = null) => new(1, 12, false, Names(items, F(formats)));

    /// <summary>Reads S1F12 Status Variable Namelist Reply.</summary>
    /// <param name="message">The received S1F12.</param>
    /// <returns>The entries in the order the equipment sent them (each ID in the format the equipment sent).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S1F12.</exception>
    /// <exception cref="FormatException">The body is not &lt;L &lt;L ID A A&gt;…&gt;.</exception>
    public static IReadOnlyList<GemVariableInfo> ParseS1F12(SecsMessage message)
    {
        Check(message, 1, 12);
        return ReadNames(message);
    }

    // 3층 장비용: 받은 ID 아이템을 그대로 되돌린다(모르는 SVID, D3)
    internal static SecsMessage S1F12(IReadOnlyList<(SecsItem Id, string Name, string Units)> items) => new(1, 12, false, Names(items));
    #endregion

    #region S1F13 / S1F14
    /// <summary>Builds S1F13 Establish Communications Request: &lt;L&gt; when both names are null (the Host form), otherwise &lt;L MDLN SOFTREV&gt; (the equipment form).</summary>
    /// <param name="modelName">MDLN, or null together with <paramref name="softwareRevision"/>.</param>
    /// <param name="softwareRevision">SOFTREV, or null together with <paramref name="modelName"/>.</param>
    /// <returns>The S1F13 message.</returns>
    /// <exception cref="ArgumentException">Only one of the names is null, or a name has a character above 255.</exception>
    public static SecsMessage S1F13(string? modelName = null, string? softwareRevision = null) => new(1, 13, true, Model(modelName, softwareRevision));

    /// <summary>Reads S1F13 Establish Communications Request.</summary>
    /// <remarks>Like GEM equipment and Host, it does not reject the body: any shape other than &lt;L A A&gt; gives null names.</remarks>
    /// <param name="message">The received S1F13.</param>
    /// <returns>(MDLN, SOFTREV), or (null, null) for &lt;L&gt; or any other shape.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S1F13.</exception>
    public static (string? ModelName, string? SoftwareRevision) ParseS1F13(SecsMessage message)
    {
        Check(message, 1, 13);
        return ModelOf(message.Item) is { } model ? (model.Item1, model.Item2) : (null, null);
    }

    /// <summary>Builds S1F14 Establish Communications Request Acknowledge &lt;L COMMACK &lt;L MDLN SOFTREV&gt;&gt; (&lt;L COMMACK &lt;L&gt;&gt; when both names are null).</summary>
    /// <param name="commack">COMMACK (0 = accepted, 1 = denied).</param>
    /// <param name="modelName">MDLN, or null together with <paramref name="softwareRevision"/>.</param>
    /// <param name="softwareRevision">SOFTREV, or null together with <paramref name="modelName"/>.</param>
    /// <returns>The S1F14 message.</returns>
    /// <exception cref="ArgumentException">Only one of the names is null, or a name has a character above 255.</exception>
    public static SecsMessage S1F14(byte commack, string? modelName = null, string? softwareRevision = null)
        => new(1, 14, false, SecsItem.L(GemItems.Ack(commack), Model(modelName, softwareRevision)));

    /// <summary>Reads S1F14 Establish Communications Request Acknowledge.</summary>
    /// <remarks>COMMACK may be in any numeric format (0 to 255). The names are read only when the second item is &lt;L A A&gt;; any other second item gives null names without an exception.</remarks>
    /// <param name="message">The received S1F14.</param>
    /// <returns>(COMMACK, MDLN, SOFTREV).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S1F14.</exception>
    /// <exception cref="FormatException">The body is not a two-item list, or COMMACK is not a number from 0 to 255.</exception>
    public static (byte Commack, string? ModelName, string? SoftwareRevision) ParseS1F14(SecsMessage message)
    {
        Check(message, 1, 14);
        const string expected = "<L COMMACK <L MDLN SOFTREV>>";
        if (message.Item is not { Format: SecsFormat.List, Count: 2 } body) throw Fail(message, expected);
        byte ack = Ack(body.Items[0], message, expected);
        return ModelOf(body.Items[1]) is { } model ? (ack, model.Item1, model.Item2) : (ack, null, null);
    }
    #endregion

    #region S1F15 ~ S1F18
    /// <summary>Builds S1F15 Request OFF-LINE (header only, W-bit on).</summary>
    /// <returns>The S1F15 message.</returns>
    public static SecsMessage S1F15() => new(1, 15, true);

    /// <summary>Builds S1F16 OFF-LINE Acknowledge &lt;B OFLACK&gt;.</summary>
    /// <param name="oflack">OFLACK (0 = accepted).</param>
    /// <returns>The S1F16 message.</returns>
    public static SecsMessage S1F16(byte oflack) => new(1, 16, false, GemItems.Ack(oflack));

    /// <summary>Reads S1F16 OFF-LINE Acknowledge.</summary>
    /// <param name="message">The received S1F16.</param>
    /// <returns>OFLACK (any numeric format, 0 to 255).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S1F16.</exception>
    /// <exception cref="FormatException">The body is not a single number from 0 to 255.</exception>
    public static byte ParseS1F16(SecsMessage message)
    {
        Check(message, 1, 16);
        return Ack(message.Item, message, "OFLACK");
    }

    /// <summary>Builds S1F17 Request ON-LINE (header only, W-bit on).</summary>
    /// <returns>The S1F17 message.</returns>
    public static SecsMessage S1F17() => new(1, 17, true);

    /// <summary>Builds S1F18 ON-LINE Acknowledge &lt;B ONLACK&gt;.</summary>
    /// <param name="onlack">ONLACK (0 = accepted, 1 = not allowed, 2 = already ON-LINE).</param>
    /// <returns>The S1F18 message.</returns>
    public static SecsMessage S1F18(byte onlack) => new(1, 18, false, GemItems.Ack(onlack));

    /// <summary>Reads S1F18 ON-LINE Acknowledge.</summary>
    /// <param name="message">The received S1F18.</param>
    /// <returns>ONLACK (any numeric format, 0 to 255).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S1F18.</exception>
    /// <exception cref="FormatException">The body is not a single number from 0 to 255.</exception>
    public static byte ParseS1F18(SecsMessage message)
    {
        Check(message, 1, 18);
        return Ack(message.Item, message, "ONLACK");
    }
    #endregion

    #region S1F21 ~ S1F24 (internal, 3층용)
    internal static SecsMessage S1F21(IReadOnlyList<GemId>? vids, GemFormats? formats = null) => new(1, 21, true, IdList(vids, F(formats).Vid));

    internal static IReadOnlyList<GemId> ParseS1F21(SecsMessage message)
    {
        Check(message, 1, 21);
        return IdVector(message.Item, message, "<L VID…>");
    }

    internal static SecsMessage S1F22(IReadOnlyList<GemVariableInfo> items, GemFormats? formats = null) => new(1, 22, false, Names(items, F(formats)));

    internal static SecsMessage S1F22(IReadOnlyList<(SecsItem Id, string Name, string Units)> items) => new(1, 22, false, Names(items));

    internal static IReadOnlyList<GemVariableInfo> ParseS1F22(SecsMessage message)
    {
        Check(message, 1, 22);
        return ReadNames(message);
    }

    internal static SecsMessage S1F23(IReadOnlyList<GemId>? ceids, GemFormats? formats = null) => new(1, 23, true, IdList(ceids, F(formats).Ceid));

    internal static IReadOnlyList<GemId> ParseS1F23(SecsMessage message)
    {
        Check(message, 1, 23);
        return IdVector(message.Item, message, "<L CEID…>");
    }

    // <L <L CEID CENAME <L VID…>>…>. CEID는 받은 아이템 그대로(모르는 CEID 되돌리기), VID는 formats.Vid
    internal static SecsMessage S1F24(IReadOnlyList<(SecsItem Ceid, string Name, IReadOnlyList<GemId> Vids)> items, GemFormats? formats = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        var vid = F(formats).Vid;
        return new(1, 24, false, SecsItem.L([.. items.Select(e => SecsItem.L(e.Ceid, SecsItem.A(e.Name), IdList(e.Vids, vid)))]));
    }

    // 지금 GemHost 규칙: 항목 <L ID A <L VID…>>, VID 목록은 리스트만
    internal static IReadOnlyList<GemEventInfo> ParseS1F24(SecsMessage message)
    {
        Check(message, 1, 24);
        const string expected = "<L <L CEID CENAME <L VID…>>…>";
        return [.. Entries(message, expected).Select(e =>
            e is { Format: SecsFormat.List, Count: 3 } && e.Items[1].Format == SecsFormat.ASCII && e.Items[2].Format == SecsFormat.List
                ? new GemEventInfo(Id(e.Items[0], message, expected), e.Items[1].GetString(), [.. e.Items[2].Items.Select(v => Id(v, message, expected))])
                : throw Fail(message, expected))];
    }
    #endregion

    #region S1 도우미
    // 이름 두 개: 둘 다 null = <L>, 하나만 null = ArgumentException
    static SecsItem Model(string? modelName, string? softwareRevision)
    {
        if (modelName == null && softwareRevision == null) return SecsItem.L();
        if (modelName == null || softwareRevision == null)
            throw new ArgumentException("MDLN과 SOFTREV는 둘 다 주거나 둘 다 null이어야 합니다.", modelName == null ? nameof(modelName) : nameof(softwareRevision));
        return SecsItem.L(SecsItem.A(modelName), SecsItem.A(softwareRevision));
    }

    // <L A A>이면 두 문자열, 아니면 null
    static (string, string)? ModelOf(SecsItem? item)
        => item is { Format: SecsFormat.List, Count: 2 } && item.Items[0].Format == SecsFormat.ASCII && item.Items[1].Format == SecsFormat.ASCII
            ? (item.Items[0].GetString(), item.Items[1].GetString())
            : null;

    static SecsItem Names(IReadOnlyList<GemVariableInfo> items, GemFormats formats)
    {
        ArgumentNullException.ThrowIfNull(items);
        return SecsItem.L([.. items.Select(i =>
        {
            ArgumentNullException.ThrowIfNull(i, nameof(items));
            return SecsItem.L(i.Id.ToItem(formats.Vid), SecsItem.A(i.Name), SecsItem.A(i.Units));
        })]);
    }

    static SecsItem Names(IReadOnlyList<(SecsItem Id, string Name, string Units)> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return SecsItem.L([.. items.Select(i => SecsItem.L(i.Id, SecsItem.A(i.Name), SecsItem.A(i.Units)))]);
    }

    // 지금 GemHost 규칙: 항목 <L ID A A>
    static IReadOnlyList<GemVariableInfo> ReadNames(SecsMessage message)
    {
        const string expected = "<L <L VID NAME UNITS>…>";
        return [.. Entries(message, expected).Select(e =>
            e is { Format: SecsFormat.List, Count: 3 } && e.Items[1].Format == SecsFormat.ASCII && e.Items[2].Format == SecsFormat.ASCII
                ? new GemVariableInfo(Id(e.Items[0], message, expected), e.Items[1].GetString(), e.Items[2].GetString())
                : throw Fail(message, expected))];
    }
    #endregion
}
