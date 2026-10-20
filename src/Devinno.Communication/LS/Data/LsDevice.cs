using System.Globalization;

namespace Devinno.Communication.LS.Data;

// LS 직접 변수 이름 (%MW100, %MX0, %IW0.0.0 …). % + 영역 문자 + 크기 문자(X/B/W/D/L) + 주소, 4~16자.
internal readonly record struct LsDevice(string Name, char Area, char Size, string Address)
{
    public const int MaxNameLength = 16;
    // 개별 읽기/쓰기(RSS/WSS) 한 번에 지정할 수 있는 최대 디바이스 수
    public const int MaxSingles = 16;

    // 값 하나의 바이트 수 (비트 X는 1바이트 00/01로 오간다)
    public int Width => Size switch { 'B' or 'X' => 1, 'W' => 2, 'D' => 4, _ => 8 };
    public bool IsBit => Size == 'X';

    public static LsDevice Parse(string name, string paramName)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("디바이스 이름이 비어 있습니다.", paramName);
        var n = name.ToUpperInvariant();
        if (n.Length < 4 || n.Length > MaxNameLength || n[0] != '%')
            throw new ArgumentException($"디바이스 이름 형식이 아닙니다: \"{name}\" (예: %MW100, 4~{MaxNameLength}자)", paramName);
        foreach (var c in n)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '%' || c == '.'))
                throw new ArgumentException($"디바이스 이름에 쓸 수 없는 문자가 있습니다: \"{name}\"", paramName);
        if (!char.IsAsciiLetterUpper(n[1]))
            throw new ArgumentException($"영역 문자가 잘못되었습니다: \"{name}\"", paramName);
        if ("XBWDL".IndexOf(n[2]) < 0)
            throw new ArgumentException($"크기 문자는 X, B, W, D, L 중 하나여야 합니다: \"{name}\"", paramName);
        if (!char.IsAsciiDigit(n[3]))
            throw new ArgumentException($"주소는 숫자로 시작해야 합니다: \"{name}\"", paramName);
        return new LsDevice(n, n[1], n[2], n[3..]);
    }

    // 값이 크기 범위에 들어가는지 확인한다. 비트는 0/1, B/W/D는 부호 있는 최솟값~부호 없는 최댓값, L은 전체.
    public void CheckValue(long value, string paramName)
    {
        if (IsBit)
        {
            if (value is not (0 or 1)) throw new ArgumentOutOfRangeException(paramName, $"{Name}은(는) 비트라서 0 또는 1만 쓸 수 있습니다: {value}");
            return;
        }
        if (Width == 8) return;
        long min = -(1L << (Width * 8 - 1)), max = (1L << (Width * 8)) - 1;
        if (value < min || value > max) throw new ArgumentOutOfRangeException(paramName, $"{Name} 값 범위는 {min}~{max} 입니다: {value}");
    }

    // 바이트 단위 이름으로 바꾼다 (FEnet 연속 명령용). 예: %MW100 → %MB200. 비트·점 주소는 불가.
    public LsDevice ToByteDevice(string paramName)
    {
        if (IsBit || !long.TryParse(Address, NumberStyles.None, CultureInfo.InvariantCulture, out var a))
            throw new ArgumentException($"연속 읽기/쓰기는 %MW100 같은 숫자 주소만 됩니다(비트, 점 주소 불가): \"{Name}\"", paramName);
        return Size == 'B' ? this : Parse($"%{Area}B{a * Width}", paramName);
    }

    // 쉼표로 구분한 디바이스 목록 문자열(XML의 Devices 속성)을 나눈다. 빈 항목은 버리고 앞뒤 공백은 지운다.
    internal static string[] SplitNames(string? text)
        => (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // 개별 명령의 디바이스 목록을 검사하고 해석한다: 1~MaxSingles개, 크기 문자가 모두 같아야 한다.
    internal static List<LsDevice> ParseSingles(IReadOnlyList<string> devices, string paramName)
    {
        ArgumentNullException.ThrowIfNull(devices, paramName);
        if (devices.Count < 1 || devices.Count > MaxSingles)
            throw new ArgumentOutOfRangeException(paramName, $"디바이스는 1~{MaxSingles}개 입니다.");
        var list = devices.Select(d => Parse(d, paramName)).ToList();
        if (list.Exists(d => d.Size != list[0].Size))
            throw new ArgumentException("개별 읽기/쓰기의 디바이스는 크기 문자(X/B/W/D/L)가 모두 같아야 합니다.", paramName);
        return list;
    }
}
