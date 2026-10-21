namespace Devinno.Communication.Secs.Data;

/// <summary>The result of one <c>SendOrdered</c> transaction (<see cref="Hsms.SecsHsms.SendOrdered(SecsMessage, Action{SecsSendResult})"/>, <see cref="Serial.SecsSerial.SendOrdered(SecsMessage, Action{SecsSendResult})"/>).</summary>
public sealed class SecsSendResult
{
    internal SecsSendResult(SecsMessage primary, SecsMessage? reply, Exception? exception)
    {
        Primary = primary;
        Reply = reply;
        Exception = exception;
    }

    /// <summary>The primary message as sent: a copy with the system bytes and device ID filled in (the object passed to SendOrdered is not modified).</summary>
    public SecsMessage Primary { get; }

    /// <summary>The reply message. null if the W-bit is not set or the transaction failed.</summary>
    public SecsMessage? Reply { get; }

    /// <summary>Why the transaction failed, or null if it succeeded. The exception types are the same as those of <c>Send</c>: <see cref="SecsTimeoutException"/>, <see cref="SecsAbortException"/>, <see cref="SecsErrorException"/>, <see cref="SecsDisconnectedException"/>, or <see cref="IOException"/> (SECS-I retry limit exceeded).</summary>
    public Exception? Exception { get; }

    /// <summary>true if the transaction succeeded (<see cref="Exception"/> is null).</summary>
    public bool IsSuccess => Exception == null;

    // 세션의 순서 있는 완료(wire, 끝난 Task)를 공개 결과로 바꿔 콜백을 부른다. 콜백 예외는 무시한다(D7: PrimaryReceived 핸들러와 같음)
    internal static void Invoke(Action<SecsSendResult> completed, SecsMessage wire, Task<SecsMessage?> task)
    {
        SecsSendResult r = task.IsCompletedSuccessfully
            ? new(wire, task.Result, null)
            : new(wire, null, task.IsFaulted ? task.Exception!.InnerException! : new TaskCanceledException(task));
        try { completed(r); } catch (Exception) { }
    }
}
