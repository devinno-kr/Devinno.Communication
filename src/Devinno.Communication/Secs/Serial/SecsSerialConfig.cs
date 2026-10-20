using System.ComponentModel;
using System.IO.Ports;
using System.Xml.Serialization;
using Devinno.Communication.Internal;
using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Serial;

/// <summary>
/// The communication settings of <see cref="SecsSerial"/>. In XML it is the &lt;SecsSerial&gt; element and it has no child elements.
/// </summary>
/// <remarks>
/// Omitted attributes use their default values, and if IsMaster is omitted, the Role default applies (master if Equipment). Ranges are checked on load:
/// DeviceID 0 to 32767, T1 100 to 10000, T2 200 to 25000, T3 1000 to 120000, T4 1000 to 120000 (ms), RetryLimit 0 to 31, DataBits 5 to 8.
/// You can check the format by generating the XSD with <see cref="ConfigSchema.Get{T}"/>.
/// </remarks>
/// <example>
/// <code>
/// &lt;SecsSerial Role="Equipment" DeviceID="1" Port="COM3" Baudrate="9600" T1="500" T2="10000" T3="45000" T4="45000" RetryLimit="3" /&gt;
/// </code>
/// </example>
[XmlRoot(nameof(SecsSerial))]
public class SecsSerialConfig
{
    bool isMaster;

    /// <summary>Role: Host or Equipment (default: Host).</summary>
    [XmlAttribute] public SecsRole Role { get; set; } = SecsRole.Host;
    /// <summary>Device ID, 0 to 32767 (default: 0).</summary>
    [XmlAttribute] public int DeviceID { get; set; }
    /// <summary>T3 reply timeout in ms, 1000 to 120000 (default: 45000).</summary>
    [XmlAttribute] public int T3 { get; set; } = 45000;
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
    /// <summary>T1 inter-character timeout in ms, 100 to 10000 (default: 500).</summary>
    [XmlAttribute] public int T1 { get; set; } = 500;
    /// <summary>T2 protocol timeout in ms, 200 to 25000 (default: 10000).</summary>
    [XmlAttribute] public int T2 { get; set; } = 10000;
    /// <summary>T4 inter-block timeout in ms, 1000 to 120000 (default: 45000).</summary>
    [XmlAttribute] public int T4 { get; set; } = 45000;
    /// <summary>Retry count for sending a block, 0 to 31 (default: 3).</summary>
    [XmlAttribute] public int RetryLimit { get; set; } = 3;
    /// <summary>Whether to act as master on contention. If omitted, the Role default applies (true if Equipment).</summary>
    [XmlAttribute] public bool IsMaster { get => isMaster; set { isMaster = value; IsMasterSpecified = true; } }
    /// <summary>Whether <see cref="IsMaster"/> was specified.</summary>
    [XmlIgnore, EditorBrowsable(EditorBrowsableState.Never)] public bool IsMasterSpecified { get; set; }
    /// <summary>Whether to check for duplicate blocks (default: true).</summary>
    [XmlAttribute] public bool DuplicateDetection { get; set; } = true;
    /// <summary>Whether to reopen the port after a disconnection (default: false).</summary>
    [XmlAttribute] public bool AutoReconnect { get; set; } = false;

    internal void Validate()
    {
        const string e = nameof(SecsSerial);
        XmlConfig.Range(e, nameof(DeviceID), DeviceID, 0, 32767);
        XmlConfig.Range(e, nameof(T3), T3, 1000, 120000);
        XmlConfig.NotEmpty(e, nameof(Port), Port);
        XmlConfig.Range(e, nameof(Baudrate), Baudrate, 1, int.MaxValue);
        XmlConfig.Range(e, nameof(DataBits), DataBits, 5, 8);
        if (StopBits == StopBits.None) throw XmlConfig.Invalid(e, nameof(StopBits), "None", "One, OnePointFive, Two");
        XmlConfig.Range(e, nameof(T1), T1, 100, 10000);
        XmlConfig.Range(e, nameof(T2), T2, 200, 25000);
        XmlConfig.Range(e, nameof(T4), T4, 1000, 120000);
        XmlConfig.Range(e, nameof(RetryLimit), RetryLimit, 0, 31);
    }
}
