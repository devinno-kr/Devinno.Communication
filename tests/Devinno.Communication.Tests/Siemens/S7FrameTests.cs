using Devinno.Communication.Siemens;

namespace Devinno.Communication.Tests.Siemens;

/// 골든 벡터 출처(근거 조사 문서 9장): C1/C2 = 실장비 S7-300 캡처, T1 = S7.Net 단위 테스트 리터럴, D = 같은 구조로 스크립트 계산(파생)
public class S7FrameTests
{
    static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    /// TPKT(4) + COTP DT(3)를 떼어 낸 S7 PDU
    static byte[] Pdu(string frame) => Hex(frame)[7..];

    /// 바이트 배열을 스트림처럼 읽는 readExact. 끝나면 null(시간 초과와 같음).
    static Func<int, byte[]?> Reader(byte[] stream)
    {
        int p = 0;
        return n =>
        {
            if (stream.Length - p < n) return null;
            var r = stream[p..(p + n)];
            p += n;
            return r;
        };
    }

    #region 연결
    [Fact]
    public void ConnectionRequest_Derived_Slot1() =>
        Assert.Equal(Hex("03 00 00 16 11 E0 00 00 00 01 00 C1 02 01 00 C2 02 03 01 C0 01 0A"), S7Frame.ConnectionRequest(0x0100, 0x0301));

    [Fact]
    public void ConnectionRequest_RealCapture_DiffersOnlyInTpduSize()
    {
        // C2: libnodave ↔ S7-300, 로컬 01.00, PLC 01.02 (PG, 랙 0 슬롯 2), TPDU 크기 09(512) — 우리는 0A(1024)
        var real = Hex("03 00 00 16 11 E0 00 00 00 01 00 C1 02 01 00 C2 02 01 02 C0 01 09");
        var mine = S7Frame.ConnectionRequest(0x0100, 0x0102);
        Assert.Equal(real[..^1], mine[..^1]);
        Assert.Equal(0x0A, mine[^1]);
    }

    [Fact]
    public void ConnectionRequest_S7NetLiteral_DiffersOnlyInSourceReference()
    {
        // T1: S7.Net S7-1200 랙 0 슬롯 1 (원본 참조 00 2E) — 우리는 00 01
        var t1 = Hex("03 00 00 16 11 E0 00 00 00 2E 00 C1 02 01 00 C2 02 03 01 C0 01 0A");
        var mine = S7Frame.ConnectionRequest(0x0100, 0x0301);
        Assert.Equal([.. t1[..8], .. t1[10..]], [.. mine[..8], .. mine[10..]]);
    }

    [Theory]
    [InlineData("11 D0 00 01 00 03 00 C0 01 09 C1 02 01 00 C2 02 01 02", true)]   // C2 실장비 CC: 파라미터 순서가 요청과 다름
    [InlineData("06 D0 00 00 00 00 00", true)]                                    // T1 S7.Net 최소 CC (파라미터 없음)
    [InlineData("06 80 00 00 00 00 00", false)]                                   // DR(연결 끊기 요청)
    [InlineData("06 E0 00 00 00 00 00", false)]                                   // CR
    [InlineData("11 D0 00 01 00 03 00 C0 01 09 C1 02 01 00 C2 05 01 02", false)]  // 파라미터 길이가 남은 바이트보다 김
    [InlineData("12 D0 00 01 00 03 00 C0 01 09 C1 02 01 00 C2 02 01 02", false)]  // 길이 표시가 맞지 않음
    [InlineData("08 D0 00 01 00 03 00 C0 01", false)]                             // 파라미터 값이 잘림
    public void IsConnectionConfirm(string cotp, bool expected) => Assert.Equal(expected, S7Frame.IsConnectionConfirm(Hex(cotp)));
    #endregion

    #region Setup Communication
    [Fact]
    public void Setup_RealCapture_LibnodaveRequest() =>
        // C2: 참조 FFFF, AmQ 1/1, PDU 1920
        Assert.Equal(Hex("03 00 00 19 02 F0 80 32 01 00 00 FF FF 00 08 00 00 F0 00 00 01 00 01 07 80"), S7Frame.Job(0xFFFF, S7Frame.SetupParam(1920), []));

    [Fact]
    public void Setup_Derived_Pdu960() =>
        Assert.Equal(Hex("03 00 00 19 02 F0 80 32 01 00 00 00 01 00 08 00 00 F0 00 00 01 00 01 03 C0"), S7Frame.Job(1, S7Frame.SetupParam(960), []));

    [Theory]
    [InlineData("03 00 00 1B 02 F0 80 32 03 00 00 FF FF 00 08 00 00 00 00 F0 00 00 01 00 01 00 F0", 240)]   // C2 실장비
    [InlineData("03 00 00 1B 02 F0 80 32 03 00 00 00 01 00 08 00 00 00 00 F0 00 00 01 00 02 00 F0", 240)]   // C1 실장비 (WinCC)
    public void ParseSetup_RealCaptures(string frame, int pdu) => Assert.Equal(pdu, S7Frame.ParseSetup(Pdu(frame)));

    [Theory]
    [InlineData("32 03 00 00 00 01 00 08 00 00 81 04 F0 00 00 01 00 01 00 F0")]   // 헤더 오류
    [InlineData("32 01 00 00 00 01 00 08 00 00 F0 00 00 01 00 01 00 F0")]         // Job (응답 아님)
    [InlineData("32 03 00 00 00 01 00 08 00 00 00 00 04 00 00 01 00 01 00 F0")]   // 기능 코드가 F0이 아님
    [InlineData("32 03 00 00 00 01 00 08 00 00 00 00 F0 00 00 01 00 01 00")]      // 길이 부족
    public void ParseSetup_Invalid_Null(string pdu) => Assert.Null(S7Frame.ParseSetup(Hex(pdu)));
    #endregion

    #region Read Var 요청
    [Fact]
    public void ReadRequest_RealCapture_DbBlock64() =>
        // C2: DB1.DBB0 × 64 (BYTE), 참조 0 — 연속 읽기 조각과 같은 형태
        Assert.Equal(Hex("03 00 00 1F 02 F0 80 32 01 00 00 00 00 00 0E 00 00 04 01 12 0A 10 02 00 40 00 01 84 00 00 00"),
            S7Frame.Job(0, S7Frame.ReadParam([S7Item.Bytes(S7Address.AreaDB, 1, 0, 64)]), []));

    [Fact]
    public void ReadRequest_RealCapture_MB0x16()
    {
        var a = S7Address.ParseBlockStart("MB0", "a");
        Assert.Equal(Hex("03 00 00 1F 02 F0 80 32 01 00 00 00 01 00 0E 00 00 04 01 12 0A 10 02 00 10 00 00 83 00 00 00"),
            S7Frame.Job(1, S7Frame.ReadParam([S7Item.Bytes(a.Area, a.Db, a.Byte, 16)]), []));
    }

    [Fact]
    public void ReadRequest_RealCapture_WinCC8Items()
    {
        // C1: DB1.DBX2.0, DB1.DBD8(REAL), DB1.DBD4(DWORD), DB1.DBW0(WORD), M1.0, M1.1, T0, C0 — 항목 배치 확인용 (T/C·REAL 전송 크기는 그대로 넣음)
        S7Item[] items =
        [
            new(0x01, 1, 1, 0x84, 0x10), new(0x08, 1, 1, 0x84, 0x40), new(0x06, 1, 1, 0x84, 0x20), new(0x04, 1, 1, 0x84, 0x00),
            new(0x01, 1, 0, 0x83, 0x08), new(0x01, 1, 0, 0x83, 0x09), new(0x1D, 1, 0, 0x1D, 0x00), new(0x1C, 1, 0, 0x1C, 0x00),
        ];
        Assert.Equal(Hex("03 00 00 73 02 F0 80 32 01 00 00 00 02 00 62 00 00 04 08 " +
            "12 0A 10 01 00 01 00 01 84 00 00 10 12 0A 10 08 00 01 00 01 84 00 00 40 " +
            "12 0A 10 06 00 01 00 01 84 00 00 20 12 0A 10 04 00 01 00 01 84 00 00 00 " +
            "12 0A 10 01 00 01 00 00 83 00 00 08 12 0A 10 01 00 01 00 00 83 00 00 09 " +
            "12 0A 10 1D 00 01 00 00 1D 00 00 00 12 0A 10 1C 00 01 00 00 1C 00 00 00"),
            S7Frame.Job(2, S7Frame.ReadParam(items), []));
    }

    [Theory]
    [InlineData("DB1.DBX0.1", 3, "03 00 00 1F 02 F0 80 32 01 00 00 00 03 00 0E 00 00 04 01 12 0A 10 01 00 01 00 01 84 00 00 01")]   // D
    [InlineData("MW10", 4, "03 00 00 1F 02 F0 80 32 01 00 00 00 04 00 0E 00 00 04 01 12 0A 10 02 00 02 00 00 83 00 00 50")]         // D
    public void ReadRequest_Derived_FromAddress(string address, int pduRef, string frame) =>
        Assert.Equal(Hex(frame), S7Frame.Job((ushort)pduRef, S7Frame.ReadParam([S7Address.Parse(address, "a").ToItem()]), []));
    #endregion

    #region Read Var 응답
    [Fact]
    public void ParseRead_RealCapture_WinCC8Items_TransportSizeUnitsAndFillBytes()
    {
        // C1 실장비 응답: 비트(03, 길이 1비트) 뒤 채움 바이트, REAL(07, 길이 4바이트), DWORD(04, 32비트), WORD(04, 16비트), T/C(09, 2바이트, 마지막은 채움 없음)
        var pdu = Pdu("03 00 00 49 02 F0 80 32 03 00 00 00 02 00 02 00 34 00 00 04 08 " +
            "FF 03 00 01 01 00 FF 07 00 04 AA BB CC DD FF 04 00 20 FE AD BE EF FF 04 00 10 BA BE " +
            "FF 03 00 01 01 00 FF 03 00 01 01 00 FF 09 00 02 25 04 FF 09 00 02 00 11");
        var r = S7Frame.ParseRead(pdu, [1, 4, 4, 2, 1, 1, 2, 2]);
        Assert.Equal(S7Status.Ack, r.Status);
        Assert.Equal(new[] { "01", "AABBCCDD", "FEADBEEF", "BABE", "01", "01", "2504", "0011" }, r.Items.Select(Convert.ToHexString));
    }

    [Fact]
    public void ParseRead_RealCapture_MB0x16()
    {
        var r = S7Frame.ParseRead(Pdu("03 00 00 29 02 F0 80 32 03 00 00 00 01 00 02 00 14 00 00 04 01 FF 04 00 80 A9 10 00 00 00 00 01 01 00 00 00 00 00 00 00 00"), [16]);
        Assert.Equal(S7Status.Ack, r.Status);
        Assert.Equal("A9100000000001010000000000000000", Convert.ToHexString(r.Items[0]));
    }

    [Fact]
    public void ParseRead_S7NetLiteral_EndsWithReal()
    {
        // T1: 16바이트, 끝 4바이트 = REAL 1.1 (3F 8C CC CD)
        var r = S7Frame.ParseRead(Pdu("03 00 00 29 02 F0 80 32 03 00 00 00 01 00 02 00 14 00 00 04 01 FF 04 00 80 77 10 00 01 00 00 01 03 00 00 00 03 3F 8C CC CD"), [16]);
        Assert.Equal(1.1f, BitConverter.Int32BitsToSingle((int)S7Address.Parse("MD12", "a").Decode(r.Items[0].AsSpan(12))));
    }

    [Fact]
    public void ParseRead_RealCapture_MissingDb_IsNak()
    {
        // C1: DB1이 없는 S7-300 → 항목 반환 코드 0A, 4바이트 (rc 00 00 00)
        var r = S7Frame.ParseRead(Pdu("03 00 00 19 02 F0 80 32 03 00 00 00 00 00 02 00 04 00 00 04 01 0A 00 00 00"), [64]);
        Assert.Equal((S7Status.Nak, 0x0A, 0), (r.Status, r.ErrorCode, r.ItemIndex));
    }

    [Theory]
    [InlineData("05 00 00 04")]   // D: Snap7 서버 형식 — 길이 필드가 4지만 데이터 없음
    [InlineData("05 00 00 00")]   // D: 실장비 형식
    public void ParseRead_FailedItemInMiddle_Skips4Bytes_FirstFailureWins(string failed)
    {
        var pdu = Hex("32 03 00 00 00 09 00 02 00 0F 00 00 04 03 FF 04 00 10 12 34 " + failed + " FF 03 00 01 01");
        var r = S7Frame.ParseRead(pdu, [2, 2, 1]);
        Assert.Equal((S7Status.Nak, 0x05, 1), (r.Status, r.ErrorCode, r.ItemIndex));
    }

    [Fact]
    public void ParseRead_RealCapture_HeaderError_IsNakWithIndexMinus1()
    {
        // C1: Ack(02), 오류 등급 D2 코드 0C
        var r = S7Frame.ParseRead(Pdu("03 00 00 13 02 F0 80 32 02 00 00 15 00 00 00 00 00 D2 0C"), [2]);
        Assert.Equal((S7Status.Nak, 0xD20C, -1), (r.Status, r.ErrorCode, r.ItemIndex));
    }

    [Theory]
    [InlineData("32 03 00 00 00 01 00 02 00 06 00 00 04 01 FF 04 00 08 12 34", "2")]           // 길이 8비트인데 2바이트 기대
    [InlineData("32 03 00 00 00 01 00 02 00 06 00 00 04 01 FF 02 00 10 12 34", "2")]           // 모르는 전송 크기
    [InlineData("32 03 00 00 00 01 00 02 00 06 00 00 05 01 FF 04 00 10 12 34", "2")]           // 기능 코드가 쓰기
    [InlineData("32 03 00 00 00 01 00 02 00 06 00 00 04 02 FF 04 00 10 12 34", "2")]           // 항목 수가 다름
    [InlineData("32 03 00 00 00 01 00 02 00 09 00 00 04 02 FF 03 00 01 01 FF 03 00 01", "1,1")]  // 둘째 항목 데이터 없음 (요청 2개)
    [InlineData("32 07 00 00 00 01 00 02 00 06 04 01 FF 04 00 10 12 34", "2")]                 // Userdata
    [InlineData("33 03 00 00 00 01 00 02 00 06 00 00 04 01 FF 04 00 10 12 34", "2")]           // 프로토콜 ID
    [InlineData("32 03 00 00 00 01 00 02 00 07 00 00 04 01 FF 04 00 10 12 34", "2")]           // 데이터 길이 필드가 맞지 않음
    [InlineData("32 03 00 00 00 01 00 02 00 08 00 00 04 01 FF 04 00 10 12 34 00 00", "2")]     // 끝에 남는 바이트 2개
    public void ParseRead_Malformed_IsInvalid(string pdu, string lengths)
        => Assert.Equal(S7Status.Invalid, S7Frame.ParseRead(Hex(pdu), [.. lengths.Split(',').Select(int.Parse)]).Status);

    [Fact]
    public void ParseRead_OddLastItem_TrailingFillTolerated()
    {
        // D: 비트 하나 응답 뒤에 채움 바이트가 붙어 와도 받는다
        var r = S7Frame.ParseRead(Hex("32 03 00 00 00 01 00 02 00 06 00 00 04 01 FF 03 00 01 01 00"), [1]);
        Assert.Equal((S7Status.Ack, "01"), (r.Status, Convert.ToHexString(r.Items[0])));
    }
    #endregion

    #region Write Var
    [Fact]
    public void WriteRequest_RealCapture_WinCCBit() =>
        // C1: M1.0 = 0
        Assert.Equal(Hex("03 00 00 24 02 F0 80 32 01 00 00 00 01 00 0E 00 05 05 01 12 0A 10 01 00 01 00 00 83 00 00 08 00 03 00 01 00"),
            Write(1, ("M1.0", 0)));

    [Fact]
    public void WriteRequest_RealCapture_LibnodaveMB0x4()
    {
        // C2: MB0 × 4 = A9 10 00 01 (연속 쓰기 조각과 같은 형태)
        var (p, d) = S7Frame.WriteParam([(S7Item.Bytes(S7Address.AreaM, 0, 0, 4), Hex("A9 10 00 01"))]);
        Assert.Equal(Hex("03 00 00 27 02 F0 80 32 01 00 00 00 02 00 0E 00 08 05 01 12 0A 10 02 00 04 00 00 83 00 00 00 00 04 00 20 A9 10 00 01"), S7Frame.Job(2, p, d));
    }

    [Fact]
    public void WriteRequest_Derived_RealAsDword() =>
        Assert.Equal(Hex("03 00 00 27 02 F0 80 32 01 00 00 00 05 00 0E 00 08 05 01 12 0A 10 02 00 04 00 01 84 00 00 20 00 04 00 20 3F C0 00 00"),
            Write(5, ("DB1.DBD4", BitConverter.SingleToInt32Bits(1.5f))));

    [Fact]
    public void WriteRequest_Derived_OutputBit() =>
        Assert.Equal(Hex("03 00 00 24 02 F0 80 32 01 00 00 00 06 00 0E 00 05 05 01 12 0A 10 01 00 01 00 00 82 00 00 00 00 03 00 01 01"),
            Write(6, ("Q0.0", 1)));

    [Fact]
    public void WriteRequest_Derived_FillByteAfterOddItemExceptLast()
    {
        // D: 비트(1바이트) 뒤 채움 00, 마지막 홀수 항목 뒤에는 없음
        Assert.Equal(Hex("03 00 00 37 02 F0 80 32 01 00 00 00 07 00 1A 00 0C 05 02 12 0A 10 01 00 01 00 00 83 00 00 00 12 0A 10 02 00 02 00 00 83 00 00 10 00 03 00 01 01 00 00 04 00 10 12 34"),
            Write(7, ("M0.0", 1), ("MW2", 0x1234)));
        Assert.Equal(Hex("03 00 00 36 02 F0 80 32 01 00 00 00 08 00 1A 00 0B 05 02 12 0A 10 02 00 02 00 00 83 00 00 10 12 0A 10 02 00 01 00 00 83 00 00 28 00 04 00 10 12 34 00 04 00 08 56"),
            Write(8, ("MW2", 0x1234), ("MB5", 0x56)));
    }

    static byte[] Write(ushort pduRef, params (string Address, long Value)[] values)
    {
        var (p, d) = S7Frame.WriteParam([.. values.Select(v => { var a = S7Address.Parse(v.Address, "a"); return (a.ToItem(), a.Encode(v.Value)); })]);
        return S7Frame.Job(pduRef, p, d);
    }

    [Fact]
    public void ParseWrite_RealCaptures()
    {
        Assert.Equal(S7Status.Ack, S7Frame.ParseWrite(Pdu("03 00 00 16 02 F0 80 32 03 00 00 00 01 00 02 00 01 00 00 05 01 FF"), 1).Status);
        // C1 Snap7 5항목 쓰기: T/C 쓰기는 03(접근 거부) — 응답 데이터 FF FF FF 03 03 (헤더는 D)
        var r = S7Frame.ParseWrite(Hex("32 03 00 00 00 01 00 02 00 05 00 00 05 05 FF FF FF 03 03"), 5);
        Assert.Equal((S7Status.Nak, 0x03, 3), (r.Status, r.ErrorCode, r.ItemIndex));
        Assert.Equal(S7Status.Invalid, S7Frame.ParseWrite(Hex("32 03 00 00 00 01 00 02 00 01 00 00 05 02 FF"), 2).Status);
        Assert.Equal(S7Status.Invalid, S7Frame.ParseWrite(Hex("32 03 00 00 00 01 00 02 00 01 00 00 04 01 FF"), 1).Status);
    }
    #endregion

    #region 프레임 읽기 (TPKT / COTP DT 조각)
    [Fact]
    public void ReadPdu_EmptyFragmentsThenPdu()
    {
        // T1 COTP_ReadTSDU 구조: 빈 DT(EOT 아님) 두 개 뒤 실제 PDU (PDU는 C2 Setup 응답)
        var stream = Hex("03 00 00 07 02 F0 00 03 00 00 07 02 F0 00 03 00 00 1B 02 F0 80 32 03 00 00 FF FF 00 08 00 00 00 00 F0 00 00 01 00 01 00 F0");
        var pdu = S7Frame.ReadPdu(Reader(stream));
        Assert.Equal(240, S7Frame.ParseSetup(pdu));
    }

    [Fact]
    public void ReadPdu_SplitFragments_Reassembled()
    {
        // D: C2 실장비 MB0×16 응답을 DT 두 조각(EOT=0, EOT=1)으로 나눔
        var whole = Pdu("03 00 00 29 02 F0 80 32 03 00 00 00 01 00 02 00 14 00 00 04 01 FF 04 00 80 A9 10 00 00 00 00 01 01 00 00 00 00 00 00 00 00");
        byte[] Dt(byte[] part, bool eot) => [0x03, 0x00, 0x00, (byte)(part.Length + 7), 0x02, 0xF0, (byte)(eot ? 0x80 : 0x00), .. part];
        var pdu = S7Frame.ReadPdu(Reader([.. Dt(whole[..10], false), .. Dt(whole[10..], true)]));
        Assert.Equal(whole, pdu);
    }

    [Fact]
    public void ReadPdu_EndlessNonEotFragments_ThrowsIOException()
    {
        // EOT가 영원히 오지 않는 조각(각 1000바이트) → 65535바이트를 넘으면 IOException으로 끝나야 한다
        var part = new byte[] { 0x03, 0x00, 0x03, 0xEF, 0x02, 0xF0, 0x00 }.Concat(new byte[1000]).ToArray();
        Assert.Throws<IOException>(() => S7Frame.ReadPdu(n => n == 4 ? part[..4] : part[4..]));
    }

    [Fact]
    public void ReadPdu_ShortStream_ReturnsNull()
        // T1 TPKT_ReadShort: TPKT 길이는 0x29인데 스트림이 일찍 끝남 → 시간 초과(null), 멈추지 않음
        => Assert.Null(S7Frame.ReadPdu(Reader(Hex("03 00 00 29 02 F0 80 32 03 00 00 00 01 00 02"))));

    [Theory]
    [InlineData("04 00 00 07 02 F0 80")]   // TPKT 버전
    [InlineData("03 00 00 06 02 F0 80")]   // TPKT 길이 7 미만
    [InlineData("03 00 00 0B 06 80 00 00 00 01 00")]   // DT가 아님 (DR)
    [InlineData("03 00 00 07 03 F0 80")]   // DT 길이 표시
    public void ReadPdu_Malformed_ThrowsIOException(string stream)
        => Assert.Throws<IOException>(() => S7Frame.ReadPdu(Reader(Hex(stream))));

    [Fact]
    public void ParseHeader_PduReference()
    {
        var h = S7Frame.ParseHeader(Pdu("03 00 00 1B 02 F0 80 32 03 00 00 FF FF 00 08 00 00 00 00 F0 00 00 01 00 01 00 F0"))!.Value;
        Assert.Equal((S7Frame.RosctrAckData, (ushort)0xFFFF, 0, 12, 8, 20, 0), (h.Rosctr, h.PduRef, h.Error, h.ParamOffset, h.ParamLength, h.DataOffset, h.DataLength));
    }
    #endregion

    #region 크기 한계
    [Theory]
    [InlineData(240, 222, 212)]
    [InlineData(480, 462, 452)]
    [InlineData(960, 942, 932)]
    public void Chunks_MatchOfficialGetPutLimits(int pdu, int read, int write)
        // S7-1200 공식 매뉴얼: GET 222바이트 / PUT 212바이트 (PDU 240)
        => Assert.Equal((read, write), (S7Frame.ReadChunk(pdu), S7Frame.WriteChunk(pdu)));

    [Fact]
    public void Fits_MinimumPdu240()
    {
        Assert.True(S7Frame.ReadFits(Enumerable.Repeat(2, 19).ToList()));    // 요청 10+2+12×19 = 240
        Assert.False(S7Frame.ReadFits(Enumerable.Repeat(2, 20).ToList()));   // 252
        Assert.True(S7Frame.ReadFits([222]));                                  // 응답 12+2+4+222 = 240
        Assert.False(S7Frame.ReadFits([223]));
        Assert.True(S7Frame.WriteFits(Enumerable.Repeat(1, 12).ToList()));   // 비트 12개: 227
        Assert.False(S7Frame.WriteFits(Enumerable.Repeat(1, 13).ToList()));  // 245
        Assert.True(S7Frame.WriteFits([212]));
        Assert.False(S7Frame.WriteFits([213]));
        Assert.False(S7Frame.WriteFits([211, 1]));                             // 채움 바이트까지 계산: 10+2+24+(4+211+1)+(4+1)=257
    }
    #endregion
}
