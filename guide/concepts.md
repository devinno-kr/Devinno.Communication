# 공통 개념

## Master 스케줄
  * **Auto** : 등록해 두면 반복해서 폴링
  * **Manual** : 한 번만 실행되고 Auto보다 먼저 처리됨. 타임아웃 시 재시도 횟수 지정 가능
  * 응답과 오류(타임아웃, CRC/BCC/섬체크/합계, NAK)는 이벤트로 전달되며, 등록할 때 준 MessageID로 요청을 구분
  * 작업마다 타임아웃 지정 가능, 연결이 끊기면 자동 재연결(옵션)

## 실행 모델
  * 통신 모듈마다 스케줄러(Auto/Manual 작업 큐)를 직접 가진 자기완결형 구조. 공통 베이스 클래스가 없음
  * Master와 RTU Slave는 장치마다 전용 백그라운드 스레드에서 실행 → ASP.NET Core에서 스레드풀을 점유하지 않음
  * TCP Slave는 async(`AcceptAsync` / `ReceiveAsync`)로 클라이언트를 처리
  * 이벤트는 통신 스레드에서 발생하므로 UI 갱신은 사용하는 쪽에서 마샬링해야 함 (WinForms `BeginInvoke`, WPF `Dispatcher` 등)
  * 작업 등록과 처리는 스레드 안전
  * MQTT·SECS/GEM의 스레드 구성은 각 프로토콜 문서 참고 ([MQTT](protocols/mqtt.md#실행-모델), [SECS/GEM](protocols/secs-gem.md#실행-모델))

## XML 설정
  * 통신 설정(포트, 보레이트, IP, 타임아웃, 주기 등)과 Auto 스케줄을 XML로 저장하고 불러올 수 있음
  * 인스턴스 하나에 XML 하나. 클래스마다 설정 모델(`…Config`)이 있어 코드로도 설정 가능
  * `ConfigSchema.Get<T>()`로 XSD 스키마 생성 → AI나 편집기에 형식 제공
  * 엄격 모드: 모르는 속성·요소, 잘못된 값은 요소와 속성 이름을 담은 오류로 알림 (적용은 전부 아니면 무)
  * 비밀번호는 저장하지 않음

## 이름 있는 쓰기
  * PLC Master 10종(Modbus RTU/TCP, LsCNet·LsFEnet, McSerial·McEthernet, S7Ethernet, FinsUdp·FinsTcp·FinsSerial)은 XML `<Write>`로 쓰기 영역에 이름을 붙이고 `NamedWrite("이름", 값)`으로 쓸 수 있음. 메모리맵이 바뀌면 XML만 고치면 됨
  * `<Write>`는 Auto 요소 뒤에 작성하며 `Name`(1~64자, 중복 불가)·`Id`·주소(`Device`/`Address`, Modbus는 `Func`·`Slave`(`Start`는 선택, 기본 0), LsCNet·McSerial은 `Slave`도)는 필수, `Timeout`·`RepeatCount`는 선택. 값은 XML에 넣지 않음
  * `NamedWrite(name, 값)` / `NamedWrite(name, 값 목록)`은 이름에 해당하는 Manual 쓰기를 등록할 뿐이라 큐·우선순위·이벤트·재시도는 Manual과 같음. 응답 이벤트의 MessageID는 XML의 `Id`. 인자로 준 `repeatCount`/`timeout`이 XML 값보다 우선. `RepeatCount`를 안 쓰면 재시도는 없음. 재시도는 장비가 이미 실행했을 수 있는 쓰기를 다시 보냅니다. 펄스·카운터·증가형 쓰기에는 쓰지 마세요.
  * 없는 이름은 `ArgumentException`, `ContainsNamedWrite(name)`으로 등록 여부 확인
  * 클래스별 값 한 개/여러 개 동작은 [Modbus](protocols/modbus.md#이름-있는-쓰기), [S7](protocols/siemens-s7.md#이름-있는-쓰기), [FINS](protocols/omron-fins.md#이름-있는-쓰기) 참고. LS는 한 개 WSS·여러 개 WSB, 미쓰비시는 비트 디바이스 BW·워드 디바이스 WW

```xml
<LsFEnet RemoteIP="192.168.0.20">
  <Write Name="속도" Id="20" Device="%MW300" />
</LsFEnet>
<McEthernet RemoteIP="192.168.0.30" RemotePort="5000" Frame="E3">
  <Write Name="운전지령" Id="21" Device="D200" />
</McEthernet>
```
