using System.Globalization;
using System.Xml.Linq;
using Devinno.Communication.Internal;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Hsms;
using Devinno.Communication.Secs.Serial;

namespace Devinno.Communication.Secs.Gem;

// 설정(GemHostConfig) ↔ XML: 전송 설정 + Host 설정 + 구독을 파일 하나에
public partial class GemHost
{
    const string Root = nameof(GemHost);

    /// <summary>Builds a configuration object from the current settings (transport settings, Host settings, and subscriptions).</summary>
    /// <returns>The configuration object (the transport settings are always included).</returns>
    public GemHostConfig ToConfig()
    {
        lock (lifeLock)
        {
            var c = new GemHostConfig
            {
                VidFormat = GemItems.FormatName(vidFormat),
                CeidFormat = GemItems.FormatName(ceidFormat),
                RptidFormat = GemItems.FormatName(rptidFormat),
                AlidFormat = GemItems.FormatName(alidFormat),
                DataidFormat = GemItems.FormatName(dataidFormat),
                TimeFormat = timeFormat,
                AutoConnectSequence = autoConnectSequence,
                RequestOnline = connectRequestOnline,
                SetEquipmentClock = connectSetClock,
                ConfigureReports = connectConfigureReports,
                EnableAlarms = connectEnableAlarms,
                DeleteReportsFirst = deleteReportsFirst,
                ReportIdStart = reportIdStart,
                EstablishCommunicationRetry = establishCommunicationRetry,
                CommandReplyTimeout = commandReplyTimeout,
                MaxPendingEvents = maxPendingEvents,
            };
            if (transport.Target is SecsHsms h) c.Hsms = h.ToConfig();
            else c.Serial = ((SecsSerial)transport.Target).ToConfig();
            foreach (var s in Subscriptions)
                c.Subscriptions.Add(new GemSubscribeConfig { Ceid = s.Ceid.ToString(), Vids = s.Vids.Count == 0 ? null : string.Join(",", s.Vids) });
            return c;
        }
    }

    /// <summary>Applies a configuration object (replaces the transport settings, Host settings, and all subscriptions).</summary>
    /// <remarks>Everything is validated before anything is applied, so if any value is invalid, nothing changes. If the transport settings are absent, the transport is left as it is; if present, Role is set to Host.</remarks>
    /// <param name="config">The configuration to apply.</param>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Called while running (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">A value is invalid, or the type of the transport settings differs from the wrapped transport (the message includes the element and attribute names).</exception>
    public void ApplyConfig(GemHostConfig config)
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

            var vid = IdFormatAttr(config.VidFormat, nameof(config.VidFormat));
            var ceid = IdFormatAttr(config.CeidFormat, nameof(config.CeidFormat));
            var rptid = IdFormatAttr(config.RptidFormat, nameof(config.RptidFormat));
            var alid = IdFormatAttr(config.AlidFormat, nameof(config.AlidFormat));
            var dataid = IdFormatAttr(config.DataidFormat, nameof(config.DataidFormat));
            XmlConfig.Range(Root, nameof(config.TimeFormat), config.TimeFormat, 0, 2);
            XmlConfig.Range(Root, nameof(config.EstablishCommunicationRetry), config.EstablishCommunicationRetry, 1, 3600);
            XmlConfig.Range(Root, nameof(config.CommandReplyTimeout), config.CommandReplyTimeout, 1000, 110000);
            XmlConfig.Range(Root, nameof(config.MaxPendingEvents), config.MaxPendingEvents, 1, 1000000);

            var subs = new List<GemSubscription>();
            foreach (var s in config.Subscriptions)
            {
                var label = $"Subscribe Ceid=\"{s.Ceid}\"";
                var id = ParseSubscriptionId(label, nameof(s.Ceid), s.Ceid, ceid);
                var vids = (s.Vids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(t => ParseSubscriptionId(label, nameof(s.Vids), t, vid)).ToList();
                var sub = new GemSubscription(id, vids);
                int i = subs.FindIndex(x => x.Ceid == id);
                if (i >= 0) subs[i] = sub;
                else subs.Add(sub);
            }
            int withVids = subs.Count(s => s.Vids.Count > 0);
            if (config.ReportIdStart < 0)
                throw XmlConfig.Invalid(Root, nameof(config.ReportIdStart), config.ReportIdStart.ToString(CultureInfo.InvariantCulture), "0 이상");
            if (rptid != SecsFormat.ASCII && withVids > 0 && (config.ReportIdStart > GemItems.IntegerRange(rptid).Max - (withVids - 1)))
                throw XmlConfig.Invalid(Root, nameof(config.ReportIdStart), config.ReportIdStart.ToString(CultureInfo.InvariantCulture),
                    $"VID가 있는 구독 {withVids}개만큼 늘리면 RptidFormat {GemItems.FormatName(rptid)} 범위를 넘음");

            // 2. 적용
            if (config.Hsms != null)
            {
                config.Hsms.Role = SecsRole.Host;
                ((SecsHsms)transport.Target).ApplyConfig(config.Hsms);
            }
            if (config.Serial != null)
            {
                config.Serial.Role = SecsRole.Host;
                ((SecsSerial)transport.Target).ApplyConfig(config.Serial);
            }
            (vidFormat, ceidFormat, rptidFormat, alidFormat, dataidFormat) = (vid, ceid, rptid, alid, dataid);
            timeFormat = config.TimeFormat;
            autoConnectSequence = config.AutoConnectSequence;
            connectRequestOnline = config.RequestOnline;
            connectSetClock = config.SetEquipmentClock;
            connectConfigureReports = config.ConfigureReports;
            connectEnableAlarms = config.EnableAlarms;
            deleteReportsFirst = config.DeleteReportsFirst;
            reportIdStart = config.ReportIdStart;
            establishCommunicationRetry = config.EstablishCommunicationRetry;
            commandReplyTimeout = config.CommandReplyTimeout;
            maxPendingEvents = config.MaxPendingEvents;
            lock (gate)
            {
                subscriptions.Clear();
                subscriptions.AddRange(subs);
            }
        }
    }

    static SecsFormat IdFormatAttr(string? text, string attr)
        => GemItems.TryParseFormatName(text, out var f) && Array.IndexOf(GemItems.IdFormats, f) >= 0 ? f
            : throw XmlConfig.Invalid(Root, attr, text ?? "", "U1, U2, U4, U8, I1, I2, I4, I8, A");

    // 구독 ID 텍스트: 포맷이 숫자면 10진 정수(포맷 범위 안), A면 그대로
    static GemId ParseSubscriptionId(string label, string attr, string? text, SecsFormat f)
    {
        if (string.IsNullOrEmpty(text)) throw XmlConfig.Invalid(label, attr, text ?? "", "비어 있으면 안 됨");
        if (f == SecsFormat.ASCII) return new GemId(text);
        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
            throw XmlConfig.Invalid(label, attr, text, $"{GemItems.FormatName(f)} 포맷 ID는 10진 정수");
        var id = new GemId(n);
        return GemItems.Fits(id, f) ? id : throw XmlConfig.Invalid(label, attr, text, $"{GemItems.FormatName(f)} 범위 밖");
    }

    /// <summary>Builds an XML element from the current settings. The transport element (&lt;SecsHsms&gt; or &lt;SecsSerial&gt;) is always included.</summary>
    /// <returns>The &lt;GemHost&gt; element.</returns>
    public XElement ToXml() => XmlConfig.Serialize(ToConfig());

    /// <summary>Loads the settings from an XML element.</summary>
    /// <remarks>
    /// <para>Omitted attributes take their default values. Unknown attributes and elements are errors (to catch typos). Everything is validated before anything is applied, so if any value is invalid, nothing changes.</para>
    /// <para>The Role attribute of the transport element must be absent or Host. You can check the format as an XSD by passing <see cref="GemHostConfig"/> to <see cref="ConfigSchema.Get{T}"/>.</para>
    /// </remarks>
    /// <param name="element">The &lt;GemHost&gt; element.</param>
    /// <exception cref="InvalidOperationException">Called while running (<see cref="IsStart"/>).</exception>
    /// <exception cref="FormatException">The root name differs, an attribute or element is unknown, a value is invalid, or the transport type or Role does not match.</exception>
    public void FromXml(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (IsStart) throw new InvalidOperationException("통신 중에는 설정을 불러올 수 없습니다. Stop() 후 호출하세요.");
        foreach (var t in element.Elements().Where(x => x.Name.LocalName is nameof(SecsHsms) or nameof(SecsSerial)))
            if (t.Attribute("Role") is { } role && role.Value != nameof(SecsRole.Host))
                throw XmlConfig.Invalid(t.Name.LocalName, "Role", role.Value, "GemHost의 전송은 Host만 됨");
        ApplyConfig(XmlConfig.Deserialize<GemHostConfig>(element));
    }

    /// <summary>Saves the settings to an XML file.</summary>
    /// <param name="path">The file path.</param>
    public void SaveXml(string path) => ToXml().Save(path);

    /// <summary>Loads the settings from an XML file. The rules are the same as for <see cref="FromXml"/>.</summary>
    /// <param name="path">The file path.</param>
    public void LoadXml(string path) => FromXml(XElement.Load(path));
}
