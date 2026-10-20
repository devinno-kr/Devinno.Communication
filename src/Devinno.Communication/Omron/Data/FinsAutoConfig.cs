using System.ComponentModel;
using System.Xml.Serialization;
using Devinno.Communication.Internal;

namespace Devinno.Communication.Omron.Data;

/// <summary>Properties common to the Auto (polling) items of an Omron FINS master (FinsUdp, FinsTcp, FinsSerial).</summary>
public abstract class FinsAutoConfig
{
    int timeout;

    /// <summary>Message ID. Returned as the MessageID of the response event (required).</summary>
    [XmlAttribute, ConfigRequired] public int Id { get; set; }
    /// <summary>Timeout of this item in ms, 1 to 60000. If not specified, the Timeout of the communication class is used.</summary>
    [XmlAttribute] public int Timeout { get => timeout; set { timeout = value; TimeoutSpecified = true; } }
    /// <summary>Whether <see cref="Timeout"/> was specified.</summary>
    [XmlIgnore, EditorBrowsable(EditorBrowsableState.Never)] public bool TimeoutSpecified { get; set; }

    // 검사를 마친 Auto 항목 (각 통신 클래스가 자기 Work로 바꾼다)
    internal readonly record struct Item(int Id, FinsRequest Request, int? Timeout);

    // Auto 항목들을 모두 검사해 바꾼다. 틀린 값은 요소·속성 이름(예: Read Id="3"의 Addresses)을 담은 FormatException.
    internal static List<Item> Parse(IEnumerable<FinsAutoConfig>? autos, int maxItems)
    {
        var list = new List<Item>();
        foreach (var a in autos ?? [])
        {
            var label = $"{(a is FinsReadConfig ? "Read" : "ReadBlock")} Id=\"{a.Id}\"";
            if (a.TimeoutSpecified) XmlConfig.Range(label, nameof(Timeout), a.Timeout, 1, 60000);
            int? t = a.TimeoutSpecified ? a.Timeout : null;
            try
            {
                switch (a)
                {
                    case FinsReadConfig r:
                        list.Add(new Item(a.Id, FinsRequest.Read(FinsAddress.SplitNames(r.Addresses), maxItems), t));
                        break;
                    case FinsReadBlockConfig b:
                        list.Add(new Item(a.Id, FinsRequest.ReadBlock(b.Address, b.Count), t));
                        break;
                    default:
                        throw new FormatException($"알 수 없는 Auto 항목: {a.GetType().Name}");
                }
            }
            catch (ArgumentException ex)
            {
                throw XmlConfig.Invalid(label, ex, new Dictionary<string, (string, string)>
                {
                    ["addresses"] = ("Addresses", (a as FinsReadConfig)?.Addresses ?? ""),
                    ["address"] = ("Address", (a as FinsReadBlockConfig)?.Address ?? ""),
                    ["count"] = ("Count", (a as FinsReadBlockConfig)?.Count.ToString() ?? ""),
                });
            }
        }
        return list;
    }

    // Auto 작업 하나를 설정 항목으로 (ToConfig용)
    internal static FinsAutoConfig From(int id, FinsCommand command, FinsAddress[] items, int count, int? timeout)
    {
        FinsAutoConfig a = command == FinsCommand.Read
            ? new FinsReadConfig { Id = id, Addresses = string.Join(",", items.Select(x => x.Name)) }
            : new FinsReadBlockConfig { Id = id, Address = items[0].Name, Count = count };
        if (timeout is int t) a.Timeout = t;
        return a;
    }
}

/// <summary>A random (individual) read (Read, 0104) Auto item. In XML it is the &lt;Read&gt; element.</summary>
public class FinsReadConfig : FinsAutoConfig
{
    /// <summary>Word and bit addresses separated by commas (e.g., "D100,CIO0.05,W10"). 1 to 167 for FinsUdp and FinsTcp, 1 to 134 for FinsSerial (required).</summary>
    [XmlAttribute, ConfigRequired] public string Addresses { get; set; } = "";
}

/// <summary>A block read (ReadBlock, 0101) Auto item. In XML it is the &lt;ReadBlock&gt; element.</summary>
public class FinsReadBlockConfig : FinsAutoConfig
{
    /// <summary>Start address. A word address reads words and a bit address reads bits (e.g., "D100", "CIO100.00") (required).</summary>
    [XmlAttribute, ConfigRequired] public string Address { get; set; } = "";
    /// <summary>Number of items to read, 1 to 65535. The last address must be within the memory area (required).</summary>
    [XmlAttribute, ConfigRequired] public int Count { get; set; } = 1;
}
