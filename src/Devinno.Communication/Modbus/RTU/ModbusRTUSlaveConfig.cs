using System.IO.Ports;
using System.Xml.Serialization;
using Devinno.Communication.Internal;

namespace Devinno.Communication.Modbus.RTU;

/// <summary>
/// The communication settings of <see cref="ModbusRTUSlave"/>. In XML it is the &lt;ModbusRTUSlave&gt; element. The memory arrays are not included.
/// </summary>
/// <remarks>Omitted attributes use their default values.</remarks>
/// <example>
/// <code>
/// &lt;ModbusRTUSlave Port="COM4" Baudrate="9600" Slave="1" AutoReconnect="true" /&gt;
/// </code>
/// </example>
[XmlRoot(nameof(ModbusRTUSlave))]
public class ModbusRTUSlaveConfig
{
    /// <summary>Serial port name (default: "COM1").</summary>
    [XmlAttribute] public string Port { get; set; } = "COM1";
    /// <summary>Baud rate in bps, 1 or more (default: 115200).</summary>
    [XmlAttribute] public int Baudrate { get; set; } = 115200;
    /// <summary>Parity (default: None).</summary>
    [XmlAttribute] public Parity Parity { get; set; } = Parity.None;
    /// <summary>Data bits, 5 to 8 (default: 8).</summary>
    [XmlAttribute] public int DataBits { get; set; } = 8;
    /// <summary>Stop bits: One, OnePointFive, or Two (default: One).</summary>
    [XmlAttribute] public StopBits StopBits { get; set; } = StopBits.One;
    /// <summary>Address of this slave, 1 to 247 (default: 1).</summary>
    [XmlAttribute] public int Slave { get; set; } = 1;
    /// <summary>Whether to reopen the port when it is lost (default: false).</summary>
    [XmlAttribute] public bool AutoReconnect { get; set; } = false;

    internal void Validate()
    {
        const string e = nameof(ModbusRTUSlave);
        XmlConfig.NotEmpty(e, nameof(Port), Port);
        XmlConfig.Range(e, nameof(Baudrate), Baudrate, 1, int.MaxValue);
        XmlConfig.Range(e, nameof(DataBits), DataBits, 5, 8);
        if (StopBits == StopBits.None) throw XmlConfig.Invalid(e, nameof(StopBits), "None", "One, OnePointFive, Two");
        XmlConfig.Range(e, nameof(Slave), Slave, 1, 247);
    }
}
