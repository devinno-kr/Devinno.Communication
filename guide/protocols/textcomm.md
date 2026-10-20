# TextComm

| 클래스 | 전송 | 역할 |
|---|---|---|
| `TextCommRTUMaster` / `TextCommRTUSlave` | Serial | Master / Slave |
| `TextCommTCPMaster` / `TextCommTCPSlave` | TCP | Master / Slave |

* STX(0x02) / ETX(0x03) / DLE(0x10) 기반 텍스트 메시지 프로토콜
* Slave는 `MessageRequest` 이벤트에서 응답 메시지를 정함
* Master는 `ManualSend` 전부를 쓰기로 봄(라이브러리가 명령 의미를 모름): 연결이 없는 동안 `ManualSend`는 대기열에 넣지 않고 `WriteDiscarded`로 알림(`DiscardWritesWhileDisconnected`, 기본 true), 끊길 때와 `Stop()`에서 대기 중인 것도 버림. Auto는 영향 없음. `repeatCount` 재시도는 장비가 이미 실행한 명령을 다시 보내므로 펄스·카운터·증가형 명령에는 쓰지 말 것. `TimeoutReceived`/`CheckSumErrorReceived`의 `Attempt`·`IsFinal`·`RequestSent`로 시도 정보 확인
