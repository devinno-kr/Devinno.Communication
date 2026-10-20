using System.Collections.Concurrent;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Serial;

namespace Devinno.Communication.Tests.Fakes;

/// 메모리 시리얼 회선의 한쪽 끝. Pair()로 서로 이어진 두 끝을 만들고, SecsSerial.OpenLine에 ToLine()을 넣는다.
/// 테스트가 직접 쓰는 끝은 규칙을 어기는 SECS-I 상대(늦은 응답, 깨진 블록, 경합, 중복 블록)를 흉내 낸다.
internal sealed class MemoryLine
{
    readonly BlockingCollection<byte> inbox = [];
    MemoryLine? peer;
    volatile bool closed;

    /// 이 끝이 쓰는 바이트를 상대에게 넘기기 전에 바꾼다(null을 돌려주면 버림)
    public Func<byte[], byte[]?>? Tap { get; set; }
    /// 이 끝이 쓴 바이트 묶음(Write 한 번 = 한 항목, Tap 적용 전)
    public ConcurrentQueue<byte[]> Written { get; } = new();

    public static (MemoryLine A, MemoryLine B) Pair()
    {
        var a = new MemoryLine();
        var b = new MemoryLine();
        a.peer = b;
        b.peer = a;
        return (a, b);
    }

    public SecsSerialLine ToLine() => new(ReadByte, Write, Close);

    public int ReadByte(int timeoutMs)
    {
        if (closed) throw new IOException("회선이 닫혔습니다.");
        return inbox.TryTake(out var b, timeoutMs) ? b : closed ? throw new IOException("회선이 닫혔습니다.") : -1;
    }

    public void Write(byte[] bytes)
    {
        if (closed) throw new IOException("회선이 닫혔습니다.");
        Written.Enqueue(bytes);
        var t = Tap == null ? bytes : Tap(bytes);
        if (t == null || peer!.closed) return;
        foreach (var b in t) peer.inbox.Add(b);
    }

    public void Close() => closed = true;

    /// 블록 묶음(길이 2 이상)만 센다
    public int BlocksWritten => Written.Count(w => w.Length > 1);

    #region 상대 흉내 (테스트 쪽 끝에서 씀)
    public void Expect(byte expected, int timeoutMs = 3000)
    {
        int b = ReadByte(timeoutMs);
        Assert.True(b == expected, $"0x{expected:X2}를 기다렸는데 {(b < 0 ? "시간 초과" : $"0x{b:X2}")}");
    }

    /// 상대(우리 SecsSerial)가 보내는 블록 하나를 받는다: ENQ → EOT → 블록 → ACK
    public (SecsIHeader Header, byte[] Data) ReceiveBlock(int timeoutMs = 3000)
    {
        Expect(SecsIBlock.ENQ, timeoutMs);
        Write([SecsIBlock.EOT]);
        int len = ReadByte(3000);
        Assert.InRange(len, 10, 254);
        var buf = new byte[len + 2];
        for (int i = 0; i < buf.Length; i++)
        {
            int c = ReadByte(3000);
            Assert.True(c >= 0, "블록이 중간에 끊겼습니다.");
            buf[i] = (byte)c;
        }
        Assert.Equal(SecsIBlock.Checksum(buf.AsSpan(0, len)), buf[len] << 8 | buf[len + 1]);
        Write([SecsIBlock.ACK]);
        return (SecsIHeader.Parse(buf.AsSpan(0, 10)), buf[10..len]);
    }

    /// 메시지 하나(E비트까지)를 받아 해석한다
    public (SecsIHeader First, SecsMessage Message) ReceiveMessage(int timeoutMs = 3000)
    {
        var (first, data) = ReceiveBlock(timeoutMs);
        var body = new List<byte>(data);
        var last = first;
        while (!last.EBit)
        {
            var (h, d) = ReceiveBlock();
            Assert.True(h.IsNextOf(last));
            body.AddRange(d);
            last = h;
        }
        var m = new SecsMessage(first.Stream, first.Function, first.WBit, SecsCodec.Decode(body.ToArray())).WithHeader(first.SystemBytes, first.DeviceID);
        return (first, m);
    }

    /// 블록 하나를 보낸다: ENQ → (EOT) → 블록 → (ACK). 받은 응답 바이트를 돌려준다
    public int SendBlock(byte[] block)
    {
        Write([SecsIBlock.ENQ]);
        Expect(SecsIBlock.EOT);
        Write(block);
        return ReadByte(3000);
    }

    /// 메시지를 블록으로 나눠 모두 보낸다(모두 ACK여야 함)
    public void SendMessage(SecsMessage wire, bool rbit)
    {
        foreach (var b in SecsIBlock.Split(wire, rbit)) Assert.Equal(SecsIBlock.ACK, SendBlock(b));
    }
    #endregion
}
