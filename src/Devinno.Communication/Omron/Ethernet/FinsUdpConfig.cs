using System.ComponentModel;
using System.Xml.Serialization;
using Devinno.Communication.Internal;
using Devinno.Communication.Omron.Data;

namespace Devinno.Communication.Omron.Ethernet;

/// <summary>
/// The communication settings, Auto list, and named write list of <see cref="FinsUdp"/>. In XML it is the &lt;FinsUdp&gt; element; Auto items are &lt;Read&gt;/&lt;ReadBlock&gt; elements and named writes are &lt;Write&gt; elements.
/// </summary>
/// <remarks>Omitted attributes use their default values. If DestinationNode and SourceNode are omitted, the last number of the IP is used. You can check the format by generating the XSD with <see cref="ConfigSchema.Get{T}"/>.</remarks>
/// <example>
/// <code>
/// &lt;FinsUdp RemoteIP="192.168.250.1" RemotePort="9600" MaxWordsPerRequest="499"&gt;
///   &lt;Read Id="1" Addresses="D100,CIO0.05,W10" /&gt;
///   &lt;ReadBlock Id="2" Address="D1000" Count="800" Timeout="2000" /&gt;
///   &lt;Write Name="Speed" Id="20" Address="D200" /&gt;
/// &lt;/FinsUdp&gt;
/// </code>
/// </example>
[XmlRoot(nameof(FinsUdp))]
public class FinsUdpConfig
{
    int destinationNode, sourceNode;

    /// <summary>IP address or host name of the PLC (default: "127.0.0.1").</summary>
    [XmlAttribute] public string RemoteIP { get; set; } = "127.0.0.1";
    /// <summary>FINS/UDP port of the PLC, 1 to 65535 (default: 9600).</summary>
    [XmlAttribute] public int RemotePort { get; set; } = 9600;
    /// <summary>UDP port of this PC, 0 to 65535 (default: 0 = any port).</summary>
    [XmlAttribute] public int LocalPort { get; set; }
    /// <summary>Destination node DA1, 0 to 254. If not specified, the last number of RemoteIP.</summary>
    [XmlAttribute] public int DestinationNode { get => destinationNode; set { destinationNode = value; DestinationNodeSpecified = true; } }
    /// <summary>Whether <see cref="DestinationNode"/> was specified.</summary>
    [XmlIgnore, EditorBrowsable(EditorBrowsableState.Never)] public bool DestinationNodeSpecified { get; set; }
    /// <summary>Source node SA1, 1 to 254. If not specified, the last number of the IP of this PC that routes to the PLC.</summary>
    [XmlAttribute] public int SourceNode { get => sourceNode; set { sourceNode = value; SourceNodeSpecified = true; } }
    /// <summary>Whether <see cref="SourceNode"/> was specified.</summary>
    [XmlIgnore, EditorBrowsable(EditorBrowsableState.Never)] public bool SourceNodeSpecified { get; set; }
    /// <summary>Destination network DNA, 0 to 127 (default: 0).</summary>
    [XmlAttribute] public int DestinationNetwork { get; set; }
    /// <summary>Source network SNA, 0 to 127 (default: 0).</summary>
    [XmlAttribute] public int SourceNetwork { get; set; }
    /// <summary>Maximum number of items per request for block read/write, 1 to 997 (default: 499).</summary>
    [XmlAttribute] public int MaxWordsPerRequest { get; set; } = FinsFrame.DefaultWords;
    /// <summary>Response timeout in ms, 1 to 60000 (default: 1000).</summary>
    [XmlAttribute] public int Timeout { get; set; } = 1000;
    /// <summary>Wait between jobs in ms, 0 to 60000 (default: 10).</summary>
    [XmlAttribute] public int Interval { get; set; } = 10;
    /// <summary>Whether to reopen the socket on a socket error (default: true).</summary>
    [XmlAttribute] public bool AutoReconnect { get; set; } = true;
    /// <summary>Auto (polling) read list. &lt;Read&gt; and &lt;ReadBlock&gt; elements can be mixed and are polled in order.</summary>
    [XmlElement("Read", typeof(FinsReadConfig)), XmlElement("ReadBlock", typeof(FinsReadBlockConfig))]
    public List<FinsAutoConfig> Autos { get; set; } = [];
    /// <summary>Named write list. These are &lt;Write&gt; elements, invoked by name through <see cref="FinsUdp.NamedWrite(string, long, int?, int?)"/>.</summary>
    [XmlElement("Write")] public List<FinsWriteConfig> Writes { get; set; } = [];

    internal void Validate()
    {
        const string e = nameof(FinsUdp);
        XmlConfig.NotEmpty(e, nameof(RemoteIP), RemoteIP);
        XmlConfig.Range(e, nameof(RemotePort), RemotePort, 1, 65535);
        XmlConfig.Range(e, nameof(LocalPort), LocalPort, 0, 65535);
        if (DestinationNodeSpecified) XmlConfig.Range(e, nameof(DestinationNode), DestinationNode, 0, 254);
        if (SourceNodeSpecified) XmlConfig.Range(e, nameof(SourceNode), SourceNode, 1, 254);
        XmlConfig.Range(e, nameof(DestinationNetwork), DestinationNetwork, 0, 127);
        XmlConfig.Range(e, nameof(SourceNetwork), SourceNetwork, 0, 127);
        XmlConfig.Range(e, nameof(MaxWordsPerRequest), MaxWordsPerRequest, 1, FinsFrame.MaxWords);
        XmlConfig.Range(e, nameof(Timeout), Timeout, 1, 60000);
        XmlConfig.Range(e, nameof(Interval), Interval, 0, 60000);
    }
}
