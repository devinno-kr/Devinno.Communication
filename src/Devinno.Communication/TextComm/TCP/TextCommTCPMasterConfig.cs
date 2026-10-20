using System.Text;
using System.Xml.Serialization;
using Devinno.Communication.Internal;

namespace Devinno.Communication.TextComm.TCP;

/// <summary>
/// The communication settings and Auto list of <see cref="TextCommTCPMaster"/>. In XML it is the &lt;TextCommTCPMaster&gt; element.
/// </summary>
/// <remarks>
/// Omitted attributes use their default values. You can check the format by generating the XSD with <see cref="ConfigSchema.Get{T}"/>.
/// MessageEncoding is the web name of the encoding (Encoding.WebName, for example "us-ascii", "utf-8"). Code-page encodings such as euc-kr require the app to call
/// Encoding.RegisterProvider(CodePagesEncodingProvider.Instance) first (the library does not register it).
/// </remarks>
/// <example>
/// <code>
/// &lt;TextCommTCPMaster RemoteIP="192.168.0.50" RemotePort="7897" Timeout="300" MessageEncoding="utf-8"&gt;
///   &lt;Auto Id="1" Slave="1" Command="65" Message="READ" /&gt;
/// &lt;/TextCommTCPMaster&gt;
/// </code>
/// </example>
[XmlRoot(nameof(TextCommTCPMaster))]
public class TextCommTCPMasterConfig
{
    /// <summary>IP address or host name of the slave to connect to (default: "127.0.0.1").</summary>
    [XmlAttribute] public string RemoteIP { get; set; } = "127.0.0.1";
    /// <summary>Port to connect to, 1 to 65535 (default: 7897).</summary>
    [XmlAttribute] public int RemotePort { get; set; } = 7897;
    /// <summary>Response timeout in ms, 1 to 60000 (default: 100).</summary>
    [XmlAttribute] public int Timeout { get; set; } = 100;
    /// <summary>Wait between jobs in ms, 0 to 60000 (default: 10).</summary>
    [XmlAttribute] public int Interval { get; set; } = 10;
    /// <summary>Whether to reconnect when the connection is lost (default: true).</summary>
    [XmlAttribute] public bool AutoReconnect { get; set; } = true;
    /// <summary>Whether to discard writes while there is no connection (default: true). See <see cref="TextCommTCPMaster.DiscardWritesWhileDisconnected"/>.</summary>
    [XmlAttribute] public bool DiscardWritesWhileDisconnected { get; set; } = true;
    /// <summary>Maximum size in bytes of a response frame that can be received, 16 to 65536 (default: 1024).</summary>
    [XmlAttribute] public int BufferSize { get; set; } = 1024;
    /// <summary>Web name of the message encoding (default: "us-ascii").</summary>
    [XmlAttribute] public string MessageEncoding { get; set; } = "us-ascii";
    /// <summary>Auto (polling) transmission list.</summary>
    [XmlElement("Auto")] public List<TextCommAutoConfig> Autos { get; set; } = [];

    // 값을 검사하고 인코딩을 돌려준다. 실패하면 FormatException.
    internal Encoding Validate()
    {
        const string e = nameof(TextCommTCPMaster);
        XmlConfig.NotEmpty(e, nameof(RemoteIP), RemoteIP);
        XmlConfig.Range(e, nameof(RemotePort), RemotePort, 1, 65535);
        XmlConfig.Range(e, nameof(Timeout), Timeout, 1, 60000);
        XmlConfig.Range(e, nameof(Interval), Interval, 0, 60000);
        XmlConfig.Range(e, nameof(BufferSize), BufferSize, 16, 65536);
        var encoding = TextCommXml.Encoding(e, MessageEncoding);
        foreach (var a in Autos ?? []) a.Validate();
        return encoding;
    }
}
