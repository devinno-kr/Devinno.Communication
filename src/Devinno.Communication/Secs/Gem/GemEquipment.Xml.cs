using System.Globalization;
using System.Xml.Linq;
using Devinno.Communication.Internal;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Hsms;
using Devinno.Communication.Secs.Serial;

namespace Devinno.Communication.Secs.Gem;

// 설정(GemEquipmentConfig) ↔ XML: 전송 설정 + GEM 설정 + 장비 정의를 파일 하나에
public partial class GemEquipment
{
    const string Root = nameof(GemEquipment);

    /// <summary>Builds the current settings (transport settings, GEM settings, equipment definitions) as a configuration object.</summary>
    /// <returns>The configuration object (the transport settings are always included)</returns>
    /// <exception cref="InvalidOperationException">A definition has a value that cannot be written to XML (an array or a non-empty list value)</exception>
    public GemEquipmentConfig ToConfig()
    {
        lock (lifeLock)
        {
            var c = new GemEquipmentConfig
            {
                ModelName = modelName,
                SoftwareRevision = softwareRevision,
                VidFormat = GemItems.FormatName(vidFormat),
                CeidFormat = GemItems.FormatName(ceidFormat),
                RptidFormat = GemItems.FormatName(rptidFormat),
                AlidFormat = GemItems.FormatName(alidFormat),
                DataidFormat = GemItems.FormatName(dataidFormat),
                InitialCommunicationState = initialCommunicationState,
                InitiateCommunication = initiateCommunication,
                InitialControlState = initialControlState,
                OnlineFailedState = onlineFailedState,
                EventsEnabledByDefault = eventsEnabledByDefault,
                AlarmsEnabledByDefault = alarmsEnabledByDefault,
                EventReplyExpected = eventReplyExpected,
                AlarmReplyExpected = alarmReplyExpected,
                TerminalReplyExpected = terminalReplyExpected,
                CommandReplyTimeout = commandReplyTimeout,
                MaxPendingSends = maxPendingSends,
                MaxPendingEvents = maxPendingEvents,
            };
            if (transport.Target is SecsHsms h) c.Hsms = h.ToConfig();
            else c.Serial = ((SecsSerial)transport.Target).ToConfig();
            foreach (var s in Enum.GetValues<GemStandard>())
                if (StandardIds.Get(s) is { } id) (c.Standard ??= new()).Set(s, id.ToString());
            static string? Initial(SecsItem v, SecsFormat f) => v.Equals(GemItems.Empty(f)) ? null : Text(v);
            static string Text(SecsItem v) => GemItems.ValueText(v) ?? throw new InvalidOperationException($"XML로 쓸 수 없는 값입니다(배열·List 값은 코드로 정의): {v.ToSml()}");
            foreach (var v in definitions.StatusVariables)
                c.StatusVariables.Add(new() { Id = v.Id.ToString(), Name = v.Name, Format = GemItems.FormatName(v.Format), Units = v.Units, InitialValue = Initial(v.InitialValue, v.Format) });
            foreach (var v in definitions.DataVariables)
                c.DataVariables.Add(new() { Id = v.Id.ToString(), Name = v.Name, Format = GemItems.FormatName(v.Format), Units = v.Units, InitialValue = Initial(v.InitialValue, v.Format) });
            foreach (var v in definitions.Constants)
                c.EquipmentConstants.Add(new()
                {
                    Id = v.Id.ToString(), Name = v.Name, Format = GemItems.FormatName(v.Format), Default = Text(v.Default),
                    Min = v.Min == null ? null : Text(v.Min), Max = v.Max == null ? null : Text(v.Max), Units = v.Units,
                });
            foreach (var e in definitions.Events)
                c.CollectionEvents.Add(new() { Id = e.Id.ToString(), Name = e.Name, Vids = string.Join(",", e.DataVariables) });
            foreach (var a in definitions.Alarms)
                c.Alarms.Add(new() { Id = a.Id.ToString(), Text = a.Text, Category = a.Category, SetCeid = a.SetCeid.ToString(), ClearCeid = a.ClearCeid.ToString() });
            foreach (var r in definitions.Commands)
                c.RemoteCommands.Add(new()
                {
                    Name = r.Name,
                    Parameters = [.. r.Parameters.Select(p => new GemCommandParameterConfig { Name = p.Name, Format = p.Format is { } f ? GemItems.FormatName(f) : null })],
                });
            foreach (var (s, f) in definitions.Messages) c.Messages.Add(new() { Stream = s, Function = f });
            return c;
        }
    }

    /// <summary>Applies a configuration object (replaces the transport settings, GEM settings, and all equipment definitions).</summary>
    /// <remarks>Everything is checked before anything is applied, so if any part is wrong nothing changes. The definition checks (ID overlap, format range, unknown VID reference) are the same as at Start. If there are no transport settings the transport is left as it is; if there are, the Role is set to Equipment.</remarks>
    /// <param name="config">Configuration to apply</param>
    /// <exception cref="ArgumentNullException">config is null</exception>
    /// <exception cref="InvalidOperationException">Called while running (<see cref="IsStart"/>)</exception>
    /// <exception cref="FormatException">A value is wrong, the kind of transport settings differs from the wrapped transport, or a definition is wrong (the message contains the element and attribute names)</exception>
    public void ApplyConfig(GemEquipmentConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (lifeLock)
        {
            if (IsStart) throw new InvalidOperationException("통신 중에는 설정을 바꿀 수 없습니다. Stop() 후 호출하세요.");
            // 1. 전체 검사(아무것도 바꾸지 않음)
            if (config.Hsms != null && config.Serial != null) throw new FormatException($"<{Root}>에는 <SecsHsms>와 <SecsSerial> 중 하나만 둘 수 있습니다.");
            if (config.Hsms != null && transport.Target is not SecsHsms) throw new FormatException($"<{Root}>의 전송 요소 <SecsHsms>가 감싼 전송(SecsSerial)과 다릅니다.");
            if (config.Serial != null && transport.Target is not SecsSerial) throw new FormatException($"<{Root}>의 전송 요소 <SecsSerial>가 감싼 전송(SecsHsms)과 다릅니다.");
            config.Hsms?.Validate();
            config.Serial?.Validate();

            string Text20Attr(string? v, string attr)
            {
                try { return Text20(v ?? "", attr); }
                catch (ArgumentException ex) { throw XmlConfig.Invalid(Root, attr, v ?? "", Detail(ex)); }
            }
            var mdln = Text20Attr(config.ModelName, nameof(config.ModelName));
            var softrev = Text20Attr(config.SoftwareRevision, nameof(config.SoftwareRevision));
            var vid = IdFormatAttr(config.VidFormat, nameof(config.VidFormat));
            var ceid = IdFormatAttr(config.CeidFormat, nameof(config.CeidFormat));
            var rptid = IdFormatAttr(config.RptidFormat, nameof(config.RptidFormat));
            var alid = IdFormatAttr(config.AlidFormat, nameof(config.AlidFormat));
            var dataid = IdFormatAttr(config.DataidFormat, nameof(config.DataidFormat));
            if (config.InitialControlState is not (GemControlState.EquipmentOffline or GemControlState.HostOffline or GemControlState.OnlineLocal or GemControlState.OnlineRemote))
                throw XmlConfig.Invalid(Root, nameof(config.InitialControlState), config.InitialControlState.ToString(), "EquipmentOffline, HostOffline, OnlineLocal, OnlineRemote");
            if (config.OnlineFailedState is not (GemControlState.EquipmentOffline or GemControlState.HostOffline))
                throw XmlConfig.Invalid(Root, nameof(config.OnlineFailedState), config.OnlineFailedState.ToString(), "EquipmentOffline, HostOffline");
            XmlConfig.Range(Root, nameof(config.CommandReplyTimeout), config.CommandReplyTimeout, 1000, 110000);
            XmlConfig.Range(Root, nameof(config.MaxPendingSends), config.MaxPendingSends, 1, 100000);
            XmlConfig.Range(Root, nameof(config.MaxPendingEvents), config.MaxPendingEvents, 1, 1000000);

            var ids = new GemStandardIds();
            foreach (var s in Enum.GetValues<GemStandard>())
            {
                var text = config.Standard?.Get(s);
                if (text != null) ids.Set(s, ParseId("Standard", s.ToString(), text, s >= GemStandard.EquipmentOffline ? ceid : vid));
            }
            var defs = BuildDefinitions(config, vid, ceid, alid);
            try { GemCatalog.Build(defs, ids, vid, ceid, rptid, alid, dataid); }
            catch (InvalidOperationException ex) { throw new FormatException($"<{Root}> 정의 오류: {ex.Message}", ex); }

            // 2. 적용
            if (config.Hsms != null)
            {
                config.Hsms.Role = SecsRole.Equipment;
                ((SecsHsms)transport.Target).ApplyConfig(config.Hsms);
            }
            if (config.Serial != null)
            {
                config.Serial.Role = SecsRole.Equipment;
                ((SecsSerial)transport.Target).ApplyConfig(config.Serial);
            }
            modelName = mdln;
            softwareRevision = softrev;
            (vidFormat, ceidFormat, rptidFormat, alidFormat, dataidFormat) = (vid, ceid, rptid, alid, dataid);
            initialCommunicationState = config.InitialCommunicationState;
            initiateCommunication = config.InitiateCommunication;
            initialControlState = config.InitialControlState;
            onlineFailedState = config.OnlineFailedState;
            eventsEnabledByDefault = config.EventsEnabledByDefault;
            alarmsEnabledByDefault = config.AlarmsEnabledByDefault;
            eventReplyExpected = config.EventReplyExpected;
            alarmReplyExpected = config.AlarmReplyExpected;
            terminalReplyExpected = config.TerminalReplyExpected;
            commandReplyTimeout = config.CommandReplyTimeout;
            maxPendingSends = config.MaxPendingSends;
            maxPendingEvents = config.MaxPendingEvents;
            foreach (var s in Enum.GetValues<GemStandard>()) StandardIds.Set(s, ids.Get(s));
            definitions.Clear();
            definitions.StatusVariables.AddRange(defs.StatusVariables);
            definitions.DataVariables.AddRange(defs.DataVariables);
            definitions.Constants.AddRange(defs.Constants);
            definitions.Events.AddRange(defs.Events);
            definitions.Alarms.AddRange(defs.Alarms);
            definitions.Commands.AddRange(defs.Commands);
            definitions.Messages.AddRange(defs.Messages);
        }
    }

    static GemDefinitionSet BuildDefinitions(GemEquipmentConfig c, SecsFormat vid, SecsFormat ceid, SecsFormat alid)
    {
        var d = new GemDefinitionSet();
        foreach (var v in c.StatusVariables)
        {
            var label = $"SV Id=\"{v.Id}\"";
            var f = FormatAttr(label, nameof(v.Format), v.Format);
            Define(label, () => d.Add(new GemStatusVariable(ParseId(label, nameof(v.Id), v.Id, vid), v.Name, f, v.Units, ValueAttr(label, nameof(v.InitialValue), v.InitialValue, f))));
        }
        foreach (var v in c.DataVariables)
        {
            var label = $"DV Id=\"{v.Id}\"";
            var f = FormatAttr(label, nameof(v.Format), v.Format);
            Define(label, () => d.Add(new GemDataVariable(ParseId(label, nameof(v.Id), v.Id, vid), v.Name, f, v.Units, ValueAttr(label, nameof(v.InitialValue), v.InitialValue, f))));
        }
        foreach (var v in c.EquipmentConstants)
        {
            var label = $"EC Id=\"{v.Id}\"";
            var f = FormatAttr(label, nameof(v.Format), v.Format);
            Define(label, () => d.Add(new GemEquipmentConstant(ParseId(label, nameof(v.Id), v.Id, vid), v.Name, ValueAttr(label, nameof(v.Default), v.Default, f)!,
                ValueAttr(label, nameof(v.Min), v.Min, f), ValueAttr(label, nameof(v.Max), v.Max, f), v.Units)));
        }
        foreach (var e in c.CollectionEvents)
        {
            var label = $"CE Id=\"{e.Id}\"";
            var vids = (e.Vids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => ParseId(label, nameof(e.Vids), t, vid)).ToList();
            Define(label, () => d.Add(new GemCollectionEvent(ParseId(label, nameof(e.Id), e.Id, ceid), e.Name, vids)));
        }
        foreach (var a in c.Alarms)
        {
            var label = $"Alarm Id=\"{a.Id}\"";
            Define(label, () => d.Add(new GemAlarm(ParseId(label, nameof(a.Id), a.Id, alid), a.Text, a.Category,
                ParseId(label, nameof(a.SetCeid), a.SetCeid, ceid), ParseId(label, nameof(a.ClearCeid), a.ClearCeid, ceid))));
        }
        foreach (var r in c.RemoteCommands)
        {
            var label = $"Command Name=\"{r.Name}\"";
            var ps = r.Parameters.Select(p => new { p.Name, Format = p.Format == null ? (SecsFormat?)null : FormatAttr($"Param Name=\"{p.Name}\"", nameof(p.Format), p.Format) }).ToList();
            Define(label, () => d.Add(new GemRemoteCommand(r.Name, ps.Select(p => new GemCommandParameter(p.Name, p.Format)))));
        }
        foreach (var m in c.Messages) Define($"Message Stream=\"{m.Stream}\" Function=\"{m.Function}\"", () => d.AddMessage(m.Stream, m.Function));
        return d;
    }

    // 정의 생성자·등록의 인자 오류를 요소 이름이 든 FormatException으로
    static void Define(string label, Action add)
    {
        try { add(); }
        catch (ArgumentException ex) { throw new FormatException($"<{label}> 정의가 잘못되었습니다: {Detail(ex)}", ex); }
    }

    static string Detail(ArgumentException ex)
    {
        int i = ex.Message.IndexOf(" (Parameter", StringComparison.Ordinal);
        return i >= 0 ? ex.Message[..i] : ex.Message;
    }

    static SecsFormat IdFormatAttr(string? text, string attr)
        => GemItems.TryParseFormatName(text, out var f) && Array.IndexOf(GemItems.IdFormats, f) >= 0 ? f
            : throw XmlConfig.Invalid(Root, attr, text ?? "", "U1, U2, U4, U8, I1, I2, I4, I8, A");

    static SecsFormat FormatAttr(string label, string attr, string? text)
        => GemItems.TryParseFormatName(text, out var f) ? f : throw XmlConfig.Invalid(label, attr, text ?? "", "L, B, BOOLEAN, A, J, C2, I1, I2, I4, I8, U1, U2, U4, U8, F4, F8");

    static SecsItem? ValueAttr(string label, string attr, string? text, SecsFormat f)
    {
        if (text == null) return null;
        try { return GemItems.ParseValue(text, f); }
        catch (FormatException ex) { throw XmlConfig.Invalid(label, attr, text, ex.Message); }
    }

    static GemId ParseId(string label, string attr, string? text, SecsFormat f)
    {
        if (string.IsNullOrEmpty(text)) throw XmlConfig.Invalid(label, attr, text ?? "", "비어 있으면 안 됨");
        if (f == SecsFormat.ASCII) return new GemId(text);
        return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? new GemId(n)
            : throw XmlConfig.Invalid(label, attr, text, $"{GemItems.FormatName(f)} 포맷 ID는 10진 정수");
    }

    /// <summary>Builds the current settings as an XML element. The transport element (&lt;SecsHsms&gt;/&lt;SecsSerial&gt;) is always included.</summary>
    /// <returns>The &lt;GemEquipment&gt; element</returns>
    /// <exception cref="InvalidOperationException">A definition has a value that cannot be written to XML</exception>
    public XElement ToXml() => XmlConfig.Serialize(ToConfig());

    /// <summary>Loads the settings from an XML element.</summary>
    /// <remarks>
    /// <para>Omitted attributes take their default values. An unknown attribute or element is an error (to catch typos). Everything is checked before anything is applied, so if anything is wrong nothing changes.</para>
    /// <para>The Role attribute of the transport element must be absent or Equipment. The format can be checked as an XSD by passing <see cref="GemEquipmentConfig"/> to <see cref="ConfigSchema.Get{T}"/>.</para>
    /// </remarks>
    /// <param name="element">The &lt;GemEquipment&gt; element</param>
    /// <exception cref="InvalidOperationException">Called while running (<see cref="IsStart"/>)</exception>
    /// <exception cref="FormatException">The root name differs, or there is an unknown attribute or element, a wrong value, a mismatched transport kind or Role, or a definition error</exception>
    public void FromXml(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (IsStart) throw new InvalidOperationException("통신 중에는 설정을 불러올 수 없습니다. Stop() 후 호출하세요.");
        foreach (var t in element.Elements().Where(x => x.Name.LocalName is nameof(SecsHsms) or nameof(SecsSerial)))
            if (t.Attribute("Role") is { } role && role.Value != nameof(SecsRole.Equipment))
                throw XmlConfig.Invalid(t.Name.LocalName, "Role", role.Value, "GemEquipment의 전송은 Equipment만 됨");
        ApplyConfig(XmlConfig.Deserialize<GemEquipmentConfig>(element));
    }

    /// <summary>Saves the settings to an XML file.</summary>
    /// <param name="path">File path</param>
    public void SaveXml(string path) => ToXml().Save(path);

    /// <summary>Loads the settings from an XML file. The rules are the same as <see cref="FromXml"/>.</summary>
    /// <param name="path">File path</param>
    public void LoadXml(string path) => FromXml(XElement.Load(path));
}
