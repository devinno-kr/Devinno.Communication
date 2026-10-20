using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// Plan 7 골든 벡터(Wireshark 이슈 12942 sample_hsms.pcapng: secsgem 장비 ↔ secsgem 호스트 캡처)와 GEM이 만든 본문의 바이트 비교.
/// 같은 값(MDLN "gemequp", SOFTREV "1.0.0", DATAID U1, CEID·RPTID U2, B 값 SV)으로 설정하면 우리 장비의 본문이 캡처와 바이트까지 같아야 한다.
public class GemCaptureBytesTests
{
    static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    /// 캡처 프레임(길이 4 + 헤더 10 + 본문)의 본문
    static byte[] Body(string frame) => Hex(frame)[14..];

    static GemEquipment Eq() => GemKit.Equipment(e =>
    {
        e.ModelName = "gemequp";
        e.SoftwareRevision = "1.0.0";
        e.DataidFormat = SecsFormat.U1;
        e.CeidFormat = SecsFormat.U2;
        e.RptidFormat = SecsFormat.U2;
        e.AddStatusVariable(new GemStatusVariable(10, "B1", SecsFormat.Binary, initialValue: SecsItem.B(0x01)));
        e.AddStatusVariable(new GemStatusVariable(11, "B2", SecsFormat.Binary, initialValue: SecsItem.B(0x01, 0x02)));
        e.AddCollectionEvent(new GemCollectionEvent(1337, "Captured"));
        e.InitialControlState = GemControlState.OnlineRemote;
        e.EventsEnabledByDefault = true;
    });

    [Fact]
    public async Task C8_C11_EquipmentS1F13_And_S1F14_BodiesMatchCapture()
    {
        using var eq = Eq();
        using var host = await GemKit.Connect(eq);
        var s1f13 = host.Next(1, 13);
        Assert.True(s1f13.ReplyExpected);
        Assert.Equal(Body("00 00 00 1c 00 00 81 0d 00 00 73 2c 90 e5 01 02 41 07 67 65 6d 65 71 75 70 41 05 31 2e 30 2e 30"), SecsCodec.Encode(s1f13.Item));
        var s1f14 = await host.SendAsync("S1F13 W <L>");
        Assert.Equal(Body("00 00 00 21 00 00 01 0e 00 00 1e 3a 32 54 01 02 21 01 00 01 02 41 07 67 65 6d 65 71 75 70 41 05 31 2e 30 2e 30"), SecsCodec.Encode(s1f14!.Item));
    }

    [Fact]
    public async Task C13_S6F11_BodyMatchesCapture()
    {
        using var eq = Eq();
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F34 <B 0x00>", await host.Ask("S2F33 W <L <U4 0> <L <L <U2 1000> <L <U4 10> <U4 11>>>>>"));
        Assert.Equal("S2F36 <B 0x00>", await host.Ask("S2F35 W <L <U4 0> <L <L <U2 1337> <L <U2 1000>>>>>"));
        eq.TriggerEvent(1337);
        var s6f11 = host.Next(6, 11);
        Assert.True(s6f11.ReplyExpected);
        Assert.Equal(Body("00 00 00 24 00 00 86 0b 00 00 73 2c 90 e6 01 03 a5 01 01 a9 02 05 39 01 01 01 02 a9 02 03 e8 01 02 21 01 01 21 02 01 02"), SecsCodec.Encode(s6f11.Item));
    }
}
