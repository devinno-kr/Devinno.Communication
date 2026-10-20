using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Devinno.Communication.Siemens;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Siemens;

public class S7EthernetTests : IDisposable
{
    readonly FakeS7Plc plc = new();
    readonly S7Ethernet s7;

    public S7EthernetTests()
    {
        plc.Start();
        s7 = new S7Ethernet { RemoteIP = "127.0.0.1", RemotePort = plc.Port, Timeout = 500, Interval = 1 };
    }

    public void Dispose()
    {
        s7.Dispose();
        plc.Dispose();
    }

    static TaskCompletionSource<T> Tcs<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    static Task<T> Wait<T>(TaskCompletionSource<T> t) => t.Task.WaitAsync(TimeSpan.FromSeconds(3));

    async Task Open()
    {
        var opened = Tcs<bool>();
        s7.DeviceOpened += (s, e) => opened.TrySetResult(true);
        s7.Start();
        await Wait(opened);
    }

    #region 연결
    [Fact]
    public async Task Connect_DefaultTsap_PduNegotiatedToPlcMaximum()
    {
        plc.PduSize = 480;
        Assert.Equal(0, s7.NegotiatedPduSize);
        await Open();
        Assert.True(s7.IsOpen);
        Assert.Equal(480, s7.NegotiatedPduSize);
        Assert.Equal((960, 0x0100, 0x0101), (plc.LastRequestedPdu, plc.LastLocalTsap, plc.LastRemoteTsap));
    }

    [Theory]
    [InlineData(S7ConnectionType.PG, 0, 1, 0x0101)]
    [InlineData(S7ConnectionType.Basic, 0, 2, 0x0302)]
    [InlineData(S7ConnectionType.OP, 1, 3, 0x0223)]
    [InlineData(S7ConnectionType.Basic, 7, 31, 0x03FF)]
    public async Task RemoteTsap_FromConnectionTypeRackSlot(S7ConnectionType type, int rack, int slot, int tsap)
    {
        plc.ExpectedRemoteTsap = tsap;
        s7.ConnectionType = type; s7.Rack = rack; s7.Slot = slot;
        await Open();
        Assert.Equal(tsap, plc.LastRemoteTsap);
    }

    [Fact]
    public async Task RemoteTsapOverride_AndLocalTsap()
    {
        plc.ExpectedRemoteTsap = 0x2000;   // LOGO! 예: 로컬 10.00, PLC 20.00
        s7.Rack = 3;
        s7.RemoteTsap = 0x2000;
        s7.LocalTsap = 0x1000;
        await Open();
        Assert.Equal((0x1000, 0x2000), (plc.LastLocalTsap, plc.LastRemoteTsap));
    }

    [Fact]
    public async Task HandshakeRefused_NeverOpens_RetriesEverySecond()
    {
        plc.ExpectedRemoteTsap = 0x0102;
        int opened = 0;
        s7.DeviceOpened += (s, e) => Interlocked.Increment(ref opened);
        s7.Start();
        await TestUtil.Until(() => plc.Connections >= 2, 3000);
        Assert.Equal(0, Volatile.Read(ref opened));
        Assert.False(s7.IsOpen);
        Assert.Equal(0, s7.NegotiatedPduSize);
        Assert.True(s7.IsStart);
    }

    [Fact]
    public void Properties_RangeChecked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => s7.Rack = 8);
        Assert.Throws<ArgumentOutOfRangeException>(() => s7.Slot = 32);
        Assert.Throws<ArgumentOutOfRangeException>(() => s7.PduSize = 239);
        Assert.Throws<ArgumentOutOfRangeException>(() => s7.PduSize = 961);
        Assert.Throws<ArgumentOutOfRangeException>(() => s7.LocalTsap = 0x10000);
        Assert.Throws<ArgumentOutOfRangeException>(() => s7.RemoteTsap = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => s7.ConnectionType = (S7ConnectionType)9);
        Assert.Equal((102, 0, 1, S7ConnectionType.PG, (int?)null, 0x0100, 960), (new S7Ethernet().RemotePort, s7.Rack, s7.Slot, s7.ConnectionType, s7.RemoteTsap, s7.LocalTsap, s7.PduSize));
    }
    [Fact]
    public async Task RequestedPduBelowPlcReply_MasterKeepsSmaller_ChunksBy222()
    {
        // PLC가 요청과 상관없이 960을 돌려줘도 Master는 요청한 240을 쓴다
        plc.IgnoreRequestedPdu = true;
        plc.AddDb(1, 500);
        plc.SetBytes("DB1.DBB0", [.. Enumerable.Range(0, 500).Select(i => (byte)(i * 5))]);
        s7.PduSize = 240;
        var block = Tcs<byte[]>();
        s7.BlockReceived += (s, e) => block.TrySetResult(e.Data);
        await Open();
        Assert.Equal(240, plc.LastRequestedPdu);
        Assert.Equal(240, s7.NegotiatedPduSize);
        s7.ManualReadBlock(1, "DB1.DBB0", 445);
        Assert.Equal(Enumerable.Range(0, 445).Select(i => (byte)(i * 5)), await Wait(block));
        Assert.Equal(new (byte, int)[] { (4, 222), (4, 222), (4, 1) }, plc.Jobs);
    }

    [Fact]
    public async Task SetupReplyBelow240_HandshakeFails_NeverOpens()
    {
        plc.PduSize = 200;
        int opened = 0;
        s7.DeviceOpened += (s, e) => Interlocked.Increment(ref opened);
        s7.Start();
        await TestUtil.Until(() => plc.Connections >= 2, 3000);
        Assert.Equal(960, plc.LastRequestedPdu);   // Setup까지는 갔다
        Assert.Equal(0, Volatile.Read(ref opened));
        Assert.False(s7.IsOpen);
        Assert.Equal(0, s7.NegotiatedPduSize);
    }

    [Fact]
    public async Task ConnectFails_NoAutoReconnect_LoopEnds()
    {
        plc.ExpectedRemoteTsap = 0x0102;
        s7.AutoReconnect = false;
        int opened = 0;
        s7.DeviceOpened += (s, e) => Interlocked.Increment(ref opened);
        s7.Start();
        await TestUtil.Until(() => !s7.IsStart, 3000);
        await Task.Delay(1200);   // 재접속 간격(1초)보다 오래 기다려도 다시 접속하지 않는다
        Assert.Equal(1, plc.Connections);
        Assert.Equal(0, Volatile.Read(ref opened));
        Assert.False(s7.IsOpen);
    }

    [Fact]
    public async Task Stop_DuringStalledHandshake_ReturnsQuickly()
    {
        // 접속은 받지만 CR에 답하지 않는 서버
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        TcpClient? held = null;
        try
        {
            var accepted = Tcs<TcpClient>();
            _ = Task.Run(async () =>
            {
                try { accepted.TrySetResult(await l.AcceptTcpClientAsync()); }
                catch (Exception ex) { accepted.TrySetException(ex); }
            });
            using var m = new S7Ethernet { RemoteIP = "127.0.0.1", RemotePort = ((IPEndPoint)l.LocalEndpoint).Port, Timeout = 10000 };
            int opened = 0;
            m.DeviceOpened += (s, e) => Interlocked.Increment(ref opened);
            m.Start();
            held = await Wait(accepted);
            await Task.Delay(200);   // CR을 보내고 CC를 기다리는 중
            var sw = Stopwatch.StartNew();
            m.Stop();
            Assert.True(sw.ElapsedMilliseconds < 1500, $"Stop {sw.ElapsedMilliseconds}ms");
            Assert.False(m.IsStart);
            Assert.False(m.IsOpen);
            Assert.Equal(0, Volatile.Read(ref opened));
        }
        finally
        {
            held?.Dispose();
            l.Stop();
        }
    }
    #endregion

    #region 읽기 / 쓰기
    [Fact]
    public async Task AutoRead_MixedAreasAndSizes()
    {
        plc.AddDb(1, 16);
        plc.Set("DB1.DBX0.1", 1); plc.Set("DB1.DBB1", 0xA5); plc.Set("DB1.DBW2", 0xBEEF); plc.Set("DB1.DBD4", 0xFEADBEEF);
        plc.Set("M10.0", 1); plc.Set("MW12", 0x1234); plc.Set("IB0", 0x5A); plc.Set("QD4", 0x01020304);
        var tcs = Tcs<S7Ethernet.DataEventArgs>();
        s7.DataReceived += (s, e) => tcs.TrySetResult(e);
        s7.AutoRead(1, ["db1.dbx0.1", "DB1.DBB1", "DB1.DBW2", "DB1.DBD4", "M10.0", "MW12", "EB0", "AD4"]);
        s7.Start();
        var e = await Wait(tcs);
        Assert.Equal((1, S7Command.Read), (e.MessageID, e.Command));
        Assert.Equal(new[] { "DB1.DBX0.1", "DB1.DBB1", "DB1.DBW2", "DB1.DBD4", "M10.0", "MW12", "EB0", "AD4" }, e.Addresses);
        Assert.Equal(new long[] { 1, 0xA5, 0xBEEF, 0xFEADBEEF, 1, 0x1234, 0x5A, 0x01020304 }, e.Values);
    }

    [Fact]
    public async Task ManualWrite_UpdatesPlc_TruncatesNegative()
    {
        plc.AddDb(1, 16);
        var done = Tcs<S7Ethernet.ResultEventArgs>();
        s7.WriteReceived += (s, e) => done.TrySetResult(e);
        s7.Start();
        s7.ManualWrite(7, [new S7Value("M0.3", 1), new S7Value("DB1.DBW0", -1), new S7Value("DB1.DBB2", 0x7F),
            new S7Value("QD0", 4294967295), new S7Value("DB1.DBD4", BitConverter.SingleToInt32Bits(1.5f))]);
        var e = await Wait(done);
        Assert.Equal((7, S7Command.Write), (e.MessageID, e.Command));
        Assert.Equal((1L, 0xFFFFL, 0x7FL, 0xFFFFFFFFL), (plc.Get("M0.3"), plc.Get("DB1.DBW0"), plc.Get("DB1.DBB2"), plc.Get("QD0")));
        Assert.Equal(1.5f, BitConverter.Int32BitsToSingle((int)plc.Get("DB1.DBD4")));
    }

    [Theory]
    [InlineData(240)]
    [InlineData(480)]
    [InlineData(960)]
    public async Task ReadBlock_SplitByNegotiatedPdu(int pdu)
    {
        plc.PduSize = pdu;
        plc.AddDb(1, 3000);
        var pattern = Enumerable.Range(0, 3000).Select(i => (byte)(i * 7)).ToArray();
        plc.SetBytes("DB1.DBB0", pattern);
        int chunk = pdu - 18;
        var blocks = new System.Collections.Concurrent.ConcurrentQueue<S7Ethernet.BlockEventArgs>();
        s7.BlockReceived += (s, e) => blocks.Enqueue(e);
        s7.ManualReadBlock(1, "DB1.DBB5", 2 * chunk + 1);
        s7.ManualReadBlock(2, "DB1.DBB0", chunk);
        s7.Start();
        await TestUtil.Until(() => blocks.Count == 2);
        var b = blocks.ToArray();
        Assert.Equal((1, S7Command.ReadBlock, "DB1.DBB5"), (b[0].MessageID, b[0].Command, b[0].Address));
        Assert.Equal(pattern[5..(5 + 2 * chunk + 1)], b[0].Data);
        Assert.Equal(pattern[..chunk], b[1].Data);
        Assert.Equal(new (byte, int)[] { (4, chunk), (4, chunk), (4, 1), (4, chunk) }, plc.Jobs);
    }

    [Theory]
    [InlineData(240)]
    [InlineData(480)]
    [InlineData(960)]
    public async Task WriteBlock_SplitByNegotiatedPdu(int pdu)
    {
        plc.PduSize = pdu;
        plc.AddDb(1, 3000);
        int chunk = pdu - 28;
        var data = Enumerable.Range(0, 2 * chunk + 1).Select(i => (byte)(i * 3 + 1)).ToArray();
        int writes = 0;
        s7.WriteReceived += (s, e) => Interlocked.Increment(ref writes);
        s7.ManualWriteBlock(1, "DB1.DBB3", data);
        s7.ManualWriteBlock(2, "MB0", data[..chunk]);
        s7.Start();
        await TestUtil.Until(() => Volatile.Read(ref writes) == 2);
        Assert.Equal(data, plc.GetBytes("DB1.DBB3", data.Length));
        Assert.Equal(data[..chunk], plc.GetBytes("MB0", chunk));
        Assert.Equal(new (byte, int)[] { (5, chunk), (5, chunk), (5, 1), (5, chunk) }, plc.Jobs);
    }

    [Fact]
    public async Task ManualWriteBlock_CopiesDataAtRegistration()
    {
        plc.AddDb(1, 4);
        var data = new byte[] { 1, 2, 3, 4 };
        var done = Tcs<bool>();
        s7.WriteReceived += (s, e) => done.TrySetResult(true);
        s7.ManualWriteBlock(1, "DB1.DBB0", data);
        data[0] = 99;
        s7.Start();
        await Wait(done);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, plc.GetBytes("DB1.DBB0", 4));
    }

    [Fact]
    public async Task MultiItem_Limits_FitMinimumPdu240()
    {
        var words = Enumerable.Range(0, 21).Select(i => $"MW{i * 2}").ToArray();
        var ex20 = Assert.Throws<ArgumentException>(() => s7.AutoRead(1, words[..20]));
        Assert.Contains("240", ex20.Message);
        var ex21 = Assert.Throws<ArgumentException>(() => s7.AutoRead(1, words));
        Assert.Contains("1~20", ex21.Message);
        Assert.Contains("1~20", Assert.Throws<ArgumentException>(() => s7.AutoRead(1, [])).Message);
        var bits = Enumerable.Range(0, 13).Select(i => new S7Value($"Q{i}.0", 1)).ToArray();
        Assert.Contains("240", Assert.Throws<ArgumentException>(() => s7.ManualWrite(1, bits)).Message);
        Assert.Null(s7.TakeNextWork());

        // 등록이 되는 최대 크기는 실제로 PDU 240에서 동작한다
        plc.PduSize = 240;
        for (int i = 0; i < 19; i++) plc.Set(words[i], i + 100);
        var data = Tcs<long[]>();
        var written = Tcs<bool>();
        s7.DataReceived += (s, e) => data.TrySetResult(e.Values);
        s7.WriteReceived += (s, e) => written.TrySetResult(true);
        s7.ManualWrite(2, bits[..12]);
        s7.ManualRead(3, words[..19]);
        s7.Start();
        Assert.True(await Wait(written));
        Assert.Equal(Enumerable.Range(100, 19).Select(x => (long)x), await Wait(data));
        Assert.Equal(1L, plc.Get("Q11.0"));
    }
    [Fact]
    public async Task AutoReadBlock_RaisesBlockReceived()
    {
        plc.AddDb(1, 10);
        plc.SetBytes("DB1.DBB0", [.. Enumerable.Range(1, 10).Select(i => (byte)i)]);
        var tcs = Tcs<S7Ethernet.BlockEventArgs>();
        s7.BlockReceived += (s, e) => tcs.TrySetResult(e);
        s7.AutoReadBlock(4, "db1.dbb2", 5);
        s7.Start();
        var e = await Wait(tcs);
        Assert.Equal((4, S7Command.ReadBlock, "DB1.DBB2"), (e.MessageID, e.Command, e.Address));
        Assert.Equal(new byte[] { 3, 4, 5, 6, 7 }, e.Data);
    }
    #endregion

    #region 오류
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingDb_RaisesNak_WithItemIndex_NoRetry_NoData(bool snapStyle)
    {
        plc.SnapStyleErrors = snapStyle;
        plc.AddDb(1, 16);
        var nak = Tcs<S7Ethernet.NAKEventArgs>();
        int data = 0, timedOut = 0;
        s7.NAKReceived += (s, e) => nak.TrySetResult(e);
        s7.DataReceived += (s, e) => Interlocked.Increment(ref data);
        s7.TimeoutReceived += (s, e) => Interlocked.Increment(ref timedOut);
        s7.Start();
        s7.ManualRead(1, ["DB1.DBW0", "DB9.DBW0", "DB1.DBW20"], repeatCount: 2);
        var e = await Wait(nak);
        Assert.Equal((1, S7Command.Read, 0x0A, 1), (e.MessageID, e.Command, e.ErrorCode, e.ItemIndex));
        await Task.Delay(200);
        Assert.Equal(0, Volatile.Read(ref data));
        Assert.Equal(0, Volatile.Read(ref timedOut));
        Assert.Single(plc.Jobs);
    }

    [Fact]
    public async Task WriteProtected_RaisesNak03_ValueUnchanged()
    {
        plc.AddDb(1, 4); plc.AddDb(2, 4); plc.WriteProtectDb(2);
        var nak = Tcs<S7Ethernet.NAKEventArgs>();
        int written = 0;
        s7.NAKReceived += (s, e) => nak.TrySetResult(e);
        s7.WriteReceived += (s, e) => Interlocked.Increment(ref written);
        s7.Start();
        s7.ManualWrite(1, [new S7Value("DB1.DBW0", 5), new S7Value("DB2.DBW0", 6)]);
        var e = await Wait(nak);
        Assert.Equal((S7Command.Write, 0x03, 1), (e.Command, e.ErrorCode, e.ItemIndex));
        Assert.Equal(0L, plc.Get("DB2.DBW0"));
        Assert.Equal(0, Volatile.Read(ref written));
    }

    [Fact]
    public async Task HeaderError_RaisesNak_ItemIndexMinus1()
    {
        plc.HeaderError = 0x8104;
        var nak = Tcs<S7Ethernet.NAKEventArgs>();
        s7.NAKReceived += (s, e) => nak.TrySetResult(e);
        s7.Start();
        s7.ManualRead(1, ["MW0"]);
        var e = await Wait(nak);
        Assert.Equal((0x8104, -1), (e.ErrorCode, e.ItemIndex));
    }

    [Fact]
    public async Task Block_ChunkFails_NakOnly_NoBlockOrWriteEvent()
    {
        plc.PduSize = 240;
        plc.AddDb(1, 300);
        var naks = new System.Collections.Concurrent.ConcurrentQueue<S7Ethernet.NAKEventArgs>();
        int block = 0, written = 0;
        s7.NAKReceived += (s, e) => naks.Enqueue(e);
        s7.BlockReceived += (s, e) => Interlocked.Increment(ref block);
        s7.WriteReceived += (s, e) => Interlocked.Increment(ref written);
        s7.ManualReadBlock(1, "DB1.DBB0", 400);   // 222바이트 성공 후 둘째 조각(222~399)이 DB 길이 300을 넘음
        s7.ManualWriteBlock(2, "DB1.DBB0", new byte[400]);
        s7.Start();
        await TestUtil.Until(() => naks.Count == 2);
        Assert.Equal(new[] { (1, S7Command.ReadBlock, 0x05, 0), (2, S7Command.WriteBlock, 0x05, 0) }, naks.Select(e => (e.MessageID, e.Command, e.ErrorCode, e.ItemIndex)));
        Assert.Equal(0, Volatile.Read(ref block));
        Assert.Equal(0, Volatile.Read(ref written));
        Assert.Equal(new (byte, int)[] { (4, 222), (4, 178), (5, 212), (5, 188) }, plc.Jobs);
    }

    [Fact]
    public async Task Silent_RaisesTimeout_RepeatCountPlusOne()
    {
        plc.Silent = true;
        int count = 0;
        var done = Tcs<bool>();
        s7.Timeout = 100;
        s7.TimeoutReceived += (s, e) => { if (Interlocked.Increment(ref count) == 3) done.TrySetResult(true); };
        s7.Start();
        s7.ManualRead(1, ["MW0"], repeatCount: 2);
        await Wait(done);
        await Task.Delay(300);
        Assert.Equal(3, Volatile.Read(ref count));
    }

    [Fact]
    public async Task StalePduReference_IsDiscarded()
    {
        plc.StaleReply = true;
        plc.Set("MW0", 0x600D);
        var tcs = Tcs<long>();
        int nak = 0;
        s7.NAKReceived += (s, e) => Interlocked.Increment(ref nak);
        s7.DataReceived += (s, e) => tcs.TrySetResult(e.Values[0]);
        s7.ManualRead(1, ["MW0"]);
        s7.Start();
        Assert.Equal(0x600D, await Wait(tcs));
        Assert.Equal(0, Volatile.Read(ref nak));
    }

    [Fact]
    public async Task FragmentedResponses_Reassembled()
    {
        plc.FragmentSize = 3;
        plc.AddDb(1, 600);
        plc.SetBytes("DB1.DBB0", [.. Enumerable.Range(0, 600).Select(i => (byte)i)]);
        plc.Set("MW4", 0x4321);
        var block = Tcs<byte[]>();
        var data = Tcs<long>();
        s7.BlockReceived += (s, e) => block.TrySetResult(e.Data);
        s7.DataReceived += (s, e) => data.TrySetResult(e.Values[0]);
        s7.ManualReadBlock(1, "DB1.DBB0", 600);
        s7.ManualRead(2, ["MW4"]);
        s7.Start();
        Assert.Equal(Enumerable.Range(0, 600).Select(i => (byte)i), await Wait(block));
        Assert.Equal(0x4321, await Wait(data));
        Assert.Equal(960, s7.NegotiatedPduSize);
    }

    [Fact]
    public async Task HeaderErrorNak_IsNotRetried()
    {
        plc.HeaderError = 0x8104;
        int naks = 0, timeouts = 0;
        var nak = Tcs<bool>();
        s7.NAKReceived += (s, e) => { Interlocked.Increment(ref naks); nak.TrySetResult(true); };
        s7.TimeoutReceived += (s, e) => Interlocked.Increment(ref timeouts);
        s7.Start();
        s7.ManualRead(1, ["MW0"], repeatCount: 3);
        await Wait(nak);
        await Task.Delay(300);
        Assert.Equal(1, plc.JobRequests);
        Assert.Equal(1, Volatile.Read(ref naks));
        Assert.Equal(0, Volatile.Read(ref timeouts));
    }

    [Fact]
    public async Task MalformedTpkt_Reconnects()
    {
        // 직접 만든 서버: 연결 설정에는 정상 응답, 첫 읽기에 TPKT 버전이 틀린 프레임 → 스트림이 깨진 것으로 보고 다시 접속
        var l = new TcpListener(IPAddress.Loopback, 0);
        using var cts = new CancellationTokenSource();
        l.Start();
        try
        {
            int accepted = 0;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        using var c = await l.AcceptTcpClientAsync(cts.Token);
                        Interlocked.Increment(ref accepted);
                        var s = c.GetStream();
                        var buf = new byte[512];
                        await s.ReadAsync(buf, cts.Token);
                        await s.WriteAsync(Convert.FromHexString("0300001611D00001000300C0010AC1020100C2020101"), cts.Token);
                        await s.ReadAsync(buf, cts.Token);
                        var setup = Convert.FromHexString("0300001B02F080320300000001000800000000F0000001000103C0");
                        setup[11] = buf[11]; setup[12] = buf[12];   // PDU 참조를 요청과 같게
                        await s.WriteAsync(setup, cts.Token);
                        await s.ReadAsync(buf, cts.Token);
                        await s.WriteAsync(Convert.FromHexString("0400000702F080"), cts.Token);
                        await Task.Delay(500, cts.Token);
                    }
                }
                catch { }
            });
            using var m = new S7Ethernet { RemoteIP = "127.0.0.1", RemotePort = ((IPEndPoint)l.LocalEndpoint).Port, Timeout = 500, Interval = 1 };
            int opened = 0, closed = 0, timeouts = 0;
            m.DeviceOpened += (s, e) => Interlocked.Increment(ref opened);
            m.DeviceClosed += (s, e) => Interlocked.Increment(ref closed);
            m.TimeoutReceived += (s, e) => Interlocked.Increment(ref timeouts);
            m.AutoRead(1, ["MW0"]);
            m.Start();
            await TestUtil.Until(() => Volatile.Read(ref opened) >= 2, 4000);
            Assert.True(Volatile.Read(ref closed) >= 1);
            Assert.True(Volatile.Read(ref timeouts) >= 1);
            Assert.True(Volatile.Read(ref accepted) >= 2);
            m.Stop();
        }
        finally
        {
            cts.Cancel();
            l.Stop();
        }
    }
    #endregion

    #region 스케줄 / 수명주기
    [Fact]
    public async Task ManualRunsBeforeAuto()
    {
        var first = Tcs<int>();
        s7.DataReceived += (s, e) => first.TrySetResult(e.MessageID);
        s7.AutoRead(1, ["MW0"]);
        s7.ManualRead(2, ["MW2"]);
        s7.Start();
        Assert.Equal(2, await Wait(first));
    }

    [Fact]
    public async Task EventArrays_AreCopies()
    {
        var second = Tcs<string>();
        int n = 0;
        s7.DataReceived += (s, e) =>
        {
            if (Interlocked.Increment(ref n) == 1) e.Addresses[0] = "MW999";
            else second.TrySetResult(e.Addresses[0]);
        };
        s7.AutoRead(1, ["MW0"]);
        s7.Start();
        Assert.Equal("MW0", await Wait(second));
    }

    [Fact]
    public async Task HandlerExceptions_DoNotStopLoop()
    {
        int n = 0;
        s7.DeviceOpened += (s, e) => throw new InvalidOperationException("opened");
        s7.DataReceived += (s, e) => { Interlocked.Increment(ref n); throw new InvalidOperationException("data"); };
        s7.AutoRead(1, ["MW0"]);
        s7.Start();
        await TestUtil.Until(() => Volatile.Read(ref n) >= 3);
        Assert.True(s7.IsOpen);
    }

    [Fact]
    public async Task Stop_ReturnsQuickly_AndCanRestart()
    {
        await Open();
        s7.AutoRead(1, ["MW0"]);
        var sw = Stopwatch.StartNew();
        s7.Stop();
        Assert.True(sw.ElapsedMilliseconds < 2000);
        Assert.False(s7.IsStart);
        Assert.False(s7.IsOpen);
        Assert.Equal(0, s7.NegotiatedPduSize);

        var again = Tcs<bool>();
        s7.DataReceived += (s, e) => again.TrySetResult(true);
        s7.Start();
        Assert.True(await Wait(again));
    }

    [Fact]
    public async Task ConnectionDropped_ReconnectsAndContinues()
    {
        int opened = 0, closed = 0, data = 0;
        s7.DeviceOpened += (s, e) => Interlocked.Increment(ref opened);
        s7.DeviceClosed += (s, e) => Interlocked.Increment(ref closed);
        s7.DataReceived += (s, e) => Interlocked.Increment(ref data);
        s7.AutoRead(1, ["MW0"]);
        s7.Start();
        await TestUtil.Until(() => Volatile.Read(ref data) > 0);
        plc.Kick();
        await TestUtil.Until(() => Volatile.Read(ref opened) == 2, 4000);
        int before = Volatile.Read(ref data);
        await TestUtil.Until(() => Volatile.Read(ref data) > before);
        Assert.Equal(1, Volatile.Read(ref closed));
    }

    [Fact]
    public void Register_Invalid_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => s7.AutoRead(1, null!));
        Assert.Throws<ArgumentException>(() => s7.AutoRead(1, ["MW0", "T5"]));
        Assert.Throws<ArgumentException>(() => s7.AutoRead(1, ["MW0", null!]));
        Assert.Throws<ArgumentException>(() => s7.AutoReadBlock(1, "DB1.DBW0", 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => s7.AutoReadBlock(1, "DB1.DBB0", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => s7.AutoReadBlock(1, "DB1.DBB0", 65536));
        Assert.Throws<ArgumentNullException>(() => s7.ManualWrite(1, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => s7.ManualWrite(1, [new S7Value("M0.0", 2)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => s7.ManualWrite(1, [new S7Value("MW0", 65536)]));
        Assert.Throws<ArgumentNullException>(() => s7.ManualWriteBlock(1, "MB0", null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => s7.ManualWriteBlock(1, "MB0", []));
        Assert.Throws<ArgumentException>(() => s7.ManualWriteBlock(1, "M0.0", [1]));
        Assert.Null(s7.TakeNextWork());
    }

    [Fact]
    public void Schedule_ContainRemoveClear()
    {
        s7.AutoRead(1, ["MW0"]);
        s7.AutoReadBlock(2, "MB0", 4);
        s7.ManualRead(3, ["MW0"]);
        Assert.True(s7.ContainAutoID(2));
        Assert.True(s7.RemoveAuto(2));
        Assert.False(s7.ContainAutoID(2));
        Assert.True(s7.RemoveManual(3));
        Assert.False(s7.RemoveManual(3));
        Assert.Equal(1, s7.TakeNextWork()!.MessageID);
        s7.ClearWorkSchedule();
        s7.ClearAuto();
        s7.ManualRead(4, ["MW0"]);
        s7.ClearManual();
        Assert.Null(s7.TakeNextWork());
    }
    #endregion
}
