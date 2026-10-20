using System.Collections.Concurrent;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Fakes;

/// 테스트 전용 GEM 전송. 실제 소켓으로는 만들 수 없는 순서(지난 연결의 송신 실패가 DeviceClosed·DeviceOpened 뒤에 옴)를 테스트가 직접 만든다.
/// 열림·닫힘·1차 메시지는 붙은 핸들러를 테스트 스레드에서 바로 부르고, 보낸 메시지는 완료 콜백과 함께 Sent에 쌓는다(완료는 테스트가 부름).
internal sealed class FakeGemTransport
{
    EventHandler<SecsPrimaryEventArgs>? onPrimary;
    EventHandler? onOpened, onClosed;
    volatile bool open, started;

    public FakeGemTransport()
    {
        Transport = new GemTransport
        {
            Target = this,
            Send = _ => throw new NotSupportedException("FakeGemTransport는 동기 Send를 지원하지 않습니다."),
            SendAsync = (m, _) =>
            {
                var tcs = new TaskCompletionSource<SecsMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
                Sent.Add((m, t => Copy(t, tcs)));
                return tcs.Task;
            },
            SendOrdered = (m, completed) => Sent.Add((m, completed)),
            Post = a => a(),
            IsDispatcherThread = () => false,
            EnsureHost = () => { },
            IsOpen = () => open,
            IsStart = () => started,
            Start = () => started = true,
            Stop = () => started = false,
            Dispose = () => started = false,
            Attach = (p, o, c) => { onPrimary = p; onOpened = o; onClosed = c; },
            Detach = (p, o, c) => { onPrimary = null; onOpened = null; onClosed = null; },
        };
    }

    public GemTransport Transport { get; }

    /// GEM이 보낸 메시지와 그 완료(테스트가 이미 끝난 Task로 부른다)
    public BlockingCollection<(SecsMessage Message, Action<Task<SecsMessage?>> Completed)> Sent { get; } = [];

    /// GEM이 1차 메시지에 보낸 응답
    public BlockingCollection<SecsMessage> Replies { get; } = [];

    public void Open()
    {
        open = true;
        onOpened?.Invoke(this, EventArgs.Empty);
    }

    public void Close()
    {
        open = false;
        onClosed?.Invoke(this, EventArgs.Empty);
    }

    public void Primary(SecsMessage m) => onPrimary?.Invoke(this, new SecsPrimaryEventArgs(m, new byte[10], Replies.Add));

    /// 다음으로 보낸 메시지. SxFy가 아니면 실패
    public (SecsMessage Message, Action<Task<SecsMessage?>> Completed) Next(int stream, int function, int timeoutMs = 3000)
    {
        Assert.True(Sent.TryTake(out var s, timeoutMs), $"S{stream}F{function}이(가) {timeoutMs}ms 안에 오지 않았습니다.");
        Assert.True(s.Message.Stream == stream && s.Message.Function == function, $"S{stream}F{function}을(를) 기다렸는데 {s.Message.ToSml()}이(가) 왔습니다.");
        return s;
    }

    static void Copy(Task<SecsMessage?> t, TaskCompletionSource<SecsMessage?> tcs)
    {
        if (t.IsCompletedSuccessfully) tcs.SetResult(t.Result);
        else tcs.SetException(t.Exception!.InnerExceptions);
    }
}
