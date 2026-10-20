using System.ComponentModel;
using System.IO.Ports;
using System.Xml.Serialization;
using Devinno.Communication.Internal;
using Devinno.Communication.Mitsubishi.Data;

namespace Devinno.Communication.Mitsubishi.Serial;

/// <summary>
/// The communication settings and Auto list of <see cref="McSerial"/>. In XML it is the &lt;McSerial&gt; element; Auto items are &lt;BR&gt;/&lt;WR&gt; elements and named writes are &lt;Write&gt; elements.
/// </summary>
/// <remarks>Omitted attributes use their default values. You can check the format by generating the XSD with <see cref="ConfigSchema.Get{T}"/>.</remarks>
/// <example>
/// <code>
/// &lt;McSerial Port="COM3" Baudrate="9600" Format="Format1" SumCheck="true" MessageWait="0" XYNotation="Auto"&gt;
///   &lt;BR Id="1" Slave="0" Device="M0" Count="16" /&gt;
///   &lt;WR Id="2" Slave="0" Device="D100" Count="10" Timeout="500" /&gt;
///   &lt;Write Name="Speed" Id="20" Slave="0" Device="D200" /&gt;
/// &lt;/McSerial&gt;
/// </code>
/// </example>
[XmlRoot(nameof(McSerial))]
public class McSerialConfig
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
    /// <summary>Frame format, Format1 or Format4 (default: Format1).</summary>
    [XmlAttribute] public McSerialFormat Format { get; set; } = McSerialFormat.Format1;
    /// <summary>Whether to use the sum check (default: true).</summary>
    [XmlAttribute] public bool SumCheck { get; set; } = true;
    /// <summary>Message wait, 0 to 15 (x10 ms, default: 0).</summary>
    [XmlAttribute] public int MessageWait { get; set; } = 0;
    /// <summary>Number base of X/Y numbers: Auto, Hex, or Octal (default: Auto, which is octal).</summary>
    [XmlAttribute] public McXYNotation XYNotation { get; set; } = McXYNotation.Auto;
    /// <summary>Response timeout in ms, 1 to 60000 (default: 1000).</summary>
    [XmlAttribute] public int Timeout { get; set; } = 1000;
    /// <summary>Wait between jobs in ms, 0 to 60000 (default: 10).</summary>
    [XmlAttribute] public int Interval { get; set; } = 10;
    /// <summary>Whether to reopen the port after a disconnection (default: false).</summary>
    [XmlAttribute] public bool AutoReconnect { get; set; } = false;
    /// <summary>Auto (polling) read list. &lt;BR&gt; and &lt;WR&gt; can be mixed and are polled in order.</summary>
    [XmlElement("BR", typeof(McSerialBrConfig)), XmlElement("WR", typeof(McSerialWrConfig))]
    public List<McSerialAutoConfig> Autos { get; set; } = [];
    /// <summary>Named write list. These are &lt;Write&gt; elements, invoked by name through <see cref="McSerial.NamedWrite(string, long, int?, int?)"/>.</summary>
    [XmlElement("Write")] public List<McSerialWriteConfig> Writes { get; set; } = [];

    internal void Validate()
    {
        const string e = nameof(McSerial);
        XmlConfig.NotEmpty(e, nameof(Port), Port);
        XmlConfig.Range(e, nameof(Baudrate), Baudrate, 1, int.MaxValue);
        XmlConfig.Range(e, nameof(DataBits), DataBits, 5, 8);
        if (StopBits == StopBits.None) throw XmlConfig.Invalid(e, nameof(StopBits), "None", "One, OnePointFive, Two");
        XmlConfig.Range(e, nameof(MessageWait), MessageWait, 0, 15);
        XmlConfig.Range(e, nameof(Timeout), Timeout, 1, 60000);
        XmlConfig.Range(e, nameof(Interval), Interval, 0, 60000);
    }
}

/// <summary>Common attributes of a McSerial Auto item.</summary>
public abstract class McSerialAutoConfig
{
    int timeout;

    /// <summary>Message ID. Returned as the MessageID of the response event (required).</summary>
    [XmlAttribute, ConfigRequired] public int Id { get; set; }
    /// <summary>Station number, 0 to 31 (0 to 15 for FX) (required).</summary>
    [XmlAttribute, ConfigRequired] public int Slave { get; set; }
    /// <summary>Start device (for example "D100", "M0") (required).</summary>
    [XmlAttribute, ConfigRequired] public string Device { get; set; } = "";
    /// <summary>Number of points to read. BR 1 to 256, WR 1 to 64 for a word device and 1 to 32 for a bit device (required).</summary>
    [XmlAttribute, ConfigRequired] public int Count { get; set; } = 1;
    /// <summary>Timeout of this item in ms, 1 to 60000. If not specified, the Timeout of McSerial is used.</summary>
    [XmlAttribute] public int Timeout { get => timeout; set { timeout = value; TimeoutSpecified = true; } }
    /// <summary>Whether <see cref="Timeout"/> was specified.</summary>
    [XmlIgnore, EditorBrowsable(EditorBrowsableState.Never)] public bool TimeoutSpecified { get; set; }
}

/// <summary>BR (bit batch read) Auto item. In XML it is the &lt;BR&gt; element.</summary>
public class McSerialBrConfig : McSerialAutoConfig { }

/// <summary>WR (word batch read) Auto item. In XML it is the &lt;WR&gt; element.</summary>
public class McSerialWrConfig : McSerialAutoConfig { }

/// <summary>McSerial named write item. In XML it is the &lt;Write&gt; element. A bit device is sent as BW and a word device as WW.</summary>
public class McSerialWriteConfig : NamedWriteConfig
{
    /// <summary>Station number, 0 to 31 (0 to 15 for FX) (required).</summary>
    [XmlAttribute, ConfigRequired] public int Slave { get; set; }
    /// <summary>Start device (for example "D200", "M100") (required).</summary>
    [XmlAttribute, ConfigRequired] public string Device { get; set; } = "";
}
