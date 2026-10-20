using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Devinno.Communication.Omron.Data;
using Devinno.Communication.Omron.Ethernet;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Omron.Ethernet;

public class FinsUdpTests : IDisposable
{
    readonly FakeOmronPlc plc = new();
    readonly FinsUdp fins;

    public FinsUdpTests()
    {
        plc.Start();
        fins = new FinsUdp { RemoteIP = "127.0.0.1", RemotePort = plc.UdpPort, Timeout = 500, Interval = 1 };
    }

    public void Dispose()
    {
        fins.Dispose();
        plc.Dispose();
    }

    static TaskCompletionSource<T> Tcs<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    static Task<T> Wait<T>(TaskCompletionSource<T> t) => t.Task.WaitAsync(TimeSpan.FromSeconds(3));

    async Task<FinsUdp.DataEventArgs> ReadOnce(Action register)
    {
        var tcs = Tcs<FinsUdp.DataEventArgs>();
        fins.DataReceived += (s, e) => tcs.TrySetResult(e);
        register();
        fins.Start();
        return await Wait(tcs);
    }

    #region 소켓 / 노드
    [Fact]
    public async Task Open_DefaultNodes_FromIpLastOctets()
    {
        var opened = Tcs<bool>();
        fins.DeviceOpened += (s, e) => opened.TrySetResult(true);
        Assert.False(fins.IsOpen);
        await ReadOnce(() => fins.ManualRead(1, ["D0"]));
        Assert.True(await Wait(opened));
        Assert.True(fins.IsOpen);
        var h = plc.LastHeader;
        Assert.Equal(new byte[] { 0x80, 0x00, 0x02, 0x00, 0x01, 0x00, 0x00, 0x01, 0x00 }, h[..9]);   // DA1 = 1(127.0.0.1), SA1 = 1(127.0.0.1)
        Assert.Equal(IPAddress.Loopback, plc.LastUdpSource!.Address);
    }

    [Fact]
    public void DefaultNodes_DistinctAddresses_UseEachLastOctet()
    {
        var n = FinsUdp.Nodes(IPAddress.Parse("10.0.0.7"), IPAddress.Parse("10.0.0.42"), 0, null, 0, null);
        Assert.Equal(new FinsNodes(0, 7, 0, 0, 42, 0), n);
        var e = FinsUdp.Nodes(IPAddress.Parse("10.0.0.7"), IPAddress.Parse("10.0.0.42"), 3, 0, 5, 100);
        Assert.Equal(new FinsNodes(3, 0, 0, 5, 100, 0), e);
    }

    [Fact]
    public async Task ExplicitNodesAndSourceNetwork_AreSent()
    {
        plc.Node = 0x0A;
        fins.DestinationNode = 0x0A;
        fins.SourceNode = 0x64;
        fins.SourceNetwork = 5;
        plc.Set("D7", 77);
        var e = await ReadOnce(() => fins.ManualRead(1, ["D7"]));
        Assert.Equal(77, e.Values[0]);
        Assert.Equal(new byte[] { 0x80, 0x00, 0x02, 0x00, 0x0A, 0x00, 0x05, 0x64, 0x00 }, plc.LastHeader[..9]);
    }

    [Fact]
    public async Task DestinationNode0_PlcAnswersWithOwnNode_Accepted()
    {
        plc.Node = 0xC8;
        fins.DestinationNode = 0;
        plc.Set("D1", 0x600D);
        var e = await ReadOnce(() => fins.ManualRead(1, ["D1"]));
        Assert.Equal(0x600D, e.Values[0]);
        Assert.Equal(0, plc.LastHeader[4]);
    }

    [Fact]
    public async Task HostName_ResolvesToIPv4()
    {
        fins.RemoteIP = "localhost";
        plc.Set("W3", 3);
        Assert.Equal(3, (await ReadOnce(() => fins.ManualRead(1, ["W3"]))).Values[0]);
    }

    [Fact]
    public async Task LocalPort_IsUsedAsSourcePort()
    {
        // 포트 0(동적 대역)으로 고른 번호는 놓는 순간 병렬로 도는 다른 테스트가 가져갈 수 있다.
        // 그래서 동적 대역(49152~) 밖에서 비어 있는 번호를 고른다.
        int port = 0;
        for (int i = 0; i < 100 && port == 0; i++)
        {
            int candidate = Random.Shared.Next(30000, 40000);
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try { probe.Bind(new IPEndPoint(IPAddress.Loopback, candidate)); port = candidate; }
            catch (SocketException) { }
        }
        Assert.NotEqual(0, port);
        fins.LocalPort = port;
        await ReadOnce(() => fins.ManualRead(1, ["D0"]));
        Assert.Equal(port, plc.LastUdpSource!.Port);
    }

    [Fact]
    public async Task BindFails_NoAutoReconnect_LoopEnds()
    {
        using var taken = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        taken.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        fins.LocalPort = ((IPEndPoint)taken.LocalEndPoint!).Port;
        fins.AutoReconnect = false;
        int opened = 0;
        fins.DeviceOpened += (s, e) => Interlocked.Increment(ref opened);
        fins.Start();
        await TestUtil.Until(() => !fins.IsStart, 3000);
        Assert.Equal(0, Volatile.Read(ref opened));
        Assert.False(fins.IsOpen);
    }

    [Fact]
    public void Properties_RangeChecked_AndDefaults()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => fins.LocalPort = 65536);
        Assert.Throws<ArgumentOutOfRangeException>(() => fins.DestinationNode = 255);
        Assert.Throws<ArgumentOutOfRangeException>(() => fins.SourceNode = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => fins.SourceNode = 255);
        Assert.Throws<ArgumentOutOfRangeException>(() => fins.DestinationNetwork = 128);
        Assert.Throws<ArgumentOutOfRangeException>(() => fins.SourceNetwork = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => fins.MaxWordsPerRequest = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => fins.MaxWordsPerRequest = 998);
        var d = new FinsUdp();
        Assert.Equal(("127.0.0.1", 9600, 0, (int?)null, (int?)null, 0, 0, 499, 1000, 10, true),
            (d.RemoteIP, d.RemotePort, d.LocalPort, d.DestinationNode, d.SourceNode, d.DestinationNetwork, d.SourceNetwork, d.MaxWordsPerRequest, d.Timeout, d.Interval, d.AutoReconnect));
    }
    #endregion

    #region 읽기 / 쓰기
    [Fact]
    public async Task AutoRead_MixedAreas_WordsAndBits()
    {
        plc.Set("D100", 0xBEEF); plc.Set("CIO0.05", 1); plc.Set("W10", 0x1234); plc.Set("H20.15", 1);
        plc.Set("A100", 7); plc.Set("E0_100", 0xA0A0); plc.Set("E10_5", 0x6060); plc.Set("D100.15", 1);
        var e = await ReadOnce(() => fins.AutoRead(1, ["dm100", "0.05", "W10", "hr20.15", "A100", "em0_100", "E10_5", "D100.15", "D101"]));
        Assert.Equal((1, FinsCommand.Read), (e.MessageID, e.Command));
        Assert.Equal(new[] { "D100", "CIO0.05", "W10", "H20.15", "A100", "E0_100", "E10_5", "D100.15", "D101" }, e.Addresses);
        Assert.Equal(new long[] { 0xBEEF, 1, 0x1234, 1, 7, 0xA0A0, 0x6060, 1, 0 }, e.Values);
        Assert.Equal((4, 9), (plc.Requests[0].Src, plc.Requests[0].Count));
    }

    [Fact]
    public async Task ManualWrite_WordTruncatesNegative_AndBit()
    {
        fins.DiscardWritesWhileDisconnected = false;   // Start 전에 대기열에 넣은 쓰기를 시험한다
        int writes = 0;
        var done = Tcs<FinsUdp.ResultEventArgs>();
        fins.WriteReceived += (s, e) => { if (Interlocked.Increment(ref writes) == 2) done.TrySetResult(e); };
        fins.ManualWrite(7, "D200", -1);
        fins.ManualWrite(8, "CIO100.05", 1);
        fins.Start();
        var e = await Wait(done);
        Assert.Equal((8, FinsCommand.Write), (e.MessageID, e.Command));
        Assert.Equal((0xFFFFL, 1L, 0x20L), (plc.Get("D200"), plc.Get("CIO100.05"), plc.Get("CIO100")));
    }

    [Theory]
    [InlineData(499)]
    [InlineData(997)]
    [InlineData(10)]
    public async Task ReadBlock_SplitAtMaxWordsPerRequest(int max)
    {
        fins.MaxWordsPerRequest = max;
        var pattern = Enumerable.Range(0, 2 * max + 1).Select(i => (ushort)(i * 7 + 1)).ToArray();
        plc.SetWords("D5", pattern);
        var e = await ReadOnce(() => fins.ManualReadBlock(1, "D5", 2 * max + 1));
        Assert.Equal((FinsCommand.ReadBlock, "D5"), (e.Command, Assert.Single(e.Addresses)));
        Assert.Equal(pattern.Select(x => (long)x), e.Values);
        Assert.Equal(new (byte, byte, int, int, int)[] { (1, 0x82, 5, 0, max), (1, 0x82, 5 + max, 0, max), (1, 0x82, 5 + 2 * max, 0, 1) }, plc.Requests);
    }

    [Fact]
    public async Task ReadBlock_ExactlyMax_IsOneRequest()
    {
        await ReadOnce(() => fins.ManualReadBlock(1, "W0", 499));
        Assert.Equal(new (byte, byte, int, int, int)[] { (1, 0xB1, 0, 0, 499) }, plc.Requests);
    }

    [Fact]
    public async Task ReadBlock_Bits_CarryIntoNextWord()
    {
        plc.Set("CIO100.14", 1); plc.Set("CIO101.01", 1);
        var e = await ReadOnce(() => fins.ManualReadBlock(1, "100.14", 4));
        Assert.Equal("CIO100.14", e.Addresses[0]);
        Assert.Equal(new long[] { 1, 0, 0, 1 }, e.Values);
        Assert.Equal(new (byte, byte, int, int, int)[] { (1, 0x30, 100, 14, 4) }, plc.Requests);
    }

    [Fact]
    public async Task WriteBlock_WordsAndBits_SplitAtMax()
    {
        fins.DiscardWritesWhileDisconnected = false;   // Start 전에 대기열에 넣은 쓰기를 시험한다
        fins.MaxWordsPerRequest = 16;
        var bits = Enumerable.Range(0, 40).Select(i => (long)(i % 3 == 0 ? 1 : 0)).ToArray();
        var words = Enumerable.Range(0, 20).Select(i => (long)(i - 10)).ToArray();
        int writes = 0;
        fins.WriteReceived += (s, e) => Interlocked.Increment(ref writes);
        fins.ManualWriteBlock(1, "CIO0.10", bits);
        fins.ManualWriteBlock(2, "H0", words);
        fins.Start();
        await TestUtil.Until(() => Volatile.Read(ref writes) == 2);
        Assert.Equal(bits, Enumerable.Range(0, 40).Select(i => plc.Get($"CIO{(10 + i) / 16}.{(10 + i) % 16:D2}")));
        Assert.Equal(words.Select(v => (ushort)v), plc.GetWords("H0", 20));
        Assert.Equal(new (byte, byte, int, int, int)[] { (2, 0x30, 0, 10, 16), (2, 0x30, 1, 10, 16), (2, 0x30, 2, 10, 8), (2, 0xB2, 0, 0, 16), (2, 0xB2, 16, 0, 4) }, plc.Requests);
    }

    [Fact]
    public async Task ManualWriteBlock_CopiesValuesAtRegistration()
    {
        fins.DiscardWritesWhileDisconnected = false;   // Start 전에 대기열에 넣은 쓰기를 시험한다
        var values = new long[] { 1, 2, 3 };
        var done = Tcs<bool>();
        fins.WriteReceived += (s, e) => done.TrySetResult(true);
        fins.ManualWriteBlock(1, "D0", values);
        values[0] = 99;
        fins.Start();
        await Wait(done);
        Assert.Equal(new ushort[] { 1, 2, 3 }, plc.GetWords("D0", 3));
    }

    [Fact]
    public async Task MultipleRead_167Max_WorksAgainstPlc()
    {
        var names = Enumerable.Range(0, 168).Select(i => $"D{i}").ToArray();
        Assert.Contains("1~167", Assert.Throws<ArgumentException>(() => fins.AutoRead(1, names)).Message);
        for (int i = 0; i < 167; i++) plc.Set(names[i], i + 1000);
        var e = await ReadOnce(() => fins.ManualRead(2, names[..167]));
        Assert.Equal(Enumerable.Range(1000, 167).Select(x => (long)x), e.Values);
    }

    [Fact]
    public async Task AutoReadBlock_RepeatsAndRaisesData()
    {
        int n = 0;
        var twice = Tcs<bool>();
        fins.DataReceived += (s, e) => { if (Interlocked.Increment(ref n) == 2) twice.TrySetResult(true); };
        fins.AutoReadBlock(4, "d10", 3);
        fins.Start();
        Assert.True(await Wait(twice));
    }
    #endregion

    #region 오류
    [Fact]
    public async Task EndCode_RaisesNak_NoRetry_NoData()
    {
        plc.EndCode = 0x1103;
        var nak = Tcs<FinsUdp.NAKEventArgs>();
        int data = 0, timeouts = 0;
        fins.NAKReceived += (s, e) => nak.TrySetResult(e);
        fins.DataReceived += (s, e) => Interlocked.Increment(ref data);
        fins.TimeoutReceived += (s, e) => Interlocked.Increment(ref timeouts);
        fins.ManualRead(1, ["D0", "D1"], repeatCount: 3);
        fins.Start();
        var e = await Wait(nak);
        Assert.Equal((1, FinsCommand.Read, 0x1103), (e.MessageID, e.Command, e.ErrorCode));
        await Task.Delay(200);
        Assert.Single(plc.Requests);
        Assert.Equal((0, 0), (Volatile.Read(ref data), Volatile.Read(ref timeouts)));
    }

    [Theory]
    [InlineData(0x0040)]
    [InlineData(0x0080)]
    [InlineData(0x00C0)]
    public async Task CpuErrorFlags_OnNormalResponse_AreNotErrors(int flags)
    {
        plc.FlagBits = flags;
        plc.Set("D9", 9);
        int naks = 0;
        fins.NAKReceived += (s, e) => Interlocked.Increment(ref naks);
        var e = await ReadOnce(() => fins.ManualRead(1, ["D9"]));
        Assert.Equal(9, e.Values[0]);
        Assert.Equal(0, Volatile.Read(ref naks));
    }

    [Fact]
    public async Task WriteProtected_NakCodeWithoutFlags()
    {
        fins.DiscardWritesWhileDisconnected = false;   // Start 전에 대기열에 넣은 쓰기를 시험한다
        plc.WriteProtect = true;
        plc.FlagBits = 0x0040;
        var nak = Tcs<FinsUdp.NAKEventArgs>();
        fins.NAKReceived += (s, e) => nak.TrySetResult(e);
        fins.ManualWrite(1, "D0", 5);
        fins.Start();
        Assert.Equal(0x2102, (await Wait(nak)).ErrorCode);
        Assert.Equal(0L, plc.Get("D0"));
    }

    [Fact]
    public async Task Block_SecondChunkFails_NakOnly_NoDataOrWriteEvent()
    {
        plc.EndCode = 0x1103;
        plc.EndCodeAfter = 1;
        fins.MaxWordsPerRequest = 10;
        var naks = new ConcurrentQueue<FinsUdp.NAKEventArgs>();
        int data = 0, written = 0;
        fins.NAKReceived += (s, e) => naks.Enqueue(e);
        fins.DataReceived += (s, e) => Interlocked.Increment(ref data);
        fins.WriteReceived += (s, e) => Interlocked.Increment(ref written);
        fins.ManualReadBlock(1, "D0", 25);
        fins.Start();
        await TestUtil.Until(() => naks.Count == 1);
        plc.EndCodeAfter = 1;
        fins.ManualWriteBlock(2, "D0", new long[25]);
        await TestUtil.Until(() => naks.Count == 2);
        Assert.Equal(new[] { (1, FinsCommand.ReadBlock, 0x1103), (2, FinsCommand.WriteBlock, 0x1103) }, naks.Select(e => (e.MessageID, e.Command, e.ErrorCode)));
        Assert.Equal((0, 0), (Volatile.Read(ref data), Volatile.Read(ref written)));
        Assert.Equal(new[] { 1, 1, 2, 2 }, plc.Requests.Select(r => (int)r.Src));
    }

    [Fact]
    public async Task Silent_RaisesTimeout_RepeatCountPlusOne()
    {
        plc.Silent = true;
        fins.Timeout = 100;
        int count = 0;
        var done = Tcs<bool>();
        fins.TimeoutReceived += (s, e) => { if (Interlocked.Increment(ref count) == 3) done.TrySetResult(true); };
        fins.ManualRead(1, ["D0"], repeatCount: 2);
        fins.Start();
        await Wait(done);
        await Task.Delay(300);
        Assert.Equal(3, Volatile.Read(ref count));
        Assert.Equal(3, plc.Requests.Length);
    }

    [Fact]
    public async Task NoPlcListening_TimesOut_KeepsRunning()
    {
        int port;
        using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            port = ((IPEndPoint)probe.LocalEndPoint!).Port;
        }
        fins.RemotePort = port;   // 아무도 없는 포트: ICMP 포트 닫힘(Windows는 ConnectionReset)도 타임아웃으로 처리
        fins.Timeout = 100;
        int count = 0;
        fins.TimeoutReceived += (s, e) => Interlocked.Increment(ref count);
        fins.AutoRead(1, ["D0"]);
        fins.Start();
        await TestUtil.Until(() => Volatile.Read(ref count) >= 3);
        Assert.True(fins.IsOpen);
        Assert.True(fins.IsStart);
    }

    [Fact]
    public async Task StaleSid_IsDiscarded()
    {
        plc.StaleReply = true;
        plc.Set("D0", 0x600D);
        int naks = 0;
        fins.NAKReceived += (s, e) => Interlocked.Increment(ref naks);
        var e = await ReadOnce(() => fins.ManualRead(1, ["D0"]));
        Assert.Equal(0x600D, e.Values[0]);
        Assert.Equal(0, Volatile.Read(ref naks));
    }

    [Fact]
    public async Task ReplyFromOtherEndpoint_IsDiscarded()
    {
        plc.ForeignReply = true;
        plc.Set("D0", 0x600D);
        var e = await ReadOnce(() => fins.ManualRead(1, ["D0"]));
        Assert.Equal(0x600D, e.Values[0]);
    }
    #endregion

    #region 스케줄 / 수명주기
    [Fact]
    public async Task ManualRunsBeforeAuto()
    {
        var first = Tcs<int>();
        fins.DataReceived += (s, e) => first.TrySetResult(e.MessageID);
        fins.AutoRead(1, ["D0"]);
        fins.ManualRead(2, ["D1"]);
        fins.Start();
        Assert.Equal(2, await Wait(first));
    }

    [Fact]
    public async Task EventArrays_AreCopies()
    {
        var second = Tcs<(string, long)>();
        int n = 0;
        fins.DataReceived += (s, e) =>
        {
            if (Interlocked.Increment(ref n) == 1) { e.Addresses[0] = "D999"; e.Values[0] = 999; }
            else second.TrySetResult((e.Addresses[0], e.Values[0]));
        };
        fins.AutoRead(1, ["D0"]);
        fins.Start();
        Assert.Equal(("D0", 0L), await Wait(second));
    }

    [Fact]
    public async Task HandlerExceptions_DoNotStopLoop()
    {
        int n = 0;
        fins.DeviceOpened += (s, e) => throw new InvalidOperationException("opened");
        fins.DataReceived += (s, e) => { Interlocked.Increment(ref n); throw new InvalidOperationException("data"); };
        fins.AutoRead(1, ["D0"]);
        fins.Start();
        await TestUtil.Until(() => Volatile.Read(ref n) >= 3);
        Assert.True(fins.IsOpen);
    }

    [Fact]
    public async Task Stop_ClosesSocket_AndCanRestart()
    {
        var opened = Tcs<bool>();
        int closed = 0;
        fins.DeviceOpened += (s, e) => opened.TrySetResult(true);
        fins.DeviceClosed += (s, e) => Interlocked.Increment(ref closed);
        fins.Start();
        await Wait(opened);
        fins.AutoRead(1, ["D0"]);
        var sw = Stopwatch.StartNew();
        fins.Stop();
        Assert.True(sw.ElapsedMilliseconds < 2000);
        Assert.False(fins.IsStart);
        Assert.False(fins.IsOpen);
        Assert.Equal(1, Volatile.Read(ref closed));

        var again = Tcs<bool>();
        fins.DataReceived += (s, e) => again.TrySetResult(true);
        fins.Start();
        Assert.True(await Wait(again));
    }

    [Fact]
    public void Register_Invalid_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => fins.AutoRead(1, null!));
        Assert.Throws<ArgumentException>(() => fins.AutoRead(1, ["D0", "T5"]));
        Assert.Throws<ArgumentException>(() => fins.AutoRead(1, ["D0", null!]));
        Assert.Throws<ArgumentOutOfRangeException>(() => fins.AutoReadBlock(1, "D0", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => fins.AutoReadBlock(1, "D32767", 2));
        Assert.Throws<ArgumentException>(() => fins.AutoReadBlock(1, "X0", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => fins.ManualWrite(1, "D0", 65536));
        Assert.Throws<ArgumentOutOfRangeException>(() => fins.ManualWrite(1, "D0.00", 2));
        Assert.Throws<ArgumentNullException>(() => fins.ManualWriteBlock(1, "D0", null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => fins.ManualWriteBlock(1, "D0", []));
        Assert.Throws<ArgumentOutOfRangeException>(() => fins.ManualWriteBlock(1, "W511", [1, 2]));
        Assert.Throws<ArgumentOutOfRangeException>(() => fins.ManualWriteBlock(1, "CIO0.00", [0, 1, 5]));
        Assert.Null(fins.TakeNextWork());
    }

    [Fact]
    public void Schedule_ContainRemoveClear()
    {
        fins.AutoRead(1, ["D0"]);
        fins.AutoReadBlock(2, "D0", 4);
        fins.ManualRead(3, ["D0"]);
        Assert.True(fins.ContainAutoID(2));
        Assert.True(fins.RemoveAuto(2));
        Assert.False(fins.ContainAutoID(2));
        Assert.True(fins.RemoveManual(3));
        Assert.False(fins.RemoveManual(3));
        Assert.Equal(1, fins.TakeNextWork()!.MessageID);
        fins.ClearWorkSchedule();
        fins.ClearAuto();
        fins.ManualRead(4, ["D0"]);
        fins.ClearManual();
        Assert.Null(fins.TakeNextWork());
    }
    #endregion
}
