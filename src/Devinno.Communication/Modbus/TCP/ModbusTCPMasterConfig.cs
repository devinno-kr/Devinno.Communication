using System.Xml.Serialization;
using Devinno.Communication.Internal;

namespace Devinno.Communication.Modbus.TCP;

/// <summary>
/// The communication settings, Auto list, and named write list of <see cref="ModbusTCPMaster"/>. In XML it is the &lt;ModbusTCPMaster&gt; element.
/// </summary>
/// <remarks>
/// Omitted attributes use their default values. You can check the format by generating the XSD with <see cref="ConfigSchema.Get{T}"/>.
/// </remarks>
/// <example>
/// <code>
/// &lt;ModbusTCPMaster RemoteIP="192.168.0.10" RemotePort="502" Timeout="300" Interval="10" AutoReconnect="true"&gt;
///   &lt;Auto Id="1" Func="FC3" Slave="1" Start="0" Length="10" /&gt;
///   &lt;Write Name="Recipe" Id="30" Func="FC16" Slave="1" Start="10" /&gt;
/// &lt;/ModbusTCPMaster&gt;
/// </code>
/// </example>
[XmlRoot(nameof(ModbusTCPMaster))]
public class ModbusTCPMasterConfig
{
    /// <summary>IP address or host name of the slave to connect to (default: "127.0.0.1").</summary>
    [XmlAttribute] public string RemoteIP { get; set; } = "127.0.0.1";
    /// <summary>Port of the slave to connect to, 1 to 65535 (default: 502).</summary>
    [XmlAttribute] public int RemotePort { get; set; } = 502;
    /// <summary>Response timeout in ms, 1 to 60000 (default: 100).</summary>
    [XmlAttribute] public int Timeout { get; set; } = 100;
    /// <summary>Wait between jobs in ms, 0 to 60000 (default: 10).</summary>
    [XmlAttribute] public int Interval { get; set; } = 10;
    /// <summary>Whether to reconnect when the connection is lost (default: true).</summary>
    [XmlAttribute] public bool AutoReconnect { get; set; } = true;
    /// <summary>Whether to discard writes while there is no connection (default: true). See <see cref="ModbusTCPMaster.DiscardWritesWhileDisconnected"/>.</summary>
    [XmlAttribute] public bool DiscardWritesWhileDisconnected { get; set; } = true;
    /// <summary>Auto (polling) read list. Unit ID 0 to 255.</summary>
    [XmlElement("Auto")] public List<ModbusAutoConfig> Autos { get; set; } = [];
    /// <summary>Named write list. These are &lt;Write&gt; elements, invoked by name through <see cref="ModbusTCPMaster.NamedWrite(string, long, int?, int?)"/>. Slave address 0 to 255.</summary>
    [XmlElement("Write")] public List<ModbusWriteConfig> Writes { get; set; } = [];

    internal void Validate()
    {
        const string e = nameof(ModbusTCPMaster);
        XmlConfig.NotEmpty(e, nameof(RemoteIP), RemoteIP);
        XmlConfig.Range(e, nameof(RemotePort), RemotePort, 1, 65535);
        XmlConfig.Range(e, nameof(Timeout), Timeout, 1, 60000);
        XmlConfig.Range(e, nameof(Interval), Interval, 0, 60000);
    }
}
