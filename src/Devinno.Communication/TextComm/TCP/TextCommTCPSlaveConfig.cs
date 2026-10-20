using System.Text;
using System.Xml.Serialization;
using Devinno.Communication.Internal;

namespace Devinno.Communication.TextComm.TCP;

/// <summary>
/// The settings of <see cref="TextCommTCPSlave"/>. In XML it is the &lt;TextCommTCPSlave&gt; element.
/// </summary>
/// <remarks>
/// Omitted attributes use their default values. MessageEncoding is the web name of the encoding (for example, "us-ascii", "utf-8").
/// Code-page encodings such as euc-kr require the app to register CodePagesEncodingProvider.
/// </remarks>
/// <example>
/// <code>
/// &lt;TextCommTCPSlave LocalPort="7897" MaxClients="10" MessageEncoding="utf-8" /&gt;
/// </code>
/// </example>
[XmlRoot(nameof(TextCommTCPSlave))]
public class TextCommTCPSlaveConfig
{
    /// <summary>Listening port, 1 to 65535 (default: 7897).</summary>
    [XmlAttribute] public int LocalPort { get; set; } = 7897;
    /// <summary>Maximum number of simultaneous connections, 1 to 1000 (default: 10).</summary>
    [XmlAttribute] public int MaxClients { get; set; } = 10;
    /// <summary>Web name of the message encoding (default: "us-ascii").</summary>
    [XmlAttribute] public string MessageEncoding { get; set; } = "us-ascii";

    // 값을 검사하고 인코딩을 돌려준다. 실패하면 FormatException.
    internal Encoding Validate()
    {
        const string e = nameof(TextCommTCPSlave);
        XmlConfig.Range(e, nameof(LocalPort), LocalPort, 1, 65535);
        XmlConfig.Range(e, nameof(MaxClients), MaxClients, 1, 1000);
        return TextCommXml.Encoding(e, MessageEncoding);
    }
}
