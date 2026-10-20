using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Hsms;
using Devinno.Communication.Secs.Serial;

namespace Devinno.Communication.Secs.Gem;

// SecsHsms·SecsSerial을 같은 모양으로 다루는 대리자 묶음(상태 없음). 두 전송 클래스에 공통 베이스·interface가 없어서 둔다(스펙 3장).
internal sealed class GemTransport
{
    public required object Target { get; init; }
    // 동기 보내기: 호출 스레드에서 송신 완료·응답을 기다린다(스레드풀을 거치지 않음, Plan 7)
    public required Func<SecsMessage, SecsMessage?> Send { get; init; }
    public required Func<SecsMessage, CancellationToken, Task<SecsMessage?>> SendAsync { get; init; }
    // Role을 Host로 되돌린다(GemHost.Start가 전송 시작 전에 부름)
    public required Action EnsureHost { get; init; }
    public required Func<bool> IsOpen { get; init; }
    public required Func<bool> IsStart { get; init; }
    public required Action Start { get; init; }
    public required Action Stop { get; init; }
    public required Action Dispose { get; init; }
    // (1차 메시지, 열림, 닫힘) 핸들러 붙이기·떼기
    public required Action<EventHandler<SecsPrimaryEventArgs>, EventHandler, EventHandler> Attach { get; init; }
    public required Action<EventHandler<SecsPrimaryEventArgs>, EventHandler, EventHandler> Detach { get; init; }

    public static GemTransport Of(SecsHsms h) => new()
    {
        Target = h,
        Send = h.Send,
        SendAsync = h.SendAsync,
        EnsureHost = () => h.Role = SecsRole.Host,
        IsOpen = () => h.IsOpen,
        IsStart = () => h.IsStart,
        Start = h.Start,
        Stop = h.Stop,
        Dispose = h.Dispose,
        Attach = (p, o, c) => { h.PrimaryReceived += p; h.DeviceOpened += o; h.DeviceClosed += c; },
        Detach = (p, o, c) => { h.PrimaryReceived -= p; h.DeviceOpened -= o; h.DeviceClosed -= c; },
    };

    public static GemTransport Of(SecsSerial s) => new()
    {
        Target = s,
        Send = s.Send,
        SendAsync = s.SendAsync,
        EnsureHost = () => s.Role = SecsRole.Host,
        IsOpen = () => s.IsOpen,
        IsStart = () => s.IsStart,
        Start = s.Start,
        Stop = s.Stop,
        Dispose = s.Dispose,
        Attach = (p, o, c) => { s.PrimaryReceived += p; s.DeviceOpened += o; s.DeviceClosed += c; },
        Detach = (p, o, c) => { s.PrimaryReceived -= p; s.DeviceOpened -= o; s.DeviceClosed -= c; },
    };
}
