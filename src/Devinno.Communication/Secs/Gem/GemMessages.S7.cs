using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

// Stream 7: 언포맷 레시피(Process Program) — S7F1~F6, S7F17~F20
public static partial class GemMessages
{
    #region S7F1 / S7F2
    /// <summary>Builds S7F1 Process Program Load Inquire &lt;L &lt;A PPID&gt; &lt;U4 LENGTH&gt;&gt;.</summary>
    /// <param name="ppid">PPID (1 to 120 characters).</param>
    /// <param name="length">The length in bytes of the process program that will follow in S7F3.</param>
    /// <returns>The S7F1 message (W-bit set).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ppid"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="ppid"/> breaks the PPID rules of <see cref="GemProcessProgram"/>.</exception>
    public static SecsMessage S7F1(string ppid, uint length)
        => new(7, 1, true, SecsItem.L(SecsItem.A(GemProcessProgram.CheckPpid(ppid, nameof(ppid))), SecsItem.U4(length)));

    /// <summary>Reads S7F1 Process Program Load Inquire. The PPID length is not checked (the caller decides).</summary>
    /// <param name="message">The received S7F1.</param>
    /// <returns>The PPID and LENGTH (LENGTH in any integer format, not negative).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S7F1.</exception>
    /// <exception cref="FormatException">The body is not &lt;L &lt;A PPID&gt; LENGTH&gt;.</exception>
    public static (string Ppid, ulong Length) ParseS7F1(SecsMessage message)
    {
        Check(message, 7, 1);
        const string expected = "<L <A PPID> LENGTH>";
        if (message.Item is not { Format: SecsFormat.List, Items.Count: 2 } l) throw Fail(message, expected);
        return (Ppid(l.Items[0], message, expected), Length(l.Items[1], message, expected));
    }

    /// <summary>Builds S7F2 Process Program Load Grant &lt;B PPGNT&gt;.</summary>
    /// <param name="ppgnt">PPGNT (see the Ppgnt values of <see cref="GemAck"/>).</param>
    /// <returns>The S7F2 message.</returns>
    public static SecsMessage S7F2(byte ppgnt) => new(7, 2, false, GemItems.Ack(ppgnt));

    /// <summary>Reads S7F2 Process Program Load Grant.</summary>
    /// <param name="message">The received S7F2.</param>
    /// <returns>PPGNT (any numeric format, 0 to 255).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S7F2.</exception>
    /// <exception cref="FormatException">The body is not a single number from 0 to 255.</exception>
    public static byte ParseS7F2(SecsMessage message)
    {
        Check(message, 7, 2);
        return Ack(message.Item, message, "PPGNT");
    }
    #endregion

    #region S7F3 / S7F4
    /// <summary>Builds S7F3 Process Program Send &lt;L &lt;A PPID&gt; PPBODY&gt;.</summary>
    /// <param name="program">The process program.</param>
    /// <returns>The S7F3 message (W-bit set).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> is null.</exception>
    public static SecsMessage S7F3(GemProcessProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        return new(7, 3, true, ProgramBody(program));
    }

    /// <summary>Reads S7F3 Process Program Send. The PPID length and the body size are not checked (the caller decides).</summary>
    /// <param name="message">The received S7F3.</param>
    /// <returns>The PPID and PPBODY (any single item that is not a list).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S7F3.</exception>
    /// <exception cref="FormatException">The body is not &lt;L &lt;A PPID&gt; PPBODY&gt;, or PPBODY is a list.</exception>
    public static (string Ppid, SecsItem Body) ParseS7F3(SecsMessage message)
    {
        Check(message, 7, 3);
        return ReadProgram(message.Item, message) ?? throw Fail(message, "<L <A PPID> PPBODY>");
    }

    /// <summary>Builds S7F4 Process Program Acknowledge &lt;B ACKC7&gt;.</summary>
    /// <param name="ackc7">ACKC7 (see the Ackc7 values of <see cref="GemAck"/>).</param>
    /// <returns>The S7F4 message.</returns>
    public static SecsMessage S7F4(byte ackc7) => new(7, 4, false, GemItems.Ack(ackc7));

    /// <summary>Reads S7F4 Process Program Acknowledge.</summary>
    /// <param name="message">The received S7F4.</param>
    /// <returns>ACKC7 (any numeric format, 0 to 255).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S7F4.</exception>
    /// <exception cref="FormatException">The body is not a single number from 0 to 255.</exception>
    public static byte ParseS7F4(SecsMessage message)
    {
        Check(message, 7, 4);
        return Ack(message.Item, message, "ACKC7");
    }
    #endregion

    #region S7F5 / S7F6
    /// <summary>Builds S7F5 Process Program Request &lt;A PPID&gt;.</summary>
    /// <param name="ppid">PPID (1 to 120 characters).</param>
    /// <returns>The S7F5 message (W-bit set).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ppid"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="ppid"/> breaks the PPID rules of <see cref="GemProcessProgram"/>.</exception>
    public static SecsMessage S7F5(string ppid) => new(7, 5, true, SecsItem.A(GemProcessProgram.CheckPpid(ppid, nameof(ppid))));

    /// <summary>Reads S7F5 Process Program Request.</summary>
    /// <param name="message">The received S7F5.</param>
    /// <returns>The PPID (the length is not checked).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S7F5.</exception>
    /// <exception cref="FormatException">The body is not &lt;A PPID&gt;.</exception>
    public static string ParseS7F5(SecsMessage message)
    {
        Check(message, 7, 5);
        return Ppid(message.Item, message, "<A PPID>");
    }

    /// <summary>Builds S7F6 Process Program Data &lt;L &lt;A PPID&gt; PPBODY&gt;, or &lt;L&gt; when the request is denied or the PPID is unknown.</summary>
    /// <param name="program">The process program, or null to deny.</param>
    /// <returns>The S7F6 message.</returns>
    public static SecsMessage S7F6(GemProcessProgram? program) => new(7, 6, false, program == null ? SecsItem.L() : ProgramBody(program));

    /// <summary>Reads S7F6 Process Program Data.</summary>
    /// <param name="message">The received S7F6.</param>
    /// <returns>The PPID and PPBODY, or null when the body is &lt;L&gt; (denied or unknown PPID).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S7F6.</exception>
    /// <exception cref="FormatException">The body is neither &lt;L&gt; nor &lt;L &lt;A PPID&gt; PPBODY&gt;.</exception>
    public static (string Ppid, SecsItem Body)? ParseS7F6(SecsMessage message)
    {
        Check(message, 7, 6);
        if (message.Item is { Format: SecsFormat.List, Items.Count: 0 }) return null;
        return ReadProgram(message.Item, message) ?? throw Fail(message, "<L> or <L <A PPID> PPBODY>");
    }
    #endregion

    #region S7F17 / S7F18
    /// <summary>Builds S7F17 Delete Process Program Send &lt;L &lt;A PPID&gt;…&gt;. An empty list deletes all process programs.</summary>
    /// <param name="ppids">The PPIDs to delete (empty = all).</param>
    /// <returns>The S7F17 message (W-bit set).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ppids"/> or one of its items is null.</exception>
    /// <exception cref="ArgumentException">A PPID breaks the PPID rules of <see cref="GemProcessProgram"/>.</exception>
    public static SecsMessage S7F17(IReadOnlyList<string> ppids) => new(7, 17, true, PpidList(ppids));

    /// <summary>Reads S7F17 Delete Process Program Send.</summary>
    /// <param name="message">The received S7F17.</param>
    /// <returns>The PPIDs (empty = all; a message without a body is also empty).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S7F17.</exception>
    /// <exception cref="FormatException">The body is not &lt;L &lt;A PPID&gt;…&gt;.</exception>
    public static IReadOnlyList<string> ParseS7F17(SecsMessage message)
    {
        Check(message, 7, 17);
        return ReadPpids(message);
    }

    /// <summary>Builds S7F18 Delete Process Program Acknowledge &lt;B ACKC7&gt;.</summary>
    /// <param name="ackc7">ACKC7 (see the Ackc7 values of <see cref="GemAck"/>).</param>
    /// <returns>The S7F18 message.</returns>
    public static SecsMessage S7F18(byte ackc7) => new(7, 18, false, GemItems.Ack(ackc7));

    /// <summary>Reads S7F18 Delete Process Program Acknowledge.</summary>
    /// <param name="message">The received S7F18.</param>
    /// <returns>ACKC7 (any numeric format, 0 to 255).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S7F18.</exception>
    /// <exception cref="FormatException">The body is not a single number from 0 to 255.</exception>
    public static byte ParseS7F18(SecsMessage message)
    {
        Check(message, 7, 18);
        return Ack(message.Item, message, "ACKC7");
    }
    #endregion

    #region S7F19 / S7F20
    /// <summary>Builds S7F19 Current EPPD Request (no body).</summary>
    /// <returns>The S7F19 message (W-bit set).</returns>
    public static SecsMessage S7F19() => new(7, 19, true);

    /// <summary>Builds S7F20 Current EPPD Data &lt;L &lt;A PPID&gt;…&gt;.</summary>
    /// <param name="ppids">The PPIDs the equipment holds.</param>
    /// <returns>The S7F20 message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ppids"/> or one of its items is null.</exception>
    /// <exception cref="ArgumentException">A PPID breaks the PPID rules of <see cref="GemProcessProgram"/>.</exception>
    public static SecsMessage S7F20(IReadOnlyList<string> ppids) => new(7, 20, false, PpidList(ppids));

    /// <summary>Reads S7F20 Current EPPD Data.</summary>
    /// <param name="message">The received S7F20.</param>
    /// <returns>The PPIDs (a message without a body is empty).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S7F20.</exception>
    /// <exception cref="FormatException">The body is not &lt;L &lt;A PPID&gt;…&gt;.</exception>
    public static IReadOnlyList<string> ParseS7F20(SecsMessage message)
    {
        Check(message, 7, 20);
        return ReadPpids(message);
    }
    #endregion

    #region S7 도우미
    static SecsItem ProgramBody(GemProcessProgram p) => SecsItem.L(SecsItem.A(p.Ppid), p.Body);

    static SecsItem PpidList(IReadOnlyList<string> ppids)
    {
        ArgumentNullException.ThrowIfNull(ppids);
        return SecsItem.L([.. ppids.Select(p => SecsItem.A(GemProcessProgram.CheckPpid(p, nameof(ppids))))]);
    }

    // PPID = A 하나(길이 검사 없음, 3층 몫)
    static string Ppid(SecsItem? item, SecsMessage m, string expected)
        => item is { Format: SecsFormat.ASCII } a ? a.GetString() : throw Fail(m, expected);

    // LENGTH = 정수 하나(음수 아님)
    static ulong Length(SecsItem item, SecsMessage m, string expected)
    {
        if (!GemItems.IsInteger(item.Format) || item.Count != 1) throw Fail(m, expected);
        try { return item.Format is SecsFormat.I1 or SecsFormat.I2 or SecsFormat.I4 or SecsFormat.I8 ? checked((ulong)item.GetInt64()) : item.GetUInt64(); }
        catch (OverflowException) { throw Fail(m, expected); }
    }

    // <L <A PPID> PPBODY>(PPBODY는 리스트 아님). 아니면 null
    static (string, SecsItem)? ReadProgram(SecsItem? item, SecsMessage m)
    {
        if (item is not { Format: SecsFormat.List, Items.Count: 2 } l || l.Items[0].Format != SecsFormat.ASCII || l.Items[1].Format == SecsFormat.List) return null;
        return (l.Items[0].GetString(), l.Items[1]);
    }

    static IReadOnlyList<string> ReadPpids(SecsMessage m)
    {
        const string expected = "<L <A PPID>…>";
        return [.. Entries(m, expected).Select(i => Ppid(i, m, expected))];
    }
    #endregion
}
