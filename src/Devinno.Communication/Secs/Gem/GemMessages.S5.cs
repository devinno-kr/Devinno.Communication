using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

// S5: 알람(S5F1~S5F8), 3층용 internal S5F6Entries
public static partial class GemMessages
{
    #region S5F1 / S5F2
    /// <summary>Builds S5F1 Alarm Report Send &lt;L ALCD ALID ALTX&gt;.</summary>
    /// <param name="alcd">ALCD (0x80 = alarm set, lower 7 bits = category).</param>
    /// <param name="alid">The ALID.</param>
    /// <param name="text">ALTX.</param>
    /// <param name="formats">The ID formats (null = <see cref="GemFormats.Default"/>); the ALID uses <see cref="GemFormats.Alid"/>.</param>
    /// <param name="replyExpected">true to set the W-bit (default); false when the equipment does not wait for S5F2.</param>
    /// <returns>The S5F1 message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="ArgumentException">A text ALID cannot be sent in a numeric format, or <paramref name="text"/> has a character above 255.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric ALID is out of the range of the format.</exception>
    public static SecsMessage S5F1(byte alcd, GemId alid, string text, GemFormats? formats = null, bool replyExpected = true)
        => new(5, 1, replyExpected, AlarmItem(alcd, alid.ToItem(F(formats).Alid), GemDefinitionCheck.Ascii(text, nameof(text))));

    /// <summary>Reads S5F1 Alarm Report Send.</summary>
    /// <remarks>A zero-length ALCD throws <see cref="FormatException"/>, unlike <see cref="ParseS5F6"/> and <see cref="ParseS5F8"/>, where it reads as 0.</remarks>
    /// <param name="message">The received S5F1.</param>
    /// <returns>The alarm (ALID in any integer format or A, ALCD in any numeric format).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S5F1.</exception>
    /// <exception cref="FormatException">The body is not &lt;L ALCD ALID ALTX&gt;.</exception>
    public static GemAlarmInfo ParseS5F1(SecsMessage message)
    {
        Check(message, 5, 1);
        return Alarm(message.Item, message, "<L ALCD ALID ALTX>", emptyAlcd: false);
    }

    /// <summary>Builds S5F2 Alarm Report Acknowledge &lt;B ACKC5&gt;.</summary>
    /// <param name="ackc5">ACKC5 (0 = accepted).</param>
    /// <returns>The S5F2 message.</returns>
    public static SecsMessage S5F2(byte ackc5) => new(5, 2, false, GemItems.Ack(ackc5));

    /// <summary>Reads S5F2 Alarm Report Acknowledge.</summary>
    /// <param name="message">The received S5F2.</param>
    /// <returns>ACKC5 (any numeric format, 0 to 255).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S5F2.</exception>
    /// <exception cref="FormatException">The body is not a single number from 0 to 255.</exception>
    public static byte ParseS5F2(SecsMessage message)
    {
        Check(message, 5, 2);
        return Ack(message.Item, message, "ACKC5");
    }
    #endregion

    #region S5F3 / S5F4
    /// <summary>Builds S5F3 Enable/Disable Alarm Send &lt;L ALED ALID&gt;.</summary>
    /// <param name="enable">true to enable (ALED 0x80), false to disable (ALED 0x00).</param>
    /// <param name="alid">The ALID (null = a zero-length ALID, meaning all alarms).</param>
    /// <param name="formats">The ID formats (null = <see cref="GemFormats.Default"/>); the ALID uses <see cref="GemFormats.Alid"/>.</param>
    /// <returns>The S5F3 message.</returns>
    /// <exception cref="ArgumentException">A text ALID cannot be sent in a numeric format.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric ALID is out of the range of the format.</exception>
    public static SecsMessage S5F3(bool enable, GemId? alid, GemFormats? formats = null)
    {
        var f = F(formats).Alid;
        return new(5, 3, true, SecsItem.L(SecsItem.B(enable ? GemAck.AledEnable : (byte)0), alid is { } a ? a.ToItem(f) : GemItems.Empty(f)));
    }

    /// <summary>Reads S5F3 Enable/Disable Alarm Send.</summary>
    /// <remarks>ALED may be B, a single U1 (the 0x80 bit means enable), or BOOLEAN. The ALID may be a zero-length item (null = all alarms), a single ID item, or a list holding one ID.</remarks>
    /// <param name="message">The received S5F3.</param>
    /// <returns>(enable, ALID); a null ALID means all alarms.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S5F3.</exception>
    /// <exception cref="FormatException">The body is not &lt;L ALED ALID&gt;, ALED has another format, or there are two or more ALIDs.</exception>
    public static (bool Enable, GemId? Alid) ParseS5F3(SecsMessage message)
    {
        Check(message, 5, 3);
        const string expected = "<L ALED ALID>";
        if (message.Item is not { Format: SecsFormat.List, Count: 2 } body) throw Fail(message, expected);
        var aled = body.Items[0];
        bool enable = aled switch
        {
            { Format: SecsFormat.Boolean, Count: 1 } => aled.GetBooleans()[0],
            { Format: SecsFormat.Binary or SecsFormat.U1, Count: 1 } => (aled.GetInt64() & GemAck.AledEnable) != 0,
            _ => throw Fail(message, expected),
        };
        var ids = IdVector(body.Items[1], message, expected);
        if (ids.Count > 1) throw Fail(message, expected);
        return (enable, ids.Count == 0 ? (GemId?)null : ids[0]);   // null 리터럴이 GemId(string) 암시 변환으로 잡히지 않게 캐스트
    }

    /// <summary>Builds S5F4 Enable/Disable Alarm Acknowledge &lt;B ACKC5&gt;.</summary>
    /// <param name="ackc5">ACKC5 (0 = accepted, 1 = error such as an unknown ALID).</param>
    /// <returns>The S5F4 message.</returns>
    public static SecsMessage S5F4(byte ackc5) => new(5, 4, false, GemItems.Ack(ackc5));

    /// <summary>Reads S5F4 Enable/Disable Alarm Acknowledge.</summary>
    /// <param name="message">The received S5F4.</param>
    /// <returns>ACKC5 (any numeric format, 0 to 255).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S5F4.</exception>
    /// <exception cref="FormatException">The body is not a single number from 0 to 255.</exception>
    public static byte ParseS5F4(SecsMessage message)
    {
        Check(message, 5, 4);
        return Ack(message.Item, message, "ACKC5");
    }
    #endregion

    #region S5F5 / S5F6
    /// <summary>Builds S5F5 List Alarms Request.</summary>
    /// <remarks>With a numeric <see cref="GemFormats.Alid"/> the ALIDs are one integer array item (no ALID = a zero-length array = all alarms). With A, zero ALIDs give an empty A item and one gives a single A item; two or more cannot be sent because there is no A array.</remarks>
    /// <param name="alids">The ALIDs (null or empty = all alarms).</param>
    /// <param name="formats">The ID formats (null = <see cref="GemFormats.Default"/>); ALIDs use <see cref="GemFormats.Alid"/>.</param>
    /// <returns>The S5F5 message.</returns>
    /// <exception cref="ArgumentException">A text ALID cannot be sent in a numeric format, or the ALID format is A and there are two or more ALIDs.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric ALID is out of the range of the format.</exception>
    public static SecsMessage S5F5(IReadOnlyList<GemId>? alids, GemFormats? formats = null)
    {
        var f = F(formats).Alid;
        alids ??= [];
        SecsItem item;
        if (f != SecsFormat.ASCII) item = GemItems.IdList(alids, f);
        else if (alids.Count <= 1) item = alids.Count == 0 ? SecsItem.A("") : alids[0].ToItem(SecsFormat.ASCII);
        else throw new ArgumentException("ALID 포맷이 A면 ALID는 0개 또는 1개만 보낼 수 있습니다(A 배열 없음).", nameof(alids));
        return new(5, 5, true, item);
    }

    /// <summary>Reads S5F5 List Alarms Request.</summary>
    /// <remarks>Accepts &lt;L ALID…&gt;, an integer array item, or a single A item, with any integer format or A for each ID (the configured format is not checked). A header-only message or a zero-length item is an empty list (= all alarms).</remarks>
    /// <param name="message">The received S5F5.</param>
    /// <returns>The requested ALIDs.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S5F5.</exception>
    /// <exception cref="FormatException">The body is not an ID list.</exception>
    public static IReadOnlyList<GemId> ParseS5F5(SecsMessage message)
    {
        Check(message, 5, 5);
        return IdVector(message.Item, message, "ALID…");
    }

    /// <summary>Builds S5F6 List Alarms Data &lt;L &lt;L ALCD ALID ALTX&gt;…&gt;.</summary>
    /// <param name="alarms">The alarms in order.</param>
    /// <param name="formats">The ID formats (null = <see cref="GemFormats.Default"/>); ALIDs use <see cref="GemFormats.Alid"/>.</param>
    /// <returns>The S5F6 message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="alarms"/> or an entry is null.</exception>
    /// <exception cref="ArgumentException">A text ALID cannot be sent in a numeric format, or a text has a character above 255.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric ALID is out of the range of the format.</exception>
    public static SecsMessage S5F6(IReadOnlyList<GemAlarmInfo> alarms, GemFormats? formats = null) => new(5, 6, false, AlarmList(alarms, F(formats)));

    /// <summary>Reads S5F6 List Alarms Data.</summary>
    /// <remarks>A zero-length ALCD (an alarm the equipment does not know) is read as 0.</remarks>
    /// <param name="message">The received S5F6.</param>
    /// <returns>The alarms in the order the equipment sent them (an empty list for a header-only message).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S5F6.</exception>
    /// <exception cref="FormatException">The body is not &lt;L &lt;L ALCD ALID ALTX&gt;…&gt;.</exception>
    public static IReadOnlyList<GemAlarmInfo> ParseS5F6(SecsMessage message)
    {
        Check(message, 5, 6);
        return AlarmEntries(message);
    }

    // 장비용: 모르는 ALID는 받은 ALID 아이템 그대로, ALCD null이면 길이 0 <B>(D3). 이름을 S5F6과 달리 둬서 [] 호출이 모호하지 않게 한다
    internal static SecsMessage S5F6Entries(IReadOnlyList<(SecsItem Alid, byte? Alcd, string Text)> alarms)
    {
        ArgumentNullException.ThrowIfNull(alarms);
        return new(5, 6, false, SecsItem.L([.. alarms.Select(a => SecsItem.L(a.Alcd is { } c ? SecsItem.B(c) : SecsItem.B(), a.Alid, SecsItem.A(a.Text)))]));
    }
    #endregion

    #region S5F7 / S5F8
    /// <summary>Builds S5F7 List Enabled Alarm Request (header only, W-bit on).</summary>
    /// <returns>The S5F7 message.</returns>
    public static SecsMessage S5F7() => new(5, 7, true);

    /// <summary>Builds S5F8 List Enabled Alarm Data &lt;L &lt;L ALCD ALID ALTX&gt;…&gt;.</summary>
    /// <param name="alarms">The alarms whose reporting is enabled, in order.</param>
    /// <param name="formats">The ID formats (null = <see cref="GemFormats.Default"/>); ALIDs use <see cref="GemFormats.Alid"/>.</param>
    /// <returns>The S5F8 message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="alarms"/> or an entry is null.</exception>
    /// <exception cref="ArgumentException">A text ALID cannot be sent in a numeric format, or a text has a character above 255.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric ALID is out of the range of the format.</exception>
    public static SecsMessage S5F8(IReadOnlyList<GemAlarmInfo> alarms, GemFormats? formats = null) => new(5, 8, false, AlarmList(alarms, F(formats)));

    /// <summary>Reads S5F8 List Enabled Alarm Data. The rules are the same as for <see cref="ParseS5F6"/>.</summary>
    /// <param name="message">The received S5F8.</param>
    /// <returns>The alarms in the order the equipment sent them (an empty list for a header-only message).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S5F8.</exception>
    /// <exception cref="FormatException">The body is not &lt;L &lt;L ALCD ALID ALTX&gt;…&gt;.</exception>
    public static IReadOnlyList<GemAlarmInfo> ParseS5F8(SecsMessage message)
    {
        Check(message, 5, 8);
        return AlarmEntries(message);
    }
    #endregion

    #region S5 도우미
    static SecsItem AlarmItem(byte alcd, SecsItem alid, string text) => SecsItem.L(SecsItem.B(alcd), alid, SecsItem.A(text));

    static SecsItem AlarmList(IReadOnlyList<GemAlarmInfo> alarms, GemFormats f)
    {
        ArgumentNullException.ThrowIfNull(alarms);
        return SecsItem.L([.. alarms.Select(a =>
        {
            ArgumentNullException.ThrowIfNull(a, nameof(alarms));
            return AlarmItem(a.Alcd, a.Alid.ToItem(f.Alid), GemDefinitionCheck.Ascii(a.Text, nameof(alarms)));
        })]);
    }

    // <L ALCD ALID ALTX> 하나. emptyAlcd면 ALCD 길이 0 숫자 아이템(S5F6·S5F8의 모르는 ALID) = 0, 아니면(S5F1) ALCD 값이 있어야 한다
    static GemAlarmInfo Alarm(SecsItem? e, SecsMessage m, string expected, bool emptyAlcd)
    {
        if (e is not { Format: SecsFormat.List, Count: 3 } || e.Items[2].Format != SecsFormat.ASCII) throw Fail(m, expected);
        byte alcd = emptyAlcd && e.Items[0] is { Count: 0, Format: not (SecsFormat.List or SecsFormat.ASCII) } ? (byte)0 : Ack(e.Items[0], m, expected);
        return new GemAlarmInfo(Id(e.Items[1], m, expected), alcd, e.Items[2].GetString());
    }

    static IReadOnlyList<GemAlarmInfo> AlarmEntries(SecsMessage m)
    {
        const string expected = "<L <L ALCD ALID ALTX>…>";
        return [.. Entries(m, expected).Select(e => Alarm(e, m, expected, emptyAlcd: true))];
    }
    #endregion
}
