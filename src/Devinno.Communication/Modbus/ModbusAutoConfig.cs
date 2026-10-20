using System.ComponentModel;
using System.Xml.Serialization;
using Devinno.Communication.Internal;

namespace Devinno.Communication.Modbus;

/// <summary>
/// A single Auto (polling) read item of a Modbus master. In XML it is the &lt;Auto&gt; element.
/// </summary>
/// <example>
/// <code>
/// &lt;Auto Id="1" Func="FC3" Slave="1" Start="0" Length="10" Timeout="200" /&gt;
/// </code>
/// </example>
public class ModbusAutoConfig
{
    int timeout;

    /// <summary>Message ID. Returned as the MessageID of the response event (required).</summary>
    [XmlAttribute, ConfigRequired] public int Id { get; set; }
    /// <summary>Read function, FC1 to FC4 (required).</summary>
    [XmlAttribute, ConfigRequired] public ModbusFunction Func { get; set; } = ModbusFunction.WORDREAD_F3;
    /// <summary>Slave address. 1 to 247 for RTU, 0 to 255 for TCP (required).</summary>
    [XmlAttribute, ConfigRequired] public int Slave { get; set; } = 1;
    /// <summary>Start address, 0 to 65535 (default: 0).</summary>
    [XmlAttribute] public int Start { get; set; }
    /// <summary>Number of items to read. 1 to 2000 for FC1/FC2, 1 to 125 for FC3/FC4 (required).</summary>
    [XmlAttribute, ConfigRequired] public int Length { get; set; } = 1;
    /// <summary>Timeout of this item in ms, 1 to 60000. If not specified, the master's Timeout is used.</summary>
    [XmlAttribute] public int Timeout { get => timeout; set { timeout = value; TimeoutSpecified = true; } }
    /// <summary>Whether <see cref="Timeout"/> was specified. If false, it is not written to the XML and the master's Timeout is used.</summary>
    [XmlIgnore, EditorBrowsable(EditorBrowsableState.Never)] public bool TimeoutSpecified { get; set; }

    // 값을 검사하고 요청 PDU를 만든다. 실패하면 FormatException.
    internal (byte[] Pdu, int? Timeout) Validate(int minSlave, int maxSlave)
    {
        var e = $"Auto Id=\"{Id}\"";
        if (Func is not (ModbusFunction.BITREAD_F1 or ModbusFunction.BITREAD_F2 or ModbusFunction.WORDREAD_F3 or ModbusFunction.WORDREAD_F4))
            throw XmlConfig.Invalid(e, nameof(Func), XmlConfig.EnumName(Func), "FC1, FC2, FC3, FC4");
        XmlConfig.Range(e, nameof(Slave), Slave, minSlave, maxSlave);
        XmlConfig.Range(e, nameof(Start), Start, 0, 65535);
        int maxLength = Func is ModbusFunction.BITREAD_F1 or ModbusFunction.BITREAD_F2 ? ModbusPdu.MaxReadBits : ModbusPdu.MaxReadWords;
        XmlConfig.Range(e, nameof(Length), Length, 1, maxLength);
        if (TimeoutSpecified) XmlConfig.Range(e, nameof(Timeout), Timeout, 1, 60000);
        try { return (ModbusPdu.ReadRequest(Func, Start, Length), TimeoutSpecified ? Timeout : null); }
        catch (ArgumentException ex) { throw XmlConfig.Invalid(e, nameof(Length), Length.ToString(), ex.Message); }
    }

    internal static ModbusAutoConfig FromWork(int id, int slave, byte[] pdu, int? timeout)
    {
        var c = new ModbusAutoConfig
        {
            Id = id,
            Func = (ModbusFunction)pdu[0],
            Slave = slave,
            Start = ModbusPdu.Word(pdu, 1),
            Length = ModbusPdu.Word(pdu, 3),
        };
        if (timeout is int t) c.Timeout = t;
        return c;
    }
}
