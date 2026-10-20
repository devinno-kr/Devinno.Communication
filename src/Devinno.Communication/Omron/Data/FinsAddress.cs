using System.Globalization;
using System.Text.RegularExpressions;

namespace Devinno.Communication.Omron.Data;

// 옴론 FINS 메모리 주소 (CS/CJ/CP 표기). 대소문자 무시, 이름은 정규형으로 바꾼다:
// CIO는 "CIO100"(숫자만 쓴 "100"도 CIO), W·H·A·D는 "W10"·"H20"·"A100"·"D100"("WR"·"HR"·"AR"·"DM"도 받음),
// EM은 "E{뱅크 16진수}_{워드}"("EM0_100"도 받음), 비트는 ".{00~15}"(예: "CIO100.05", "D100.15").
// Area는 워드 주소면 워드 영역 코드(B0/B1/B2/B3/82/A0+뱅크/60+뱅크−0x10), 비트 주소면 비트 영역 코드(30/31/32/33/02/20+뱅크/E0+뱅크−0x10)다.
// 타이머(T)·카운터(C)는 지원하지 않는다.
internal readonly record struct FinsAddress(string Name, byte Area, int Word, int Bit, bool IsBit)
{
    // EM 뱅크 최댓값 (CJ2: 0~0x18, CS1/CJ1은 0~0xC)
    public const int MaxEmBank = 0x18;
    // 연속 읽기/쓰기 최대 개수 (워드 또는 비트)
    public const int MaxBlock = 65535;

    static readonly Regex Pattern = new(
        @"^(?:(?<area>CIO|WR|W|HR|H|AR|A|DM|D)?(?<word>[0-9]{1,5})|EM?(?<bank>[0-9A-F]{1,2})_(?<eword>[0-9]{1,5}))(?:\.(?<bit>[0-9]{1,2}))?\z",
        RegexOptions.CultureInvariant);

    // 값 하나의 바이트 수 (워드 2, 비트 1 = 00/01)
    public int Width => IsBit ? 1 : 2;

    // 이 영역의 마지막 워드 번호 (CS/CJ 최대값: CIO 6143, W·H 511, A 959, D·EM 32767)
    public int MaxWord => Area switch
    {
        0xB0 or 0x30 => 6143,
        0xB1 or 0x31 or 0xB2 or 0x32 => 511,
        0xB3 or 0x33 => 959,
        _ => 32767,
    };

    public static FinsAddress Parse(string name, string paramName)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("주소가 비어 있습니다.", paramName);
        var n = name.ToUpperInvariant();
        if (n.Length >= 2 && (n[0] is 'T' or 'C') && (char.IsAsciiDigit(n[1]) || n.StartsWith("TIM", StringComparison.Ordinal) || n.StartsWith("CNT", StringComparison.Ordinal)))
            throw new ArgumentException($"타이머(T)·카운터(C)는 지원하지 않습니다: \"{name}\" (CIO는 CIO100처럼 씁니다)", paramName);
        var m = Pattern.Match(n);
        if (!m.Success)
            throw new ArgumentException($"주소 형식이 아닙니다: \"{name}\" (예: D100, CIO100, 100, W10, H20, A100, E0_100, CIO100.05, D100.15)", paramName);

        bool isEm = m.Groups["bank"].Success;
        int bank = isEm ? int.Parse(m.Groups["bank"].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture) : 0;
        if (bank > MaxEmBank) throw new ArgumentException($"EM 뱅크는 0~{MaxEmBank:X} (16진수) 입니다: \"{name}\"", paramName);
        char kind = isEm ? 'E' : m.Groups["area"].Value switch { "" or "CIO" => 'C', "W" or "WR" => 'W', "H" or "HR" => 'H', "A" or "AR" => 'A', _ => 'D' };
        int word = int.Parse((isEm ? m.Groups["eword"] : m.Groups["word"]).Value, NumberStyles.None, CultureInfo.InvariantCulture);
        bool isBit = m.Groups["bit"].Success;
        int bit = isBit ? int.Parse(m.Groups["bit"].Value, NumberStyles.None, CultureInfo.InvariantCulture) : 0;
        if (bit > 15) throw new ArgumentException($"비트 번호는 00~15 입니다: \"{name}\"", paramName);

        var (wordCode, bitCode) = kind switch
        {
            'C' => (0xB0, 0x30),
            'W' => (0xB1, 0x31),
            'H' => (0xB2, 0x32),
            'A' => (0xB3, 0x33),
            'D' => (0x82, 0x02),
            _ => bank < 0x10 ? (0xA0 + bank, 0x20 + bank) : (0x60 + bank - 0x10, 0xE0 + bank - 0x10),
        };
        string prefix = kind switch { 'C' => "CIO", 'E' => $"E{bank:X}_", _ => kind.ToString() };
        var a = new FinsAddress(prefix + word.ToString(CultureInfo.InvariantCulture) + (isBit ? "." + bit.ToString("D2", CultureInfo.InvariantCulture) : ""),
            (byte)(isBit ? bitCode : wordCode), word, bit, isBit);
        if (word > a.MaxWord) throw new ArgumentException($"워드 번호는 0~{a.MaxWord} 입니다: \"{name}\"", paramName);
        return a;
    }

    // 쉼표로 구분한 주소 목록 (XML Addresses 속성). 앞뒤 공백은 무시한다.
    public static string[] SplitNames(string names) => [.. (names ?? "").Split(',').Select(x => x.Trim())];

    // 개별 읽기 주소들. 1~maxItems개.
    public static FinsAddress[] ParseItems(IReadOnlyList<string> addresses, int maxItems, string paramName)
    {
        ArgumentNullException.ThrowIfNull(addresses, paramName);
        if (addresses.Count < 1 || addresses.Count > maxItems)
            throw new ArgumentException($"주소는 1~{maxItems}개입니다: {addresses.Count}개", paramName);
        return [.. addresses.Select(a => Parse(a, paramName))];
    }

    // 연속 읽기/쓰기 시작 주소. 개수는 1~65535이고 마지막 워드·비트가 영역 안에 있어야 한다.
    public static FinsAddress ParseBlock(string address, int count, string paramName, string countName)
    {
        var a = Parse(address, paramName);
        if (count < 1 || count > MaxBlock) throw new ArgumentOutOfRangeException(countName, $"개수는 1~{MaxBlock} 입니다: {count}");
        long lastWord = a.IsBit ? (a.Word * 16L + a.Bit + count - 1) / 16 : a.Word + (long)count - 1;
        if (lastWord > a.MaxWord) throw new ArgumentOutOfRangeException(countName, $"{a.Name}부터 {count}개는 영역 끝(워드 {a.MaxWord})을 넘습니다.");
        return a;
    }

    // 요청에 들어가는 주소 4바이트: 영역 코드, 워드(빅엔디언 2바이트), 비트(워드 주소는 00)
    public byte[] Encode() => [Area, (byte)(Word >> 8), (byte)Word, (byte)Bit];

    // count개 뒤의 주소 (연속 읽기/쓰기 조각의 시작). 비트는 15 다음이 다음 워드의 00이다. Name은 바꾸지 않는다(요청 생성용).
    public FinsAddress Offset(int count)
    {
        if (!IsBit) return this with { Word = Word + count };
        int index = Word * 16 + Bit + count;
        return this with { Word = index / 16, Bit = index % 16 };
    }

    // 쓸 값 검사: 비트는 0/1, 워드는 -32768~65535 (음수는 2의 보수로 보냄)
    public void CheckValue(long value, string paramName)
    {
        if (IsBit)
        {
            if (value is not (0 or 1)) throw new ArgumentOutOfRangeException(paramName, $"{Name}은(는) 비트라서 0 또는 1만 쓸 수 있습니다: {value}");
        }
        else if (value < -32768 || value > 65535)
            throw new ArgumentOutOfRangeException(paramName, $"{Name} 값 범위는 -32768~65535 입니다: {value}");
    }
}
