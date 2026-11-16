using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

// 설정 영속화(6J.4): 켜기 설정, 바뀐 표의 저장(gate 안에서 스냅숏만 만들어 저장소에 넘김), Start 때 복원
public partial class GemEquipment
{
    string? stateDirectory;
    GemStateStore? stateStore;                  // gate: 영속화를 켠 실행(Start 때 StateDirectory != null)의 저장소

    /// <summary>Folder where the settings chosen by the Host are kept across restarts (default: null = persistence off). Setting it turns persistence on at the next <see cref="Start"/>.</summary>
    /// <remarks>
    /// <para>Kept in <c>gem.state</c> (XML): the reports (S2F33), links (S2F35), CE enables (S2F37), alarm enables (S5F3), all EC values (S2F15 and <see cref="SetConstant"/>), and the LOCAL/REMOTE switch. The clock offset, SV/DV values, and the alarm set state are not kept.
    /// At <see cref="Start"/>, entries that no longer match the definitions (a removed ID, an EC value outside the new range, an ID that cannot be expressed in a changed ID format) are dropped and reported once through <see cref="ProtocolError"/>; a CE or alarm added since is enabled according to <see cref="EventsEnabledByDefault"/> / <see cref="AlarmsEnabledByDefault"/>.
    /// A damaged file is renamed to <c>gem.state.bad</c> and GEM starts from the initial values.</para>
    /// <para>The file is written on a separate thread after the reply, so a power loss right after a change may lose that last change. To go back to the initial state, delete <c>gem.state</c> while stopped.
    /// One folder serves one equipment (<see cref="Start"/> throws <see cref="IOException"/> if another equipment uses it), but it may be the same folder as <see cref="SpoolDirectory"/>.
    /// If <see cref="InitialControlState"/> is OnlineLocal or OnlineRemote, that setting wins over the saved switch position.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">An empty string</exception>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public string? StateDirectory
    {
        get => stateDirectory;
        set => Setting(() => stateDirectory = value is { Length: 0 } ? throw new ArgumentException("StateDirectory는 빈 문자열일 수 없습니다(끄려면 null).", nameof(StateDirectory)) : value);
    }

    // 테스트용: 이번 실행의 저장소
    internal GemStateStore? StateStore { get { lock (gate) return stateStore; } }

    // 지금 표를 저장소에 넘긴다(gate 안, 표가 실제로 바뀐 뒤). 영속화가 꺼져 있으면 아무것도 하지 않는다. 직렬화만 하고 파일은 저장소 스레드가 쓴다
    void SaveStateLocked()
    {
        if (stateStore == null) return;
        stateStore.Save(new GemStateSnapshot(
            [.. reports.Select(r => (r.Key, (IReadOnlyList<GemId>)[.. r.Value]))],
            [.. links.Select(l => (l.Key, (IReadOnlyList<GemId>)[.. l.Value]))],
            [.. catalog.EventList.Select(e => (e.Key, enabledEvents.Contains(e.Key)))],
            [.. catalog.AlarmList.Select(a => (a.Key, alarmsEnabled.Contains(a.Key)))],
            [.. catalog.Constants.Select(v => (v.Key, values[v.Key]))],
            localRemote));
    }

    // Start 때 저장된 설정을 지금 정의에 맞는 것만 적용한다(gate 안, Reset* 뒤). 버린 항목 수를 돌려준다(6J.4.4).
    // 버린 것이 있으면 파일도 지금 정의에 맞게 다시 쓴다
    int RestoreStateLocked(GemStateSnapshot? s)
    {
        if (s == null) return 0;
        int dropped = 0;
        var keptReports = new HashSet<GemId>();
        foreach (var (rptid, vids) in s.Reports)
        {
            var r = Convert(rptid, catalog.RptidFormat);
            var vs = vids.Select(v => Convert(v, catalog.VidFormat)).ToList();
            if (r is not { } key || vs.Any(v => v is not { } k || !catalog.Vars.ContainsKey(k))) { dropped++; continue; }
            reports[key] = [.. vs.Select(v => v!.Value)];
            keptReports.Add(key);
        }
        foreach (var (ceid, rptids) in s.Links)
        {
            if (Convert(ceid, catalog.CeidFormat) is not { } key || !catalog.Events.ContainsKey(key)) { dropped++; continue; }
            var rs = rptids.Select(r => Convert(r, catalog.RptidFormat)).Where(r => r is { } k && keptReports.Contains(k)).Select(r => r!.Value).ToList();
            if (rs.Count > 0) links[key] = rs;          // 빠진 RPTID는 리포트에서 이미 셈
        }
        foreach (var (ceid, on) in s.Events)
        {
            if (Convert(ceid, catalog.CeidFormat) is not { } key || !catalog.Events.ContainsKey(key)) { dropped++; continue; }
            if (on) enabledEvents.Add(key);
            else enabledEvents.Remove(key);
        }
        foreach (var (alid, on) in s.Alarms)
        {
            if (Convert(alid, catalog.AlidFormat) is not { } key || !catalog.Alarms.ContainsKey(key)) { dropped++; continue; }
            if (on) alarmsEnabled.Add(key);
            else alarmsEnabled.Remove(key);
        }
        foreach (var (ecid, value) in s.Constants)
        {
            if (Convert(ecid, catalog.VidFormat) is not { } key || !catalog.Vars.TryGetValue(key, out var v) || v.Kind != GemVarKind.Constant) { dropped++; continue; }
            var converted = GemItems.ConvertNumeric(value, v.Format);
            if (converted == null || !GemItems.Matches(converted, v) || (GemItems.IsNumeric(v.Format) && !GemItems.InRange(converted, v.Min, v.Max))) { dropped++; continue; }
            values[key] = converted;
        }
        // 앱이 ON-LINE 쪽 초기 상태를 정했으면 그 설정이 이긴다(버린 것으로 세지 않음)
        if (initialControlState is not (GemControlState.OnlineLocal or GemControlState.OnlineRemote)) localRemote = s.Switch;
        if (dropped > 0) SaveStateLocked();
        return dropped;
    }

    // 저장된 ID를 지금 포맷의 표 열쇠로. 바꿀 수 없으면 null(A면 숫자를 10진 문자열로, 숫자 포맷이면 문자 ID를 정수로)
    static GemId? Convert(GemId id, SecsFormat format)
    {
        if (format == SecsFormat.ASCII) id = GemItems.Key(id, format);
        else if (id.IsText)
        {
            if (!long.TryParse(id.Text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var n)) return null;
            id = new GemId(n);
        }
        return GemItems.Fits(id, format) ? id : (GemId?)null;   // (GemId?) 없이 null이면 string 암시 변환으로 GemId(null) 예외
    }
}
