using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

// S2: 장비 상수·시계·리포트 설정·이벤트 사용·리모트 명령(S2F13~S2F42), 3층용 internal S2F40
public static partial class GemMessages
{
    #region S2F13 / S2F14
    /// <summary>Builds S2F13 Equipment Constant Request &lt;L ECID…&gt;.</summary>
    /// <param name="ecids">The ECIDs (null or empty = all ECs).</param>
    /// <param name="formats">The ID formats (null = <see cref="GemFormats.Default"/>); ECIDs use <see cref="GemFormats.Vid"/>.</param>
    /// <returns>The S2F13 message.</returns>
    /// <exception cref="ArgumentException">A text ECID cannot be sent in a numeric format.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric ECID is out of the range of the format.</exception>
    public static SecsMessage S2F13(IReadOnlyList<GemId>? ecids, GemFormats? formats = null) => new(2, 13, true, IdList(ecids, F(formats).Vid));

    /// <summary>Reads S2F13 Equipment Constant Request.</summary>
    /// <remarks>Accepts &lt;L ECID…&gt;, an integer array item, or a single A item, with any integer format or A for each ID (the configured format is not checked). A header-only message or a zero-length item is an empty list (= all ECs).</remarks>
    /// <param name="message">The received S2F13.</param>
    /// <returns>The requested ECIDs.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S2F13.</exception>
    /// <exception cref="FormatException">The body is not an ID list.</exception>
    public static IReadOnlyList<GemId> ParseS2F13(SecsMessage message)
    {
        Check(message, 2, 13);
        return IdVector(message.Item, message, "<L ECID…>");
    }

    /// <summary>Builds S2F14 Equipment Constant Data &lt;L ECV…&gt;.</summary>
    /// <param name="values">The values in the requested order.</param>
    /// <returns>The S2F14 message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> or a value is null.</exception>
    public static SecsMessage S2F14(IReadOnlyList<SecsItem> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new(2, 14, false, SecsItem.L([.. values]));
    }

    /// <summary>Reads S2F14 Equipment Constant Data.</summary>
    /// <param name="message">The received S2F14.</param>
    /// <returns>The values in the order the equipment sent them (an empty list for a header-only message).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S2F14.</exception>
    /// <exception cref="FormatException">The body is not a list.</exception>
    public static IReadOnlyList<SecsItem> ParseS2F14(SecsMessage message)
    {
        Check(message, 2, 14);
        return Entries(message, "<L ECV…>");
    }
    #endregion

    #region S2F15 / S2F16
    /// <summary>Builds S2F15 New Equipment Constant Send &lt;L &lt;L ECID ECV&gt;…&gt;.</summary>
    /// <param name="changes">The ECIDs and their new values.</param>
    /// <param name="formats">The ID formats (null = <see cref="GemFormats.Default"/>); ECIDs use <see cref="GemFormats.Vid"/>.</param>
    /// <returns>The S2F15 message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="changes"/> or a value is null.</exception>
    /// <exception cref="ArgumentException">A text ECID cannot be sent in a numeric format.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric ECID is out of the range of the format.</exception>
    public static SecsMessage S2F15(IReadOnlyList<(GemId Ecid, SecsItem Value)> changes, GemFormats? formats = null)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var vid = F(formats).Vid;
        return new(2, 15, true, SecsItem.L([.. changes.Select(c =>
        {
            ArgumentNullException.ThrowIfNull(c.Value, nameof(changes));
            return SecsItem.L(c.Ecid.ToItem(vid), c.Value);
        })]));
    }

    /// <summary>Reads S2F15 New Equipment Constant Send.</summary>
    /// <param name="message">The received S2F15.</param>
    /// <returns>The ECIDs (any integer format or A) and values in the order received (an empty list for a header-only message).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S2F15.</exception>
    /// <exception cref="FormatException">The body is not &lt;L &lt;L ECID ECV&gt;…&gt;.</exception>
    public static IReadOnlyList<(GemId Ecid, SecsItem Value)> ParseS2F15(SecsMessage message)
    {
        Check(message, 2, 15);
        const string expected = "<L <L ECID ECV>…>";
        return [.. Entries(message, expected).Select(e =>
            e is { Format: SecsFormat.List, Count: 2 } ? (Id(e.Items[0], message, expected), e.Items[1]) : throw Fail(message, expected))];
    }

    /// <summary>Builds S2F16 New Equipment Constant Acknowledge &lt;B EAC&gt;.</summary>
    /// <param name="eac">EAC (0 = accepted, 1 = unknown constant, 2 = busy, 3 = out of range).</param>
    /// <returns>The S2F16 message.</returns>
    public static SecsMessage S2F16(byte eac) => new(2, 16, false, GemItems.Ack(eac));

    /// <summary>Reads S2F16 New Equipment Constant Acknowledge.</summary>
    /// <param name="message">The received S2F16.</param>
    /// <returns>EAC (any numeric format, 0 to 255).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S2F16.</exception>
    /// <exception cref="FormatException">The body is not a single number from 0 to 255.</exception>
    public static byte ParseS2F16(SecsMessage message)
    {
        Check(message, 2, 16);
        return Ack(message.Item, message, "EAC");
    }
    #endregion

    #region S2F17 / S2F18
    /// <summary>Builds S2F17 Date and Time Request (header only, W-bit on).</summary>
    /// <returns>The S2F17 message.</returns>
    public static SecsMessage S2F17() => new(2, 17, true);

    /// <summary>Builds S2F18 Date and Time Data &lt;A TIME&gt;.</summary>
    /// <param name="time">The time (a UTC value is converted to local time).</param>
    /// <param name="timeFormat">The TimeFormat: 0 (12 digits), 1 (16 digits), or 2 (extended form with time zone).</param>
    /// <returns>The S2F18 message.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeFormat"/> is not 0, 1, or 2.</exception>
    public static SecsMessage S2F18(DateTime time, int timeFormat) => new(2, 18, false, SecsItem.A(TimeText(time, timeFormat)));

    /// <summary>Reads S2F18 Date and Time Data.</summary>
    /// <remarks>All three TIME forms are accepted whatever the sender's TimeFormat is (see <see cref="GemTime.TryParse"/>).</remarks>
    /// <param name="message">The received S2F18.</param>
    /// <returns>The local time.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S2F18.</exception>
    /// <exception cref="FormatException">The body is not an A item, or the text is not a valid TIME.</exception>
    public static DateTime ParseS2F18(SecsMessage message)
    {
        Check(message, 2, 18);
        return Time(message);
    }
    #endregion

    #region S2F29 / S2F30
    /// <summary>Builds S2F29 Equipment Constant Namelist Request &lt;L ECID…&gt;.</summary>
    /// <param name="ecids">The ECIDs (null or empty = all ECs).</param>
    /// <param name="formats">The ID formats (null = <see cref="GemFormats.Default"/>); ECIDs use <see cref="GemFormats.Vid"/>.</param>
    /// <returns>The S2F29 message.</returns>
    /// <exception cref="ArgumentException">A text ECID cannot be sent in a numeric format.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric ECID is out of the range of the format.</exception>
    public static SecsMessage S2F29(IReadOnlyList<GemId>? ecids, GemFormats? formats = null) => new(2, 29, true, IdList(ecids, F(formats).Vid));

    /// <summary>Reads S2F29 Equipment Constant Namelist Request.</summary>
    /// <remarks>The accepted shapes are the same as for <see cref="ParseS2F13"/>.</remarks>
    /// <param name="message">The received S2F29.</param>
    /// <returns>The requested ECIDs (empty = all ECs).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S2F29.</exception>
    /// <exception cref="FormatException">The body is not an ID list.</exception>
    public static IReadOnlyList<GemId> ParseS2F29(SecsMessage message)
    {
        Check(message, 2, 29);
        return IdVector(message.Item, message, "<L ECID…>");
    }

    /// <summary>Builds S2F30 Equipment Constant Namelist &lt;L &lt;L ECID ECNAME ECMIN ECMAX ECDEF UNITS&gt;…&gt;.</summary>
    /// <param name="items">The entries in the requested order.</param>
    /// <param name="formats">The ID formats (null = <see cref="GemFormats.Default"/>); ECIDs use <see cref="GemFormats.Vid"/>.</param>
    /// <returns>The S2F30 message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> or an entry is null.</exception>
    /// <exception cref="ArgumentException">A text ECID cannot be sent in a numeric format, or a name has a character above 255.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric ECID is out of the range of the format.</exception>
    public static SecsMessage S2F30(IReadOnlyList<GemConstantInfo> items, GemFormats? formats = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        var vid = F(formats).Vid;
        return new(2, 30, false, SecsItem.L([.. items.Select(i =>
        {
            ArgumentNullException.ThrowIfNull(i, nameof(items));
            return SecsItem.L(i.Id.ToItem(vid), SecsItem.A(i.Name), i.Min, i.Max, i.Default, SecsItem.A(i.Units));
        })]));
    }

    /// <summary>Reads S2F30 Equipment Constant Namelist.</summary>
    /// <param name="message">The received S2F30.</param>
    /// <returns>The entries in the order the equipment sent them (each ID in the format the equipment sent; an empty list for a header-only message).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S2F30.</exception>
    /// <exception cref="FormatException">The body is not &lt;L &lt;L ID A min max default A&gt;…&gt;.</exception>
    public static IReadOnlyList<GemConstantInfo> ParseS2F30(SecsMessage message)
    {
        Check(message, 2, 30);
        const string expected = "<L <L ECID ECNAME ECMIN ECMAX ECDEF UNITS>…>";
        return [.. Entries(message, expected).Select(e =>
            e is { Format: SecsFormat.List, Count: 6 } && e.Items[1].Format == SecsFormat.ASCII && e.Items[5].Format == SecsFormat.ASCII
                ? new GemConstantInfo(Id(e.Items[0], message, expected), e.Items[1].GetString(), e.Items[2], e.Items[3], e.Items[4], e.Items[5].GetString())
                : throw Fail(message, expected))];
    }
    #endregion

    #region S2F31 / S2F32
    /// <summary>Builds S2F31 Date and Time Set Request &lt;A TIME&gt;.</summary>
    /// <param name="time">The time (a UTC value is converted to local time).</param>
    /// <param name="timeFormat">The TimeFormat: 0 (12 digits), 1 (16 digits), or 2 (extended form with time zone).</param>
    /// <returns>The S2F31 message.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeFormat"/> is not 0, 1, or 2.</exception>
    public static SecsMessage S2F31(DateTime time, int timeFormat) => new(2, 31, true, SecsItem.A(TimeText(time, timeFormat)));

    /// <summary>Reads S2F31 Date and Time Set Request.</summary>
    /// <remarks>All three TIME forms are accepted (see <see cref="GemTime.TryParse"/>). GEM equipment answers TIACK 1 when this throws <see cref="FormatException"/>.</remarks>
    /// <param name="message">The received S2F31.</param>
    /// <returns>The local time.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S2F31.</exception>
    /// <exception cref="FormatException">The body is not an A item, or the text is not a valid TIME.</exception>
    public static DateTime ParseS2F31(SecsMessage message)
    {
        Check(message, 2, 31);
        return Time(message);
    }

    /// <summary>Builds S2F32 Date and Time Set Acknowledge &lt;B TIACK&gt;.</summary>
    /// <param name="tiack">TIACK (0 = accepted, 1 = error).</param>
    /// <returns>The S2F32 message.</returns>
    public static SecsMessage S2F32(byte tiack) => new(2, 32, false, GemItems.Ack(tiack));

    /// <summary>Reads S2F32 Date and Time Set Acknowledge.</summary>
    /// <param name="message">The received S2F32.</param>
    /// <returns>TIACK (any numeric format, 0 to 255).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S2F32.</exception>
    /// <exception cref="FormatException">The body is not a single number from 0 to 255.</exception>
    public static byte ParseS2F32(SecsMessage message)
    {
        Check(message, 2, 32);
        return Ack(message.Item, message, "TIACK");
    }
    #endregion

    #region S2F33 / S2F34
    /// <summary>Builds S2F33 Define Report &lt;L DATAID &lt;L &lt;L RPTID &lt;L VID…&gt;&gt;…&gt;&gt;.</summary>
    /// <param name="dataId">DATAID, sent in <see cref="GemFormats.Dataid"/>.</param>
    /// <param name="reports">The reports (an empty list deletes all reports; an empty VID list deletes that report). RPTIDs use <see cref="GemFormats.Rptid"/> and VIDs use <see cref="GemFormats.Vid"/>.</param>
    /// <param name="formats">The ID formats (null = <see cref="GemFormats.Default"/>).</param>
    /// <returns>The S2F33 message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reports"/> or a VID list is null.</exception>
    /// <exception cref="ArgumentException">A text ID cannot be sent in a numeric format.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric ID is out of the range of its format.</exception>
    public static SecsMessage S2F33(GemId dataId, IReadOnlyList<(GemId Rptid, IReadOnlyList<GemId> Vids)> reports, GemFormats? formats = null)
    {
        ArgumentNullException.ThrowIfNull(reports);
        var f = F(formats);
        return new(2, 33, true, SecsItem.L(dataId.ToItem(f.Dataid), SecsItem.L([.. reports.Select(r =>
        {
            ArgumentNullException.ThrowIfNull(r.Vids, nameof(reports));
            return SecsItem.L(r.Rptid.ToItem(f.Rptid), IdList(r.Vids, f.Vid));
        })])));
    }

    /// <summary>Reads S2F33 Define Report.</summary>
    /// <remarks>RPTIDs and VIDs are any integer format or A (the configured formats are not checked). A VID list may be &lt;L VID…&gt;, an integer array item, or a single A item.</remarks>
    /// <param name="message">The received S2F33.</param>
    /// <returns>DATAID as the item received, and the reports (an empty VID list = delete that report; an empty report list = delete all).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S2F33.</exception>
    /// <exception cref="FormatException">The body is not &lt;L DATAID &lt;L &lt;L RPTID &lt;L VID…&gt;&gt;…&gt;&gt;.</exception>
    public static (SecsItem DataId, IReadOnlyList<(GemId Rptid, IReadOnlyList<GemId> Vids)> Reports) ParseS2F33(SecsMessage message)
    {
        Check(message, 2, 33);
        const string expected = "<L DATAID <L <L RPTID <L VID…>>…>>";
        var (dataId, list) = DataAndList(message, expected);
        return (dataId, [.. list.Items.Select(r =>
            r is { Format: SecsFormat.List, Count: 2 } ? (Id(r.Items[0], message, expected), IdVector(r.Items[1], message, expected)) : throw Fail(message, expected))]);
    }

    /// <summary>Builds S2F34 Define Report Acknowledge &lt;B DRACK&gt;.</summary>
    /// <param name="drack">DRACK (0 = accepted).</param>
    /// <returns>The S2F34 message.</returns>
    public static SecsMessage S2F34(byte drack) => new(2, 34, false, GemItems.Ack(drack));

    /// <summary>Reads S2F34 Define Report Acknowledge.</summary>
    /// <param name="message">The received S2F34.</param>
    /// <returns>DRACK (any numeric format, 0 to 255).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S2F34.</exception>
    /// <exception cref="FormatException">The body is not a single number from 0 to 255.</exception>
    public static byte ParseS2F34(SecsMessage message)
    {
        Check(message, 2, 34);
        return Ack(message.Item, message, "DRACK");
    }
    #endregion

    #region S2F35 / S2F36
    /// <summary>Builds S2F35 Link Event Report &lt;L DATAID &lt;L &lt;L CEID &lt;L RPTID…&gt;&gt;…&gt;&gt;.</summary>
    /// <param name="dataId">DATAID, sent in <see cref="GemFormats.Dataid"/>.</param>
    /// <param name="links">The links (an empty RPTID list unlinks that event). CEIDs use <see cref="GemFormats.Ceid"/> and RPTIDs use <see cref="GemFormats.Rptid"/>.</param>
    /// <param name="formats">The ID formats (null = <see cref="GemFormats.Default"/>).</param>
    /// <returns>The S2F35 message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="links"/> or an RPTID list is null.</exception>
    /// <exception cref="ArgumentException">A text ID cannot be sent in a numeric format.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric ID is out of the range of its format.</exception>
    public static SecsMessage S2F35(GemId dataId, IReadOnlyList<(GemId Ceid, IReadOnlyList<GemId> Rptids)> links, GemFormats? formats = null)
    {
        ArgumentNullException.ThrowIfNull(links);
        var f = F(formats);
        return new(2, 35, true, SecsItem.L(dataId.ToItem(f.Dataid), SecsItem.L([.. links.Select(l =>
        {
            ArgumentNullException.ThrowIfNull(l.Rptids, nameof(links));
            return SecsItem.L(l.Ceid.ToItem(f.Ceid), IdList(l.Rptids, f.Rptid));
        })])));
    }

    /// <summary>Reads S2F35 Link Event Report.</summary>
    /// <remarks>The accepted shapes are the same as for <see cref="ParseS2F33"/>.</remarks>
    /// <param name="message">The received S2F35.</param>
    /// <returns>DATAID as the item received, and the links.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S2F35.</exception>
    /// <exception cref="FormatException">The body is not &lt;L DATAID &lt;L &lt;L CEID &lt;L RPTID…&gt;&gt;…&gt;&gt;.</exception>
    public static (SecsItem DataId, IReadOnlyList<(GemId Ceid, IReadOnlyList<GemId> Rptids)> Links) ParseS2F35(SecsMessage message)
    {
        Check(message, 2, 35);
        const string expected = "<L DATAID <L <L CEID <L RPTID…>>…>>";
        var (dataId, list) = DataAndList(message, expected);
        return (dataId, [.. list.Items.Select(l =>
            l is { Format: SecsFormat.List, Count: 2 } ? (Id(l.Items[0], message, expected), IdVector(l.Items[1], message, expected)) : throw Fail(message, expected))]);
    }

    /// <summary>Builds S2F36 Link Event Report Acknowledge &lt;B LRACK&gt;.</summary>
    /// <param name="lrack">LRACK (0 = accepted).</param>
    /// <returns>The S2F36 message.</returns>
    public static SecsMessage S2F36(byte lrack) => new(2, 36, false, GemItems.Ack(lrack));

    /// <summary>Reads S2F36 Link Event Report Acknowledge.</summary>
    /// <param name="message">The received S2F36.</param>
    /// <returns>LRACK (any numeric format, 0 to 255).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S2F36.</exception>
    /// <exception cref="FormatException">The body is not a single number from 0 to 255.</exception>
    public static byte ParseS2F36(SecsMessage message)
    {
        Check(message, 2, 36);
        return Ack(message.Item, message, "LRACK");
    }
    #endregion

    #region S2F37 / S2F38
    /// <summary>Builds S2F37 Enable/Disable Event Report &lt;L &lt;BOOLEAN CEED&gt; &lt;L CEID…&gt;&gt;.</summary>
    /// <param name="enable">true to enable, false to disable.</param>
    /// <param name="ceids">The CEIDs (null or empty = all collection events).</param>
    /// <param name="formats">The ID formats (null = <see cref="GemFormats.Default"/>); CEIDs use <see cref="GemFormats.Ceid"/>.</param>
    /// <returns>The S2F37 message.</returns>
    /// <exception cref="ArgumentException">A text CEID cannot be sent in a numeric format.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric CEID is out of the range of the format.</exception>
    public static SecsMessage S2F37(bool enable, IReadOnlyList<GemId>? ceids, GemFormats? formats = null)
        => new(2, 37, true, SecsItem.L(SecsItem.Boolean(enable), IdList(ceids, F(formats).Ceid)));

    /// <summary>Reads S2F37 Enable/Disable Event Report.</summary>
    /// <remarks>CEED may be BOOLEAN, B, or a single U1 (a nonzero value means enable). The CEID list may be &lt;L CEID…&gt;, an integer array item, or a single A item.</remarks>
    /// <param name="message">The received S2F37.</param>
    /// <returns>(enable, CEIDs); an empty CEID list means all collection events.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S2F37.</exception>
    /// <exception cref="FormatException">The body is not &lt;L CEED &lt;L CEID…&gt;&gt;, or CEED has another format.</exception>
    public static (bool Enable, IReadOnlyList<GemId> Ceids) ParseS2F37(SecsMessage message)
    {
        Check(message, 2, 37);
        const string expected = "<L CEED <L CEID…>>";
        if (message.Item is not { Format: SecsFormat.List, Count: 2 } body) throw Fail(message, expected);
        var ceed = body.Items[0];
        bool enable = ceed switch
        {
            { Format: SecsFormat.Boolean, Count: 1 } => ceed.GetBooleans()[0],
            { Format: SecsFormat.Binary or SecsFormat.U1, Count: 1 } => ceed.GetInt64() != 0,
            _ => throw Fail(message, expected),
        };
        return (enable, IdVector(body.Items[1], message, expected));
    }

    /// <summary>Builds S2F38 Enable/Disable Event Report Acknowledge &lt;B ERACK&gt;.</summary>
    /// <param name="erack">ERACK (0 = accepted, 1 = a CEID does not exist).</param>
    /// <returns>The S2F38 message.</returns>
    public static SecsMessage S2F38(byte erack) => new(2, 38, false, GemItems.Ack(erack));

    /// <summary>Reads S2F38 Enable/Disable Event Report Acknowledge.</summary>
    /// <param name="message">The received S2F38.</param>
    /// <returns>ERACK (any numeric format, 0 to 255).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S2F38.</exception>
    /// <exception cref="FormatException">The body is not a single number from 0 to 255.</exception>
    public static byte ParseS2F38(SecsMessage message)
    {
        Check(message, 2, 38);
        return Ack(message.Item, message, "ERACK");
    }
    #endregion

    #region S2F41 / S2F42
    /// <summary>Builds S2F41 Host Command Send &lt;L RCMD &lt;L &lt;L CPNAME CPVAL&gt;…&gt;&gt;.</summary>
    /// <param name="command">RCMD.</param>
    /// <param name="parameters">The parameters in order (null = none).</param>
    /// <returns>The S2F41 message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/>, a name, or a value is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> or a name has a character above 255.</exception>
    public static SecsMessage S2F41(string command, IReadOnlyList<(string Name, SecsItem Value)>? parameters = null)
    {
        GemDefinitionCheck.Ascii(command, nameof(command));
        var ps = new List<SecsItem>();
        foreach (var (name, value) in parameters ?? [])
        {
            GemDefinitionCheck.Ascii(name, nameof(parameters));
            ArgumentNullException.ThrowIfNull(value, nameof(parameters));
            ps.Add(SecsItem.L(SecsItem.A(name), value));
        }
        return new(2, 41, true, SecsItem.L(SecsItem.A(command), SecsItem.L([.. ps])));
    }

    /// <summary>Reads S2F41 Host Command Send.</summary>
    /// <remarks>RCMD and CPNAME may be an A item or a single integer item (read as a decimal string).</remarks>
    /// <param name="message">The received S2F41.</param>
    /// <returns>The command name and the parameters (name, value) in the order received.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S2F41.</exception>
    /// <exception cref="FormatException">The body is not &lt;L RCMD &lt;L &lt;L CPNAME CPVAL&gt;…&gt;&gt;, or a name is neither A nor an integer.</exception>
    public static (string Command, IReadOnlyList<(string Name, SecsItem Value)> Parameters) ParseS2F41(SecsMessage message)
    {
        var (command, parameters) = ParseS2F41Raw(message);
        return (command, [.. parameters.Select(p => (p.Name, p.Value))]);
    }

    // 장비용: 받은 CPNAME 아이템도 함께 돌려준다(CPACK에 그대로 되돌리기, D3)
    internal static (string Command, IReadOnlyList<(SecsItem NameItem, string Name, SecsItem Value)> Parameters) ParseS2F41Raw(SecsMessage message)
    {
        Check(message, 2, 41);
        const string expected = "<L RCMD <L <L CPNAME CPVAL>…>>";
        if (message.Item is not { Format: SecsFormat.List, Count: 2 } body || body.Items[1].Format != SecsFormat.List) throw Fail(message, expected);
        string command = NameText(body.Items[0], message, expected);
        return (command, [.. body.Items[1].Items.Select(p =>
            p is { Format: SecsFormat.List, Count: 2 } ? (p.Items[0], NameText(p.Items[0], message, expected), p.Items[1]) : throw Fail(message, expected))]);
    }

    /// <summary>Builds S2F42 Host Command Acknowledge &lt;L HCACK &lt;L &lt;L CPNAME CPACK&gt;…&gt;&gt;.</summary>
    /// <param name="hcack">HCACK (see the <c>Hcack...</c> values of <see cref="GemAck"/>).</param>
    /// <param name="parameterAcks">The parameters with an error (CPNAME, CPACK); null = none.</param>
    /// <returns>The S2F42 message.</returns>
    /// <exception cref="ArgumentNullException">A name is null.</exception>
    /// <exception cref="ArgumentException">A name has a character above 255.</exception>
    public static SecsMessage S2F42(byte hcack, IReadOnlyList<(string Name, byte Cpack)>? parameterAcks = null)
        => S2F42(hcack, [.. (parameterAcks ?? []).Select(a => (SecsItem.A(GemDefinitionCheck.Ascii(a.Name, nameof(parameterAcks))), a.Cpack))]);

    // 장비용: 받은 CPNAME 아이템 그대로(D3)
    internal static SecsMessage S2F42(byte hcack, IReadOnlyList<(SecsItem Name, byte Cpack)> parameterAcks)
    {
        ArgumentNullException.ThrowIfNull(parameterAcks);
        return new(2, 42, false, SecsItem.L(GemItems.Ack(hcack), SecsItem.L([.. parameterAcks.Select(a => SecsItem.L(a.Name, GemItems.Ack(a.Cpack)))])));
    }

    /// <summary>Reads S2F42 Host Command Acknowledge.</summary>
    /// <remarks>HCACK and CPACK may be in any numeric format (0 to 255). CPNAME may be an A item or a single integer item (read as a decimal string).</remarks>
    /// <param name="message">The received S2F42.</param>
    /// <returns>HCACK and the parameter acknowledgements.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S2F42.</exception>
    /// <exception cref="FormatException">The body is not &lt;L HCACK &lt;L &lt;L CPNAME CPACK&gt;…&gt;&gt;.</exception>
    public static GemCommandResult ParseS2F42(SecsMessage message)
    {
        Check(message, 2, 42);
        const string expected = "<L HCACK <L <L CPNAME CPACK>…>>";
        if (message.Item is not { Format: SecsFormat.List, Count: 2 } body || body.Items[1].Format != SecsFormat.List) throw Fail(message, expected);
        byte hcack = Ack(body.Items[0], message, expected);
        var acks = body.Items[1].Items.Select(p => p is { Format: SecsFormat.List, Count: 2 }
            ? (Name: NameText(p.Items[0], message, expected), Cpack: Ack(p.Items[1], message, expected))
            : throw Fail(message, expected)).ToList();
        return new GemCommandResult(hcack, acks);
    }
    #endregion

    #region S2F39 / S2F40 (internal, 3층용)
    internal static SecsMessage S2F40(byte grant) => new(2, 40, false, GemItems.Ack(grant));
    #endregion

    #region S2 도우미
    // 시각 → TIME 텍스트. UTC는 로컬로 바꾼다
    static string TimeText(DateTime time, int timeFormat) => GemTime.Format(time.Kind == DateTimeKind.Utc ? time.ToLocalTime() : time, timeFormat);

    // A 하나를 TIME으로. 아니면 Fail
    static DateTime Time(SecsMessage m)
        => m.Item is { Format: SecsFormat.ASCII } item && GemTime.TryParse(item.GetString(), out var time) ? time : throw Fail(m, "TIME (A, 형식 0·1·2)");

    // <L DATAID <L …>> → (DATAID 아이템, 둘째 리스트). 아니면 Fail
    static (SecsItem DataId, SecsItem List) DataAndList(SecsMessage m, string expected)
        => m.Item is { Format: SecsFormat.List, Count: 2 } body && body.Items[1].Format == SecsFormat.List ? (body.Items[0], body.Items[1]) : throw Fail(m, expected);

    // RCMD·CPNAME 텍스트: A(관대하게 정수 하나도 10진 문자열)
    static string NameText(SecsItem item, SecsMessage m, string expected)
    {
        if (item.Format == SecsFormat.ASCII) return item.GetString();
        if (GemItems.IsInteger(item.Format) && item.Count == 1) return Id(item, m, expected).ToString();
        throw Fail(m, expected);
    }
    #endregion
}
