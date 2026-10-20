using System.Xml.Serialization;
using Devinno.Communication.Internal;

namespace Devinno.Communication.Omron.Data;

/// <summary>A named write item of an Omron FINS master (FinsUdp, FinsTcp, FinsSerial). In XML it is the &lt;Write&gt; element. One value is sent as Write (0102), several values as WriteBlock (0102).</summary>
public class FinsWriteConfig : NamedWriteConfig
{
    /// <summary>Address to write (e.g., "D100", "CIO0.05"). A word address writes words and a bit address writes bits (required).</summary>
    [XmlAttribute, ConfigRequired] public string Address { get; set; } = "";
}
