using System.Globalization;
using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

// 리밋 모니터링(6J.8): Host가 S2F45로 숫자 SV의 리밋을 정하고 S2F47로 읽는다. 표는 gate 안, 정의는 gem.state에 저장
public partial class GemEquipment
{
    // 리밋 하나(VID × LIMITID): 데드밴드 양 끝(SV 포맷)과 지금 구역
    sealed class LimitState
    {
        public required SecsItem Upper;
        public required SecsItem Lower;
        public GemLimitZone Zone;
    }

    readonly Dictionary<GemId, SortedDictionary<byte, LimitState>> limits = [];   // gate. 열쇠 = SV Key

    /// <summary>Returns the limits the Host has defined on a variable, in LIMITID order, with the zone the current value is in (for diagnostics).</summary>
    /// <param name="vid">VID</param>
    /// <returns>The limits and zones; empty when the variable has no limits or cannot have limits.</returns>
    /// <exception cref="ArgumentException">The VID is not defined</exception>
    /// <exception cref="InvalidOperationException">Not started</exception>
    public IReadOnlyList<(GemLimit Limit, GemLimitZone Zone)> GetLimits(GemId vid)
    {
        Started();
        lock (gate)
        {
            var v = FindVarLocked(vid, nameof(vid));
            if (!limits.TryGetValue(v.Key, out var m)) return [];
            return [.. m.Select(kv => (new GemLimit(kv.Key, kv.Value.Upper, kv.Value.Lower), kv.Value.Zone))];
        }
    }

    partial void AddLimitHandlers()
    {
        handlers[2 * 256 + 45] = OnS2F45;
        handlers[2 * 256 + 47] = OnS2F47;
    }

    partial void ResetLimits() => limits.Clear();

    // 리밋 능력의 메시지(S2F45·S2F47) 중 하나라도 앱이 등록했으면 능력 전체를 끈다(6J.3 원칙 2)
    static bool IsLimitMessage(int key) => key == 2 * 256 + 45 || key == 2 * 256 + 47;

    // 값이 리밋 하나에 대해 어느 구역인지. 숫자 하나(NaN 아님)가 아니면 current 그대로(L5). 사이(데드밴드)면 current
    static GemLimitZone ZoneOf(SecsItem value, LimitState l, GemLimitZone current)
    {
        if (!GemItems.IsNumeric(value.Format) || value.Count != 1 || value.RawValues.GetValue(0) is float f && float.IsNaN(f) || value.RawValues.GetValue(0) is double d && double.IsNaN(d)) return current;
        if (GemItems.CompareFirst(value, l.Upper) >= 0) return GemLimitZone.Above;
        if (GemItems.CompareFirst(value, l.Lower) <= 0) return GemLimitZone.Below;
        return current;
    }

    // SetValue가 리밋 SV 값을 바꾼 뒤(gate 안, 6J.8.4): 리밋마다 구역을 갱신하고 BELOW↔ABOVE 넘어감을 방향별로 모아
    // 아래→위 CE를 먼저, 그다음 위→아래 CE를 보낸다(TriggerEvent 규칙 그대로 — OFF-LINE·CE 끔이면 안 가도 구역은 이미 바뀜)
    void EvaluateLimitsLocked(GemVar v, SecsItem value)
    {
        if (v.LimitEvent is not { } ce || !limits.TryGetValue(v.Key, out var m)) return;
        List<byte>? up = null, down = null;
        foreach (var (id, l) in m)
        {
            var z = ZoneOf(value, l, l.Zone);
            if (l.Zone == GemLimitZone.Below && z == GemLimitZone.Above) (up ??= []).Add(id);
            else if (l.Zone == GemLimitZone.Above && z == GemLimitZone.Below) (down ??= []).Add(id);
            l.Zone = z;
        }
        if (up != null) SendLimitEventLocked(v, ce, up, 0);
        if (down != null) SendLimitEventLocked(v, ce, down, 1);
    }

    void SendLimitEventLocked(GemVar v, GemEventDef ce, List<byte> ids, byte transition)
    {
        var overrides = new Dictionary<GemId, SecsItem>
        {
            [catalog.Std(GemStandard.LimitVariable)] = v.Id.ToItem(catalog.VidFormat),
            [catalog.Std(GemStandard.EventLimit)] = SecsItem.L([.. ids.Select(i => SecsItem.B(i))]),
            [catalog.Std(GemStandard.TransitionType)] = SecsItem.B(transition),
        };
        TriggerLocked(ce, overrides);
    }

    #region S2F45 / S2F46
    // 검사 순서(L1): LVACK 1 → 2 → 3, 리밋마다 LIMITACK 1 → 7 → 5 → 6 → 2 → 3 → 4. VID마다 첫 오류 하나. 오류가 하나라도 있으면 아무것도 바꾸지 않음
    void OnS2F45(SecsPrimaryEventArgs e)
    {
        var (_, defs) = GemMessages.ParseS2F45Raw(e.Message);                 // 구조 오류 → FormatException → S9F7
        var errors = new List<(SecsItem Vid, byte Lvack, byte? LimitId, byte? Limitack)>();
        lock (gate)
        {
            if (!running) return;
            var seen = new HashSet<GemId>();
            var plan = new List<(GemVar Var, List<(byte Id, SecsItem? Upper, SecsItem? Lower)> Limits)>();
            foreach (var (vidItem, ls) in defs)
            {
                if (!catalog.Vars.TryGetValue(GemItems.Key(GemId.FromItem(vidItem), catalog.VidFormat), out var v)) { errors.Add((vidItem, GemAck.LvackUnknownVid, null, null)); continue; }
                if (v.LimitCount == 0) { errors.Add((vidItem, GemAck.LvackNoLimits, null, null)); continue; }
                if (!seen.Add(v.Key)) { errors.Add((vidItem, GemAck.LvackRepeatedVid, null, null)); continue; }
                var ok = new List<(byte, SecsItem?, SecsItem?)>();
                var ids = new HashSet<byte>();
                (byte Id, byte Ack)? bad = null;
                foreach (var (id, upper, lower) in ls)
                {
                    if (CheckLimit(v, id, upper, lower, ids, out var u, out var l) is { } ack) { bad = (id, ack); break; }
                    ok.Add((id, u, l));
                }
                if (bad is { } b) { errors.Add((vidItem, GemAck.LvackLimitValueError, b.Id, b.Ack)); continue; }
                plan.Add((v, ok));
            }
            if (errors.Count == 0)
            {
                if (defs.Count == 0) limits.Clear();
                foreach (var (v, ls) in plan) ApplyLimitsLocked(v, ls);
                SaveStateLocked();
            }
        }
        Respond(e, GemMessages.S2F46(errors.Count == 0 ? GemAck.VlaackOk : GemAck.VlaackLimitError, errors));
    }

    // 리밋 하나 검사. 괜찮으면 null과 SV 포맷으로 바꾼 값(해제면 둘 다 null), 아니면 LIMITACK
    static byte? CheckLimit(GemVar v, byte id, SecsItem? upper, SecsItem? lower, HashSet<byte> ids, out SecsItem? u, out SecsItem? l)
    {
        u = l = null;
        if (id < 1 || id > v.LimitCount) return GemAck.LimitackUnknownLimitId;
        if (!ids.Add(id)) return GemAck.LimitackDuplicateLimitId;
        if (upper == null) return null;                                       // 해제(p = 0)
        foreach (var db in new[] { upper, lower! })
            if (db.Format != SecsFormat.ASCII && (!GemItems.IsNumeric(db.Format) || db.Count != 1)) return GemAck.LimitackIllegalFormat;
        if (ReadDb(upper, v, out var up) is { } a1) return a1;
        if (ReadDb(lower!, v, out var lo) is { } a2) return a2;
        ReadDb(v.LimitMax!, v, out var max);
        ReadDb(v.LimitMin!, v, out var min);
        if (up.CompareTo(max) > 0) return GemAck.LimitackUpperAboveMax;
        if (lo.CompareTo(min) < 0) return GemAck.LimitackLowerBelowMin;
        if (up.CompareTo(lo) < 0) return GemAck.LimitackUpperBelowLower;
        u = ToFormat(up, v.Format);
        l = ToFormat(lo, v.Format);
        return null;
    }

    // DB 하나를 비교용 숫자로(L2·L3): 정수 SV는 decimal, 실수 SV는 double. 소수가 든 숫자 아이템을 정수 SV에 → 5, A인데 숫자가 아님(정수 SV에 소수 포함) → 6
    static byte? ReadDb(SecsItem db, GemVar v, out IComparable n)
    {
        bool integer = GemItems.IsInteger(v.Format);
        n = 0m;
        if (db.Format == SecsFormat.ASCII)
        {
            var t = db.GetString().Trim();
            if (integer)
            {
                if (!decimal.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var dec) || dec != decimal.Truncate(dec)) return GemAck.LimitackNotNumeric;
                n = dec;
            }
            else
            {
                if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var dbl) || !double.IsFinite(dbl)) return GemAck.LimitackNotNumeric;
                n = dbl;
            }
            return null;
        }
        var raw = db.RawValues.GetValue(0)!;
        if (!integer)
        {
            double dbl = System.Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            if (double.IsNaN(dbl)) return GemAck.LimitackIllegalFormat;
            n = dbl;
            return null;
        }
        if (raw is float or double)
        {
            double dbl = System.Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            if (!double.IsFinite(dbl) || Math.Floor(dbl) != dbl || Math.Abs(dbl) > 1e20) return GemAck.LimitackIllegalFormat;
            n = (decimal)dbl;
        }
        else n = System.Convert.ToDecimal(raw, CultureInfo.InvariantCulture);
        return null;
    }

    // 검사를 통과한 숫자(LIMITMIN~LIMITMAX 안)를 SV 포맷 값 하나로
    static SecsItem ToFormat(IComparable n, SecsFormat f) => n switch
    {
        double d => f == SecsFormat.F4 ? SecsItem.F4((float)d) : SecsItem.F8(d),
        decimal m => GemItems.ConvertNumeric(m >= 0 ? SecsItem.U8((ulong)m) : SecsItem.I8((long)m), f)!,
        _ => throw new InvalidOperationException(),
    };

    // 검사를 통과한 한 VID의 정의를 적용(gate 안). 빈 목록 = 그 VID 전부 해제. 새로 정의·바꾼 리밋은 지금 값으로 구역을 정한다(CE 없음)
    void ApplyLimitsLocked(GemVar v, List<(byte Id, SecsItem? Upper, SecsItem? Lower)> ls)
    {
        if (ls.Count == 0) { limits.Remove(v.Key); return; }
        if (!limits.TryGetValue(v.Key, out var m)) limits[v.Key] = m = [];
        foreach (var (id, upper, lower) in ls)
        {
            if (upper == null) { m.Remove(id); continue; }
            var s = new LimitState { Upper = upper, Lower = lower!, Zone = GemLimitZone.NoZone };
            s.Zone = ZoneOf(values[v.Key], s, GemLimitZone.NoZone);
            m[id] = s;
        }
        if (m.Count == 0) limits.Remove(v.Key);
    }
    #endregion

    #region S2F47 / S2F48
    // 빈 목록 = 리밋 가능한 SV 전부(정의 순서). 모르는·리밋 불가 VID는 <L VID <L>>(받은 아이템 그대로, L7)
    void OnS2F47(SecsPrimaryEventArgs e)
    {
        var vids = GemMessages.ParseS2F47Raw(e.Message);
        List<(SecsItem Vid, GemLimitInfo? Info)> entries;
        lock (gate)
        {
            if (!running) return;
            entries = vids.Count == 0
                ? [.. catalog.LimitVars.Select(v => (v.Id.ToItem(catalog.VidFormat), (GemLimitInfo?)LimitInfoLocked(v)))]
                : [.. vids.Select(i => (i, catalog.Vars.TryGetValue(GemItems.Key(GemId.FromItem(i), catalog.VidFormat), out var v) && v.LimitCount > 0 ? LimitInfoLocked(v) : null))];
        }
        Respond(e, GemMessages.S2F48(entries));
    }

    GemLimitInfo LimitInfoLocked(GemVar v)
        => new(v.Id, v.Units, v.LimitMin, v.LimitMax, limits.TryGetValue(v.Key, out var m) ? [.. m.Select(kv => new GemLimit(kv.Key, kv.Value.Upper, kv.Value.Lower))] : []);
    #endregion

    #region 영속
    // gem.state에 넣을 리밋 정의(gate 안)
    List<(GemId Vid, byte LimitId, SecsItem Upper, SecsItem Lower)> LimitSnapshotLocked()
        => [.. limits.SelectMany(kv => kv.Value.Select(l => (kv.Key, l.Key, l.Value.Upper, l.Value.Lower)))];

    // 복원(gate 안, L9): 지금 정의와 맞지 않으면 버리고 센다. 구역은 NO ZONE
    int RestoreLimitsLocked(IReadOnlyList<(GemId Vid, byte LimitId, SecsItem Upper, SecsItem Lower)>? saved)
    {
        int dropped = 0;
        foreach (var (vid, id, upper, lower) in saved ?? [])
        {
            if (Convert(vid, catalog.VidFormat) is not { } key || !catalog.Vars.TryGetValue(key, out var v) || id < 1 || id > v.LimitCount) { dropped++; continue; }
            var u = GemItems.ConvertNumeric(upper, v.Format);
            var l = GemItems.ConvertNumeric(lower, v.Format);
            if (u is not { Count: 1 } || l is not { Count: 1 } || !GemItems.InRange(u, v.LimitMin, v.LimitMax) || !GemItems.InRange(l, v.LimitMin, v.LimitMax) || GemItems.CompareFirst(u, l) < 0) { dropped++; continue; }
            if (!limits.TryGetValue(key, out var m)) limits[key] = m = [];
            m[id] = new LimitState { Upper = u, Lower = l, Zone = GemLimitZone.NoZone };
        }
        return dropped;
    }
    #endregion
}
