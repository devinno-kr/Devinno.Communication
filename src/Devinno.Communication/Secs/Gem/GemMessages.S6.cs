using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

// S6: 이벤트 리포트(S6F11~S6F20), 3층용 internal S6F6·S6F11Body
public static partial class GemMessages
{
    #region S6F5 / S6F6 (internal, 3층용)
    // Host의 자동 응답 S6F6 <B GRANT6>(S6F5는 어느 층도 보내지 않음)
    internal static SecsMessage S6F6(byte grant6) => new(6, 6, false, GemItems.Ack(grant6));
    #endregion

    #region S6F11 / S6F12
    /// <summary>Builds S6F11 Event Report Send &lt;L DATAID CEID &lt;L &lt;L RPTID &lt;L V…&gt;&gt;…&gt;&gt;.</summary>
    /// <param name="dataId">The DATAID (uses <see cref="GemFormats.Dataid"/>).</param>
    /// <param name="ceid">The CEID (uses <see cref="GemFormats.Ceid"/>).</param>
    /// <param name="reports">The reports in order: the RPTID (uses <see cref="GemFormats.Rptid"/>) and its values.</param>
    /// <param name="formats">The ID formats (null = <see cref="GemFormats.Default"/>).</param>
    /// <param name="replyExpected">true to set the W-bit (default); false when the equipment does not wait for S6F12.</param>
    /// <returns>The S6F11 message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reports"/> or a value is null.</exception>
    /// <exception cref="ArgumentException">A text ID cannot be sent in a numeric format.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric ID is out of the range of the format.</exception>
    public static SecsMessage S6F11(GemId dataId, GemId ceid, IReadOnlyList<(GemId Rptid, IReadOnlyList<SecsItem> Values)> reports, GemFormats? formats = null, bool replyExpected = true)
        => new(6, 11, replyExpected, EventBody(dataId, ceid, reports, F(formats)));

    /// <summary>Reads S6F11 Event Report Send.</summary>
    /// <remarks>
    /// The DATAID is kept as the item the equipment sent. When <paramref name="reportVids"/> is given, each report whose RPTID is in the table and whose value count equals the VID count gets its VIDs; every other report has <see cref="GemReportValue.Vid"/> = null.
    /// The table key and the received RPTID are matched with the same normalization by <see cref="GemFormats.Rptid"/> of <paramref name="formats"/>, so build the table with the same <see cref="GemFormats.Rptid"/>. If it is A, a numeric ID such as 1000 is treated as the text "1000", so a table key 1000 matches an RPTID the equipment sent as A "1000". The text key is looked up first; the numeric key (only for an RPTID that is a plain decimal integer) is tried only when that first lookup misses. If it is numeric, the keys are compared as numbers and a text RPTID never matches.
    /// </remarks>
    /// <param name="message">The received S6F11.</param>
    /// <param name="reportVids">The VIDs of each report definition (null = none known).</param>
    /// <param name="formats">The ID formats (null = <see cref="GemFormats.Default"/>); only <see cref="GemFormats.Rptid"/> is used, to look up the table.</param>
    /// <returns>The interpreted event report.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S6F11.</exception>
    /// <exception cref="FormatException">The body is not &lt;L DATAID CEID &lt;L &lt;L RPTID &lt;L V…&gt;&gt;…&gt;&gt;.</exception>
    public static GemEventReport ParseS6F11(SecsMessage message, IReadOnlyDictionary<GemId, IReadOnlyList<GemId>>? reportVids = null, GemFormats? formats = null)
    {
        Check(message, 6, 11);
        return EventReport(message, reportVids, formats);
    }

    /// <summary>Builds S6F12 Event Report Acknowledge &lt;B ACKC6&gt;.</summary>
    /// <param name="ackc6">ACKC6 (0 = accepted).</param>
    /// <returns>The S6F12 message.</returns>
    public static SecsMessage S6F12(byte ackc6) => new(6, 12, false, GemItems.Ack(ackc6));

    /// <summary>Reads S6F12 Event Report Acknowledge.</summary>
    /// <param name="message">The received S6F12.</param>
    /// <returns>ACKC6 (any numeric format, 0 to 255).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S6F12.</exception>
    /// <exception cref="FormatException">The body is not a single number from 0 to 255.</exception>
    public static byte ParseS6F12(SecsMessage message)
    {
        Check(message, 6, 12);
        return Ack(message.Item, message, "ACKC6");
    }
    #endregion

    #region S6F15 / S6F16
    /// <summary>Builds S6F15 Event Report Request &lt;CEID&gt;.</summary>
    /// <param name="ceid">The CEID.</param>
    /// <param name="formats">The ID formats (null = <see cref="GemFormats.Default"/>); the CEID uses <see cref="GemFormats.Ceid"/>.</param>
    /// <returns>The S6F15 message.</returns>
    /// <exception cref="ArgumentException">A text CEID cannot be sent in a numeric format.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric CEID is out of the range of the format.</exception>
    public static SecsMessage S6F15(GemId ceid, GemFormats? formats = null) => new(6, 15, true, ceid.ToItem(F(formats).Ceid));

    /// <summary>Reads S6F15 Event Report Request.</summary>
    /// <param name="message">The received S6F15.</param>
    /// <returns>The CEID (any integer format or A).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S6F15.</exception>
    /// <exception cref="FormatException">The body is not a single ID.</exception>
    public static GemId ParseS6F15(SecsMessage message)
    {
        Check(message, 6, 15);
        return Id(message.Item, message, "CEID");
    }

    /// <summary>Builds S6F16 Event Report Data (the same body as S6F11).</summary>
    /// <param name="dataId">The DATAID (uses <see cref="GemFormats.Dataid"/>).</param>
    /// <param name="ceid">The CEID (uses <see cref="GemFormats.Ceid"/>).</param>
    /// <param name="reports">The reports in order: the RPTID (uses <see cref="GemFormats.Rptid"/>) and its values.</param>
    /// <param name="formats">The ID formats (null = <see cref="GemFormats.Default"/>).</param>
    /// <returns>The S6F16 message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reports"/> or a value is null.</exception>
    /// <exception cref="ArgumentException">A text ID cannot be sent in a numeric format.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric ID is out of the range of the format.</exception>
    public static SecsMessage S6F16(GemId dataId, GemId ceid, IReadOnlyList<(GemId Rptid, IReadOnlyList<SecsItem> Values)> reports, GemFormats? formats = null)
        => new(6, 16, false, EventBody(dataId, ceid, reports, F(formats)));

    /// <summary>Reads S6F16 Event Report Data. The rules, including the report table key, are the same as for <see cref="ParseS6F11"/>: for an A <see cref="GemFormats.Rptid"/> the numeric key is tried only when the first lookup (the text key normalized like <c>GemItems.Key</c>) misses.</summary>
    /// <param name="message">The received S6F16.</param>
    /// <param name="reportVids">The VIDs of each report definition (null = none known).</param>
    /// <param name="formats">The ID formats (null = <see cref="GemFormats.Default"/>); only <see cref="GemFormats.Rptid"/> is used, to look up the table.</param>
    /// <returns>The interpreted event report.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S6F16.</exception>
    /// <exception cref="FormatException">The body is not &lt;L DATAID CEID &lt;L &lt;L RPTID &lt;L V…&gt;&gt;…&gt;&gt;.</exception>
    public static GemEventReport ParseS6F16(SecsMessage message, IReadOnlyDictionary<GemId, IReadOnlyList<GemId>>? reportVids = null, GemFormats? formats = null)
    {
        Check(message, 6, 16);
        return EventReport(message, reportVids, formats);
    }

    // 3층 장비용: DATAID·CEID·RPTID를 이미 만든 아이템으로(모르는 CEID는 받은 아이템 그대로 되돌리기, D3)
    internal static SecsMessage S6F16(SecsItem dataId, SecsItem ceid, IReadOnlyList<(SecsItem Rptid, IReadOnlyList<SecsItem> Values)> reports)
        => new(6, 16, false, S6F11Body(dataId, ceid, reports));
    #endregion

    #region S6F19 / S6F20
    /// <summary>Builds S6F19 Individual Report Request &lt;RPTID&gt;.</summary>
    /// <param name="rptid">The RPTID.</param>
    /// <param name="formats">The ID formats (null = <see cref="GemFormats.Default"/>); the RPTID uses <see cref="GemFormats.Rptid"/>.</param>
    /// <returns>The S6F19 message.</returns>
    /// <exception cref="ArgumentException">A text RPTID cannot be sent in a numeric format.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric RPTID is out of the range of the format.</exception>
    public static SecsMessage S6F19(GemId rptid, GemFormats? formats = null) => new(6, 19, true, rptid.ToItem(F(formats).Rptid));

    /// <summary>Reads S6F19 Individual Report Request.</summary>
    /// <param name="message">The received S6F19.</param>
    /// <returns>The RPTID (any integer format or A).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S6F19.</exception>
    /// <exception cref="FormatException">The body is not a single ID.</exception>
    public static GemId ParseS6F19(SecsMessage message)
    {
        Check(message, 6, 19);
        return Id(message.Item, message, "RPTID");
    }

    /// <summary>Builds S6F20 Individual Report Data &lt;L V…&gt;.</summary>
    /// <param name="values">The values in the order of the report definition (empty for an unknown RPTID).</param>
    /// <returns>The S6F20 message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> or a value is null.</exception>
    public static SecsMessage S6F20(IReadOnlyList<SecsItem> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new(6, 20, false, SecsItem.L([.. values]));
    }

    /// <summary>Reads S6F20 Individual Report Data.</summary>
    /// <param name="message">The received S6F20.</param>
    /// <returns>The values in the order the equipment sent them (an empty list for a header-only message).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S6F20.</exception>
    /// <exception cref="FormatException">The body is not a list.</exception>
    public static IReadOnlyList<SecsItem> ParseS6F20(SecsMessage message)
    {
        Check(message, 6, 20);
        return Entries(message, "<L V…>");
    }
    #endregion

    #region S6 도우미
    static SecsItem EventBody(GemId dataId, GemId ceid, IReadOnlyList<(GemId Rptid, IReadOnlyList<SecsItem> Values)> reports, GemFormats f)
    {
        ArgumentNullException.ThrowIfNull(reports);
        return S6F11Body(dataId.ToItem(f.Dataid), ceid.ToItem(f.Ceid), [.. reports.Select(r => (r.Rptid.ToItem(f.Rptid), r.Values))]);
    }

    // 장비용: DATAID·CEID·RPTID를 이미 만든 아이템으로. S6F11·S6F16 본문 하나(링크가 없으면 빈 L)
    internal static SecsItem S6F11Body(SecsItem dataId, SecsItem ceid, IReadOnlyList<(SecsItem Rptid, IReadOnlyList<SecsItem> Values)> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);
        return SecsItem.L(dataId, ceid, SecsItem.L([.. reports.Select(r =>
        {
            ArgumentNullException.ThrowIfNull(r.Values, nameof(reports));
            return SecsItem.L(r.Rptid, SecsItem.L([.. r.Values]));
        })]));
    }

    // 표 찾기: 받은 RPTID를 formats.Rptid 기준으로 정규화한 열쇠(GemItems.Key)로. A 포맷이면 표에 숫자 1000으로 넣은 앱도 맞도록 10진 정수 문자열이면 숫자 열쇠도 본다
    static bool TryKnown(IReadOnlyDictionary<GemId, IReadOnlyList<GemId>> table, GemId rptid, SecsFormat f, out IReadOnlyList<GemId> known)
    {
        if (table.TryGetValue(GemItems.Key(rptid, f), out known!)) return true;
        if (f == SecsFormat.ASCII && long.TryParse(rptid.ToString(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var n)
            && n.ToString(System.Globalization.CultureInfo.InvariantCulture) == rptid.ToString())
            return table.TryGetValue(new GemId(n), out known!);
        return false;
    }

    // S6F11·S6F16 해석. 표 열쇠는 formats.Rptid 기준 GemItems.Key, 개수가 다르거나 표에 없으면 Vid = null(경고 문장은 3층이 계산)
    static GemEventReport EventReport(SecsMessage m, IReadOnlyDictionary<GemId, IReadOnlyList<GemId>>? table, GemFormats? formats)
    {
        const string expected = "<L DATAID CEID <L <L RPTID <L V…>>…>>";
        if (m.Item is not { Format: SecsFormat.List, Count: 3 } body || body.Items[2].Format != SecsFormat.List) throw Fail(m, expected);
        var ceid = Id(body.Items[1], m, expected);
        var rptidFormat = F(formats).Rptid;
        var reports = new List<GemReportData>();
        foreach (var r in body.Items[2].Items)
        {
            if (r is not { Format: SecsFormat.List, Count: 2 } || r.Items[1].Format != SecsFormat.List) throw Fail(m, expected);
            var rptid = Id(r.Items[0], m, expected);
            var values = r.Items[1].Items;
            IReadOnlyList<GemId>? vids = null;
            if (table != null && TryKnown(table, rptid, rptidFormat, out var known) && known.Count == values.Count) vids = known;
            reports.Add(new GemReportData(rptid, [.. values.Select((v, i) => new GemReportValue(vids?[i], v))]));
        }
        return new GemEventReport(body.Items[0], ceid, reports);
    }
    #endregion
}
