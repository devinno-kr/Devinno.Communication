using System.ComponentModel;
using System.Xml.Serialization;
using Devinno.Communication.Internal;
using Devinno.Communication.Mitsubishi.Data;

namespace Devinno.Communication.Mitsubishi.Ethernet;

/// <summary>
/// The communication settings and Auto list of <see cref="McEthernet"/>. In XML it is the &lt;McEthernet&gt; element; Auto items are &lt;BR&gt;/&lt;WR&gt; elements and named writes are &lt;Write&gt; elements.
/// </summary>
/// <remarks>Omitted attributes use their default values. You can check the format by generating the XSD with <see cref="ConfigSchema.Get{T}"/>.</remarks>
/// <example>
/// <code>
/// &lt;McEthernet RemoteIP="192.168.0.30" RemotePort="5000" Frame="E3" XYNotation="Auto"&gt;
///   &lt;WR Id="1" Device="D100" Count="10" /&gt;
///   &lt;BR Id="2" Device="M0" Count="32" Timeout="500" /&gt;
///   &lt;Write Name="Speed" Id="20" Device="D200" /&gt;
/// &lt;/McEthernet&gt;
/// </code>
/// </example>
[XmlRoot(nameof(McEthernet))]
public class McEthernetConfig
{
    /// <summary>IP address or host name of the PLC to connect to (default: "127.0.0.1").</summary>
    [XmlAttribute] public string RemoteIP { get; set; } = "127.0.0.1";
    /// <summary>Port to connect to, 1 to 65535 (default: 5000).</summary>
    [XmlAttribute] public int RemotePort { get; set; } = 5000;
    /// <summary>Frame, E3 or E1 (default: E3).</summary>
    [XmlAttribute] public McFrame Frame { get; set; } = McFrame.E3;
    /// <summary>Number base of X/Y numbers: Auto, Hex, or Octal (default: Auto, which is hexadecimal for E3 and octal for E1). FX5 over 3E uses octal X/Y: set Octal. With Auto, X/Y numbers are read as hexadecimal and address the wrong points.</summary>
    [XmlAttribute] public McXYNotation XYNotation { get; set; } = McXYNotation.Auto;
    /// <summary>Response timeout in ms, 1 to 60000 (default: 1000).</summary>
    [XmlAttribute] public int Timeout { get; set; } = 1000;
    /// <summary>Wait between jobs in ms, 0 to 60000 (default: 10).</summary>
    [XmlAttribute] public int Interval { get; set; } = 10;
    /// <summary>Whether to reconnect after a disconnection (default: true).</summary>
    [XmlAttribute] public bool AutoReconnect { get; set; } = true;
    /// <summary>Whether to discard writes while there is no connection (default: true). See <see cref="McEthernet.DiscardWritesWhileDisconnected"/>.</summary>
    [XmlAttribute] public bool DiscardWritesWhileDisconnected { get; set; } = true;
    /// <summary>Auto (polling) read list. &lt;BR&gt; and &lt;WR&gt; can be mixed and are polled in order.</summary>
    [XmlElement("BR", typeof(McEthernetBrConfig)), XmlElement("WR", typeof(McEthernetWrConfig))]
    public List<McEthernetAutoConfig> Autos { get; set; } = [];
    /// <summary>Named write list. These are &lt;Write&gt; elements, invoked by name through <see cref="McEthernet.NamedWrite(string, long, int?, int?)"/>.</summary>
    [XmlElement("Write")] public List<McEthernetWriteConfig> Writes { get; set; } = [];

    internal void Validate()
    {
        const string e = nameof(McEthernet);
        XmlConfig.NotEmpty(e, nameof(RemoteIP), RemoteIP);
        XmlConfig.Range(e, nameof(RemotePort), RemotePort, 1, 65535);
        XmlConfig.Range(e, nameof(Timeout), Timeout, 1, 60000);
        XmlConfig.Range(e, nameof(Interval), Interval, 0, 60000);
    }
}

/// <summary>Common attributes of a McEthernet Auto item.</summary>
public abstract class McEthernetAutoConfig
{
    int timeout;

    /// <summary>Message ID. Returned as the MessageID of the response event (required).</summary>
    [XmlAttribute, ConfigRequired] public int Id { get; set; }
    /// <summary>Start device (for example "D100", "M0") (required).</summary>
    [XmlAttribute, ConfigRequired] public string Device { get; set; } = "";
    /// <summary>Number of points to read. E3: BR 1 to 3584, WR 1 to 960. E1: BR 1 to 256, WR 1 to 64 for a word device and 1 to 32 for a bit device (required).</summary>
    [XmlAttribute, ConfigRequired] public int Count { get; set; } = 1;
    /// <summary>Timeout of this item in ms, 1 to 60000. If not specified, the Timeout of McEthernet is used.</summary>
    [XmlAttribute] public int Timeout { get => timeout; set { timeout = value; TimeoutSpecified = true; } }
    /// <summary>Whether <see cref="Timeout"/> was specified.</summary>
    [XmlIgnore, EditorBrowsable(EditorBrowsableState.Never)] public bool TimeoutSpecified { get; set; }
}

/// <summary>BR (bit batch read) Auto item. In XML it is the &lt;BR&gt; element.</summary>
public class McEthernetBrConfig : McEthernetAutoConfig { }

/// <summary>WR (word batch read) Auto item. In XML it is the &lt;WR&gt; element.</summary>
public class McEthernetWrConfig : McEthernetAutoConfig { }

/// <summary>McEthernet named write item. In XML it is the &lt;Write&gt; element. A bit device is sent as BW and a word device as WW.</summary>
public class McEthernetWriteConfig : NamedWriteConfig
{
    /// <summary>Start device (for example "D200", "M100") (required).</summary>
    [XmlAttribute, ConfigRequired] public string Device { get; set; } = "";
}
