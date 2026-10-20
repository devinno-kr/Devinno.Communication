using System.IO.Ports;
using System.Xml.Serialization;
using Devinno.Communication.Internal;
using Devinno.Communication.Omron.Data;

namespace Devinno.Communication.Omron.Serial;

/// <summary>
/// The communication settings, Auto list, and named write list of <see cref="FinsSerial"/>. In XML it is the &lt;FinsSerial&gt; element; Auto items are &lt;Read&gt;/&lt;ReadBlock&gt; elements and named writes are &lt;Write&gt; elements.
/// </summary>
/// <remarks>Omitted attributes use their default values (the Omron Host Link defaults: 9600, 7, Even, Two). You can check the format by generating the XSD with <see cref="ConfigSchema.Get{T}"/>.</remarks>
/// <example>
/// <code>
/// &lt;FinsSerial Port="COM3" Baudrate="9600" Parity="Even" DataBits="7" StopBits="Two" UnitNumber="0"&gt;
///   &lt;Read Id="1" Addresses="D100,CIO0.00" /&gt;
///   &lt;ReadBlock Id="2" Address="D0" Count="100" Timeout="2000" /&gt;
///   &lt;Write Name="Speed" Id="20" Address="D200" /&gt;
/// &lt;/FinsSerial&gt;
/// </code>
/// </example>
[XmlRoot(nameof(FinsSerial))]
public class FinsSerialConfig
{
    /// <summary>Serial port name (default: "COM1").</summary>
    [XmlAttribute] public string Port { get; set; } = "COM1";
    /// <summary>Baud rate in bps, 1 or more (default: 9600).</summary>
    [XmlAttribute] public int Baudrate { get; set; } = 9600;
    /// <summary>Parity (default: Even).</summary>
    [XmlAttribute] public Parity Parity { get; set; } = Parity.Even;
    /// <summary>Data bits, 5 to 8 (default: 7).</summary>
    [XmlAttribute] public int DataBits { get; set; } = 7;
    /// <summary>Stop bits: One, OnePointFive, or Two (default: Two).</summary>
    [XmlAttribute] public StopBits StopBits { get; set; } = StopBits.Two;
    /// <summary>Host Link unit number of the PLC, 0 to 31 (default: 0).</summary>
    [XmlAttribute] public int UnitNumber { get; set; }
    /// <summary>PLC response wait time, 0 to 15 (x10 ms, default: 0).</summary>
    [XmlAttribute] public int ResponseWait { get; set; }
    /// <summary>Maximum number of items per request for block read/write, 1 to 266 (default: 266).</summary>
    [XmlAttribute] public int MaxWordsPerRequest { get; set; } = FinsFrame.SerialMaxWords;
    /// <summary>Response timeout in ms, 1 to 60000 (default: 1000).</summary>
    [XmlAttribute] public int Timeout { get; set; } = 1000;
    /// <summary>Wait between jobs in ms, 0 to 60000 (default: 10).</summary>
    [XmlAttribute] public int Interval { get; set; } = 10;
    /// <summary>Whether to reopen the port after a disconnection (default: false).</summary>
    [XmlAttribute] public bool AutoReconnect { get; set; } = false;
    /// <summary>Auto (polling) read list. &lt;Read&gt; and &lt;ReadBlock&gt; can be mixed and are polled in order.</summary>
    [XmlElement("Read", typeof(FinsReadConfig)), XmlElement("ReadBlock", typeof(FinsReadBlockConfig))]
    public List<FinsAutoConfig> Autos { get; set; } = [];
    /// <summary>Named write list. These are &lt;Write&gt; elements, invoked by name through <see cref="FinsSerial.NamedWrite(string, long, int?, int?)"/>.</summary>
    [XmlElement("Write")] public List<FinsWriteConfig> Writes { get; set; } = [];

    internal void Validate()
    {
        const string e = nameof(FinsSerial);
        XmlConfig.NotEmpty(e, nameof(Port), Port);
        XmlConfig.Range(e, nameof(Baudrate), Baudrate, 1, int.MaxValue);
        XmlConfig.Range(e, nameof(DataBits), DataBits, 5, 8);
        if (StopBits == StopBits.None) throw XmlConfig.Invalid(e, nameof(StopBits), "None", "One, OnePointFive, Two");
        XmlConfig.Range(e, nameof(UnitNumber), UnitNumber, 0, 31);
        XmlConfig.Range(e, nameof(ResponseWait), ResponseWait, 0, 15);
        XmlConfig.Range(e, nameof(MaxWordsPerRequest), MaxWordsPerRequest, 1, FinsFrame.SerialMaxWords);
        XmlConfig.Range(e, nameof(Timeout), Timeout, 1, 60000);
        XmlConfig.Range(e, nameof(Interval), Interval, 0, 60000);
    }
}
