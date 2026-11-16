using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

// 리밋 모니터링(6J.8.5 GemHost): S2F45로 정하고 S2F47로 읽는다
public partial class GemHost
{
    /// <summary>Defines or releases limits on the equipment's variables (S2F45 Define Variable Limit Attributes). DATAID is the next number of the counter also used by <see cref="DefineReports"/>.</summary>
    /// <remarks>An empty <paramref name="definitions"/> list releases every limit; a VID whose Limits is null releases all limits of that variable; a limit whose Upper and Lower are both null releases that LIMITID. Send UPPERDB and LOWERDB in the variable's format (the equipment also accepts other numeric formats and A). If anything is wrong the equipment changes nothing and answers VLAACK 1 with the first error of each variable.</remarks>
    /// <param name="definitions">The variables and their limits.</param>
    /// <returns>VLAACK (0 = applied) and the variables in error.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definitions"/> is null.</exception>
    /// <exception cref="ArgumentException">Only one of Upper and Lower is null, or a text ID cannot be sent in a numeric format.</exception>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S2F0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body structure differs from what was expected.</exception>
    public (byte Vlaack, IReadOnlyList<GemLimitError> Errors) DefineLimits(IReadOnlyList<(GemId Vid, IReadOnlyList<(byte LimitId, SecsItem? Upper, SecsItem? Lower)>? Limits)> definitions)
        => GemMessages.ParseS2F46(Ask(LimitRequest(definitions)));

    /// <summary>Defines or releases limits (S2F45). The rules and exceptions are the same as for <see cref="DefineLimits"/>.</summary>
    /// <param name="definitions">The variables and their limits.</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>VLAACK and the variables in error.</returns>
    public async Task<(byte Vlaack, IReadOnlyList<GemLimitError> Errors)> DefineLimitsAsync(IReadOnlyList<(GemId Vid, IReadOnlyList<(byte LimitId, SecsItem? Upper, SecsItem? Lower)>? Limits)> definitions, CancellationToken cancellationToken = default)
    {
        var request = LimitRequest(definitions);
        return GemMessages.ParseS2F46(await AskAsync(request, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Reads the limit attributes of variables (S2F47 Variable Limit Attribute Request). With no VID, the equipment answers every variable that can have limits.</summary>
    /// <param name="vids">The VIDs (none = all).</param>
    /// <returns>One entry per variable; <see cref="GemLimitInfo.IsSupported"/> is false for a variable that cannot have limits.</returns>
    /// <exception cref="ArgumentException">A text VID cannot be sent in a numeric format.</exception>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="SecsTimeoutException">No reply within T3.</exception>
    /// <exception cref="SecsAbortException">The equipment replied with S2F0.</exception>
    /// <exception cref="SecsErrorException">The equipment replied with S9Fx.</exception>
    /// <exception cref="SecsDisconnectedException">The connection dropped while waiting for the reply.</exception>
    /// <exception cref="FormatException">The reply body structure differs from what was expected.</exception>
    public IReadOnlyList<GemLimitInfo> ReadLimits(params GemId[] vids)
        => GemMessages.ParseS2F48(Ask(GemMessages.S2F47(vids, Formats)));

    /// <summary>Reads the limit attributes of variables (S2F47). The rules and exceptions are the same as for <see cref="ReadLimits"/>.</summary>
    /// <param name="vids">The VIDs (null or empty = all).</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <returns>One entry per variable.</returns>
    public async Task<IReadOnlyList<GemLimitInfo>> ReadLimitsAsync(IReadOnlyList<GemId>? vids = null, CancellationToken cancellationToken = default)
    {
        var request = GemMessages.S2F47(vids, Formats);
        return GemMessages.ParseS2F48(await AskAsync(request, cancellationToken).ConfigureAwait(false));
    }

    // S2F45: DATAID는 S2F33·S2F35와 같은 카운터(빌더가 인자 오류로 던지면 번호를 쓰지 않음)
    SecsMessage LimitRequest(IReadOnlyList<(GemId Vid, IReadOnlyList<(byte LimitId, SecsItem? Upper, SecsItem? Lower)>? Limits)> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var formats = Formats;
        return WithNextDataId(id => GemMessages.S2F45(id, definitions, formats));
    }
}
