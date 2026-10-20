using System.Net;
using System.Net.Sockets;
using System.Text;
using Devinno.Communication.Tests.Fakes;

namespace Devinno.Communication.Tests.Omron;

/// 가상 PLC 자체의 응답 형식과 엄격성을 소켓·바이트로 직접 확인한다 (Master 코드를 쓰지 않음).
public class FakeOmronPlcTests : IDisposable
{
    readonly FakeOmronPlc plc = new() { Node = 0x0A };
    readonly Socket udp = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

    public FakeOmronPlcTests()
    {
        plc.Start();
        udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    }

    public void Dispose()
    {
        udp.Dispose();
        plc.Dispose();
    }

    static byte[] H(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    /// 요청을 보내고 온 응답 (없으면 null). 응답을 기대하는 테스트는 병렬 부하를 고려해 3초까지 기다리고,
    /// 무응답을 확인하는 테스트(wait 짧게)는 300ms만 기다린다.
    byte[]? Udp(string hex, int waitMs = 3000)
    {
        udp.SendTo(H(hex), new IPEndPoint(IPAddress.Loopback, plc.UdpPort));
        if (!udp.Poll(waitMs * 1000, SelectMode.SelectRead)) return null;
        var buf = new byte[4096];
        int n = udp.Receive(buf);
        return buf[..n];
    }

    #region UDP
    [Fact]
    public void Udp_ReadWords_ReplySwapsAddresses_EchoesSid()
    {
        plc.SetWords("D100", 0x1234, 0xABCD);
        Assert.Equal(H("C0 00 02 00 64 00 00 0A 00 01 01 01 00 00 12 34 AB CD"), Udp("80 00 02 00 0A 00 00 64 00 01 01 01 82 00 64 00 00 02"));
        Assert.Equal(H("80 00 02 00 0A 00 00 64 00 01"), plc.LastHeader);
        Assert.Equal(((IPEndPoint)udp.LocalEndPoint!).Port, plc.LastUdpSource!.Port);
        Assert.Equal(new (byte, byte, int, int, int)[] { (1, 0x82, 100, 0, 2) }, plc.Requests);
    }

    [Fact]
    public void Udp_DestinationNode0_RepliesWithOwnNode()
        => Assert.Equal(H("C0 00 02 00 64 00 00 0A 00 07 01 01 00 00 00 00"), Udp("80 00 02 00 00 00 00 64 00 07 01 01 82 00 00 00 00 01"));

    [Fact]
    public void Udp_WriteWordsAndBits_ThenMultipleRead()
    {
        Assert.Equal(H("C0 00 02 00 64 00 00 0A 00 02 01 02 00 00"), Udp("80 00 02 00 0A 00 00 64 00 02 01 02 82 00 64 00 00 01 12 34"));
        Assert.Equal(H("C0 00 02 00 64 00 00 0A 00 03 01 02 00 00"), Udp("80 00 02 00 0A 00 00 64 00 03 01 02 30 00 0A 0F 00 02 01 01"));
        Assert.Equal((0x1234L, 1L, 1L), (plc.Get("D100"), plc.Get("CIO10.15"), plc.Get("CIO11.00")));
        plc.Set("CIO10", 0xABCD);
        Assert.Equal(H("C0 00 02 00 64 00 00 0A 00 05 01 04 00 00 82 12 34 B0 AB CD 30 00"),
            Udp("80 00 02 00 0A 00 00 64 00 05 01 04 82 00 64 00 B0 00 0A 00 30 00 0A 0E"));
    }

    [Fact]
    public void Udp_EmBanksAndBitAreas_ShareWordMemory()
    {
        plc.Set("E10_5", 0x8001);
        Assert.Equal((1L, 1L, 0L), (plc.Get("E10_5.15"), plc.Get("E10_5.00"), plc.Get("E10_5.01")));
        Assert.Equal(H("C0 00 02 00 64 00 00 0A 00 09 01 01 00 00 01 00"), Udp("80 00 02 00 0A 00 00 64 00 09 01 01 E0 00 05 0F 00 02"));
    }

    [Theory]
    [InlineData("80 00 02 00 0A 00 00 64 00 01 01 01 09 00 00 00 00 01", "01 01 11 01")]   // 타이머 영역 코드 → 영역 없음
    [InlineData("80 00 02 00 0A 00 00 64 00 01 01 01 82 7F FF 00 00 02", "01 01 11 03")]   // D32767부터 2워드 → 범위 밖
    [InlineData("80 00 02 00 0A 00 00 64 00 01 05 01 00", "05 01 04 01")]                  // 지원하지 않는 명령
    public void Udp_AreaOrRangeError_EndCode(string request, string body)
        => Assert.Equal(H("C0 00 02 00 64 00 00 0A 00 01" + body.Replace(" ", "")), Udp(request));

    [Fact]
    public void Udp_EndCodeFlagsAndWriteProtect()
    {
        plc.FlagBits = 0x0040;
        Assert.Equal(H("C0 00 02 00 64 00 00 0A 00 01 01 01 00 40 00 00"), Udp("80 00 02 00 0A 00 00 64 00 01 01 01 82 00 00 00 00 01"));
        plc.WriteProtect = true;
        Assert.Equal(H("C0 00 02 00 64 00 00 0A 00 02 01 02 21 02"), Udp("80 00 02 00 0A 00 00 64 00 02 01 02 82 00 00 00 00 01 00 05"));
        plc.EndCode = 0x1103;
        plc.EndCodeAfter = 1;
        Assert.Equal(H("C0 00 02 00 64 00 00 0A 00 03 01 01 00 40 00 00"), Udp("80 00 02 00 0A 00 00 64 00 03 01 01 82 00 00 00 00 01"));
        Assert.Equal(H("C0 00 02 00 64 00 00 0A 00 04 01 01 11 03"), Udp("80 00 02 00 0A 00 00 64 00 04 01 01 82 00 00 00 00 01"));
        Assert.Equal(0L, plc.Get("D0"));
    }

    [Theory]
    [InlineData("81 00 02 00 0A 00 00 64 00 01 01 01 82 00 00 00 00 01")]   // ICF
    [InlineData("80 00 07 00 0A 00 00 64 00 01 01 01 82 00 00 00 00 01")]   // GCT
    [InlineData("80 00 02 01 0A 00 00 64 00 01 01 01 82 00 00 00 00 01")]   // DNA
    [InlineData("80 00 02 00 0B 00 00 64 00 01 01 01 82 00 00 00 00 01")]   // DA1
    [InlineData("80 00 02 00 0A 01 00 64 00 01 01 01 82 00 00 00 00 01")]   // DA2
    [InlineData("80 00 02 00 0A 00 00 00 00 01 01 01 82 00 00 00 00 01")]   // SA1 0
    [InlineData("80 00 02 00 0A 00 00 64 EF 01 01 01 82 00 00 00 00 01")]   // SA2
    [InlineData("80 00 02 00 0A 00 00 64 00 01 01 01 82 00 00 00 00")]      // 길이 짧음
    [InlineData("80 00 02 00 0A 00 00 64 00 01 01 01 82 00 00 00 00 00")]   // 개수 0
    [InlineData("80 00 02 00 0A 00 00 64 00 01 01 01 82 00 00 01 00 01")]   // 워드 영역에 비트 번호
    [InlineData("80 00 02 00 0A 00 00 64 00 01 01 01 30 00 00 10 00 01")]   // 비트 16
    [InlineData("80 00 02 00 0A 00 00 64 00 01 01 02 82 00 00 00 00 02 00 01")]   // 쓰기 데이터 부족
    [InlineData("80 00 02 00 0A 00 00 64 00 01 01 02 30 00 00 00 00 01 02")]   // 비트 값 2
    [InlineData("80 00 02 00 0A 00 00 64 00 01 01 04")]                        // 0104 주소 없음
    public async Task Udp_Violation_NoReply(string request)
    {
        Assert.Null(Udp(request, 300));
        await TestUtil.Until(() => plc.Violations == 1);   // 바쁜 머신에서는 가상 PLC가 300ms 뒤에야 처리할 수 있다
        Assert.Empty(plc.Requests);
    }

    [Fact]
    public async Task Udp_Silent_NoReply_ButCounted()
    {
        plc.Silent = true;
        Assert.Null(Udp("80 00 02 00 0A 00 00 64 00 01 01 01 82 00 00 00 00 01", 300));
        await TestUtil.Until(() => plc.Requests.Length == 1);
        Assert.Equal(0, plc.Violations);
    }
    #endregion

    #region TCP
    static byte[] ReadExactly(NetworkStream s, int n)
    {
        var b = new byte[n];
        s.ReadTimeout = 2000;
        s.ReadExactly(b);
        return b;
    }

    [Fact]
    public void Tcp_Handshake_AutoAllocatesFrom239_ThenFrame()
    {
        plc.SetWords("D100", 0x1234);
        using var c1 = new TcpClient();
        c1.Connect(IPAddress.Loopback, plc.TcpPort);
        var s1 = c1.GetStream();
        s1.Write(H("46494E53 0000000C 00000000 00000000 00000000"));
        Assert.Equal(H("46494E53 00000010 00000001 00000000 000000EF 0000000A"), ReadExactly(s1, 24));
        s1.Write(H("46494E53 0000001A 00000002 00000000 80 00 02 00 0A 00 00 EF 00 01 01 01 82 00 64 00 00 01"));
        Assert.Equal(H("46494E53 00000018 00000002 00000000 C0 00 02 00 EF 00 00 0A 00 01 01 01 00 00 12 34"), ReadExactly(s1, 32));

        using var c2 = new TcpClient();
        c2.Connect(IPAddress.Loopback, plc.TcpPort);
        var s2 = c2.GetStream();
        s2.Write(H("46494E53 0000000C 00000000 00000000 00000005"));
        Assert.Equal(H("46494E53 00000010 00000001 00000000 00000005 0000000A"), ReadExactly(s2, 24));
        Assert.Equal(5, plc.LastRequestedClient);
    }

    [Fact]
    public void Tcp_WrongClientNodeInFrame_ClosesConnection()
    {
        using var c = new TcpClient();
        c.Connect(IPAddress.Loopback, plc.TcpPort);
        var s = c.GetStream();
        s.Write(H("46494E53 0000000C 00000000 00000000 00000000"));
        ReadExactly(s, 24);
        s.Write(H("46494E53 0000001A 00000002 00000000 80 00 02 00 0A 00 00 05 00 01 01 01 82 00 64 00 00 01"));
        Assert.Equal(0, s.Read(new byte[16]));   // 끊김
        Assert.Equal(1, plc.Violations);
    }

    [Fact]
    public void Tcp_HandshakeError_SendsErrorCode_ThenCloses()
    {
        plc.HandshakeError = 0x21;
        using var c = new TcpClient();
        c.Connect(IPAddress.Loopback, plc.TcpPort);
        var s = c.GetStream();
        s.Write(H("46494E53 0000000C 00000000 00000000 00000000"));
        Assert.Equal(H("46494E53 00000010 00000001 00000021 00000000 00000000"), ReadExactly(s, 24));
        Assert.Equal(0, s.Read(new byte[16]));
    }

    [Fact]
    public void Tcp_ErrorNotice_ThenCloses()
    {
        plc.ErrorNotice = true;
        using var c = new TcpClient();
        c.Connect(IPAddress.Loopback, plc.TcpPort);
        var s = c.GetStream();
        s.Write(H("46494E53 0000000C 00000000 00000000 00000000"));
        ReadExactly(s, 24);
        s.Write(H("46494E53 0000001A 00000002 00000000 80 00 02 00 0A 00 00 EF 00 01 01 01 82 00 64 00 00 01"));
        Assert.Equal(H("46494E53 00000008 00000003 00000003"), ReadExactly(s, 16));
        Assert.Equal(0, s.Read(new byte[16]));
    }
    #endregion

    #region Host Link
    [Theory]
    [InlineData(true, "@00FA004000000001010000123456784B*\r")]
    [InlineData(false, "@00FA4000000001010000123456784B*\r")]
    public void HostLink_ReadD100x2_BothResponseLayouts(bool endCode, string response)
    {
        plc.HostLinkEndCode = endCode;
        plc.SetWords("D100", 0x1234, 0x5678);
        Assert.Equal(response, Encoding.ASCII.GetString(plc.HandleHostLink(Encoding.ASCII.GetBytes("@00FA00000000001018200640000027D*\r"))!));
    }

    [Fact]
    public void HostLink_WriteD100_ThenWrongUnitIsSilent_BadFcsIsViolation()
    {
        Assert.Equal("@00FA00400000000102000040*\r", Encoding.ASCII.GetString(plc.HandleHostLink(Encoding.ASCII.GetBytes("@00FA0000000000102820064000001123479*\r"))!));
        Assert.Equal(0x1234L, plc.Get("D100"));
        plc.UnitNumber = 1;
        Assert.Null(plc.HandleHostLink(Encoding.ASCII.GetBytes("@00FA00000000001018200640000027D*\r")));
        Assert.Equal(0, plc.Violations);
        plc.UnitNumber = 0;
        Assert.Null(plc.HandleHostLink(Encoding.ASCII.GetBytes("@00FA00000000001018200640000027C*\r")));
        Assert.Equal(1, plc.Violations);
    }

    [Fact]
    public void HostLink_CorruptFcs_OnlyNextResponses()
    {
        plc.CorruptFcs = 1;
        var req = Encoding.ASCII.GetBytes("@00FA00000000001018200640000027D*\r");
        Assert.Equal("@00FA00400000000101000000000000" + "42*\r", Encoding.ASCII.GetString(plc.HandleHostLink(req)!));
        Assert.Equal("@00FA00400000000101000000000000" + "43*\r", Encoding.ASCII.GetString(plc.HandleHostLink(req)!));
    }
    #endregion
}
