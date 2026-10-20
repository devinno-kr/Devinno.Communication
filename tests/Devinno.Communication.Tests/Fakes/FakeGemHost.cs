using System.Collections.Concurrent;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Hsms;

namespace Devinno.Communication.Tests.Fakes;

/// 테스트 전용 GEM Host. Plan 7 SecsHsms(원시 송수신, Active·Host)에 SML 응답 대본을 붙인 것이다.
/// 장비가 보낸 1차 메시지는 받은 순서대로 모아 두고(Next), 대본대로 응답한다(Reply). 응답을 붙잡아 두었다가(Hold) 나중에 줄 수도 있다.
/// 대본에 없거나 본문을 null로 둔 W 메시지는 응답하지 않아 전송 계층이 SxF0을 보낸다.
internal sealed class FakeGemHost : IDisposable
{
    readonly BlockingCollection<SecsMessage> received = [];
    readonly ConcurrentDictionary<int, string?> replies = new();
    readonly ConcurrentDictionary<int, bool> holds = new();

    public FakeGemHost(int port, int deviceId = 1)
    {
        Hsms = new SecsHsms
        {
            Role = SecsRole.Host, Mode = HsmsMode.Active, RemoteIP = "127.0.0.1", RemotePort = port, DeviceID = deviceId,
            T3 = 3000, T5 = 200, T6 = 1000, T7 = 2000, T8 = 1000, LinktestInterval = 0,
        };
        // 기본 대본: 장비가 보내는 1차 메시지에 정상 응답
        Reply(1, 13, "<L <B 0> <L>>");
        Reply(1, 1, "<L>");
        Reply(2, 17, "<A \"2026101412000000\">");
        Reply(5, 1, "<B 0>");
        Reply(6, 11, "<B 0>");
        Reply(10, 1, "<B 0>");
        Hsms.PrimaryReceived += OnPrimary;
    }

    public SecsHsms Hsms { get; }

    /// Hold로 붙잡은 1차 메시지(응답하지 않은 채). 테스트가 나중에 Reply한다
    public BlockingCollection<SecsPrimaryEventArgs> Held { get; } = [];

    void OnPrimary(object? sender, SecsPrimaryEventArgs e)
    {
        var m = e.Message;
        int key = m.Stream * 256 + m.Function;
        if (holds.ContainsKey(key))
        {
            e.Defer();
            received.Add(m);
            Held.Add(e);
            return;
        }
        if (m.ReplyExpected && replies.TryGetValue(key, out var body) && body != null)
            e.Reply(new SecsMessage(m.Stream, m.Function + 1, false, SecsItem.ParseSml(body)));
        received.Add(m);                                   // 응답을 보낸 뒤에 넣는다(Next가 돌아오면 응답은 이미 나감)
    }

    /// SxFy에 대한 응답 본문(SML 아이템). null이면 응답하지 않음(전송 계층이 SxF0)
    public void Reply(int stream, int function, string? body) => replies[stream * 256 + function] = body;

    /// SxFy를 받으면 응답하지 않고 Held에 넣는다
    public void Hold(int stream, int function) => holds[stream * 256 + function] = true;

    public void Unhold(int stream, int function) => holds.TryRemove(stream * 256 + function, out _);

    public void Start() => Hsms.Start();

    /// 장비가 보낸 다음 1차 메시지(받은 순서). SxFy가 아니면 실패
    public SecsMessage Next(int stream, int function, int timeoutMs = 3000)
    {
        Assert.True(received.TryTake(out var m, timeoutMs), $"S{stream}F{function}이(가) {timeoutMs}ms 안에 오지 않았습니다.");
        Assert.True(m!.Stream == stream && m.Function == function, $"S{stream}F{function}을(를) 기다렸는데 {m.ToSml()}이(가) 왔습니다.");
        return m;
    }

    /// 다른 메시지는 건너뛰고 SxFy가 올 때까지 기다린다
    public SecsMessage NextOf(int stream, int function, int timeoutMs = 3000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var left = (int)(until - Environment.TickCount64);
            SecsMessage? m = null;
            Assert.True(left > 0 && received.TryTake(out m, left), $"S{stream}F{function}이(가) {timeoutMs}ms 안에 오지 않았습니다.");
            if (m!.Stream == stream && m.Function == function) return m;
        }
    }

    /// waitMs 동안 장비가 아무 1차 메시지도 보내지 않았는지
    public void NoMore(int waitMs = 300) => Assert.False(received.TryTake(out var m, waitMs), $"보내지 않아야 하는데 {m?.ToSml()}");

    /// waitMs 동안 받은 1차 메시지 중 그 Stream이 없는지(다른 메시지는 버림)
    public void NoneOf(int stream, int waitMs = 300)
    {
        var until = Environment.TickCount64 + waitMs;
        int left;
        while ((left = (int)(until - Environment.TickCount64)) > 0)
            if (received.TryTake(out var m, left)) Assert.False(m.Stream == stream, $"S{stream}을(를) 보내지 않아야 하는데 {m.ToSml()}");
    }

    public Task<SecsMessage?> SendAsync(string sml) => Hsms.SendAsync(SecsMessage.ParseSml(sml));

    /// SML 1차 메시지를 보내고 응답을 SML로 돌려준다(응답이 없으면 "(없음)")
    public async Task<string> Ask(string sml) => (await Hsms.SendAsync(SecsMessage.ParseSml(sml)))?.ToSml() ?? "(없음)";

    public void Dispose() => Hsms.Dispose();
}
