using System.Globalization;
using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

/// <summary>Stateless builders (<c>SxFy(...)</c>) and parsers (<c>ParseSxFy(...)</c>) for GEM message bodies.</summary>
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

    // ID 목록: <L ID…>·정수 배열·A 하나(GemItems.ReadIdVector와 같은 세 모양, 설정 포맷 검사 없음). 본문 없음·길이 0 = 빈 목록
    internal static IReadOnlyList<GemId> IdVector(SecsItem? item, SecsMessage m, string expected)
    {
        if (item == null) return [];
        if (item.Format == SecsFormat.List) return [.. item.Items.Select(i => Id(i, m, expected))];
        if (item.Count == 0 && (item.Format == SecsFormat.ASCII || GemItems.IsInteger(item.Format))) return [];
        if (GemItems.IsInteger(item.Format))
        {
            var list = new List<GemId>(item.Count);
            foreach (var v in item.RawValues)
            {
                if (v is ulong u && u > long.MaxValue) throw Fail(m, expected);
                list.Add(new GemId(Convert.ToInt64(v, CultureInfo.InvariantCulture)));
            }
            return list;
        }
        return [Id(item, m, expected)];
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
