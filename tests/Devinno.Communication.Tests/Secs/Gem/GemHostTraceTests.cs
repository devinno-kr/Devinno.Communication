using System.Collections.Concurrent;
using Devinno.Communication.Secs;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// GemHost 트레이스: InitializeTrace·StopTrace(S2F23), TraceDataReceived(S6F1, 표본별로 나눔), 구독자가 없으면 MessageReceived
public class GemHostTraceTests
{
    sealed class Clock
    {
        long now;
        public long Now => Interlocked.Read(ref now);
        public void Add(long ms) => Interlocked.Add(ref now, ms);
    }

    static Task<(GemEquipment Eq, GemHost Host)> Loopback(Clock clock, Action<GemHost>? hostSetup = null) => GemHostKit.Loopback(e =>
    {
        e.InitialControlState = GemControlState.OnlineRemote;
        e.TraceClock = () => clock.Now;
    }, hostSetup);

    [Fact]
    public async Task Loopback_InitializeAndReceive()
    {
        var clock = new Clock();
        var got = new BlockingCollection<GemTraceDataEventArgs>();
        var (eq, host) = await Loopback(clock, h => h.TraceDataReceived += (s, e) => got.Add(e));
        using (eq)
        using (host)
        {
            Assert.Equal(GemAck.TiaackOk, host.InitializeTrace(1, TimeSpan.FromSeconds(1), 4, 2, [2001, 2002]));
            for (int i = 1; i <= 4; i++)
            {
                var left = (ulong)(4 - i);
                clock.Add(1000);
                await TestUtil.Until(() => eq.ActiveTraces.Count == 0 || eq.ActiveTraces[0].RemainingSamples == left, 3000);
            }
            var a = Take(got);
            var b = Take(got);
            Assert.Equal([2UL, 4UL], new[] { a.SampleNumber, b.SampleNumber });
            Assert.Equal((GemId)1, a.Trid);
            Assert.Equal(2, a.Samples.Count);
            Assert.All(a.Samples, s => Assert.Equal(2, s.Count));
            Assert.Equal(SecsItem.F4(25), a.Samples[1][0]);
            Assert.Equal(16, a.Time.Length);
            Assert.Equal(6, a.Message.Stream);
        }
    }

    static GemTraceDataEventArgs Take(BlockingCollection<GemTraceDataEventArgs> got)
    {
        Assert.True(got.TryTake(out var e, 3000), "TraceDataReceived가 오지 않았습니다.");
        return e!;
    }

    [Fact]
    public async Task StopTrace_StopsEquipment()
    {
        var clock = new Clock();
        var (eq, host) = await Loopback(clock);
        using (eq)
        using (host)
        {
            Assert.Equal(GemAck.TiaackOk, await host.InitializeTraceAsync(1, TimeSpan.FromSeconds(1), 4, 2, [2001]));
            Assert.Single(eq.ActiveTraces);
            Assert.Equal(GemAck.TiaackOk, host.StopTrace(1));
            Assert.Empty(eq.ActiveTraces);
            Assert.Equal(GemAck.TiaackOk, await host.StopTraceAsync(1));
        }
    }

    [Fact]
    public async Task InitializeTrace_ReturnsTiaack()
    {
        var clock = new Clock();
        var (eq, host) = await Loopback(clock);
        using (eq)
        using (host)
            Assert.Equal(GemAck.TiaackUnknownSvid, host.InitializeTrace(1, TimeSpan.FromSeconds(1), 4, 2, [9999]));
    }

    [Fact]
    public async Task UnknownTrid_FlatSingleSample()
    {
        var got = new BlockingCollection<GemTraceDataEventArgs>();
        var (fake, host) = await GemHostKit.Fake(h => h.TraceDataReceived += (s, e) => got.Add(e));
        using (fake)
        using (host)
        {
            Assert.Equal("S6F2 <B 0x00>", await fake.Ask("S6F1 W <L <U4 9> <U4 2> <A \"t\"> <L <U4 1> <U4 2> <U4 3> <U4 4>>>"));
            var e = Take(got);
            Assert.Equal((GemId)9, e.Trid);
            Assert.Equal(4, Assert.Single(e.Samples).Count);
        }
    }

    [Fact]
    public async Task KnownTrid_CountMismatch_Flat()
    {
        var got = new BlockingCollection<GemTraceDataEventArgs>();
        var (fake, host) = await GemHostKit.Fake(h => h.TraceDataReceived += (s, e) => got.Add(e), f => f.Reply(2, 23, "<B 0>"));
        using (fake)
        using (host)
        {
            Assert.Equal(GemAck.TiaackOk, host.InitializeTrace(1, TimeSpan.FromSeconds(1), 4, 2, [2001, 2002]));
            await fake.Ask("S6F1 W <L <U4 1> <U4 2> <A \"t\"> <L <U4 1> <U4 2> <U4 3> <U4 4>>>");
            Assert.Equal(2, Take(got).Samples.Count);                                // 2개씩 나눔
            await fake.Ask("S6F1 W <L <U4 1> <U4 3> <A \"t\"> <L <U4 1> <U4 2> <U4 3>>>");
            Assert.Equal(3, Assert.Single(Take(got).Samples).Count);                 // 배수가 아니면 평평하게
            host.StopTrace(1);
            await fake.Ask("S6F1 W <L <U4 1> <U4 4> <A \"t\"> <L <U4 1> <U4 2> <U4 3> <U4 4>>>");
            Assert.Single(Take(got).Samples);                                        // 멈춘 뒤에는 모름
        }
    }

    [Fact]
    public async Task NoSubscriber_GoesToMessageReceived()
    {
        var got = new BlockingCollection<SecsMessage>();
        var (fake, host) = await GemHostKit.Fake(h => h.MessageReceived += (s, e) =>
        {
            got.Add(e.Message);
            e.Reply(new SecsMessage(6, 2, false, SecsItem.B(3)));
        });
        using (fake)
        using (host)
        {
            Assert.Equal("S6F2 <B 0x03>", await fake.Ask("S6F1 W <L <U4 9> <U4 1> <A \"t\"> <L>>"));
            Assert.True(got.TryTake(out var m, 3000));
            Assert.Equal(1, m!.Function);
        }
    }

    [Fact]
    public async Task Malformed_SxF0()
    {
        var (fake, host) = await GemHostKit.Fake(h => h.TraceDataReceived += (s, e) => { });
        using (fake)
        using (host)
        {
            await Assert.ThrowsAsync<SecsAbortException>(() => fake.SendAsync("S6F1 W <L <U4 9> <U4 1> <U4 1> <L>>"));     // GemHost 규칙: 구조 오류는 SxF0
        }
    }

    [Fact]
    public async Task Period_Encoding()
    {
        var (fake, host) = await GemHostKit.Fake(fakeSetup: f => f.Reply(2, 23, "<B 0>"));
        using (fake)
        using (host)
        {
            host.InitializeTrace("T1", TimeSpan.FromMilliseconds(1509), 4, 2, [2001]);
            var m = fake.Next(2, 23);
            Assert.Equal("S2F23 W <L [5] <A \"T1\"> <A \"00000150\"> <U4 4> <U4 2> <L [1] <U4 2001>>>", m.ToSml());
            host.StopTrace("T1");
            Assert.Equal("S2F23 W <L [5] <A \"T1\"> <A \"000000\"> <U4 0> <U4 0> <L [0]>>", fake.Next(2, 23).ToSml());
        }
    }

    [Fact]
    public async Task Arguments_Validated()
    {
        var (fake, host) = await GemHostKit.Fake(fakeSetup: f => f.Reply(2, 23, "<B 0>"));
        using (fake)
        using (host)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => host.InitializeTrace(1, TimeSpan.Zero, 4, 2, [2001]));
            Assert.Throws<ArgumentOutOfRangeException>(() => host.InitializeTrace(1, TimeSpan.FromHours(100), 4, 2, [2001]));
            Assert.Throws<ArgumentOutOfRangeException>(() => host.InitializeTrace(1, TimeSpan.FromSeconds(1), 0, 1, [2001]));
            Assert.Throws<ArgumentOutOfRangeException>(() => host.InitializeTrace(1, TimeSpan.FromSeconds(1), 4, 0, [2001]));
            Assert.Throws<ArgumentNullException>(() => host.InitializeTrace(1, TimeSpan.FromSeconds(1), 4, 2, null!));
            fake.NoMore();
        }
    }
}
