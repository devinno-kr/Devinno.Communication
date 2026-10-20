using System.IO.Ports;
using System.Text;
using System.Xml.Serialization;
using Devinno.Communication.Internal;

namespace Devinno.Communication.TextComm.RTU;

/// <summary>
/// The communication settings of <see cref="TextCommRTUSlave"/>. In XML it is the &lt;TextCommRTUSlave&gt; element.
/// </summary>
/// <remarks>
/// Omitted attributes use their default values. MessageEncoding is the web name of the encoding (for example, "us-ascii", "utf-8").
/// Code-page encodings such as euc-kr require the app to register CodePagesEncodingProvider.
/// </remarks>
/// <example>
/// <code>
/// &lt;TextCommRTUSlave Port="COM4" Baudrate="115200" MessageEncoding="us-ascii" AutoReconnect="true" /&gt;
/// </code>
/// </example>
[XmlRoot(nameof(TextCommRTUSlave))]
public class TextCommRTUSlaveConfig
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
    /// <summary>Web name of the message encoding (default: "us-ascii").</summary>
    [XmlAttribute] public string MessageEncoding { get; set; } = "us-ascii";
    /// <summary>Whether to reopen the port when it is lost (default: false).</summary>
    [XmlAttribute] public bool AutoReconnect { get; set; } = false;

    // 값을 검사하고 인코딩을 돌려준다. 실패하면 FormatException.
    internal Encoding Validate()
    {
        const string e = nameof(TextCommRTUSlave);
        XmlConfig.NotEmpty(e, nameof(Port), Port);
        XmlConfig.Range(e, nameof(Baudrate), Baudrate, 1, int.MaxValue);
        XmlConfig.Range(e, nameof(DataBits), DataBits, 5, 8);
        if (StopBits == StopBits.None) throw XmlConfig.Invalid(e, nameof(StopBits), "None", "One, OnePointFive, Two");
        return TextCommXml.Encoding(e, MessageEncoding);
    }
}
