using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

/// <summary>Stateless builders (<c>SxFy(...)</c>) and parsers (<c>ParseSxFy(...)</c>) for GEM message bodies.</summary>
/// <remarks>
/// <para><b>Three layers</b>: the transport (<c>SecsHsms</c>/<c>SecsSerial</c>) sends and receives raw SECS-II messages; this class (the messaging layer) only builds and reads GEM message bodies and keeps no state; <c>GemEquipment</c>/<c>GemHost</c> are full E30 modules that own the state (communication, control, reports) and answer automatically. Pick one level per link. Mixing this class with a module is fine (a module's state is not affected by what you build or parse here), but when you use a module, do not subscribe to the transport's <c>PrimaryReceived</c> yourself, because the module owns it.</para>
/// <para><b>Builders</b>: a primary message (odd function) has the W-bit set. Only S5F1, S6F11, S10F1 and S10F3, whose reply is optional in E5, take <c>replyExpected</c> (default true). A reply builder (even function) never has the W-bit, so use it as <c>e.Reply(GemMessages.S1F14(...))</c>.</para>
/// <para><b>Parsers</b>: a parser throws <see cref="ArgumentException"/> (parameter <c>message</c>) when the message is not the expected stream and function (a programming error), and <see cref="FormatException"/> when the body structure differs (the text holds the first 2048 characters of the message as SML). A message without a body, such as S1F3, reads as an empty list.</para>
/// <para><b>Lenient on receive, strict on send</b>: a parser accepts every integer ID format or A (see <see cref="GemId"/>), an ack in any numeric format (0 to 255, otherwise <see cref="FormatException"/>), and for an ID list <c>&lt;L ID…&gt;</c>, an integer array, or a single A. A builder writes each ID in the format of <see cref="GemFormats"/> (null = <see cref="GemFormats.Default"/>, all U4): an out-of-range number throws <see cref="ArgumentOutOfRangeException"/>, and a number sent as A becomes a decimal string. An ack is always a one-byte <c>B</c>; pass a <see cref="GemAck"/> constant or any <see cref="byte"/>.</para>
/// <para><b>What the caller checks</b>: whether an ID matches your configured format, how many items a request may hold, and text lengths (MDLN 20, ALTX 120 and so on) are not checked here.</para>
/// <para><b>Order</b>: if you keep the state yourself, send with <c>SendOrdered</c> (on <c>SecsHsms</c> and <c>SecsSerial</c>) so that a reply is handled before the next primary message the peer sent after it. Count connections by incrementing a number in each <c>DeviceOpened</c> and record it when sending, to tell which connection a result belongs to.</para>
/// </remarks>
/// <example>
/// An equipment application: establish communications on connect (SendOrdered) and answer S1F3 itself.
/// <code>
/// var hsms = new SecsHsms { Role = SecsRole.Equipment, Mode = HsmsMode.Passive, LocalPort = 5000 };
/// long generation = 0;
/// hsms.DeviceOpened += (s, e) =>
/// {
///     long mine = Interlocked.Increment(ref generation);   // which connection this result belongs to
///     hsms.SendOrdered(GemMessages.S1F13("EQ", "1.0"), r =>
///     {
///         if (!r.IsSuccess || mine != Interlocked.Read(ref generation)) return;
///         var (commack, modelName, softwareRevision) = GemMessages.ParseS1F14(r.Reply!);
///         // commack 0 = communicating
///     });
/// };
/// hsms.PrimaryReceived += (s, e) =>
/// {
///     if (e.Message is { Stream: 1, Function: 3 })
///     {
///         var svids = GemMessages.ParseS1F3(e.Message);
///         e.Reply(GemMessages.S1F4([.. svids.Select(id => SecsItem.F4(25))]));
///     }
/// };
/// hsms.Start();
/// </code>
/// A Host application: read an event report (the table gives the VIDs of each report) and acknowledge it.
/// <code>
/// var table = new Dictionary&lt;GemId, IReadOnlyList&lt;GemId&gt;&gt; { [1000] = new GemId[] { 2001, 3001 } };
/// var formats = new GemFormats();   // the same ID formats as the equipment
/// hsms.PrimaryReceived += (s, e) =>
/// {
///     if (e.Message is { Stream: 6, Function: 11 })
///     {
///         GemEventReport report = GemMessages.ParseS6F11(e.Message, table, formats);
///         e.Reply(GemMessages.S6F12(GemAck.Ackc6Accepted));
///     }
/// };
/// </code>
/// </example>
public static partial class GemMessages
{
    // 2층 공통 도우미: S/F 검사, 진단 문장(FormatException), 받을 때 관대한 ID·ack 읽기, 보낼 때 엄격한 ID 목록

    // 해석기 입구: null이면 ArgumentNullException, Stream/Function이 다르면 ArgumentException(파라미터 이름 message)
    internal static void Check(SecsMessage m, int s, int f)
    {
        ArgumentNullException.ThrowIfNull(m, "message");
        if (m.Stream != s || m.Function != f)
            throw new ArgumentException($"S{s}F{f} 메시지가 아닙니다: S{m.Stream}F{m.Function}", "message");
    }

    // 본문 구조가 기대와 다름. 문장은 GemHost의 응답 오류 문장과 같다(짝수 Function = "응답 ", SML은 앞 SmlLimit자)
    internal static FormatException Fail(SecsMessage m, string expected)
        => new($"S{m.Stream}F{m.Function} {(m.Function % 2 == 0 ? "응답 " : "")}본문이 기대({expected})와 다릅니다: {GemItems.Sml(m)}");

    // ack 하나(숫자 포맷 무엇이든, 0~255). 아니면 Fail
    internal static byte Ack(SecsItem? item, SecsMessage m, string expected)
    {
        try { return GemItems.ReadAck(item); }
        catch (FormatException) { throw Fail(m, expected); }
    }

    // ID 하나(정수 8종·A, 설정 포맷 검사 없음). 아니면 Fail
    internal static GemId Id(SecsItem? item, SecsMessage m, string expected)
    {
        if (item == null) throw Fail(m, expected);
        try { return GemId.FromItem(item); }
        catch (FormatException) { throw Fail(m, expected); }
    }

    // ID 목록: <L ID…>·정수 배열·A 하나(읽기 규칙은 GemItems.ReadIds 한 곳, 설정 포맷 검사 없음). 본문 없음·길이 0 = 빈 목록
    internal static IReadOnlyList<GemId> IdVector(SecsItem? item, SecsMessage m, string expected)
    {
        if (item == null) return [];
        try { return GemItems.ReadIds(item, i => Id(i, m, expected), integerArray: true); }
        catch (FormatException) { throw Fail(m, expected); }
    }

    // 보낼 ID 목록 <L ID…>(그 포맷). null = 빈 목록. 범위 밖이면 ArgumentOutOfRangeException, 숫자 포맷에 문자열 ID면 ArgumentException
    internal static SecsItem IdList(IReadOnlyList<GemId>? ids, SecsFormat f) => SecsItem.L([.. (ids ?? []).Select(i => i.ToItem(f))]);

    internal static GemFormats F(GemFormats? f) => f ?? GemFormats.Default;

    // <L …> 본문의 항목. 본문 없음 = 빈 목록, 리스트가 아니면 Fail
    internal static IReadOnlyList<SecsItem> Entries(SecsMessage m, string expected) => m.Item switch
    {
        null => [],
        { Format: SecsFormat.List } list => list.Items,
        _ => throw Fail(m, expected),
    };
}
