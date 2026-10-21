using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

// S10: 터미널(S10F1~S10F4), 3층용 internal S10F5·S10F6
public static partial class GemMessages
{
    #region S10F1 / S10F2
    /// <summary>Builds S10F1 Terminal Request &lt;L TID TEXT&gt; (equipment to host).</summary>
    /// <param name="tid">The terminal number TID.</param>
    /// <param name="text">The text (ASCII).</param>
    /// <param name="replyExpected">true to set the W-bit (default); false when the sender does not wait for S10F2.</param>
    /// <returns>The S10F1 message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="text"/> has a character above 255.</exception>
    public static SecsMessage S10F1(byte tid, string text, bool replyExpected = true) => new(10, 1, replyExpected, Terminal(tid, text));

    /// <summary>Reads S10F1 Terminal Request.</summary>
    /// <param name="message">The received S10F1.</param>
    /// <returns>The terminal number (any numeric format, 0 to 255) and the text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S10F1.</exception>
    /// <exception cref="FormatException">The body is not &lt;L TID TEXT&gt;.</exception>
    public static (byte Tid, string Text) ParseS10F1(SecsMessage message)
    {
        Check(message, 10, 1);
        return TerminalText(message);
    }

    /// <summary>Builds S10F2 Terminal Request Acknowledge &lt;B ACKC10&gt;.</summary>
    /// <param name="ackc10">ACKC10 (0 = accepted, 1 = not displayed, 2 = no terminal).</param>
    /// <returns>The S10F2 message.</returns>
    public static SecsMessage S10F2(byte ackc10) => new(10, 2, false, GemItems.Ack(ackc10));

    /// <summary>Reads S10F2 Terminal Request Acknowledge.</summary>
    /// <param name="message">The received S10F2.</param>
    /// <returns>ACKC10 (any numeric format, 0 to 255).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S10F2.</exception>
    /// <exception cref="FormatException">The body is not a single number from 0 to 255.</exception>
    public static byte ParseS10F2(SecsMessage message)
    {
        Check(message, 10, 2);
        return Ack(message.Item, message, "ACKC10");
    }
    #endregion

    #region S10F3 / S10F4
    /// <summary>Builds S10F3 Terminal Display, Single &lt;L TID TEXT&gt; (host to equipment).</summary>
    /// <param name="tid">The terminal number TID.</param>
    /// <param name="text">The text (ASCII).</param>
    /// <param name="replyExpected">true to set the W-bit (default); false when the sender does not wait for S10F4.</param>
    /// <returns>The S10F3 message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="text"/> has a character above 255.</exception>
    public static SecsMessage S10F3(byte tid, string text, bool replyExpected = true) => new(10, 3, replyExpected, Terminal(tid, text));

    /// <summary>Reads S10F3 Terminal Display, Single.</summary>
    /// <param name="message">The received S10F3.</param>
    /// <returns>The terminal number (any numeric format, 0 to 255) and the text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S10F3.</exception>
    /// <exception cref="FormatException">The body is not &lt;L TID TEXT&gt;.</exception>
    public static (byte Tid, string Text) ParseS10F3(SecsMessage message)
    {
        Check(message, 10, 3);
        return TerminalText(message);
    }

    /// <summary>Builds S10F4 Terminal Display, Single Acknowledge &lt;B ACKC10&gt;.</summary>
    /// <param name="ackc10">ACKC10 (0 = accepted, 1 = not displayed, 2 = no terminal).</param>
    /// <returns>The S10F4 message.</returns>
    public static SecsMessage S10F4(byte ackc10) => new(10, 4, false, GemItems.Ack(ackc10));

    /// <summary>Reads S10F4 Terminal Display, Single Acknowledge.</summary>
    /// <param name="message">The received S10F4.</param>
    /// <returns>ACKC10 (any numeric format, 0 to 255).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not S10F4.</exception>
    /// <exception cref="FormatException">The body is not a single number from 0 to 255.</exception>
    public static byte ParseS10F4(SecsMessage message)
    {
        Check(message, 10, 4);
        return Ack(message.Item, message, "ACKC10");
    }
    #endregion

    #region S10F5 / S10F6 (internal, 3층용)
    // <L TID <L TEXT…>>. 줄 하나라도 A가 아니면 Fail
    internal static (byte Tid, IReadOnlyList<string> Lines) ParseS10F5(SecsMessage message)
    {
        Check(message, 10, 5);
        const string expected = "<L TID <L TEXT…>>";
        if (message.Item is not { Format: SecsFormat.List, Count: 2 } body || body.Items[1].Format != SecsFormat.List
            || body.Items[1].Items.Any(l => l.Format != SecsFormat.ASCII)) throw Fail(message, expected);
        return (Ack(body.Items[0], message, expected), [.. body.Items[1].Items.Select(l => l.GetString())]);
    }

    internal static SecsMessage S10F6(byte ackc10) => new(10, 6, false, GemItems.Ack(ackc10));
    #endregion

    #region S10 도우미
    static SecsItem Terminal(byte tid, string text) => SecsItem.L(SecsItem.B(tid), SecsItem.A(GemDefinitionCheck.Ascii(text, nameof(text))));

    static (byte Tid, string Text) TerminalText(SecsMessage m)
    {
        const string expected = "<L TID TEXT>";
        if (m.Item is not { Format: SecsFormat.List, Count: 2 } body || body.Items[1].Format != SecsFormat.ASCII) throw Fail(m, expected);
        return (Ack(body.Items[0], m, expected), body.Items[1].GetString());
    }
    #endregion
}
