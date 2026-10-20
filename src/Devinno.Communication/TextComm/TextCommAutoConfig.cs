using System.ComponentModel;
using System.Text;
using System.Xml.Serialization;
using Devinno.Communication.Internal;

namespace Devinno.Communication.TextComm;

/// <summary>
/// A single Auto (polling) transmission item of a TextComm master (RTU/TCP). In XML it is the &lt;Auto&gt; element.
/// </summary>
/// <remarks>Message may be an empty string (""), but the attribute must be written (Message="").</remarks>
/// <example>
/// <code>
/// &lt;Auto Id="1" Slave="1" Command="65" Message="READ" /&gt;
/// &lt;Auto Id="2" Slave="1" Command="66" Message="" Timeout="200" /&gt;
/// </code>
/// </example>
public class TextCommAutoConfig
{
    int timeout;

    /// <summary>Message ID. Returned as the MessageID of the response event (required).</summary>
    [XmlAttribute, ConfigRequired] public int Id { get; set; }
    /// <summary>Slave, 0 to 255 (required).</summary>
    [XmlAttribute, ConfigRequired] public int Slave { get; set; }
    /// <summary>Command, 0 to 255 (required).</summary>
    [XmlAttribute, ConfigRequired] public int Command { get; set; }
    /// <summary>Message to send. An empty string is allowed (required).</summary>
    [XmlAttribute, ConfigRequired] public string Message { get; set; } = "";
    /// <summary>Timeout of this item in ms, 1 to 60000. If not specified, the master's Timeout is used.</summary>
    [XmlAttribute] public int Timeout { get => timeout; set { timeout = value; TimeoutSpecified = true; } }
    /// <summary>Whether <see cref="Timeout"/> was specified. If false, it is not written to the XML and the master's Timeout is used.</summary>
    [XmlIgnore, EditorBrowsable(EditorBrowsableState.Never)] public bool TimeoutSpecified { get; set; }

    // 값을 검사한다. 실패하면 FormatException.
    internal void Validate()
    {
        var e = $"Auto Id=\"{Id}\"";
        XmlConfig.Range(e, nameof(Slave), Slave, 0, 255);
        XmlConfig.Range(e, nameof(Command), Command, 0, 255);
        if (Message == null) throw XmlConfig.Invalid(e, nameof(Message), "", "null이면 안 됨 (빈 문자열은 가능)");
        if (TimeoutSpecified) XmlConfig.Range(e, nameof(Timeout), Timeout, 1, 60000);
    }
}

// TextComm 설정 모델의 공통 검사
internal static class TextCommXml
{
    // MessageEncoding 속성 값(Encoding.WebName, 예: "us-ascii", "utf-8")을 인코딩으로 바꾼다. 모르는 이름이면 FormatException.
    // euc-kr 같은 코드 페이지 인코딩은 앱이 CodePagesEncodingProvider를 등록했을 때만 찾을 수 있다(라이브러리는 등록하지 않는다).
    public static Encoding Encoding(string element, string? name)
    {
        XmlConfig.NotEmpty(element, "MessageEncoding", name);
        try { return System.Text.Encoding.GetEncoding(name!); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            throw XmlConfig.Invalid(element, "MessageEncoding", name!, "알 수 없는 인코딩 이름 (예: us-ascii, utf-8)");
        }
    }

    // Auto 메시지에 XML 1.0이 담을 수 없는 문자가 있으면 InvalidOperationException (ToXml/SaveXml이 쓰기 전에 호출).
    public static void VerifyMessages(IEnumerable<TextCommAutoConfig> autos)
    {
        foreach (var a in autos)
        {
            try { System.Xml.XmlConvert.VerifyXmlChars(a.Message); }
            catch (System.Xml.XmlException)
            {
                throw new InvalidOperationException($"Auto Id=\"{a.Id}\"의 Message에 XML이 저장할 수 없는 문자(제어 문자 등)가 있어 XML로 만들 수 없습니다. 이런 Auto 항목은 코드에서 관리하세요.");
            }
        }
    }
}
