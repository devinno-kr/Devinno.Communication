using System.Net.Sockets;
using Devinno.Communication.Siemens;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Siemens;

/// 가상 PLC가 실장비 응답 형식을 따르고 잘못된 요청을 거부하는지 (Master 없이 소켓으로 직접 확인)
public class FakeS7PlcTests : IDisposable
{
    readonly FakeS7Plc plc = new();
    readonly TcpClient client = new();

    public FakeS7PlcTests()
    {
        plc.Start();
        client.Connect("127.0.0.1", plc.Port);
    }

    public void Dispose()
    {
        client.Dispose();
        plc.Dispose();
    }

    static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    /// TPKT 프레임 하나. 상대가 끊으면 null
    async Task<byte[]?> ReadFrame()
    {
        using var cts = new CancellationTokenSource(3000);
        var s = client.GetStream();
        var head = new byte[4];
        try
        {
            await s.ReadExactlyAsync(head, cts.Token);
            var body = new byte[(head[2] << 8 | head[3]) - 4];
            await s.ReadExactlyAsync(body, cts.Token);
            return [.. head, .. body];
        }
        catch (EndOfStreamException) { return null; }
        catch (IOException) { return null; }
    }

    async Task Send(byte[] frame) => await client.GetStream().WriteAsync(frame);

    async Task<byte[]> Connect(int pdu = 960)
    {
        await Send(S7Frame.ConnectionRequest(0x0100, 0x0101));
        var cc = await ReadFrame();
        await Send(S7Frame.Job(1, S7Frame.SetupParam(pdu), []));
        await ReadFrame();
        return cc!;
    }

    [Fact]
    public async Task ConnectionConfirm_ParametersReordered_LikeRealPlc()
    {
        var cc = await Connect();
        Assert.Equal(Hex("03 00 00 16 11 D0 00 01 00 03 00 C0 01 0A C1 02 01 00 C2 02 01 01"), cc);
        Assert.True(S7Frame.IsConnectionConfirm(cc.AsSpan(4)));
        Assert.Equal((0x0100, 0x0101), (plc.LastLocalTsap, plc.LastRemoteTsap));
    }

    [Theory]
    [InlineData(960, 480, 480)]
    [InlineData(240, 960, 240)]
    public async Task Setup_NegotiatesMinimum(int requested, int plcMax, int expected)
    {
        plc.PduSize = plcMax;
        await Send(S7Frame.ConnectionRequest(0x0100, 0x0101));
        await ReadFrame();
        await Send(S7Frame.Job(7, S7Frame.SetupParam(requested), []));
        var reply = (await ReadFrame())!;
        Assert.Equal(expected, S7Frame.ParseSetup(reply.AsSpan(7)));
        Assert.Equal(7, S7Frame.ParseHeader(reply.AsSpan(7))!.Value.PduRef);
    }

    [Fact]
    public async Task WrongTsap_ClosesWithoutCc()
    {
        await Send(S7Frame.ConnectionRequest(0x0100, 0x0102));
        Assert.Null(await ReadFrame());
    }

    [Fact]
    public async Task Read_RealPlcFormat_TransportSizesAndFill()
    {
        plc.AddDb(1, 16);
        plc.SetBytes("DB1.DBB0", 0x02, 0x12, 0x34);
        await Connect();
        S7Item[] items = [S7Address.Parse("DB1.DBX0.1", "a").ToItem(), S7Address.Parse("DB1.DBW1", "a").ToItem(), S7Address.Parse("DB1.DBB2", "a").ToItem()];
        await Send(S7Frame.Job(2, S7Frame.ReadParam(items), []));
        var reply = (await ReadFrame())!;
        Assert.Equal(Hex("32 03 00 00 00 02 00 02 00 11 00 00 04 03 FF 03 00 01 01 00 FF 04 00 10 12 34 FF 04 00 08 34"), reply[7..]);
    }

    [Theory]
    [InlineData(false, "0A 00 00 00 05 00 00 00")]
    [InlineData(true, "0A 00 00 04 05 00 00 04")]
    public async Task Read_FailedItems_RealOrSnap7Style(bool snap, string expected)
    {
        plc.SnapStyleErrors = snap;
        plc.AddDb(1, 4);
        await Connect();
        await Send(S7Frame.Job(3, S7Frame.ReadParam([S7Address.Parse("DB9.DBW0", "a").ToItem(), S7Address.Parse("DB1.DBD2", "a").ToItem()]), []));
        var reply = (await ReadFrame())!;
        Assert.Equal(Hex(expected), reply[(7 + 14)..]);
    }

    [Fact]
    public async Task Write_ProtectedDb_Returns03_OthersApplied()
    {
        plc.AddDb(1, 4); plc.AddDb(2, 4); plc.WriteProtectDb(2);
        await Connect();
        var (p, d) = S7Frame.WriteParam([(S7Address.Parse("DB1.DBW0", "a").ToItem(), new byte[] { 0xBE, 0xEF }), (S7Address.Parse("DB2.DBW0", "a").ToItem(), new byte[] { 1, 2 })]);
        await Send(S7Frame.Job(4, p, d));
        var reply = (await ReadFrame())!;
        Assert.Equal(Hex("05 02 FF 03"), reply[(7 + 12)..]);
        Assert.Equal(0xBEEFL, plc.Get("DB1.DBW0"));
        Assert.Equal(0L, plc.Get("DB2.DBW0"));
    }

    [Fact]
    public async Task RequestOverPdu_HeaderError8500()
    {
        plc.PduSize = 240;
        plc.AddDb(1, 1000);
        await Connect();
        await Send(S7Frame.Job(5, S7Frame.ReadParam([S7Item.Bytes(S7Address.AreaDB, 1, 0, 223)]), []));
        var reply = (await ReadFrame())!;
        Assert.Equal(0x8500, S7Frame.ParseHeader(reply.AsSpan(7))!.Value.Error);
    }

    [Theory]
    [InlineData("bit-count-2")]
    [InlineData("missing-fill")]
    [InlineData("no-eot")]
    [InlineData("read-before-setup")]
    [InlineData("db-zero")]
    public async Task MalformedRequest_ClosesConnection(string kind)
    {
        plc.AddDb(1, 16);
        if (kind != "read-before-setup") await Connect();
        else
        {
            await Send(S7Frame.ConnectionRequest(0x0100, 0x0101));
            await ReadFrame();
        }
        byte[] frame = kind switch
        {
            "bit-count-2" => S7Frame.Job(2, S7Frame.ReadParam([new S7Item(S7Item.TsBit, 2, 1, S7Address.AreaDB, 0)]), []),
            "missing-fill" => Hex("03 00 00 36 02 F0 80 32 01 00 00 00 07 00 1A 00 0B 05 02 12 0A 10 01 00 01 00 00 83 00 00 00 12 0A 10 02 00 02 00 00 83 00 00 10 00 03 00 01 01 00 04 00 10 12 34"),
            "no-eot" => NoEot(S7Frame.Job(2, S7Frame.ReadParam([S7Address.Parse("MW0", "a").ToItem()]), [])),
            "read-before-setup" => S7Frame.Job(2, S7Frame.ReadParam([S7Address.Parse("MW0", "a").ToItem()]), []),
            _ => S7Frame.Job(2, S7Frame.ReadParam([new S7Item(S7Item.TsByte, 2, 0, S7Address.AreaDB, 0)]), []),
        };
        await Send(frame);
        Assert.Null(await ReadFrame());
    }

    static byte[] NoEot(byte[] job)
    {
        job[6] = 0x00;   // COTP DT의 EOT 비트를 끔
        return job;
    }

    [Fact]
    public async Task Fragmented_StartsWithEmptyDt_EotOnLast()
    {
        await Connect();
        plc.FragmentSize = 5;
        await Send(S7Frame.Job(2, S7Frame.ReadParam([S7Address.Parse("MW0", "a").ToItem()]), []));
        var first = (await ReadFrame())!;
        Assert.Equal(Hex("03 00 00 07 02 F0 00"), first);
        var parts = new List<byte>();
        while (true)
        {
            var f = (await ReadFrame())!;
            parts.AddRange(f[7..]);
            if (f[6] == 0x80) break;
            Assert.Equal(0x00, f[6]);
        }
        Assert.Equal(S7Status.Ack, S7Frame.ParseRead(parts.ToArray(), [2]).Status);
    }
}
