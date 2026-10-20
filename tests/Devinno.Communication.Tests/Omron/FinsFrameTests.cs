using Devinno.Communication.Omron.Data;

namespace Devinno.Communication.Tests.Omron;

/// 벡터 출처(근거 조사 문서 7장): V = 옴론 공식 매뉴얼(W421/W342), C = 실장비 CP1L-EL20DR-D 캡처, D = 공식 규칙으로 계산한 파생 값.
public class FinsFrameTests
{
    static byte[] H(string hex) => Convert.FromHexString(hex.Replace(" ", ""));
    static FinsAddress A(string name) => FinsAddress.Parse(name, "p");
    /// 파생 벡터의 노드: PLC 0x0A, PC 0x64
    static readonly FinsNodes D = new(0, 0x0A, 0, 0, 0x64, 0);

    #region 요청
    [Fact]
    public void V1_W421UdpSample_Read150WordsFromD100()
        => Assert.Equal(H("80 00 02 00 64 00 00 32 00 01 01 01 82 00 64 00 00 96"),
            FinsFrame.Command(new FinsNodes(0, 0x64, 0, 0, 0x32, 0), 1, FinsFrame.ReadBody(A("D100"), 150)));

    [Fact]
    public void V3_W342_Read10WordsFromD10()
        => Assert.Equal(H("01 01 82 00 0A 00 00 0A"), FinsFrame.ReadBody(A("D10"), 10));

    [Fact]
    public void V4_W342_Write5WordsToD10()
        => Assert.Equal(H("01 02 82 00 0A 00 00 05 0001 0002 0003 FFFF 1234"), FinsFrame.WriteBody(A("D10"), new long[] { 1, 2, 3, -1, 0x1234 }));

    [Fact]
    public void C_Cp1lUdpCapture_DestinationNode0()
        => Assert.Equal(H("80 00 02 00 00 00 00 63 00 EF 05 01 00"), FinsFrame.Command(new FinsNodes(0, 0, 0, 0, 0x63, 0), 0xEF, H("05 01 00")));

    [Theory]
    [InlineData(1, "D100", 2, "80 00 02 00 0A 00 00 64 00 01 01 01 82 00 64 00 00 02")]
    [InlineData(4, "D100.15", 1, "80 00 02 00 0A 00 00 64 00 04 01 01 02 00 64 0F 00 01")]
    public void D_Read(byte sid, string start, int count, string hex)
        => Assert.Equal(H(hex), FinsFrame.Command(D, sid, FinsFrame.ReadBody(A(start), count)));

    [Theory]
    [InlineData(2, "D100", 0x1234, "80 00 02 00 0A 00 00 64 00 02 01 02 82 00 64 00 00 01 12 34")]
    [InlineData(3, "CIO100.05", 1, "80 00 02 00 0A 00 00 64 00 03 01 02 30 00 64 05 00 01 01")]
    public void D_Write(byte sid, string address, long value, string hex)
        => Assert.Equal(H(hex), FinsFrame.Command(D, sid, FinsFrame.WriteBody(A(address), new[] { value })));

    [Fact]
    public void D_MultipleRead_D100_CIO10_CIO10Bit13()
        => Assert.Equal(H("80 00 02 00 0A 00 00 64 00 05 01 04 82 00 64 00 B0 00 0A 00 30 00 0A 0D"),
            FinsFrame.Command(D, 5, FinsFrame.MultiReadBody([A("D100"), A("CIO10"), A("CIO10.13")])));
    #endregion

    #region 응답 짝짓기
    [Fact]
    public void D_WriteResponse_MatchesRequest_AndIsAck()
    {
        var resp = H("C0 00 02 00 64 00 00 0A 00 02 01 02 00 00");
        Assert.True(FinsFrame.IsResponseTo(resp, D, 2));
        var chunk = FinsFrame.Chunks(FinsCommand.Write, [A("D100")], 1, [0x1234], 499)[0];
        Assert.Equal(FinsStatus.Ack, FinsFrame.ParseBody(resp.AsSpan(10), chunk).Status);
    }

    [Fact]
    public void C_Cp1lUdpCapture_ResponseFromOwnNode_AcceptedWhenDestinationNode0()
        => Assert.True(FinsFrame.IsResponseTo(H("C0 00 02 00 63 00 00 C8 00 EF 05 01 00 00"), new FinsNodes(0, 0, 0, 0, 0x63, 0), 0xEF));

    [Theory]
    [InlineData("C0 00 02 00 64 00 00 0A 00 03 01 02 00 00", "SID 다름")]
    [InlineData("80 00 02 00 64 00 00 0A 00 02 01 02 00 00", "응답 비트 없음")]
    [InlineData("C0 00 02 00 64 00 00 0B 00 02 01 02 00 00", "SA1 다름")]
    [InlineData("C0 00 02 00 64 00 01 0A 00 02 01 02 00 00", "SNA 다름")]
    [InlineData("C0 00 02 00 64 00 00 0A 01 02 01 02 00 00", "SA2 다름")]
    [InlineData("C0 00 02 00 64 00 00 0A 00 02 01", "너무 짧음")]
    public void IsResponseTo_Mismatch_IsFalse(string hex, string why)
        => Assert.False(FinsFrame.IsResponseTo(H(hex), D, 2), why);
    #endregion

    #region 종료 코드
    [Theory]
    [InlineData(0x00, 0x00, 0x0000)]
    [InlineData(0x00, 0x40, 0x0000)]
    [InlineData(0x00, 0x80, 0x0000)]
    [InlineData(0x00, 0xC0, 0x0000)]
    [InlineData(0x80, 0x00, 0x0000)]
    [InlineData(0x11, 0x03, 0x1103)]
    [InlineData(0x11, 0x43, 0x1103)]
    [InlineData(0x91, 0x83, 0x1103)]
    [InlineData(0x21, 0x02, 0x2102)]
    [InlineData(0x04, 0x01, 0x0401)]
    [InlineData(0x00, 0x01, 0x0001)]
    public void EndCode_MasksRelayAndCpuErrorFlags(byte mres, byte sres, int expected)
        => Assert.Equal(expected, FinsFrame.EndCode(mres, sres));

    [Fact]
    public void ParseBody_NonFatalFlag0040_IsAckWithData()
    {
        var chunk = FinsFrame.Chunks(FinsCommand.ReadBlock, [A("D100")], 2, [], 499)[0];
        var r = FinsFrame.ParseBody(H("01 01 00 40 12 34 AB CD"), chunk);
        Assert.Equal(FinsStatus.Ack, r.Status);
        Assert.Equal(new long[] { 0x1234, 0xABCD }, r.Values);
    }

    [Theory]
    [InlineData("01 01 11 03", 0x1103)]
    [InlineData("01 01 11 43", 0x1103)]
    [InlineData("01 01 21 02", 0x2102)]
    [InlineData("01 01 91 03 01 0A", 0x1103)]
    public void ParseBody_ErrorEndCode_IsNak(string hex, int code)
    {
        var chunk = FinsFrame.Chunks(FinsCommand.ReadBlock, [A("D100")], 2, [], 499)[0];
        var r = FinsFrame.ParseBody(H(hex), chunk);
        Assert.Equal((FinsStatus.Nak, code), (r.Status, r.ErrorCode));
    }

    [Fact]
    public void ParseBody_RelayBitWithNormalCode_SkipsErrorNetworkAndNode()
    {
        var chunk = FinsFrame.Chunks(FinsCommand.ReadBlock, [A("D100")], 1, [], 499)[0];
        Assert.Equal(new long[] { 0x1234 }, FinsFrame.ParseBody(H("01 01 80 00 05 0A 12 34"), chunk).Values);
    }
    #endregion

    #region 응답 데이터
    [Fact]
    public void V3_W342_Response10Words()
    {
        var chunk = FinsFrame.Chunks(FinsCommand.ReadBlock, [A("D10")], 10, [], 499)[0];
        var r = FinsFrame.ParseBody(H("01 01 00 00" + string.Concat(Enumerable.Range(1, 10).Select(i => $"{i * 0x1111:X4}"))), chunk);
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (long)(i * 0x1111)), r.Values);
    }

    [Fact]
    public void ReadBits_OneBytePerBit()
    {
        var chunk = FinsFrame.Chunks(FinsCommand.ReadBlock, [A("CIO100.14")], 3, [], 499)[0];
        Assert.Equal(H("01 01 30 00 64 0E 00 03"), chunk.Body);
        Assert.Equal(new long[] { 1, 0, 1 }, FinsFrame.ParseBody(H("01 01 00 00 01 00 01"), chunk).Values);
    }

    [Fact]
    public void D_MultipleReadResponse_EchoesAreaCodeBeforeEachValue()
    {
        var chunk = FinsFrame.Chunks(FinsCommand.Read, [A("D100"), A("CIO10"), A("CIO10.13")], 3, [], 499)[0];
        var resp = H("C0 00 02 00 64 00 00 0A 00 05 01 04 00 00 82 12 34 B0 AB CD 30 01");
        Assert.True(FinsFrame.IsResponseTo(resp, D, 5));
        var r = FinsFrame.ParseBody(resp.AsSpan(10), chunk);
        Assert.Equal(FinsStatus.Ack, r.Status);
        Assert.Equal(new long[] { 0x1234, 0xABCD, 1 }, r.Values);
    }

    [Theory]
    [InlineData("01 02 00 00 12 34", "명령 코드 다름")]
    [InlineData("01 01 00 00 12 34 56", "데이터 길이 다름")]
    [InlineData("01 01 00 00 12", "데이터 짧음")]
    [InlineData("01 01 00", "본문 짧음")]
    [InlineData("01 01 80 00 05", "중계 오류 정보 짧음")]
    public void ParseBody_ReadMismatch_IsInvalid(string hex, string why)
    {
        var chunk = FinsFrame.Chunks(FinsCommand.ReadBlock, [A("D100")], 1, [], 499)[0];
        Assert.True(FinsFrame.ParseBody(H(hex), chunk).Status == FinsStatus.Invalid, why);
    }

    [Theory]
    [InlineData("01 04 00 00 82 12 34 31 AB CD 30 01", "영역 코드 다름")]
    [InlineData("01 04 00 00 82 12 34 B0 AB CD 30", "마지막 항목 짧음")]
    [InlineData("01 04 00 00 82 12 34 B0 AB CD 30 01 00", "뒤에 남는 바이트")]
    public void ParseBody_MultipleReadMismatch_IsInvalid(string hex, string why)
    {
        var chunk = FinsFrame.Chunks(FinsCommand.Read, [A("D100"), A("CIO10"), A("CIO10.13")], 3, [], 499)[0];
        Assert.True(FinsFrame.ParseBody(H(hex), chunk).Status == FinsStatus.Invalid, why);
    }

    [Fact]
    public void ParseBody_WriteResponseWithData_IsInvalid()
    {
        var chunk = FinsFrame.Chunks(FinsCommand.Write, [A("D100")], 1, [7], 499)[0];
        Assert.Equal(FinsStatus.Invalid, FinsFrame.ParseBody(H("01 02 00 00 00"), chunk).Status);
    }
    #endregion

    #region 조각 나누기
    [Fact]
    public void Chunks_ReadBlockWords_SplitAtMax()
    {
        var c = FinsFrame.Chunks(FinsCommand.ReadBlock, [A("D0")], 1000, [], 499);
        Assert.Equal(new[] { "01018200000001F3", "01018201F30001F3", "01018203E6000002" }, c.Select(x => Convert.ToHexString(x.Body)));
        Assert.Equal(new[] { 499, 499, 2 }, c.Select(x => x.Count));
    }

    [Theory]
    [InlineData(499, 499, 1)]
    [InlineData(499, 500, 2)]
    [InlineData(997, 997, 1)]
    [InlineData(997, 1994, 2)]
    [InlineData(997, 1995, 3)]
    [InlineData(1, 3, 3)]
    public void Chunks_CountBoundaries(int max, int count, int chunks)
    {
        var c = FinsFrame.Chunks(FinsCommand.ReadBlock, [A("D0")], count, [], max);
        Assert.Equal(chunks, c.Length);
        Assert.All(c, x => Assert.InRange(x.Count, 1, max));
        Assert.Equal(count, c.Sum(x => x.Count));
    }

    [Fact]
    public void Chunks_WriteBlockBits_CarryAcrossWords_AndSliceValues()
    {
        var values = Enumerable.Range(0, 40).Select(i => (long)(i % 3 == 0 ? 1 : 0)).ToArray();
        var c = FinsFrame.Chunks(FinsCommand.WriteBlock, [A("CIO0.10")], 40, values, 16);
        Assert.Equal(new[] { "3000000A0010", "3000010A0010", "3000020A0008" }, c.Select(x => Convert.ToHexString(x.Body.AsSpan(2, 6))));
        Assert.Equal(values, c.SelectMany(x => x.Body.Skip(8).Select(b => (long)b)));
    }

    [Fact]
    public void Chunks_WriteBlockWords_SliceValues()
    {
        var c = FinsFrame.Chunks(FinsCommand.WriteBlock, [A("W10")], 3, [1, 2, 3], 2);
        Assert.Equal(new[] { "0102B1000A00000200010002", "0102B1000C0000010003" }, c.Select(x => Convert.ToHexString(x.Body)));
    }

    [Fact]
    public void Chunks_ReadAndWrite_AreSingleRequests()
    {
        var items = Enumerable.Range(0, 167).Select(i => A($"D{i}")).ToArray();
        var read = Assert.Single(FinsFrame.Chunks(FinsCommand.Read, items, 167, [], 1));
        Assert.Equal(2 + 4 * 167, read.Body.Length);
        var write = Assert.Single(FinsFrame.Chunks(FinsCommand.Write, [A("W3.04")], 1, [1], 1));
        Assert.Equal(H("01 02 31 00 03 04 00 01 01"), write.Body);
    }

    [Fact]
    public void Run_ConcatenatesValues_StopsAtFirstFailure()
    {
        var c = FinsFrame.Chunks(FinsCommand.ReadBlock, [A("D0")], 5, [], 2);
        int calls = 0;
        var ok = FinsFrame.Run(c, x => { calls++; return FinsResponse.Ack([.. Enumerable.Repeat((long)x.Count, x.Count)]); });
        Assert.Equal((FinsStatus.Ack, 3), (ok.Status, calls));
        Assert.Equal(new long[] { 2, 2, 2, 2, 1 }, ok.Values);

        calls = 0;
        var nak = FinsFrame.Run(c, x => ++calls == 2 ? FinsResponse.Nak(0x1103) : FinsResponse.Ack([0, 0]));
        Assert.Equal((FinsStatus.Nak, 0x1103, 2), (nak.Status, nak.ErrorCode, calls));
        Assert.Empty(nak.Values);
    }
    #endregion
}
