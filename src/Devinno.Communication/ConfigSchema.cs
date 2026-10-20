using Devinno.Communication.Internal;

namespace Devinno.Communication;

/// <summary>
/// Builds the XML schema (XSD) of a configuration class (...Config).
/// </summary>
/// <remarks>
/// Use it to describe the configuration file format to an AI or an XML editor. Required attributes are marked use="required", and optional attributes are shown with their default values.
/// Value ranges (for example, Timeout 1 to 60000) are not part of the schema; they are checked at load time.
/// </remarks>
/// <example>
/// <code>
/// string xsd = ConfigSchema.Get&lt;ModbusRTUMasterConfig&gt;();
/// File.WriteAllText("ModbusRTUMaster.xsd", xsd);
/// </code>
/// </example>
public static class ConfigSchema
{
    /// <summary>Builds the XSD string for a configuration class.</summary>
    /// <typeparam name="T">The configuration class (for example, <c>ModbusRTUMasterConfig</c>).</typeparam>
    /// <returns>The XSD document as a string.</returns>
    public static string Get<T>() where T : class, new() => XmlConfig.Schema(typeof(T));
}
