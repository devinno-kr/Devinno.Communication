using System.Xml.Serialization;
using Devinno.Communication.Internal;

namespace Devinno.Communication.Modbus;

/// <summary>
/// A single named write item of a Modbus master. In XML it is the &lt;Write&gt; element, shared by the RTU and TCP masters.
/// </summary>
/// <example>
/// <code>
/// &lt;Write Name="Recipe" Id="30" Func="FC16" Slave="1" Start="10" /&gt;
/// </code>
/// </example>
public class ModbusWriteConfig : NamedWriteConfig
{
    /// <summary>Write function: FC5, FC6, FC15, or FC16 (required).</summary>
    [XmlAttribute, ConfigRequired] public ModbusFunction Func { get; set; } = ModbusFunction.MULTIWORDWRITE_F16;
    /// <summary>Slave address. 0 to 247 for RTU (0 is broadcast), 0 to 255 for TCP (required).</summary>
    [XmlAttribute, ConfigRequired] public int Slave { get; set; } = 1;
    /// <summary>Start address, 0 to 65535 (default: 0).</summary>
    [XmlAttribute] public int Start { get; set; }

    // 주소 속성을 검사한다. 실패하면 FormatException. (값은 호출할 때 검사)
    internal void Validate(string label, int minSlave, int maxSlave)
    {
        if (Func is not (ModbusFunction.BITWRITE_F5 or ModbusFunction.WORDWRITE_F6 or ModbusFunction.MULTIBITWRITE_F15 or ModbusFunction.MULTIWORDWRITE_F16))
            throw XmlConfig.Invalid(label, nameof(Func), XmlConfig.EnumName(Func), "FC5, FC6, FC15, FC16");
        XmlConfig.Range(label, nameof(Slave), Slave, minSlave, maxSlave);
        XmlConfig.Range(label, nameof(Start), Start, 0, 65535);
    }

    // 값 하나만 허용하는 함수(FC5, FC6)용. 개수가 1이 아니면 ArgumentException.
    internal static long Single(IReadOnlyList<long> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count != 1) throw new ArgumentException("이 함수는 값을 하나만 쓸 수 있습니다.", nameof(values));
        return values[0];
    }

    // 0/1 값을 bool로 바꾼다. 아니면 ArgumentException.
    internal static bool Bit(long value)
        => value is 0 or 1 ? value == 1 : throw new ArgumentException($"비트 값은 0 또는 1이어야 합니다. ({value})", "value");

    // -32768~65535 값을 int로 확인한다. 범위 밖이면 ArgumentException.
    internal static int Word(long value)
        => value is >= -32768 and <= 65535 ? (int)value : throw new ArgumentException($"워드 값은 -32768~65535여야 합니다. ({value})", "value");

    internal static bool[] Bits(IReadOnlyList<long> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return [.. values.Select(Bit)];
    }

    internal static int[] Words(IReadOnlyList<long> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return [.. values.Select(Word)];
    }
}
