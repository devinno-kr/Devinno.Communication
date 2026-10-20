using Devinno.Communication.LS.CNet;
using Devinno.Communication.LS.FEnet;
using Devinno.Communication.Mitsubishi.Ethernet;
using Devinno.Communication.Mitsubishi.Serial;
using Devinno.Communication.Modbus.RTU;
using Devinno.Communication.Modbus.TCP;
using Devinno.Communication.Mqtt;
using Devinno.Communication.Omron.Ethernet;
using Devinno.Communication.Omron.Serial;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Secs.Hsms;
using Devinno.Communication.Secs.Serial;
using Devinno.Communication.Siemens;
using Devinno.Communication.TextComm.RTU;
using Devinno.Communication.TextComm.TCP;

namespace Devinno.Communication.Tests;

/// <summary>Keeps the committed schemas/*.xsd files in sync with ConfigSchema output.</summary>
public class SchemaFilesTests
{
    private static readonly Dictionary<string, Func<string>> Schemas = new()
    {
        ["ModbusRTUMaster"] = ConfigSchema.Get<ModbusRTUMasterConfig>,
        ["ModbusTCPMaster"] = ConfigSchema.Get<ModbusTCPMasterConfig>,
        ["ModbusRTUSlave"] = ConfigSchema.Get<ModbusRTUSlaveConfig>,
        ["ModbusTCPSlave"] = ConfigSchema.Get<ModbusTCPSlaveConfig>,
        ["LsCNet"] = ConfigSchema.Get<LsCNetConfig>,
        ["LsFEnet"] = ConfigSchema.Get<LsFEnetConfig>,
        ["McSerial"] = ConfigSchema.Get<McSerialConfig>,
        ["McEthernet"] = ConfigSchema.Get<McEthernetConfig>,
        ["S7Ethernet"] = ConfigSchema.Get<S7EthernetConfig>,
        ["FinsUdp"] = ConfigSchema.Get<FinsUdpConfig>,
        ["FinsTcp"] = ConfigSchema.Get<FinsTcpConfig>,
        ["FinsSerial"] = ConfigSchema.Get<FinsSerialConfig>,
        ["TextCommRTUMaster"] = ConfigSchema.Get<TextCommRTUMasterConfig>,
        ["TextCommTCPMaster"] = ConfigSchema.Get<TextCommTCPMasterConfig>,
        ["TextCommRTUSlave"] = ConfigSchema.Get<TextCommRTUSlaveConfig>,
        ["TextCommTCPSlave"] = ConfigSchema.Get<TextCommTCPSlaveConfig>,
        ["MQClient"] = ConfigSchema.Get<MQClientConfig>,
        ["SecsHsms"] = ConfigSchema.Get<SecsHsmsConfig>,
        ["SecsSerial"] = ConfigSchema.Get<SecsSerialConfig>,
        ["GemEquipment"] = ConfigSchema.Get<GemEquipmentConfig>,
        ["GemHost"] = ConfigSchema.Get<GemHostConfig>,
    };

    public static IEnumerable<object[]> Roots() => Schemas.Keys.Select(k => new object[] { k });

    private static string Norm(string s) => s.Replace("\r\n", "\n").Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Devinno.Communication.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Devinno.Communication.sln not found above the test output folder.");
    }

    [Fact]
    public void SchemaFiles_CoverAll21Roots() => Assert.Equal(21, Schemas.Count);

    [Fact]
    public void SchemaFiles_FolderHasExactlyTheExpectedFiles()
    {
        var actual = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "schemas"), "*.xsd")
            .Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var expected = Schemas.Keys.Select(k => k + ".xsd").OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(Roots))]
    public void SchemaFiles_MatchConfigSchemaOutput(string root)
    {
        var expected = Norm(Schemas[root]());
        if (Environment.GetEnvironmentVariable("DEVINNO_WRITE_SCHEMAS") == "1")
        {
            var dir = Path.Combine(FindRepoRoot(), "schemas");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, root + ".xsd"), expected.Replace("\n", "\r\n"), new System.Text.UTF8Encoding(false));
            return;
        }
        var path = Path.Combine(AppContext.BaseDirectory, "schemas", root + ".xsd");
        Assert.True(File.Exists(path), $"schemas/{root}.xsd is missing. Rewrite with: DEVINNO_WRITE_SCHEMAS=1 dotnet test --filter SchemaFiles");
        Assert.True(expected == Norm(File.ReadAllText(path)),
            $"schemas/{root}.xsd is out of date. Rewrite with: DEVINNO_WRITE_SCHEMAS=1 dotnet test --filter SchemaFiles");
    }

    [Theory]
    [MemberData(nameof(Roots))]
    public void SchemaFiles_LoadWithXmlSchemaRead(string root)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "schemas", root + ".xsd");
        using var fs = File.OpenRead(path);
        var schema = System.Xml.Schema.XmlSchema.Read(fs, null)!;
        var set = new System.Xml.Schema.XmlSchemaSet();
        set.Add(schema);
        set.Compile();
        Assert.True(set.IsCompiled);
    }

    [Theory]
    [MemberData(nameof(Roots))]
    public void SchemaFiles_AreDeterministic(string root) => Assert.Equal(Schemas[root](), Schemas[root]());
}
