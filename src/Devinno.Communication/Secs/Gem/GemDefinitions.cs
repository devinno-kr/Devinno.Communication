using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

/// <summary>
/// Definition of a status variable (SV). A value of the equipment that the Host can read with S1F3 and that can be included in an event report (S6F11).
/// </summary>
/// <remarks>Change the value with <c>GemEquipment.SetValue</c>; its format must match <see cref="Format"/>. The name and units are used in the S1F11 reply.</remarks>
public sealed class GemStatusVariable
{
    /// <summary>Creates a status variable definition.</summary>
    /// <param name="id">SVID (VID space: shared by SV, DV, and EC)</param>
    /// <param name="name">Name (ASCII, SVNAME in S1F11)</param>
    /// <param name="format">Value format (arrays and lists are allowed)</param>
    /// <param name="units">Units (ASCII, default: "")</param>
    /// <param name="initialValue">Value at Start (if null, a zero-length item of that format)</param>
    /// <exception cref="ArgumentNullException">name or units is null</exception>
    /// <exception cref="ArgumentException">The name or units contain characters outside ASCII (Latin-1), or the format of initialValue differs from format</exception>
    public GemStatusVariable(GemId id, string name, SecsFormat format, string units = "", SecsItem? initialValue = null)
    {
        Id = id;
        Name = GemDefinitionCheck.Ascii(name, nameof(name));
        Format = format;
        Units = GemDefinitionCheck.Ascii(units, nameof(units));
        InitialValue = GemDefinitionCheck.Value(initialValue, format, nameof(initialValue));
    }

    /// <summary>SVID</summary>
    public GemId Id { get; }
    /// <summary>Name (SVNAME)</summary>
    public string Name { get; }
    /// <summary>Value format</summary>
    public SecsFormat Format { get; }
    /// <summary>Units</summary>
    public string Units { get; }
    /// <summary>Value at Start</summary>
    public SecsItem InitialValue { get; }
}

/// <summary>
/// Definition of a data variable (DV). A value that is meaningful only when an event occurs (for example, the LOT ID that just finished); used in reports and S1F21.
/// </summary>
/// <remarks>Change the value with <c>GemEquipment.SetValue</c>, or supply it only for that report through the <c>dataValues</c> of <c>TriggerEvent</c>.</remarks>
public sealed class GemDataVariable
{
    /// <summary>Creates a data variable definition.</summary>
    /// <param name="id">DVID (VID space: shared by SV, DV, and EC)</param>
    /// <param name="name">Name (ASCII, DVVALNAME in S1F21)</param>
    /// <param name="format">Value format</param>
    /// <param name="units">Units (ASCII, default: "")</param>
    /// <param name="initialValue">Value at Start (if null, a zero-length item of that format)</param>
    /// <exception cref="ArgumentNullException">name or units is null</exception>
    /// <exception cref="ArgumentException">The name or units contain characters outside ASCII (Latin-1), or the format of initialValue differs from format</exception>
    public GemDataVariable(GemId id, string name, SecsFormat format, string units = "", SecsItem? initialValue = null)
    {
        Id = id;
        Name = GemDefinitionCheck.Ascii(name, nameof(name));
        Format = format;
        Units = GemDefinitionCheck.Ascii(units, nameof(units));
        InitialValue = GemDefinitionCheck.Value(initialValue, format, nameof(initialValue));
    }

    /// <summary>DVID</summary>
    public GemId Id { get; }
    /// <summary>Name (DVVALNAME)</summary>
    public string Name { get; }
    /// <summary>Value format</summary>
    public SecsFormat Format { get; }
    /// <summary>Units</summary>
    public string Units { get; }
    /// <summary>Value at Start</summary>
    public SecsItem InitialValue { get; }
}

/// <summary>
/// Definition of an equipment constant (EC). A setting value that the Host reads with S2F13 and changes with S2F15. The value starts as <see cref="Default"/> at Start.
/// </summary>
/// <remarks>The format is the format of <see cref="Default"/>. <see cref="Min"/>/<see cref="Max"/> can be set only for a numeric format, and if the Host sends a value outside the range it is rejected with EAC 3.</remarks>
public sealed class GemEquipmentConstant
{
    /// <summary>Creates an equipment constant definition.</summary>
    /// <param name="id">ECID (VID space: shared by SV, DV, and EC)</param>
    /// <param name="name">Name (ASCII, ECNAME in S2F29)</param>
    /// <param name="defaultValue">Default value (its format is the format of this constant)</param>
    /// <param name="min">Minimum value (numeric formats only, same format as the default value, null = none)</param>
    /// <param name="max">Maximum value (numeric formats only, same format as the default value, null = none)</param>
    /// <param name="units">Units (ASCII, default: "")</param>
    /// <exception cref="ArgumentNullException">name, defaultValue, or units is null</exception>
    /// <exception cref="ArgumentException">The name or units are not ASCII, or Min/Max is not a numeric format, has a format different from the default value, or does not hold exactly one value</exception>
    /// <exception cref="ArgumentOutOfRangeException">Min is greater than Max, or the default value is outside the range</exception>
    public GemEquipmentConstant(GemId id, string name, SecsItem defaultValue, SecsItem? min = null, SecsItem? max = null, string units = "")
    {
        ArgumentNullException.ThrowIfNull(defaultValue);
        Id = id;
        Name = GemDefinitionCheck.Ascii(name, nameof(name));
        Default = defaultValue;
        Units = GemDefinitionCheck.Ascii(units, nameof(units));
        foreach (var (v, p) in new[] { (min, nameof(min)), (max, nameof(max)) })
        {
            if (v == null) continue;
            if (!GemItems.IsNumeric(defaultValue.Format)) throw new ArgumentException("Min/Max는 숫자 포맷 상수에만 둘 수 있습니다.", p);
            if (v.Format != defaultValue.Format || v.Count != 1) throw new ArgumentException($"Min/Max는 기본값과 같은 포맷({defaultValue.Format})의 값 하나여야 합니다.", p);
        }
        if (min != null && max != null && !GemItems.InRange(min, null, max)) throw new ArgumentOutOfRangeException(nameof(min), "Min이 Max보다 큽니다.");
        if (GemItems.IsNumeric(defaultValue.Format) && !GemItems.InRange(defaultValue, min, max))
            throw new ArgumentOutOfRangeException(nameof(defaultValue), "기본값이 Min~Max 범위 밖입니다.");
        Min = min;
        Max = max;
    }

    /// <summary>ECID</summary>
    public GemId Id { get; }
    /// <summary>Name (ECNAME)</summary>
    public string Name { get; }
    /// <summary>Default value (ECDEF). Its format is the format of this constant.</summary>
    public SecsItem Default { get; }
    /// <summary>Minimum value (ECMIN), or null if none</summary>
    public SecsItem? Min { get; }
    /// <summary>Maximum value (ECMAX), or null if none</summary>
    public SecsItem? Max { get; }
    /// <summary>Units</summary>
    public string Units { get; }
    /// <summary>Value format (= the format of <see cref="Default"/>)</summary>
    public SecsFormat Format => Default.Format;
}

/// <summary>
/// Definition of a collection event (CE). When something happens in the equipment, it is reported with <c>GemEquipment.TriggerEvent</c>, and if the Host has enabled it, it is sent with S6F11.
/// </summary>
/// <remarks><see cref="DataVariables"/> is a list for documentation, used in the S1F23 reply. The values carried in an actual report are decided by the Host with S2F33/S2F35.</remarks>
public sealed class GemCollectionEvent
{
    /// <summary>Creates a collection event definition.</summary>
    /// <param name="id">CEID</param>
    /// <param name="name">Name (ASCII, CENAME in S1F23)</param>
    /// <param name="dataVariables">List of VIDs related to this event (null = empty list)</param>
    /// <exception cref="ArgumentNullException">name is null</exception>
    /// <exception cref="ArgumentException">The name is not ASCII</exception>
    public GemCollectionEvent(GemId id, string name, IEnumerable<GemId>? dataVariables = null)
    {
        Id = id;
        Name = GemDefinitionCheck.Ascii(name, nameof(name));
        DataVariables = [.. dataVariables ?? []];
    }

    /// <summary>CEID</summary>
    public GemId Id { get; }
    /// <summary>Name (CENAME)</summary>
    public string Name { get; }
    /// <summary>List of related VIDs (S1F23)</summary>
    public IReadOnlyList<GemId> DataVariables { get; }
}

/// <summary>
/// Definition of an alarm. Reporting it with <c>GemEquipment.SetAlarm</c>/<c>ClearAlarm</c> sends S5F1 and the alarm events (Set/Clear CE).
/// </summary>
/// <remarks>The two CEs (<see cref="SetCeid"/>, <see cref="ClearCeid"/>) are defined automatically by GEM (named "Alarm {Id} Set/Clear", related DV = AlarmID). If the application defines the same CEID separately, Start fails with an error.</remarks>
public sealed class GemAlarm
{
    /// <summary>Creates an alarm definition.</summary>
    /// <param name="id">ALID</param>
    /// <param name="text">Alarm text (ASCII, 1 to 120 characters, ALTX)</param>
    /// <param name="category">Category 1 to 8 (lower bits of ALCD: 1 personal safety, 2 equipment safety, 3 parameter control warning, 4 parameter control error, 5 irrecoverable error, 6 equipment status warning, 7 attention flag, 8 data integrity)</param>
    /// <param name="setCeid">CEID sent when the alarm occurs</param>
    /// <param name="clearCeid">CEID sent when the alarm is cleared</param>
    /// <exception cref="ArgumentNullException">text is null</exception>
    /// <exception cref="ArgumentException">text is not 1 to 120 ASCII characters, or the two CEIDs are the same</exception>
    /// <exception cref="ArgumentOutOfRangeException">category is outside 1 to 8</exception>
    public GemAlarm(GemId id, string text, int category, GemId setCeid, GemId clearCeid)
    {
        GemDefinitionCheck.Ascii(text, nameof(text));
        if (text.Length is < 1 or > 120) throw new ArgumentException("알람 문장은 1~120자입니다.", nameof(text));
        if (category is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(category), category, "알람 분류는 1~8 입니다.");
        if (setCeid == clearCeid) throw new ArgumentException("SetCeid와 ClearCeid는 달라야 합니다.", nameof(clearCeid));
        Id = id;
        Text = text;
        Category = (byte)category;
        SetCeid = setCeid;
        ClearCeid = clearCeid;
    }

    /// <summary>ALID</summary>
    public GemId Id { get; }
    /// <summary>Alarm text (ALTX)</summary>
    public string Text { get; }
    /// <summary>Category 1 to 8 (lower bits of ALCD)</summary>
    public byte Category { get; }
    /// <summary>CEID for the alarm occurring</summary>
    public GemId SetCeid { get; }
    /// <summary>CEID for the alarm being cleared</summary>
    public GemId ClearCeid { get; }
}

/// <summary>
/// Definition of a remote command (RCMD of S2F41).
/// </summary>
/// <remarks>A command sent by the Host is passed to the application through <c>GemEquipment.RemoteCommandReceived</c> after its name and parameters are checked.</remarks>
public sealed class GemRemoteCommand
{
    /// <summary>Creates a remote command definition.</summary>
    /// <param name="name">Command name (ASCII, 1 to 40 characters, case-sensitive)</param>
    /// <param name="parameters">Parameters it can accept (null = none)</param>
    /// <exception cref="ArgumentNullException">name is null</exception>
    /// <exception cref="ArgumentException">The name is not 1 to 40 ASCII characters, or parameter names overlap</exception>
    public GemRemoteCommand(string name, IEnumerable<GemCommandParameter>? parameters = null)
    {
        GemDefinitionCheck.Ascii(name, nameof(name));
        if (name.Length is < 1 or > 40) throw new ArgumentException("명령 이름은 1~40자입니다.", nameof(name));
        Name = name;
        Parameters = [.. parameters ?? []];
        if (Parameters.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != Parameters.Count)
            throw new ArgumentException("파라미터 이름이 겹칩니다.", nameof(parameters));
    }

    /// <summary>Command name (RCMD)</summary>
    public string Name { get; }
    /// <summary>Parameters it can accept</summary>
    public IReadOnlyList<GemCommandParameter> Parameters { get; }
}

/// <summary>
/// Definition of a remote command parameter (CPNAME).
/// </summary>
public sealed class GemCommandParameter
{
    /// <summary>Creates a parameter definition.</summary>
    /// <param name="name">Parameter name (ASCII, at least 1 character, case-sensitive)</param>
    /// <param name="format">Value format (null = any format). A different format gives CPACK 3</param>
    /// <exception cref="ArgumentNullException">name is null</exception>
    /// <exception cref="ArgumentException">The name is empty or not ASCII</exception>
    public GemCommandParameter(string name, SecsFormat? format = null)
    {
        GemDefinitionCheck.Ascii(name, nameof(name));
        if (name.Length == 0) throw new ArgumentException("파라미터 이름은 비어 있을 수 없습니다.", nameof(name));
        Name = name;
        Format = format;
    }

    /// <summary>Parameter name (CPNAME)</summary>
    public string Name { get; }
    /// <summary>Value format, or any format if null</summary>
    public SecsFormat? Format { get; }
}

// 정의 생성자 공용 검사
internal static class GemDefinitionCheck
{
    // A 아이템으로 보낼 수 있는 문자열(Latin-1)인지
    public static string Ascii(string value, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        if (value.Any(c => c > 0xFF)) throw new ArgumentException("ASCII(Latin-1) 문자만 쓸 수 있습니다(A 아이템).", paramName);
        return value;
    }

    // 값의 포맷이 정의 포맷과 같은지(null이면 길이 0 아이템)
    public static SecsItem Value(SecsItem? value, SecsFormat format, string paramName)
    {
        if (value == null) return GemItems.Empty(format);
        if (value.Format != format) throw new ArgumentException($"값의 포맷({value.Format})이 정의 포맷({format})과 다릅니다.", paramName);
        return value;
    }
}
