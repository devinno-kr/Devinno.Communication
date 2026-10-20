using System.ComponentModel;
using System.IO.Ports;
using System.Xml.Serialization;
using Devinno.Communication.Internal;

namespace Devinno.Communication.LS.CNet;

/// <summary>
/// The communication settings and Auto list of <see cref="LsCNet"/>. In XML it is the &lt;LsCNet&gt; element; Auto items are &lt;RSS&gt;/&lt;RSB&gt; elements and named writes are &lt;Write&gt; elements.
/// </summary>
/// <remarks>Omitted attributes use their default values. You can check the format by generating the XSD with <see cref="ConfigSchema.Get{T}"/>.</remarks>
/// <example>
/// <code>
/// &lt;LsCNet Port="COM5" Baudrate="9600" Timeout="1000"&gt;
///   &lt;RSS Id="1" Slave="1" Devices="%MW100,%MW105" /&gt;
///   &lt;RSB Id="2" Slave="1" Device="%MW200" Count="10" /&gt;
///   &lt;Write Name="Speed" Id="20" Slave="1" Device="%MW300" /&gt;
/// &lt;/LsCNet&gt;
/// </code>
/// </example>
[XmlRoot(nameof(LsCNet))]
public class LsCNetConfig
{
    /// <summary>Serial port name (default: "COM1").</summary>
    [XmlAttribute] public string Port { get; set; } = "COM1";
    /// <summary>Baud rate in bps, 1 or more (default: 9600).</summary>
    [XmlAttribute] public int Baudrate { get; set; } = 9600;
    /// <summary>Parity (default: None).</summary>
    [XmlAttribute] public Parity Parity { get; set; } = Parity.None;
    /// <summary>Data bits, 5 to 8 (default: 8).</summary>
    [XmlAttribute] public int DataBits { get; set; } = 8;
    /// <summary>Stop bits: One, OnePointFive, or Two (default: One).</summary>
    [XmlAttribute] public StopBits StopBits { get; set; } = StopBits.One;
    /// <summary>Response timeout in ms, 1 to 60000 (default: 1000).</summary>
    [XmlAttribute] public int Timeout { get; set; } = 1000;
    /// <summary>Wait between jobs in ms, 0 to 60000 (default: 10).</summary>
    [XmlAttribute] public int Interval { get; set; } = 10;
    /// <summary>Whether to reopen the port after a disconnection (default: false).</summary>
    [XmlAttribute] public bool AutoReconnect { get; set; } = false;
    /// <summary>Whether to discard writes while there is no connection (default: true). See <see cref="LsCNet.DiscardWritesWhileDisconnected"/>.</summary>
    [XmlAttribute] public bool DiscardWritesWhileDisconnected { get; set; } = true;
    /// <summary>Auto (polling) read list. &lt;RSS&gt; and &lt;RSB&gt; can be mixed and are polled in order.</summary>
    [XmlElement("RSS", typeof(LsCNetRssConfig)), XmlElement("RSB", typeof(LsCNetRsbConfig))]
    public List<LsCNetAutoConfig> Autos { get; set; } = [];
    /// <summary>Named write list. These are &lt;Write&gt; elements, invoked by name through <see cref="LsCNet.NamedWrite(string, long, int?, int?)"/>.</summary>
    [XmlElement("Write")] public List<LsCNetWriteConfig> Writes { get; set; } = [];

    internal void Validate()
    {
        const string e = nameof(LsCNet);
        XmlConfig.NotEmpty(e, nameof(Port), Port);
        XmlConfig.Range(e, nameof(Baudrate), Baudrate, 1, int.MaxValue);
        XmlConfig.Range(e, nameof(DataBits), DataBits, 5, 8);
        if (StopBits == StopBits.None) throw XmlConfig.Invalid(e, nameof(StopBits), "None", "One, OnePointFive, Two");
        XmlConfig.Range(e, nameof(Timeout), Timeout, 1, 60000);
        XmlConfig.Range(e, nameof(Interval), Interval, 0, 60000);
    }
}

/// <summary>Common attributes of an LsCNet Auto item.</summary>
public abstract class LsCNetAutoConfig
{
    int timeout;

    /// <summary>Message ID. Returned as the MessageID of the response event (required).</summary>
    [XmlAttribute, ConfigRequired] public int Id { get; set; }
    /// <summary>Station number, 0 to 255 (required).</summary>
    [XmlAttribute, ConfigRequired] public int Slave { get; set; }
    /// <summary>Timeout of this item in ms, 1 to 60000. If not specified, the Timeout of CNet is used.</summary>
    [XmlAttribute] public int Timeout { get => timeout; set { timeout = value; TimeoutSpecified = true; } }
    /// <summary>Whether <see cref="Timeout"/> was specified.</summary>
    [XmlIgnore, EditorBrowsable(EditorBrowsableState.Never)] public bool TimeoutSpecified { get; set; }
}

/// <summary>RSS (random read) Auto item. In XML it is the &lt;RSS&gt; element.</summary>
public class LsCNetRssConfig : LsCNetAutoConfig
{
    /// <summary>1 to 16 devices separated by commas (for example "%MW100,%MW105"). All size characters must be the same (required).</summary>
    [XmlAttribute, ConfigRequired] public string Devices { get; set; } = "";
}

/// <summary>RSB (block read) Auto item. In XML it is the &lt;RSB&gt; element.</summary>
public class LsCNetRsbConfig : LsCNetAutoConfig
{
    /// <summary>Start device (bit X not allowed, for example "%MW100") (required).</summary>
    [XmlAttribute, ConfigRequired] public string Device { get; set; } = "";
    /// <summary>Number of items to read. At most 120 bytes (60 for W) (required).</summary>
    [XmlAttribute, ConfigRequired] public int Count { get; set; } = 1;
}

/// <summary>LsCNet named write item. In XML it is the &lt;Write&gt; element. One value is sent as WSS, several values as WSB.</summary>
public class LsCNetWriteConfig : NamedWriteConfig
{
    /// <summary>Station number, 0 to 255 (required).</summary>
    [XmlAttribute, ConfigRequired] public int Slave { get; set; }
    /// <summary>Device to write (for example "%MW100"). When writing several values this is the start device, and bit X is not allowed (required).</summary>
    [XmlAttribute, ConfigRequired] public string Device { get; set; } = "";
}
