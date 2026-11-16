# SECS/GEM

| 클래스 | 전송 | 역할 |
|---|---|---|
| `SecsHsms` (HSMS-SS, SEMI E37, Active/Passive) | TCP | Host / Equipment |
| `SecsSerial` (SECS-I, SEMI E4, RS-232) | Serial | Host / Equipment |
| `GemMessages` (GEM 메시지 본문 만들기·해석, 상태 없음) | — | Host / Equipment |
| `GemEquipment` (GEM 장비 쪽, SEMI E30: 통신·제어 상태, 이벤트·알람 보고, 리모트 명령, EC, 시계, 터미널) | HSMS / SECS-I | Equipment |
| `GemHost` (GEM Host 쪽: 접속 시퀀스, 리포트 구독·이벤트 해석, 알람·터미널 받기, 명령 메서드) | HSMS / SECS-I | Host |

## 세 층
| 층 | 클래스 | 하는 일 | 상태 |
|---|---|---|---|
| 전송 | `SecsHsms` · `SecsSerial` | SECS-II 메시지 원본을 주고받음(`Send`·`PrimaryReceived`·`SendOrdered`) | 연결 |
| 메시징 | `GemMessages` | GEM 메시지 본문을 만들고(`S1F13(...)`) 읽음(`ParseS6F11(...)`) | 없음 |
| 모듈 | `GemEquipment` · `GemHost` | E30 상태(통신·제어·리포트)를 갖고 자동 응답 | 있음 |

* 연결 하나에는 한 층을 고름. 메시징 층은 모듈과 섞어 써도 모듈의 상태에 영향을 주지 않지만, **모듈을 쓰면 전송의 `PrimaryReceived`를 직접 구독하지 않음**(모듈이 쓰고 있음)
* 모듈이 지원하지 않는 메시지를 다루거나 상태를 직접 관리하고 싶을 때 메시징 층을 씀

## SECS-II 전송 (SecsHsms · SecsSerial)
SECS-II 메시지(`SecsItem`, `SecsMessage`, SML 텍스트 변환)를 HSMS(TCP)나 SECS-I(RS-232)로 주고받음. 양방향 대화형이라 Auto/Manual 폴링 대신 `Send`/`SendAsync`(응답을 T3까지 기다림)와 `PrimaryReceived`(상대의 1차 메시지, 핸들러에서 `Reply`)를 씀. Select·Linktest·Separate, SECS-I 블록 분할·재시도·경합은 자동 처리하고, Equipment 역할이면 S9Fx 오류 메시지를 자동으로 보냄

## GEM 장비 (GemEquipment)
`GemEquipment`에 SV·DV·EC·CE·알람·리모트 명령을 정의만 하면(코드 또는 XML) 통신 수립(S1F13), 제어 상태(ON-LINE/OFF-LINE, LOCAL/REMOTE), Host의 조회·설정(S1F3/11/21/23, S2F13/15/29, S2F33/35/37, S5F3/5/7, S6F15/19, S10F3/5, 시계)에 라이브러리가 자동으로 응답. 장비 쪽은 `TriggerEvent`·`SetAlarm`·`SendTerminalMessage`로 알리기만 하면 됨(값은 부른 순간 기준, 한 번에 하나씩 순서대로 보냄).

* 트레이스·리밋은 아직 없음(등록 메시지로 앱이 직접 처리 가능)
* E30 원문 대조는 하지 못했고 secsgem과의 상호 운용만 확인함

### 스풀링 (SEMI E30 7.12)
기본은 꺼짐. `GemEquipment.SpoolDirectory`에 폴더를 정하면 켜짐(`MaxSpoolMessages` 1~1,000,000, 기본 1000). 통신이 실패하면(제어 상태와 상관없이) 스풀이 ACTIVE가 되고, ACTIVE인 동안 Host가 S2F43으로 고른 S5F1·S6F11·S10F1을 `SendFailed`로 버리는 대신 그 폴더에 쌓고(OFF-LINE이면 보내지 않으므로 쌓지도 않음), Host가 S6F23으로 되찾아 감(오래된 것부터 하나씩, 응답이 와야 다음). 켜면 표준 SV(SpoolCountActual 1009 · SpoolCountTotal 1010 · SpoolFullTime 1011 · SpoolStartTime 1012)·EC(EnableSpooling 1103 · MaxSpoolTransmit 1104 · OverWriteSpool 1105)·CE(SpoolingActivated 1307 · SpoolingDeactivated 1308 · SpoolTransmitFailure 1309)가 카탈로그에 들어가고 S2F43·S6F23을 GEM이 직접 처리함(`RegisterMessage`로 등록 불가). 상태는 `SpoolState`·`IsSpoolFull`·`SpoolCount`·`SpoolStateChanged`. 스풀이 가득 차 버려진 메시지는 `SendFailed(SpoolFull)`(`OverWriteSpool`이 true면 가장 오래된 것을 버림). EnableSpooling을 false로 하면 새 활성화만 막고 이미 ACTIVE인 스풀은 그대로임.
Host 쪽은 `GemHost.SetSpoolStreams`(S2F43)·`TransmitSpool`·`PurgeSpool`(S6F23)을 씀.

* **Host가 S6F23을 보내야 함**: 스풀이 ACTIVE인 동안에는 통신이 다시 수립돼도 고른 메시지가 실시간으로 나가지 않고 스풀 끝에 쌓임. Host가 S6F23(전송 또는 지우기)을 보내기 전에는 다시 실시간으로 돌아오지 않음
* **최소 한 번 전달**: 비정상 종료나, 스풀에서 꺼내 보내는 중에 `DisableCommunication`이 끼면 그 메시지가 나중에 다시 나갈 수 있음(Host는 중복을 견뎌야 함)
* **폴더 하나 = 장비 하나**: 같은 폴더로 두 번째 `Start`를 하면 `IOException`
* **정전**: 쓰기는 비동기라 마지막 몇 개는 사라질 수 있음. 비정상 종료로 상태 파일을 잃으면 SpoolStartTime이 비어 있을 수 있음
* 스풀한 값은 부른 순간 기준이라 Host가 받을 때는 현재 장비 상태와 다를 수 있음
* 이벤트 순서(스풀을 켰을 때): 끊김의 스풀한 메시지는 `SendFailed`를 내지 않음. 순서는 ① 스풀하지 못한 것의 `SendFailed` → ② `CommunicationStateChanged` → ③ (전이 4의 `ControlStateChanged`) → ④ `SpoolStateChanged`(활성화). 꺼져 있으면 아래 표 그대로

### 설정 영속화
기본은 꺼짐. `GemEquipment.StateDirectory`에 폴더를 정하면 켜짐. Host가 정한 리포트(S2F33)·링크(S2F35)·CE 활성화(S2F37)·알람 활성화(S5F3)와 모든 EC 값(S2F15·`SetConstant`), LOCAL/REMOTE 스위치를 `gem.state`(XML)에 저장하고 다음 `Start`에서 되살림. 그래서 처음 접속할 때만 설정하는 Host도 장비를 재시작한 뒤 데이터 수집이 끊기지 않음. 시계 오프셋·SV/DV 값·알람 발생 상태는 저장하지 않음(앱이 장비 실제 상태로 다시 정함).

* **정의가 바뀐 경우**: 앱을 업데이트해 VID·CE·알람·EC를 빼거나 ID 포맷을 바꿨으면, 지금 정의와 맞지 않는 항목만 버리고 나머지는 복원. 버린 개수는 `Start` 뒤 `ProtocolError` 한 번. 새로 더한 CE·알람은 `EventsEnabledByDefault`·`AlarmsEnabledByDefault`대로 시작
* **폴더 하나 = 장비 하나**: 같은 폴더로 두 번째 `Start`를 하면 `IOException`. `SpoolDirectory`와 같은 폴더는 써도 됨(파일 이름이 다름)
* **정전**: 파일은 응답 뒤 별도 스레드에서 쓰므로 바뀐 직후의 정전에는 마지막 변경을 잃을 수 있음. 깨진 파일은 `gem.state.bad`로 옮기고 초기값으로 시작
* **처음 상태로 되돌리기**: 멈춘 동안 `gem.state`를 지움
* `InitialControlState`를 OnlineLocal·OnlineRemote로 정했으면 그 설정이 저장된 스위치 위치보다 우선

### 확장 리모트 명령(S2F49)과 다중 블록 문의(S6F5)
* **S2F49**는 S2F41과 같은 명령 정의(`AddRemoteCommand`)와 같은 `RemoteCommandReceived`로 받고 S2F50으로 답함. `e.ObjectSpec`에 대상 객체(OBJSPEC), `e.IsEnhanced`가 true. 리스트 값 파라미터도 받고(정의 포맷이 null이면 아무 포맷, `SecsFormat.List`면 리스트만), 리스트 CEPACK은 `e.SetParameterAck(name, SecsItem)`. 대상이 없으면 HCACK 6(`GemAck.HcackNoSuchObject`)은 앱이 정함. 앱이 `RegisterMessage(2, 49)`를 했으면 앱이 처리. S2F21은 처리하지 않음
* GemHost: `SendEnhancedRemoteCommand(objectSpec, rcmd, params…)`(DATAID는 S2F33과 같은 카운터)
* **S6F5(SECS-I 전용, 기본 꺼짐)**: `MultiBlockInquire = true`면 본문이 244바이트를 넘는 S6F11·S6F1(트레이스 보고) 앞에 S6F5를 먼저 보냄. GRANT6 0이면 바로 S6F11, 아니면 보내지 않고 `SendFailed(NotGranted)`(재시도 없음). 스풀 머리가 거절되면 스풀에서 지우고 내보내기를 이어 감. HSMS에서는 켜도 아무 변화 없음

### 트레이스 데이터 수집(S2F23·S6F1)
* Host가 S2F23으로 트레이스를 정의하면 GEM이 받아서 처리함. 앱이 트레이스를 시작하는 API는 없음(Host가 정하는 능력). 대상은 SV만(앱 SV + 표준 SV), DV·EC는 TIAACK 4
* GEM 작업 스레드가 주기(DSPER)마다 SV 값을 표본으로 뜸(TriggerEvent와 같은 스냅숏). 첫 표본은 응답(S2F24) 뒤 한 주기 뒤. REPGSZ개가 모이면 S6F1 하나로 보냄. 본문은 표본을 순서대로 이어 붙인 평평한 목록(표본1 SV1..SVk, 표본2 …)
* **SMPLN** = 그 보고의 마지막 표본 번호(1부터), **STIME** = 그 보고 첫 표본을 뜬 순간의 GEM 시계(`TimeFormat` 형식). Host는 STIME + i·DSPER로 표본 시각을 복원
* 늦은 틱(부하)은 놓친 표본을 메우지 않음: 표본 하나만 뜨고 다음 예정 시각으로(누적 지연 없음, 번호는 SMPLN이 셈)
* TOTSMP개를 다 보내면 저절로 지움. TOTSMP가 REPGSZ의 배수가 아니면 마지막 보고는 남은 표본만
* TOTSMP 0 = 그 TRID 멈춤(없는 TRID여도 0). 같은 TRID를 다시 보내면 앞 트레이스를 끝내고 새로 시작. 단 거절되면 앞 트레이스는 그대로
* TIAACK: 1 SVID 너무 많음(`MaxTraceVariables`, 기본 100), 2 트레이스 더 못 받음(`MaxTraces`, 기본 10, 같은 TRID 재정의는 세지 않음), 3 주기 잘못(DSPER가 `hhmmss`·`hhmmsscc`가 아니거나 0이거나 `MinTracePeriod` 기본 100ms보다 짧음), 4 모르는 SVID(빈 목록 포함), 5 REPGSZ 잘못(0, TOTSMP보다 큼, 1000 초과). 문제가 여럿이면 번호가 낮은 것
* TRID는 받은 아이템 그대로 S6F1에 돌려줌. 정수 포맷끼리는 같은 TRID(U1 5 = U4 5), 정수 5와 A "5"는 다름
* OFF-LINE이면 표본은 계속 뜨지만 보고는 버림(`SendFailed` 없음, 번호는 계속 늘어 ON-LINE 복귀 뒤 빈 구간을 알 수 있음). 연결이 끊기면 버림 경로(`SendFailed NotCommunicating`), 스풀링 ACTIVE이고 S2F43으로 S6F1을 대상으로 정했으면 스풀
* `DisableCommunication`·`Stop`이면 모든 트레이스를 지움(영속하지 않음). `MaxTraces = 0`이면 트레이스를 끔(S2F23에 S9F5). 앱이 `RegisterMessage(2, 23)`를 했으면 앱이 처리
* `TraceReplyExpected`(기본 true) = S6F1 W비트, ACKC6 ≠ 0이면 `SendFailed(NotAcknowledged)`. `ActiveTraces`는 진단용 복사본
* GemHost: `InitializeTrace(trid, period, totalSamples, groupSize, svids)`(주기는 `hhmmsscc`로 보냄, 1/100초 미만 버림) → TIAACK, `StopTrace(trid)`(TOTSMP 0). `TraceDataReceived`는 값을 표본별로 나눠 줌(자기가 시작한 TRID의 SVID 수로 나눔, 모르면 값 전체가 한 항목). 구독자가 없으면 S6F1은 `MessageReceived`로

### 레시피 관리 (언포맷 Process Program, S7)
기본은 꺼짐. `GemEquipment.ProcessProgramStore`에 저장소(`GemProcessProgramFolder` 또는 직접 만든 `IGemProcessProgramStore`)를 정하면 켜짐. 켜면 GEM이 Stream 7을 직접 처리하고 표준 SV(PPExecName 1013 · PPFormat 1014)·DV(PPChangeName 1202 · PPChangeStatus 1203)·CE(ProcessProgramChange 1310 · ProcessProgramSelected 1311)와 리모트 명령 PP-SELECT(파라미터 PPID)가 생김.

* **Host가 바꿀 때**: S7F1(문의)·S7F3(다운로드)·S7F17(삭제)은 `ProcessProgramChangeRequested`로 앱에 묻고 `Ack`가 0이면 저장소를 바꿈. 실행 중인 레시피처럼 앱만 아는 것은 여기서 거절(권장 ACKC7 1). 선택된 레시피의 덮어쓰기·삭제와 없는 PPID 삭제는 GEM이 먼저 거절. S7F1 없이 온 S7F3도 받음. S7F5(요청)·S7F19(목록)는 저장소로 바로 답함
* **LOCAL에서도** 레시피 전송·선택(PP-SELECT)은 받음(다른 리모트 명령은 지금처럼 HCACK 2). OFF-LINE이면 S7F0
* **운전자 변경**은 `SaveProcessProgram`·`DeleteProcessProgram`(ProcessProgramChange CE), **선택**은 `SelectProcessProgram`(PPExecName + ProcessProgramSelected CE). Host가 바꾼 것에는 ProcessProgramChange CE를 내지 않음(E30은 운전자 변경에만 씀)
* **장비 → Host**: `UploadProcessProgramAsync`(S7F1 → S7F3)·`RequestProcessProgramAsync`(S7F5). 결과는 Task로 오고 `SendFailed`는 내지 않음. 스풀하지 않음
* **폴더 저장소**: PPID 하나 = 파일 하나. 대소문자만 다른 PPID나 파일 이름에 못 쓰는 문자가 든 PPID도 안전하게 저장(`%XX`, 너무 길면 해시 이름)
* 저장소는 GEM의 lock 하나 안에서만 부르므로 GEM에 대해서는 스레드 안전하지 않아도 됨. 승인 핸들러가 `CommandReplyTimeout`을 넘기면 GEM이 거절로 대신 답하고 늦게 온 결과는 반영하지 않음. 저장소 쓰기는 응답할 권리를 얻은 뒤 하므로 느리면 응답이 늦어질 뿐 Host가 들은 결과와 저장소는 늘 같음. 승인 핸들러는 GEM의 저장소 잠금 밖에서 불리므로 다른 스레드의 레시피 메서드를 기다려도 막히지 않음
* GemHost: `DownloadProcessProgram`·`UploadProcessProgram`·`DeleteProcessPrograms`·`ListProcessPrograms`·`SelectProcessProgram`, 장비가 보낸 S7F1·S7F3·S7F5는 `ProcessProgramReceived`·`ProcessProgramRequested`(구독자가 없으면 지금처럼 `MessageReceived`)

## GEM Host (GemHost)
`GemHost.Subscribe(ceid, vids…)`로 받을 값을 정해 두면 연결될 때마다 접속 시퀀스(S1F13 → S1F17 → 리포트 정의·연결·켜기 → 알람 켜기)를 자동으로 실행하고, 장비의 이벤트 리포트(S6F11)를 VID별 값으로 해석해 `EventReceived`로 줌. 알람(S5F1)·터미널(S10F1)도 자동으로 응답하고 이벤트로 알림. SV·EC 읽기·쓰기, 이름 목록, 시계, 리모트 명령(S2F41), 알람 목록 등은 동기·비동기(`…Async`) 메서드 짝이고 0이 아닌 ack는 예외가 아니라 반환값. 장비마다 다른 ID 포맷(U1~U8, I1~I8, A)을 받아들임.

* SV 주기 읽기(폴링)는 넣지 않음(앱 타이머로 `ReadStatusAsync`)
* 리모트 명령(S2F41)은 `CommandReplyTimeout`이 지나 HCACK 2로 응답해도 핸들러는 계속 돌아 동작이 실행될 수 있음. 핸들러는 짧게 두고 오래 걸리면 HCACK 4로 응답한 뒤 이벤트로 완료를 알릴 것. Host는 타임아웃·연결 끊김·취소·HCACK 2만으로 미실행을 단정하지 말고 S1F3 등으로 상태를 확인한 뒤 다시 보낼 것
* 실제 MES·장비와는 확인하지 못했고 secsgem 장비와의 상호 운용만 확인함

## 메시징 층 (GemMessages)
`GemMessages`는 정적 클래스로, 만들기 `S1F13(...)`는 `SecsMessage`를 돌려주고 해석 `ParseS6F11(...)`는 값이나 기존 정보 형식(`GemEventReport`·`GemAlarmInfo` 등)을 돌려줌. 메서드 이름이 SEMI 메시지 이름이라 IntelliSense에서 `GemMessages.S6`으로 찾을 수 있음. S1·S2·S5·S6·S10의 주요 메시지를 지원함.

* 1차 메시지(홀수 Function)는 W비트가 켜짐. E5에서 응답이 선택인 S5F1·S6F11·S10F1·S10F3만 `replyExpected`(기본 true) 인자가 있음. 응답 빌더(짝수 Function)는 W비트 없이 `e.Reply(GemMessages.S1F14(...))`로 씀
* 해석기는 Stream/Function이 다르면 `ArgumentException`(프로그램 오류), 본문 모양이 다르면 `FormatException`(문장에 앞 2048자 SML). 본문이 없는 메시지(S1F3 등)는 빈 목록
* **받을 때 관대하게**: ID는 정수 8종·A 무엇이든(`GemId`), ack는 숫자 포맷 무엇이든 0~255, ID 목록은 `<L ID…>`·정수 배열·A 하나. **보낼 때 엄격하게**: ID는 `GemFormats`의 포맷(기본 모두 U4)으로, 범위를 넘으면 `ArgumentOutOfRangeException`, ack는 `B` 1바이트(`byte`와 `GemAck` 상수)
* ID가 설정한 포맷에 맞는지, 요청 개수, 텍스트 길이(MDLN 20자, ALTX 120자 등)는 검사하지 않음 — 호출자 몫
* `ParseS6F11`·`ParseS6F16`에 리포트 표(RPTID → VID 목록)를 주면 VID를 붙여 줌. 표 열쇠와 받은 RPTID는 `GemFormats.Rptid` 기준으로 맞춤(A면 표에 `1000`으로 넣어도 장비가 A `"1000"`으로 보낸 RPTID와 맞음)

장비 앱이 직접 S1F13을 보내고 S1F3·S6F11을 다루는 예:
```csharp
var hsms = new SecsHsms { Role = SecsRole.Equipment, Mode = HsmsMode.Passive, LocalPort = 5000 };
long generation = 0;
hsms.DeviceOpened += (s, e) =>
{
    long mine = Interlocked.Increment(ref generation);   // 이 결과가 어느 연결의 것인지
    hsms.SendOrdered(GemMessages.S1F13("EQ", "1.0"), r =>
    {
        if (!r.IsSuccess || mine != Interlocked.Read(ref generation)) return;
        var (commack, modelName, softwareRevision) = GemMessages.ParseS1F14(r.Reply!);
        // commack 0 = 통신 중
    });
};
hsms.PrimaryReceived += (s, e) =>
{
    if (e.Message is { Stream: 1, Function: 3 })
    {
        var svids = GemMessages.ParseS1F3(e.Message);
        e.Reply(GemMessages.S1F4([.. svids.Select(id => SecsItem.F4(25))]));
    }
};
hsms.Start();

// 이벤트 보고(S6F11): 응답(S6F12)이 올 때까지 기다림
var ack = await hsms.SendAsync(GemMessages.S6F11(1, 5001, [(1000, [SecsItem.F4(25), SecsItem.A("LOT1")])]));
byte ackc6 = GemMessages.ParseS6F12(ack!);
```
Host 앱은 `PrimaryReceived`에서 `GemMessages.ParseS6F11(e.Message, table, formats)`로 읽고 `e.Reply(GemMessages.S6F12(GemAck.Ackc6Accepted))`로 응답함.

### SendOrdered와 연결 세대
상태를 직접 관리하면 "응답을 처리하기 전에 상대의 다음 1차 메시지가 `PrimaryReceived`에 먼저 도착"하는 경합을 피해야 함. `SendOrdered(primary, completed)`는 결과를 디스패처 스레드에서 와이어 순서로 정확히 한 번 알림: 응답을 받았다면 그 뒤에 받은 1차 메시지의 `PrimaryReceived`보다 먼저 불림. 결과는 `SecsSendResult`(`Primary`·`Reply`·`Exception`·`IsSuccess`)로 옴.

* 인자 오류와 연결 없음(`InvalidOperationException`)은 `Send`처럼 호출할 때 던지고(그때는 `completed`가 불리지 않음), 그 밖의 실패(SxF0·S9Fx·T3·끊김·송신 실패)는 모두 `Exception`으로 옴
* **연결 세대 세는 법**: `DeviceOpened`마다 번호를 늘리고 보낼 때 그 값을 기록. 끊김 실패(T3 참고)는 그 연결의 `DeviceClosed`보다 먼저일 수도 나중일 수도 있으므로, 완료 때 기록해 둔 번호가 현재 번호와 같은지 보고 낡은 연결의 결과는 버림
* 콜백 안의 동기 `Send`는 되지만 응답을 기다리는 동안(최대 T3) 이벤트가 밀림. **콜백·이벤트 핸들러 안에서 다른 `SendOrdered`의 완료를 기다리면 교착됨**(완료가 같은 디스패처 뒤에 줄 섬). 콜백이 던진 예외는 무시됨

## 실행 모델
  * SecsHsms·SecsSerial은 연결마다 I/O 전용 스레드와 `PrimaryReceived` 디스패처 스레드를 쓰고(`DeviceOpened`·`DeviceClosed`·`ProtocolError`도 같은 디스패처 스레드에서 발생해 핸들러 안에서 `Send`를 불러도 교착되지 않음. 단 디스패처가 연결당 하나라서 핸들러 안의 동기 `Send`는 응답을 기다리는 동안(최대 T3) 상대의 다른 1차 메시지 전달을 막고, 양쪽이 접속 직후 그러면 서로 T3까지 기다릴 수 있으므로 접속 시퀀스는 `SendAsync`로 시작하고 이어서 처리하기를 권장), `Send`는 호출 스레드에서 응답을 기다림(`SendAsync`는 같은 대기를 Task로)
  * GemEquipment는 전송 스레드 외에 GEM 작업 스레드(송신 대기열·타이머)와 앱 이벤트 스레드를 하나씩 더 씀. Host 메시지 자동 응답은 앱 코드 없이 끝나므로 앱 이벤트 핸들러가 오래 걸려도 응답이 늦어지지 않음
  * GemHost도 GEM 작업 스레드(응답 기한)와 앱 이벤트 스레드를 하나씩 더 쓰고 장비 메시지 자동 응답은 앱 코드 없이 끝남. 명령 메서드의 동기 버전은 호출 스레드에서 응답을 기다려 스레드풀이 고갈돼도 늦어지지 않고 UI 스레드나 이벤트 핸들러에서 불러도 교착되지 않음. 핸들러가 멈춰 앱 이벤트가 쌓이면(`MaxPendingEvents`, 기본 10000) 오래된 것부터 버리고 `DroppedEventCount`로 셈(GemEquipment도 같음)
  * SecsHsms·SecsSerial은 이벤트가 I/O 스레드가 아니라 디스패처 스레드에서 발생

## 이벤트 순서 계약
용어: **연결** = HSMS Selected 한 번(Select 성공 ~ 끊김), SECS-I 포트 열림 한 번. **I/O** = 수신 스레드. **디스패처** = 전송당 1개의 `PrimaryReceived` 스레드.

### 전송(SecsHsms · SecsSerial)이 보장하는 것
| # | 보장 |
|---|---|
| T1 | `DeviceOpened`·`PrimaryReceived`·`ProtocolError`·`DeviceClosed`는 디스패처 한 스레드에서 I/O가 알아챈 순서대로 하나씩 발생. `Stop` 때 아직 발생하지 않은 것은 `Stop`을 부른 스레드에서 같은 순서로 발생 |
| T2 | 연결마다 `DeviceOpened` → 그 연결의 `PrimaryReceived`들 → `DeviceClosed` 한 번. 다음 연결의 `DeviceOpened`는 앞 연결의 `DeviceClosed` 뒤 |
| T3 | 링크가 끊기면 그 연결의 대기 트랜잭션 전부가 `SecsDisconnectedException`으로 끝나고 `DeviceClosed`가 한 번 발생. 앱이 끊김 실패를 보는 시점은 그 `DeviceClosed`보다 먼저일 수도 나중일 수도 있음(어느 연결의 실패인지는 앱이 `DeviceOpened`마다 세는 번호로 구분) |
| T4 | `SecsDisconnectedException`을 받았다면 그 연결의 `DeviceClosed`는 이미 발생했거나 곧 발생(`Stop` 경계 제외) |
| T5 | 한 트랜잭션은 정확히 하나의 결과(응답·SxF0·S9·T3·끊김·취소)로 끝남. T3 뒤에 온 응답은 짝 없는 응답(`ProtocolError`) |
| T6 | 동기 `Send`는 호출 스레드에서 기다려 스레드풀이 고갈돼도 늦어지지 않음. `SendAsync`의 이어 실행은 동기로 끝나면 호출 스레드, 아니면 스레드풀. I/O 스레드에서는 돌지 않고, 디스패처 스레드에서는 거기서 불렀을 때만 돔. 핸들러 안 동기 `Send`도 교착되지 않음(최대 T3 동안 다음 이벤트가 밀릴 뿐) |
| T7 | 연결이 없을 때(`DeviceClosed` 뒤, `DeviceOpened` 전) `Send`/`SendAsync`는 바로 `InvalidOperationException`, 연결 중 쓰기 실패는 `SecsDisconnectedException`. 둘 다 "링크 없음"으로 다룸 |
| T8 | 이전 연결의 트랜잭션은 새 연결의 응답으로 끝나지 않음. 이전 연결에서 받은 1차 메시지의 `Reply`는 보내지 않고 `SecsDisconnectedException` |
| T9 | `Stop`이 돌아온 뒤에는 이벤트가 없음. 단 `Stop`이 수신 스레드를 3초 안에 끝내지 못하면 그 연결의 `DeviceClosed`가 빠질 수 있음 |

### 전송이 보장하지 않는 것
| 보장하지 않음 | 앱이 할 일 |
|---|---|
| `Send`/`SendAsync` 결과를 보는 순간과 `PrimaryReceived`·`DeviceClosed`의 선후 | 결과는 호출 스레드·스레드풀, 이벤트는 디스패처로 감. 선후에 기대지 않음 |
| 동시에 보낸 여러 `SendAsync` 결과가 도착하는 순서 | 순서가 필요하면 하나씩 보냄 |
| `IsOpen`·`State`와 이벤트의 선후 | 그 순간의 스냅숏(끊김 예외 뒤에도 잠깐 true일 수 있음). 링크 상태는 `DeviceOpened`/`DeviceClosed`로만 판단 |
| `ProtocolError`의 개수·문장 | 진단용. 동작에 쓰지 않음 |
| SECS-I 재시도 초과(`IOException`) 뒤의 연결 상태 | 회선은 유지(`DeviceClosed` 없음), 그 메시지만 실패 |

### GEM(GemEquipment · GemHost)이 앱에 주는 순서
모든 공개 이벤트는 GEM 앱 이벤트 스레드에서 넣은 순서대로 발생하고, 아래는 그 "넣는 순서"다.

| # | GemEquipment |
|---|---|
| E1 | GEM 상태(통신·제어)는 와이어 순서로 바뀜. Host 1차 메시지 처리(자동 응답, `RemoteCommandReceived` 등)와 GEM 송신 결과(`SendFailed`, S1F2 → `ControlStateChanged`, S2F18 → `ClockSet`, S1F14 → `CommunicationStateChanged`)는 장비가 그 메시지들을 받은 순서대로 일어나고 그 순서로 이벤트가 나감 |
| E2a | 끊김·`DisableCommunication`: ① 버린 메시지의 `SendFailed`(NotCommunicating 또는 Disabled, 원래 보낼 순서대로 — 응답 대기 중이던 것 먼저, 다음 대기열 앞부터, 장비 S1F13은 알리지 않음) → ② `CommunicationStateChanged` → ③ ATTEMPT ON-LINE이었으면 전이 4의 `ControlStateChanged` |
| E2b | S5F1·S6F11·S10F1·S2F17의 T3: ① 그 메시지의 `SendFailed(Timeout)` → ② 남은 대기열의 `SendFailed(NotCommunicating)` → ③ `CommunicationStateChanged` → ④ ATTEMPT ON-LINE이었으면 전이 4의 `ControlStateChanged` |
| E2c | `GoOnline`의 S1F1 T3: ① `SendFailed(Timeout)` → ② `ControlStateChanged`(→ `OnlineFailedState`) → ③ 남은 대기열의 `SendFailed(NotCommunicating)` → ④ `CommunicationStateChanged` |
| E2d | 장비 S1F13의 T3·실패: `ProtocolError`만(통신 실패 아님) |
| E2 참고 | E2a·E2b에서 `SendFailed`가 `CommunicationStateChanged`보다 먼저이므로 `SendFailed` 핸들러 안에서 `CommunicationState`가 아직 Communicating일 수 있음 |
| E3 | 끊김 때문에 GEM 메시지가 `SendFailed(Disconnected)`로 끝나지 않음. COMMUNICATING에서 끊기면 NotCommunicating으로만 알림 |
| E4 | 끊김에 의한 `CommunicationStateChanged(Communicating → NotCommunicating)`은 연결당 최대 한 번이고, 그 연결에 관한 이벤트는 그 뒤에 더 오지 않음 |
| E5 | 앱의 전송 이벤트(`DeviceOpened`·`DeviceClosed`·`ProtocolError`) 핸들러가 재연결을 사이에 두고 전송 디스패처를 붙잡고 있지 않다면, 다시 연결되면 S1F13으로 다시 수립하기 전에는 S5/S6/S10을 보내지 않음(붙잡고 있으면 GEM이 아직 옛 연결로 아는 동안 대기열의 S5/S6/S10이 새 연결로 먼저 나가고, 옛 연결의 `DeviceClosed` 처리 때 `SendFailed(NotCommunicating)`으로 알려질 수 있음 — 전부터 있던 창). `InitiateCommunication`이면 끊긴 뒤(S1F14를 기다리던 중 포함) 다음 연결이 열리자마자 S1F13을 보내고, 연결된 채 실패(T3·COMMACK ≠ 0·S1F0)하면 EstablishCommunicationsTimeout 초 뒤 다시 보냄 |
| E6 | `SendFailed`의 `Disconnected`는 SECS-I 재시도 초과(회선 유지)에만 남음 |

| # | GemHost |
|---|---|
| H1 | GemHost가 그 연결의 `DeviceClosed`를 처리한 뒤에는 그 연결로 `CommunicationStateChanged(→ Communicating)`이 나가지 않고 `IsCommunicating`도 true가 되지 않음(물리적으로 끊긴 뒤 `DeviceClosed`를 처리하기 전까지는 잠깐 true일 수 있고, 처리하면 false로 돌아감). 수동 `EstablishCommunication()`이 COMMACK 0을 받아도 GemHost가 그 연결의 `DeviceClosed`를 이미 처리했으면 반환값만 COMMACK이고 수립되지 않음. GemHost가 그 연결의 `DeviceOpened`를 처리하기 전에 COMMACK 0이 오면 `DeviceOpened`를 처리할 때 수립을 반영. 이 규칙은 앱의 전송 이벤트(`DeviceOpened`·`DeviceClosed`·`ProtocolError`) 핸들러가 재연결을 사이에 두고 전송 디스패처를 붙잡고 있지 않을 때의 이야기다(붙잡고 있으면 새 연결로 보낸 수동 S1F13이 옛 연결 몫으로 잡혀 옛 연결의 `DeviceClosed` 처리 때 지워지고 `IsCommunicating`이 false로 남을 수 있음 — 전부터 있던 창). 전송이 연결되지 않았으면 지금처럼 전송의 `InvalidOperationException` |
| H2 | **자동** 접속 시퀀스가 끊김으로 실패하면 `CommunicationStateChanged(→ NotCommunicating)` → `ConnectSequenceCompleted(실패, SecsDisconnectedException)` 순서(수립 전이면 결과만). `Stop`으로 끝나면 알리지 않음. 수동 `RunConnectSequence()`는 결과를 반환값으로 바로 줌(이 순서 계약 밖) |
| H3 | `EventReceived`의 VID: `DefineReports`(`ConfigureReports`도 씀)의 응답(S2F34)보다 먼저 받은 S6F11은 옛 정의로, 메서드가 돌아온 뒤에 받은 것은 새 정의로 해석. 그 사이에 받은 것은 어느 쪽일 수도 있음 |
| H4 | 동기·비동기 명령 메서드의 예외·반환과 앱 이벤트(`CommunicationStateChanged` 등)의 선후는 보장하지 않음(서로 다른 스레드) |

## XML 설정
  * GemEquipment는 전송 설정과 장비 정의까지, GemHost는 전송 설정과 구독 목록까지 한 파일에 담음
