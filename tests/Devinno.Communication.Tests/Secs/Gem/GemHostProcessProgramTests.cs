using System.Collections.Concurrent;
using Devinno.Communication.Secs.Data;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// GemHost 레시피 명령과 이벤트(Plan 16 Task 7): 우리 GemEquipment(레시피 켬)와 루프백
[Collection(nameof(GemHostCollection))]
public class GemHostProcessProgramTests
{
    static readonly GemProcessProgram R1 = new("R1", SecsItem.B(1, 2, 3));

    static Task<(GemEquipment Eq, GemHost Host)> Pair(InMemoryProcessProgramStore store, Action<GemEquipment>? eq = null, Action<GemHost>? host = null)
        => GemHostKit.Loopback(e =>
        {
            e.InitialControlState = GemControlState.OnlineRemote;
            e.ProcessProgramStore = store;
            eq?.Invoke(e);
        }, host);

    [Fact]
    public async Task Download_WithInquire()
    {
        var store = new InMemoryProcessProgramStore();
        var (eq, host) = await Pair(store);
        using (eq) using (host)
        {
            var kinds = new ConcurrentQueue<GemProcessProgramChangeKind>();
            eq.ProcessProgramChangeRequested += (s, e) => kinds.Enqueue(e.Kind);
            var r = host.DownloadProcessProgram(R1);
            Assert.Equal((byte)0, r.Ppgnt);
            Assert.Equal((byte)0, r.Ackc7);
            Assert.True(r.IsSuccess);
            Assert.Equal(new[] { GemProcessProgramChangeKind.Inquire, GemProcessProgramChangeKind.Download }, kinds);
            Assert.Equal(R1.Body, store.Peek("R1")!.Body);
        }
    }

    [Fact]
    public async Task Download_InquireRefused_NoSend()
    {
        var store = new InMemoryProcessProgramStore();
        var (eq, host) = await Pair(store);
        using (eq) using (host)
        {
            eq.ProcessProgramChangeRequested += (s, e) => e.Ack = 2;
            var r = host.DownloadProcessProgram(R1);
            Assert.Equal((byte)2, r.Ppgnt);
            Assert.Null(r.Ackc7);
            Assert.False(r.IsSuccess);
            Assert.Null(store.Peek("R1"));
            var noInquire = host.DownloadProcessProgram(R1, inquire: false);
            Assert.Null(noInquire.Ppgnt);
            Assert.Equal((byte)2, noInquire.Ackc7);
        }
    }

    [Fact]
    public async Task Upload_List_Delete_Select()
    {
        var store = new InMemoryProcessProgramStore();
        store.Put(R1);
        store.Put(new GemProcessProgram("R2", SecsItem.A("x")));
        var (eq, host) = await Pair(store);
        using (eq) using (host)
        {
            eq.RemoteCommandReceived += (s, e) => e.Hcack = GemAck.HcackAccepted;
            Assert.Equal(R1.Body, host.UploadProcessProgram("R1")!.Body);
            Assert.Equal(new[] { "R1", "R2" }, host.ListProcessPrograms());
            Assert.Equal(0, host.SelectProcessProgram("R1").Hcack);
            await TestUtil.Until(() => eq.SelectedProcessProgram == "R1", 3000);
            Assert.Equal(GemAck.Ackc7PermissionNotGranted, host.DeleteProcessPrograms(["R1"]));     // 선택된 것
            Assert.Equal(GemAck.Ackc7Accepted, host.DeleteProcessPrograms(["R2"]));
            Assert.Equal(new[] { "R1" }, host.ListProcessPrograms());
        }
    }

    [Fact]
    public async Task Upload_Unknown_Null()
    {
        var (eq, host) = await Pair(new InMemoryProcessProgramStore());
        using (eq) using (host)
        {
            Assert.Null(host.UploadProcessProgram("NOPE"));
            Assert.Null(await host.UploadProcessProgramAsync("NOPE"));
        }
    }

    [Fact]
    public async Task EquipmentUpload_ToHost()
    {
        var store = new InMemoryProcessProgramStore();
        store.Put(R1);
        var got = new ConcurrentQueue<string>();
        var (eq, host) = await Pair(store, host: h => h.ProcessProgramReceived += (s, e) =>
            got.Enqueue(e.IsInquire ? $"inquire {e.Ppid} {e.Length}" : $"program {e.Program!.Ppid} {e.Program.Body.ToSml()}"));
        using (eq) using (host)
        {
            var r = await eq.UploadProcessProgramAsync("R1");
            Assert.True(r.IsSuccess);
            Assert.Equal(new[] { $"inquire R1 {SecsCodec.Encode(R1.Body).Length}", "program R1 <B 0x01 0x02 0x03>" }, got);
        }
    }

    [Fact]
    public async Task EquipmentRequest_FromHost()
    {
        var (eq, host) = await Pair(new InMemoryProcessProgramStore(), host: h => h.ProcessProgramRequested += (s, e) =>
        {
            if (e.Ppid == "R9") e.Program = new GemProcessProgram("R9", SecsItem.A("v"));
        });
        using (eq) using (host)
        {
            Assert.Equal(SecsItem.A("v"), (await eq.RequestProcessProgramAsync("R9"))!.Body);
            Assert.Null(await eq.RequestProcessProgramAsync("R8"));
        }
    }

    [Fact]
    public async Task NoSubscriber_FallsBackToMessageReceived()
    {
        var store = new InMemoryProcessProgramStore();
        store.Put(R1);
        var seen = new ConcurrentQueue<string>();
        var (eq, host) = await Pair(store, host: h => h.MessageReceived += (s, e) =>
        {
            seen.Enqueue($"S{e.Message.Stream}F{e.Message.Function}");
            e.Reply(new SecsMessage(7, e.Message.Function + 1, false, SecsItem.B(0)));
        });
        using (eq) using (host)
        {
            Assert.True((await eq.UploadProcessProgramAsync("R1")).IsSuccess);
            Assert.Equal(new[] { "S7F1", "S7F3" }, seen);
        }
        var (eq2, host2) = await Pair(store);
        using (eq2) using (host2)
            Assert.Equal(GemUploadFailure.Aborted, (await eq2.UploadProcessProgramAsync("R1")).Failure);   // MessageReceived도 없으면 S7F0
    }

    [Fact]
    public async Task Host_DeadlineReply()
    {
        var store = new InMemoryProcessProgramStore();
        store.Put(R1);
        var release = new ManualResetEventSlim(false);
        var (eq, host) = await Pair(store, host: h =>
        {
            h.CommandReplyTimeoutForTest = 300;
            h.ProcessProgramReceived += (s, e) => release.Wait(5000);
        });
        try
        {
            using (eq) using (host)
            {
                var r = await eq.UploadProcessProgramAsync("R1");
                Assert.Equal(GemUploadFailure.Rejected, r.Failure);
                Assert.Equal(GemAck.PpgntBusy, r.Ppgnt);
            }
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task Async_SameAsSync()
    {
        var store = new InMemoryProcessProgramStore();
        var (eq, host) = await Pair(store);
        using (eq) using (host)
        {
            var r = await host.DownloadProcessProgramAsync(R1);
            Assert.True(r.IsSuccess);
            Assert.Equal(new[] { "R1" }, await host.ListProcessProgramsAsync());
            Assert.Equal(GemAck.Ackc7Accepted, await host.DeleteProcessProgramsAsync([]));
            Assert.Empty(await host.ListProcessProgramsAsync());
        }
    }
}
