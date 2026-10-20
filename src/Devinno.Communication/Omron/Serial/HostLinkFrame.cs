using System.Globalization;
using System.Text;
using Devinno.Communication.Omron.Data;

namespace Devinno.Communication.Omron.Serial;

// Host Link FINS(헤더 코드 FA, 직접 연결 형식) ASCII 프레임 (W342 3-5-3).
// 요청: '@' 국번(10진 2자리) "FA" 응답 대기(16진 1자리, ×10ms) ICF "00" DA2 "00" SA2 "00" SID "00" FINS 본문(16진) FCS(16진 2자리) '*' CR
// 응답: '@' 국번 "FA" [Host Link 종료 코드 "00"] ICF "40"(또는 "C0") DA2 SA2 SID FINS 응답 본문(16진) FCS '*' CR
// FCS = '@'부터 FCS 앞 글자까지 모든 글자의 XOR (7비트).
internal static class HostLinkFrame
{
    // 응답 프레임 최대 글자 수 (W342: 1,115글자) + 여유
    public const int MaxResponse = 1200;

    // FCS 2글자 (대문자 16진수)
    public static string Fcs(ReadOnlySpan<byte> ascii)
    {
        int x = 0;
        foreach (var b in ascii) x ^= b & 0x7F;
        return x.ToString("X2", CultureInfo.InvariantCulture);
    }

    // 요청 프레임. body는 FINS 본문(MRC부터).
    public static byte[] Command(int unit, int wait, ReadOnlySpan<byte> body)
    {
        var sb = new StringBuilder();
        sb.Append('@').Append(unit.ToString("D2", CultureInfo.InvariantCulture)).Append("FA")
          .Append(wait.ToString("X1", CultureInfo.InvariantCulture)).Append("00000000").Append(Convert.ToHexString(body));
        var text = Encoding.ASCII.GetBytes(sb.ToString());
        return [.. text, .. Encoding.ASCII.GetBytes(Fcs(text)), (byte)'*', (byte)'\r'];
    }

    // 버퍼에서 응답 프레임 하나('@' … "*" CR)의 위치. 아직 없으면 길이 0.
    public static (int Start, int Length) Find(ReadOnlySpan<byte> buf)
    {
        int start = buf.IndexOf((byte)'@');
        if (start < 0) return (0, 0);
        int end = buf[start..].IndexOf("*\r"u8);
        return end < 0 ? (0, 0) : (start, end + 2);
    }

    // 응답 프레임 해석. FCS가 틀리면 FcsError, 국번·형식이 틀리면 Invalid, 그 밖에는 FINS 본문 해석 결과.
    // ICF 앞의 Host Link 종료 코드 "00"은 있어도 없어도 받는다(근거 조사 5.2: 실장비 미확인).
    public static FinsResponse Parse(ReadOnlySpan<byte> frame, int unit, FinsChunk chunk)
    {
        if (frame.Length < 11 || frame[0] != '@' || frame[^2] != '*' || frame[^1] != '\r') return FinsResponse.Invalid;
        var fcsText = Encoding.ASCII.GetString(frame[^4..^2]);
        if (!string.Equals(fcsText, Fcs(frame[..^4]), StringComparison.OrdinalIgnoreCase)) return FinsResponse.FcsError;
        var s = Encoding.ASCII.GetString(frame[1..^4]);
        if (s[..2] != unit.ToString("D2", CultureInfo.InvariantCulture) || s[2..4] != "FA") return FinsResponse.Invalid;
        var text = s[4..];
        // "00"이 아닌 Host Link 종료 코드(13 FCS, 14 형식, 18 프레임 길이 등)는 FINS 본문 없이 오므로 Invalid가 되고, 마스터가 타임아웃·재시도로 처리한다.
        static bool IsIcf(string t, int at) => t.Length >= at + 2 && (string.Compare(t, at, "40", 0, 2, StringComparison.OrdinalIgnoreCase) == 0 || string.Compare(t, at, "C0", 0, 2, StringComparison.OrdinalIgnoreCase) == 0);
        if (text.StartsWith("00", StringComparison.Ordinal) && IsIcf(text, 2)) text = text[2..];
        if (!IsIcf(text, 0) || text.Length % 2 != 0) return FinsResponse.Invalid;
        byte[] bytes;
        try { bytes = Convert.FromHexString(text); }
        catch (FormatException) { return FinsResponse.Invalid; }
        return bytes.Length < 4 ? FinsResponse.Invalid : FinsFrame.ParseBody(bytes.AsSpan(4), chunk);
    }
}
