namespace Devinno.Communication.Secs.Data;

/// <summary>
/// Event arguments for a primary message (odd function) sent by the peer. Reply with <see cref="Reply"/> inside the handler.
/// </summary>
/// <remarks>
/// <para>If <see cref="SecsMessage.ReplyExpected"/> (W-bit) is set but the handler returns without replying (including by an exception), SxF0 (abort) is sent automatically. This keeps the peer from waiting until T3.</para>
/// <para>The handler is called in the order received on a dispatcher thread that is separate from the I/O thread, so calling <c>Send</c> inside the handler does not deadlock. However, there is only one dispatcher per connection, so a long-running handler delays the delivery of the next primary message, and a synchronous <c>Send</c> inside the handler blocks the delivery of the peer's other primary messages while it waits for the reply (up to T3). If both sides do this right after connecting, they can wait for each other until T3, so in the connection sequence only start sending with <c>SendAsync</c> and handle the replies afterward.</para>
/// </remarks>
public sealed class SecsPrimaryEventArgs : EventArgs
{
    readonly Action<SecsMessage> send;
    readonly object gate = new();
    int state;   // 0 응답 가능, 1 응답함, 2 핸들러 끝남
    bool deferred;   // Defer(): 핸들러가 끝나도 응답 가능 상태로 둔다(자동 SxF0 없음)

    internal SecsPrimaryEventArgs(SecsMessage message, byte[] receivedHeader, Action<SecsMessage> send)
    {
        Message = message;
        ReceivedHeader = receivedHeader;
        this.send = send;
    }

    /// <summary>The primary message received (including the system bytes and device ID).</summary>
    public SecsMessage Message { get; }

    // 받은 그대로의 헤더 10바이트(HSMS 헤더, SECS-I는 첫 블록 헤더). GEM 계층이 S9F3/S9F5의 MHEAD로 쓴다.
    internal byte[] ReceivedHeader { get; }

    /// <summary>Sends a reply once inside the handler. Waits until it has been sent (for SECS-I, until the peer's ACK).</summary>
    /// <remarks>The system bytes and device ID of the reply are filled in to match the primary message. The function must be the primary + 1 (a normal reply) or 0 (abort), and the W-bit must not be set.</remarks>
    /// <param name="reply">The reply message (e.g., <c>new SecsMessage(1, 14, false, item)</c>).</param>
    /// <exception cref="ArgumentNullException">reply is null.</exception>
    /// <exception cref="ArgumentException">The stream differs, the function is neither the primary + 1 nor 0, or the W-bit is set.</exception>
    /// <exception cref="InvalidOperationException">The primary message does not expect a reply (no W-bit), a reply was already sent, or the handler has already finished.</exception>
    /// <exception cref="SecsDisconnectedException">The connection over which the primary message was received has already been lost.</exception>
    /// <exception cref="IOException">In SECS-I, the peer did not accept the block up to the retry limit.</exception>
    public void Reply(SecsMessage reply)
    {
        Validate(reply);
        lock (gate)
        {
            if (state != 0) throw new InvalidOperationException(state == 1 ? "이미 응답했습니다." : "핸들러가 끝난 뒤에는 응답할 수 없습니다.");
            state = 1;
        }
        send(reply);
    }

    // Reply의 인자 검사만 한다(상태는 바꾸지 않음). GEM 계층이 응답 권리를 쓰기 전에 미리 검사할 때 쓴다.
    internal void Validate(SecsMessage reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (!Message.ReplyExpected) throw new InvalidOperationException($"S{Message.Stream}F{Message.Function}은(는) 응답을 기다리지 않는 메시지입니다(W비트 없음).");
        if (reply.Stream != Message.Stream || (reply.Function != Message.Function + 1 && reply.Function != 0))
            throw new ArgumentException($"S{Message.Stream}F{Message.Function}의 응답은 S{Message.Stream}F{Message.Function + 1} 또는 S{Message.Stream}F0 이어야 합니다. 현재: S{reply.Stream}F{reply.Function}", nameof(reply));
        if (reply.ReplyExpected) throw new ArgumentException("응답 메시지에는 W비트를 둘 수 없습니다.", nameof(reply));
    }

    // 응답을 미룬다(GEM 계층용): 핸들러가 끝나도 자동 SxF0을 보내지 않고, 그 뒤 아무 스레드에서 Reply를 한 번 부를 수 있다.
    // 이미 응답했거나 핸들러가 끝났으면 아무것도 하지 않는다. 미룬 뒤 Reply를 부르지 않으면 응답은 나가지 않는다(S9F3/F5/F7 뒤 "응답 안 함"에 씀).
    internal void Defer()
    {
        lock (gate)
        {
            if (state == 0) deferred = true;
        }
    }

    // 핸들러가 끝났음을 표시한다. 응답하지 않았고 미루지도 않았으면 true(자동 SxF0 대상). 미뤘으면 응답 가능 상태를 그대로 둔다.
    internal bool Close()
    {
        lock (gate)
        {
            if (state != 0 || deferred) return false;
            state = 2;
            return true;
        }
    }
}
