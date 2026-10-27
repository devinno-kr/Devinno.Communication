using System.Net;
using System.Xml.Serialization;
using Devinno.Communication.Internal;

namespace Devinno.Communication.Modbus.TCP;

/// <summary>
/// The settings of <see cref="ModbusTCPSlave"/>. In XML it is the &lt;ModbusTCPSlave&gt; element. The memory arrays are not included.
/// </summary>
/// <remarks>Omitted attributes use their default values.</remarks>
/// <example>
/// <code>
/// &lt;ModbusTCPSlave LocalIP="0.0.0.0" LocalPort="502" Slave="1" MaxClients="10" /&gt;
/// </code>
/// </example>
[XmlRoot(nameof(ModbusTCPSlave))]
public class ModbusTCPSlaveConfig
{
    /// <summary>Local IP on which to listen, an IP address (default: "0.0.0.0" = all interfaces; "127.0.0.1" = local connections only).</summary>
    [XmlAttribute] public string LocalIP { get; set; } = "0.0.0.0";
    /// <summary>Listening port, 0 to 65535 (default: 502).</summary>
    [XmlAttribute] public int LocalPort { get; set; } = 502;
    /// <summary>Unit ID of this slave, 1 to 247 (default: 1).</summary>
    [XmlAttribute] public int Slave { get; set; } = 1;
    /// <summary>Maximum number of simultaneous connections, 1 to 1000 (default: 10).</summary>
    [XmlAttribute] public int MaxClients { get; set; } = 10;

    internal void Validate()
    {
        const string e = nameof(ModbusTCPSlave);
        if (!IPAddress.TryParse(LocalIP, out _)) throw XmlConfig.Invalid(e, nameof(LocalIP), LocalIP ?? "", "IP 주소가 아님");
        XmlConfig.Range(e, nameof(LocalPort), LocalPort, 0, 65535);
        XmlConfig.Range(e, nameof(Slave), Slave, 1, 247);
        XmlConfig.Range(e, nameof(MaxClients), MaxClients, 1, 1000);
    }
}
