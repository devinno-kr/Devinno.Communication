namespace Devinno.Communication.Secs.Gem;

// 설정 영속화(6J.4): 켜기 설정, 바뀐 표의 저장(gate 안에서 스냅숏만 만들어 저장소에 넘김), Start 때 복원
public partial class GemEquipment
{
    string? stateDirectory;
    GemStateStore? stateStore;                  // gate: 영속화를 켠 실행(Start 때 StateDirectory != null)의 저장소

    /// <summary>Folder where the settings chosen by the Host are kept across restarts (default: null = persistence off). Setting it turns persistence on at the next <see cref="Start"/>.</summary>
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
}
