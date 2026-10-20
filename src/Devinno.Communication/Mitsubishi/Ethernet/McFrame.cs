namespace Devinno.Communication.Mitsubishi.Ethernet;

/// <summary>MC protocol Ethernet frame (binary, TCP).</summary>
public enum McFrame
{
    /// <summary>3E frame (QnA compatible, Ethernet built into Q/L/FX5 CPUs, commands 0401/1401). X/Y default to hexadecimal.</summary>
    E3,
    /// <summary>1E frame (A compatible, FX3 Ethernet such as FX3U-ENET(-ADP/-L), commands 00H to 03H). X/Y default to octal.</summary>
    E1,
}
