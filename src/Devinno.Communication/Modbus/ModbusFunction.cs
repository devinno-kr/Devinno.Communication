using System.Xml.Serialization;

namespace Devinno.Communication.Modbus;

/// <summary>Modbus function codes.</summary>
/// <remarks>In XML configuration they are written as <c>FC1</c>, <c>FC3</c>, and so on.</remarks>
public enum ModbusFunction : byte
{
    /// <summary>FC1 read coils</summary>
    [XmlEnum("FC1")] BITREAD_F1 = 1,
    /// <summary>FC2 read discrete inputs</summary>
    [XmlEnum("FC2")] BITREAD_F2 = 2,
    /// <summary>FC3 read holding registers</summary>
    [XmlEnum("FC3")] WORDREAD_F3 = 3,
    /// <summary>FC4 read input registers</summary>
    [XmlEnum("FC4")] WORDREAD_F4 = 4,
    /// <summary>FC5 write single coil</summary>
    [XmlEnum("FC5")] BITWRITE_F5 = 5,
    /// <summary>FC6 write single register</summary>
    [XmlEnum("FC6")] WORDWRITE_F6 = 6,
    /// <summary>FC15 write multiple coils</summary>
    [XmlEnum("FC15")] MULTIBITWRITE_F15 = 15,
    /// <summary>FC16 write multiple registers</summary>
    [XmlEnum("FC16")] MULTIWORDWRITE_F16 = 16,
    /// <summary>FC26 set a bit within a register (Devinno extension)</summary>
    [XmlEnum("FC26")] WORDBITSET_F26 = 26,
}
