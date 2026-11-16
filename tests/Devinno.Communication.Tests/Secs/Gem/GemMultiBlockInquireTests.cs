using System.Collections.Concurrent;
using System.Xml.Linq;
using System.Xml.Schema;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;
using Devinno.Communication.Secs.Hsms;
using Devinno.Communication.Secs.Serial;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Secs.Gem;

/// SECS-I 다중 블록 문의 S6F5(Plan 17 Task 4): 244바이트를 넘는 S6F11 앞에만, 거절·실패는 원래 S6F11 기준, 스풀 머리
public class GemMultiBlockInquireTests
{
    // 메모리 회선 위 SECS-I 장비(MultiBlockInquire 켬)와 Host. Host는 S1F13·S6F11에 답하고 S6F5에는 Grants 대본으로 답한다(비면 답하지 않음)
    sealed class Ctx : IDisposable
    {
        public GemEquipment Eq = null!;
        public SecsSerial Host = null!;
        public readonly BlockingCollection<SecsMessage> Got = new();
        public readonly ConcurrentQueue<byte> Grants = new();
        public ConcurrentQueue<string> Log = null!;
        public volatile bool AnswerS1F13 = true;

        public static async Task<Ctx> Start(Action<GemEquipment>? more = null)
        {
            var c = new Ctx();
            var (a, b) = MemoryLine.Pair();
            var line = new SecsSerial { DeviceID = 1, T1 = 200, T2 = 1000, T3 = 2000, T4 = 2000 };
            line.OpenLine = a.ToLine;
            c.Eq = new GemEquipment(line) { ModelName = "DV-100", SoftwareRevision = "1.0.0", InitialControlState = GemControlState.OnlineRemote, EventsEnabledByDefault = true, MultiBlockInquire = true };
            GemKit.Sample(c.Eq);
            c.Eq.AddStatusVariable(new GemStatusVariable(2003, "Recipe", SecsFormat.ASCII, initialValue: SecsItem.A("")));
            more?.Invoke(c.Eq);
            c.Log = GemKit.Log(c.Eq);
            c.Host = new SecsSerial { Role = SecsRole.Host, DeviceID = 1, T1 = 200, T2 = 1000, T3 = 2000, T4 = 2000 };
            c.Host.OpenLine = b.ToLine;
            c.Host.PrimaryReceived += (s, e) =>
            {
                var m = e.Message;
                c.Got.Add(m);
                if (m is { Stream: 1, Function: 13 } && c.AnswerS1F13) e.Reply(new SecsMessage(1, 14, false, SecsItem.L(SecsItem.B(0), SecsItem.L())));
                else if (m is { Stream: 6, Function: 11 }) e.Reply(new SecsMessage(6, 12, false, SecsItem.B(0)));
                else if (m is { Stream: 6, Function: 5 } && c.Grants.TryDequeue(out var g)) e.Reply(GemMessages.S6F6(g));
                else e.Defer();                                                                  // 답하지 않음(장비는 T3)
            };
            c.Host.Start();
            c.Eq.Start();
            await TestUtil.Until(() => c.Eq.CommunicationState == GemCommunicationState.Communicating, 5000);
            // CE 5001에 SV 2003(A)을 리포트로 연결
            await c.Host.SendAsync(SecsMessage.ParseSml("S2F33 W <L <U4 1> <L <L <U4 7001> <L <U4 2003>>>>>"));
            await c.Host.SendAsync(SecsMessage.ParseSml("S2F35 W <L <U4 1> <L <L <U4 5001> <L <U4 7001>>>>>"));
            return c;
        }

        // 본문이 정확히 bodyBytes 바이트인 S6F11(5001)이 되도록 SV 2003을 채운다
        public void SetBodySize(int bodyBytes)
        {
            int Body(int n) => SecsCodec.Encode(GemMessages.S6F11(1, 5001, [((GemId)7001, [SecsItem.A(new string('x', n))])]).Item).Length;
            int n = bodyBytes - Body(0);
            while (n > 0 && Body(n) > bodyBytes) n--;                                            // 255자를 넘으면 A 길이 바이트가 하나 늘어남
            Assert.Equal(bodyBytes, Body(n));
            Eq.SetValue(2003, SecsItem.A(new string('x', n)));
        }

        public SecsMessage Next(int stream, int function, int timeoutMs = 5000)
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (Got.TryTake(out var m, Math.Max(1, (int)(until - DateTime.UtcNow).TotalMilliseconds)))
                if (m.Stream == stream && m.Function == function) return m;
            throw new TimeoutException($"S{stream}F{function}을(를) 받지 못했습니다.");
        }

        public void NoneOf(int stream, int function, int waitMs = 500)
        {
            var until = DateTime.UtcNow.AddMilliseconds(waitMs);
            while (Got.TryTake(out var m, Math.Max(1, (int)(until - DateTime.UtcNow).TotalMilliseconds)))
                Assert.False(m.Stream == stream && m.Function == function, $"S{stream}F{function}을(를) 보내지 않아야 합니다.");
        }

        public void Dispose()
        {
            Eq.Dispose();
            Host.Dispose();
        }
    }

    [Fact]
    public async Task Large_S6F11_InquiresFirst()
    {
        using var c = await Ctx.Start();
        c.SetBodySize(300);
        c.Grants.Enqueue(GemAck.Grant6Granted);
        Assert.True(c.Eq.TriggerEvent(5001));
        var s6f5 = c.Next(6, 5);
        var s6f11 = c.Next(6, 11);
        Assert.Equal(s6f11.Item!.Items[0], s6f5.Item!.Items[0]);                                  // DATAID 아이템 그대로
        Assert.Equal((ulong)SecsCodec.Encode(s6f11.Item).Length, s6f5.Item.Items[1].GetUInt64()); // DATALENGTH = 본문 바이트
        Assert.Equal(300UL, s6f5.Item.Items[1].GetUInt64());
    }

    [Fact]
    public async Task Small_S6F11_NoInquiry_Boundary244()
    {
        using var c = await Ctx.Start();
        c.SetBodySize(244);
        Assert.True(c.Eq.TriggerEvent(5001));
        c.Next(6, 11);
        c.NoneOf(6, 5);
        c.SetBodySize(245);
        c.Grants.Enqueue(GemAck.Grant6Granted);
        Assert.True(c.Eq.TriggerEvent(5001));
        c.Next(6, 5);
        c.Next(6, 11);
    }

    [Fact]
    public async Task Refused_NotSent_SendFailedNotGranted()
    {
        using var c = await Ctx.Start();
        c.SetBodySize(300);
        c.Grants.Enqueue(GemAck.Grant6Busy);
        Assert.True(c.Eq.TriggerEvent(5001));
        c.Next(6, 5);
        await TestUtil.Until(() => GemKit.Only(c.Log, "SendFailed").Length == 1, 5000);
        Assert.Equal("SendFailed S6F11 NotGranted 1", GemKit.Only(c.Log, "SendFailed").Single());
        c.NoneOf(6, 11);
        c.Grants.Enqueue(GemAck.Grant6Granted);                                                    // 다음 이벤트는 다시 문의
        Assert.True(c.Eq.TriggerEvent(5001));
        c.Next(6, 5);
        c.Next(6, 11);
    }

    [Fact]
    public async Task Off_NoInquiry()
    {
        using var c = await Ctx.Start(e => e.MultiBlockInquire = false);
        c.SetBodySize(300);
        Assert.True(c.Eq.TriggerEvent(5001));
        c.Next(6, 11);
        c.NoneOf(6, 5);
    }

    [Fact]
    public async Task MultiBlockInquire_Hsms_NoInquiry()
    {
        using var eq = GemKit.Equipment(e =>
        {
            GemKit.Sample(e);
            e.AddStatusVariable(new GemStatusVariable(2003, "Recipe", SecsFormat.ASCII, initialValue: SecsItem.A(new string('x', 400))));
            e.InitialControlState = GemControlState.OnlineRemote;
            e.EventsEnabledByDefault = true;
            e.MultiBlockInquire = true;
        });
        using var host = await GemKit.Communicating(eq);
        Assert.Equal("S2F34 <B 0x00>", await host.Ask("S2F33 W <L <U4 1> <L <L <U4 7001> <L <U4 2003>>>>>"));
        Assert.Equal("S2F36 <B 0x00>", await host.Ask("S2F35 W <L <U4 1> <L <L <U4 5001> <L <U4 7001>>>>>"));
        Assert.True(eq.TriggerEvent(5001));
        host.Next(6, 11);
        host.NoneOf(6);
    }

    [Fact]
    public async Task Inquiry_T3_ReportsOriginalMessage()
    {
        using var c = await Ctx.Start();
        c.SetBodySize(300);
        Assert.True(c.Eq.TriggerEvent(5001));                                                     // Grants 없음 → S6F5에 답하지 않음 → T3
        c.Next(6, 5);
        await TestUtil.Until(() => GemKit.Only(c.Log, "SendFailed").Length > 0, 6000);
        Assert.Equal("SendFailed S6F11 Timeout", GemKit.Only(c.Log, "SendFailed").First());
        Assert.DoesNotContain(GemKit.Only(c.Log, "SendFailed"), l => l.Contains("S6F5"));
    }

    [Fact]
    public async Task Inquiry_Disable_ReportsOriginalMessage()
    {
        using var c = await Ctx.Start();
        c.SetBodySize(300);
        Assert.True(c.Eq.TriggerEvent(5001));
        c.Next(6, 5);                                                                             // 답하지 않는 사이
        c.Eq.DisableCommunication();
        await TestUtil.Until(() => GemKit.Only(c.Log, "SendFailed").Length > 0, 3000);
        Assert.Equal("SendFailed S6F11 Disabled", GemKit.Only(c.Log, "SendFailed").Single());
    }

    [Fact]
    public async Task Spool_InquiryRefused_HeadRemoved_Continues()
    {
        var dir = Path.Combine(Path.GetTempPath(), "devinno-spool-" + Guid.NewGuid());
        try
        {
            using var c = await Ctx.Start(e => e.SpoolDirectory = dir);
            c.Eq.SetConstant(1101, SecsItem.U2(1));                                               // CommDelay 1초
            await c.Host.SendAsync(SecsMessage.ParseSml("S2F43 W <L <L <U1 6> <L>>>"));            // S6 전부 스풀 대상
            c.SetBodySize(300);
            c.AnswerS1F13 = false;
            Assert.True(c.Eq.TriggerEvent(5001));                                                 // S6F5에 답하지 않음 → T3 → 통신 실패 → 스풀 ACTIVE
            await TestUtil.Until(() => c.Eq.SpoolState == GemSpoolState.NoOutput, 8000);
            Assert.True(c.Eq.TriggerEvent(5001));                                                 // 스풀에 둘(+ SpoolingActivated CE)
            Assert.True(c.Eq.TriggerEvent(5001));
            int before = c.Eq.SpoolCount;
            c.AnswerS1F13 = true;
            await TestUtil.Until(() => c.Eq.CommunicationState == GemCommunicationState.Communicating, 8000);
            while (c.Got.TryTake(out _)) { }
            // 스풀 머리 순서: SpoolingActivated(작음) → 큰 S6F11 → 큰 S6F11. 첫 큰 것은 거절, 둘째는 허락
            c.Grants.Enqueue(GemAck.Grant6Busy);
            c.Grants.Enqueue(GemAck.Grant6Granted);
            var rsda = await c.Host.SendAsync(GemMessages.S6F23(GemAck.RsdcTransmit));
            Assert.Equal(GemAck.RsdaAccepted, GemMessages.ParseS6F24(rsda!));
            await TestUtil.Until(() => c.Eq.SpoolState == GemSpoolState.Inactive, 8000);
            Assert.Contains(GemKit.Only(c.Log, "SendFailed"), l => l == "SendFailed S6F11 NotGranted 1");
            Assert.Equal(0, c.Eq.SpoolCount);
            Assert.True(before >= 3);
            var ceids = new List<ulong>();
            while (c.Got.TryTake(out var m, 300)) if (m is { Stream: 6, Function: 11 }) ceids.Add(m.Item!.Items[1].GetUInt64());
            Assert.DoesNotContain(1309UL, ceids);                                                 // SpoolTransmitFailure 없음
            Assert.Equal(1308UL, ceids.Last());                                                   // 끝에 SpoolingDeactivated
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void Xml_RoundTrip_MultiBlockInquire()
    {
        using var a = new GemEquipment(new SecsHsms());
        Assert.False(a.MultiBlockInquire);
        a.MultiBlockInquire = true;
        var xml = a.ToXml();
        Assert.Equal("true", (string?)xml.Attribute("MultiBlockInquire"));
        using var b = new GemEquipment(new SecsHsms());
        b.FromXml(xml);
        Assert.True(b.MultiBlockInquire);
        var set = new XmlSchemaSet();
        set.Add(XmlSchema.Read(new StringReader(ConfigSchema.Get<GemEquipmentConfig>()), null)!);
        new XDocument(xml).Validate(set, (_, e) => Assert.Fail(e.Message));
    }

    [Fact]
    public void MultiBlockInquire_ChangeWhileRunning_Throws()
    {
        using var eq = GemKit.Equipment();
        eq.Start();
        Assert.Throws<InvalidOperationException>(() => eq.MultiBlockInquire = true);
    }
}
