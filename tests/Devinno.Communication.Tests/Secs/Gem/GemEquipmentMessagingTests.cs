using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 장비가 GemMessages(2층) 위로 옮겨진 뒤에도 3층 규칙(D2)을 지키는지: 2층은 받지만 장비는 거절하는 모양
public class GemEquipmentMessagingTests
{
    [Fact]
    public async Task Equipment_StillRejects_IntegerArrayIdList_AndWrongIdFormat()
    {
        using var eq = GemKit.Equipment(e =>
        {
            GemKit.Sample(e);
            e.InitialControlState = GemControlState.OnlineRemote;
        });
        using var host = await GemKit.Communicating(eq);
        // ID 목록 요청은 <L ID…>만(정수 배열은 2층 ParseS1F3이 받지만 장비는 S9F7)
        Assert.Equal(7, (await Assert.ThrowsAsync<SecsErrorException>(() => host.SendAsync("S1F3 W <U4 1001 1002>"))).Function);
        // 숫자 포맷(VidFormat U4) 설정에 A ID → S9F7
        Assert.Equal(7, (await Assert.ThrowsAsync<SecsErrorException>(() => host.SendAsync("S1F3 W <L <A \"1001\">>"))).Function);
        // S2F33: 숫자 포맷(RptidFormat U4)에 A RPTID → DRACK 형식 오류
        Assert.Equal($"S2F34 <B 0x{GemAck.DrackInvalidFormat:X2}>", await host.Ask("S2F33 W <L <U4 1> <L <L <A \"1000\"> <L>>>>"));
        // S2F35: 숫자 포맷(CeidFormat U4)에 A CEID → LRACK 형식 오류
        Assert.Equal($"S2F36 <B 0x{GemAck.LrackInvalidFormat:X2}>", await host.Ask("S2F35 W <L <U4 1> <L <L <A \"5001\"> <L>>>>"));
    }
}
