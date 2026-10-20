using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 받은 메시지 처리 표(스펙 6E.3.5)의 모든 GEM 메시지에 처리기가 있는지: ON-LINE REMOTE에서 S9F5(모르는 Function)가 나오면 안 된다
public class GemMessageTableTests
{
    [Fact]
    public async Task EveryGemMessage_IsHandled_NoS9F5()
    {
        using var eq = GemKit.Equipment(e =>
        {
            GemKit.Sample(e);
            e.InitialControlState = GemControlState.OnlineRemote;
        });
        using var host = await GemKit.Communicating(eq);
        var handled = new List<string>();
        foreach (var stream in GemCatalog.GemStreams)
        {
            for (int function = 1; function < 256; function += 2)
            {
                if (!GemCatalog.IsGemMessage(stream, function)) continue;
                try { await host.SendAsync($"S{stream}F{function} W <L>"); }
                catch (SecsErrorException ex) { Assert.NotEqual(5, ex.Function); }      // 본문이 틀려 S9F7은 됨
                catch (SecsAbortException) { }                                           // 상태에 따라 SxF0도 됨(예: S1F15 뒤 OFF-LINE)
                handled.Add($"S{stream}F{function}");
                if (stream == 1 && function == 15) await host.Ask("S1F17 W");          // OFF-LINE으로 갔으면 다시 ON-LINE
            }
        }
        Assert.Equal(25, handled.Count);
        Assert.Equal(5, (await Assert.ThrowsAsync<SecsErrorException>(() => host.SendAsync("S6F11 W <L>"))).Function);   // 장비가 보내는 쪽 메시지는 모름
    }
}
