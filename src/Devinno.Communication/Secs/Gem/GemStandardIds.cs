namespace Devinno.Communication.Secs.Gem;

/// <summary>
/// The IDs of the standard variables and events (E30 names) whose values the library manages. If a property is null, the default ID is used.
/// </summary>
/// <remarks>
/// <para>Default IDs (when the format is numeric): SV Clock 1001, ControlState 1002, PreviousControlState 1003, EventsEnabled 1004, AlarmsEnabled 1005, AlarmsSet 1006,
/// ProcessState 1007, PreviousProcessState 1008, SpoolCountActual 1009, SpoolCountTotal 1010, SpoolFullTime 1011, SpoolStartTime 1012 / EC EstablishCommunicationsTimeout 1101, TimeFormat 1102,
/// EnableSpooling 1103, MaxSpoolTransmit 1104, OverWriteSpool 1105 / DV AlarmID 1201 /
/// CE EquipmentOffline 1301, ControlStateLocal 1302, ControlStateRemote 1303, ProcessStateChange 1304, OperatorEquipmentConstantChange 1305, MessageRecognition 1306, SpoolingActivated 1307, SpoolingDeactivated 1308, SpoolTransmitFailure 1309.
/// The spool variables and events (SpoolCountActual to SpoolStartTime, EnableSpooling to OverWriteSpool, SpoolingActivated to SpoolTransmitFailure) exist only when <c>GemEquipment.SpoolDirectory</c> is set.
/// VIDs are sent in <c>VidFormat</c> and CEIDs in <c>CeidFormat</c>; when that format is A, the default ID is the property name as a string (for example, "Clock").</para>
/// <para><c>GemEquipment.Start</c> fails if an ID collides with an app-defined ID, so change the ID here in that case. The values are read at <c>Start</c>; a value changed while running takes effect from the next <c>Start</c>.</para>
/// </remarks>
public class GemStandardIds
{
    /// <summary>SV Clock (the GEM clock, an A item in the TimeFormat format). null = 1001</summary>
    public GemId? Clock { get; set; }
    /// <summary>SV ControlState (U1, 1 to 5). null = 1002</summary>
    public GemId? ControlState { get; set; }
    /// <summary>SV PreviousControlState (U1, initially 0). null = 1003</summary>
    public GemId? PreviousControlState { get; set; }
    /// <summary>SV EventsEnabled (list of enabled CEIDs). null = 1004</summary>
    public GemId? EventsEnabled { get; set; }
    /// <summary>SV AlarmsEnabled (list of enabled ALIDs). null = 1005</summary>
    public GemId? AlarmsEnabled { get; set; }
    /// <summary>SV AlarmsSet (list of ALIDs currently set). null = 1006</summary>
    public GemId? AlarmsSet { get; set; }
    /// <summary>SV ProcessState (U1, the SetProcessState value). null = 1007</summary>
    public GemId? ProcessState { get; set; }
    /// <summary>SV PreviousProcessState (U1). null = 1008</summary>
    public GemId? PreviousProcessState { get; set; }
    /// <summary>SV SpoolCountActual (U4, the number of messages currently in the spool). null = 1009</summary>
    public GemId? SpoolCountActual { get; set; }
    /// <summary>SV SpoolCountTotal (U4, the number of messages spooled since spooling started). null = 1010</summary>
    public GemId? SpoolCountTotal { get; set; }
    /// <summary>SV SpoolFullTime (an A item in the TimeFormat format, empty if the spool never became full). null = 1011</summary>
    public GemId? SpoolFullTime { get; set; }
    /// <summary>SV SpoolStartTime (an A item in the TimeFormat format, empty if spooling has not started). null = 1012</summary>
    public GemId? SpoolStartTime { get; set; }
    /// <summary>EC EstablishCommunicationsTimeout (U2 in seconds, default 10, 1 to 3600). null = 1101</summary>
    public GemId? EstablishCommunicationsTimeout { get; set; }
    /// <summary>EC TimeFormat (U1, default 1, 0 to 2). null = 1102</summary>
    public GemId? TimeFormat { get; set; }
    /// <summary>EC EnableSpooling (BOOLEAN, default true). null = 1103</summary>
    public GemId? EnableSpooling { get; set; }
    /// <summary>EC MaxSpoolTransmit (U4, default 0 = unlimited, 0 to 1000000). null = 1104</summary>
    public GemId? MaxSpoolTransmit { get; set; }
    /// <summary>EC OverWriteSpool (BOOLEAN, default false). null = 1105</summary>
    public GemId? OverWriteSpool { get; set; }
    /// <summary>DV AlarmID (the ALID of an alarm event). null = 1201</summary>
    public GemId? AlarmID { get; set; }
    /// <summary>CE EquipmentOffline (the equipment goes OFF-LINE). null = 1301</summary>
    public GemId? EquipmentOffline { get; set; }
    /// <summary>CE ControlStateLocal (the equipment becomes ON-LINE LOCAL). null = 1302</summary>
    public GemId? ControlStateLocal { get; set; }
    /// <summary>CE ControlStateRemote (the equipment becomes ON-LINE REMOTE). null = 1303</summary>
    public GemId? ControlStateRemote { get; set; }
    /// <summary>CE ProcessStateChange (the value changed through SetProcessState). null = 1304</summary>
    public GemId? ProcessStateChange { get; set; }
    /// <summary>CE OperatorEquipmentConstantChange (the operator changed an EC through SetConstant). null = 1305</summary>
    public GemId? OperatorEquipmentConstantChange { get; set; }
    /// <summary>CE MessageRecognition (the operator acknowledged a Host terminal message). null = 1306</summary>
    public GemId? MessageRecognition { get; set; }
    /// <summary>CE SpoolingActivated (spooling started). null = 1307</summary>
    public GemId? SpoolingActivated { get; set; }
    /// <summary>CE SpoolingDeactivated (spooling ended). null = 1308</summary>
    public GemId? SpoolingDeactivated { get; set; }
    /// <summary>CE SpoolTransmitFailure (sending the spool failed). null = 1309</summary>
    public GemId? SpoolTransmitFailure { get; set; }

    internal GemId? Get(GemStandard s) => s switch
    {
        GemStandard.Clock => Clock,
        GemStandard.ControlState => ControlState,
        GemStandard.PreviousControlState => PreviousControlState,
        GemStandard.EventsEnabled => EventsEnabled,
        GemStandard.AlarmsEnabled => AlarmsEnabled,
        GemStandard.AlarmsSet => AlarmsSet,
        GemStandard.ProcessState => ProcessState,
        GemStandard.PreviousProcessState => PreviousProcessState,
        GemStandard.SpoolCountActual => SpoolCountActual,
        GemStandard.SpoolCountTotal => SpoolCountTotal,
        GemStandard.SpoolFullTime => SpoolFullTime,
        GemStandard.SpoolStartTime => SpoolStartTime,
        GemStandard.EstablishCommunicationsTimeout => EstablishCommunicationsTimeout,
        GemStandard.TimeFormat => TimeFormat,
        GemStandard.EnableSpooling => EnableSpooling,
        GemStandard.MaxSpoolTransmit => MaxSpoolTransmit,
        GemStandard.OverWriteSpool => OverWriteSpool,
        GemStandard.AlarmID => AlarmID,
        GemStandard.EquipmentOffline => EquipmentOffline,
        GemStandard.ControlStateLocal => ControlStateLocal,
        GemStandard.ControlStateRemote => ControlStateRemote,
        GemStandard.ProcessStateChange => ProcessStateChange,
        GemStandard.OperatorEquipmentConstantChange => OperatorEquipmentConstantChange,
        GemStandard.MessageRecognition => MessageRecognition,
        GemStandard.SpoolingActivated => SpoolingActivated,
        GemStandard.SpoolingDeactivated => SpoolingDeactivated,
        _ => SpoolTransmitFailure,
    };

    internal void Set(GemStandard s, GemId? id)
    {
        switch (s)
        {
            case GemStandard.Clock: Clock = id; break;
            case GemStandard.ControlState: ControlState = id; break;
            case GemStandard.PreviousControlState: PreviousControlState = id; break;
            case GemStandard.EventsEnabled: EventsEnabled = id; break;
            case GemStandard.AlarmsEnabled: AlarmsEnabled = id; break;
            case GemStandard.AlarmsSet: AlarmsSet = id; break;
            case GemStandard.ProcessState: ProcessState = id; break;
            case GemStandard.PreviousProcessState: PreviousProcessState = id; break;
            case GemStandard.SpoolCountActual: SpoolCountActual = id; break;
            case GemStandard.SpoolCountTotal: SpoolCountTotal = id; break;
            case GemStandard.SpoolFullTime: SpoolFullTime = id; break;
            case GemStandard.SpoolStartTime: SpoolStartTime = id; break;
            case GemStandard.EstablishCommunicationsTimeout: EstablishCommunicationsTimeout = id; break;
            case GemStandard.TimeFormat: TimeFormat = id; break;
            case GemStandard.EnableSpooling: EnableSpooling = id; break;
            case GemStandard.MaxSpoolTransmit: MaxSpoolTransmit = id; break;
            case GemStandard.OverWriteSpool: OverWriteSpool = id; break;
            case GemStandard.AlarmID: AlarmID = id; break;
            case GemStandard.EquipmentOffline: EquipmentOffline = id; break;
            case GemStandard.ControlStateLocal: ControlStateLocal = id; break;
            case GemStandard.ControlStateRemote: ControlStateRemote = id; break;
            case GemStandard.ProcessStateChange: ProcessStateChange = id; break;
            case GemStandard.OperatorEquipmentConstantChange: OperatorEquipmentConstantChange = id; break;
            case GemStandard.MessageRecognition: MessageRecognition = id; break;
            case GemStandard.SpoolingActivated: SpoolingActivated = id; break;
            case GemStandard.SpoolingDeactivated: SpoolingDeactivated = id; break;
            default: SpoolTransmitFailure = id; break;
        }
    }

    // 기본 ID: 포맷이 A면 이름, 아니면 번호
    internal static GemId Default(GemStandard s, bool ascii) => ascii ? new GemId(s.ToString()) : new GemId(s switch
    {
        GemStandard.Clock => 1001,
        GemStandard.ControlState => 1002,
        GemStandard.PreviousControlState => 1003,
        GemStandard.EventsEnabled => 1004,
        GemStandard.AlarmsEnabled => 1005,
        GemStandard.AlarmsSet => 1006,
        GemStandard.ProcessState => 1007,
        GemStandard.PreviousProcessState => 1008,
        GemStandard.SpoolCountActual => 1009,
        GemStandard.SpoolCountTotal => 1010,
        GemStandard.SpoolFullTime => 1011,
        GemStandard.SpoolStartTime => 1012,
        GemStandard.EstablishCommunicationsTimeout => 1101,
        GemStandard.TimeFormat => 1102,
        GemStandard.EnableSpooling => 1103,
        GemStandard.MaxSpoolTransmit => 1104,
        GemStandard.OverWriteSpool => 1105,
        GemStandard.AlarmID => 1201,
        GemStandard.EquipmentOffline => 1301,
        GemStandard.ControlStateLocal => 1302,
        GemStandard.ControlStateRemote => 1303,
        GemStandard.ProcessStateChange => 1304,
        GemStandard.OperatorEquipmentConstantChange => 1305,
        GemStandard.MessageRecognition => 1306,
        GemStandard.SpoolingActivated => 1307,
        GemStandard.SpoolingDeactivated => 1308,
        _ => 1309,
    });
}

// 표준 변수·이벤트 (이름 = E30 이름 = GemStandardIds 속성 이름)
internal enum GemStandard
{
    Clock, ControlState, PreviousControlState, EventsEnabled, AlarmsEnabled, AlarmsSet, ProcessState, PreviousProcessState,
    SpoolCountActual, SpoolCountTotal, SpoolFullTime, SpoolStartTime,
    EstablishCommunicationsTimeout, TimeFormat,
    EnableSpooling, MaxSpoolTransmit, OverWriteSpool,
    AlarmID,
    EquipmentOffline, ControlStateLocal, ControlStateRemote, ProcessStateChange, OperatorEquipmentConstantChange, MessageRecognition,
    SpoolingActivated, SpoolingDeactivated, SpoolTransmitFailure,
}
