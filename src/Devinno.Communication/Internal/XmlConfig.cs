using System.Globalization;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using System.Xml.Serialization;

namespace Devinno.Communication.Internal;

// 설정 모델의 XML 속성 중 반드시 있어야 하는 속성 표시
[AttributeUsage(AttributeTargets.Property)]
internal sealed class ConfigRequiredAttribute : Attribute { }

// 설정 모델(…Config) ↔ XML 변환. XmlSerializer를 엄격 모드로 쓰고, 오류는 요소·속성 이름을 담은 FormatException으로 바꾼다.
internal static class XmlConfig
{
    static readonly XmlSerializerNamespaces EmptyNs = new([XmlQualifiedName.Empty]);

    #region Error
    public static FormatException Invalid(string element, string attribute, string value, string detail)
        => new($"<{element}> 요소의 {attribute} 속성 값이 잘못되었습니다: \"{value}\" ({detail})");

    public static void Range(string element, string attribute, int value, int min, int max)
    {
        if (value < min || value > max)
            throw Invalid(element, attribute, value.ToString(CultureInfo.InvariantCulture), $"범위 {min}~{max}");
    }

    public static void NotEmpty(string element, string attribute, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw Invalid(element, attribute, value ?? "", "비어 있으면 안 됨");
    }

    // 인자 검사 예외(ArgumentException)를 XML 오류로 바꾼다. attributes: 인자 이름 → (XML 속성 이름, 값)
    public static FormatException Invalid(string element, ArgumentException ex, IReadOnlyDictionary<string, (string Attribute, string Value)> attributes)
    {
        var (attr, value) = ex.ParamName != null && attributes.TryGetValue(ex.ParamName, out var a) ? a : (ex.ParamName ?? "?", "");
        var msg = ex.Message;
        int i = msg.IndexOf(" (Parameter", StringComparison.Ordinal);
        return Invalid(element, attr, value, i >= 0 ? msg[..i] : msg);
    }

    // 오류 메시지용 요소 표시. Write 요소는 Name, 그 외는 Id 속성이 있으면 함께 표시한다(예: Write Name="Run", Auto Id="3").
    static string Label(XElement e)
        => e.Name.LocalName == "Write" && e.Attribute("Name") is { } name ? $"Write Name=\"{name.Value}\""
         : e.Attribute("Id") is { } id ? $"{e.Name.LocalName} Id=\"{id.Value}\"" : e.Name.LocalName;
    #endregion

    #region Serialize / Deserialize
    public static XElement Serialize<T>(T model) where T : class
    {
        var doc = new XDocument();
        using (var w = doc.CreateWriter()) new XmlSerializer(typeof(T)).Serialize(w, model, EmptyNs);
        var root = doc.Root!;
        root.Remove();
        return root;
    }

    public static T Deserialize<T>(XElement element) where T : class
    {
        ArgumentNullException.ThrowIfNull(element);
        var types = ElementTypes(typeof(T));
        var rootName = types[typeof(T)];
        if (element.Name.LocalName != rootName)
            throw new FormatException($"루트 요소는 <{rootName}> 이어야 합니다. 현재: <{element.Name.LocalName}>");

        // 줄·칸 위치로 오류 속성을 찾기 위해 같은 텍스트를 위치 정보와 함께 다시 읽는다
        var text = element.ToString(SaveOptions.DisableFormatting);
        var doc = XElement.Parse(text, LoadOptions.SetLineInfo);

        var ser = new XmlSerializer(typeof(T));
        FormatException? unknown = null;
        ser.UnknownAttribute += (_, e) => unknown ??= new FormatException(
            $"<{NameOf(types, e.ObjectBeingDeserialized)}> 요소에 알 수 없는 속성 {e.Attr.Name}이(가) 있습니다. 사용할 수 있는 속성: {string.Join(", ", AttributeNames(e.ObjectBeingDeserialized?.GetType()))}");
        ser.UnknownElement += (_, e) => unknown ??= new FormatException(
            $"<{NameOf(types, e.ObjectBeingDeserialized)}> 안에 알 수 없는 요소 <{e.Element.Name}>이(가) 있습니다. 사용할 수 있는 자식 요소: {ChildNames(e.ObjectBeingDeserialized?.GetType())}");

        using var reader = XmlReader.Create(new StringReader(text));
        T result;
        try { result = (T)ser.Deserialize(reader)!; }
        catch (InvalidOperationException ex)
        {
            var li = (IXmlLineInfo)reader;
            throw ConversionError(doc, types, li.LineNumber, li.LinePosition, ex);
        }

        if (unknown != null) throw unknown;
        CheckRequired(doc, types);
        return result;
    }

    static FormatException ConversionError(XElement doc, Dictionary<Type, string> types, int line, int pos, InvalidOperationException ex)
    {
        var attr = doc.DescendantsAndSelf().SelectMany(e => e.Attributes())
            .FirstOrDefault(a => ((IXmlLineInfo)a).LineNumber == line && ((IXmlLineInfo)a).LinePosition == pos);
        var type = attr == null ? null : types.FirstOrDefault(kv => kv.Value == attr.Parent!.Name.LocalName).Key;
        var prop = type == null ? null : FindAttributeProperty(type, attr!.Name.LocalName);
        if (attr == null || prop == null)
            return new FormatException($"XML을 해석할 수 없습니다 ({line},{pos}): {ex.InnerException?.Message ?? ex.Message}", ex);

        var t = prop.PropertyType;
        var detail = t.IsEnum ? string.Join(", ", EnumNames(t))
                   : t == typeof(bool) ? "true 또는 false"
                   : t == typeof(int) ? "정수가 아님"
                   : "형식이 맞지 않음";
        return Invalid(Label(attr.Parent!), attr.Name.LocalName, attr.Value, detail);
    }

    static void CheckRequired(XElement doc, Dictionary<Type, string> types)
    {
        foreach (var e in doc.DescendantsAndSelf())
        {
            var type = types.FirstOrDefault(kv => kv.Value == e.Name.LocalName).Key;
            if (type == null) continue;
            foreach (var p in type.GetProperties().Where(p => p.GetCustomAttribute<ConfigRequiredAttribute>() != null))
            {
                var name = AttributeName(p);
                if (e.Attribute(name) == null) throw Invalid(Label(e), name, "", "필수 속성");
            }
        }
    }
    #endregion

    #region Schema
    // 루트 모델의 XSD. XmlSerializer 기본 출력의 필수/선택을 실제 규칙([ConfigRequired], 기본값)에 맞게 고친다.
    public static string Schema(Type root)
    {
        var schemas = new XmlSchemas();
        new XmlSchemaExporter(schemas).ExportTypeMapping(new XmlReflectionImporter().ImportTypeMapping(root));
        // 추상 베이스 타입(예: CNetAutoConfig)도 스키마에 자기 complexType이 있으므로 함께 보정한다
        var types = ElementTypes(root).Keys
            .SelectMany(t => { var list = new List<Type>(); for (var b = t; b != null && b != typeof(object); b = b.BaseType) list.Add(b); return list; })
            .Distinct().ToDictionary(t => t.Name);

        var sw = new StringWriter();
        foreach (XmlSchema schema in schemas)
        {
            foreach (var ct in schema.Items.OfType<XmlSchemaComplexType>())
            {
                if (!types.TryGetValue(ct.Name ?? "", out var type)) continue;
                var defaults = type.IsAbstract ? null : Activator.CreateInstance(type);
                // 파생 타입의 자기 속성은 complexContent extension 안에 들어간다
                var attributes = ct.ContentModel?.Content is XmlSchemaComplexContentExtension ext ? ext.Attributes : ct.Attributes;
                foreach (var a in attributes.OfType<XmlSchemaAttribute>())
                {
                    var p = FindAttributeProperty(type, a.Name ?? "");
                    if (p == null) continue;
                    if (p.GetCustomAttribute<ConfigRequiredAttribute>() != null) { a.Use = XmlSchemaUse.Required; continue; }
                    a.Use = XmlSchemaUse.Optional;
                    bool hasSpecified = type.GetProperty(p.Name + "Specified") != null;
                    if (!hasSpecified && defaults != null && ToXmlText(p.GetValue(defaults)) is string d) a.DefaultValue = d;
                }
            }
            schema.Write(sw);
        }
        return sw.ToString();
    }

    static string? ToXmlText(object? v) => v switch
    {
        null => null,
        bool b => b ? "true" : "false",
        Enum e => EnumName(e),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString(),
    };
    #endregion

    #region Reflection
    // 루트 모델과 자식 모델 타입 → XML 요소 이름
    static Dictionary<Type, string> ElementTypes(Type root)
    {
        var map = new Dictionary<Type, string> { [root] = root.GetCustomAttribute<XmlRootAttribute>()?.ElementName ?? root.Name };
        AddChildren(root, map);
        return map;
    }

    static void AddChildren(Type type, Dictionary<Type, string> map)
    {
        foreach (var p in type.GetProperties())
        {
            foreach (var xe in p.GetCustomAttributes<XmlElementAttribute>())
            {
                var t = xe.Type ?? (p.PropertyType.IsGenericType ? p.PropertyType.GetGenericArguments()[0] : p.PropertyType);
                if (map.ContainsKey(t)) continue;
                map[t] = string.IsNullOrEmpty(xe.ElementName) ? p.Name : xe.ElementName;
                AddChildren(t, map);
            }
        }
    }

    static string NameOf(Dictionary<Type, string> types, object? o)
        => o != null && types.TryGetValue(o.GetType(), out var n) ? n : o?.GetType().Name ?? "?";

    static string AttributeName(PropertyInfo p)
        => p.GetCustomAttribute<XmlAttributeAttribute>() is { AttributeName: { Length: > 0 } n } ? n : p.Name;

    static PropertyInfo? FindAttributeProperty(Type type, string name)
        => type.GetProperties().FirstOrDefault(p => p.GetCustomAttribute<XmlAttributeAttribute>() != null && AttributeName(p) == name);

    static IEnumerable<string> AttributeNames(Type? type)
        => type == null ? [] : type.GetProperties().Where(p => p.GetCustomAttribute<XmlAttributeAttribute>() != null).Select(AttributeName);

    static string ChildNames(Type? type)
    {
        var names = type == null ? [] : type.GetProperties()
            .SelectMany(p => p.GetCustomAttributes<XmlElementAttribute>().Select(xe => string.IsNullOrEmpty(xe.ElementName) ? p.Name : xe.ElementName))
            .Distinct().ToList();
        return names.Count == 0 ? "없음" : string.Join(", ", names);
    }

    // enum 값의 XML 표기(XmlEnum 이름이 있으면 그 이름)
    public static string EnumName(Enum value)
        => value.GetType().GetField(value.ToString())?.GetCustomAttribute<XmlEnumAttribute>()?.Name ?? value.ToString();

    public static IEnumerable<string> EnumNames(Type enumType)
        => enumType.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => f.GetCustomAttribute<XmlEnumAttribute>()?.Name ?? f.Name);
    #endregion
}
