using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

/// <summary>The name information of a single variable (SV or DV) (an S1F12 or S1F22 entry).</summary>
/// <param name="id">The VID (read in the format the equipment sent).</param>
/// <param name="name">The name ("" if the equipment does not know the VID).</param>
/// <param name="units">The units ("" if the equipment does not know the VID).</param>
public sealed class GemVariableInfo(GemId id, string name, string units)
{
    /// <summary>Gets the VID.</summary>
    public GemId Id { get; } = id;
    /// <summary>Gets the name ("" if the equipment does not know the VID).</summary>
    public string Name { get; } = name;
    /// <summary>Gets the units ("" if the equipment does not know the VID).</summary>
    public string Units { get; } = units;
}

/// <summary>The information of a single collection event (CE) (an S1F24 entry).</summary>
/// <param name="ceid">CEID</param>
/// <param name="name">The name ("" if the equipment does not know the CEID).</param>
/// <param name="vids">The VIDs related to this event (informational; the values carried in a report are decided by the Host).</param>
public sealed class GemEventInfo(GemId ceid, string name, IReadOnlyList<GemId> vids)
{
    /// <summary>Gets the CEID.</summary>
    public GemId Ceid { get; } = ceid;
    /// <summary>Gets the name ("" if the equipment does not know the CEID).</summary>
    public string Name { get; } = name;
    /// <summary>Gets the list of related VIDs.</summary>
    public IReadOnlyList<GemId> Vids { get; } = vids;
}

/// <summary>The information of a single equipment constant (EC) (an S2F30 entry).</summary>
/// <param name="id">ECID</param>
/// <param name="name">The name ("" if the equipment does not know the ECID).</param>
/// <param name="min">The minimum value (a zero-length item if none).</param>
/// <param name="max">The maximum value (a zero-length item if none).</param>
/// <param name="defaultValue">The default value.</param>
/// <param name="units">The units.</param>
public sealed class GemConstantInfo(GemId id, string name, SecsItem min, SecsItem max, SecsItem defaultValue, string units)
{
    /// <summary>Gets the ECID.</summary>
    public GemId Id { get; } = id;
    /// <summary>Gets the name ("" if the equipment does not know the ECID).</summary>
    public string Name { get; } = name;
    /// <summary>Gets the minimum value (a zero-length item if none).</summary>
    public SecsItem Min { get; } = min;
    /// <summary>Gets the maximum value (a zero-length item if none).</summary>
    public SecsItem Max { get; } = max;
    /// <summary>Gets the default value.</summary>
    public SecsItem Default { get; } = defaultValue;
    /// <summary>Gets the units.</summary>
    public string Units { get; } = units;
}

/// <summary>A single value inside an event report.</summary>
/// <param name="vid">The VID of this value (the VID if the report was defined by the Host; null if the report is unknown or the number of values differs from the definition).</param>
/// <param name="value">The value.</param>
public sealed class GemReportValue(GemId? vid, SecsItem value)
{
    /// <summary>Gets the VID of this value (null if unknown).</summary>
    public GemId? Vid { get; } = vid;
    /// <summary>Gets the value.</summary>
    public SecsItem Value { get; } = value;
}

/// <summary>A single report inside an event report.</summary>
/// <param name="rptid">RPTID</param>
/// <param name="values">The list of values (in the VID order of the report definition).</param>
public sealed class GemReportData(GemId rptid, IReadOnlyList<GemReportValue> values)
{
    /// <summary>Gets the RPTID.</summary>
    public GemId Rptid { get; } = rptid;
    /// <summary>Gets the list of values (in the VID order of the report definition).</summary>
    public IReadOnlyList<GemReportValue> Values { get; } = values;
}

/// <summary>An interpreted event report (S6F11, S6F16).</summary>
/// <param name="dataId">The DATAID (the item exactly as the equipment sent it).</param>
/// <param name="ceid">CEID</param>
/// <param name="reports">The list of reports (in the order the equipment sent them).</param>
public sealed class GemEventReport(SecsItem dataId, GemId ceid, IReadOnlyList<GemReportData> reports)
{
    /// <summary>Gets the DATAID (the item exactly as the equipment sent it; the format differs between equipment).</summary>
    public SecsItem DataId { get; } = dataId;
    /// <summary>Gets the CEID (read in the format the equipment sent).</summary>
    /// <remarks>If the equipment sent it as A (usually when <c>CeidFormat</c> is A), it is a text ID even if it looks like a number. For example, <c>Ceid == new GemId("5001")</c> is true and <c>Ceid == 5001</c> is false.</remarks>
    public GemId Ceid { get; } = ceid;
    /// <summary>Gets the list of reports (in the order the equipment sent them).</summary>
    public IReadOnlyList<GemReportData> Reports { get; } = reports;

    /// <summary>Finds a value by VID (the first one if it appears in several reports).</summary>
    /// <param name="vid">VID</param>
    /// <returns>The value, or null if there is none (or the report does not know its VIDs).</returns>
    public SecsItem? GetValue(GemId vid)
    {
        foreach (var r in Reports)
            foreach (var v in r.Values)
                if (v.Vid == vid) return v.Value;
        return null;
    }
}

/// <summary>The information of a single alarm (an S5F6 or S5F8 entry).</summary>
/// <param name="alid">ALID</param>
/// <param name="alcd">The ALCD (0x80 = currently set, lower 7 bits = category, 0 if the equipment does not know the ALID).</param>
/// <param name="text">The alarm text ("" if the equipment does not know the ALID).</param>
public sealed class GemAlarmInfo(GemId alid, byte alcd, string text)
{
    /// <summary>ALID</summary>
    public GemId Alid { get; } = alid;
    /// <summary>Gets the ALCD (0x80 = currently set, lower 7 bits = category, 0 if the equipment does not know the ALID).</summary>
    public byte Alcd { get; } = alcd;
    /// <summary>Gets a value indicating whether the alarm is currently set.</summary>
    public bool IsSet => (Alcd & GemAck.AlcdSet) != 0;
    /// <summary>Gets the alarm category (the lower 7 bits of ALCD).</summary>
    public int Category => Alcd & 0x7F;
    /// <summary>Gets the alarm text ("" if the equipment does not know the ALID).</summary>
    public string Text { get; } = text;
}

/// <summary>The result of a remote command (S2F41). A nonzero HCACK is also a value, not an exception.</summary>
/// <param name="hcack">HCACK</param>
/// <param name="parameterAcks">The list of parameter errors (CPNAME, CPACK) (copied and kept read-only).</param>
public sealed class GemCommandResult(byte hcack, IReadOnlyList<(string Name, byte Cpack)> parameterAcks)
{
    /// <summary>Gets the HCACK (0 = executed, 1 = unknown command, 2 = cannot be done now, 3 = parameter error, 4 = accepted, will finish later).</summary>
    public byte Hcack { get; } = hcack;
    /// <summary>Gets the list of parameter errors (CPNAME, CPACK) (CPACK 1 = unknown name, 2 = invalid value, 3 = invalid format).</summary>
    public IReadOnlyList<(string Name, byte Cpack)> ParameterAcks { get; } = Array.AsReadOnly(parameterAcks.ToArray());
    /// <summary>Gets a value indicating whether the equipment accepted the command (HCACK 0 or 4).</summary>
    public bool IsAccepted => Hcack is GemAck.HcackAccepted or GemAck.HcackAcceptedLater;
}

/// <summary>An unformatted process program (recipe): a PPID and a body that is a single item (not a list).</summary>
/// <remarks>The body keeps its SECS-II format (B, A, an integer array and so on), so it is sent back exactly as it was received.</remarks>
public sealed class GemProcessProgram
{
    /// <summary>The maximum length of a PPID (120 characters, SEMI E5).</summary>
    public const int MaxPpidLength = 120;

    /// <summary>Creates a process program.</summary>
    /// <param name="ppid">PPID, 1 to 120 characters that an A item can carry.</param>
    /// <param name="body">PPBODY, a single item that is not a list.</param>
    /// <exception cref="ArgumentNullException"><paramref name="ppid"/> or <paramref name="body"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="ppid"/> is empty, longer than 120 characters, or has a character an A item cannot carry; or <paramref name="body"/> is a list.</exception>
    public GemProcessProgram(string ppid, SecsItem body)
    {
        Ppid = CheckPpid(ppid, nameof(ppid));
        ArgumentNullException.ThrowIfNull(body);
        if (body.Format == SecsFormat.List) throw new ArgumentException("PPBODY는 리스트가 아닌 아이템 하나입니다.", nameof(body));
        Body = body;
    }

    /// <summary>Gets the PPID.</summary>
    public string Ppid { get; }
    /// <summary>Gets the PPBODY.</summary>
    public SecsItem Body { get; }

    // PPID 규칙(1~120자, A로 보낼 수 있는 문자). 빌더도 같은 규칙
    internal static string CheckPpid(string ppid, string paramName)
    {
        GemDefinitionCheck.Ascii(ppid, paramName);
        if (ppid.Length is 0 or > MaxPpidLength) throw new ArgumentException($"PPID는 1~{MaxPpidLength}자입니다. 현재: {ppid.Length}자", paramName);
        return ppid;
    }

    // PPBODY 데이터 바이트 수(S7F1 LENGTH): 인코딩에서 포맷 바이트와 길이 바이트를 뺀 것
    internal static long DataLength(SecsItem body)
    {
        var enc = SecsCodec.Encode(body);
        return enc.Length - 1 - (enc[0] & 0x03);
    }

    // 받은 PPID가 규칙에 맞는지(예외 없이)
    internal static bool IsValidPpid(string ppid) => ppid.Length is > 0 and <= MaxPpidLength && ppid.All(c => c <= 0xFF);
}

/// <summary>The result of an equipment-initiated process program upload (S7F1 then S7F3). A refusal by the Host is a value, not an exception.</summary>
public sealed class GemUploadResult
{
    internal GemUploadResult(GemUploadFailure failure, byte? ppgnt = null, byte? ackc7 = null, Exception? exception = null)
    {
        Failure = failure;
        Ppgnt = ppgnt;
        Ackc7 = ackc7;
        Exception = exception;
    }

    /// <summary>Gets the PPGNT of S7F2 (null if no S7F1 was sent or no reply came).</summary>
    public byte? Ppgnt { get; }
    /// <summary>Gets the ACKC7 of S7F4 (null if no S7F3 was sent or no reply came).</summary>
    public byte? Ackc7 { get; }
    /// <summary>Gets why the upload did not succeed (<see cref="GemUploadFailure.None"/> if the Host answered both steps).</summary>
    public GemUploadFailure Failure { get; }
    /// <summary>Gets the transport exception for Timeout, Aborted, Error, Disconnected, and InvalidReply (otherwise null).</summary>
    public Exception? Exception { get; }
    /// <summary>Gets a value indicating whether the Host accepted the process program (ACKC7 0).</summary>
    public bool IsSuccess => Failure == GemUploadFailure.None && Ackc7 == GemAck.Ackc7Accepted;
}

/// <summary>Thrown by <c>GemEquipment.RequestProcessProgramAsync</c> when the request could not be made or answered (a denial by the Host is a null result, not this exception).</summary>
public sealed class GemProcessProgramRequestException : Exception
{
    internal GemProcessProgramRequestException(GemUploadFailure failure, Exception? inner = null)
        : base($"레시피 요청(S7F5)을 하지 못했습니다: {failure}", inner) => Failure = failure;

    /// <summary>Gets why the request failed.</summary>
    public GemUploadFailure Failure { get; }
}

/// <summary>The result of a host-initiated process program download (S7F1 then S7F3). A refusal by the equipment is a value, not an exception.</summary>
public sealed class GemDownloadResult
{
    internal GemDownloadResult(byte? ppgnt, byte? ackc7)
    {
        Ppgnt = ppgnt;
        Ackc7 = ackc7;
    }

    /// <summary>Gets the PPGNT of S7F2 (null if no S7F1 was sent).</summary>
    public byte? Ppgnt { get; }
    /// <summary>Gets the ACKC7 of S7F4 (null if the inquire was refused and no S7F3 was sent).</summary>
    public byte? Ackc7 { get; }
    /// <summary>Gets a value indicating whether the equipment accepted the process program (ACKC7 0).</summary>
    public bool IsSuccess => Ackc7 == GemAck.Ackc7Accepted;
}

/// <summary>The zone of a value against one limit (SEMI E30 limits monitoring).</summary>
public enum GemLimitZone
{
    /// <summary>Not in a zone yet: the value has stayed in the deadband (or is not a single number) since the limit was defined or restored.</summary>
    NoZone,
    /// <summary>The value reached LOWERDB or went below it (lower zone).</summary>
    Below,
    /// <summary>The value reached UPPERDB or went above it (upper zone).</summary>
    Above,
}

/// <summary>One limit of a variable: LIMITID and its deadband edges.</summary>
/// <param name="limitId">LIMITID</param>
/// <param name="upper">UPPERDB</param>
/// <param name="lower">LOWERDB</param>
public sealed class GemLimit(byte limitId, SecsItem upper, SecsItem lower)
{
    /// <summary>Gets the limit ID (LIMITID).</summary>
    public byte LimitId { get; } = limitId;
    /// <summary>Gets the upper edge of the deadband (UPPERDB). A value at or above it is in the upper zone.</summary>
    public SecsItem Upper { get; } = upper;
    /// <summary>Gets the lower edge of the deadband (LOWERDB). A value at or below it is in the lower zone.</summary>
    public SecsItem Lower { get; } = lower;
}

/// <summary>The limit attributes of one variable read from S2F48.</summary>
/// <param name="vid">VID</param>
/// <param name="units">UNITS (null when the variable cannot have limits)</param>
/// <param name="limitMin">LIMITMIN (null when the variable cannot have limits)</param>
/// <param name="limitMax">LIMITMAX (null when the variable cannot have limits)</param>
/// <param name="limits">The limits now defined (copied and kept read-only)</param>
public sealed class GemLimitInfo(GemId vid, string? units, SecsItem? limitMin, SecsItem? limitMax, IReadOnlyList<GemLimit> limits)
{
    /// <summary>Gets the VID.</summary>
    public GemId Vid { get; } = vid;
    /// <summary>Gets the units, or null when the variable cannot have limits.</summary>
    public string? Units { get; } = units;
    /// <summary>Gets the smallest value a limit may use (LIMITMIN), or null when the variable cannot have limits.</summary>
    public SecsItem? LimitMin { get; } = limitMin;
    /// <summary>Gets the largest value a limit may use (LIMITMAX), or null when the variable cannot have limits.</summary>
    public SecsItem? LimitMax { get; } = limitMax;
    /// <summary>Gets the limits now defined, in LIMITID order as sent.</summary>
    public IReadOnlyList<GemLimit> Limits { get; } = Array.AsReadOnly(limits.ToArray());
    /// <summary>Gets a value indicating whether the variable can have limits (the equipment did not answer &lt;L&gt;).</summary>
    public bool IsSupported => LimitMin != null;
}

/// <summary>One variable in error in S2F46.</summary>
/// <param name="vid">VID</param>
/// <param name="lvack">LVACK (1 unknown variable, 2 no limits capability, 3 variable repeated, 4 limit value error)</param>
/// <param name="limitId">The LIMITID in error, or null</param>
/// <param name="limitack">LIMITACK (1 to 7), or null</param>
public sealed class GemLimitError(GemId vid, byte lvack, byte? limitId, byte? limitack)
{
    /// <summary>Gets the VID.</summary>
    public GemId Vid { get; } = vid;
    /// <summary>Gets LVACK.</summary>
    public byte Lvack { get; } = lvack;
    /// <summary>Gets the LIMITID in error, or null.</summary>
    public byte? LimitId { get; } = limitId;
    /// <summary>Gets LIMITACK (1 LIMITID does not exist, 2 UPPERDB above LIMITMAX, 3 LOWERDB below LIMITMIN, 4 UPPERDB below LOWERDB, 5 illegal format, 6 ASCII not numeric, 7 duplicate LIMITID), or null.</summary>
    public byte? Limitack { get; } = limitack;
}

/// <summary>A trace request read from S2F23 (Trace Initialize Send).</summary>
/// <param name="trid">TRID</param>
/// <param name="period">DSPER as received (<c>hhmmss</c> or <c>hhmmsscc</c>, not checked)</param>
/// <param name="totalSamples">TOTSMP (0 = stop the trace)</param>
/// <param name="groupSize">REPGSZ</param>
/// <param name="svids">The SVIDs (copied and kept read-only)</param>
public sealed class GemTraceRequest(GemId trid, string period, ulong totalSamples, ulong groupSize, IReadOnlyList<GemId> svids)
{
    /// <summary>Gets the trace ID (TRID).</summary>
    public GemId Trid { get; } = trid;
    /// <summary>Gets the sampling period text (DSPER) as received.</summary>
    public string Period { get; } = period;
    /// <summary>Gets the total number of samples (TOTSMP). 0 asks to stop the trace.</summary>
    public ulong TotalSamples { get; } = totalSamples;
    /// <summary>Gets the number of samples per report (REPGSZ).</summary>
    public ulong GroupSize { get; } = groupSize;
    /// <summary>Gets the SVIDs to sample.</summary>
    public IReadOnlyList<GemId> Svids { get; } = Array.AsReadOnly(svids.ToArray());
}

/// <summary>Trace data read from S6F1 (Trace Data Send).</summary>
/// <param name="trid">TRID</param>
/// <param name="sampleNumber">SMPLN</param>
/// <param name="time">STIME as received</param>
/// <param name="values">The values (copied and kept read-only)</param>
public sealed class GemTraceData(GemId trid, ulong sampleNumber, string time, IReadOnlyList<SecsItem> values)
{
    /// <summary>Gets the trace ID (TRID).</summary>
    public GemId Trid { get; } = trid;
    /// <summary>Gets the number of the last sample in this report (SMPLN, from 1).</summary>
    public ulong SampleNumber { get; } = sampleNumber;
    /// <summary>Gets the time of the first sample in this report (STIME) as GEM TIME text, as received.</summary>
    public string Time { get; } = time;
    /// <summary>Gets the values, sample after sample in SVID order.</summary>
    public IReadOnlyList<SecsItem> Values { get; } = Array.AsReadOnly(values.ToArray());
}

/// <summary>The result of an enhanced remote command (S2F49). A nonzero HCACK is also a value, not an exception.</summary>
/// <param name="hcack">HCACK</param>
/// <param name="parameterAcks">The list of parameter errors (CPNAME, CEPACK item) (copied and kept read-only).</param>
public sealed class GemEnhancedCommandResult(byte hcack, IReadOnlyList<(string Name, SecsItem Cepack)> parameterAcks)
{
    /// <summary>Gets the HCACK (0 = executed, 1 = unknown command, 2 = cannot be done now, 3 = parameter error, 4 = accepted, will finish later, 6 = no such object).</summary>
    public byte Hcack { get; } = hcack;
    /// <summary>Gets the list of parameter errors (CPNAME, CEPACK). A CEPACK is a number (1 = unknown name, 2 = invalid value, 3 = invalid format, 4 = not valid as used) or a list shaped like the CEPVAL list.</summary>
    public IReadOnlyList<(string Name, SecsItem Cepack)> ParameterAcks { get; } = Array.AsReadOnly(parameterAcks.ToArray());
    /// <summary>Gets a value indicating whether the equipment accepted the command (HCACK 0 or 4).</summary>
    public bool IsAccepted => Hcack is GemAck.HcackAccepted or GemAck.HcackAcceptedLater;
}
