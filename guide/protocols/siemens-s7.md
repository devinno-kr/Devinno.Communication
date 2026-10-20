# Siemens S7

| 클래스 | 전송 | 역할 |
|---|---|---|
| `S7Ethernet` (S7comm, ISO-on-TCP 102, 개별·연속 읽기/쓰기) | TCP | Master |

* 지원 PLC : S7-300/400/1200/1500, S7-200 SMART·LOGO!(TSAP 직접 지정)
* 절대 주소(DB1.DBW0, M10.0, IB0 …)만 지원하며 심볼·최적화 DB·타이머/카운터는 지원하지 않음
* **S7-1200/1500은 PLC에서 PUT/GET 허용과 "최적화된 블록 액세스" 해제가 필요**
* 연속 쓰기(`ManualWriteBlock`)는 원자적이지 않음: 중간에 실패하면 앞 조각은 이미 PLC에 써졌을 수 있으며(`NAKReceived`/`TimeoutReceived`의 `WrittenBytes`가 그 시도에서 PLC가 Ack한 바이트 수, 재시도는 첫 조각부터 다시), 트리거/핸드셰이크 워드는 블록이 성공한 뒤 따로 쓴다

## 이름 있는 쓰기
  * Master XML에 `<Write Name Id Address>`로 쓰기 주소에 이름을 붙이고(`<Read>` 등 Auto 요소 뒤에 작성), `NamedWrite("이름", 값)`으로 쓴다 (`ContainsNamedWrite`로 등록 여부 확인)
  * 값 하나는 개별 쓰기(`ManualWrite`), 값 목록은 B/W/D 주소에서 값을 그 크기의 빅엔디언으로 이어 붙여 같은 시작 바이트에 연속 쓰기(`ManualWriteBlock`). 비트 주소는 값 목록을 쓸 수 없음
  * 공통 설명은 [XML 설정](../concepts.md#이름-있는-쓰기)

```xml
<Write Name="SpeedSet" Id="20" Address="DB1.DBW10" />
<Write Name="Recipe" Id="21" Address="DB1.DBW20" />
```
```csharp
plc.NamedWrite("SpeedSet", 1500);
plc.NamedWrite("Recipe", [100, 200, 300]);   // 워드 3개
```
