using System.ComponentModel;
using System.Xml.Serialization;
using Devinno.Communication.Internal;

namespace Devinno.Communication.Siemens;

/// <summary>
/// The communication settings and Auto list of <see cref="S7Ethernet"/>. In XML it is the &lt;S7Ethernet&gt; element; Auto items are &lt;Read&gt;/&lt;ReadBlock&gt; elements and named writes are &lt;Write&gt; elements.
/// </summary>
/// <remarks>Omitted attributes use their default values. TSAP is written in decimal (for example 0x0301 is 769). You can check the format by generating the XSD with <see cref="ConfigSchema.Get{T}"/>.</remarks>
/// <example>
/// <code>
/// &lt;S7Ethernet RemoteIP="192.168.0.10" Rack="0" Slot="1" PduSize="960"&gt;
///   &lt;Read Id="1" Addresses="DB1.DBW0,DB1.DBD2,M10.0" /&gt;
///   &lt;ReadBlock Id="2" Address="DB1.DBB100" Count="500" Timeout="2000" /&gt;
///   &lt;Write Name="Speed" Id="20" Address="DB1.DBW10" /&gt;
/// &lt;/S7Ethernet&gt;
/// </code>
/// </example>
[XmlRoot(nameof(S7Ethernet))]
public class S7EthernetConfig
{
    int remoteTsap;

    /// <summary>IP address or host name of the PLC to connect to (default: "127.0.0.1").</summary>
    [XmlAttribute] public string RemoteIP { get; set; } = "127.0.0.1";
    /// <summary>Port to connect to, 1 to 65535 (default: 102).</summary>
    [XmlAttribute] public int RemotePort { get; set; } = 102;
    /// <summary>CPU rack number, 0 to 7 (default: 0).</summary>
    [XmlAttribute] public int Rack { get; set; }
    /// <summary>CPU slot number, 0 to 31 (default: 1 = S7-1200/1500; 2 for S7-300, 3 for S7-400).</summary>
    [XmlAttribute] public int Slot { get; set; } = 1;
    /// <summary>Connection type: PG, OP, or Basic (default: PG).</summary>
    [XmlAttribute] public S7ConnectionType ConnectionType { get; set; } = S7ConnectionType.PG;
    /// <summary>Specifies the remote (PLC) TSAP directly, 0 to 65535 (decimal). If not specified, it is calculated from ConnectionType, Rack, and Slot.</summary>
    [XmlAttribute] public int RemoteTsap { get => remoteTsap; set { remoteTsap = value; RemoteTsapSpecified = true; } }
    /// <summary>Whether <see cref="RemoteTsap"/> was specified.</summary>
    [XmlIgnore, EditorBrowsable(EditorBrowsableState.Never)] public bool RemoteTsapSpecified { get; set; }
    /// <summary>Own (local) TSAP, 0 to 65535 (decimal, default: 256 = 0x0100).</summary>
    [XmlAttribute] public int LocalTsap { get; set; } = 0x0100;
    /// <summary>PDU size to request, 240 to 960 (default: 960).</summary>
    [XmlAttribute] public int PduSize { get; set; } = 960;
    /// <summary>Response timeout in ms, 1 to 60000 (default: 1000).</summary>
    [XmlAttribute] public int Timeout { get; set; } = 1000;
    /// <summary>Wait between jobs in ms, 0 to 60000 (default: 10).</summary>
    [XmlAttribute] public int Interval { get; set; } = 10;
    /// <summary>Whether to reconnect after a disconnection (default: true).</summary>
    [XmlAttribute] public bool AutoReconnect { get; set; } = true;
    /// <summary>Auto (polling) read list. &lt;Read&gt; and &lt;ReadBlock&gt; can be mixed and are polled in order.</summary>
    [XmlElement("Read", typeof(S7ReadConfig)), XmlElement("ReadBlock", typeof(S7ReadBlockConfig))]
    public List<S7EthernetAutoConfig> Autos { get; set; } = [];
    /// <summary>Named write list. These are &lt;Write&gt; elements, invoked by name through <see cref="S7Ethernet.NamedWrite(string, long, int?, int?)"/>.</summary>
    [XmlElement("Write")] public List<S7WriteConfig> Writes { get; set; } = [];

    internal void Validate()
    {
        const string e = nameof(S7Ethernet);
        XmlConfig.NotEmpty(e, nameof(RemoteIP), RemoteIP);
        XmlConfig.Range(e, nameof(RemotePort), RemotePort, 1, 65535);
        XmlConfig.Range(e, nameof(Rack), Rack, 0, 7);
        XmlConfig.Range(e, nameof(Slot), Slot, 0, 31);
        if (!Enum.IsDefined(ConnectionType))
            throw XmlConfig.Invalid(e, nameof(ConnectionType), ((int)ConnectionType).ToString(), "PG, OP, Basic 중 하나");
        if (RemoteTsapSpecified) XmlConfig.Range(e, nameof(RemoteTsap), RemoteTsap, 0, 0xFFFF);
        XmlConfig.Range(e, nameof(LocalTsap), LocalTsap, 0, 0xFFFF);
        XmlConfig.Range(e, nameof(PduSize), PduSize, 240, 960);
        XmlConfig.Range(e, nameof(Timeout), Timeout, 1, 60000);
        XmlConfig.Range(e, nameof(Interval), Interval, 0, 60000);
    }
}

/// <summary>Common attributes of an S7Ethernet Auto item.</summary>
public abstract class S7EthernetAutoConfig
{
    int timeout;

    /// <summary>Message ID. Returned as the MessageID of the response event (required).</summary>
    [XmlAttribute, ConfigRequired] public int Id { get; set; }
    /// <summary>Timeout of this item in ms, 1 to 60000. If not specified, the Timeout of S7Ethernet is used.</summary>
    [XmlAttribute] public int Timeout { get => timeout; set { timeout = value; TimeoutSpecified = true; } }
    /// <summary>Whether <see cref="Timeout"/> was specified.</summary>
    [XmlIgnore, EditorBrowsable(EditorBrowsableState.Never)] public bool TimeoutSpecified { get; set; }
}

/// <summary>Random read (Read) Auto item. In XML it is the &lt;Read&gt; element.</summary>
public class S7ReadConfig : S7EthernetAutoConfig
{
    /// <summary>At most 19 addresses of any size, separated by commas (for example "DB1.DBW0,M10.0"); the request and response must fit in 240 bytes, so 20 is rejected (required).</summary>
    [XmlAttribute, ConfigRequired] public string Addresses { get; set; } = "";
}

/// <summary>Block read (ReadBlock) Auto item. In XML it is the &lt;ReadBlock&gt; element.</summary>
public class S7ReadBlockConfig : S7EthernetAutoConfig
{
    /// <summary>Start byte address (for example "DB1.DBB0", "MB0") (required).</summary>
    [XmlAttribute, ConfigRequired] public string Address { get; set; } = "";
    /// <summary>Number of bytes to read, 1 to 65535 (required).</summary>
    [XmlAttribute, ConfigRequired] public int Count { get; set; } = 1;
}

/// <summary>S7Ethernet named write item. In XML it is the &lt;Write&gt; element. One value is sent as a random write, several values as a block write.</summary>
public class S7WriteConfig : NamedWriteConfig
{
    /// <summary>Address to write (for example "DB1.DBW10", "M10.0"). To write several values, it must be a B, W, or D size address (required).</summary>
    [XmlAttribute, ConfigRequired] public string Address { get; set; } = "";
}
