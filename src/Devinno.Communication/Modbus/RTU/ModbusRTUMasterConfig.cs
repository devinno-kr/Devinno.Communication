using System.IO.Ports;
using System.Xml.Serialization;
using Devinno.Communication.Internal;

namespace Devinno.Communication.Modbus.RTU;

/// <summary>
/// The communication settings, Auto list, and named write list of <see cref="ModbusRTUMaster"/>. In XML it is the &lt;ModbusRTUMaster&gt; element.
/// </summary>
/// <remarks>
/// Omitted attributes use their default values. You can check the format by generating the XSD with <see cref="ConfigSchema.Get{T}"/>.
/// </remarks>
/// <example>
/// <code>
/// &lt;ModbusRTUMaster Port="COM3" Baudrate="9600" Parity="None" DataBits="8" StopBits="One"
///                  Timeout="200" Interval="10" AutoReconnect="true"&gt;
///   &lt;Auto Id="1" Func="FC3" Slave="1" Start="0" Length="10" /&gt;
///   &lt;Write Name="Recipe" Id="30" Func="FC16" Slave="1" Start="10" /&gt;
/// &lt;/ModbusRTUMaster&gt;
/// </code>
/// </example>
[XmlRoot(nameof(ModbusRTUMaster))]
public class ModbusRTUMasterConfig
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
    /// <summary>Response timeout in ms, 1 to 60000 (default: 100).</summary>
    [XmlAttribute] public int Timeout { get; set; } = 100;
    /// <summary>Wait between jobs in ms, 0 to 60000 (default: 10).</summary>
    [XmlAttribute] public int Interval { get; set; } = 10;
    /// <summary>Whether to reopen the port when it is lost (default: false).</summary>
    [XmlAttribute] public bool AutoReconnect { get; set; } = false;
    /// <summary>Whether to discard writes while there is no connection (default: true). See <see cref="ModbusRTUMaster.DiscardWritesWhileDisconnected"/>.</summary>
    [XmlAttribute] public bool DiscardWritesWhileDisconnected { get; set; } = true;
    /// <summary>Auto (polling) read list. Slave address 1 to 247.</summary>
    [XmlElement("Auto")] public List<ModbusAutoConfig> Autos { get; set; } = [];
    /// <summary>Named write list. These are &lt;Write&gt; elements, invoked by name through <see cref="ModbusRTUMaster.NamedWrite(string, long, int?, int?)"/>. Slave address 0 to 247 (0 is broadcast).</summary>
    [XmlElement("Write")] public List<ModbusWriteConfig> Writes { get; set; } = [];

    internal void Validate()
    {
        const string e = nameof(ModbusRTUMaster);
        XmlConfig.NotEmpty(e, nameof(Port), Port);
        XmlConfig.Range(e, nameof(Baudrate), Baudrate, 1, int.MaxValue);
        XmlConfig.Range(e, nameof(DataBits), DataBits, 5, 8);
        if (StopBits == StopBits.None) throw XmlConfig.Invalid(e, nameof(StopBits), "None", "One, OnePointFive, Two");
        XmlConfig.Range(e, nameof(Timeout), Timeout, 1, 60000);
        XmlConfig.Range(e, nameof(Interval), Interval, 0, 60000);
    }
}
