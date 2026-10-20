# SECS/GEM

| 클래스 | 전송 | 역할 |
|---|---|---|
| `SecsHsms` (HSMS-SS, SEMI E37, Active/Passive) | TCP | Host / Equipment |
| `SecsSerial` (SECS-I, SEMI E4, RS-232) | Serial | Host / Equipment |
| `GemEquipment` (GEM 장비 쪽, SEMI E30: 통신·제어 상태, 이벤트·알람 보고, 리모트 명령, EC, 시계, 터미널) | HSMS / SECS-I | Equipment |
| `GemHost` (GEM Host 쪽: 접속 시퀀스, 리포트 구독·이벤트 해석, 알람·터미널 받기, 명령 메서드) | HSMS / SECS-I | Host |

## SECS-II 전송 (SecsHsms · SecsSerial)
SECS-II 메시지(`SecsItem`, `SecsMessage`, SML 텍스트 변환)를 HSMS(TCP)나 SECS-I(RS-232)로 주고받음. 양방향 대화형이라 Auto/Manual 폴링 대신 `Send`/`SendAsync`(응답을 T3까지 기다림)와 `PrimaryReceived`(상대의 1차 메시지, 핸들러에서 `Reply`)를 씀. Select·Linktest·Separate, SECS-I 블록 분할·재시도·경합은 자동 처리하고, Equipment 역할이면 S9Fx 오류 메시지를 자동으로 보냄

## GEM 장비 (GemEquipment)
`GemEquipment`에 SV·DV·EC·CE·알람·리모트 명령을 정의만 하면(코드 또는 XML) 통신 수립(S1F13), 제어 상태(ON-LINE/OFF-LINE, LOCAL/REMOTE), Host의 조회·설정(S1F3/11/21/23, S2F13/15/29, S2F33/35/37, S5F3/5/7, S6F15/19, S10F3/5, 시계)에 라이브러리가 자동으로 응답. 장비 쪽은 `TriggerEvent`·`SetAlarm`·`SendTerminalMessage`로 알리기만 하면 됨(값은 부른 순간 기준, 한 번에 하나씩 순서대로 보냄).

* 스풀링·트레이스·리밋·레시피(S7)는 아직 없음(등록 메시지로 앱이 직접 처리 가능)
* E30 원문 대조는 하지 못했고 secsgem과의 상호 운용만 확인함

## GEM Host (GemHost)
`GemHost.Subscribe(ceid, vids…)`로 받을 값을 정해 두면 연결될 때마다 접속 시퀀스(S1F13 → S1F17 → 리포트 정의·연결·켜기 → 알람 켜기)를 자동으로 실행하고, 장비의 이벤트 리포트(S6F11)를 VID별 값으로 해석해 `EventReceived`로 줌. 알람(S5F1)·터미널(S10F1)도 자동으로 응답하고 이벤트로 알림. SV·EC 읽기·쓰기, 이름 목록, 시계, 리모트 명령(S2F41), 알람 목록 등은 동기·비동기(`…Async`) 메서드 짝이고 0이 아닌 ack는 예외가 아니라 반환값. 장비마다 다른 ID 포맷(U1~U8, I1~I8, A)을 받아들임.

* SV 주기 읽기(폴링)는 넣지 않음(앱 타이머로 `ReadStatusAsync`)
* 실제 MES·장비와는 확인하지 못했고 secsgem 장비와의 상호 운용만 확인함

## 실행 모델
  * SecsHsms·SecsSerial은 연결마다 I/O 전용 스레드와 `PrimaryReceived` 디스패처 스레드를 쓰고(`DeviceOpened`·`DeviceClosed`·`ProtocolError`도 같은 디스패처 스레드에서 발생해 핸들러 안에서 `Send`를 불러도 교착되지 않음. 단 디스패처가 연결당 하나라서 핸들러 안의 동기 `Send`는 응답을 기다리는 동안(최대 T3) 상대의 다른 1차 메시지 전달을 막고, 양쪽이 접속 직후 그러면 서로 T3까지 기다릴 수 있으므로 접속 시퀀스는 `SendAsync`로 시작하고 이어서 처리하기를 권장), `Send`는 호출 스레드에서 응답을 기다림(`SendAsync`는 같은 대기를 Task로)
  * GemEquipment는 전송 스레드 외에 GEM 작업 스레드(송신 대기열·타이머)와 앱 이벤트 스레드를 하나씩 더 씀. Host 메시지 자동 응답은 앱 코드 없이 끝나므로 앱 이벤트 핸들러가 오래 걸려도 응답이 늦어지지 않음
  * GemHost도 GEM 작업 스레드(응답 기한)와 앱 이벤트 스레드를 하나씩 더 쓰고 장비 메시지 자동 응답은 앱 코드 없이 끝남. 명령 메서드의 동기 버전은 호출 스레드에서 응답을 기다려 스레드풀이 고갈돼도 늦어지지 않고 UI 스레드나 이벤트 핸들러에서 불러도 교착되지 않음. 핸들러가 멈춰 앱 이벤트가 쌓이면(`MaxPendingEvents`, 기본 10000) 오래된 것부터 버리고 `DroppedEventCount`로 셈(GemEquipment도 같음)
  * SecsHsms·SecsSerial은 이벤트가 I/O 스레드가 아니라 디스패처 스레드에서 발생

## XML 설정
  * GemEquipment는 전송 설정과 장비 정의까지, GemHost는 전송 설정과 구독 목록까지 한 파일에 담음
