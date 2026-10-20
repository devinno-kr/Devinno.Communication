# Omron FINS

| 클래스 | 전송 | 역할 |
|---|---|---|
| `FinsUdp` (FINS/UDP 9600, 0101/0102/0104) | UDP | Master |
| `FinsTcp` (FINS/TCP 9600, 노드 주소 자동 할당) | TCP | Master |
| `FinsSerial` (Host Link FINS, 헤더 코드 FA) | Serial | Master |

* 지원 PLC : CS1/CJ1/CJ2, CP1L/CP1E/CP1H, NJ/NX(CJ 메모리에 AT 지정한 변수)
* CIO/W/H/A/D/EM 워드·비트 주소(D100, CIO100.05, E0_100 …)를 개별(0104)·연속(0101/0102)으로 읽고 쓰며, 큰 블록은 자동으로 나눠 보냄
* C-mode Host Link, CIP 태그, 타이머/카운터는 지원하지 않음

## 이름 있는 쓰기
  * Master XML에 `<Write Name Id Address>`로 쓰기 주소에 이름을 붙이고(Auto 요소 뒤에 작성), `NamedWrite("이름", 값)`으로 쓴다 (`ContainsNamedWrite`로 등록 여부 확인). `FinsUdp`·`FinsTcp`·`FinsSerial` 모두 같다
  * 값 하나는 `ManualWrite`, 값 목록은 `ManualWriteBlock`
  * 공통 설명은 [XML 설정](../concepts.md#이름-있는-쓰기)

```xml
<Write Name="SpeedSet" Id="20" Address="D200" />
```
```csharp
fins.NamedWrite("SpeedSet", 1500);
```
