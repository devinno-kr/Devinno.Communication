using System.Xml.Serialization;
using Devinno.Communication.Internal;
using Devinno.Communication.Secs.Hsms;
using Devinno.Communication.Secs.Serial;

namespace Devinno.Communication.Secs.Gem;

/// <summary>
/// <see cref="GemEquipment"/> configuration. In XML it is the &lt;GemEquipment&gt; element, which holds the transport settings (&lt;SecsHsms&gt; or &lt;SecsSerial&gt;) and the equipment definitions as child elements.
/// </summary>
/// <remarks>
/// <para>ID format attributes (VidFormat and so on) are U1 U2 U4 U8 I1 I2 I4 I8 A, and value format attributes (Format) are the SML names L B BOOLEAN A J C2 I1 I2 I4 I8 U1 U2 U4 U8 F4 F8.
/// ID text is a decimal integer if its format is numeric, and is used as is for A. Value text (Default, Min, Max, InitialValue) is one decimal number for numeric formats, as is for A, true/false for BOOLEAN, and a list of 0x bytes for B, J, and C2 (use code for array and list values).</para>
/// <para>Ranges: CommandReplyTimeout 1000 to 110000, MaxPendingSends 1 to 100000, MaxPendingEvents 1 to 1000000, ModelName and SoftwareRevision 0 to 20 characters, alarm Category 1 to 8 and Text 1 to 120 characters. The definition checks (ID overlap and so on) are the same as at Start and are also performed when loading.</para>
/// <para>Not saved: the current values of SV, DV, and EC (for EC only Default), the reports, links, and enable flags set by the Host, the alarm state, and the clock offset.</para>
/// </remarks>
/// <example>
/// <code>
/// &lt;GemEquipment ModelName="DV-100" SoftwareRevision="1.0.0" InitialControlState="HostOffline"&gt;
///   &lt;SecsHsms DeviceID="1" Mode="Passive" LocalPort="5000" /&gt;
///   &lt;SV Id="2001" Name="Temperature" Format="F4" Units="C" /&gt;
///   &lt;EC Id="4001" Name="MaxTemp" Format="F4" Default="80" Min="0" Max="200" Units="C" /&gt;
///   &lt;CE Id="5001" Name="ProcessStarted" Vids="2001" /&gt;
///   &lt;Alarm Id="1" Text="Over temperature" Category="2" SetCeid="9001" ClearCeid="9002" /&gt;
///   &lt;Command Name="START"&gt;&lt;Param Name="LOTID" Format="A" /&gt;&lt;/Command&gt;
/// &lt;/GemEquipment&gt;
/// </code>
/// </example>
[XmlRoot(nameof(GemEquipment))]
public class GemEquipmentConfig
{
    /// <summary>MDLN (0 to 20 characters, default: "").</summary>
    [XmlAttribute] public string ModelName { get; set; } = "";
    /// <summary>SOFTREV (0 to 20 characters, default: "").</summary>
    [XmlAttribute] public string SoftwareRevision { get; set; } = "";
    /// <summary>VID format (default: U4).</summary>
    [XmlAttribute] public string VidFormat { get; set; } = "U4";
    /// <summary>CEID format (default: U4).</summary>
    [XmlAttribute] public string CeidFormat { get; set; } = "U4";
    /// <summary>RPTID format (default: U4).</summary>
    [XmlAttribute] public string RptidFormat { get; set; } = "U4";
    /// <summary>ALID format (default: U4).</summary>
    [XmlAttribute] public string AlidFormat { get; set; } = "U4";
    /// <summary>DATAID format (default: U4).</summary>
    [XmlAttribute] public string DataidFormat { get; set; } = "U4";
    /// <summary>Whether communication is enabled at start: Disabled, Enabled (default: Enabled).</summary>
    [XmlAttribute] public GemEnableState InitialCommunicationState { get; set; } = GemEnableState.Enabled;
    /// <summary>Whether the equipment sends S1F13 first (default: true).</summary>
    [XmlAttribute] public bool InitiateCommunication { get; set; } = true;
    /// <summary>Control state at start: EquipmentOffline, HostOffline, OnlineLocal, OnlineRemote (default: HostOffline).</summary>
    [XmlAttribute] public GemControlState InitialControlState { get; set; } = GemControlState.HostOffline;
    /// <summary>State to go to when an ON-LINE attempt fails: EquipmentOffline, HostOffline (default: EquipmentOffline).</summary>
    [XmlAttribute] public GemControlState OnlineFailedState { get; set; } = GemControlState.EquipmentOffline;
    /// <summary>Enable all CEs at Start (default: false).</summary>
    [XmlAttribute] public bool EventsEnabledByDefault { get; set; }
    /// <summary>Turn on all alarm reporting at Start (default: false).</summary>
    [XmlAttribute] public bool AlarmsEnabledByDefault { get; set; }
    /// <summary>W-bit of S6F11 (default: true).</summary>
    [XmlAttribute] public bool EventReplyExpected { get; set; } = true;
    /// <summary>W-bit of S5F1 (default: true).</summary>
    [XmlAttribute] public bool AlarmReplyExpected { get; set; } = true;
    /// <summary>W-bit of S10F1 (default: true).</summary>
    [XmlAttribute] public bool TerminalReplyExpected { get; set; } = true;
    /// <summary>Deadline in ms for replies the application decides, 1000 to 110000 (default: 10000).</summary>
    [XmlAttribute] public int CommandReplyTimeout { get; set; } = 10000;
    /// <summary>Upper limit of the send queue, 1 to 100000 (default: 1000).</summary>
    [XmlAttribute] public int MaxPendingSends { get; set; } = 1000;
    /// <summary>Upper limit of the application event queue, 1 to 1000000 (default: 10000). When exceeded, the oldest events are discarded.</summary>
    [XmlAttribute] public int MaxPendingEvents { get; set; } = 10000;

    /// <summary>HSMS transport settings (&lt;SecsHsms&gt;; if omitted the transport settings are not changed). Only Equipment is allowed for Role.</summary>
    [XmlElement("SecsHsms")] public SecsHsmsConfig? Hsms { get; set; }
    /// <summary>SECS-I transport settings (&lt;SecsSerial&gt;; if omitted the transport settings are not changed). Only Equipment is allowed for Role.</summary>
    [XmlElement("SecsSerial")] public SecsSerialConfig? Serial { get; set; }
    /// <summary>IDs of the standard variables and events (&lt;Standard&gt;; omitted = default IDs).</summary>
    [XmlElement("Standard")] public GemStandardIdsConfig? Standard { get; set; }
    /// <summary>Status variables (&lt;SV&gt;).</summary>
    [XmlElement("SV")] public List<GemStatusVariableConfig> StatusVariables { get; set; } = [];
    /// <summary>Data variables (&lt;DV&gt;).</summary>
    [XmlElement("DV")] public List<GemDataVariableConfig> DataVariables { get; set; } = [];
    /// <summary>Equipment constants (&lt;EC&gt;).</summary>
    [XmlElement("EC")] public List<GemEquipmentConstantConfig> EquipmentConstants { get; set; } = [];
    /// <summary>Collection events (&lt;CE&gt;).</summary>
    [XmlElement("CE")] public List<GemCollectionEventConfig> CollectionEvents { get; set; } = [];
    /// <summary>Alarms (&lt;Alarm&gt;).</summary>
    [XmlElement("Alarm")] public List<GemAlarmConfig> Alarms { get; set; } = [];
    /// <summary>Remote commands (&lt;Command&gt;).</summary>
    [XmlElement("Command")] public List<GemRemoteCommandConfig> RemoteCommands { get; set; } = [];
    /// <summary>Registered messages (&lt;Message&gt;).</summary>
    [XmlElement("Message")] public List<GemMessageConfig> Messages { get; set; } = [];
}

/// <summary>&lt;Standard&gt;: IDs of the standard variables and events. An omitted attribute uses the default ID (<see cref="GemStandardIds"/>).</summary>
public class GemStandardIdsConfig
{
    /// <summary>SV Clock</summary>
    [XmlAttribute] public string? Clock { get; set; }
    /// <summary>SV ControlState</summary>
    [XmlAttribute] public string? ControlState { get; set; }
    /// <summary>SV PreviousControlState</summary>
    [XmlAttribute] public string? PreviousControlState { get; set; }
    /// <summary>SV EventsEnabled</summary>
    [XmlAttribute] public string? EventsEnabled { get; set; }
    /// <summary>SV AlarmsEnabled</summary>
    [XmlAttribute] public string? AlarmsEnabled { get; set; }
    /// <summary>SV AlarmsSet</summary>
    [XmlAttribute] public string? AlarmsSet { get; set; }
    /// <summary>SV ProcessState</summary>
    [XmlAttribute] public string? ProcessState { get; set; }
    /// <summary>SV PreviousProcessState</summary>
    [XmlAttribute] public string? PreviousProcessState { get; set; }
    /// <summary>EC EstablishCommunicationsTimeout</summary>
    [XmlAttribute] public string? EstablishCommunicationsTimeout { get; set; }
    /// <summary>EC TimeFormat</summary>
    [XmlAttribute] public string? TimeFormat { get; set; }
    /// <summary>DV AlarmID</summary>
    [XmlAttribute] public string? AlarmID { get; set; }
    /// <summary>CE EquipmentOffline</summary>
    [XmlAttribute] public string? EquipmentOffline { get; set; }
    /// <summary>CE ControlStateLocal</summary>
    [XmlAttribute] public string? ControlStateLocal { get; set; }
    /// <summary>CE ControlStateRemote</summary>
    [XmlAttribute] public string? ControlStateRemote { get; set; }
    /// <summary>CE ProcessStateChange</summary>
    [XmlAttribute] public string? ProcessStateChange { get; set; }
    /// <summary>CE OperatorEquipmentConstantChange</summary>
    [XmlAttribute] public string? OperatorEquipmentConstantChange { get; set; }
    /// <summary>CE MessageRecognition</summary>
    [XmlAttribute] public string? MessageRecognition { get; set; }

    internal string? Get(GemStandard s) => (string?)GetType().GetProperty(s.ToString())!.GetValue(this);
    internal void Set(GemStandard s, string? value) => GetType().GetProperty(s.ToString())!.SetValue(this, value);
}

/// <summary>&lt;SV&gt;: status variable definition (required: Id, Name, Format).</summary>
public class GemStatusVariableConfig
{
    /// <summary>SVID</summary>
    [XmlAttribute, ConfigRequired] public string Id { get; set; } = "";
    /// <summary>Name.</summary>
    [XmlAttribute, ConfigRequired] public string Name { get; set; } = "";
    /// <summary>Value format (SML name).</summary>
    [XmlAttribute, ConfigRequired] public string Format { get; set; } = "";
    /// <summary>Units (default: "").</summary>
    [XmlAttribute] public string Units { get; set; } = "";
    /// <summary>Initial value (omitted = a zero-length item of that format).</summary>
    [XmlAttribute] public string? InitialValue { get; set; }
}

/// <summary>&lt;DV&gt;: data variable definition (required: Id, Name, Format).</summary>
public class GemDataVariableConfig
{
    /// <summary>DVID</summary>
    [XmlAttribute, ConfigRequired] public string Id { get; set; } = "";
    /// <summary>Name.</summary>
    [XmlAttribute, ConfigRequired] public string Name { get; set; } = "";
    /// <summary>Value format (SML name).</summary>
    [XmlAttribute, ConfigRequired] public string Format { get; set; } = "";
    /// <summary>Units (default: "").</summary>
    [XmlAttribute] public string Units { get; set; } = "";
    /// <summary>Initial value (omitted = a zero-length item of that format).</summary>
    [XmlAttribute] public string? InitialValue { get; set; }
}

/// <summary>&lt;EC&gt;: equipment constant definition (required: Id, Name, Format, Default).</summary>
public class GemEquipmentConstantConfig
{
    /// <summary>ECID</summary>
    [XmlAttribute, ConfigRequired] public string Id { get; set; } = "";
    /// <summary>Name.</summary>
    [XmlAttribute, ConfigRequired] public string Name { get; set; } = "";
    /// <summary>Value format (SML name).</summary>
    [XmlAttribute, ConfigRequired] public string Format { get; set; } = "";
    /// <summary>Default value.</summary>
    [XmlAttribute, ConfigRequired] public string Default { get; set; } = "";
    /// <summary>Minimum value (numeric formats only, omitted = none).</summary>
    [XmlAttribute] public string? Min { get; set; }
    /// <summary>Maximum value (numeric formats only, omitted = none).</summary>
    [XmlAttribute] public string? Max { get; set; }
    /// <summary>Units (default: "").</summary>
    [XmlAttribute] public string Units { get; set; } = "";
}

/// <summary>&lt;CE&gt;: collection event definition (required: Id, Name).</summary>
public class GemCollectionEventConfig
{
    /// <summary>CEID</summary>
    [XmlAttribute, ConfigRequired] public string Id { get; set; } = "";
    /// <summary>Name.</summary>
    [XmlAttribute, ConfigRequired] public string Name { get; set; } = "";
    /// <summary>List of related VIDs (comma-separated, default: "").</summary>
    [XmlAttribute] public string Vids { get; set; } = "";
}

/// <summary>&lt;Alarm&gt;: alarm definition (required: Id, Text, Category, SetCeid, ClearCeid).</summary>
public class GemAlarmConfig
{
    /// <summary>ALID</summary>
    [XmlAttribute, ConfigRequired] public string Id { get; set; } = "";
    /// <summary>Alarm text, 1 to 120 characters.</summary>
    [XmlAttribute, ConfigRequired] public string Text { get; set; } = "";
    /// <summary>Category, 1 to 8.</summary>
    [XmlAttribute, ConfigRequired] public int Category { get; set; }
    /// <summary>CEID for the alarm occurring.</summary>
    [XmlAttribute, ConfigRequired] public string SetCeid { get; set; } = "";
    /// <summary>CEID for the alarm being cleared.</summary>
    [XmlAttribute, ConfigRequired] public string ClearCeid { get; set; } = "";
}

/// <summary>&lt;Command&gt;: remote command definition (required: Name); the parameters are child &lt;Param&gt; elements.</summary>
public class GemRemoteCommandConfig
{
    /// <summary>Command name, 1 to 40 characters.</summary>
    [XmlAttribute, ConfigRequired] public string Name { get; set; } = "";
    /// <summary>Parameters.</summary>
    [XmlElement("Param")] public List<GemCommandParameterConfig> Parameters { get; set; } = [];
}

/// <summary>&lt;Param&gt;: remote command parameter (required: Name).</summary>
public class GemCommandParameterConfig
{
    /// <summary>Parameter name.</summary>
    [XmlAttribute, ConfigRequired] public string Name { get; set; } = "";
    /// <summary>Value format (SML name, omitted = any format).</summary>
    [XmlAttribute] public string? Format { get; set; }
}

/// <summary>&lt;Message&gt;: registered message (required: Stream, Function).</summary>
public class GemMessageConfig
{
    /// <summary>Stream, 1 to 127 (excluding 9).</summary>
    [XmlAttribute, ConfigRequired] public int Stream { get; set; }
    /// <summary>Function (odd).</summary>
    [XmlAttribute, ConfigRequired] public int Function { get; set; }
}
