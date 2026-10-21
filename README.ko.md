<!-- en-hash: e3b0c44298fc1c14 -->
[English](https://github.com/devinno-kr/Devinno.Communication/blob/main/README.md) | 한국어

# Devinno.Communication
<!-- en-hash: 04ee66a7d62b90d3 -->

산업용 PLC·장비 통신 프로토콜을 하나로 모은 .NET 8 라이브러리
(Modbus · LS산전 · Mitsubishi · Siemens · Omron · MQTT · SECS/GEM)

> ⚠️ **개발 중 (0.x)** — 버전이 올라갈 때 API가 바뀔 수 있음

## 설계: AI와 잘 맞는 통신 라이브러리
<!-- en-hash: d6cff4f6e145eb6b -->

Devinno.Communication은 장비 통신 설정을 AI(LLM) 코딩 도구로 한다고 가정하고 만들었습니다. AI가 PLC 메모리맵을 읽고, 라이브러리는 AI의 실수가 조용히 넘어가지 않고 드러나도록 되어 있습니다. XML과 코드를 직접 쓰는 사람도 같은 도움을 받습니다.

**Auto / Manual 작업 모델**
- 읽을 것을 한 번 등록하면(Auto 목록) 라이브러리가 계속 폴링하고, 타임아웃과 재접속도 맡습니다. 쓰기는 한 번 실행되는 Manual 작업으로 큐에 들어가 다음 Auto 작업보다 먼저 실행됩니다. 결과는 작업에 준 메시지 ID를 담은 이벤트로 옵니다.
- 메모리맵에서 읽기 영역은 Auto 목록이 되고, 쓰기 영역은 이름 있는 쓰기가 됩니다.

![Auto / Manual 작업 모델](https://raw.githubusercontent.com/devinno-kr/Devinno.Communication/main/assets/auto-manual.ko.png)

**메모리맵에서 XML로**
- AI가 메모리맵을 보고 장비마다 XML 파일 하나를 씁니다: 연결 설정, Auto(폴링) 목록, 이름 있는 쓰기.
- 불러오기는 엄격합니다. 모르는 속성·요소는 불러오기 오류이고, `FormatException`이 요소와 속성 이름을 알려 줍니다.
- 루트 요소마다 XSD가 패키지에 들어 있습니다(`docs/schemas/`). 커밋된 스키마가 코드와 같은지는 테스트가 확인합니다.

**이름 있는 쓰기**
- 쓰기를 XML에 `<Write Name="...">`로 한 번 정의하고 코드에서 `NamedWrite("이름", 값)`으로 호출합니다. 이름은 메모리맵에서 오고, 주소는 XML에만 있습니다.

**코드와 맞는 문서**
- `llms.txt`, `llm-rules.md`, `comm-xml-reference.md`가 패키지에 들어 있습니다. 빌드하면 `.devinno/Devinno.Communication/`에 복사되어 AI 도구가 찾을 수 있습니다. 시작점은 `llms.txt`입니다.
- C# API는 영어 IntelliSense(모든 공개 API의 XML 문서 주석)와 한국어(ko) 현지화를 제공합니다.

**AI 에이전트로 평가**
- 새 AI 에이전트에게 패키지 문서만 주고 제조사 메모리맵 3개(Mitsubishi 3E, Modbus TCP, Siemens S7)를 XML과 C# 코드로 바꾸게 했고, 자동 채점기를 모두 통과했습니다(전 항목 통과, 100%. 맵마다 55~58개 항목). 모델 크기를 달리해(Claude Sonnet, 그다음 Haiku) 2회 했고, 두 회차 사이에 에이전트가 보고한 문서의 빈틈을 고쳤습니다.
- 평가 규모는 작습니다: 맵마다 회차마다 에이전트 1개, 실제 장비 없음, 이 3개 맵뿐입니다. LS, Omron, 시리얼, TextComm, MQTT, SECS/GEM은 평가하지 않았습니다. 방법·점수·한계는 저장소의 `tests/ai-eval/RESULTS.md`에 있습니다.

**실수가 드러남**
- 연결이 없을 때 한 쓰기는 버려지고 `WriteDiscarded`로 알립니다. 실패 이벤트에는 `Attempt`, `IsFinal`, `RequestSent`가 있습니다. Modbus FC6 / FC16 쓰기는 범위 밖 값이면 `ArgumentOutOfRangeException`을 던집니다. [쓰기 안전](#쓰기-안전) 참고.

**SECS/GEM**
- 세 층: 전송(`SecsHsms` / `SecsSerial`), 상태 없는 메시징(`GemMessages`), GEM 모듈(`GemEquipment` / `GemHost`). 앱이 받는 이벤트 순서는 계약으로 문서화되어 있고, 스풀링은 선택(기본 꺼짐)입니다.

## 설치
<!-- en-hash: bd0a22744e882a8d -->

```bash
dotnet add package Devinno.Communication
```

## 시작하기
<!-- en-hash: 6690dbcd5b6f27b1 -->

Modbus TCP 장비에서 홀딩 레지스터 0~9를 계속 읽고, 100번지에 한 번 값을 씁니다.

```csharp
using Devinno.Communication.Modbus.TCP;

var master = new ModbusTCPMaster { RemoteIP = "192.168.0.10", RemotePort = 502 };

// Responses arrive as events (raised on the worker thread)
master.WordReadReceived += (s, e) =>
{
    if (e.MessageID == 1)
        Console.WriteLine($"Holding registers 0-9 = {string.Join(", ", e.ReceiveData)}");
};
master.TimeoutReceived += (s, e) => Console.WriteLine($"Timeout: {e.MessageID}");

// Auto: registered once, polled repeatedly (message ID 1, slave 1, 10 words from address 0)
master.AutoWordRead_FC3(id: 1, slave: 1, startAddr: 0, length: 10);

// Manual: runs once, and is processed before the Auto list.
// Write once connected: a write made while not connected is discarded (WriteDiscarded).
master.DeviceOpened += (s, e) => master.ManualWordWrite_FC6(id: 2, slave: 1, startAddr: 100, value: 1234);   // runs on every (re)connect

master.Start();

Console.ReadLine();
master.Dispose();
```

다른 Master 클래스도 같은 흐름입니다: 생성 → 이벤트 연결 → `Auto…`/`Manual…` 등록 → `Start()`.

## 특징
<!-- en-hash: 6842e9859b8dbdc5 -->
  * 프로토콜 9종, Master/Slave · Host/Equipment 역할 지원
  * **Auto**(반복 폴링) + **Manual**(1회 실행) 작업 큐, 연결이 끊기면 자동 재연결
  * 통신 설정과 스케줄을 XML로 저장·불러오기, XSD 스키마 생성
  * 이름 있는 쓰기(named write): XML에 쓰기를 이름과 함께 한 번 정의해 두고, 코드에서 그 이름으로 호출
  * WinForms, WPF, Skia+OpenTK, ASP.NET Core 서비스에서 사용 가능
  * 영어 IntelliSense(모든 공개 API의 XML 문서 주석), 한국어(ko) 현지화 제공

## 지원 프로토콜
<!-- en-hash: 0c7bd297040f9c8a -->

| 분류 | 클래스 | 전송 | 역할 | 문서 |
|---|---|---|---|---|
| Modbus | `ModbusRTUMaster` / `ModbusRTUSlave` | Serial | Master / Slave | [문서](guide/protocols/modbus.md) |
| Modbus | `ModbusTCPMaster` / `ModbusTCPSlave` | TCP | Master / Slave | [문서](guide/protocols/modbus.md) |
| LS산전 PLC | `LsCNet` (XGT, RSS/RSB/WSS/WSB) | Serial | Master | |
| LS산전 PLC | `LsFEnet` (XGT, TCP 2004) | TCP | Master | |
| Mitsubishi PLC | `McSerial` (MC 1C 형식 1·4, BR/WR/BW/WW) | Serial | Master | |
| Mitsubishi PLC | `McEthernet` (MC 3E/1E 바이너리, BR/WR/BW/WW) | TCP | Master | |
| Siemens S7 | `S7Ethernet` (S7comm, ISO-on-TCP 102) | TCP | Master | [문서](guide/protocols/siemens-s7.md) |
| Omron PLC | `FinsUdp` / `FinsTcp` (FINS 9600) | UDP / TCP | Master | [문서](guide/protocols/omron-fins.md) |
| Omron PLC | `FinsSerial` (Host Link FINS) | Serial | Master | [문서](guide/protocols/omron-fins.md) |
| TextComm | `TextCommRTUMaster` / `TextCommRTUSlave` | Serial | Master / Slave | [문서](guide/protocols/textcomm.md) |
| TextComm | `TextCommTCPMaster` / `TextCommTCPSlave` | TCP | Master / Slave | [문서](guide/protocols/textcomm.md) |
| MQTT | `MQClient` (MQTTnet 래퍼) | TCP | Client | [문서](guide/protocols/mqtt.md) |
| SECS/GEM | `SecsHsms` (HSMS-SS, SEMI E37) | TCP | Host / Equipment | [문서](guide/protocols/secs-gem.md) |
| SECS/GEM | `SecsSerial` (SECS-I, SEMI E4) | Serial | Host / Equipment | [문서](guide/protocols/secs-gem.md) |
| SECS/GEM | `GemMessages` (상태 없는 GEM 메시지 만들기/해석) | — | Host / Equipment | [문서](guide/protocols/secs-gem.md#메시징-층-gemmessages) |
| SECS/GEM | `GemEquipment` (SEMI E30 장비 쪽) | HSMS / SECS-I | Equipment | [문서](guide/protocols/secs-gem.md#gem-장비-gemequipment) |
| SECS/GEM | `GemHost` (GEM Host 쪽) | HSMS / SECS-I | Host | [문서](guide/protocols/secs-gem.md#gem-host-gemhost) |

SECS/GEM은 세 층입니다: 전송(`SecsHsms`/`SecsSerial`), 상태 없는 메시징(`GemMessages`), GEM 모듈(`GemEquipment`/`GemHost`).

* Modbus Function : FC1, FC2, FC3, FC4, FC5, FC6, FC15, FC16, FC26
* **S7-1200/1500은 PLC에서 PUT/GET 허용과 "최적화된 블록 액세스" 해제가 필요**

## 핵심 개념
<!-- en-hash: d948057c237a352a -->

  * **스케줄** : Auto는 등록해 두면 반복 폴링, Manual은 한 번만 실행되고 Auto보다 먼저 처리. 응답·오류(타임아웃, 체크섬, NAK)는 이벤트로 오고 등록할 때 준 MessageID로 구분
  * **스레드** : 이벤트는 통신 스레드에서 발생하므로 핸들러는 짧게 유지하고 UI 갱신은 사용하는 쪽에서 마샬링 (WinForms `BeginInvoke`, WPF `Dispatcher.BeginInvoke` 등)
  * **연결 이벤트** : Master 클래스는 `DeviceOpened` / `DeviceClosed`를 발생시킴 (`MQClient`는 `Connected` / `Disconnected`). 재접속은 끄지 않는 한 자동
  * **XML 설정** : 클래스마다 `SaveXml`/`LoadXml`과 설정 객체(`…Config`, 예: `ModbusTCPMasterConfig`)가 있음. `ConfigSchema.Get<T>()`로 XSD 생성. 인스턴스 하나 = XML 파일 하나, 루트 요소 이름은 클래스 이름. 엄격 모드라 모르는 속성·요소는 `FormatException`. 불러오기는 정지 상태에서만 가능
  * **네트워크 보안** : Modbus, S7, FINS, MC, HSMS 같은 프로토콜에는 인증·암호화가 없으니 격리된 신뢰 네트워크에서만 사용

### 쓰기 안전
<!-- en-hash: 44e23c1369c1c357 -->

  * 기본으로 연결이 없을 때 한 쓰기는 보내지 않고 `WriteDiscarded` 이벤트로 알립니다(`NotConnected`). 연결이 끊길 때 대기 중이던 쓰기도 버립니다(`Disconnected`).
  * `Stop()`은 대기 중인 Manual 쓰기를 항상 비웁니다(`WriteDiscarded`, `Stopped`).
  * `DiscardWritesWhileDisconnected="false"`(속성 또는 XML 속성)로 예전 동작(연결될 때까지 쓰기를 대기열에 유지)으로 되돌릴 수 있습니다.
  * 실패 이벤트(`TimeoutReceived`와 체크섬 오류 이벤트)에는 `Attempt`, `IsFinal`, `RequestSent`가 있습니다. `RequestSent`가 true이면 장비가 요청을 이미 실행했을 수 있으니 다시 쓰기 전에 읽어서 확인하세요.
  * 쓰기 하나는 성공 이벤트, NAK 또는 예외 이벤트, `IsFinal`이 true인 실패 이벤트, `WriteDiscarded` 중 정확히 하나로 끝납니다.
  * 펄스, 카운터, 증가형 쓰기에는 `RepeatCount`를 쓰지 마세요. 재시도는 장비가 이미 실행했을 수 있는 쓰기를 다시 보냅니다.
  * Modbus FC6 / FC16 Manual 쓰기는 -32768..65535 밖의 값이면 `ArgumentOutOfRangeException`을 던집니다.
  * S7과 FINS의 블록 쓰기는 원자적이지 않습니다. 중간에 실패하면 `WrittenBytes`(S7) / `WrittenCount`(FINS)로 이미 쓴 양을 알 수 있습니다.
  * FX5를 3E로 쓸 때 X/Y 번호는 8진수입니다: `XYNotation="Octal"`로 지정하세요. `Auto`에서는 16진수로 읽어 엉뚱한 점을 가리킵니다.
  * TextComm Master는 `ManualSend`를 전부 쓰기로 취급합니다(라이브러리가 명령의 의미를 모르므로). 그래서 연결이 없을 때는 `ManualSend`도 버립니다.
  * 쓰기는 `Start()` 직후가 아니라 연결된 뒤(`DeviceOpened` 안)에 하세요.

### XML 설정과 이름 있는 쓰기
<!-- en-hash: 19e0bbc3628a234f -->

XML에는 연결 설정, Auto(폴링) 목록, 이름 있는 쓰기 목록이 들어갑니다. 값은 XML에 저장하지 않습니다.

```xml
<ModbusTCPMaster RemoteIP="192.168.0.10" RemotePort="502" Timeout="300">
  <Auto Id="1" Func="FC3" Slave="1" Start="0" Length="10" />
  <Write Name="SpeedSet" Id="100" Func="FC6" Slave="1" Start="100" />
  <Write Name="Recipe" Id="101" Func="FC16" Slave="1" Start="300" />
</ModbusTCPMaster>
```

```csharp
using Devinno.Communication;
using Devinno.Communication.Modbus.TCP;

var master = new ModbusTCPMaster();
master.LoadXml("line1.xml");      // only while stopped; the Auto list is registered from the XML
master.Start();

// Named write: queues a Manual job for the <Write> with that name
master.NamedWrite("SpeedSet", 1234);
master.NamedWrite("Recipe", [10, 20, 30]);   // a list of long values (FC16 block write)

// XSD for the configuration object
string xsd = ConfigSchema.Get<ModbusTCPMasterConfig>();
```

이름 있는 쓰기는 PLC Master 10종에서 쓸 수 있습니다: `ModbusRTUMaster`, `ModbusTCPMaster`, `LsCNet`, `LsFEnet`, `McSerial`, `McEthernet`, `S7Ethernet`, `FinsUdp`, `FinsTcp`, `FinsSerial`. 결과는 `<Write>`의 `Id`를 MessageID로 가진 이벤트로 옵니다.

자세한 내용 → [공통 개념](guide/concepts.md)

## 제약 사항 / 검증 상태
<!-- en-hash: 27265551e8fb6bb7 -->

  * Siemens S7 : 절대 주소만 지원. 심볼·최적화 DB·타이머/카운터 미지원
  * Omron FINS : C-mode Host Link, CIP 태그, 타이머/카운터 미지원
  * GEM 장비 : 트레이스·리밋·레시피(S7) 아직 없음. 스풀링(SEMI E30 7.12)은 있고 기본은 꺼짐(`GemEquipment.SpoolDirectory`)
  * GEM 장비·Host : E30 원문 대조는 하지 못했고 secsgem과의 상호 운용만 확인함. 실제 MES·장비와는 확인하지 못함

## 요구 사항
<!-- en-hash: 9260074370befb5b -->

  * .NET 8.0
  * 의존성 : [System.IO.Ports](https://www.nuget.org/packages/System.IO.Ports), [MQTTnet](https://www.nuget.org/packages/MQTTnet/5.2.0.1603) 5.2.0.1603

## 문서
<!-- en-hash: 2148371624677d0a -->

패키지의 `docs` 폴더에 다음 문서가 들어 있습니다.

  * 패키지의 `docs/llms.txt` (빌드 후: `.devinno/Devinno.Communication/llms.txt`): 문서 안내, 가장 먼저 읽기
  * 패키지의 `docs/llm-rules.md` (빌드 후: `.devinno/Devinno.Communication/llm-rules.md`): 규칙, 클래스 선택, 메모리맵→XML 절차
  * 패키지의 `docs/comm-xml-reference.md` (빌드 후: `.devinno/Devinno.Communication/comm-xml-reference.md`): 모든 XML 루트 요소·속성·기본값·범위
  * 패키지의 `docs/schemas/*.xsd` (빌드 후: `.devinno/Devinno.Communication/schemas/*.xsd`): 루트 요소별 정확한 스키마
  * C# API: 모든 공개 API의 XML 문서 주석 (IntelliSense, 패키지의 `Devinno.Communication.xml`)

## AI 도구용 문서
<!-- en-hash: 1f83e178946120ee -->

SDK 스타일 프로젝트에서 `dotnet build`를 한 번 하면 패키지가 문서를 `.devinno/Devinno.Communication/`에 복사합니다(restore만으로는 복사되지 않음). AI 지침 파일(`CLAUDE.md`, `AGENTS.md` 등)에 다음 줄을 추가하세요.

```
Devinno library docs: read .devinno/*/llms.txt first.
```

  * 복사를 끄려면 프로젝트 파일에 `<DevinnoCopyDocs>false</DevinnoCopyDocs>`를 넣습니다.
  * `.devinno/`를 `.gitignore`에 추가하는 것을 권장합니다.
  * 대체 위치: `%UserProfile%\.nuget\packages\devinno.communication\<버전>\docs\`.

## 저장소 (저장소 전용)
<!-- en-hash: 772361c92c2f74d8 -->

GitHub 저장소에는 있지만 패키지에는 없는 파일을 설명합니다.

  * [`guide/`](guide/concepts.md): 프로토콜별 한국어 가이드와 공통 개념 가이드. [Modbus](guide/protocols/modbus.md), [Siemens S7](guide/protocols/siemens-s7.md), [Omron FINS](guide/protocols/omron-fins.md), [TextComm](guide/protocols/textcomm.md), [MQTT](guide/protocols/mqtt.md), [SECS/GEM](guide/protocols/secs-gem.md)
  * `tests/Devinno.Communication.Tests`: xUnit 테스트. `dotnet test tests/Devinno.Communication.Tests`로 실행합니다.
  * `tests/ai-eval`: AI 평가 키트(메모리맵, 채점 데이터, Grader 콘솔 프로젝트, 프롬프트)와 `RESULTS.md`.
  * `schemas/`: 패키지에 들어가는 XSD 파일. 테스트가 코드와 같은지 확인합니다.
  * `i18n/ko`: 한국어 IntelliSense 번역. `tools/XmlDocI18n`이 관리하고 `README.ko.md`가 이 README를 따르는지 확인합니다.
  * CI: `ci.yml`(빌드·테스트·pack, Actions 탭에서 수동 실행)과 `publish-nuget.yml`(`v*.*.*` 태그를 push하면 nuget.org에 게시).

## 면책
<!-- en-hash: 3cd0bc908c5ecf6f -->

Devinno.Communication은 **"있는 그대로" 제공되며 어떠한 보증도 하지 않습니다.** 사용으로 생긴 손해에 대해 작성자는 책임지지 않습니다(LICENSE 참고).

- 이 라이브러리는 안전 기능용으로 설계되거나 인증되지 않았습니다. 비상 정지, 인터록 같은 안전 기능은 안전 등급 장비와 PLC 프로그램에서 구현하세요.
- 각 프로토콜은 공개 자료를 근거로 구현해 가상 장치로 시험했으며, 모든 기종과 펌웨어에서 검증되지는 않았습니다.
- PLC에 쓰면 실제 기계가 움직입니다. 실제 장비에 연결하기 전에 시뮬레이터나 시험 장비로 확인하고, 모든 쓰기의 결과를 확인하세요("쓰기 안전" 참고).
- 이 라이브러리로 만든 시스템의 시험, 검증, 안전한 운영은 사용자의 책임입니다.

제품 이름은 각 소유자의 상표이며, 이 프로젝트는 그 회사들과 관계가 없습니다.

## 라이선스
<!-- en-hash: b46496942cf1e813 -->

MIT. 저장소 루트의 `LICENSE` 파일 참고.
