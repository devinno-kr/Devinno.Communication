using System.Globalization;
using System.Text.RegularExpressions;

namespace Devinno.Communication.Siemens;

// S7 절대 주소 (DB1.DBW0, DB1.DBX0.1, M10.0, MW10, IB0/EB0, QD4/AD4 …). 대소문자 무시, 이름은 대문자로 정규화.
// 독일식 E(입력)·A(출력)는 I·Q와 같은 영역이다. 타이머(T)·카운터(C/Z)는 지원하지 않는다.
internal readonly record struct S7Address(string Name, byte Area, char Size, int Db, int Byte, int Bit)
{
    public const byte AreaI = 0x81, AreaQ = 0x82, AreaM = 0x83, AreaDB = 0x84;
    public const int MaxByte = 65535, MaxDb = 65535;
    // 개별 읽기/쓰기 한 번에 지정할 수 있는 최대 주소 수
    public const int MaxItems = 20;
    // 연속 읽기/쓰기 최대 바이트 수
    public const int MaxBlock = 65535;

    static readonly Regex Pattern = new(
        @"^(?:DB(?<db>[0-9]{1,9})\.DB(?<dsize>[XBWD])(?<dbyte>[0-9]{1,9})(?:\.(?<dbit>[0-9]{1,9}))?|(?<area>[IEQAM])(?<size>[BWD]?)(?<byte>[0-9]{1,9})(?:\.(?<bit>[0-9]{1,9}))?)\z",
        RegexOptions.CultureInvariant);

    // 값 하나의 바이트 수 (비트 X는 1바이트 00/01로 오간다)
    public int Width => Size switch { 'W' => 2, 'D' => 4, _ => 1 };
    public bool IsBit => Size == 'X';
    // 요청 항목의 24비트 주소 = 바이트 × 8 + 비트
    public int BitAddress => Byte * 8 + Bit;

    public static S7Address Parse(string name, string paramName)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("주소가 비어 있습니다.", paramName);
        var n = name.ToUpperInvariant();
        if (n.Length >= 2 && (n[0] is 'T' or 'C' or 'Z') && char.IsAsciiDigit(n[1]))
            throw new ArgumentException($"타이머(T)·카운터(C/Z)는 지원하지 않습니다: \"{name}\" (값 형식을 실장비로 확인하지 못함)", paramName);
        var m = Pattern.Match(n);
        if (!m.Success)
            throw new ArgumentException($"주소 형식이 아닙니다: \"{name}\" (예: DB1.DBW0, DB1.DBX0.1, M10.0, MW10, IB0, QD4)", paramName);

        bool isDb = m.Groups["db"].Success;
        char size = isDb ? m.Groups["dsize"].Value[0] : (m.Groups["size"].Value is { Length: 1 } s ? s[0] : 'X');
        var bitGroup = isDb ? m.Groups["dbit"] : m.Groups["bit"];
        if (size == 'X' && !bitGroup.Success) throw new ArgumentException($"비트 주소에는 비트 번호가 필요합니다: \"{name}\" (예: M10.0, DB1.DBX0.1)", paramName);
        if (size != 'X' && bitGroup.Success) throw new ArgumentException($"바이트·워드·더블워드 주소에는 비트 번호를 붙일 수 없습니다: \"{name}\"", paramName);

        int db = isDb ? Number(m.Groups["db"].Value, 1, MaxDb, "DB 번호", name, paramName) : 0;
        int b = Number((isDb ? m.Groups["dbyte"] : m.Groups["byte"]).Value, 0, MaxByte, "바이트 주소", name, paramName);
        int bit = bitGroup.Success ? Number(bitGroup.Value, 0, 7, "비트 번호", name, paramName) : 0;
        byte area = isDb ? AreaDB : m.Groups["area"].Value[0] switch { 'I' or 'E' => AreaI, 'Q' or 'A' => AreaQ, _ => AreaM };
        return new S7Address(n, area, size, db, b, bit);
    }

    static int Number(string text, int min, int max, string what, string name, string paramName)
    {
        int v = int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
        if (v < min || v > max) throw new ArgumentException($"{what}는 {min}~{max} 입니다: \"{name}\"", paramName);
        return v;
    }

    // 쉼표로 구분한 주소 목록 (XML Addresses 속성). 앞뒤 공백은 무시한다.
    public static string[] SplitNames(string names) => [.. (names ?? "").Split(',').Select(x => x.Trim())];

    // 연속 읽기/쓰기 시작 주소. 바이트 주소(DB1.DBB0, MB0, IB0, QB0)만 된다.
    public static S7Address ParseBlockStart(string name, string paramName)
    {
        var a = Parse(name, paramName);
        if (a.Size != 'B') throw new ArgumentException($"연속 읽기/쓰기 시작 주소는 바이트 주소여야 합니다: \"{name}\" (예: DB1.DBB0, MB0, IB0, QB0)", paramName);
        return a;
    }

    // 같은 영역·DB·시작 바이트의 바이트 주소 문자열 (DB1.DBW10 → DB1.DBB10, MD4 → MB4, EW0 → IB0, AD8 → QB8). 이름 있는 쓰기가 값 여러 개를 블록으로 보낼 때 쓴다.
    public string ByteStartName() => Area switch
    {
        AreaDB => $"DB{Db}.DBB{Byte}",
        AreaI => $"IB{Byte}",
        AreaQ => $"QB{Byte}",
        _ => $"MB{Byte}",
    };

    // 읽기 요청 항목. 비트는 BIT ×1, 나머지는 BYTE × 크기.
    public S7Item ToItem() => IsBit ? new(S7Item.TsBit, 1, Db, Area, BitAddress) : new(S7Item.TsByte, Width, Db, Area, BitAddress);

    // 값이 크기 범위에 들어가는지 확인한다. 비트는 0/1, B/W/D는 부호 있는 최솟값~부호 없는 최댓값.
    public void CheckValue(long value, string paramName)
    {
        if (IsBit)
        {
            if (value is not (0 or 1)) throw new ArgumentOutOfRangeException(paramName, $"{Name}은(는) 비트라서 0 또는 1만 쓸 수 있습니다: {value}");
            return;
        }
        long min = -(1L << (Width * 8 - 1)), max = (1L << (Width * 8)) - 1;
        if (value < min || value > max) throw new ArgumentOutOfRangeException(paramName, $"{Name} 값 범위는 {min}~{max} 입니다: {value}");
    }

    // 쓸 값의 바이트(빅엔디언, 크기에 맞게 자름). 비트는 1바이트 00/01.
    public byte[] Encode(long value)
    {
        var b = new byte[Width];
        for (int i = 0; i < b.Length; i++) b[i] = (byte)(value >> (8 * (b.Length - 1 - i)));
        return b;
    }

    // 읽은 바이트(빅엔디언)를 부호 없는 값으로. 비트는 bit0만 본다.
    public long Decode(ReadOnlySpan<byte> data)
    {
        if (IsBit) return data[0] & 1;
        long v = 0;
        for (int i = 0; i < Width; i++) v = (v << 8) | data[i];
        return v;
    }
}

// 요청 항목(S7ANY) 하나: 전송 크기, 개수, DB 번호(DB가 아니면 0), 영역, 24비트 주소(바이트 × 8 + 비트)
internal readonly record struct S7Item(byte TransportSize, int Count, int Db, byte Area, int BitAddress)
{
    // 요청 전송 크기: BIT(비트 1개), BYTE(바이트 Count개)
    public const byte TsBit = 0x01, TsByte = 0x02;

    // 바이트 단위 항목 (연속 읽기/쓰기 조각)
    public static S7Item Bytes(byte area, int db, int byteOffset, int count) => new(TsByte, count, db, area, byteOffset * 8);
}
