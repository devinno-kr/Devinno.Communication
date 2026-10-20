using System.Text;
using Devinno.Communication.Mitsubishi.Data;
using Devinno.Communication.Mitsubishi.Serial;

namespace Devinno.Communication.Tests.Fakes;

/// 테스트 전용 가상 미쓰비시 PLC. 라이브러리에는 들어가지 않는다.
/// 라이브러리의 McDevice를 쓰지 않고 디바이스 코드·번호를 스스로 해석한다(같은 오해를 공유하지 않도록).
/// 메모리: 비트 디바이스와 워드 디바이스를 따로 둔다. 워드 단위로 비트 디바이스를 읽고 쓰면 앞 번호부터 16점이 한 워드(bit0 = 앞 번호).
/// 1C 요청 해석은 엄격하다: 16진 필드는 정확한 폭의 대문자 0-9A-F만 받고, 점수 한계를 지킨다.
/// 이더넷(3E/1E) 처리와 TCP 서버는 FakeMcPlc.Ethernet.cs에 있다.
internal sealed partial class FakeMcPlc : IDisposable
{
    const char ENQ = (char)0x05, STX = (char)0x02, ETX = (char)0x03, ACK = (char)0x06, NAK = (char)0x15;

    static readonly HashSet<string> BitCodes = ["X", "Y", "M", "L", "F", "V", "B", "S", "SM", "TS", "TC", "SS", "SC", "CS", "CC", "SB"];
    static readonly HashSet<string> WordCodes = ["D", "W", "R", "ZR", "Z", "SD", "TN", "SN", "CN", "SW"];
    static readonly HashSet<string> HexCodes = ["B", "W", "SB", "SW", "ZR"];
    static readonly HashSet<string> Serial1CCodes = ["X", "Y", "M", "L", "S", "B", "F", "D", "W", "R", "TN", "TS", "TC", "CN", "CS", "CC"];

    readonly Dictionary<(string Code, int Number), bool> bits = [];
    readonly Dictionary<(string Code, int Number), ushort> words = [];
    readonly object sync = new();
    int requests, hostAcks;

    /// 국번 (1C)
    public int Station { get; set; }
    /// X/Y 번호를 8진수로 볼지 (Get/Set 이름, 1C 디바이스 필드). 바이너리(3E/1E)는 번호 값 그대로라 상관없다.
    public bool XYOctal { get; set; } = true;
    /// 설정하면 모든 요청에 이 코드로 오류 응답 (1C: NAK 코드, 3E: 종료 코드, 1E: 종료 코드 또는 0x5Bxx = 5BH + 이상 코드)
    public int? ForceError { get; set; }
    /// 무응답
    public bool Silent { get; set; }
    /// 1C 응답의 섬체크를 일부러 틀리게
    public bool BadSum { get; set; }
    /// 받은 요청 수 (1C ENQ 프레임, 3E/1E 요청)
    public int Requests => Volatile.Read(ref requests);
    /// 받은 컴퓨터 ACK 수 (1C 읽기 응답 뒤)
    public int HostAcks => Volatile.Read(ref hostAcks);

    #region Memory
    (string Code, int Number) Key(string name)
    {
        var n = name.ToUpperInvariant();
        var code = n.Length > 2 && (BitCodes.Contains(n[..2]) || WordCodes.Contains(n[..2])) ? n[..2] : n[..1];
        int radix = code is "X" or "Y" ? (XYOctal ? 8 : 16) : HexCodes.Contains(code) ? 16 : 10;
        return (code, Convert.ToInt32(n[code.Length..], radix));
    }

    public long Get(string device)
    {
        lock (sync)
        {
            var (code, number) = Key(device);
            return WordCodes.Contains(code) ? words.GetValueOrDefault((code, number)) : bits.GetValueOrDefault((code, number)) ? 1 : 0;
        }
    }

    public void Set(string device, long value)
    {
        lock (sync)
        {
            var (code, number) = Key(device);
            if (WordCodes.Contains(code)) words[(code, number)] = (ushort)value;
            else bits[(code, number)] = value != 0;
        }
    }

    // 반드시 sync 안에서 호출한다. 워드 단위: 비트 디바이스는 head+16k부터 16점이 k번째 워드(bit0 = 앞 번호)
    ushort GetWord(string code, int head, int k)
    {
        if (WordCodes.Contains(code)) return words.GetValueOrDefault((code, head + k));
        int v = 0;
        for (int b = 0; b < 16; b++) if (bits.GetValueOrDefault((code, head + 16 * k + b))) v |= 1 << b;
        return (ushort)v;
    }

    void SetWord(string code, int head, int k, ushort v)
    {
        if (WordCodes.Contains(code)) { words[(code, head + k)] = v; return; }
        for (int b = 0; b < 16; b++) bits[(code, head + 16 * k + b)] = ((v >> b) & 1) != 0;
    }

    /// 공통 실행. 읽기는 값, 쓰기는 빈 배열. 모르는 디바이스나 비트 명령에 워드 디바이스면 InvalidDataException.
    long[] Execute(McCommand command, string code, int head, int count, long[]? data)
    {
        lock (sync)
        {
            bool word = WordCodes.Contains(code);
            if (!word && !BitCodes.Contains(code)) throw new InvalidDataException($"모르는 디바이스 {code}");
            if (word && command is McCommand.BR or McCommand.BW) throw new InvalidDataException("비트 명령에 워드 디바이스");
            var r = new long[command is McCommand.BR or McCommand.WR ? count : 0];
            for (int i = 0; i < count; i++)
            {
                switch (command)
                {
                    case McCommand.BR: r[i] = bits.GetValueOrDefault((code, head + i)) ? 1 : 0; break;
                    case McCommand.BW: bits[(code, head + i)] = data![i] != 0; break;
                    case McCommand.WR: r[i] = GetWord(code, head, i); break;
                    default: SetWord(code, head, i, (ushort)data![i]); break;
                }
            }
            return r;
        }
    }
    #endregion

    #region 1C
    static int Sum(string s) => Encoding.ASCII.GetBytes(s).Sum(b => (int)b) & 0xFF;

    // 정확히 width자, 대문자 0-9A-F만 (소문자·0x·공백·부호 불가)
    static bool IsHex(string s, int width) => s.Length == width && s.All(c => c is (>= '0' and <= '9') or (>= 'A' and <= 'F'));

    /// 1C 요청에 대한 응답. 국번이 다르거나(형식 오류 포함) Silent면 null. 컴퓨터 ACK(읽기 응답 뒤)를 받으면 HostAcks를 세고 null.
    public byte[]? Handle1C(byte[] request, McSerialFormat format, bool sumCheck)
    {
        var s = Encoding.ASCII.GetString(request);
        string crlf = format == McSerialFormat.Format4 ? "\r\n" : "";
        if (!s.EndsWith(crlf, StringComparison.Ordinal)) return null;
        s = s[..^crlf.Length];
        byte[] Frame(string x) => Encoding.ASCII.GetBytes(x + crlf);
        string head = Station.ToString("X2") + "FF";
        byte[] Nak(string code) => Frame(NAK + head + code);

        if (s.Length == 5 && s[0] == ACK)
        {
            if (s[1..] == head) Interlocked.Increment(ref hostAcks);
            return null;
        }
        if (s.Length < 1 || s[0] != ENQ) return null;
        Interlocked.Increment(ref requests);
        if (Silent) return null;

        try
        {
            string body = sumCheck ? s[1..^2] : s[1..];
            if (body.Length < 14 || !IsHex(body[..2], 2) || Convert.ToInt32(body[..2], 16) != Station) return null;
            if (sumCheck && (!IsHex(s[^2..], 2) || Convert.ToInt32(s[^2..], 16) != Sum(body))) return Nak("02");
            if (body.Substring(2, 2) != "FF") return Nak("10");
            if (ForceError is int fe) return Nak(fe.ToString("X2"));
            string cmd = body.Substring(4, 2);
            if (cmd is not ("BR" or "WR" or "BW" or "WW")) return Nak("03");
            if (!IsHex(body.Substring(6, 1), 1)) return Nak("06");   // 메시지 대기
            string field = body.Substring(7, 5);
            string code = field[..2] is "TN" or "TS" or "TC" or "CN" or "CS" or "CC" ? field[..2] : field[..1];
            if (!Serial1CCodes.Contains(code)) return Nak("06");
            string digits = field[code.Length..];
            if (!IsHex(digits, digits.Length)) return Nak("06");
            int radix = code is "X" or "Y" ? (XYOctal ? 8 : 16) : HexCodes.Contains(code) ? 16 : 10;
            int number = Convert.ToInt32(digits, radix);
            string countText = body.Substring(12, 2);
            if (!IsHex(countText, 2)) return Nak("06");
            int count = Convert.ToInt32(countText, 16);
            var command = Enum.Parse<McCommand>(cmd);
            if (count == 0)
            {
                if (command != McCommand.BR) return Nak("06");   // 00 = 256 은 BR만
                count = 256;
            }
            bool wordDevice = WordCodes.Contains(code);
            int max = command switch
            {
                McCommand.BR => 256,
                McCommand.BW => 160,
                McCommand.WR => wordDevice ? 64 : 32,
                _ => wordDevice ? 64 : 10,
            };
            if (count > max) return Nak("06");
            string data = body[14..];
            long[]? values = null;
            if (command == McCommand.BW)
            {
                if (data.Length != count || data.Any(c => c is not ('0' or '1'))) return Nak("06");
                values = data.Select(c => (long)(c - '0')).ToArray();
            }
            else if (command == McCommand.WW)
            {
                if (data.Length != count * 4 || !IsHex(data, data.Length)) return Nak("06");
                values = Enumerable.Range(0, count).Select(i => (long)Convert.ToInt32(data.Substring(i * 4, 4), 16)).ToArray();
            }
            else if (data.Length != 0) return Nak("06");

            var r = Execute(command, code, number, count, values);
            if (values != null) return Frame(ACK + head);
            string payload = head + string.Concat(r.Select(v => command == McCommand.BR ? v.ToString() : v.ToString("X4"))) + ETX;
            if (sumCheck) payload += ((Sum(payload) + (BadSum ? 1 : 0)) & 0xFF).ToString("X2");
            return Frame(STX + payload);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidDataException or OverflowException)
        {
            return Nak("06");
        }
    }
    #endregion

    partial void StopServer();

    public void Dispose() => StopServer();
}
