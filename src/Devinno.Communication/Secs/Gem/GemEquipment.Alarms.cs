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
    /// <remarks>The alarm state changes even while OFF-LINE or not communicating (only the sending is skipped). If ON-LINE but communication is not established, the S5F1 is reported as <see cref="SendFailed"/> (NotCommunicating).</remarks>
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
                var s5f1 = new SecsMessage(5, 1, alarmReplyExpected, AlarmEntry(a, set));
                if (commState == GemCommunicationState.Communicating) EnqueueLocked(s5f1);
                else DiscardLocked(s5f1, GemSendFailure.NotCommunicating);
            }
            TriggerLocked(set ? a.SetEvent : a.ClearEvent, new Dictionary<GemId, SecsItem> { [catalog.Std(GemStandard.AlarmID)] = a.Key.ToItem(catalog.AlidFormat) });
            return true;
        }
    }

    // <L ALCD ALID ALTX>. ALCD = B 1바이트: 발생 중이면 0x80 | 분류
    SecsItem AlarmEntry(GemAlarmDef a, bool set)
        => SecsItem.L(SecsItem.B((byte)((set ? GemAck.AlcdSet : 0) | a.Def.Category)), a.Key.ToItem(catalog.AlidFormat), SecsItem.A(a.Def.Text));

    SecsItem AlarmListLocked(IEnumerable<GemAlarmDef> alarms) => SecsItem.L([.. alarms.Select(a => AlarmEntry(a, alarmsSet.Contains(a.Key)))]);

    // S5F3 <L ALED ALID> → ACKC5. ALED의 0x80 = 켬(관대하게 U1·BOOLEAN도). ALID가 길이 0이면 모든 알람. 모르는 ALID = 1
    void OnS5F3(SecsPrimaryEventArgs e)
    {
        if (e.Message.Item is not { Format: SecsFormat.List, Count: 2 } body) throw new FormatException("S5F3 본문은 <L ALED ALID>입니다.");
        var aled = body.Items[0];
        bool enable = aled switch
        {
            { Format: SecsFormat.Boolean, Count: 1 } => aled.GetBooleans()[0],
            { Format: SecsFormat.Binary or SecsFormat.U1, Count: 1 } => (aled.GetInt64() & GemAck.AledEnable) != 0,
            _ => throw new FormatException($"ALED는 B 1바이트여야 합니다: {GemItems.Sml(aled)}"),
        };
        var ids = GemItems.ReadIdVector(body.Items[1], catalog.AlidFormat);
        if (ids.Count > 1) throw new FormatException("S5F3의 ALID는 하나(또는 길이 0 = 전체)입니다.");
        byte ack = GemAck.Ackc5Accepted;
        lock (gate)
        {
            if (ids.Count == 1 && !catalog.Alarms.ContainsKey(ids[0])) ack = GemAck.Ackc5Error;
            else
            {
                foreach (var key in ids.Count == 0 ? catalog.AlarmList.Select(a => a.Key) : ids)
                {
                    if (enable) alarmsEnabled.Add(key);
                    else alarmsEnabled.Remove(key);
                }
            }
        }
        Respond(e, GemItems.Ack(ack));
    }

    // S5F7 → 보고가 켜진 알람만, S5F6과 같은 형식
    void OnS5F7(SecsPrimaryEventArgs e)
    {
        SecsItem body;
        lock (gate) body = AlarmListLocked(catalog.AlarmList.Where(a => alarmsEnabled.Contains(a.Key)));
        Respond(e, body);
    }

    // S5F5 ALID(배열, 길이 0 = 전체) → <L <L ALCD ALID ALTX>…>. 모르는 ALID 항목은 <L <B> ALID <A>>
    void OnS5F5(SecsPrimaryEventArgs e)
    {
        var ids = GemItems.ReadIdVector(e.Message.Item ?? SecsItem.L(), catalog.AlidFormat);
        SecsItem body;
        lock (gate)
        {
            body = ids.Count == 0
                ? AlarmListLocked(catalog.AlarmList)
                : SecsItem.L([.. ids.Select(id => catalog.Alarms.TryGetValue(id, out var a)
                    ? AlarmEntry(a, alarmsSet.Contains(a.Key))
                    : SecsItem.L(SecsItem.B(), GemItems.Fits(id, catalog.AlidFormat) ? id.ToItem(catalog.AlidFormat) : SecsItem.A(id.ToString()), SecsItem.A("")))]);
        }
        Respond(e, body);
    }
}
