using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

// 알람: SetAlarm/ClearAlarm(S5F1 + 알람 CE), S5F3 활성화, S5F5/S5F7 목록
public partial class GemEquipment
{
    const int S5F3 = 5 * 256 + 3, S5F5 = 5 * 256 + 5, S5F7 = 5 * 256 + 7;

    bool alarmsEnabledByDefault, alarmReplyExpected = true;
    readonly HashSet<GemId> alarmsEnabled = [];     // gate: S5F1을 보낼 ALID 열쇠
    readonly HashSet<GemId> alarmsSet = [];         // gate: 발생 중인 ALID 열쇠

    /// <summary>Gets or sets whether all alarm reporting (S5F1) is turned on at Start (default: false). If false, the Host turns it on with S5F3.</summary>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public bool AlarmsEnabledByDefault { get => alarmsEnabledByDefault; set => Setting(() => alarmsEnabledByDefault = value); }

    /// <summary>Gets or sets whether S5F1 carries the W-bit (default: true). If true, ACKC5 other than 0 is reported through <see cref="SendFailed"/>.</summary>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public bool AlarmReplyExpected { get => alarmReplyExpected; set => Setting(() => alarmReplyExpected = value); }

    partial void AddAlarmHandlers()
    {
        handlers[S5F3] = OnS5F3;
        handlers[S5F5] = OnS5F5;
        handlers[S5F7] = OnS5F7;
        standardValues[GemStandard.AlarmsEnabled] = () => GemItems.IdList(catalog.AlarmList.Where(a => alarmsEnabled.Contains(a.Key)).Select(a => a.Key), catalog.AlidFormat);
        standardValues[GemStandard.AlarmsSet] = () => GemItems.IdList(catalog.AlarmList.Where(a => alarmsSet.Contains(a.Key)).Select(a => a.Key), catalog.AlidFormat);
    }

    partial void ResetAlarms()
    {
        alarmsEnabled.Clear();
        alarmsSet.Clear();
        if (alarmsEnabledByDefault) foreach (var a in catalog.AlarmList) alarmsEnabled.Add(a.Key);
    }

    /// <summary>Reports that an alarm occurred. Updates AlarmsSet, and if reporting is enabled and the state is ON-LINE, sends S5F1 (ALCD = 0x80 | category) and then the alarm Set CE (DV AlarmID = ALID).</summary>
    /// <remarks>The alarm state changes even while OFF-LINE or not communicating (only the sending is skipped). If ON-LINE but communication is not established, the S5F1 is reported as <see cref="SendFailed"/> (NotCommunicating), unless it is put in the spool (see <see cref="SpoolDirectory"/>).</remarks>
    /// <param name="alid">ALID</param>
    /// <returns>true if the state changed (false if the alarm was already set; nothing is sent)</returns>
    /// <exception cref="ArgumentException">The ALID is not defined</exception>
    /// <exception cref="InvalidOperationException">Not started</exception>
    public bool SetAlarm(GemId alid) => ChangeAlarm(alid, true);

    /// <summary>Reports that an alarm was cleared. The rules are the same as <see cref="SetAlarm"/>; ALCD carries only the category, and the CE is the alarm Clear CE.</summary>
    /// <param name="alid">ALID</param>
    /// <returns>true if the state changed (false if the alarm was not set)</returns>
    /// <exception cref="ArgumentException">The ALID is not defined</exception>
    /// <exception cref="InvalidOperationException">Not started</exception>
    public bool ClearAlarm(GemId alid) => ChangeAlarm(alid, false);

    bool ChangeAlarm(GemId alid, bool set)
    {
        Started();
        lock (gate)
        {
            if (!catalog.Alarms.TryGetValue(GemItems.Key(alid, catalog.AlidFormat), out var a))
                throw new ArgumentException($"정의되지 않은 ALID {alid}입니다.", nameof(alid));
            if (set ? !alarmsSet.Add(a.Key) : !alarmsSet.Remove(a.Key)) return false;
            if (running && alarmsEnabled.Contains(a.Key) && commState != GemCommunicationState.Disabled && !IsOffline(control))
            {
                var s5f1 = GemMessages.S5F1(Alcd(a, set), a.Key, a.Def.Text, formats, alarmReplyExpected);
                SendOrSpoolLocked(s5f1);
            }
            TriggerLocked(set ? a.SetEvent : a.ClearEvent, new Dictionary<GemId, SecsItem> { [catalog.Std(GemStandard.AlarmID)] = a.Key.ToItem(catalog.AlidFormat) });
            return true;
        }
    }

    // ALCD: 발생 중이면 0x80 | 분류
    static byte Alcd(GemAlarmDef a, bool set) => (byte)((set ? GemAck.AlcdSet : 0) | a.Def.Category);

    // 알람 정보(gate 안): ALCD는 지금 발생 여부
    GemAlarmInfo AlarmInfoLocked(GemAlarmDef a) => new(a.Key, Alcd(a, alarmsSet.Contains(a.Key)), a.Def.Text);

    // S5F3 <L ALED ALID> → ACKC5. ALED의 0x80 = 켬(관대하게 U1·BOOLEAN도). ALID가 길이 0이면 모든 알람. 모르는 ALID = 1
    void OnS5F3(SecsPrimaryEventArgs e)
    {
        var (enable, alid) = GemMessages.ParseS5F3(e.Message);     // 구조·ALED·ALID 둘 이상 = FormatException(S9F7)
        if (alid is { } one)
        {
            var item = e.Message.Item!.Items[1];
            Expect(one, item.Format == SecsFormat.List ? item.Items[0].Format : item.Format, catalog.AlidFormat);   // 설정 포맷(D2)
        }
        byte ack = GemAck.Ackc5Accepted;
        lock (gate)
        {
            if (alid is { } id && !catalog.Alarms.ContainsKey(id)) ack = GemAck.Ackc5Error;
            else
            {
                foreach (var key in alid is { } only ? new[] { only } : catalog.AlarmList.Select(a => a.Key))
                {
                    if (enable) alarmsEnabled.Add(key);
                    else alarmsEnabled.Remove(key);
                }
            }
        }
        Respond(e, GemMessages.S5F4(ack));
    }

    // S5F7 → 보고가 켜진 알람만, S5F6과 같은 형식
    void OnS5F7(SecsPrimaryEventArgs e)
    {
        List<GemAlarmInfo> alarms;
        lock (gate) alarms = [.. catalog.AlarmList.Where(a => alarmsEnabled.Contains(a.Key)).Select(AlarmInfoLocked)];
        Respond(e, GemMessages.S5F8(alarms, formats));
    }

    // S5F5 ALID(배열, 길이 0 = 전체) → <L <L ALCD ALID ALTX>…>. 모르는 ALID 항목은 <L <B> ALID <A>>
    void OnS5F5(SecsPrimaryEventArgs e)
    {
        // 2층은 <L ALID…>·정수 배열·A 하나(본문 없음·길이 0 = 전체)를 받는다. 각 ALID는 설정 포맷(D2)
        var ids = GemMessages.ParseS5F5(e.Message);
        var request = e.Message.Item;
        for (int i = 0; i < ids.Count; i++) Expect(ids[i], request!.Format == SecsFormat.List ? request.Items[i].Format : request.Format, catalog.AlidFormat);
        List<(SecsItem Alid, byte? Alcd, string Text)> alarms;
        lock (gate)
        {
            (SecsItem, byte?, string) Known(GemAlarmDef a) => (a.Key.ToItem(catalog.AlidFormat), Alcd(a, alarmsSet.Contains(a.Key)), a.Def.Text);
            alarms = ids.Count == 0
                ? [.. catalog.AlarmList.Select(Known)]
                : [.. ids.Select(id => catalog.Alarms.TryGetValue(id, out var a)
                    ? Known(a)
                    : (GemItems.Fits(id, catalog.AlidFormat) ? id.ToItem(catalog.AlidFormat) : SecsItem.A(id.ToString()), (byte?)null, ""))];
        }
        Respond(e, GemMessages.S5F6Entries(alarms));
    }
}
