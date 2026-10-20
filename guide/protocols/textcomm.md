# TextComm

| 클래스 | 전송 | 역할 |
|---|---|---|
| `TextCommRTUMaster` / `TextCommRTUSlave` | Serial | Master / Slave |
| `TextCommTCPMaster` / `TextCommTCPSlave` | TCP | Master / Slave |

* STX(0x02) / ETX(0x03) / DLE(0x10) 기반 텍스트 메시지 프로토콜
* Slave는 `MessageRequest` 이벤트에서 응답 메시지를 정함
