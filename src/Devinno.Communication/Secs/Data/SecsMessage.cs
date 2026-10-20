namespace Devinno.Communication.Secs.Data;

/// <summary>
/// A SECS-II message (stream, function, W-bit, and body item). It does not change after it is created.
/// </summary>
/// <remarks>
/// <para>A primary message has an odd function, and its reply has the primary function + 1. Function 0 (SxF0) is the "not processed (abort)" reply.</para>
/// <para><see cref="SystemBytes"/> and <see cref="DeviceID"/> are filled in by the transport layer (<c>SecsHsms</c>, <c>SecsSerial</c>).
/// A message you create yourself has 0, and a received message contains the actual values. The object passed when sending is not modified (the transport layer puts the values into a copy).</para>
/// <para>A message can be converted to SML text (<see cref="ToSml"/>) or created from text (<see cref="ParseSml"/>). Example: <c>S1F13 W &lt;L [2] &lt;A "MDLN"&gt; &lt;U4 1 2&gt;&gt;</c></para>
/// </remarks>
/// <example>
/// <code>
/// var s1f13 = new SecsMessage(1, 13, true, SecsItem.L());
/// var same = SecsMessage.ParseSml("S1F13 W &lt;L&gt;");
/// Console.WriteLine(s1f13.ToSml());   // S1F13 W &lt;L [0]&gt;
/// </code>
/// </example>
public sealed class SecsMessage
{
    /// <summary>Creates a message.</summary>
    /// <param name="stream">Stream, 0 to 127.</param>
    /// <param name="function">Function, 0 to 255 (odd for a primary message).</param>
    /// <param name="replyExpected">Whether a reply is expected (W-bit).</param>
    /// <param name="item">The body item (if null, a message with only a header).</param>
    /// <exception cref="ArgumentOutOfRangeException">Stream or function is out of range.</exception>
    public SecsMessage(int stream, int function, bool replyExpected, SecsItem? item = null)
    {
        if (stream is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(stream), stream, "Stream은 0~127 입니다.");
        if (function is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(function), function, "Function은 0~255 입니다.");
        Stream = stream;
        Function = function;
        ReplyExpected = replyExpected;
        Item = item;
    }

    SecsMessage(SecsMessage m, uint systemBytes, int deviceId) : this(m.Stream, m.Function, m.ReplyExpected, m.Item)
    {
        SystemBytes = systemBytes;
        DeviceID = deviceId;
    }

    /// <summary>Stream 0~127</summary>
    public int Stream { get; }
    /// <summary>Function, 0 to 255 (odd = primary message, even = reply, 0 = abort).</summary>
    public int Function { get; }
    /// <summary>Whether a reply is expected (W-bit).</summary>
    public bool ReplyExpected { get; }
    /// <summary>The body item. null for a message with only a header.</summary>
    public SecsItem? Item { get; }
    /// <summary>Transaction number (system bytes). Filled in by the transport layer; a reply has the same value as its primary message. 0 for a message you create yourself.</summary>
    public uint SystemBytes { get; }
    /// <summary>Device ID, 0 to 32767. Filled in by the transport layer. 0 for a message you create yourself.</summary>
    public int DeviceID { get; }

    // 전송 계층이 System Bytes·장치 ID를 넣은 복사본
    internal SecsMessage WithHeader(uint systemBytes, int deviceId) => new(this, systemBytes, deviceId);

    /// <summary>Converts the message to SML text. Example: <c>S1F13 W &lt;L [2] &lt;A "MDLN"&gt; &lt;U4 1 2&gt;&gt;</c></summary>
    /// <returns>The SML text (one line).</returns>
    public string ToSml() => SecsSml.Message(this);

    /// <summary>Creates a message from SML text.</summary>
    /// <remarks>
    /// <para>Format: <c>S{Stream}F{Function} [W] [item] [.]</c>. It is case-insensitive, and whitespace and line breaks are free.</para>
    /// <para>The item syntax is the same as <see cref="SecsItem.ParseSml"/>.</para>
    /// </remarks>
    /// <param name="text">The SML text.</param>
    /// <returns>The message (SystemBytes and DeviceID = 0).</returns>
    /// <exception cref="ArgumentNullException">text is null.</exception>
    /// <exception cref="FormatException">The syntax is invalid (the message includes the character position).</exception>
    public static SecsMessage ParseSml(string text) => SecsSml.ParseMessage(text);

    /// <summary>Returns the SML text (same as <see cref="ToSml"/>).</summary>
    /// <returns>The SML text.</returns>
    public override string ToString() => ToSml();
}
