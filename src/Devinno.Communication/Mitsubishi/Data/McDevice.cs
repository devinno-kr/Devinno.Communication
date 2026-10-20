namespace Devinno.Communication.Mitsubishi.Data;

// 디바이스 표를 고르는 프레임 종류
internal enum McTarget { Serial1C, Ethernet3E, Ethernet1E }

// 미쓰비시 디바이스 이름 (D100, M0, X17, XA0, TN5, SM400, ZR100 …). 디바이스 코드(1~2문자) + 번호(디바이스의 진수).
// Number는 번호의 값(진수 해석 후), Radix는 해석한 진수, WireCode는 바이너리 디바이스 코드(3E 1바이트, 1E 2바이트, 1C는 0).
internal readonly record struct McDevice(string Name, string Code, int Number, bool IsBit, int Radix, int WireCode)
{
    readonly record struct Spec(bool IsBit, bool Hex, int Wire);

    // 1C (ACPU 공통 명령): 1문자 코드 + 4자리, 2문자 코드(TN/TS/TC/CN/CS/CC) + 3자리
    static readonly Dictionary<string, Spec> Serial1C = new()
    {
        ["X"] = new(true, false, 0), ["Y"] = new(true, false, 0), ["M"] = new(true, false, 0), ["L"] = new(true, false, 0),
        ["S"] = new(true, false, 0), ["B"] = new(true, true, 0), ["F"] = new(true, false, 0),
        ["D"] = new(false, false, 0), ["W"] = new(false, true, 0), ["R"] = new(false, false, 0),
        ["TN"] = new(false, false, 0), ["TS"] = new(true, false, 0), ["TC"] = new(true, false, 0),
        ["CN"] = new(false, false, 0), ["CS"] = new(true, false, 0), ["CC"] = new(true, false, 0),
    };

    // 3E 바이너리 (Q/L/FX5, 서브커맨드 0000/0001): 1바이트 코드
    static readonly Dictionary<string, Spec> Ethernet3E = new()
    {
        ["SM"] = new(true, false, 0x91), ["SD"] = new(false, false, 0xA9),
        ["X"] = new(true, true, 0x9C), ["Y"] = new(true, true, 0x9D),
        ["M"] = new(true, false, 0x90), ["L"] = new(true, false, 0x92), ["F"] = new(true, false, 0x93), ["V"] = new(true, false, 0x94),
        ["B"] = new(true, true, 0xA0), ["D"] = new(false, false, 0xA8), ["W"] = new(false, true, 0xB4),
        ["TS"] = new(true, false, 0xC1), ["TC"] = new(true, false, 0xC0), ["TN"] = new(false, false, 0xC2),
        ["SS"] = new(true, false, 0xC7), ["SC"] = new(true, false, 0xC6), ["SN"] = new(false, false, 0xC8),
        ["CS"] = new(true, false, 0xC4), ["CC"] = new(true, false, 0xC3), ["CN"] = new(false, false, 0xC5),
        ["SB"] = new(true, true, 0xA1), ["SW"] = new(false, true, 0xB5), ["S"] = new(true, false, 0x98),
        ["Z"] = new(false, false, 0xCC), ["R"] = new(false, false, 0xAF), ["ZR"] = new(false, true, 0xB0),
    };

    // 1E 바이너리 (A 호환, FX3 이더넷): 2바이트 코드
    static readonly Dictionary<string, Spec> Ethernet1E = new()
    {
        ["X"] = new(true, true, 0x5820), ["Y"] = new(true, true, 0x5920), ["M"] = new(true, false, 0x4D20),
        ["S"] = new(true, false, 0x5320), ["L"] = new(true, false, 0x4C20), ["F"] = new(true, false, 0x4620),
        ["B"] = new(true, true, 0x4220), ["D"] = new(false, false, 0x4420), ["R"] = new(false, false, 0x5220),
        ["W"] = new(false, true, 0x5720), ["TN"] = new(false, false, 0x544E), ["TS"] = new(true, false, 0x5453),
        ["TC"] = new(true, false, 0x5443), ["CN"] = new(false, false, 0x434E), ["CS"] = new(true, false, 0x4353),
        ["CC"] = new(true, false, 0x4343),
    };

    static Dictionary<string, Spec> Table(McTarget target) => target switch
    {
        McTarget.Serial1C => Serial1C,
        McTarget.Ethernet3E => Ethernet3E,
        _ => Ethernet1E,
    };

    static string TargetName(McTarget target) => target switch
    {
        McTarget.Serial1C => "1C",
        McTarget.Ethernet3E => "3E",
        _ => "1E",
    };

    // X/Y를 8진수로 볼지. Auto는 프레임 기본값(autoIsOctal)을 따른다.
    public static bool IsOctal(McXYNotation notation, bool autoIsOctal)
        => notation == McXYNotation.Octal || (notation == McXYNotation.Auto && autoIsOctal);

    // 디바이스 이름을 해석한다. 대소문자 무시, 앞뒤 공백 무시. 실패하면 ArgumentException(paramName).
    public static McDevice Parse(string name, McTarget target, bool xyOctal, string paramName)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("디바이스 이름이 비어 있습니다.", paramName);
        var n = name.Trim().ToUpperInvariant();
        var table = Table(target);
        // 2문자 코드(TN, SM, ZR …)를 먼저 찾는다. 16진 번호가 A~F로 시작해도 된다(XA0, B1F)
        var code = n.Length > 2 && table.ContainsKey(n[..2]) ? n[..2] : n.Length > 1 && table.ContainsKey(n[..1]) ? n[..1] : "";
        if (code.Length == 0 || n[code.Length] is >= 'G' and <= 'Z')
            throw new ArgumentException($"{TargetName(target)} 프레임에서 쓸 수 없는 디바이스입니다: \"{name}\" (사용 가능: {string.Join(", ", table.Keys)})", paramName);
        var spec = table[code];

        int radix = code is "X" or "Y" ? (xyOctal ? 8 : 16) : spec.Hex ? 16 : 10;
        long max = target switch
        {
            McTarget.Serial1C => (long)Math.Pow(radix, code.Length == 1 ? 4 : 3) - 1,
            McTarget.Ethernet3E => 0xFFFFFF,
            _ => int.MaxValue,
        };
        long number = 0;
        foreach (var c in n[code.Length..])
        {
            int v = c >= '0' && c <= '9' ? c - '0' : c >= 'A' && c <= 'F' ? c - 'A' + 10 : 99;
            if (v >= radix) throw new ArgumentException($"{code} 번호는 {radix}진수입니다: \"{name}\"", paramName);
            number = number * radix + v;
            if (number > max) throw new ArgumentException($"{code} 번호가 {TargetName(target)} 프레임 범위를 넘습니다: \"{name}\"", paramName);
        }
        return new McDevice(n, code, (int)number, spec.IsBit, radix, spec.Wire);
    }

    // 한 요청의 최대 점수. 3E: 워드 960, 비트 3584. 1C/1E(FX3 기준): BR 256, BW 160, WR 워드 디바이스 64·비트 디바이스 32워드, WW 64·10워드.
    public static int MaxCount(McTarget target, McCommand command, bool bitDevice)
    {
        if (target == McTarget.Ethernet3E) return command is McCommand.BR or McCommand.BW ? 3584 : 960;
        return command switch
        {
            McCommand.BR => 256,
            McCommand.BW => 160,
            McCommand.WR => bitDevice ? 32 : 64,
            _ => bitDevice ? 10 : 64,
        };
    }

    // 명령에 쓸 디바이스를 해석하고 점수를 검사한다. BR/BW는 비트 디바이스만 된다.
    public static McDevice ParseFor(McCommand command, string device, int count, McTarget target, bool xyOctal, string deviceParam, string countParam)
    {
        var d = Parse(device, target, xyOctal, deviceParam);
        if (command is McCommand.BR or McCommand.BW && !d.IsBit)
            throw new ArgumentException($"{command}는 비트 디바이스만 쓸 수 있습니다: \"{d.Name}\" (워드 디바이스는 WR/WW)", deviceParam);
        int max = MaxCount(target, command, d.IsBit);
        if (count < 1 || count > max) throw new ArgumentOutOfRangeException(countParam, $"{command} {d.Name} 점수는 1~{max} 입니다.");
        return d;
    }

    // 쓸 값 검사: BW는 0/1, WW는 -32768~65535 (16비트로 잘라 보냄).
    public static void CheckValues(McCommand command, IReadOnlyList<long> values, string paramName)
    {
        foreach (var v in values)
        {
            if (command == McCommand.BW ? v is not (0 or 1) : v < -32768 || v > 65535)
                throw new ArgumentOutOfRangeException(paramName, command == McCommand.BW
                    ? $"비트 값은 0 또는 1만 쓸 수 있습니다: {v}"
                    : $"워드 값 범위는 -32768~65535 입니다: {v}");
        }
    }

    // 바이너리(3E/1E) 쓰기 데이터. BW: 1점 4비트(앞 점이 상위 니블, 홀수면 마지막 하위 니블 0), WW: 워드 리틀엔디언.
    public static byte[] EncodeBinary(McCommand command, IReadOnlyList<long>? values)
    {
        if (values == null || command is McCommand.BR or McCommand.WR) return [];
        if (command == McCommand.BW)
        {
            var r = new byte[(values.Count + 1) / 2];
            for (int i = 0; i < values.Count; i++)
                if (values[i] != 0) r[i / 2] |= (byte)(i % 2 == 0 ? 0x10 : 0x01);
            return r;
        }
        var w = new byte[values.Count * 2];
        for (int i = 0; i < values.Count; i++) { w[i * 2] = (byte)values[i]; w[i * 2 + 1] = (byte)(values[i] >> 8); }
        return w;
    }

    // 바이너리(3E/1E) 정상 응답 데이터 해석. 길이나 값이 맞지 않으면 Invalid.
    public static McResponse DecodeBinary(McCommand command, ReadOnlySpan<byte> data, int count)
    {
        switch (command)
        {
            case McCommand.BR:
                {
                    if (data.Length != (count + 1) / 2) return McResponse.Invalid;
                    var r = new long[count];
                    for (int i = 0; i < count; i++)
                    {
                        int nib = i % 2 == 0 ? data[i / 2] >> 4 : data[i / 2] & 0x0F;
                        if (nib > 1) return McResponse.Invalid;
                        r[i] = nib;
                    }
                    return new(McStatus.Ack, r, 0);
                }
            case McCommand.WR:
                {
                    if (data.Length != count * 2) return McResponse.Invalid;
                    var r = new long[count];
                    for (int i = 0; i < count; i++) r[i] = data[i * 2] | data[i * 2 + 1] << 8;
                    return new(McStatus.Ack, r, 0);
                }
            default:
                return data.Length == 0 ? new(McStatus.Ack, [], 0) : McResponse.Invalid;
        }
    }
}
