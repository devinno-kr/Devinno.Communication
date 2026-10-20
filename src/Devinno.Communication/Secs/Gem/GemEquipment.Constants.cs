using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

/// <summary>Arguments for when the Host requested an EC change (S2F15). If the handler sets <see cref="Eac"/>, the reply uses that value.</summary>
/// <remarks>Only requests for which GEM has already finished the ECID, format, and range checks arrive. After the handler returns, all values are applied if EAC is 0. If the deadline (<c>CommandReplyTimeout</c>) passes, GEM replies with EAC 2 on its behalf and discards the result.</remarks>
public class GemConstantsChangeEventArgs : EventArgs
{
    internal GemConstantsChangeEventArgs(IReadOnlyList<(GemId Ecid, SecsItem Value)> changes) => Changes = changes;

    /// <summary>ECs to change and their new values (converted to the EC format)</summary>
    public IReadOnlyList<(GemId Ecid, SecsItem Value)> Changes { get; }

    /// <summary>EAC to reply with (default: 0 = accepted). If the change cannot be made now, set it to <see cref="GemAck.EacBusy"/> (2) or the like.</summary>
    public byte Eac { get; set; }
}

// 장비 상수: S2F13/F14 읽기, S2F15/F16 쓰기(검사 → 앱 승인 → 적용), S2F29/F30 이름, 운전자 변경 SetConstant
public partial class GemEquipment
{
    const int S2F13 = 2 * 256 + 13, S2F15 = 2 * 256 + 15, S2F29 = 2 * 256 + 29;

    /// <summary>Raised on the application event thread when the Host requested an EC change (S2F15). When the handler returns, the reply uses <see cref="GemConstantsChangeEventArgs.Eac"/>.</summary>
    /// <remarks>If there is no subscriber, a request that passed the checks is applied immediately and the reply is EAC 0. If the handler does not return within <see cref="CommandReplyTimeout"/>, GEM replies with EAC 2 (busy) on its behalf and does not change the values. If the handler throws, GEM replies with EAC 2, does not change the values, and reports it through <see cref="ProtocolError"/>.</remarks>
    public event EventHandler<GemConstantsChangeEventArgs>? EquipmentConstantsChangeRequested;

    partial void AddConstantHandlers()
    {
        handlers[S2F13] = OnS2F13;
        handlers[S2F15] = OnS2F15;
        handlers[S2F29] = OnS2F29;
    }

    /// <summary>The operator changes an EC (with the same checks as a Host change). After applying it, the OperatorEquipmentConstantChange CE is sent.</summary>
    /// <remarks>Between numeric formats, the value is converted to the EC format (a fraction is not allowed for an integer format). Numeric and BOOLEAN values must have the same number of elements as the default value (A may have any length). If Min or Max is set, NaN is out of range. EstablishCommunicationsTimeout takes effect from the next retry interval, and TimeFormat takes effect immediately.</remarks>
    /// <param name="ecid">ECID (including standard ECs)</param>
    /// <param name="value">New value</param>
    /// <exception cref="ArgumentNullException">value is null</exception>
    /// <exception cref="ArgumentException">The ECID is not defined, or the value has a format that cannot be converted or a different number of elements</exception>
    /// <exception cref="ArgumentOutOfRangeException">Outside the Min to Max range</exception>
    /// <exception cref="InvalidOperationException">Not started</exception>
    public void SetConstant(GemId ecid, SecsItem value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Started();
        lock (gate)
        {
            if (!catalog.Vars.TryGetValue(GemItems.Key(ecid, catalog.VidFormat), out var v) || v.Kind != GemVarKind.Constant)
                throw new ArgumentException($"정의되지 않은 ECID {ecid}입니다.", nameof(ecid));
            var converted = GemItems.ConvertNumeric(value, v.Format);
            if (converted == null || !GemItems.Matches(converted, v))
                throw new ArgumentException($"EC {ecid}의 값은 {v.Initial.ToSml()}와 같은 포맷·개수여야 합니다. 받은 값: {value.ToSml()}", nameof(value));
            if (GemItems.IsNumeric(v.Format) && !GemItems.InRange(converted, v.Min, v.Max))
                throw new ArgumentOutOfRangeException(nameof(value), $"EC {ecid}의 범위({v.Min?.ToSml()}~{v.Max?.ToSml()}) 밖입니다: {value.ToSml()}");
            values[v.Key] = converted;
            TriggerStandardEventLocked(GemStandard.OperatorEquipmentConstantChange);
        }
    }

    // S2F13 <L ECID…> → <L ECV…>. 빈 목록 = 모든 EC(표준 먼저), 모르는 ECID = <L>
    void OnS2F13(SecsPrimaryEventArgs e)
    {
        var ids = RequestedIds(e.Message.Item, catalog.VidFormat);
        SecsItem body;
        lock (gate)
        {
            body = ids.Count == 0
                ? SecsItem.L([.. catalog.Constants.Select(v => values[v.Key])])
                : SecsItem.L([.. ids.Select(id => catalog.Vars.TryGetValue(id, out var v) && v.Kind == GemVarKind.Constant ? values[v.Key] : SecsItem.L())]);
        }
        Respond(e, body);
    }

    // S2F29 <L ECID…> → <L <L ECID ECNAME ECMIN ECMAX ECDEF UNITS>…>. Min/Max 없음 = 그 포맷의 길이 0. 모르는 ECID는 이름·단위 빈 A, 값 자리 <L>
    void OnS2F29(SecsPrimaryEventArgs e)
    {
        var request = e.Message.Item;
        var ids = RequestedIds(request, catalog.VidFormat);
        SecsItem Entry(GemVar v) => SecsItem.L(v.Key.ToItem(catalog.VidFormat), SecsItem.A(v.Name),
            v.Min ?? GemItems.Empty(v.Format), v.Max ?? GemItems.Empty(v.Format), v.Initial, SecsItem.A(v.Units));
        var items = ids.Count == 0
            ? catalog.Constants.Select(Entry)
            : ids.Select((id, i) => catalog.Vars.TryGetValue(id, out var v) && v.Kind == GemVarKind.Constant
                ? Entry(v) : SecsItem.L(request!.Items[i], SecsItem.A(""), SecsItem.L(), SecsItem.L(), SecsItem.L(), SecsItem.A("")));
        Respond(e, SecsItem.L([.. items]));
    }

    // S2F15 <L <L ECID ECV>…> → EAC. 전부 검사(모르는 ECID 1, 포맷·개수·범위 3) → 앱 승인(미룬 응답) → EAC 0이면 모두 적용
    void OnS2F15(SecsPrimaryEventArgs e)
    {
        if (e.Message.Item is not { Format: SecsFormat.List } body) throw new FormatException("S2F15 본문은 <L <L ECID ECV>…>입니다.");
        var requests = body.Items.Select(i => i is { Format: SecsFormat.List, Count: 2 }
            ? (Id: GemItems.ReadId(i.Items[0], catalog.VidFormat), Value: i.Items[1])
            : throw new FormatException("S2F15 항목은 <L ECID ECV>입니다.")).ToList();
        var changes = new List<(GemVar Var, SecsItem Value)>();
        byte eac = GemAck.EacAccepted;
        int r;
        lock (gate)
        {
            r = run;
            foreach (var (id, value) in requests)
            {
                if (!catalog.Vars.TryGetValue(id, out var v) || v.Kind != GemVarKind.Constant) { eac = GemAck.EacUnknownConstant; break; }
                var converted = GemItems.ConvertNumeric(value, v.Format);
                if (converted == null || !GemItems.Matches(converted, v) || (GemItems.IsNumeric(v.Format) && !GemItems.InRange(converted, v.Min, v.Max))) { eac = GemAck.EacOutOfRange; break; }
                changes.Add((v, converted));
            }
        }
        var handler = EquipmentConstantsChangeRequested;
        if (eac != GemAck.EacAccepted || handler == null)
        {
            if (eac == GemAck.EacAccepted) ApplyConstants(changes, r);
            Respond(e, GemItems.Ack(eac));
            return;
        }
        if (!e.Message.ReplyExpected)
        {
            Error("W비트가 없는 S2F15는 앱 승인을 거칠 수 없어 적용하지 않았습니다.");
            return;
        }
        var args = new GemConstantsChangeEventArgs([.. changes.Select(c => (c.Var.Id, c.Value))]);
        var d = DeferReply(e, new SecsMessage(2, 16, false, GemItems.Ack(GemAck.EacBusy)), "S2F15(EC 변경 승인)");
        PostApp(() =>
        {
            byte result;
            try { handler(this, args); result = args.Eac; }
            catch (Exception ex)
            {
                result = GemAck.EacBusy;                                                   // 실패 쪽으로: 아무것도 적용하지 않음
                ErrorIn(d.Run, $"S2F15(EC 변경 승인): EquipmentConstantsChangeRequested 핸들러 예외로 EAC 2로 응답합니다: {ex.GetType().Name}: {ex.Message}");
            }
            FinishDeferred(d, new SecsMessage(2, 16, false, GemItems.Ack(result)), () => { if (result == GemAck.EacAccepted) ApplyConstants(changes, d.Run); });
        });
    }

    // 적용(실행 번호 r의 요청). Stop → Start 뒤에 돌아온 이전 실행의 결과는 버린다
    void ApplyConstants(List<(GemVar Var, SecsItem Value)> changes, int r)
    {
        lock (gate)
        {
            if (!running || run != r) return;
            foreach (var (v, value) in changes) values[v.Key] = value;
        }
    }
}
