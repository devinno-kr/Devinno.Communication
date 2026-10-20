# MQTT

| 클래스 | 전송 | 역할 |
|---|---|---|
| `MQClient` (MQTTnet 래퍼, 자동 재접속·재구독, 수신 대기열) | TCP | Client |

* 받은 메시지는 내부 대기열(상한·초과 정책 설정 가능)을 거쳐 전용 스레드에서 `Received`로 전달 → 느린 처리가 확인 응답(PUBACK)을 막지 않음
* 1초마다 QoS 1 alive를 보내 응답이 없으면 끊고 1초 간격으로 다시 접속·재구독
* 연결이 없을 때 `Publish`는 보내지 않고 호출한 스레드에서 `PublishDiscarded`를 발생시킴. 명령 토픽에는 retain을 쓰지 말 것(새 구독자마다 다시 받음)
* 비밀번호는 XML에 저장하지 않음
* 의존성 : [MQTTnet](https://www.nuget.org/packages/MQTTnet/5.2.0.1603) 5.2.0.1603

## 실행 모델
  * MQClient는 재접속·alive 루프와 `Received` 디스패처를 각각 전용 백그라운드 스레드에서 돌리고, 소켓 I/O는 MQTTnet async로 처리
