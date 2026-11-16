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
