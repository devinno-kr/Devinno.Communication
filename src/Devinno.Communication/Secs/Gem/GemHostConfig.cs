using System.Xml.Serialization;
using Devinno.Communication.Internal;
using Devinno.Communication.Secs.Hsms;
using Devinno.Communication.Secs.Serial;

namespace Devinno.Communication.Secs.Gem;

/// <summary>
/// The <see cref="GemHost"/> configuration. In XML it is the &lt;GemHost&gt; element, and it holds the transport settings (&lt;SecsHsms&gt; or &lt;SecsSerial&gt;) and the subscriptions (&lt;Subscribe&gt;) as child elements.
/// </summary>
/// <remarks>
/// <para>The ID format attributes (VidFormat and so on) take U1, U2, U4, U8, I1, I2, I4, I8, or A. In &lt;Subscribe&gt;, Ceid and Vids (comma-separated) are decimal integers when the format is numeric and are used as-is when the format is A.</para>
/// <para>Ranges: TimeFormat 0 to 2, EstablishCommunicationRetry 1 to 3600 (seconds), CommandReplyTimeout 1000 to 110000 (ms), MaxPendingEvents 1 to 1000000, ReportIdStart 0 or greater and
/// still within the RptidFormat range after being incremented once per subscription that has VIDs. If the same Ceid appears more than once, the later entry replaces the earlier one.</para>
/// <para>The attributes RequestOnline, SetEquipmentClock, ConfigureReports, and EnableAlarms correspond to ConnectRequestOnline, ConnectSetClock, ConnectConfigureReports, and ConnectEnableAlarms on GemHost (the prefix avoids clashing with method names).</para>
/// <para>Not saved: the equipment name, whether communication is established, and the report interpretation table (the connect sequence rebuilds it).</para>
/// </remarks>
/// <example>
/// <code>
/// &lt;GemHost VidFormat="U4" CeidFormat="U4" RptidFormat="U4" AutoConnectSequence="true" ReportIdStart="1000"&gt;
///   &lt;SecsHsms Mode="Active" RemoteIP="192.168.0.50" RemotePort="5000" DeviceID="1" /&gt;
///   &lt;Subscribe Ceid="5001" Vids="2001,3001" /&gt;
///   &lt;Subscribe Ceid="1302" /&gt;
/// &lt;/GemHost&gt;
/// </code>
/// </example>
[XmlRoot(nameof(GemHost))]
public class GemHostConfig
{
    /// <summary>VID format (default: U4)</summary>
    [XmlAttribute] public string VidFormat { get; set; } = "U4";
    /// <summary>CEID format (default: U4)</summary>
    [XmlAttribute] public string CeidFormat { get; set; } = "U4";
    /// <summary>RPTID format (default: U4)</summary>
    [XmlAttribute] public string RptidFormat { get; set; } = "U4";
    /// <summary>ALID format (default: U4)</summary>
    [XmlAttribute] public string AlidFormat { get; set; } = "U4";
    /// <summary>DATAID format (default: U4)</summary>
    [XmlAttribute] public string DataidFormat { get; set; } = "U4";
    /// <summary>Time format the Host sends, 0 to 2 (default: 1)</summary>
    [XmlAttribute] public int TimeFormat { get; set; } = 1;
    /// <summary>Runs the connect sequence on every connection (default: true)</summary>
    [XmlAttribute] public bool AutoConnectSequence { get; set; } = true;
    /// <summary>Sends the ON-LINE request S1F17 in the connect sequence (default: true)</summary>
    [XmlAttribute] public bool RequestOnline { get; set; } = true;
    /// <summary>Sets the equipment clock (S2F31) in the connect sequence (default: false)</summary>
    [XmlAttribute] public bool SetEquipmentClock { get; set; }
    /// <summary>Configures reports according to the subscriptions in the connect sequence (default: true)</summary>
    [XmlAttribute] public bool ConfigureReports { get; set; } = true;
    /// <summary>Enables reporting of all alarms (S5F3) in the connect sequence (default: true)</summary>
    [XmlAttribute] public bool EnableAlarms { get; set; } = true;
    /// <summary>Deletes all of the equipment reports first when configuring reports (default: true)</summary>
    [XmlAttribute] public bool DeleteReportsFirst { get; set; } = true;
    /// <summary>Starting RPTID number (default: 1000)</summary>
    [XmlAttribute] public long ReportIdStart { get; set; } = 1000;
    /// <summary>Establish-communications retry interval in seconds, 1 to 3600 (default: 10)</summary>
    [XmlAttribute] public int EstablishCommunicationRetry { get; set; } = 10;
    /// <summary>Reply deadline for MessageReceived in ms, 1000 to 110000 (default: 10000)</summary>
    [XmlAttribute] public int CommandReplyTimeout { get; set; } = 10000;
    /// <summary>Upper limit of the app event queue, 1 to 1000000 (default: 10000)</summary>
    [XmlAttribute] public int MaxPendingEvents { get; set; } = 10000;

    /// <summary>HSMS transport settings (&lt;SecsHsms&gt;; if omitted, the transport settings are left unchanged). Role must be Host.</summary>
    [XmlElement("SecsHsms")] public SecsHsmsConfig? Hsms { get; set; }
    /// <summary>SECS-I transport settings (&lt;SecsSerial&gt;; if omitted, the transport settings are left unchanged). Role must be Host.</summary>
    [XmlElement("SecsSerial")] public SecsSerialConfig? Serial { get; set; }
    /// <summary>Subscriptions (&lt;Subscribe&gt;)</summary>
    [XmlElement("Subscribe")] public List<GemSubscribeConfig> Subscriptions { get; set; } = [];
}

/// <summary>The configuration of a single subscription (&lt;Subscribe&gt;).</summary>
public class GemSubscribeConfig
{
    /// <summary>CEID (required)</summary>
    [XmlAttribute, ConfigRequired] public string Ceid { get; set; } = "";
    /// <summary>VIDs to receive, comma-separated (omitted = the event only, without values)</summary>
    [XmlAttribute] public string? Vids { get; set; }
}
