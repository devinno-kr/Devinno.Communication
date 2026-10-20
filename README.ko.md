<!-- en-hash: e3b0c44298fc1c14 -->
English: README.md

# Devinno.Communication
<!-- en-hash: a95e0eb9ef160785 -->

산업용 PLC·장비 통신 프로토콜을 하나로 모은 .NET 8 라이브러리
(Modbus · LS산전 · Mitsubishi · Siemens · Omron · MQTT · SECS/GEM)

> ⚠️ **개발 중 (0.x)** — 버전이 올라갈 때 API가 바뀔 수 있음

## 설치
<!-- en-hash: bd0a22744e882a8d -->

```bash
dotnet add package Devinno.Communication
```

## 시작하기
<!-- en-hash: 93cd32c2898401a7 -->

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

master.Start();

// Manual: runs once, and is processed before the Auto list
master.ManualWordWrite_FC6(id: 2, slave: 1, startAddr: 100, value: 1234);

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
<!-- en-hash: bdcb2a135a74b6c0 -->

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
| SECS/GEM | `GemEquipment` (SEMI E30 장비 쪽) | HSMS / SECS-I | Equipment | [문서](guide/protocols/secs-gem.md#gem-장비-gemequipment) |
| SECS/GEM | `GemHost` (GEM Host 쪽) | HSMS / SECS-I | Host | [문서](guide/protocols/secs-gem.md#gem-host-gemhost) |

* Modbus Function : FC1, FC2, FC3, FC4, FC5, FC6, FC15, FC16, FC26
* **S7-1200/1500은 PLC에서 PUT/GET 허용과 "최적화된 블록 액세스" 해제가 필요**

## 핵심 개념
<!-- en-hash: d41c70c4e3581f46 -->

  * **스케줄** : Auto는 등록해 두면 반복 폴링, Manual은 한 번만 실행되고 Auto보다 먼저 처리. 응답·오류(타임아웃, 체크섬, NAK)는 이벤트로 오고 등록할 때 준 MessageID로 구분
  * **스레드** : 이벤트는 통신 스레드에서 발생하므로 핸들러는 짧게 유지하고 UI 갱신은 사용하는 쪽에서 마샬링 (WinForms `BeginInvoke`, WPF `Dispatcher.BeginInvoke` 등)
  * **연결 이벤트** : Master 클래스는 `DeviceOpened` / `DeviceClosed`를 발생시킴 (`MQClient`는 `Connected` / `Disconnected`). 재접속은 끄지 않는 한 자동
  * **XML 설정** : 클래스마다 `SaveXml`/`LoadXml`과 설정 객체(`…Config`, 예: `ModbusTCPMasterConfig`)가 있음. `ConfigSchema.Get<T>()`로 XSD 생성. 인스턴스 하나 = XML 파일 하나, 루트 요소 이름은 클래스 이름. 엄격 모드라 모르는 속성·요소는 `FormatException`. 불러오기는 정지 상태에서만 가능

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
<!-- en-hash: d7350b4b2a9c5588 -->

  * Siemens S7 : 절대 주소만 지원. 심볼·최적화 DB·타이머/카운터 미지원
  * Omron FINS : C-mode Host Link, CIP 태그, 타이머/카운터 미지원
  * GEM 장비 : 스풀링·트레이스·리밋·레시피(S7) 아직 없음
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

프로토콜별 상세 문서는 저장소의 [`guide/protocols/`](guide/protocols/modbus.md)에 있습니다.

## AI 도구용 문서
<!-- en-hash: 1f83e178946120ee -->

SDK 스타일 프로젝트에서 `dotnet build`를 한 번 하면 패키지가 문서를 `.devinno/Devinno.Communication/`에 복사합니다(restore만으로는 복사되지 않음). AI 지침 파일(`CLAUDE.md`, `AGENTS.md` 등)에 다음 줄을 추가하세요.

```
Devinno library docs: read .devinno/*/llms.txt first.
```

  * 복사를 끄려면 프로젝트 파일에 `<DevinnoCopyDocs>false</DevinnoCopyDocs>`를 넣습니다.
  * `.devinno/`를 `.gitignore`에 추가하는 것을 권장합니다.
  * 대체 위치: `%UserProfile%\.nuget\packages\devinno.communication\<버전>\docs\`.

## 라이선스
<!-- en-hash: b46496942cf1e813 -->

MIT. 저장소 루트의 `LICENSE` 파일 참고.

