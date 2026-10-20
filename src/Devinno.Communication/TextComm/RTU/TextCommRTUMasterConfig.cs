using System.IO.Ports;
using System.Text;
using System.Xml.Serialization;
using Devinno.Communication.Internal;

namespace Devinno.Communication.TextComm.RTU;

/// <summary>
/// The communication settings and Auto list of <see cref="TextCommRTUMaster"/>. In XML it is the &lt;TextCommRTUMaster&gt; element.
/// </summary>
/// <remarks>
/// Omitted attributes use their default values. You can check the format by generating the XSD with <see cref="ConfigSchema.Get{T}"/>.
/// MessageEncoding is the web name of the encoding (Encoding.WebName, for example "us-ascii", "utf-8"). Code-page encodings such as euc-kr require the app to call
/// Encoding.RegisterProvider(CodePagesEncodingProvider.Instance) first (the library does not register it).
/// </remarks>
/// <example>
/// <code>
/// &lt;TextCommRTUMaster Port="COM3" Baudrate="115200" Parity="None" DataBits="8" StopBits="One"
///                    Timeout="100" Interval="10" AutoReconnect="false" BufferSize="1024" MessageEncoding="us-ascii"&gt;
///   &lt;Auto Id="1" Slave="1" Command="3" Message="READ" /&gt;
///   &lt;Auto Id="2" Slave="1" Command="4" Message="" Timeout="200" /&gt;
/// &lt;/TextCommRTUMaster&gt;
/// </code>
/// </example>
[XmlRoot(nameof(TextCommRTUMaster))]
public class TextCommRTUMasterConfig
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
    /// <summary>Whether to discard writes while there is no connection (default: true). See <see cref="TextCommRTUMaster.DiscardWritesWhileDisconnected"/>.</summary>
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
        const string e = nameof(TextCommRTUMaster);
        XmlConfig.NotEmpty(e, nameof(Port), Port);
        XmlConfig.Range(e, nameof(Baudrate), Baudrate, 1, int.MaxValue);
        XmlConfig.Range(e, nameof(DataBits), DataBits, 5, 8);
        if (StopBits == StopBits.None) throw XmlConfig.Invalid(e, nameof(StopBits), "None", "One, OnePointFive, Two");
        XmlConfig.Range(e, nameof(Timeout), Timeout, 1, 60000);
        XmlConfig.Range(e, nameof(Interval), Interval, 0, 60000);
        XmlConfig.Range(e, nameof(BufferSize), BufferSize, 16, 65536);
        var encoding = TextCommXml.Encoding(e, MessageEncoding);
        foreach (var a in Autos ?? []) a.Validate();
        return encoding;
    }
}
