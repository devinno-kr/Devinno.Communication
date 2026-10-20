using System.ComponentModel;
using System.Xml.Serialization;
using Devinno.Communication.Internal;

namespace Devinno.Communication.LS.FEnet;

/// <summary>
/// The communication settings and Auto list of <see cref="LsFEnet"/>. In XML it is the &lt;LsFEnet&gt; element; Auto items are &lt;RSS&gt;/&lt;RSB&gt; elements and named writes are &lt;Write&gt; elements.
/// </summary>
/// <remarks>Omitted attributes use their default values. You can check the format by generating the XSD with <see cref="ConfigSchema.Get{T}"/>.</remarks>
/// <example>
/// <code>
/// &lt;LsFEnet RemoteIP="192.168.0.20" RemotePort="2004"&gt;
///   &lt;RSB Id="1" Device="%MW100" Count="10" /&gt;
///   &lt;Write Name="Speed" Id="20" Device="%MW300" /&gt;
/// &lt;/LsFEnet&gt;
/// </code>
/// </example>
[XmlRoot(nameof(LsFEnet))]
public class LsFEnetConfig
{
    /// <summary>IP address or host name of the PLC to connect to (default: "127.0.0.1").</summary>
    [XmlAttribute] public string RemoteIP { get; set; } = "127.0.0.1";
    /// <summary>Port to connect to, 1 to 65535 (default: 2004).</summary>
    [XmlAttribute] public int RemotePort { get; set; } = 2004;
    /// <summary>Response timeout in ms, 1 to 60000 (default: 1000).</summary>
    [XmlAttribute] public int Timeout { get; set; } = 1000;
    /// <summary>Wait between jobs in ms, 0 to 60000 (default: 10).</summary>
    [XmlAttribute] public int Interval { get; set; } = 10;
    /// <summary>Whether to reconnect after a disconnection (default: true).</summary>
    [XmlAttribute] public bool AutoReconnect { get; set; } = true;
    /// <summary>Whether to discard writes while there is no connection (default: true). See <see cref="LsFEnet.DiscardWritesWhileDisconnected"/>.</summary>
    [XmlAttribute] public bool DiscardWritesWhileDisconnected { get; set; } = true;
    /// <summary>Auto (polling) read list. &lt;RSS&gt; and &lt;RSB&gt; can be mixed and are polled in order.</summary>
    [XmlElement("RSS", typeof(LsFEnetRssConfig)), XmlElement("RSB", typeof(LsFEnetRsbConfig))]
    public List<LsFEnetAutoConfig> Autos { get; set; } = [];
    /// <summary>Named write list. These are &lt;Write&gt; elements, invoked by name through <see cref="LsFEnet.NamedWrite(string, long, int?, int?)"/>.</summary>
    [XmlElement("Write")] public List<LsFEnetWriteConfig> Writes { get; set; } = [];

    internal void Validate()
    {
        const string e = nameof(LsFEnet);
        XmlConfig.NotEmpty(e, nameof(RemoteIP), RemoteIP);
        XmlConfig.Range(e, nameof(RemotePort), RemotePort, 1, 65535);
        XmlConfig.Range(e, nameof(Timeout), Timeout, 1, 60000);
        XmlConfig.Range(e, nameof(Interval), Interval, 0, 60000);
    }
}

/// <summary>Common attributes of an LsFEnet Auto item.</summary>
public abstract class LsFEnetAutoConfig
{
    int timeout;

    /// <summary>Message ID. Returned as the MessageID of the response event (required).</summary>
    [XmlAttribute, ConfigRequired] public int Id { get; set; }
    /// <summary>Timeout of this item in ms, 1 to 60000. If not specified, the Timeout of FEnet is used.</summary>
    [XmlAttribute] public int Timeout { get => timeout; set { timeout = value; TimeoutSpecified = true; } }
    /// <summary>Whether <see cref="Timeout"/> was specified.</summary>
    [XmlIgnore, EditorBrowsable(EditorBrowsableState.Never)] public bool TimeoutSpecified { get; set; }
}

/// <summary>RSS (random read) Auto item. In XML it is the &lt;RSS&gt; element.</summary>
public class LsFEnetRssConfig : LsFEnetAutoConfig
{
    /// <summary>1 to 16 devices separated by commas (for example "%MW100,%MW105"). All size characters must be the same (required).</summary>
    [XmlAttribute, ConfigRequired] public string Devices { get; set; } = "";
}

/// <summary>RSB (block read) Auto item. In XML it is the &lt;RSB&gt; element.</summary>
public class LsFEnetRsbConfig : LsFEnetAutoConfig
{
    /// <summary>Start device (numeric address only, bit X not allowed, for example "%MW100") (required).</summary>
    [XmlAttribute, ConfigRequired] public string Device { get; set; } = "";
    /// <summary>Number of items to read. At most 1400 bytes (700 for W) (required).</summary>
    [XmlAttribute, ConfigRequired] public int Count { get; set; } = 1;
}

/// <summary>LsFEnet named write item. In XML it is the &lt;Write&gt; element. One value is sent as WSS, several values as WSB.</summary>
public class LsFEnetWriteConfig : NamedWriteConfig
{
    /// <summary>Device to write (for example "%MW100"). When writing several values this is the start device; numeric address only, and bit X is not allowed (required).</summary>
    [XmlAttribute, ConfigRequired] public string Device { get; set; } = "";
}
