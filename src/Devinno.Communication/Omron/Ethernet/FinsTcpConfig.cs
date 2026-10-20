using System.ComponentModel;
using System.Xml.Serialization;
using Devinno.Communication.Internal;
using Devinno.Communication.Omron.Data;

namespace Devinno.Communication.Omron.Ethernet;

/// <summary>
/// The communication settings, Auto list, and named write list of <see cref="FinsTcp"/>. In XML it is the &lt;FinsTcp&gt; element; Auto items are &lt;Read&gt;/&lt;ReadBlock&gt; elements and named writes are &lt;Write&gt; elements.
/// </summary>
/// <remarks>Omitted attributes use their default values. If ClientNode is omitted, the PLC assigns the node automatically. You can check the format by generating the XSD with <see cref="ConfigSchema.Get{T}"/>.</remarks>
/// <example>
/// <code>
/// &lt;FinsTcp RemoteIP="192.168.250.1" RemotePort="9600" MaxWordsPerRequest="250"&gt;
///   &lt;Read Id="1" Addresses="D100,W10.03" /&gt;
///   &lt;ReadBlock Id="2" Address="E0_0" Count="1000" /&gt;
///   &lt;Write Name="Speed" Id="20" Address="D200" /&gt;
/// &lt;/FinsTcp&gt;
/// </code>
/// </example>
[XmlRoot(nameof(FinsTcp))]
public class FinsTcpConfig
{
    int clientNode;

    /// <summary>IP address or host name of the PLC (default: "127.0.0.1").</summary>
    [XmlAttribute] public string RemoteIP { get; set; } = "127.0.0.1";
    /// <summary>FINS/TCP port of the PLC, 1 to 65535 (default: 9600).</summary>
    [XmlAttribute] public int RemotePort { get; set; } = 9600;
    /// <summary>FINS node of this PC to request, 1 to 254. If not specified, the PLC assigns it automatically.</summary>
    [XmlAttribute] public int ClientNode { get => clientNode; set { clientNode = value; ClientNodeSpecified = true; } }
    /// <summary>Whether <see cref="ClientNode"/> was specified.</summary>
    [XmlIgnore, EditorBrowsable(EditorBrowsableState.Never)] public bool ClientNodeSpecified { get; set; }
    /// <summary>Maximum number of items per request for block read/write, 1 to 997 (default: 499).</summary>
    [XmlAttribute] public int MaxWordsPerRequest { get; set; } = FinsFrame.DefaultWords;
    /// <summary>Response timeout in ms, 1 to 60000 (default: 1000).</summary>
    [XmlAttribute] public int Timeout { get; set; } = 1000;
    /// <summary>Wait between jobs in ms, 0 to 60000 (default: 10).</summary>
    [XmlAttribute] public int Interval { get; set; } = 10;
    /// <summary>Whether to reconnect when the connection is lost (default: true).</summary>
    [XmlAttribute] public bool AutoReconnect { get; set; } = true;
    /// <summary>Whether to discard writes while there is no connection (default: true). See <see cref="FinsTcp.DiscardWritesWhileDisconnected"/>.</summary>
    [XmlAttribute] public bool DiscardWritesWhileDisconnected { get; set; } = true;
    /// <summary>Auto (polling) read list. &lt;Read&gt; and &lt;ReadBlock&gt; elements can be mixed and are polled in order.</summary>
    [XmlElement("Read", typeof(FinsReadConfig)), XmlElement("ReadBlock", typeof(FinsReadBlockConfig))]
    public List<FinsAutoConfig> Autos { get; set; } = [];
    /// <summary>Named write list. These are &lt;Write&gt; elements, invoked by name through <see cref="FinsTcp.NamedWrite(string, long, int?, int?)"/>.</summary>
    [XmlElement("Write")] public List<FinsWriteConfig> Writes { get; set; } = [];

    internal void Validate()
    {
        const string e = nameof(FinsTcp);
        XmlConfig.NotEmpty(e, nameof(RemoteIP), RemoteIP);
        XmlConfig.Range(e, nameof(RemotePort), RemotePort, 1, 65535);
        if (ClientNodeSpecified) XmlConfig.Range(e, nameof(ClientNode), ClientNode, 1, 254);
        XmlConfig.Range(e, nameof(MaxWordsPerRequest), MaxWordsPerRequest, 1, FinsFrame.MaxWords);
        XmlConfig.Range(e, nameof(Timeout), Timeout, 1, 60000);
        XmlConfig.Range(e, nameof(Interval), Interval, 0, 60000);
    }
}
