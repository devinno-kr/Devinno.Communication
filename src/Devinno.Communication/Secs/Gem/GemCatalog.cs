using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

// 앱이 등록한 정의(Start 전에만 바뀜). 같은 ID 두 번은 등록할 때 바로 ArgumentException(포맷에 따른 겹침·표준 ID와의 겹침은 Start의 GemCatalog.Build가 검사).
internal sealed class GemDefinitionSet
{
    public readonly List<GemStatusVariable> StatusVariables = [];
    public readonly List<GemDataVariable> DataVariables = [];
    public readonly List<GemEquipmentConstant> Constants = [];
    public readonly List<GemCollectionEvent> Events = [];
    public readonly List<GemAlarm> Alarms = [];
    public readonly List<GemRemoteCommand> Commands = [];
    public readonly List<(int Stream, int Function)> Messages = [];

    IEnumerable<GemId> Vids => StatusVariables.Select(v => v.Id).Concat(DataVariables.Select(v => v.Id)).Concat(Constants.Select(v => v.Id));

    public void Add(GemStatusVariable sv) { NewVid(sv?.Id, nameof(sv)); StatusVariables.Add(sv!); }
    public void Add(GemDataVariable dv) { NewVid(dv?.Id, nameof(dv)); DataVariables.Add(dv!); }
    public void Add(GemEquipmentConstant ec) { NewVid(ec?.Id, nameof(ec)); Constants.Add(ec!); }

    public void Add(GemCollectionEvent ce)
    {
        ArgumentNullException.ThrowIfNull(ce);
        if (Events.Any(e => e.Id == ce.Id)) throw new ArgumentException($"CEID {ce.Id}가 이미 정의되어 있습니다.", nameof(ce));
        Events.Add(ce);
    }

    public void Add(GemAlarm alarm)
    {
        ArgumentNullException.ThrowIfNull(alarm);
        if (Alarms.Any(a => a.Id == alarm.Id)) throw new ArgumentException($"ALID {alarm.Id}가 이미 정의되어 있습니다.", nameof(alarm));
        Alarms.Add(alarm);
    }

    public void Add(GemRemoteCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (Commands.Any(c => c.Name == command.Name)) throw new ArgumentException($"리모트 명령 {command.Name}이(가) 이미 정의되어 있습니다.", nameof(command));
        Commands.Add(command);
    }

    public void AddMessage(int stream, int function)
    {
        if (stream is < 1 or > 127) throw new ArgumentOutOfRangeException(nameof(stream), stream, "Stream은 1~127 입니다.");
        if (function is < 1 or > 255 || function % 2 == 0) throw new ArgumentException($"등록 메시지는 1차 메시지(홀수 Function 1~255)여야 합니다. 현재: F{function}", nameof(function));
        if (stream == 9) throw new ArgumentException("Stream 9(오류 메시지)는 등록할 수 없습니다.", nameof(stream));
        if (GemCatalog.IsGemMessage(stream, function)) throw new ArgumentException($"S{stream}F{function}은(는) GEM이 처리하는 메시지라 등록할 수 없습니다.", nameof(function));
        if (Messages.Contains((stream, function))) throw new ArgumentException($"S{stream}F{function}은(는) 이미 등록되어 있습니다.", nameof(function));
        Messages.Add((stream, function));
    }

    public void Clear()
    {
        StatusVariables.Clear(); DataVariables.Clear(); Constants.Clear(); Events.Clear(); Alarms.Clear(); Commands.Clear(); Messages.Clear();
    }

    void NewVid(GemId? id, string paramName)
    {
        if (id == null) throw new ArgumentNullException(paramName);
        if (Vids.Contains(id.Value)) throw new ArgumentException($"VID {id}가 이미 정의되어 있습니다(SV·DV·EC는 ID 공간이 하나).", paramName);
    }
}

// 변수(SV·DV·EC, 표준 포함) 하나. Key = 표의 열쇠이자 보내는 ID(GemItems.Key)
internal sealed class GemVar(GemVarKind kind, GemId id, GemId key, string name, string units, SecsFormat format, SecsItem initial,
    GemStandard? standard = null, SecsItem? min = null, SecsItem? max = null)
{
    public GemVarKind Kind { get; } = kind;
    public GemId Id { get; } = id;
    public GemId Key { get; } = key;
    public string Name { get; } = name;
    public string Units { get; } = units;
    public SecsFormat Format { get; } = format;
    public SecsItem Initial { get; } = initial;
    public GemStandard? Standard { get; } = standard;
    public SecsItem? Min { get; } = min;
    public SecsItem? Max { get; } = max;
}

internal enum GemVarKind { Status, Data, Constant }

// 수집 이벤트 하나(표준·앱·알람 CE)
internal sealed class GemEventDef(GemId id, GemId key, string name, IReadOnlyList<GemId> vidKeys, GemStandard? standard = null, GemAlarmDef? alarm = null)
{
    public GemId Id { get; } = id;
    public GemId Key { get; } = key;
    public string Name { get; } = name;
    public IReadOnlyList<GemId> VidKeys { get; } = vidKeys;
    public GemStandard? Standard { get; } = standard;
    public GemAlarmDef? Alarm { get; } = alarm;
}

internal sealed class GemAlarmDef(GemAlarm def, GemId key)
{
    public GemAlarm Def { get; } = def;
    public GemId Key { get; } = key;
    public GemEventDef SetEvent { get; set; } = null!;
    public GemEventDef ClearEvent { get; set; } = null!;
}

// Start 때 만드는 실행 중 정의 표(읽기 전용, lock 없이 읽음). 정의가 틀리면 InvalidOperationException
internal sealed class GemCatalog
{
    // GEM이 처리하는 Host 1차 메시지(등록 메시지와 겹치면 안 됨). 키 = Stream * 256 + Function
    static readonly HashSet<int> GemMessages =
    [
        .. new[] { 1, 3, 11, 13, 15, 17, 21, 23 }.Select(f => 1 * 256 + f),
        .. new[] { 13, 15, 17, 29, 31, 33, 35, 37, 39, 41 }.Select(f => 2 * 256 + f),
        .. new[] { 3, 5, 7 }.Select(f => 5 * 256 + f),
        .. new[] { 15, 19 }.Select(f => 6 * 256 + f),
        .. new[] { 3, 5 }.Select(f => 10 * 256 + f),
    ];

    // GEM이 처리하는 Stream(지원 Stream = 이것 ∪ 등록 메시지의 Stream)
    public static readonly int[] GemStreams = [1, 2, 5, 6, 9, 10];

    public static bool IsGemMessage(int stream, int function) => GemMessages.Contains(stream * 256 + function);

    public required SecsFormat VidFormat { get; init; }
    public required SecsFormat CeidFormat { get; init; }
    public required SecsFormat RptidFormat { get; init; }
    public required SecsFormat AlidFormat { get; init; }
    public required SecsFormat DataidFormat { get; init; }

    public Dictionary<GemId, GemVar> Vars { get; } = [];
    public List<GemVar> StatusVars { get; } = [];
    public List<GemVar> DataVars { get; } = [];
    public List<GemVar> Constants { get; } = [];
    public Dictionary<GemId, GemEventDef> Events { get; } = [];
    public List<GemEventDef> EventList { get; } = [];
    public Dictionary<GemId, GemAlarmDef> Alarms { get; } = [];
    public List<GemAlarmDef> AlarmList { get; } = [];
    public Dictionary<string, GemRemoteCommand> Commands { get; } = new(StringComparer.Ordinal);
    public HashSet<int> Messages { get; } = [];
    public HashSet<int> Streams { get; } = [.. GemStreams];
    readonly Dictionary<GemStandard, GemId> standardKeys = [];

    // 표준 변수·이벤트의 열쇠
    public GemId Std(GemStandard s) => standardKeys[s];

    public static GemCatalog Build(GemDefinitionSet defs, GemStandardIds ids, SecsFormat vid, SecsFormat ceid, SecsFormat rptid, SecsFormat alid, SecsFormat dataid)
    {
        var c = new GemCatalog { VidFormat = vid, CeidFormat = ceid, RptidFormat = rptid, AlidFormat = alid, DataidFormat = dataid };
        bool vidA = vid == SecsFormat.ASCII, ceidA = ceid == SecsFormat.ASCII;
        GemId StdId(GemStandard s, bool ascii) => ids.Get(s) ?? GemStandardIds.Default(s, ascii);

        // 변수: 표준 SV → 앱 SV, 표준 DV → 앱 DV, 표준 EC → 앱 EC
        SecsFormat listFormat(SecsFormat f) => f == SecsFormat.ASCII ? SecsFormat.List : f;
        void AddStd(GemVarKind kind, GemStandard s, SecsFormat format, SecsItem initial, string units = "", SecsItem? min = null, SecsItem? max = null)
            => c.AddVar(new GemVar(kind, StdId(s, vidA), c.VidKey(StdId(s, vidA), $"표준 {s}"), s.ToString(), units, format, initial, s, min, max), s);
        AddStd(GemVarKind.Status, GemStandard.Clock, SecsFormat.ASCII, SecsItem.A(""));
        AddStd(GemVarKind.Status, GemStandard.ControlState, SecsFormat.U1, SecsItem.U1(0));
        AddStd(GemVarKind.Status, GemStandard.PreviousControlState, SecsFormat.U1, SecsItem.U1(0));
        AddStd(GemVarKind.Status, GemStandard.EventsEnabled, listFormat(ceid), GemItems.Empty(listFormat(ceid)));
        AddStd(GemVarKind.Status, GemStandard.AlarmsEnabled, listFormat(alid), GemItems.Empty(listFormat(alid)));
        AddStd(GemVarKind.Status, GemStandard.AlarmsSet, listFormat(alid), GemItems.Empty(listFormat(alid)));
        AddStd(GemVarKind.Status, GemStandard.ProcessState, SecsFormat.U1, SecsItem.U1(0));
        AddStd(GemVarKind.Status, GemStandard.PreviousProcessState, SecsFormat.U1, SecsItem.U1(0));
        foreach (var sv in defs.StatusVariables)
            c.AddVar(new GemVar(GemVarKind.Status, sv.Id, c.VidKey(sv.Id, $"SV {sv.Id}"), sv.Name, sv.Units, sv.Format, sv.InitialValue), null);
        AddStd(GemVarKind.Data, GemStandard.AlarmID, alid, GemItems.Empty(alid));
        foreach (var dv in defs.DataVariables)
            c.AddVar(new GemVar(GemVarKind.Data, dv.Id, c.VidKey(dv.Id, $"DV {dv.Id}"), dv.Name, dv.Units, dv.Format, dv.InitialValue), null);
        AddStd(GemVarKind.Constant, GemStandard.EstablishCommunicationsTimeout, SecsFormat.U2, SecsItem.U2(10), "s", SecsItem.U2(1), SecsItem.U2(3600));
        AddStd(GemVarKind.Constant, GemStandard.TimeFormat, SecsFormat.U1, SecsItem.U1(1), "", SecsItem.U1(0), SecsItem.U1(2));
        foreach (var ec in defs.Constants)
            c.AddVar(new GemVar(GemVarKind.Constant, ec.Id, c.VidKey(ec.Id, $"EC {ec.Id}"), ec.Name, ec.Units, ec.Format, ec.Default, null, ec.Min, ec.Max), null);

        // 이벤트: 표준 CE → 앱 CE → 알람 CE(알람마다 Set, Clear)
        void AddStdEvent(GemStandard s, params GemStandard[] vids)
        {
            var id = StdId(s, ceidA);
            c.AddEvent(new GemEventDef(id, c.CeKey(id, $"표준 {s}"), s.ToString(), [.. vids.Select(c.Std)], s), s);
        }
        AddStdEvent(GemStandard.EquipmentOffline, GemStandard.ControlState, GemStandard.Clock);
        AddStdEvent(GemStandard.ControlStateLocal, GemStandard.ControlState, GemStandard.Clock);
        AddStdEvent(GemStandard.ControlStateRemote, GemStandard.ControlState, GemStandard.Clock);
        AddStdEvent(GemStandard.ProcessStateChange, GemStandard.ProcessState, GemStandard.PreviousProcessState);
        AddStdEvent(GemStandard.OperatorEquipmentConstantChange);
        AddStdEvent(GemStandard.MessageRecognition);
        foreach (var ce in defs.Events)
        {
            var vids = new List<GemId>();
            foreach (var v in ce.DataVariables)
            {
                var k = GemItems.Key(v, vid);
                if (!c.Vars.ContainsKey(k)) throw new InvalidOperationException($"CE {ce.Id}의 DataVariables에 정의되지 않은 VID {v}가 있습니다.");
                vids.Add(k);
            }
            c.AddEvent(new GemEventDef(ce.Id, c.CeKey(ce.Id, $"CE {ce.Id}"), ce.Name, vids), null);
        }
        foreach (var a in defs.Alarms)
        {
            var key = GemItems.Key(a.Id, alid);
            if (!GemItems.Fits(a.Id, alid)) throw new InvalidOperationException($"ALID {a.Id}는 AlidFormat({GemItems.FormatName(alid)})으로 보낼 수 없습니다.");
            if (c.Alarms.ContainsKey(key)) throw new InvalidOperationException($"ALID {a.Id}가 두 번 정의되었습니다.");
            var def = new GemAlarmDef(a, key);
            def.SetEvent = new GemEventDef(a.SetCeid, c.CeKey(a.SetCeid, $"알람 {a.Id}의 SetCeid"), $"Alarm {a.Id} Set", [c.Std(GemStandard.AlarmID)], null, def);
            def.ClearEvent = new GemEventDef(a.ClearCeid, c.CeKey(a.ClearCeid, $"알람 {a.Id}의 ClearCeid"), $"Alarm {a.Id} Clear", [c.Std(GemStandard.AlarmID)], null, def);
            c.AddEvent(def.SetEvent, null);
            c.AddEvent(def.ClearEvent, null);
            c.Alarms[key] = def;
            c.AlarmList.Add(def);
        }

        foreach (var cmd in defs.Commands) c.Commands[cmd.Name] = cmd;
        foreach (var (s, f) in defs.Messages)
        {
            c.Messages.Add(s * 256 + f);
            c.Streams.Add(s);
        }
        return c;
    }

    GemId VidKey(GemId id, string what) => Key(id, VidFormat, "VidFormat", what);
    GemId CeKey(GemId id, string what) => Key(id, CeidFormat, "CeidFormat", what);

    static GemId Key(GemId id, SecsFormat f, string setting, string what)
    {
        if (!GemItems.Fits(id, f))
            throw new InvalidOperationException($"{what}의 ID {id}는 {setting}({GemItems.FormatName(f)})으로 보낼 수 없습니다(문자열 ID는 A 포맷에서만, 숫자는 포맷 범위 안).");
        return GemItems.Key(id, f);
    }

    void AddVar(GemVar v, GemStandard? s)
    {
        if (Vars.TryGetValue(v.Key, out var old))
            throw new InvalidOperationException($"VID {v.Key}가 겹칩니다: {Describe(old)}와 {Describe(v)} (SV·DV·EC·표준 변수는 ID 공간이 하나, 표준 ID는 StandardIds로 바꿀 수 있음)");
        Vars[v.Key] = v;
        (v.Kind switch { GemVarKind.Status => StatusVars, GemVarKind.Data => DataVars, _ => Constants }).Add(v);
        if (s != null) standardKeys[s.Value] = v.Key;
    }

    void AddEvent(GemEventDef e, GemStandard? s)
    {
        if (Events.TryGetValue(e.Key, out var old))
            throw new InvalidOperationException($"CEID {e.Key}가 겹칩니다: \"{old.Name}\"와 \"{e.Name}\" (알람 CE도 CEID 공간을 씀, 표준 ID는 StandardIds로 바꿀 수 있음)");
        Events[e.Key] = e;
        EventList.Add(e);
        if (s != null) standardKeys[s.Value] = e.Key;
    }

    static string Describe(GemVar v) => v.Standard != null ? $"표준 {v.Standard}" : $"{v.Kind switch { GemVarKind.Status => "SV", GemVarKind.Data => "DV", _ => "EC" }} \"{v.Name}\"";
}
