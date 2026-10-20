using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

// 변수 값(SetValue/GetValue)과 상태·이름 조회 S1F3/F4, S1F11/F12, S1F21/F22, S1F23/F24
public partial class GemEquipment
{
    const int S1F3 = 1 * 256 + 3, S1F11 = 1 * 256 + 11, S1F21 = 1 * 256 + 21, S1F23 = 1 * 256 + 23;

    partial void AddDataHandlers()
    {
        handlers[S1F3] = OnS1F3;
        handlers[S1F11] = OnS1F11;
        handlers[S1F21] = OnS1F21;
        handlers[S1F23] = OnS1F23;
    }

    /// <summary>Changes the value of an SV or DV. It is used in later S1F3 replies and reports (S6F11).</summary>
    /// <param name="vid">SVID or DVID</param>
    /// <param name="value">New value (its format must match the format in the definition; arrays and lists are allowed)</param>
    /// <exception cref="ArgumentNullException">value is null</exception>
    /// <exception cref="ArgumentException">The VID is not defined, is a standard variable (managed by the library) or an EC (use <c>SetConstant</c>), or the format differs</exception>
    /// <exception cref="InvalidOperationException">Not started</exception>
    public void SetValue(GemId vid, SecsItem value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Started();
        lock (gate)
        {
            var v = FindVarLocked(vid, nameof(vid));
            if (v.Standard != null) throw new ArgumentException($"표준 변수 {v.Name}의 값은 라이브러리가 관리합니다.", nameof(vid));
            if (v.Kind == GemVarKind.Constant) throw new ArgumentException($"EC {vid}는 SetConstant로 바꿉니다.", nameof(vid));
            if (!GemItems.Matches(value, v))
                throw new ArgumentException($"VID {vid}의 포맷은 {GemItems.FormatName(v.Format)}입니다. 받은 값: {value.ToSml()}", nameof(value));
            values[v.Key] = value;
        }
    }

    /// <summary>Returns the current value of a variable (SV, DV, or EC, including standard ones).</summary>
    /// <param name="vid">VID</param>
    /// <returns>The current value</returns>
    /// <exception cref="ArgumentException">The VID is not defined</exception>
    /// <exception cref="InvalidOperationException">Not started</exception>
    public SecsItem GetValue(GemId vid)
    {
        Started();
        lock (gate) return ValueLocked(FindVarLocked(vid, nameof(vid)));
    }

    GemVar FindVarLocked(GemId vid, string paramName)
        => catalog.Vars.TryGetValue(GemItems.Key(vid, catalog.VidFormat), out var v) ? v : throw new ArgumentException($"정의되지 않은 VID {vid}입니다.", paramName);

    // 지금 값(gate 안): 표준 SV는 상태에서 계산, 나머지는 저장값. 계산이 던지면 그 포맷의 길이 0 값 + ProtocolError
    // (상태를 바꾼 뒤 CE 본문을 만드는 경로가 있으므로 여기서 던지면 안 됨)
    SecsItem ValueLocked(GemVar v)
    {
        if (v.Kind != GemVarKind.Status || v.Standard is not { } s) return values[v.Key];
        if (!standardValues.TryGetValue(s, out var f)) return v.Initial;
        try { return f(); }
        catch (Exception ex)
        {
            Error($"표준 SV {v.Name}({v.Key}) 값을 만들지 못해 길이 0 값을 씁니다: {ex.GetType().Name}: {ex.Message}");
            return GemItems.Empty(v.Format);
        }
    }

    // 요청 목록 <L ID…> 읽기. 본문이 없으면 빈 목록(= 전체)으로 본다
    static List<GemId> RequestedIds(SecsItem? body, SecsFormat format) => body == null ? [] : GemItems.ReadIdList(body, format);

    // S1F3: 빈 목록 = 모든 SV(정의 순서, 표준 SV 먼저). 모르는 SVID = 길이 0 L
    void OnS1F3(SecsPrimaryEventArgs e)
    {
        var ids = RequestedIds(e.Message.Item, catalog.VidFormat);
        SecsItem body;
        lock (gate)
        {
            body = ids.Count == 0
                ? SecsItem.L([.. catalog.StatusVars.Select(ValueLocked)])
                : SecsItem.L([.. ids.Select(id => catalog.Vars.TryGetValue(id, out var v) && v.Kind == GemVarKind.Status ? ValueLocked(v) : SecsItem.L())]);
        }
        Respond(e, body);
    }

    // S1F11: <L <L SVID SVNAME UNITS>…>. 모르는 SVID는 이름·단위가 빈 A
    void OnS1F11(SecsPrimaryEventArgs e) => Respond(e, NameList(e.Message.Item, catalog.StatusVars, GemVarKind.Status));

    // S1F21: <L <L VID DVVALNAME UNITS>…> (DV만)
    void OnS1F21(SecsPrimaryEventArgs e) => Respond(e, NameList(e.Message.Item, catalog.DataVars, GemVarKind.Data));

    SecsItem NameList(SecsItem? request, List<GemVar> all, GemVarKind kind)
    {
        var ids = RequestedIds(request, catalog.VidFormat);
        var items = ids.Count == 0
            ? all.Select(v => NameEntry(v.Key.ToItem(catalog.VidFormat), v))
            : ids.Select((id, i) => catalog.Vars.TryGetValue(id, out var v) && v.Kind == kind ? NameEntry(v.Key.ToItem(catalog.VidFormat), v) : NameEntry(request!.Items[i], null));
        return SecsItem.L([.. items]);
    }

    static SecsItem NameEntry(SecsItem id, GemVar? v) => SecsItem.L(id, SecsItem.A(v?.Name ?? ""), SecsItem.A(v?.Units ?? ""));

    // S1F23: <L <L CEID CENAME <L VID…>>…>. 모르는 CEID는 빈 이름과 빈 목록
    void OnS1F23(SecsPrimaryEventArgs e)
    {
        var request = e.Message.Item;
        var ids = RequestedIds(request, catalog.CeidFormat);
        SecsItem Entry(SecsItem id, GemEventDef? ce)
            => SecsItem.L(id, SecsItem.A(ce?.Name ?? ""), SecsItem.L([.. (ce?.VidKeys ?? []).Select(k => k.ToItem(catalog.VidFormat))]));
        var items = ids.Count == 0
            ? catalog.EventList.Select(ce => Entry(ce.Key.ToItem(catalog.CeidFormat), ce))
            : ids.Select((id, i) => Entry(catalog.Events.TryGetValue(id, out var ce) ? ce.Key.ToItem(catalog.CeidFormat) : request!.Items[i], catalog.Events.GetValueOrDefault(id)));
        Respond(e, SecsItem.L([.. items]));
    }
}
