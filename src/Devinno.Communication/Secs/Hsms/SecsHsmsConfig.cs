using System.Net;
using System.Xml.Serialization;
using Devinno.Communication.Internal;
using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Hsms;

/// <summary>
/// The communication settings of <see cref="SecsHsms"/>. In XML it is the &lt;SecsHsms&gt; element and it has no child elements.
/// </summary>
/// <remarks>
/// Omitted attributes use their default values. Ranges are checked on load: DeviceID 0 to 32767, T3 1000 to 120000, T5, T6, and T7 1000 to 240000, T8 1000 to 120000 (ms),
/// LinktestInterval 0 or 1000 to 3600000, MaxMessageSize 1024 to 1073741824, RemotePort 1 to 65535, LocalPort 0 to 65535. You can check the format by generating the XSD with <see cref="ConfigSchema.Get{T}"/>.
/// </remarks>
/// <example>
/// <code>
/// &lt;SecsHsms Role="Equipment" DeviceID="1" Mode="Passive" LocalIP="0.0.0.0" LocalPort="5000" T3="45000" LinktestInterval="60000" /&gt;
/// &lt;SecsHsms Role="Host" Mode="Active" RemoteIP="192.168.0.50" RemotePort="5000" T5="10000" /&gt;
/// </code>
/// </example>
[XmlRoot(nameof(SecsHsms))]
public class SecsHsmsConfig
{
    /// <summary>Role: Host or Equipment (default: Host).</summary>
    [XmlAttribute] public SecsRole Role { get; set; } = SecsRole.Host;
    /// <summary>Device ID, 0 to 32767 (default: 0).</summary>
    [XmlAttribute] public int DeviceID { get; set; }
    /// <summary>T3 reply timeout in ms, 1000 to 120000 (default: 45000).</summary>
    [XmlAttribute] public int T3 { get; set; } = 45000;
    /// <summary>Connection mode: Active or Passive (default: Active).</summary>
    [XmlAttribute] public HsmsMode Mode { get; set; } = HsmsMode.Active;
    /// <summary>Address of the peer when Active (default: "127.0.0.1").</summary>
    [XmlAttribute] public string RemoteIP { get; set; } = "127.0.0.1";
    /// <summary>Port of the peer when Active, 1 to 65535 (default: 5000).</summary>
    [XmlAttribute] public int RemotePort { get; set; } = 5000;
    /// <summary>Local IP when Passive (default: "0.0.0.0").</summary>
    [XmlAttribute] public string LocalIP { get; set; } = "0.0.0.0";
    /// <summary>Local port when Passive, 0 to 65535 (default: 5000).</summary>
    [XmlAttribute] public int LocalPort { get; set; } = 5000;
    /// <summary>T5 connect separation timeout in ms, 1000 to 240000 (default: 10000).</summary>
    [XmlAttribute] public int T5 { get; set; } = 10000;
    /// <summary>T6 control transaction timeout in ms, 1000 to 240000 (default: 5000).</summary>
    [XmlAttribute] public int T6 { get; set; } = 5000;
    /// <summary>T7 not-selected timeout in ms, 1000 to 240000 (default: 10000).</summary>
    [XmlAttribute] public int T7 { get; set; } = 10000;
    /// <summary>T8 inter-character timeout in ms, 1000 to 120000 (default: 5000).</summary>
    [XmlAttribute] public int T8 { get; set; } = 5000;
    /// <summary>Linktest interval in ms, 0 (off) or 1000 to 3600000 (default: 60000).</summary>
    [XmlAttribute] public int LinktestInterval { get; set; } = 60000;
    /// <summary>Maximum size of a message to be received in bytes, 1024 to 1073741824 (default: 16777216).</summary>
    [XmlAttribute] public int MaxMessageSize { get; set; } = 16_777_216;
    /// <summary>Whether to reconnect when Active loses the connection (default: true).</summary>
    [XmlAttribute] public bool AutoReconnect { get; set; } = true;

    internal void Validate()
    {
        const string e = nameof(SecsHsms);
        XmlConfig.Range(e, nameof(DeviceID), DeviceID, 0, 32767);
        XmlConfig.Range(e, nameof(T3), T3, 1000, 120000);
        XmlConfig.NotEmpty(e, nameof(RemoteIP), RemoteIP);
        XmlConfig.Range(e, nameof(RemotePort), RemotePort, 1, 65535);
        if (!IPAddress.TryParse(LocalIP, out _)) throw XmlConfig.Invalid(e, nameof(LocalIP), LocalIP ?? "", "IP 주소가 아님");
        XmlConfig.Range(e, nameof(LocalPort), LocalPort, 0, 65535);
        XmlConfig.Range(e, nameof(T5), T5, 1000, 240000);
        XmlConfig.Range(e, nameof(T6), T6, 1000, 240000);
        XmlConfig.Range(e, nameof(T7), T7, 1000, 240000);
        XmlConfig.Range(e, nameof(T8), T8, 1000, 120000);
        if (LinktestInterval != 0) XmlConfig.Range(e, nameof(LinktestInterval), LinktestInterval, 1000, 3600000);
        XmlConfig.Range(e, nameof(MaxMessageSize), MaxMessageSize, 1024, 1_073_741_824);
    }
}
