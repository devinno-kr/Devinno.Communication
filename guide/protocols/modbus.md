# Modbus

| 클래스 | 전송 | 역할 |
|---|---|---|
| `ModbusRTUMaster` / `ModbusRTUSlave` | Serial | Master / Slave |
| `ModbusTCPMaster` / `ModbusTCPSlave` | TCP | Master / Slave |

* 지원 Function : FC1, FC2, FC3, FC4, FC5, FC6, FC15, FC16, FC26

## Slave 메모리
  * Modbus Slave는 사용자가 넘긴 `bool[]` / `int[]` 배열을 메모리로 직접 사용 (주소 = 배열 인덱스)
  * 마스터가 쓰기를 하면 배열에 반영한 다음 Written 이벤트로 알림
  * 범위를 벗어난 주소에는 Modbus 예외 응답 02, 지원하지 않는 Function에는 01로 응답
  * Modbus TCP Slave는 여러 클라이언트의 동시 접속을 지원

## 이름 있는 쓰기
  * Master XML에 `<Write>`로 쓰기 영역에 이름을 붙이고(Auto 요소 뒤에 작성), 코드에서 `NamedWrite("이름", 값)`으로 쓴다 (`ContainsNamedWrite`로 등록 여부 확인)
  * `Func`는 FC5·FC6·FC15·FC16만 허용. 값 하나는 FC5(0/1)·FC6·FC15·FC16 1개, 값 목록은 FC15·FC16 (FC5·FC6은 원소 1개인 목록만 받아 그 값 하나로 쓰고, 2개 이상이면 `ArgumentException`)
  * 값 범위는 FC5·FC15가 0/1, FC6·FC16이 −32768~65535
  * 공통 설명은 [XML 설정](../concepts.md#이름-있는-쓰기)

```xml
<ModbusTCPMaster RemoteIP="192.168.0.40">
  <Write Name="SpeedSet" Id="30" Func="FC6" Slave="1" Start="100" />
</ModbusTCPMaster>
```
```csharp
master.NamedWrite("SpeedSet", 1500);
```
