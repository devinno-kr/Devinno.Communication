using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

/// <summary>The item formats used to send GEM IDs with <see cref="GemMessages"/> builders.</summary>
/// <remarks>Each format is one of U1, U2, U4, U8, I1, I2, I4, I8, or A (the default is U4), the same rule as the <c>VidFormat</c>, <c>CeidFormat</c>, <c>RptidFormat</c>, <c>AlidFormat</c>, and <c>DataidFormat</c> settings of <c>GemEquipment</c> and <c>GemHost</c>.
/// A numeric ID out of the range of its format makes the builder throw <see cref="ArgumentOutOfRangeException"/>, and a numeric ID sent as A becomes a decimal string.</remarks>
public sealed class GemFormats
{
    readonly SecsFormat vid = SecsFormat.U4, ceid = SecsFormat.U4, rptid = SecsFormat.U4, alid = SecsFormat.U4, dataid = SecsFormat.U4;

    /// <summary>Gets the default formats (all U4).</summary>
    public static GemFormats Default { get; } = new();

    /// <summary>Gets the format of SVID, ECID, and DVID (default U4).</summary>
    /// <exception cref="ArgumentException">The value is not U1, U2, U4, U8, I1, I2, I4, I8, or A.</exception>
    public SecsFormat Vid { get => vid; init => vid = GemItems.CheckIdFormat(value, nameof(Vid)); }

    /// <summary>Gets the format of CEID (default U4).</summary>
    /// <exception cref="ArgumentException">The value is not U1, U2, U4, U8, I1, I2, I4, I8, or A.</exception>
    public SecsFormat Ceid { get => ceid; init => ceid = GemItems.CheckIdFormat(value, nameof(Ceid)); }

    /// <summary>Gets the format of RPTID (default U4).</summary>
    /// <exception cref="ArgumentException">The value is not U1, U2, U4, U8, I1, I2, I4, I8, or A.</exception>
    public SecsFormat Rptid { get => rptid; init => rptid = GemItems.CheckIdFormat(value, nameof(Rptid)); }

    /// <summary>Gets the format of ALID (default U4).</summary>
    /// <exception cref="ArgumentException">The value is not U1, U2, U4, U8, I1, I2, I4, I8, or A.</exception>
    public SecsFormat Alid { get => alid; init => alid = GemItems.CheckIdFormat(value, nameof(Alid)); }

    /// <summary>Gets the format of DATAID (default U4).</summary>
    /// <exception cref="ArgumentException">The value is not U1, U2, U4, U8, I1, I2, I4, I8, or A.</exception>
    public SecsFormat Dataid { get => dataid; init => dataid = GemItems.CheckIdFormat(value, nameof(Dataid)); }

    // 3층(GemEquipment·GemHost)이 자기 ID 포맷 설정으로 Start 때 만든다
    internal static GemFormats Of(SecsFormat vid, SecsFormat ceid, SecsFormat rptid, SecsFormat alid, SecsFormat dataid)
        => new() { Vid = vid, Ceid = ceid, Rptid = rptid, Alid = alid, Dataid = dataid };
}
