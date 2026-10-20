using System.ComponentModel;
using System.Xml.Serialization;
using Devinno.Communication.Internal;

namespace Devinno.Communication;

/// <summary>Common properties of a named write (&lt;Write&gt;) item. Derived classes add the protocol-specific address properties.</summary>
public abstract class NamedWriteConfig
{
    int timeout;
    int repeatCount;

    /// <summary>Write name (required). 1 to 64 characters, no leading or trailing whitespace, case-sensitive, and must be unique within the same configuration.</summary>
    [XmlAttribute, ConfigRequired] public string Name { get; set; } = "";
    /// <summary>Request ID (required). The write result event uses this value to identify the request.</summary>
    [XmlAttribute, ConfigRequired] public int Id { get; set; }
    /// <summary>Timeout of this item in ms, 1 to 60000. If not specified, the Timeout of the device settings is used.</summary>
    [XmlAttribute] public int Timeout { get => timeout; set { timeout = value; TimeoutSpecified = true; } }
    /// <summary>Whether <see cref="Timeout"/> was specified.</summary>
    [XmlIgnore, EditorBrowsable(EditorBrowsableState.Never)] public bool TimeoutSpecified { get; set; }
    /// <summary>Retry count of this item, 0 to 100. If not specified, no retries (unless the NamedWrite call gives repeatCount). A retry resends a write the device may already have executed; do not use it for pulse, counter or incremental writes.</summary>
    [XmlAttribute] public int RepeatCount { get => repeatCount; set { repeatCount = value; RepeatCountSpecified = true; } }
    /// <summary>Whether <see cref="RepeatCount"/> was specified.</summary>
    [XmlIgnore, EditorBrowsable(EditorBrowsableState.Never)] public bool RepeatCountSpecified { get; set; }

    // 얕은 복사본 (표에 저장·꺼낼 때 원본과 분리)
    internal NamedWriteConfig Copy() => (NamedWriteConfig)MemberwiseClone();
}
