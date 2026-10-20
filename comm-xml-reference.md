# Devinno.Communication XML Reference

**If this document and `schemas/*.xsd` disagree, the XSD is right** for element and attribute names, structure and types. (The XSDs are generated from the code by `ConfigSchema.Get<T>()`.) The loader is **stricter** than the XSD: value ranges are not in the XSD, and some enum values that the XSD lists are rejected on load. A file must pass both the XSD and `FromXml`/`LoadXml`.

| XSD allows, loader rejects | Where |
|---|---|
| `Func` = `FC5`, `FC6`, `FC15`, `FC16`, `FC26` | Modbus `<Auto>` (reads: FC1–FC4 only) |
| `Func` = `FC1`–`FC4`, `FC26` | Modbus `<Write>` (FC5, FC6, FC15, FC16 only) |
| `StopBits="None"` | every serial class |
| `Qos="GrantedFailure"` | MQClient `<Subscribe>` |
| `InitialControlState="AttemptOnline"`; `OnlineFailedState` other than `EquipmentOffline`/`HostOffline` | GemEquipment |
| transport child `Role` other than `Equipment` (GemEquipment) / `Host` (GemHost) | GemEquipment, GemHost |
| any number outside the ranges below; device/address text the protocol parser rejects; counts above the per-request limits | all |

Read [`llm-rules.md`](llm-rules.md) first for the procedure, per-request limits and C# usage. This file lists every root element (21), every attribute and child element.

Contents: [1 ModbusRTUMaster](#1-modbusrtumaster) · [2 ModbusTCPMaster](#2-modbustcpmaster) · [3 ModbusRTUSlave](#3-modbusrtuslave) · [4 ModbusTCPSlave](#4-modbustcpslave) · [5 LsCNet](#5-lscnet) · [6 LsFEnet](#6-lsfenet) · [7 McSerial](#7-mcserial) · [8 McEthernet](#8-mcethernet) · [9 S7Ethernet](#9-s7ethernet) · [10 FinsUdp](#10-finsudp) · [11 FinsTcp](#11-finstcp) · [12 FinsSerial](#12-finsserial) · [13 TextCommRTUMaster](#13-textcommrtumaster) · [14 TextCommTCPMaster](#14-textcommtcpmaster) · [15 TextCommRTUSlave](#15-textcommrtuslave) · [16 TextCommTCPSlave](#16-textcommtcpslave) · [17 MQClient](#17-mqclient) · [18 SecsHsms](#18-secshsms) · [19 SecsSerial](#19-secsserial) · [20 GemEquipment](#20-gemequipment) · [21 GemHost](#21-gemhost)

---

## 0. Common rules

| Topic | Rule |
|---|---|
| Root element | Name = class name. One root per file. No namespace. |
| Strict mode | Unknown attribute or element → `FormatException` on load. |
| Omitted attribute | Takes the default in the tables below. "—" = no default (required, or "not specified" = falls back as described). |
| `int` | Decimal integer (`-5`, `100`). No hex, no separators. |
| `bool` | `true` or `false` (lowercase). |
| Enum | Exact, case-sensitive name from the "Range / values" column. No numbers. |
| `string` | Any text; XML-escape `&`, `<`, `"`. |
| Child order | As listed under "Children" for each root (XSD `xs:sequence`). Auto (read) elements of different kinds may be mixed with each other, but all of them come before the `<Write>` elements. |
| Load | `LoadXml(path)` / `FromXml(XElement)` validate everything first; on any error nothing changes. Only allowed while stopped. |

Serial port attributes (used by every serial class; defaults differ per class and are repeated in each table):

| Attribute | Type | Range / values |
|---|---|---|
| `Port` | string | Port name, not empty (`COM3`, `/dev/ttyUSB0`) |
| `Baudrate` | int | 1 or more |
| `Parity` | enum | `None`, `Odd`, `Even`, `Mark`, `Space` |
| `DataBits` | int | 5 – 8 |
| `StopBits` | enum | `One`, `OnePointFive`, `Two` (`None` is in the XSD but rejected on load) |

Item `Timeout` (on Auto elements and `<Write>`): 1 – 60000 ms; if omitted, the class `Timeout` is used.
`<Write>` `RepeatCount`: 0 – 100 retries; if omitted, the `NamedWrite` argument decides, else no retries.
`<Write>` `Name`: 1 – 64 characters, no leading/trailing whitespace, unique (case-sensitive) in the file. `Id`: any int, returned as `MessageID`; uniqueness is not checked.

---

## 1. ModbusRTUMaster

Class `Devinno.Communication.Modbus.RTU.ModbusRTUMaster`, config `ModbusRTUMasterConfig`. Children: `<Auto>`*, then `<Write>`*.

### `<ModbusRTUMaster>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Port` | string | no | `COM1` | not empty |
| `Baudrate` | int | no | `115200` | ≥ 1 |
| `Parity` | enum | no | `None` | `None`, `Odd`, `Even`, `Mark`, `Space` |
| `DataBits` | int | no | `8` | 5 – 8 |
| `StopBits` | enum | no | `One` | `One`, `OnePointFive`, `Two` |
| `Timeout` | int | no | `100` | 1 – 60000 ms |
| `Interval` | int | no | `10` | 0 – 60000 ms (wait between jobs) |
| `AutoReconnect` | bool | no | `false` | reopen the port when it is lost |
| `DiscardWritesWhileDisconnected` | bool | no | `true` | discard writes while not connected (and pending writes when the connection is lost); `WriteDiscarded` reports each |

### `<Auto>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Id` | int | yes | — | message ID |
| `Func` | enum | yes | — | `FC1`, `FC2`, `FC3`, `FC4` only (the XSD also lists FC5, FC6, FC15, FC16, FC26; they are rejected here) |
| `Slave` | int | yes | — | 1 – 247 |
| `Start` | int | no | `0` | 0 – 65535, 0-based |
| `Length` | int | yes | — | FC1/FC2: 1 – 2000; FC3/FC4: 1 – 125 |
| `Timeout` | int | no | — | 1 – 60000 ms |

### `<Write>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Name` | string | yes | — | 1 – 64 chars, no leading/trailing whitespace, unique in the file (case-sensitive); `NamedWrite` uses it |
| `Id` | int | yes | — | message ID of the write result |
| `Func` | enum | yes | — | `FC5`, `FC6`, `FC15`, `FC16` only |
| `Slave` | int | yes | — | 0 – 247 (0 = broadcast, no reply) |
| `Start` | int | no | `0` | 0 – 65535, 0-based |
| `Timeout` | int | no | — | 1 – 60000 ms |
| `RepeatCount` | int | no | — | 0 – 100 |

`Func` values (XML name ↔ C# enum member):

| XML (`Func`) | C# enum member (`ModbusFunction`, namespace `Devinno.Communication.Modbus`) | Meaning | Allowed in |
|---|---|---|---|
| `FC1` | `BITREAD_F1` | read coils | `<Auto>` |
| `FC2` | `BITREAD_F2` | read discrete inputs | `<Auto>` |
| `FC3` | `WORDREAD_F3` | read holding registers | `<Auto>` |
| `FC4` | `WORDREAD_F4` | read input registers | `<Auto>` |
| `FC5` | `BITWRITE_F5` | write single coil | `<Write>` |
| `FC6` | `WORDWRITE_F6` | write single register | `<Write>` |
| `FC15` | `MULTIBITWRITE_F15` | write multiple coils | `<Write>` |
| `FC16` | `MULTIWORDWRITE_F16` | write multiple registers | `<Write>` |
| `FC26` | `WORDBITSET_F26` | set a bit in a register (Devinno extension) | code only (`ManualWordBitSet_FC26`) |

In XML always write `FC3`; in C# (for example `e.Function == ModbusFunction.WORDREAD_F3`) use the member name.

```xml
<ModbusRTUMaster Port="COM3" Baudrate="9600" Parity="Even" Timeout="200">
  <Auto Id="1" Func="FC3" Slave="1" Start="0" Length="20" />
  <Auto Id="2" Func="FC2" Slave="1" Start="0" Length="16" />
  <Write Name="SpeedSet" Id="100" Func="FC6" Slave="1" Start="100" />
  <Write Name="Recipe" Id="101" Func="FC16" Slave="1" Start="200" />
</ModbusRTUMaster>
```

---

## 2. ModbusTCPMaster

Class `Devinno.Communication.Modbus.TCP.ModbusTCPMaster`, config `ModbusTCPMasterConfig`. Children: `<Auto>`*, then `<Write>`*.

### `<ModbusTCPMaster>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `RemoteIP` | string | no | `127.0.0.1` | IP or host name, not empty |
| `RemotePort` | int | no | `502` | 1 – 65535 |
| `Timeout` | int | no | `100` | 1 – 60000 ms |
| `Interval` | int | no | `10` | 0 – 60000 ms |
| `AutoReconnect` | bool | no | `true` | reconnect when the connection is lost |
| `DiscardWritesWhileDisconnected` | bool | no | `true` | discard writes while not connected (and pending writes when the connection is lost); `WriteDiscarded` reports each |

### `<Auto>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Id` | int | yes | — | message ID |
| `Func` | enum | yes | — | `FC1`, `FC2`, `FC3`, `FC4` only |
| `Slave` | int | yes | — | unit ID 0 – 255 |
| `Start` | int | no | `0` | 0 – 65535, 0-based |
| `Length` | int | yes | — | FC1/FC2: 1 – 2000; FC3/FC4: 1 – 125 |
| `Timeout` | int | no | — | 1 – 60000 ms |

### `<Write>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Name` | string | yes | — | 1 – 64 chars, no leading/trailing whitespace, unique in the file (case-sensitive); `NamedWrite` uses it |
| `Id` | int | yes | — | message ID of the write result |
| `Func` | enum | yes | — | `FC5`, `FC6`, `FC15`, `FC16` only |
| `Slave` | int | yes | — | unit ID 0 – 255 |
| `Start` | int | no | `0` | 0 – 65535, 0-based |
| `Timeout` | int | no | — | 1 – 60000 ms |
| `RepeatCount` | int | no | — | 0 – 100 |

```xml
<ModbusTCPMaster RemoteIP="192.168.0.40" RemotePort="502" Timeout="300">
  <Auto Id="1" Func="FC3" Slave="1" Start="0" Length="125" />
  <Auto Id="2" Func="FC3" Slave="1" Start="125" Length="75" />
  <Auto Id="3" Func="FC1" Slave="1" Start="0" Length="32" />
  <Write Name="RunCommand" Id="100" Func="FC5" Slave="1" Start="0" />
  <Write Name="Recipe" Id="101" Func="FC16" Slave="1" Start="300" />
</ModbusTCPMaster>
```

---

## 3. ModbusRTUSlave

Class `Devinno.Communication.Modbus.RTU.ModbusRTUSlave`, config `ModbusRTUSlaveConfig`. No children. The memory arrays (`Coils`, `Contacts`, `HoldingRegisters`, `InputRegisters`) are set in code, not in XML.

### `<ModbusRTUSlave>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Port` | string | no | `COM1` | not empty |
| `Baudrate` | int | no | `115200` | ≥ 1 |
| `Parity` | enum | no | `None` | `None`, `Odd`, `Even`, `Mark`, `Space` |
| `DataBits` | int | no | `8` | 5 – 8 |
| `StopBits` | enum | no | `One` | `One`, `OnePointFive`, `Two` |
| `Slave` | int | no | `1` | 1 – 247 (this slave's address) |
| `AutoReconnect` | bool | no | `false` | reopen the port when it is lost |

```xml
<ModbusRTUSlave Port="COM4" Baudrate="9600" Slave="1" AutoReconnect="true" />
```

---

## 4. ModbusTCPSlave

Class `Devinno.Communication.Modbus.TCP.ModbusTCPSlave`, config `ModbusTCPSlaveConfig`. No children. Memory arrays are set in code.

### `<ModbusTCPSlave>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `LocalPort` | int | no | `502` | 0 – 65535 (listening port) |
| `Slave` | int | no | `1` | 1 – 247 (this slave's unit ID) |
| `MaxClients` | int | no | `10` | 1 – 1000 simultaneous connections |

```xml
<ModbusTCPSlave LocalPort="502" Slave="1" MaxClients="10" />
```

---

## 5. LsCNet

Class `Devinno.Communication.LS.CNet.LsCNet`, config `LsCNetConfig`. Children: `<RSS>`/`<RSB>`* (mixed, polled in order), then `<Write>`*.
Devices are direct variables: `%` + area + size (`X`,`B`,`W`,`D`,`L`) + address, 4 – 16 characters (`%MW100`, `%MX0`, `%DD10`).

### `<LsCNet>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Port` | string | no | `COM1` | not empty |
| `Baudrate` | int | no | `9600` | ≥ 1 |
| `Parity` | enum | no | `None` | `None`, `Odd`, `Even`, `Mark`, `Space` |
| `DataBits` | int | no | `8` | 5 – 8 |
| `StopBits` | enum | no | `One` | `One`, `OnePointFive`, `Two` |
| `Timeout` | int | no | `1000` | 1 – 60000 ms |
| `Interval` | int | no | `10` | 0 – 60000 ms |
| `AutoReconnect` | bool | no | `false` | reopen the port after a disconnection |
| `DiscardWritesWhileDisconnected` | bool | no | `true` | discard writes while not connected (and pending writes when the connection is lost); `WriteDiscarded` reports each |

### `<RSS>` (random read)

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Id` | int | yes | — | message ID |
| `Slave` | int | yes | — | station number 0 – 255 |
| `Devices` | string | yes | — | 1 – 16 devices, comma-separated, all with the same size letter (`%MW100,%MW105,%DW0`) |
| `Timeout` | int | no | — | 1 – 60000 ms |

### `<RSB>` (block read)

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Id` | int | yes | — | message ID |
| `Slave` | int | yes | — | station number 0 – 255 |
| `Device` | string | yes | — | start device, not a bit (`X`) device |
| `Count` | int | yes | — | 1 – (120 bytes / size): B 120, W 60, D 30, L 15 |
| `Timeout` | int | no | — | 1 – 60000 ms |

### `<Write>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Name` | string | yes | — | 1 – 64 chars, no leading/trailing whitespace, unique in the file (case-sensitive); `NamedWrite` uses it |
| `Id` | int | yes | — | message ID |
| `Slave` | int | yes | — | station number 0 – 255 |
| `Device` | string | yes | — | device (start device for a list write; a list write needs a non-bit device, max 120 bytes) |
| `Timeout` | int | no | — | 1 – 60000 ms |
| `RepeatCount` | int | no | — | 0 – 100 |

```xml
<LsCNet Port="COM5" Baudrate="9600" Timeout="1000">
  <RSS Id="1" Slave="1" Devices="%MW100,%MW105" />
  <RSB Id="2" Slave="1" Device="%MW200" Count="60" />
  <Write Name="Speed" Id="100" Slave="1" Device="%MW300" />
  <Write Name="Recipe" Id="101" Slave="1" Device="%MW400" />
</LsCNet>
```

---

## 6. LsFEnet

Class `Devinno.Communication.LS.FEnet.LsFEnet`, config `LsFEnetConfig`. Children: `<RSS>`/`<RSB>`*, then `<Write>`*. Same device notation as LsCNet. No station number.

### `<LsFEnet>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `RemoteIP` | string | no | `127.0.0.1` | not empty |
| `RemotePort` | int | no | `2004` | 1 – 65535 |
| `Timeout` | int | no | `1000` | 1 – 60000 ms |
| `Interval` | int | no | `10` | 0 – 60000 ms |
| `AutoReconnect` | bool | no | `true` | reconnect after a disconnection |
| `DiscardWritesWhileDisconnected` | bool | no | `true` | discard writes while not connected (and pending writes when the connection is lost); `WriteDiscarded` reports each |

### `<RSS>` (random read)

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Id` | int | yes | — | message ID |
| `Devices` | string | yes | — | 1 – 16 devices, comma-separated, same size letter |
| `Timeout` | int | no | — | 1 – 60000 ms |

### `<RSB>` (block read)

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Id` | int | yes | — | message ID |
| `Device` | string | yes | — | start device, numeric address only (no dots), not a bit (`X`) device |
| `Count` | int | yes | — | 1 – (1400 bytes / size): B 1400, W 700, D 350, L 175 |
| `Timeout` | int | no | — | 1 – 60000 ms |

### `<Write>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Name` | string | yes | — | 1 – 64 chars, no leading/trailing whitespace, unique in the file (case-sensitive); `NamedWrite` uses it |
| `Id` | int | yes | — | message ID |
| `Device` | string | yes | — | device (a list write needs a numeric, non-bit start device, max 1400 bytes) |
| `Timeout` | int | no | — | 1 – 60000 ms |
| `RepeatCount` | int | no | — | 0 – 100 |

```xml
<LsFEnet RemoteIP="192.168.0.20" RemotePort="2004">
  <RSB Id="1" Device="%MW100" Count="700" />
  <RSS Id="2" Devices="%MX0,%MX1,%MX2" />
  <Write Name="Speed" Id="100" Device="%MW300" />
</LsFEnet>
```

---

## 7. McSerial

Class `Devinno.Communication.Mitsubishi.Serial.McSerial` (MC protocol 1C frame, ACPU common commands), config `McSerialConfig`. Children: `<BR>`/`<WR>`* (mixed), then `<Write>`*.
1C device codes: `X Y M L S B F D W R TN TS TC CN CS CC`. X/Y follow `XYNotation` (`Auto` = octal); B and W are hex; others decimal.

### `<McSerial>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Port` | string | no | `COM1` | not empty |
| `Baudrate` | int | no | `9600` | ≥ 1 |
| `Parity` | enum | no | `None` | `None`, `Odd`, `Even`, `Mark`, `Space` |
| `DataBits` | int | no | `8` | 5 – 8 |
| `StopBits` | enum | no | `One` | `One`, `OnePointFive`, `Two` |
| `Format` | enum | no | `Format1` | `Format1` (no CR LF), `Format4` (CR LF appended) |
| `SumCheck` | bool | no | `true` | use the sum check |
| `MessageWait` | int | no | `0` | 0 – 15 (×10 ms) |
| `XYNotation` | enum | no | `Auto` | `Auto` (octal), `Hex`, `Octal` |
| `Timeout` | int | no | `1000` | 1 – 60000 ms |
| `Interval` | int | no | `10` | 0 – 60000 ms |
| `AutoReconnect` | bool | no | `false` | reopen the port after a disconnection |
| `DiscardWritesWhileDisconnected` | bool | no | `true` | discard writes while not connected (and pending writes when the connection is lost); `WriteDiscarded` reports each |

### `<BR>` (bit batch read)

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Id` | int | yes | — | message ID |
| `Slave` | int | yes | — | station number 0 – 31 (0 – 15 recommended for FX) |
| `Device` | string | yes | — | start bit device (`M0`, `X0`) |
| `Count` | int | yes | — | 1 – 256 points |
| `Timeout` | int | no | — | 1 – 60000 ms |

### `<WR>` (word batch read)

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Id` | int | yes | — | message ID |
| `Slave` | int | yes | — | station number 0 – 31 |
| `Device` | string | yes | — | start device (`D100`; a bit device reads 16 points per word) |
| `Count` | int | yes | — | 1 – 64 words (word device), 1 – 32 words (bit device) |
| `Timeout` | int | no | — | 1 – 60000 ms |

### `<Write>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Name` | string | yes | — | 1 – 64 chars, no leading/trailing whitespace, unique in the file (case-sensitive); `NamedWrite` uses it |
| `Id` | int | yes | — | message ID |
| `Slave` | int | yes | — | station number 0 – 31 |
| `Device` | string | yes | — | start device; bit device → BW (max 160 points), word device → WW (max 64 words) |
| `Timeout` | int | no | — | 1 – 60000 ms |
| `RepeatCount` | int | no | — | 0 – 100 |

```xml
<McSerial Port="COM3" Baudrate="9600" Parity="Even" DataBits="7" Format="Format1" SumCheck="true">
  <BR Id="1" Slave="0" Device="M0" Count="16" />
  <WR Id="2" Slave="0" Device="D100" Count="64" />
  <Write Name="Speed" Id="100" Slave="0" Device="D200" />
</McSerial>
```

---

## 8. McEthernet

Class `Devinno.Communication.Mitsubishi.Ethernet.McEthernet` (MC protocol binary 3E or 1E over TCP), config `McEthernetConfig`. Children: `<BR>`/`<WR>`* (mixed), then `<Write>`*.
3E codes: `SM SD X Y M L F V B D W TS TC TN SS SC SN CS CC CN SB SW S Z R ZR`. 1E codes: `X Y M S L F B D R W TN TS TC CN CS CC`. X/Y follow `XYNotation` (`Auto` = hex for E3, octal for E1); B, W, SB, SW, ZR are hex; others decimal.

### `<McEthernet>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `RemoteIP` | string | no | `127.0.0.1` | not empty |
| `RemotePort` | int | no | `5000` | 1 – 65535 (no fixed MC port; use the PLC's open setting) |
| `Frame` | enum | no | `E3` | `E3` (Q/L/FX5 built-in), `E1` (FX3 Ethernet) |
| `XYNotation` | enum | no | `Auto` | `Auto`, `Hex`, `Octal` (FX5 over E3 needs `Octal`) |
| `Timeout` | int | no | `1000` | 1 – 60000 ms |
| `Interval` | int | no | `10` | 0 – 60000 ms |
| `AutoReconnect` | bool | no | `true` | reconnect after a disconnection |
| `DiscardWritesWhileDisconnected` | bool | no | `true` | discard writes while not connected (and pending writes when the connection is lost); `WriteDiscarded` reports each |

### `<BR>` (bit batch read)

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Id` | int | yes | — | message ID |
| `Device` | string | yes | — | start bit device (`M0`, `X1F`) |
| `Count` | int | yes | — | E3: 1 – 3584 points; E1: 1 – 256 points |
| `Timeout` | int | no | — | 1 – 60000 ms |

### `<WR>` (word batch read)

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Id` | int | yes | — | message ID |
| `Device` | string | yes | — | start device (`D100`; a bit device reads 16 points per word) |
| `Count` | int | yes | — | E3: 1 – 960 words; E1: 1 – 64 (word device), 1 – 32 (bit device) |
| `Timeout` | int | no | — | 1 – 60000 ms |

### `<Write>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Name` | string | yes | — | 1 – 64 chars, no leading/trailing whitespace, unique in the file (case-sensitive); `NamedWrite` uses it |
| `Id` | int | yes | — | message ID |
| `Device` | string | yes | — | start device; bit device → BW (E3 3584 / E1 160 points), word device → WW (E3 960 / E1 64 words) |
| `Timeout` | int | no | — | 1 – 60000 ms |
| `RepeatCount` | int | no | — | 0 – 100 |

```xml
<McEthernet RemoteIP="192.168.0.30" RemotePort="5000" Frame="E3">
  <WR Id="1" Device="D100" Count="960" />
  <WR Id="2" Device="D1060" Count="40" />
  <BR Id="3" Device="M0" Count="64" />
  <Write Name="RunCommand" Id="100" Device="D200" />
  <Write Name="Recipe" Id="101" Device="D300" />
  <Write Name="Reset" Id="102" Device="M100" />
</McEthernet>
```

---

## 9. S7Ethernet

Class `Devinno.Communication.Siemens.S7Ethernet`, config `S7EthernetConfig`. Children: `<Read>`/`<ReadBlock>`* (mixed), then `<Write>`*.
Addresses (case-insensitive): `DB1.DBX0.1`, `DB1.DBB0`, `DB1.DBW0`, `DB1.DBD0`, `M10.0`, `MB10`, `MW10`, `MD10`, `I0.0`, `IB0`, `IW0`, `ID0`, `Q0.0`, `QB0`, `QW0`, `QD0` (`E`/`A` accepted for `I`/`Q`). DB 1 – 65535, byte 0 – 65535, bit 0 – 7. No T/C/Z.

### `<S7Ethernet>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `RemoteIP` | string | no | `127.0.0.1` | not empty |
| `RemotePort` | int | no | `102` | 1 – 65535 |
| `Rack` | int | no | `0` | 0 – 7 |
| `Slot` | int | no | `1` | 0 – 31 (1200/1500 = 1, 300 = 2, 400 = 3) |
| `ConnectionType` | enum | no | `PG` | `PG`, `OP`, `Basic` |
| `RemoteTsap` | int | no | — | 0 – 65535 decimal; not specified = ConnectionType × 256 + Rack × 32 + Slot (S7-200 SMART / LOGO!: set explicitly) |
| `LocalTsap` | int | no | `256` | 0 – 65535 decimal (256 = 0x0100) |
| `PduSize` | int | no | `960` | 240 – 960 (requested; the PLC may negotiate lower) |
| `Timeout` | int | no | `1000` | 1 – 60000 ms |
| `Interval` | int | no | `10` | 0 – 60000 ms |
| `AutoReconnect` | bool | no | `true` | reconnect after a disconnection |
| `DiscardWritesWhileDisconnected` | bool | no | `true` | discard writes while not connected (and pending writes when the connection is lost); `WriteDiscarded` reports each |

### `<Read>` (random read)

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Id` | int | yes | — | message ID |
| `Addresses` | string | yes | — | **max 19** comma-separated addresses of any size (PDU 240; 20 is rejected) |
| `Timeout` | int | no | — | 1 – 60000 ms |

### `<ReadBlock>` (block read → `BlockReceived` bytes)

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Id` | int | yes | — | message ID |
| `Address` | string | yes | — | start **byte** address (`DB1.DBB0`, `MB0`, `IB0`, `QB0`) |
| `Count` | int | yes | — | 1 – 65535 bytes (split automatically to fit the PDU) |
| `Timeout` | int | no | — | 1 – 60000 ms (per piece) |

### `<Write>`

Limits: the random write behind `NamedWrite(name, value)` sends one value (`ManualWrite` in code accepts max 12 bit/byte/word values or max 11 dword values per call, PDU 240). The block write behind `NamedWrite(name, values)` takes 1 – 65535 bytes and is split to the negotiated PDU (not atomic). A 32-bit value (`DBD`, `MD`) is **one** value, not two words.

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Name` | string | yes | — | 1 – 64 chars, no leading/trailing whitespace, unique in the file (case-sensitive); `NamedWrite` uses it |
| `Id` | int | yes | — | message ID |
| `Address` | string | yes | — | one address of any size. `NamedWrite(name, value)` = random write of one value (bit: 0/1; `DBD`/`MD` DINT or REAL: one 32-bit value, REAL via `BitConverter.SingleToInt32Bits`). `NamedWrite(name, values)` = block write from this address (B, W or D only, not a bit), 1 – 65535 bytes in total, split automatically |
| `Timeout` | int | no | — | 1 – 60000 ms |
| `RepeatCount` | int | no | — | 0 – 100 |

```xml
<S7Ethernet RemoteIP="192.168.0.10" Rack="0" Slot="1">
  <Read Id="1" Addresses="DB1.DBW0,DB1.DBD2,M10.0" />
  <ReadBlock Id="2" Address="DB1.DBB100" Count="500" Timeout="2000" />
  <Write Name="Speed" Id="100" Address="DB1.DBW10" />
  <Write Name="Recipe" Id="101" Address="DB2.DBW0" />
</S7Ethernet>
```

---

## 10. FinsUdp

Class `Devinno.Communication.Omron.Ethernet.FinsUdp`, config `FinsUdpConfig`. Children: `<Read>`/`<ReadBlock>`* (mixed), then `<Write>`*.
Addresses: `CIO100` (or `100`), `W10`, `H20`, `A100`, `D100` (`DM100`), `E0_100` (EM bank hex 0 – 18), bits `CIO100.05`, `D100.15`. Word ranges: CIO 0 – 6143, W/H 0 – 511, A 0 – 959, D/EM 0 – 32767. No T/C.

### `<FinsUdp>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `RemoteIP` | string | no | `127.0.0.1` | not empty |
| `RemotePort` | int | no | `9600` | 1 – 65535 |
| `LocalPort` | int | no | `0` | 0 – 65535 (0 = any; 9600 for static IP conversion) |
| `DestinationNode` | int | no | — | 0 – 254; not specified = last number of `RemoteIP` |
| `SourceNode` | int | no | — | 1 – 254; not specified = last number of this PC's IP toward the PLC |
| `DestinationNetwork` | int | no | `0` | 0 – 127 |
| `SourceNetwork` | int | no | `0` | 0 – 127 |
| `MaxWordsPerRequest` | int | no | `499` | 1 – 997 (block read/write chunk size) |
| `Timeout` | int | no | `1000` | 1 – 60000 ms |
| `Interval` | int | no | `10` | 0 – 60000 ms |
| `AutoReconnect` | bool | no | `true` | reopen the socket on a socket error |
| `DiscardWritesWhileDisconnected` | bool | no | `true` | discard writes while not connected (and pending writes when the connection is lost); `WriteDiscarded` reports each |

### `<Read>` (random read, 0104)

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Id` | int | yes | — | message ID |
| `Addresses` | string | yes | — | 1 – 167 comma-separated word/bit addresses |
| `Timeout` | int | no | — | 1 – 60000 ms |

### `<ReadBlock>` (block read, 0101)

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Id` | int | yes | — | message ID |
| `Address` | string | yes | — | start address; word address → words, bit address → bits |
| `Count` | int | yes | — | 1 – 65535; the last address must be inside the area; split by `MaxWordsPerRequest` |
| `Timeout` | int | no | — | 1 – 60000 ms (per chunk) |

### `<Write>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Name` | string | yes | — | 1 – 64 chars, no leading/trailing whitespace, unique in the file (case-sensitive); `NamedWrite` uses it |
| `Id` | int | yes | — | message ID |
| `Address` | string | yes | — | word address → words, bit address → bits |
| `Timeout` | int | no | — | 1 – 60000 ms |
| `RepeatCount` | int | no | — | 0 – 100 |

```xml
<FinsUdp RemoteIP="192.168.250.1" RemotePort="9600">
  <Read Id="1" Addresses="D100,CIO0.05,W10" />
  <ReadBlock Id="2" Address="D1000" Count="800" Timeout="2000" />
  <Write Name="Speed" Id="100" Address="D200" />
  <Write Name="StartBit" Id="101" Address="W0.00" />
</FinsUdp>
```

---

## 11. FinsTcp

Class `Devinno.Communication.Omron.Ethernet.FinsTcp`, config `FinsTcpConfig`. Children and address notation as FinsUdp.

### `<FinsTcp>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `RemoteIP` | string | no | `127.0.0.1` | not empty |
| `RemotePort` | int | no | `9600` | 1 – 65535 |
| `ClientNode` | int | no | — | 1 – 254; not specified = assigned by the PLC |
| `MaxWordsPerRequest` | int | no | `499` | 1 – 997 |
| `Timeout` | int | no | `1000` | 1 – 60000 ms |
| `Interval` | int | no | `10` | 0 – 60000 ms |
| `AutoReconnect` | bool | no | `true` | reconnect when the connection is lost |
| `DiscardWritesWhileDisconnected` | bool | no | `true` | discard writes while not connected (and pending writes when the connection is lost); `WriteDiscarded` reports each |

### `<Read>` (random read, 0104)

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Id` | int | yes | — | message ID |
| `Addresses` | string | yes | — | 1 – 167 comma-separated addresses |
| `Timeout` | int | no | — | 1 – 60000 ms |

### `<ReadBlock>` (block read, 0101)

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Id` | int | yes | — | message ID |
| `Address` | string | yes | — | start address |
| `Count` | int | yes | — | 1 – 65535, split by `MaxWordsPerRequest` |
| `Timeout` | int | no | — | 1 – 60000 ms |

### `<Write>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Name` | string | yes | — | 1 – 64 chars, no leading/trailing whitespace, unique in the file (case-sensitive); `NamedWrite` uses it |
| `Id` | int | yes | — | message ID |
| `Address` | string | yes | — | word or bit address |
| `Timeout` | int | no | — | 1 – 60000 ms |
| `RepeatCount` | int | no | — | 0 – 100 |

```xml
<FinsTcp RemoteIP="192.168.250.1" RemotePort="9600" MaxWordsPerRequest="250">
  <Read Id="1" Addresses="D100,W10.03" />
  <ReadBlock Id="2" Address="E0_0" Count="1000" />
  <Write Name="Speed" Id="100" Address="D200" />
</FinsTcp>
```

---

## 12. FinsSerial

Class `Devinno.Communication.Omron.Serial.FinsSerial` (Host Link FINS), config `FinsSerialConfig`. Children and address notation as FinsUdp.

### `<FinsSerial>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Port` | string | no | `COM1` | not empty |
| `Baudrate` | int | no | `9600` | ≥ 1 |
| `Parity` | enum | no | `Even` | `None`, `Odd`, `Even`, `Mark`, `Space` |
| `DataBits` | int | no | `7` | 5 – 8 |
| `StopBits` | enum | no | `Two` | `One`, `OnePointFive`, `Two` |
| `UnitNumber` | int | no | `0` | 0 – 31 (Host Link unit number) |
| `ResponseWait` | int | no | `0` | 0 – 15 (×10 ms) |
| `MaxWordsPerRequest` | int | no | `266` | 1 – 266 |
| `Timeout` | int | no | `1000` | 1 – 60000 ms |
| `Interval` | int | no | `10` | 0 – 60000 ms |
| `AutoReconnect` | bool | no | `false` | reopen the port after a disconnection |
| `DiscardWritesWhileDisconnected` | bool | no | `true` | discard writes while not connected (and pending writes when the connection is lost); `WriteDiscarded` reports each |

### `<Read>` (random read, 0104)

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Id` | int | yes | — | message ID |
| `Addresses` | string | yes | — | 1 – 134 comma-separated addresses |
| `Timeout` | int | no | — | 1 – 60000 ms |

### `<ReadBlock>` (block read, 0101)

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Id` | int | yes | — | message ID |
| `Address` | string | yes | — | start address |
| `Count` | int | yes | — | 1 – 65535, split by `MaxWordsPerRequest` |
| `Timeout` | int | no | — | 1 – 60000 ms |

### `<Write>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Name` | string | yes | — | 1 – 64 chars, no leading/trailing whitespace, unique in the file (case-sensitive); `NamedWrite` uses it |
| `Id` | int | yes | — | message ID |
| `Address` | string | yes | — | word or bit address |
| `Timeout` | int | no | — | 1 – 60000 ms |
| `RepeatCount` | int | no | — | 0 – 100 |

```xml
<FinsSerial Port="COM3" Baudrate="9600" Parity="Even" DataBits="7" StopBits="Two" UnitNumber="0">
  <Read Id="1" Addresses="D100,CIO0.00" />
  <ReadBlock Id="2" Address="D0" Count="100" Timeout="2000" />
  <Write Name="Speed" Id="100" Address="D200" />
</FinsSerial>
```

---

## 13. TextCommRTUMaster

Class `Devinno.Communication.TextComm.RTU.TextCommRTUMaster`, config `TextCommRTUMasterConfig`. Children: `<Auto>`*. No `<Write>` (use `ManualSend` in code).

### `<TextCommRTUMaster>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Port` | string | no | `COM1` | not empty |
| `Baudrate` | int | no | `115200` | ≥ 1 |
| `Parity` | enum | no | `None` | `None`, `Odd`, `Even`, `Mark`, `Space` |
| `DataBits` | int | no | `8` | 5 – 8 |
| `StopBits` | enum | no | `One` | `One`, `OnePointFive`, `Two` |
| `Timeout` | int | no | `100` | 1 – 60000 ms |
| `Interval` | int | no | `10` | 0 – 60000 ms |
| `AutoReconnect` | bool | no | `false` | reopen the port when it is lost |
| `DiscardWritesWhileDisconnected` | bool | no | `true` | discard writes while not connected (and pending writes when the connection is lost); `WriteDiscarded` reports each |
| `BufferSize` | int | no | `1024` | 16 – 65536 bytes (max response frame) |
| `MessageEncoding` | string | no | `us-ascii` | encoding web name (`us-ascii`, `utf-8`; code pages such as `euc-kr` need `CodePagesEncodingProvider` registered by the app) |

### `<Auto>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Id` | int | yes | — | message ID |
| `Slave` | int | yes | — | 0 – 255 |
| `Command` | int | yes | — | 0 – 255 |
| `Message` | string | yes | — | text to send; `""` allowed but the attribute must be present |
| `Timeout` | int | no | — | 1 – 60000 ms |

```xml
<TextCommRTUMaster Port="COM3" Baudrate="115200" MessageEncoding="us-ascii">
  <Auto Id="1" Slave="1" Command="3" Message="READ" />
  <Auto Id="2" Slave="1" Command="4" Message="" Timeout="200" />
</TextCommRTUMaster>
```

---

## 14. TextCommTCPMaster

Class `Devinno.Communication.TextComm.TCP.TextCommTCPMaster`, config `TextCommTCPMasterConfig`. Children: `<Auto>`* (same attributes as §13).

### `<TextCommTCPMaster>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `RemoteIP` | string | no | `127.0.0.1` | not empty |
| `RemotePort` | int | no | `7897` | 1 – 65535 |
| `Timeout` | int | no | `100` | 1 – 60000 ms |
| `Interval` | int | no | `10` | 0 – 60000 ms |
| `AutoReconnect` | bool | no | `true` | reconnect when the connection is lost |
| `DiscardWritesWhileDisconnected` | bool | no | `true` | discard writes while not connected (and pending writes when the connection is lost); `WriteDiscarded` reports each |
| `BufferSize` | int | no | `1024` | 16 – 65536 bytes |
| `MessageEncoding` | string | no | `us-ascii` | encoding web name |

### `<Auto>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Id` | int | yes | — | message ID |
| `Slave` | int | yes | — | 0 – 255 |
| `Command` | int | yes | — | 0 – 255 |
| `Message` | string | yes | — | text to send; `""` allowed |
| `Timeout` | int | no | — | 1 – 60000 ms |

```xml
<TextCommTCPMaster RemoteIP="192.168.0.50" RemotePort="7897" Timeout="300" MessageEncoding="utf-8">
  <Auto Id="1" Slave="1" Command="65" Message="READ" />
</TextCommTCPMaster>
```

---

## 15. TextCommRTUSlave

Class `Devinno.Communication.TextComm.RTU.TextCommRTUSlave`, config `TextCommRTUSlaveConfig`. No children.

### `<TextCommRTUSlave>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Port` | string | no | `COM1` | not empty |
| `Baudrate` | int | no | `115200` | ≥ 1 |
| `Parity` | enum | no | `None` | `None`, `Odd`, `Even`, `Mark`, `Space` |
| `DataBits` | int | no | `8` | 5 – 8 |
| `StopBits` | enum | no | `One` | `One`, `OnePointFive`, `Two` |
| `MessageEncoding` | string | no | `us-ascii` | encoding web name |
| `AutoReconnect` | bool | no | `false` | reopen the port when it is lost |

```xml
<TextCommRTUSlave Port="COM4" Baudrate="115200" MessageEncoding="us-ascii" AutoReconnect="true" />
```

---

## 16. TextCommTCPSlave

Class `Devinno.Communication.TextComm.TCP.TextCommTCPSlave`, config `TextCommTCPSlaveConfig`. No children.

### `<TextCommTCPSlave>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `LocalPort` | int | no | `7897` | 1 – 65535 |
| `MaxClients` | int | no | `10` | 1 – 1000 |
| `MessageEncoding` | string | no | `us-ascii` | encoding web name |

```xml
<TextCommTCPSlave LocalPort="7897" MaxClients="10" MessageEncoding="utf-8" />
```

---

## 17. MQClient

Class `Devinno.Communication.Mqtt.MQClient`, config `MQClientConfig`. Children: `<Subscribe>`*. The password is never stored; pass it to `Start(clientID, userName, password)`.

### `<MQClient>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `BrokerHostName` | string | no | `127.0.0.1` | not empty |
| `BrokerPort` | int | no | `1883` | 1 – 65535 |
| `ClientID` | string | no | — | not specified = a GUID is generated at Start |
| `UserName` | string | no | — | not specified = no authentication |
| `AliveTopic` | string | no | `/test-alive` | topic name without wildcards; `""` turns off the alive check |
| `AliveInterval` | int | no | `1000` | 100 – 60000 ms |
| `AliveTimeout` | int | no | `3000` | 100 – 60000 ms |
| `MaxPendingMessages` | int | no | `100000` | 1 – 10000000 |
| `OverflowPolicy` | enum | no | `DropOldest` | `DropOldest`, `DropNewest` |

### `<Subscribe>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Topic` | string | yes | — | topic filter (wildcards `+`, `#` allowed) |
| `Qos` | enum | no | `MostOnce` | `MostOnce`, `LeastOnce`, `ExactlyOnce` (`GrantedFailure` is in the XSD but rejected) |

If several `<Subscribe>` elements have the same Topic, the last one is used.

```xml
<MQClient BrokerHostName="192.168.0.5" BrokerPort="1883" ClientID="line1" UserName="user">
  <Subscribe Topic="sensor/#" Qos="LeastOnce" />
  <Subscribe Topic="cmd/line1" />
</MQClient>
```

---

## 18. SecsHsms

Class `Devinno.Communication.Secs.Hsms.SecsHsms` (HSMS-SS), config `SecsHsmsConfig`. No children. Also used as a child of `<GemEquipment>`/`<GemHost>`.

### `<SecsHsms>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Role` | enum | no | `Host` | `Host`, `Equipment` |
| `DeviceID` | int | no | `0` | 0 – 32767 |
| `T3` | int | no | `45000` | 1000 – 120000 ms (reply timeout) |
| `Mode` | enum | no | `Active` | `Active` (connects), `Passive` (listens) |
| `RemoteIP` | string | no | `127.0.0.1` | peer address when Active, not empty |
| `RemotePort` | int | no | `5000` | 1 – 65535 |
| `LocalIP` | string | no | `0.0.0.0` | IP address (not a host name) when Passive |
| `LocalPort` | int | no | `5000` | 0 – 65535 |
| `T5` | int | no | `10000` | 1000 – 240000 ms |
| `T6` | int | no | `5000` | 1000 – 240000 ms |
| `T7` | int | no | `10000` | 1000 – 240000 ms |
| `T8` | int | no | `5000` | 1000 – 120000 ms |
| `LinktestInterval` | int | no | `60000` | 0 (off) or 1000 – 3600000 ms |
| `MaxMessageSize` | int | no | `16777216` | 1024 – 1073741824 bytes |
| `AutoReconnect` | bool | no | `true` | reconnect when Active loses the connection |

```xml
<SecsHsms Role="Equipment" DeviceID="1" Mode="Passive" LocalIP="0.0.0.0" LocalPort="5000" T3="45000" />
```

---

## 19. SecsSerial

Class `Devinno.Communication.Secs.Serial.SecsSerial` (SECS-I), config `SecsSerialConfig`. No children. Also used as a child of `<GemEquipment>`/`<GemHost>`.

### `<SecsSerial>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Role` | enum | no | `Host` | `Host`, `Equipment` |
| `DeviceID` | int | no | `0` | 0 – 32767 |
| `T3` | int | no | `45000` | 1000 – 120000 ms |
| `Port` | string | no | `COM1` | not empty |
| `Baudrate` | int | no | `9600` | ≥ 1 |
| `Parity` | enum | no | `None` | `None`, `Odd`, `Even`, `Mark`, `Space` |
| `DataBits` | int | no | `8` | 5 – 8 |
| `StopBits` | enum | no | `One` | `One`, `OnePointFive`, `Two` |
| `T1` | int | no | `500` | 100 – 10000 ms |
| `T2` | int | no | `10000` | 200 – 25000 ms |
| `T4` | int | no | `45000` | 1000 – 120000 ms |
| `RetryLimit` | int | no | `3` | 0 – 31 |
| `IsMaster` | bool | no | — | not specified = `true` for Equipment, `false` for Host |
| `DuplicateDetection` | bool | no | `true` | |
| `AutoReconnect` | bool | no | `false` | reopen the port after a disconnection |

```xml
<SecsSerial Role="Equipment" DeviceID="1" Port="COM3" Baudrate="9600" T1="500" T2="10000" RetryLimit="3" />
```

---

## 20. GemEquipment

Class `Devinno.Communication.Secs.Gem.GemEquipment` (SEMI E30 equipment side), config `GemEquipmentConfig`. Construct with the transport: `new GemEquipment(new SecsHsms())` or `new GemEquipment(new SecsSerial())`; the XML transport child must match that type.
Children, in this order: `<SecsHsms>` **or** `<SecsSerial>` (0 – 1, optional; omitted = transport unchanged), `<Standard>` (0 – 1), `<SV>`*, `<DV>`*, `<EC>`*, `<CE>`*, `<Alarm>`*, `<Command>`* (each with `<Param>`*), `<Message>`*.

- ID format attributes take `U1 U2 U4 U8 I1 I2 I4 I8 A`. An ID is a decimal integer for numeric formats, plain text for `A`.
- Value `Format` attributes take the SML names `L B BOOLEAN A J C2 I1 I2 I4 I8 U1 U2 U4 U8 F4 F8`. Value text (`InitialValue`, `Default`, `Min`, `Max`): one decimal number for numeric formats, the text for `A`, `true`/`false` for `BOOLEAN`, a list of `0x..` bytes for `B`, `J`, `C2`.
- SV, DV, EC and the standard variables share one VID space; CE, the alarm CEIDs and the standard CEs share one CEID space. Duplicates are a `FormatException`. A `<CE>` `Vids` entry must be a defined VID.
- The transport child's `Role` must be omitted or `Equipment`.

### `<GemEquipment>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `ModelName` | string | no | `""` | MDLN, 0 – 20 characters |
| `SoftwareRevision` | string | no | `""` | SOFTREV, 0 – 20 characters |
| `VidFormat` | string | no | `U4` | ID format |
| `CeidFormat` | string | no | `U4` | ID format |
| `RptidFormat` | string | no | `U4` | ID format |
| `AlidFormat` | string | no | `U4` | ID format |
| `DataidFormat` | string | no | `U4` | ID format |
| `InitialCommunicationState` | enum | no | `Enabled` | `Disabled`, `Enabled` |
| `InitiateCommunication` | bool | no | `true` | the equipment sends S1F13 first |
| `InitialControlState` | enum | no | `HostOffline` | `EquipmentOffline`, `HostOffline`, `OnlineLocal`, `OnlineRemote` (`AttemptOnline` is in the XSD but rejected) |
| `OnlineFailedState` | enum | no | `EquipmentOffline` | `EquipmentOffline`, `HostOffline` |
| `EventsEnabledByDefault` | bool | no | `false` | enable all CEs at Start |
| `AlarmsEnabledByDefault` | bool | no | `false` | enable all alarm reports at Start |
| `EventReplyExpected` | bool | no | `true` | W-bit of S6F11 |
| `AlarmReplyExpected` | bool | no | `true` | W-bit of S5F1 |
| `TerminalReplyExpected` | bool | no | `true` | W-bit of S10F1 |
| `CommandReplyTimeout` | int | no | `10000` | 1000 – 110000 ms |
| `MaxPendingSends` | int | no | `1000` | 1 – 100000 |
| `MaxPendingEvents` | int | no | `10000` | 1 – 1000000 |

### `<SecsHsms>` / `<SecsSerial>` (child)

Same attributes as §18 / §19. Only one of the two may appear. `Role` omitted or `Equipment`.

### `<Standard>`

All attributes optional strings (an ID in `VidFormat`/`CeidFormat`); omitted = the default ID.

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Clock` | string | no | — | SV, default ID 1001 |
| `ControlState` | string | no | — | SV, 1002 |
| `PreviousControlState` | string | no | — | SV, 1003 |
| `EventsEnabled` | string | no | — | SV, 1004 |
| `AlarmsEnabled` | string | no | — | SV, 1005 |
| `AlarmsSet` | string | no | — | SV, 1006 |
| `ProcessState` | string | no | — | SV, 1007 |
| `PreviousProcessState` | string | no | — | SV, 1008 |
| `EstablishCommunicationsTimeout` | string | no | — | EC, 1101 |
| `TimeFormat` | string | no | — | EC, 1102 |
| `AlarmID` | string | no | — | DV, 1201 |
| `EquipmentOffline` | string | no | — | CE, 1301 |
| `ControlStateLocal` | string | no | — | CE, 1302 |
| `ControlStateRemote` | string | no | — | CE, 1303 |
| `ProcessStateChange` | string | no | — | CE, 1304 |
| `OperatorEquipmentConstantChange` | string | no | — | CE, 1305 |
| `MessageRecognition` | string | no | — | CE, 1306 |

(With `VidFormat="A"`/`CeidFormat="A"` the default IDs are the attribute names as text.)

### `<SV>` (status variable)

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Id` | string | yes | — | SVID |
| `Name` | string | yes | — | |
| `Format` | string | yes | — | SML value format |
| `Units` | string | no | `""` | |
| `InitialValue` | string | no | — | omitted = zero-length item of the format |

### `<DV>` (data variable)

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Id` | string | yes | — | DVID |
| `Name` | string | yes | — | |
| `Format` | string | yes | — | SML value format |
| `Units` | string | no | `""` | |
| `InitialValue` | string | no | — | omitted = zero-length item |

### `<EC>` (equipment constant)

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Id` | string | yes | — | ECID |
| `Name` | string | yes | — | |
| `Format` | string | yes | — | SML value format |
| `Default` | string | yes | — | default value, within Min – Max |
| `Min` | string | no | — | numeric formats only |
| `Max` | string | no | — | numeric formats only |
| `Units` | string | no | `""` | |

### `<CE>` (collection event)

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Id` | string | yes | — | CEID |
| `Name` | string | yes | — | |
| `Vids` | string | no | `""` | comma-separated defined VIDs |

### `<Alarm>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Id` | string | yes | — | ALID |
| `Text` | string | yes | — | 1 – 120 characters (Latin-1) |
| `Category` | int | yes | — | 1 – 8 |
| `SetCeid` | string | yes | — | CEID created for "alarm set"; must not repeat another CEID |
| `ClearCeid` | string | yes | — | CEID created for "alarm cleared"; different from SetCeid |

### `<Command>` (remote command)

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Name` | string | yes | — | 1 – 40 characters |

### `<Param>` (child of `<Command>`)

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Name` | string | yes | — | not empty, unique within the command |
| `Format` | string | no | — | SML value format; omitted = any format |

### `<Message>` (registered message the app handles)

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Stream` | int | yes | — | 1 – 127, not 9; not a message GEM already handles; no duplicates |
| `Function` | int | yes | — | odd, 1 – 255 (primary message) |

```xml
<GemEquipment ModelName="DV-100" SoftwareRevision="1.0.0" InitialControlState="OnlineRemote">
  <SecsHsms DeviceID="1" Mode="Passive" LocalPort="5000" />
  <SV Id="2001" Name="Temperature" Format="F4" Units="C" />
  <DV Id="3001" Name="LotID" Format="A" />
  <EC Id="4001" Name="MaxTemp" Format="F4" Default="80" Min="0" Max="200" Units="C" />
  <CE Id="5001" Name="ProcessStarted" Vids="2001,3001" />
  <Alarm Id="1" Text="Over temperature" Category="2" SetCeid="9001" ClearCeid="9002" />
  <Command Name="START">
    <Param Name="LOTID" Format="A" />
  </Command>
  <Message Stream="7" Function="1" />
</GemEquipment>
```

---

## 21. GemHost

Class `Devinno.Communication.Secs.Gem.GemHost` (GEM host side), config `GemHostConfig`. Construct with the transport: `new GemHost(new SecsHsms())` or `new GemHost(new SecsSerial())`; the XML transport child must match that type.
Children, in this order: `<SecsHsms>` **or** `<SecsSerial>` (0 – 1, optional; `Role` omitted or `Host`), `<Subscribe>`*.

### `<GemHost>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `VidFormat` | string | no | `U4` | `U1 U2 U4 U8 I1 I2 I4 I8 A` |
| `CeidFormat` | string | no | `U4` | ID format |
| `RptidFormat` | string | no | `U4` | ID format |
| `AlidFormat` | string | no | `U4` | ID format |
| `DataidFormat` | string | no | `U4` | ID format |
| `TimeFormat` | int | no | `1` | 0 – 2 |
| `AutoConnectSequence` | bool | no | `true` | run the connect sequence on every connection |
| `RequestOnline` | bool | no | `true` | send S1F17 in the connect sequence |
| `SetEquipmentClock` | bool | no | `false` | send S2F31 in the connect sequence |
| `ConfigureReports` | bool | no | `true` | define reports from the subscriptions |
| `EnableAlarms` | bool | no | `true` | send S5F3 (enable all alarms) |
| `DeleteReportsFirst` | bool | no | `true` | delete all equipment reports before defining |
| `ReportIdStart` | long | no | `1000` | ≥ 0; + (number of subscriptions with VIDs − 1) must fit `RptidFormat` |
| `EstablishCommunicationRetry` | int | no | `10` | 1 – 3600 s |
| `CommandReplyTimeout` | int | no | `10000` | 1000 – 110000 ms |
| `MaxPendingEvents` | int | no | `10000` | 1 – 1000000 |

### `<SecsHsms>` / `<SecsSerial>` (child)

Same attributes as §18 / §19. Only one may appear. `Role` omitted or `Host`.

### `<Subscribe>`

| Attribute | Type | Required | Default | Range / values |
|---|---|---|---|---|
| `Ceid` | string | yes | — | CEID (decimal for numeric `CeidFormat`, text for `A`) |
| `Vids` | string | no | — | comma-separated VIDs to receive; omitted = event only, no values |

If the same Ceid appears twice, the later entry replaces the earlier one.

```xml
<GemHost VidFormat="U4" CeidFormat="U4" RptidFormat="U4" ReportIdStart="1000">
  <SecsHsms Mode="Active" RemoteIP="192.168.0.50" RemotePort="5000" DeviceID="1" />
  <Subscribe Ceid="5001" Vids="2001,3001" />
  <Subscribe Ceid="1302" />
</GemHost>
```
