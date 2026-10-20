using System.Collections.Concurrent;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Secs.Hsms;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Secs.Gem;

/// GemHost 테스트 클래스끼리는 차례로 실행한다(모두 스레드를 여럿 띄우는 루프백이라, 한꺼번에 돌면 부하 아래에서 다른 SECS 타이밍 테스트까지 흔들림)
[CollectionDefinition(nameof(GemHostCollection))]
public sealed class GemHostCollection { }

/// GEM Host 테스트 도구: 루프백 HSMS Host(Active, 127.0.0.1), 가짜 장비·우리 GemEquipment 상대, 이벤트 기록
internal static class GemHostKit
{
    public static SecsHsms Transport(int port) => new()
    {
        Mode = HsmsMode.Active, RemoteIP = "127.0.0.1", RemotePort = port, DeviceID = 1,
        T3 = 3000, T5 = 200, T6 = 1000, T7 = 2000, T8 = 1000, LinktestInterval = 0,
    };

    /// AutoConnectSequence는 끈다(접속 시퀀스 테스트만 setup에서 켬). 그래야 가짜 장비가 받는 메시지가 테스트가 보낸 것뿐이다
    public static GemHost Host(int port, Action<GemHost>? setup = null)
    {
        var host = new GemHost(Transport(port)) { AutoConnectSequence = false };
        setup?.Invoke(host);
        return host;
    }

    /// 가짜 장비를 시작하고 Host를 Selected까지 접속한다
    public static async Task<(FakeGemEquipment Fake, GemHost Host)> Fake(Action<GemHost>? hostSetup = null, Action<FakeGemEquipment>? fakeSetup = null)
    {
        var fake = new FakeGemEquipment();
        fakeSetup?.Invoke(fake);
        fake.Start();
        var host = Host(fake.Port, hostSetup);
        host.Start();
        await TestUtil.Until(() => host.IsOpen && fake.Hsms.IsOpen, 5000);
        return (fake, host);
    }

    /// 우리 GemEquipment(표본 정의)를 시작하고 Host를 접속해 통신 수립까지 간다(장비가 보낸 S1F13에 Host가 자동 응답)
    public static async Task<(GemEquipment Eq, GemHost Host)> Loopback(Action<GemEquipment>? eqSetup = null, Action<GemHost>? hostSetup = null)
    {
        var eq = GemKit.Equipment(e =>
        {
            GemKit.Sample(e);
            eqSetup?.Invoke(e);
        });
        eq.Start();
        var host = Host(GemKit.Port(eq), hostSetup);
        host.Start();
        await TestUtil.Until(() => eq.CommunicationState == GemCommunicationState.Communicating && host.IsCommunicating, 5000);
        return (eq, host);
    }

    /// 공용 이벤트(통신 상태, ProtocolError)를 받은 순서대로 기록한다
    public static ConcurrentQueue<string> Log(GemHost host)
    {
        var log = new ConcurrentQueue<string>();
        host.CommunicationStateChanged += (s, e) => log.Enqueue($"Comm {e.Previous}->{e.Current}");
        host.ProtocolError += (s, e) => log.Enqueue($"Error {e.Reason}");
        return log;
    }
}
