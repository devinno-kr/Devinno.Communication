namespace Devinno.Communication.Omron.Data;

// 등록할 때 검사를 마친 요청 하나 (전송과 무관). FinsUdp·FinsTcp·FinsSerial의 Work 팩토리가 함께 쓴다.
// Items: Read는 요청한 주소들, 나머지는 시작 주소 1개. Count: Read는 주소 수, Write는 1, 블록은 워드·비트 수.
// Values: Write/WriteBlock의 쓸 값(복사본), 읽기는 빈 배열. 조각 나누기는 FinsFrame.Chunks(Command, Items, Count, Values, 한 요청 최대 개수).
// 예외의 ParamName은 공개 메서드의 매개변수 이름("addresses", "address", "count", "value", "values")이다.
internal sealed record FinsRequest(FinsCommand Command, FinsAddress[] Items, int Count, long[] Values)
{
    // 개별 읽기(0104). maxItems = 전송별 한 요청 주소 수 한계 (Ethernet FinsFrame.MaxItems, Host Link FinsFrame.SerialMaxItems)
    public static FinsRequest Read(IReadOnlyList<string> addresses, int maxItems)
    {
        var items = FinsAddress.ParseItems(addresses, maxItems, "addresses");
        return new(FinsCommand.Read, items, items.Length, []);
    }

    // 연속 읽기(0101). 개수 1~65535, 마지막 워드·비트가 영역 안
    public static FinsRequest ReadBlock(string address, int count)
        => new(FinsCommand.ReadBlock, [FinsAddress.ParseBlock(address, count, "address", "count")], count, []);

    // 쓰기(0102) 하나. 비트 0/1, 워드 -32768~65535
    public static FinsRequest Write(string address, long value)
    {
        var a = FinsAddress.Parse(address, "address");
        a.CheckValue(value, "value");
        return new(FinsCommand.Write, [a], 1, [value]);
    }

    // 연속 쓰기(0102). 개수 1~65535, 마지막 워드·비트가 영역 안, 값마다 범위 검사. 값은 복사한다.
    public static FinsRequest WriteBlock(string address, IReadOnlyList<long> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var a = FinsAddress.ParseBlock(address, values.Count, "address", "values");
        foreach (var v in values) a.CheckValue(v, "values");
        return new(FinsCommand.WriteBlock, [a], values.Count, [.. values]);
    }
}
