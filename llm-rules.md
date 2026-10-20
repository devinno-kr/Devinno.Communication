# Devinno.Communication Rules (read first)

This is the **document to read first** when you write a communication XML or app code for Devinno.Communication. It is written for AI agents and humans.

- This document: principles, which class to use, XML rules, the memory map → XML procedure, per-request limits, C# lifecycle and threading, protocol pitfalls, how to verify.
- Every root element, attribute, default and range: [`comm-xml-reference.md`](comm-xml-reference.md)
- Exact schema of each root element: `schemas/<Root>.xsd` (output of `ConfigSchema.Get<T>()`)
- C# API (methods, events, event argument members): IntelliSense XML `lib/net8.0/Devinno.Communication.xml`

---

## 1. Core principles

1. **Nothing exists that is not in the docs, the XSDs or the IntelliSense XML.** Loading is strict: an unknown attribute or element throws `FormatException`. Do not invent attributes (`Port` on a TCP class, `Count` on a Modbus `<Auto>`, `Type="DWORD"` on `<Write>` — none of these exist).
2. **If a document and the code disagree, the code is right.** The XSDs are generated from the code and are authoritative for element/attribute **names, structure and types**. Loading is stricter than the XSD in some places (value ranges, and a few enum values that the XSD lists but the loader rejects — listed at the top of [`comm-xml-reference.md`](comm-xml-reference.md)). Passing the XSD is necessary, not sufficient; always also load with the real class.
3. **One instance = one XML file.** One `McEthernet` object talks to one PLC and loads one `<McEthernet>` XML. Two PLCs = two objects = two XML files. There is no multi-device file and no manager class.
4. **The XML holds the connection settings, the Auto (polling) read list and the named write list.** Values are never stored in the XML. Manual (one-shot) jobs are created from code.
5. **Values are raw.** Words arrive as unsigned raw values (0 to 65535), bits as 0/1, in `long`. Signed, 32-bit and floating-point conversion is the app's job (section 7.4).
6. **Events are raised on the library's worker thread**, never on the UI thread (section 7.3).
7. **Verify, do not guess.** Load every XML you write with the real class (`FromXml`/`LoadXml`) and validate it against the XSD (section 9).

---

## 2. Choose the class

| PLC / device | Physical layer | Class (namespace) | XML root | Auto (read) elements | `<Write>` |
|---|---|---|---|---|---|
| Any Modbus device | RS-232/485 | `ModbusRTUMaster` (`Devinno.Communication.Modbus.RTU`) | `ModbusRTUMaster` | `<Auto>` | yes |
| Any Modbus device | TCP | `ModbusTCPMaster` (`Devinno.Communication.Modbus.TCP`) | `ModbusTCPMaster` | `<Auto>` | yes |
| This PC acts as a Modbus slave | RS-232/485 / TCP | `ModbusRTUSlave` / `ModbusTCPSlave` | `ModbusRTUSlave` / `ModbusTCPSlave` | none | no |
| LS XGT (XGK/XGI/XGB), Cnet module or built-in Cnet | RS-232/485 | `LsCNet` (`Devinno.Communication.LS.CNet`) | `LsCNet` | `<RSS>`, `<RSB>` | yes |
| LS XGT, FEnet (dedicated protocol, port 2004) | TCP | `LsFEnet` (`Devinno.Communication.LS.FEnet`) | `LsFEnet` | `<RSS>`, `<RSB>` | yes |
| Mitsubishi Q/L/FX5 built-in Ethernet (MC 3E binary) | TCP | `McEthernet` with `Frame="E3"` (`Devinno.Communication.Mitsubishi.Ethernet`) | `McEthernet` | `<BR>`, `<WR>` | yes |
| Mitsubishi FX3 Ethernet (FX3U-ENET(-ADP/-L), MC 1E binary) | TCP | `McEthernet` with `Frame="E1"` | `McEthernet` | `<BR>`, `<WR>` | yes |
| Mitsubishi computer link (MC 1C, format 1 or 4) | RS-232/485 | `McSerial` (`Devinno.Communication.Mitsubishi.Serial`) | `McSerial` | `<BR>`, `<WR>` | yes |
| Siemens S7-300/400/1200/1500, S7-200 SMART, LOGO! | TCP (ISO-on-TCP, port 102) | `S7Ethernet` (`Devinno.Communication.Siemens`) | `S7Ethernet` | `<Read>`, `<ReadBlock>` | yes |
| Omron CS/CJ/CP/NJ/NX, FINS/UDP | UDP | `FinsUdp` (`Devinno.Communication.Omron.Ethernet`) | `FinsUdp` | `<Read>`, `<ReadBlock>` | yes |
| Omron, FINS/TCP | TCP | `FinsTcp` (`Devinno.Communication.Omron.Ethernet`) | `FinsTcp` | `<Read>`, `<ReadBlock>` | yes |
| Omron, Host Link FINS (serial) | RS-232/485 | `FinsSerial` (`Devinno.Communication.Omron.Serial`) | `FinsSerial` | `<Read>`, `<ReadBlock>` | yes |
| Devinno TextComm device (text messages) | serial / TCP | `TextCommRTUMaster`, `TextCommTCPMaster` (`…TextComm.RTU` / `…TextComm.TCP`) | same as class | `<Auto>` | no |
| TextComm slave | serial / TCP | `TextCommRTUSlave`, `TextCommTCPSlave` | same as class | none | no |
| MQTT broker | TCP | `MQClient` (`Devinno.Communication.Mqtt`) | `MQClient` | `<Subscribe>` | no |
| SECS-II transport only | TCP (HSMS) / serial (SECS-I) | `SecsHsms` (`…Secs.Hsms`), `SecsSerial` (`…Secs.Serial`) | `SecsHsms`, `SecsSerial` | none | no |
| SEMI E30 GEM equipment side / host side | HSMS or SECS-I | `GemEquipment`, `GemHost` (`…Secs.Gem`) | `GemEquipment`, `GemHost` | see reference | no |

- The XML root name is always the class name (`<McEthernet>`), not the config class name (`McEthernetConfig`).
- Configuration classes are `<Class>Config` (`McEthernetConfig`), used with `ConfigSchema.Get<T>()` and `ApplyConfig`/`ToConfig`.
- Named writes (`<Write>` + `NamedWrite`) exist only on the 10 PLC masters: `ModbusRTUMaster`, `ModbusTCPMaster`, `LsCNet`, `LsFEnet`, `McSerial`, `McEthernet`, `S7Ethernet`, `FinsUdp`, `FinsTcp`, `FinsSerial`.
- SECS/GEM, MQTT and the slaves are covered here only for their XML. Use IntelliSense for their messaging APIs.
- SECS/GEM event order: transport events (`DeviceOpened` → that connection's `PrimaryReceived`/`ProtocolError` → `DeviceClosed`) are raised one at a time on one dispatcher thread in the order received; GEM state changes follow wire order (GEM app events are raised in that order on the GEM app event thread).
- `Send`/`SendAsync` results (and GEM command-method returns/exceptions) are outside that order (other threads); never assume they come before or after an event.
- Judge link state by `DeviceOpened`/`DeviceClosed`, not `IsOpen` (GEM: `GemEquipment.CommunicationState`, `GemHost.IsCommunicating` and `CommunicationStateChanged`).

---

## 3. XML rules

| Rule | Do | Don't |
|---|---|---|
| Root name = class name | `<McEthernet …>` | `<McEthernetConfig>`, `<Mitsubishi>`, a wrapper `<Devices>` element |
| Strict mode | Use only attributes listed in the reference/XSD | Misspell (`RemoteIp`), add comments as attributes, add unknown elements — all throw `FormatException` |
| Omitted attribute = default value | Omit attributes you do not need | Write every attribute "to be safe" with guessed values |
| Required attributes | Always write them (Id, Func, Slave, Length, Device, Count, Name …, see reference) | Rely on a default for a required attribute (missing required = `FormatException`) |
| Enum values | Exact, case-sensitive name: `FC3`, `E3`, `Even`, `One`, `Passive` | `fc3`, `3`, `even`, `1`, `READ_HOLDING` |
| Booleans | `true` / `false` | `True`, `1`, `yes` |
| Integers | Decimal: `Start="100"`, `RemoteTsap="769"` | Hex `0x64`, `100h`, `1,000` |
| Device / address text | The protocol's own notation (`D100`, `%MW100`, `DB1.DBW0`, `D100.15`) | Numbers without the area (`100` for Mitsubishi D100) |
| Child order | All Auto (read) elements first, then all `<Write>` elements. Auto element types may be mixed (`<WR>`, `<BR>`, `<WR>`) | `<Write>` before or between Auto elements: the library still loads it, but XSD validation fails |
| GemEquipment/GemHost child order | Transport (`<SecsHsms>` or `<SecsSerial>`) first, then the other children in XSD order (reference §20/§21) | Both `<SecsHsms>` and `<SecsSerial>` |
| Validation | The whole XML is checked before anything is applied. If one value is wrong, nothing changes and `FormatException` names the element and attribute (for example `WR Id="2"`, `Write Name="Recipe"`) | Expect partial loading |
| Loading replaces | Loading replaces the whole Auto list and the whole named write list | Expect XML entries to be appended to entries registered in code |
| When to load | `LoadXml`/`FromXml` only while stopped (`IsStart == false`) | Call it after `Start()` (throws `InvalidOperationException`). To reload: `Stop()` → `LoadXml()` → `Start()` |
| XML declaration / comments | `<?xml version="1.0" encoding="utf-8"?>` and `<!-- -->` comments are allowed | — |

Note: exception messages are in Korean, but the element label and attribute name inside them are ASCII (`<WR Id="2"> … Count …`), so you can still locate the error.

---

## 4. Memory map → XML procedure

A memory map is a table of signal name, address, read/write and type. Convert it like this.

1. **Pick the class** from section 2 and fill the connection attributes (IP/port or serial settings, frame, station number) from the PLC settings. Omit what equals the default.
2. **Collect the read signals.** Sort them by area (D, M, %MW, DB1, …) and address.
3. **Make read ranges.** Merge signals of the same area into contiguous ranges; a 32-bit signal occupies two words, include both. Read through a gap (merge two ranges) only when **both** hold: (a) the merged range still fits one request (section 5) — each extra request costs one more round trip per poll cycle, extra words in a request cost almost nothing; (b) every address in the gap exists on the device. Per protocol: Mitsubishi `D`/`M`/`W`…, LS `%M`/`%D`…, Omron `D`/`W`/`CIO` — the gap is inside the CPU's area, so (b) holds as long as the range ends inside the configured area size (FINS: the library rejects a block that ends past the area). S7 — a gap inside one DB is fine as long as the range ends inside the DB length (past it the PLC returns item error 0x05). **Modbus — device-dependent**: many devices answer a read that touches an unmapped register with exception 2 for the whole request; merge across a gap only if the device documents the gap registers as readable, otherwise make separate `<Auto>` elements.
4. **Split every range at the per-request limit** (section 5). One Auto element per piece. FINS `<ReadBlock>` and S7 `<ReadBlock>` are split by the library automatically; everything else must be split by you.
5. **Make one Auto element per range** (`<WR>`, `<RSB>`, `<Auto Func="FC3">`, `<ReadBlock>` …). Use random-read elements (`<RSS>`, S7 `<Read>`, FINS `<Read>`) for scattered single signals.
6. **Make the write list.** One `<Write>` per write signal, `Name` = the signal name from the map, address = the signal's (first) address. A block of consecutive write words (a recipe) is **one** `<Write>` at its first address, written later with `NamedWrite(name, values)`. A 32-bit write signal is one `<Write>` at its (lower) address; how many values to pass depends on the protocol (table 4.4).
7. **Assign Ids** (scheme below). Ids come back as `MessageID` in every event.
8. **Order**: all Auto elements, then all `<Write>` elements.
9. **Verify** with `FromXml`/`LoadXml` and the XSD (section 9). Then write the C# code that maps `MessageID` + index to signal names.

### 4.1 Id scheme (recommended)

The library does **not** check Ids for duplicates or overlaps. Use a scheme that keeps them apart:

| Range | Use |
|---|---|
| 1 – 99 | Auto read elements, in XML order |
| 100 – 199 | `<Write>` elements, in XML order |
| 900 – 999 | Manual jobs created in code (`ManualWR`, `ManualRead` …) |

Each Auto element gets its own Id so the `DataReceived` handler can tell ranges apart. If two elements share an Id, their events cannot be distinguished.

### 4.2 Name rules for `<Write>`

- `Name`: 1 to 64 characters, no leading/trailing whitespace, case-sensitive, unique in one XML. Any characters are allowed (Korean signal names are fine). Use the memory map's signal name verbatim.
- `NamedWrite("unknown name", …)` throws `ArgumentException`. Check with `ContainsNamedWrite(name)` if the name comes from outside.

### 4.3 Address notation per protocol

| Class | Notation | Base / radix |
|---|---|---|
| Modbus | `Start` is always the **0-based** register/coil number (see 4.5 for 5/6-digit references like 40001 / 400001). If the map already gives 0-based addresses (often "0x0064" / "address 100"), use them as-is (hex → decimal). | 0-based, decimal in XML |
| LsCNet / LsFEnet | Direct variable only: `%` + area letter + size letter (`X` bit, `B` byte, `W` word, `D` dword, `L` lword) + address, 4 to 16 characters: `%MW100`, `%MX0`, `%DW100`, `%MD10`, `%IW0.0.0`. XG5000 device notation (`D00100`, `M0000`, `P0001`, `M0010F`) is **not** accepted (`FormatException`) and the library does not convert it — see 4.6 | as the PLC defines |
| McEthernet / McSerial | Device code + number: `D100`, `M0`, `X1F`, `Y20`, `B1F`, `W10`, `SM400`, `SD0`, `ZR100`, `TN5` (case-insensitive) | X/Y: per `XYNotation` (Auto = hex for E3, octal for E1 and McSerial). B, W, SB, SW, ZR: hex. Others: decimal |
| S7Ethernet | Absolute address: `DB1.DBX0.1`, `DB1.DBB0`, `DB1.DBW0`, `DB1.DBD0`, `M10.0`, `MB10`, `MW10`, `MD10`, `I0.0`/`IB0`/`IW0`/`ID0`, `Q0.0`/`QB0`/`QW0`/`QD0` (German `E`/`A` = `I`/`Q`) | byte addresses; bit 0–7 |
| FinsUdp / FinsTcp / FinsSerial | `D100`, `CIO100` (or `100`), `W10`, `H20`, `A100`, `E0_100` (EM bank hex), bits `CIO100.05`, `D100.15` | word number decimal; bit 00–15 |

Codes that exist (anything else is a `FormatException` on load):

- MC 3E: `SM SD X Y M L F V B D W TS TC TN SS SC SN CS CC CN SB SW S Z R ZR`
- MC 1E: `X Y M S L F B D R W TN TS TC CN CS CC`
- MC 1C (McSerial): `X Y M L S B F D W R TN TS TC CN CS CC`
- S7: I/E, Q/A, M, DB. Timers and counters (T, C, Z) are not supported.
- FINS: CIO, W, H, A, D (DM), EM. Timers and counters (T, C) are not supported.

Mitsubishi bit reads: `<BR>` `Count` is a number of **points** (decimal) and `Values[i]` is the point *start + i*, counted in the device's radix. Example: `<BR Device="X10" Count="16">` with hex X/Y (E3) returns X10 … X1F, so `Values[15]` is X1F; with octal X/Y (E1, McSerial, `XYNotation="Octal"`) the same element returns X10 … X17, X20 … X27.

### 4.4 32-bit (DWORD / DINT / REAL) signals

| Class | Read | `<Write>` + `NamedWrite` |
|---|---|---|
| ModbusRTUMaster / ModbusTCPMaster | two registers; word order is device-specific (check the manual; many devices put the low word at the lower address) | `Func="FC16"` at the first register, `NamedWrite(name, [first, second])` — two 16-bit words in the device's word order |
| McEthernet / McSerial | two words, low word at the lower device (`D102` low, `D103` high) | word device at the low address, `NamedWrite(name, [low, high])` |
| FinsUdp / FinsTcp / FinsSerial | two words, low word at the lower address (`D100` low, `D101` high) | word address at the low address, `NamedWrite(name, [low, high])` (block write of 2 words) |
| S7Ethernet | one `D` address (`DB1.DBD4`, `MD10`) = one 32-bit raw value (or 4 bytes in a `<ReadBlock>`, big-endian) | `Address="DB1.DBD4"`, **one** value: `NamedWrite(name, value)` (REAL: `BitConverter.SingleToInt32Bits(f)`). Passing two values writes 8 bytes (two DBDs) |
| LsCNet / LsFEnet | one `D` device (`%DD10`, `%MD10`) = one 32-bit raw value (`L` = 64-bit) | `Device="%MD10"`, **one** value: `NamedWrite(name, value)`. Passing two values writes two consecutive D devices (WSB) |

Rule: word-addressed protocols (Modbus, Mitsubishi, Omron) → two 16-bit values; byte/size-addressed protocols with a 32-bit address (S7 `D`, LS `D`/`L`) → one value.

### 4.5 Modbus reference numbers → `Func` + `Start`

| Map reference | Table | `Func` (read) | `Start` |
|---|---|---|---|
| 00001 – 09999 / 000001 – 065536 | coil | `FC1` (write: `FC5`/`FC15`) | reference − 1 (00001 → 0, 000001 → 0) |
| 10001 – 19999 / 100001 – 165536 | discrete input | `FC2` (read-only) | reference − 10001 (5 digits) / − 100001 (6 digits) |
| 30001 – 39999 / 300001 – 365536 | input register | `FC4` (read-only) | reference − 30001 / − 300001 |
| 40001 – 49999 / 400001 – 465536 | holding register | `FC3` (write: `FC6`/`FC16`) | reference − 40001 / − 400001 (40001 → 0, 40101 → 100, 400001 → 0) |

`Start` is never the reference number itself (`Start="40001"` reads the wrong register; `Start="400001"` is out of range).

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

### 4.6 LS XG5000 notation

LS memory maps are often written in XG5000 notation (`D00100`, `M0000`, `P0001`, bit `M0010F`). The library accepts only direct variables (`%DW100`, `%MW0`, `%MX0` …), and the conversion rules between the two notations are not part of this library or its docs. **Do not guess a conversion.** Ask the user (or the PLC engineer) for the direct-variable addresses, or use addresses that are already in `%` form.

---

## 5. Per-request limits (checked at load time, values from the code)

A count above the limit is a `FormatException` when the XML is loaded (or `ArgumentException` when the job is registered from code).

| Class | Element / call | Limit per element or call | Library splits? |
|---|---|---|---|
| ModbusRTUMaster / ModbusTCPMaster | `<Auto>` `Length` with `Func="FC1"` or `"FC2"` | 1 – 2000 bits | no — split yourself |
| | `<Auto>` `Length` with `Func="FC3"` or `"FC4"` | 1 – 125 words | no — split yourself |
| | `NamedWrite` on FC15 / FC16 | 1 – 1968 bits / 1 – 123 words per call | no |
| LsCNet | `<RSS>` `Devices` | 1 – 16 devices, all the same size letter | — |
| | `<RSB>` `Count`, `NamedWrite` list (WSB) | 120 bytes: B 120, W 60, D 30, L 15; bit (X) not allowed | no |
| | whole frame | 256 bytes (not checked by the library; the PLC answers NAK) | — |
| LsFEnet | `<RSS>` `Devices` | 1 – 16 devices, all the same size letter | — |
| | `<RSB>` `Count`, `NamedWrite` list (WSB) | 1400 bytes: B 1400, W 700, D 350, L 175; numeric address only, bit (X) not allowed | no |
| McEthernet `Frame="E3"` | `<WR>` `Count` / `NamedWrite` on a word device | 1 – 960 words | no |
| | `<BR>` `Count` / `NamedWrite` on a bit device | 1 – 3584 points | no |
| | (QnA CPUs accept only 480 words; not checked) | | |
| McEthernet `Frame="E1"`, McSerial (1C) | `<BR>` `Count` | 1 – 256 points | no |
| | `<WR>` `Count` | 1 – 64 words (word device), 1 – 32 words (bit device) | no |
| | `NamedWrite` on a bit device (BW) | 1 – 160 points | no |
| | `NamedWrite` on a word device (WW) | 1 – 64 words (word device), 1 – 10 words (bit device) | no |
| S7Ethernet | `<Read>` `Addresses` | **max 19 addresses** of any size (PDU 240; 20 is rejected by the size check) | no |
| | `<ReadBlock>` `Count` | 1 – 65535 bytes, start must be a byte address (`DB1.DBB0`, `MB0`) | **yes** (pieces of PDU − 18 bytes) |
| | `NamedWrite(name, value)` (random write) | one value (`ManualWrite` takes max 12 bit/byte/word values or max 11 dword values per call, PDU 240) | — |
| | `NamedWrite(name, values)` (block write) | 1 – 65535 bytes total | **yes** (pieces of PDU − 28 bytes, not atomic) |
| FinsUdp / FinsTcp | `<Read>` `Addresses` (0104) | 1 – 167 addresses | no |
| | `<ReadBlock>` `Count` (0101), `NamedWrite` list | 1 – 65535 items | **yes**, by `MaxWordsPerRequest` (default 499, max 997) |
| FinsSerial | `<Read>` `Addresses` | 1 – 134 addresses | no |
| | `<ReadBlock>` `Count`, `NamedWrite` list | 1 – 65535 items | **yes**, by `MaxWordsPerRequest` (default and max 266) |

Split example (Modbus TCP, holding registers 40001–40300 = 300 words): three `<Auto>` elements, `Start="0" Length="125"`, `Start="125" Length="125"`, `Start="250" Length="50"`.

```xml
<ModbusTCPMaster RemoteIP="192.168.0.40" RemotePort="502">
  <Auto Id="1" Func="FC3" Slave="1" Start="0" Length="125" />
  <Auto Id="2" Func="FC3" Slave="1" Start="125" Length="125" />
  <Auto Id="3" Func="FC3" Slave="1" Start="250" Length="50" />
  <Auto Id="4" Func="FC1" Slave="1" Start="0" Length="64" />
  <Write Name="SpeedSet" Id="100" Func="FC6" Slave="1" Start="400" />
  <Write Name="Recipe" Id="101" Func="FC16" Slave="1" Start="500" />
  <Write Name="Reset" Id="102" Func="FC5" Slave="1" Start="10" />
</ModbusTCPMaster>
```

### 5.1 What `NamedWrite` sends

Signatures (identical on all 10 PLC masters):

```
void NamedWrite(string name, long value, int? repeatCount = null, int? timeout = null)
void NamedWrite(string name, IReadOnlyList<long> values, int? repeatCount = null, int? timeout = null)
bool ContainsNamedWrite(string name)
```

- `NamedWrite("SpeedSet", 1500)` and `NamedWrite("SpeedSet", speed)` with an `int`/`short`/`ushort` variable bind to the `long` overload (implicit widening).
- A collection expression `[a, b]` (elements `int` or `long`), a `long[]` or a `List<long>` binds to the `IReadOnlyList<long>` overload.
- `NamedWrite(name, value)` and `NamedWrite(name, [value])` are not always the same request (S7 and FINS: random write vs block write — table below).

| Class | `NamedWrite(name, value)` | `NamedWrite(name, values)` | Value range |
|---|---|---|---|
| Modbus | FC5 / FC6 / FC15 with 1 value / FC16 with 1 value (per `Func`) | FC15 / FC16. FC5 and FC6 accept a 1-element list only (2+ → `ArgumentException`) | FC5/FC15: 0 or 1. FC6/FC16: −32768 to 65535 |
| LsCNet, LsFEnet | WSS (1 device) | WSB (consecutive devices); a 1-element list is sent as WSS | per size: X 0/1, B/W/D signed min to unsigned max, L any |
| McSerial, McEthernet | bit device → BW 1 point, word device → WW 1 word | bit device → BW, word device → WW, consecutive from `Device` | bit 0/1, word −32768 to 65535 |
| S7Ethernet | random write of 1 value at `Address` | block write: each value encoded big-endian with the address size (B 1, W 2, D 4 bytes), concatenated, written from the start byte. Bit (X) addresses → `ArgumentException` | bit 0/1; list: B 0–255, W −32768–65535, D −2147483648–4294967295 |
| FinsUdp, FinsTcp, FinsSerial | Write (0102) of 1 item | block write (0102, split by `MaxWordsPerRequest`; a 1-element list is still a block write) | word −32768 to 65535, bit 0/1 |

- `NamedWrite` only queues a Manual job (same queue, priority, events and retries as `ManualXxx`). The result arrives later as an event with `MessageID` = the `<Write>` Id.
- `repeatCount`/`timeout` arguments override the XML `RepeatCount`/`Timeout`; if both are absent: no retries and the class `Timeout`.
- **Do not use `RepeatCount` on a pulse, counter or incremental write.** A retry resends a write the device may already have executed (the response may simply have been lost), so the write can be applied twice. Keep it for idempotent writes (set a value), or read the value back before writing again.
- A Modbus Manual FC6 / FC16 write with a value outside -32768..65535 throws `ArgumentOutOfRangeException` at the call (negative values are sent as two's complement).
- S7 and FINS block writes are **not atomic**: if one fails part-way, only the first chunks may have been written. `WrittenBytes` (S7) / `WrittenCount` (FINS) on the failure event tell how much the PLC accepted in that attempt. Do not put a trigger or handshake word inside a block; write it separately after the block succeeded.
- A write made while not connected is discarded by default (`WriteDiscarded`, see 7.1.1).
- Values are `long`. Pass a `long[]`, a `List<long>` or a collection expression (`[1, 2, 3]`). An `int[]` or `List<int>` does not convert to `IReadOnlyList<long>` and does not compile.

---

## 6. Worked example: Mitsubishi Q CPU (MC 3E) memory map → XML + C#

Memory map given by the PLC engineer (PLC at 192.168.0.30, MC protocol open on TCP port 5000, binary):

| Signal | Device | R/W | Type |
|---|---|---|---|
| Status | D100 | R | word |
| AlarmCode | D101 | R | word |
| ActualSpeed | D102–D103 | R | dword (D102 = low word) |
| Counter1 … Counter20 | D110–D129 | R | word ×20 |
| TrendLog | D1000–D2199 | R | word ×1200 |
| Running | M0 | R | bit |
| Alarm | M1 | R | bit |
| Door1 … Door14 | M2–M15 | R | bit ×14 |
| RunCommand | D200 | W | word |
| SpeedSet | D201 | W | word |
| TargetPos | D202–D203 | W | dword (D202 = low word) |
| Reset | M100 | W | bit |
| Recipe | D300–D319 | W | word ×20 (written together) |

Reasoning:

- D100–D129 is 30 words (D104–D109 unused but readable) → one `<WR Count="30">`.
- D1000–D2199 is 1200 words > 960 (3E limit) → `<WR Device="D1000" Count="960">` + `<WR Device="D1960" Count="240">`.
- M0–M15 → one `<BR Count="16">` (bit points). (Alternatively `<WR Device="M0" Count="1">` returns the 16 bits as one word, bit 0 = M0.)
- Each write signal → one `<Write>`; Recipe → one `<Write Device="D300">` written with 20 values; TargetPos → one `<Write Device="D202">` written with 2 values.
- Ids: reads 1–4, writes 100–104.

`plc-line1.xml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<McEthernet RemoteIP="192.168.0.30" RemotePort="5000" Frame="E3" Timeout="1000">
  <!-- reads: Id 1-99 -->
  <WR Id="1" Device="D100" Count="30" />
  <WR Id="2" Device="D1000" Count="960" />
  <WR Id="3" Device="D1960" Count="240" />
  <BR Id="4" Device="M0" Count="16" />
  <!-- writes: Id 100-199 -->
  <Write Name="RunCommand" Id="100" Device="D200" />
  <Write Name="SpeedSet" Id="101" Device="D201" />
  <Write Name="TargetPos" Id="102" Device="D202" />
  <Write Name="Reset" Id="103" Device="M100" />
  <Write Name="Recipe" Id="104" Device="D300" Timeout="2000" RepeatCount="1" />
</McEthernet>
```

App code (console; for WinForms/WPF see 7.3):

```csharp
using Devinno.Communication.Mitsubishi.Ethernet;

var plc = new McEthernet();
plc.LoadXml(Path.Combine(AppContext.BaseDirectory, "plc-line1.xml"));   // FormatException if the XML is wrong; only while stopped

var trend = new long[1200];

// All events below run on the worker thread.
plc.DataReceived += (s, e) =>
{
    switch (e.MessageID)
    {
        case 1:                                     // D100..D129, e.Values[i] = D(100 + i)
            long status = e.Values[0];
            short alarmCode = (short)e.Values[1];   // signed view of a raw word
            uint actualSpeed = (uint)(e.Values[2] | (e.Values[3] << 16));   // D102 low, D103 high
            long counter1 = e.Values[10];           // D110
            Console.WriteLine($"Status={status} Alarm={alarmCode} Speed={actualSpeed} C1={counter1}");
            break;
        case 2: Array.Copy(e.Values, 0, trend, 0, 960); break;     // D1000..D1959
        case 3: Array.Copy(e.Values, 0, trend, 960, 240); break;   // D1960..D2199
        case 4:                                     // M0..M15, 0/1
            bool running = e.Values[0] == 1;
            bool alarm = e.Values[1] == 1;
            Console.WriteLine($"Running={running} Alarm={alarm}");
            break;
    }
};
plc.WriteReceived += (s, e) => Console.WriteLine($"write {e.MessageID} done");
plc.NAKReceived += (s, e) => Console.WriteLine($"PLC error, id {e.MessageID}: 0x{e.ErrorCode:X4}");
plc.TimeoutReceived += (s, e) => Console.WriteLine($"no response, id {e.MessageID}");
plc.DeviceOpened += (s, e) => Console.WriteLine("connected");
plc.DeviceClosed += (s, e) => Console.WriteLine("disconnected");
plc.WriteDiscarded += (s, e) => Console.WriteLine($"write {e.MessageID} not sent: {e.Reason}");

// Write only once connected: a write made while not connected is discarded (WriteDiscarded) — this handler runs on every (re)connect
plc.DeviceOpened += (s, e) =>
{
    plc.NamedWrite("RunCommand", 1);
    plc.NamedWrite("SpeedSet", 1500);

    int targetPos = -20000;                   // 32-bit signed → two raw words, low word first
    plc.NamedWrite("TargetPos", [targetPos & 0xFFFF, (targetPos >> 16) & 0xFFFF]);

    plc.NamedWrite("Reset", 1);               // bit device → BW

    long[] recipe = new long[20];
    for (int i = 0; i < recipe.Length; i++) recipe[i] = 100 + i;
    plc.NamedWrite("Recipe", recipe);         // D300..D319 in one WW request (Id 104)
};

plc.Start();                                  // connects and starts polling (AutoReconnect="true" by default)

Console.ReadLine();
plc.Dispose();                                // stops the loop, closes the connection, releases
```

### 6.1 Second example: Siemens S7-1500 (DB words, REAL, bits)

Memory map (PLC 192.168.0.10, rack 0, slot 1; DB10 and DB20 are non-optimized, PUT/GET enabled):

| Signal | Address | R/W | Type |
|---|---|---|---|
| Status | DB10.DBW0 | R | word |
| Temperature | DB10.DBD2 | R | REAL |
| Running | DB10.DBX6.0 | R | bit |
| Alarm | DB10.DBX6.1 | R | bit |
| Value1 … Value50 | DB10.DBW8 – DB10.DBW106 | R | word ×50 |
| Inputs | IB0 – IB1 | R | byte ×2 |
| SpeedSet | DB20.DBW0 | W | word |
| SetTemp | DB20.DBD2 | W | REAL |
| StartCmd | DB20.DBX6.0 | W | bit |
| Recipe | DB20.DBW10 – DB20.DBW28 | W | word ×10 (written together) |

Reasoning: DB10 bytes 0–107 are contiguous → one `<ReadBlock>` of 108 bytes starting at the byte address `DB10.DBB0` (the library splits it to fit the PDU), decoded in code. The two input bytes → one `<Read>`. Each write signal → one `<Write>`; Recipe → one `<Write>` at `DB20.DBW10` written with 10 values (sent as a 20-byte block write).

```xml
<S7Ethernet RemoteIP="192.168.0.10" Rack="0" Slot="1">
  <ReadBlock Id="1" Address="DB10.DBB0" Count="108" />
  <Read Id="2" Addresses="IB0,IB1" />
  <Write Name="SpeedSet" Id="100" Address="DB20.DBW0" />
  <Write Name="SetTemp" Id="101" Address="DB20.DBD2" />
  <Write Name="StartCmd" Id="102" Address="DB20.DBX6.0" />
  <Write Name="Recipe" Id="103" Address="DB20.DBW10" />
</S7Ethernet>
```

```csharp
using System.Buffers.Binary;
using Devinno.Communication.Siemens;

var plc = new S7Ethernet();
plc.LoadXml(Path.Combine(AppContext.BaseDirectory, "plc-s7.xml"));

plc.BlockReceived += (s, e) =>                 // <ReadBlock>: e.Data = bytes from DB10.DBB0
{
    if (e.MessageID != 1) return;
    ushort status = BinaryPrimitives.ReadUInt16BigEndian(e.Data.AsSpan(0));        // DB10.DBW0
    float temperature = BinaryPrimitives.ReadSingleBigEndian(e.Data.AsSpan(2));   // DB10.DBD2
    bool running = (e.Data[6] & (1 << 0)) != 0;                                   // DB10.DBX6.0
    bool alarm = (e.Data[6] & (1 << 1)) != 0;                                     // DB10.DBX6.1
    var values = new ushort[50];
    for (int i = 0; i < 50; i++) values[i] = BinaryPrimitives.ReadUInt16BigEndian(e.Data.AsSpan(8 + i * 2));   // DB10.DBW8..DBW106
    Console.WriteLine($"Status={status} T={temperature} Run={running} Alarm={alarm} V1={values[0]}");
};
plc.DataReceived += (s, e) =>                  // <Read>: e.Values[i] = i-th address of Addresses
{
    if (e.MessageID == 2) Console.WriteLine($"IB0={e.Values[0]} IB1={e.Values[1]}");
};
plc.WriteReceived += (s, e) => Console.WriteLine($"write {e.MessageID} done");
plc.NAKReceived += (s, e) => Console.WriteLine($"PLC error, id {e.MessageID}: 0x{e.ErrorCode:X2} item {e.ItemIndex}");
plc.TimeoutReceived += (s, e) => Console.WriteLine($"no response, id {e.MessageID} (attempt {e.Attempt}, final {e.IsFinal}, sent {e.RequestSent})");
plc.WriteDiscarded += (s, e) => Console.WriteLine($"write {e.MessageID} not sent: {e.Reason}");

// Write only once connected (a write while not connected is discarded) — this handler runs on every (re)connect
plc.DeviceOpened += (s, e) =>
{
    plc.NamedWrite("SpeedSet", 1500);
    plc.NamedWrite("SetTemp", BitConverter.SingleToInt32Bits(72.5f));   // REAL → raw 32 bits on a DBD address
    plc.NamedWrite("StartCmd", 1);
    plc.NamedWrite("Recipe", [10, 20, 30, 40, 50, 60, 70, 80, 90, 100]); // DB20.DBW10..DBW28, big-endian block write
};

plc.Start();

Console.ReadLine();
plc.Dispose();
```

### 6.2 Third example: Modbus TCP device (registers, coils, 32-bit pair)

Memory map (device 192.168.0.40, port 502, unit ID 1; the manual documents 40001–40200 and 00001–00016 as one contiguous readable block; 32-bit values: low word at the lower register):

| Signal | Reference | R/W | Type |
|---|---|---|---|
| Status … (200 registers) | 40001 – 40200 | R | word ×200 |
| TotalCount | 40011 – 40012 | R | dword (inside the block above) |
| Running, Alarm, … | 00001 – 00016 | R | coil ×16 |
| RunCmd | 00017 | W | coil |
| SpeedSet | 40301 | W | word |
| TargetPos | 40303 – 40304 | W | dword (low word first) |
| Recipe | 40311 – 40330 | W | word ×20 |

Reasoning: 200 registers > 125 → `Start="0" Length="125"` + `Start="125" Length="75"`. Coils → `FC1`. Writes: coil → `FC5`, single register → `FC6`, 32-bit and recipe → `FC16` (written with 2 / 20 values).

```xml
<ModbusTCPMaster RemoteIP="192.168.0.40" RemotePort="502" Timeout="300">
  <Auto Id="1" Func="FC3" Slave="1" Start="0" Length="125" />
  <Auto Id="2" Func="FC3" Slave="1" Start="125" Length="75" />
  <Auto Id="3" Func="FC1" Slave="1" Start="0" Length="16" />
  <Write Name="RunCmd" Id="100" Func="FC5" Slave="1" Start="16" />
  <Write Name="SpeedSet" Id="101" Func="FC6" Slave="1" Start="300" />
  <Write Name="TargetPos" Id="102" Func="FC16" Slave="1" Start="302" />
  <Write Name="Recipe" Id="103" Func="FC16" Slave="1" Start="310" />
</ModbusTCPMaster>
```

```csharp
using Devinno.Communication.Modbus.TCP;

var regs = new int[200];                       // raw 40001..40200 (0..65535)
var mb = new ModbusTCPMaster();
mb.LoadXml(Path.Combine(AppContext.BaseDirectory, "plc-modbus.xml"));

mb.WordReadReceived += (s, e) =>               // FC3: e.ReceiveData is int[] (raw 0..65535), e.StartAddress = Start
{
    Array.Copy(e.ReceiveData, 0, regs, e.StartAddress, e.ReceiveData.Length);   // Id 1 → 0..124, Id 2 → 125..199
    if (e.MessageID == 1)
    {
        short status = (short)regs[0];                                    // 40001, signed view
        int totalCount = (int)(uint)(regs[10] | (regs[11] << 16));        // 40011 low, 40012 high
        Console.WriteLine($"Status={status} Total={totalCount}");
    }
};
mb.BitReadReceived += (s, e) =>                // FC1: e.ReceiveData is bool[]
{
    if (e.MessageID == 3) Console.WriteLine($"Running={e.ReceiveData[0]} Alarm={e.ReceiveData[1]}");
};
mb.BitWriteReceived += (s, e) => Console.WriteLine($"FC5 {e.MessageID} wrote {e.WriteValue}");             // bool
mb.WordWriteReceived += (s, e) => Console.WriteLine($"FC6 {e.MessageID} wrote {e.WriteValue}");            // int
mb.MultiWordWriteReceived += (s, e) => Console.WriteLine($"FC16 {e.MessageID} wrote {e.WriteValues.Length} words");
mb.ExceptionReceived += (s, e) => Console.WriteLine($"device exception {e.ExceptionCode}, id {e.MessageID}");
mb.TimeoutReceived += (s, e) => Console.WriteLine($"no response, id {e.MessageID} (attempt {e.Attempt}, final {e.IsFinal}, sent {e.RequestSent})");
mb.WriteDiscarded += (s, e) => Console.WriteLine($"write {e.MessageID} not sent: {e.Reason}");

// Write only once connected (a write while not connected is discarded) — this handler runs on every (re)connect
mb.DeviceOpened += (s, e) =>
{
    mb.NamedWrite("RunCmd", 1);                // FC5: 0 or 1
    mb.NamedWrite("SpeedSet", 1500);           // FC6
    int targetPos = -20000;
    mb.NamedWrite("TargetPos", [targetPos & 0xFFFF, (targetPos >> 16) & 0xFFFF]);   // FC16, low word first
    long[] recipe = new long[20];
    for (int i = 0; i < recipe.Length; i++) recipe[i] = 10 * i;
    mb.NamedWrite("Recipe", recipe);           // FC16, 20 registers 40311..40330
};

mb.Start();

Console.ReadLine();
mb.Dispose();
```

---

## 7. C# rules

### 7.1 Lifecycle

```
construct → LoadXml/FromXml (or ApplyConfig) → subscribe events → Start()
          → NamedWrite / Manual… calls (any thread, any time after construction)
          → Stop() → Dispose()
```

| Step | Rule |
|---|---|
| Construct | `new McEthernet()` (parameterless). GEM classes wrap a transport: `new GemEquipment(new SecsHsms())`, `new GemHost(new SecsHsms())` |
| Load | `LoadXml(path)` or `FromXml(XElement)`. Only while stopped. Throws `FormatException` (bad XML) or `InvalidOperationException` (running). A relative path is resolved against the **current directory**, not the exe folder: use `Path.Combine(AppContext.BaseDirectory, "plc.xml")` and copy the XML to the output folder (csproj item below) |
| Subscribe | Before `Start()`, so no event is missed |
| Start | `Start()` returns immediately; connecting and polling run on the worker thread. Connection results arrive as `DeviceOpened`/`DeviceClosed` (MQTT: `Connected`/`Disconnected`) |
| Write / one-shot read | `NamedWrite(...)`, `ManualXxx(...)`. Thread-safe; they only validate and queue (see 7.1.1). Manual jobs run before the next Auto job. **A write made while not connected (including right after `Start()`) is discarded by default: write inside `DeviceOpened`, or when `IsOpen` is true** |
| Stop | `Stop()` waits up to `Timeout + 1000` ms for the loop to end. It may be called from an event handler (then it does not wait). It discards pending Manual writes (`WriteDiscarded`, `Stopped`). Needed only to `LoadXml` again and `Start()` again |
| Dispose | `Dispose()` alone is enough at shutdown: it calls `Stop()` and releases. The object cannot be started again |

App csproj item that copies the XML next to the exe:

```
<ItemGroup>
  <None Update="plc.xml" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

#### 7.1.1 Writes and reads while not connected (what the code does)

"Write" means the Manual jobs that change the device: Modbus FC5, FC6, FC15, FC16, FC26; LS WSS, WSB; Mitsubishi BW, WW; S7 and FINS Write, WriteBlock; TextComm every `ManualSend` (the library does not know what a command does). `NamedWrite` registers one of these. Manual **reads** and Auto jobs are not affected.

| Situation | `DiscardWritesWhileDisconnected="true"` (default) | `"false"` |
|---|---|---|
| Manual write / `NamedWrite` called while not connected (before `Start()`, connecting, waiting to reconnect, after `Stop()`) | Arguments are validated (bad name/value → exception at the call), then the write is **not queued**; `WriteDiscarded(NotConnected)` is raised synchronously on the calling thread | Kept in the Manual queue; sent after the connection is (re)established |
| Writes waiting in the queue when the connection is lost | After `DeviceClosed`, `WriteDiscarded(Disconnected)` once per write on the worker thread (`Stopped` if the loop was closed by `Stop()`) | Kept in the queue |
| Request in progress when the connection is lost | `TimeoutReceived` once with `IsFinal` true, then the job is dropped (not retried, even with RepeatCount); same in both modes | same |
| `Stop()` | Pending Manual writes are discarded: `WriteDiscarded(Stopped)` inside the `Stop()` call | **Same: `Stop()` always empties pending writes** |
| Manual read, Auto | Not affected: kept, run after (re)connect | Not affected |
| Connection (re)established | `DeviceOpened`, then queued Manual jobs run first (in call order), then Auto polling continues | same |
| Reconnect | With `AutoReconnect="true"` the worker tries again about every second (a TCP connect try waits up to max(`Timeout`, 1000) ms). With `false`, a failed connect or a lost connection ends the loop: `IsStart` becomes false (queued reads stay until the next `Start()`) | same |
| `FinsUdp` | Connectionless: "connected" means the socket is open, so a write is accepted after `Start()` even if the PLC is off, and then times out (`TimeoutReceived`) | same |

**Contract: every write ends in exactly one of** a success event, a NAK / exception event, a failure event with `IsFinal` true (`TimeoutReceived` or a checksum error event), or `WriteDiscarded`. The exceptions are writes the application removed itself with `ClearManual()` / `RemoveManual(id)` (no event), and a Modbus RTU broadcast (`Slave` 0, the device never replies, no event).

`WriteDiscarded` can be raised on the calling thread (`NotConnected` inside the `NamedWrite`/`ManualXxx` call, `Stopped` inside `Stop()`) as well as on the worker thread. Do not take a lock in the handler that the caller of `NamedWrite`/`Stop` may already hold.

So call `NamedWrite` after `DeviceOpened` (or when `IsOpen` is true). With `DiscardWritesWhileDisconnected="false"` a write queued while offline is sent later when the device reconnects; if a stale write must not be sent, call `ClearManual()` (or `RemoveManual(id)`) on `DeviceClosed`.

### 7.2 Events (PLC masters)

Every handler has the signature `(object? sender, Class.XxxEventArgs e)`; the args types are nested in the class (`McEthernet.DataEventArgs`, `ModbusTCPMaster.WordReadEventArgs`). In every class all args types derive from that class's `ResultEventArgs`, so the "(common)" members exist on every event of the class. All classes also have `DeviceOpened` and `DeviceClosed` (`EventHandler`, plain `EventArgs`).

| Class | Event | Args type (nested) | Members (type) |
|---|---|---|---|
| ModbusRTUMaster, ModbusTCPMaster | all events below | (common: every args type below derives from `ResultEventArgs`) | `MessageID` int, `Slave` int, `Function` ModbusFunction, `StartAddress` int |
| | `BitReadReceived` (FC1/FC2) | `BitReadEventArgs` | + `ReceiveData` bool[] |
| | `WordReadReceived` (FC3/FC4) | `WordReadEventArgs` | + `ReceiveData` int[] (raw 0–65535) |
| | `BitWriteReceived` (FC5) | `BitWriteEventArgs` | + `WriteValue` bool |
| | `WordWriteReceived` (FC6) | `WordWriteEventArgs` | + `WriteValue` int |
| | `MultiBitWriteReceived` (FC15) | `MultiBitWriteEventArgs` | + `WriteValues` bool[] |
| | `MultiWordWriteReceived` (FC16) | `MultiWordWriteEventArgs` | + `WriteValues` int[] |
| | `WordBitSetReceived` (FC26) | `WordBitSetEventArgs` | + `BitIndex` int, `WriteValue` bool |
| | `ExceptionReceived` | `ExceptionEventArgs` | + `ExceptionCode` int (Modbus exception: 1 function, 2 address, 3 value, …) |
| | `TimeoutReceived`; RTU only: `CRCErrorReceived` | `AttemptFailedEventArgs` | + `Attempt` int, `IsFinal` bool, `RequestSent` bool |
| | `WriteDiscarded` | `WriteDiscardedEventArgs` (`Devinno.Communication`, not nested) | `MessageID` int, `Reason` `WriteDiscardReason` (`NotConnected`, `Disconnected`, `Stopped`) |
| LsCNet | all events below | (common) | `MessageID` int, `Slave` int, `Command` LsCommand (`RSS`, `RSB`, `WSS`, `WSB`) |
| LsFEnet | all events below | (common) | `MessageID` int, `Command` LsCommand (no `Slave`) |
| | `DataReceived` | `DataEventArgs` | + `Devices` string[] (RSS: the listed devices; RSB: only the start device), `Values` long[] (unsigned raw of the size; X 0/1) |
| | `WriteReceived` | `ResultEventArgs` | (common only) |
| | `TimeoutReceived`; CNet only: `BCCErrorReceived` | `AttemptFailedEventArgs` | + `Attempt` int, `IsFinal` bool, `RequestSent` bool |
| | `NAKReceived` | `NAKEventArgs` | + `ErrorCode` int (PLC error code, e.g. CNet 0x1132, FEnet 0x0004) |
| McSerial | all events below | (common) | `MessageID` int, `Slave` int, `Command` McCommand (`BR`, `WR`, `BW`, `WW`) |
| McEthernet | all events below | (common) | `MessageID` int, `Command` McCommand (no `Slave`) |
| | `DataReceived` | `DataEventArgs` | + `Device` string (start device, uppercase), `Values` long[] (BR 0/1 per point; WR raw 0–65535 per word) |
| | `WriteReceived` | `ResultEventArgs` | (common only) |
| | `TimeoutReceived`; McSerial only: `SumCheckErrorReceived` | `AttemptFailedEventArgs` | + `Attempt` int, `IsFinal` bool, `RequestSent` bool |
| | `NAKReceived` | `NAKEventArgs` | + `ErrorCode` int (3E end code e.g. 0xC051; 1E end code; 1C NAK code) |
| S7Ethernet | all events below | (common) | `MessageID` int, `Command` S7Command (`Read`, `ReadBlock`, `Write`, `WriteBlock`) |
| | `DataReceived` (`<Read>`, `ManualRead`) | `DataEventArgs` | + `Addresses` string[], `Values` long[] (unsigned raw per address: bit 0/1, B 0–255, W 0–65535, D 0–4294967295) |
| | `BlockReceived` (`<ReadBlock>`, `ManualReadBlock`) | `BlockEventArgs` | + `Address` string (start byte address), `Data` byte[] (big-endian PLC bytes) |
| | `WriteReceived` (random and block write, incl. `NamedWrite`) | `ResultEventArgs` | (common only) |
| | `TimeoutReceived` | `AttemptFailedEventArgs` | + `Attempt` int, `IsFinal` bool, `RequestSent` bool, `WrittenBytes` int (block write: bytes the PLC accepted in this attempt; otherwise 0) |
| | `NAKReceived` | `NAKEventArgs` | + `ErrorCode` int (header error class×256+code, or item return code e.g. 0x05, 0x0A), `ItemIndex` int (0-based failed item; −1 header error), `WrittenBytes` int (as above) |
| FinsUdp, FinsTcp, FinsSerial | all events below | (common) | `MessageID` int, `Command` FinsCommand (`Read`, `ReadBlock`, `Write`, `WriteBlock`) |
| | `DataReceived` | `DataEventArgs` | + `Addresses` string[] (Read: the listed addresses; ReadBlock: only the start), `Values` long[] (word raw 0–65535, bit 0/1) |
| | `WriteReceived` | `ResultEventArgs` | (common only) |
| | `TimeoutReceived`; FinsSerial only: `FcsErrorReceived` | `AttemptFailedEventArgs` | + `Attempt` int, `IsFinal` bool, `RequestSent` bool, `WrittenCount` int (block write: elements the PLC accepted in this attempt; otherwise 0) |
| | `NAKReceived` | `NAKEventArgs` | + `ErrorCode` int (FINS end code e.g. 0x1103 address range, 0x2102 write-protected), `WrittenCount` int (as above) |

Every PLC master (and TextComm) also has `WriteDiscarded` (the row is shown for Modbus; the args are the same everywhere). The failure events use the nested `AttemptFailedEventArgs`, which derives from the class's `ResultEventArgs`, so a handler written for `ResultEventArgs` still compiles.

Enum namespaces: `ModbusFunction` (`Devinno.Communication.Modbus`), `LsCommand` (`…LS.Data`), `McCommand` (`…Mitsubishi.Data`), `S7Command` (`…Siemens`), `FinsCommand` (`…Omron.Data`). Other classes (slaves, TextComm, MQClient, SECS/GEM): see IntelliSense (`lib/net8.0/Devinno.Communication.xml`).

- Block reads (`<RSB>`, `<WR>`, `<BR>`, FINS `<ReadBlock>`, Modbus `<Auto>`): `Values[i]`/`ReceiveData[i]` is the value of start address + i.
- Random reads (`<RSS>`, S7/FINS `<Read>`): `Values[i]` belongs to the i-th address in the XML list.
- A device error (`NAKReceived`/`ExceptionReceived`) is not retried.
- `TimeoutReceived` has two cases: (1) no response / bad response while connected → raised on **every** attempt (first try and each retry up to `RepeatCount`), then the job is finished; (2) the connection is lost during the request → raised **once**, the job is dropped (no retry), followed by `DeviceClosed`.
- `AttemptFailedEventArgs` (`TimeoutReceived` and the checksum error events `CRCErrorReceived`, `BCCErrorReceived`, `SumCheckErrorReceived`, `FcsErrorReceived`): `Attempt` is 1-based (1 = first try, 2 = first retry, ...); `IsFinal` is true when the job will not be tried again (retries used up, or the connection was lost) — when the last attempt ends in a checksum error, that event is the final one and no `TimeoutReceived` follows; `RequestSent` tells whether this attempt's request was handed to the socket/port. **If `RequestSent` is true on a write, the device may have executed it: read the value back before writing again.** If false, the device did not receive this attempt.
- `WriteDiscarded` ends a write that was never sent (see 7.1.1); it is the only event such a write gets.
- All events, including `DeviceOpened`/`DeviceClosed`, are raised on the worker thread.

### 7.3 Threading

| Do | Don't |
|---|---|
| Keep handlers short: copy `e.Values` into your own state, then return | Block in a handler (sleep, wait for I/O, wait for another event) — polling stops while it runs |
| Marshal to the UI asynchronously: WinForms `control.BeginInvoke(...)`, WPF `Dispatcher.BeginInvoke(...)` / `InvokeAsync(...)` | Touch UI controls directly in a handler (cross-thread exception), or use synchronous `Invoke` (it can stall `Stop()` called from the UI thread) |
| Lock or use thread-safe types for state shared between the handler and other threads | Assume the handler runs on the thread that called `Start()` |
| Call `NamedWrite`/`ManualXxx` from any thread | Call `LoadXml`/`FromXml`/`ApplyConfig` while running |

An exception thrown in a handler is swallowed and the loop continues, so log inside the handler.

WinForms example:

```csharp
using System.Windows.Forms;
using Devinno.Communication.Mitsubishi.Ethernet;

public class MainForm : Form
{
    readonly McEthernet plc = new();
    readonly Label statusLabel = new() { Dock = DockStyle.Top };

    public MainForm()
    {
        Controls.Add(statusLabel);
        plc.LoadXml(Path.Combine(AppContext.BaseDirectory, "plc-line1.xml"));
        plc.DataReceived += (s, e) =>
        {
            if (e.MessageID != 1) return;
            long status = e.Values[0];                                   // worker thread: read the data here
            if (!IsHandleCreated || IsDisposed) return;                  // form not shown yet, or already closed
            try { BeginInvoke(() => statusLabel.Text = $"Status {status}"); }   // UI thread: update the control
            catch (InvalidOperationException) { }                        // handle destroyed between the check and the call
        };
        Load += (s, e) => plc.Start();
        FormClosing += (s, e) => plc.Dispose();
    }
}
```

### 7.4 Values

Conversions the app writes itself (copy as needed):

```csharp
using System.Buffers.Binary;

static class Raw
{
    // signed 16-bit view of a raw word (0..65535)
    public static short Int16(long word) => (short)word;

    // 32-bit from two words, low word at the lower address (Mitsubishi, Omron, many Modbus devices)
    public static int Int32(long low, long high) => (int)(uint)(low | (high << 16));
    public static float Real(long low, long high) => BitConverter.Int32BitsToSingle(Int32(low, high));

    // 32-bit value → two words for NamedWrite(name, values), low word first
    public static long[] Words(int value) => [value & 0xFFFF, (value >> 16) & 0xFFFF];
    public static long[] Words(float value) => Words(BitConverter.SingleToInt32Bits(value));

    // bit n of a word
    public static bool Bit(long word, int n) => (word & (1L << n)) != 0;

    // S7: a D address (DB1.DBD4, MD10) returns the whole 32-bit raw value
    public static int S7Int32(long d) => (int)d;
    public static float S7Real(long d) => BitConverter.Int32BitsToSingle((int)d);
    public static long S7RealToRaw(float value) => BitConverter.SingleToInt32Bits(value);

    // S7 BlockReceived: e.Data is big-endian bytes from the block start byte
    public static ushort S7Word(byte[] data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
    public static int S7DInt(byte[] data, int offset) => BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset));
    public static float S7Real(byte[] data, int offset) => BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(offset));
    public static bool S7Bit(byte[] data, int offset, int bit) => (data[offset] & (1 << bit)) != 0;
}
```

Word order of 32-bit values in Modbus devices varies by vendor; check the device manual. Mitsubishi and Omron put the low word at the lower address. S7 values are big-endian bytes and a `D` address returns the whole 32-bit value.

---

## 8. Protocol pitfalls

| Protocol | Pitfall |
|---|---|
| Modbus | `Start` is 0-based (40001 → 0). FC3 = holding registers (read/write), FC4 = input registers (read-only), FC1 = coils, FC2 = discrete inputs. `<Auto>` accepts only FC1–FC4; `<Write>` only FC5, FC6, FC15, FC16. RTU `<Auto>` Slave is 1–247, RTU `<Write>` Slave 0–247 (0 = broadcast, no reply), TCP 0–255. Manual FC6 / FC16 values outside -32768..65535 throw `ArgumentOutOfRangeException` |
| LS | Device names need `%` and the size letter (`%MW100`, not `M100` or `MW100`). One `<RSS>` mixes only devices with the same size letter. `<RSB>`/block write: no bit (`X`) devices; FEnet block: numeric address only (no dots). CNet `Slave` = station number 0–255 is required on `<RSS>`, `<RSB>` and `<Write>`; FEnet has no `Slave` |
| Mitsubishi | X/Y radix: Q/L (E3) = hex, FX3 (E1) and 1C = octal; **FX5 over 3E is octal → set `XYNotation="Octal"`**. B, W, ZR numbers are hex (`B1F`, `W1A`). `WR`/`WW` on a bit device: 16 points per word, bit 0 = start point. FX3 Ethernet does not support 3E; use `Frame="E1"`. `Frame`/`XYNotation` are applied when a job is registered: Auto jobs when the XML is loaded (with the XML's settings), Manual jobs and `NamedWrite` at the call (`NamedWrite` registers a Manual job, so its device is parsed with the object's current settings). Both statements in IntelliSense are true; just do not change `Frame`/`XYNotation` in code after loading. McSerial `Slave` (station number) 0–31 is required on every `<BR>`, `<WR>`, `<Write>` |
| Siemens S7 | S7-1200/1500: enable "Permit access with PUT/GET communication" in TIA Portal and turn off "Optimized block access" for every DB you read/write. Slot: 1200/1500 = 1 (default), 300 = 2, 400 = 3. Block start must be a byte address (`DB1.DBB0`, not `DB1.DBW0`). `<Read>` max 19 addresses. No T/C/Z, no symbolic names |
| Omron FINS | UDP: `DestinationNode` defaults to the last number of `RemoteIP`, `SourceNode` to the last number of this PC's IP; if the PLC uses static IP conversion, set `LocalPort="9600"`. TCP: `ClientNode` omitted = assigned by the PLC. EM bank is hex 0–18 (`E10_0` = bank 0x10). Bits are `.00`–`.15`. Large blocks are split by `MaxWordsPerRequest` (default 499 = the CP1L-E limit; CJ2 and NJ accept up to 997). NJ/NX: variables must be AT-assigned to CJ memory |
| TextComm | No addresses; `<Auto>` sends a fixed text (`Message` is required, may be `""`). No `<Write>` |
| MQTT | The password is never in the XML; pass it to `Start(clientID, userName, password)`. `AliveTopic=""` turns off the alive check. `Publish` while not connected is not sent: `PublishDiscarded` (`MQPublishDiscardedEventArgs`: `Topic`, `Qos`) is raised on the calling thread. A failure after sending (broker refusal, no acknowledgement) is not reported. A retained command topic is replayed to new subscribers |
| SECS/GEM | The transport's `Role` inside `<GemEquipment>` must be omitted or `Equipment`, inside `<GemHost>` omitted or `Host`. The transport element type must match the constructor argument (`new GemHost(new SecsHsms())` → `<SecsHsms>` child). For messaging, use IntelliSense |

---

## 9. How to verify

1. **Load with the real class.** No exception = the XML is valid for the library (all ranges, device names, required attributes, limits are checked here).
2. **Validate against the XSD.** Catches child order mistakes and type errors the same way an XML editor does. Value ranges are not in the XSD (they are checked in step 1).

```csharp
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Devinno.Communication;
using Devinno.Communication.Mitsubishi.Ethernet;

// 1. Load with the library (strict mode). The message names the element and attribute.
var plc = new McEthernet();
try { plc.LoadXml("plc-line1.xml"); Console.WriteLine("library: OK"); }
catch (FormatException ex) { Console.WriteLine($"library: {ex.Message}"); }

// 2. Validate against the XSD (same content as docs/schemas/McEthernet.xsd).
var schemas = new XmlSchemaSet();
schemas.Add(null, XmlReader.Create(new StringReader(ConfigSchema.Get<McEthernetConfig>())));
int errors = 0;
XDocument.Load("plc-line1.xml").Validate(schemas, (s, e) => { errors++; Console.WriteLine($"xsd: {e.Message}"); });
Console.WriteLine(errors == 0 ? "xsd: OK" : $"xsd: {errors} error(s)");

// 3. Optional: check that every write signal exists.
foreach (var name in new[] { "RunCommand", "SpeedSet", "TargetPos", "Reset", "Recipe" })
    if (!plc.ContainsNamedWrite(name)) Console.WriteLine($"missing <Write Name=\"{name}\">");
```

Loading does not connect to the device, so this check runs without a PLC.

---

## 10. Common mistakes

| Mistake | Result | Fix |
|---|---|---|
| One XML with several PLCs | `FormatException` (unknown element) | One object and one XML per PLC |
| `<Auto Func="FC3" Length="300">` | `FormatException` (Length range 1~125) | Split into ≤125 pieces |
| `<WR Device="D1000" Count="1200">` on 3E | `FormatException` | Split into ≤960 pieces |
| `<Write>` written before the `<WR>` elements | Loads, but XSD validation fails | Auto elements first |
| `Func="fc3"` / `Parity="even"` / `AutoReconnect="True"` | `FormatException` | Exact enum names, lowercase `true`/`false` |
| Modbus Start = 40001 | Reads the wrong registers (or range error) | `Start="0"` with `FC3` |
| LS `Device="MW100"` | `FormatException` | `%MW100` |
| S7 `<ReadBlock Address="DB1.DBW0">` | `FormatException` | `DB1.DBB0` |
| FX5 over 3E with default `XYNotation` | X/Y addresses read the wrong points | `XYNotation="Octal"` |
| Writing right after `Start()` | The connection is not up yet, so the write is discarded (`WriteDiscarded`, `NotConnected`) | Write inside `DeviceOpened`, or when `IsOpen` is true |
| `RepeatCount` on a pulse, counter or incremental write | A retry can apply the write twice | `RepeatCount` 0 (the default); read back before resending |
| Same Id on two Auto elements | Cannot tell the events apart (not an error) | Unique Ids (4.1) |
| `NamedWrite("Recipe", new int[] { … })` | Compile error | `long[]` or `[ … ]` |
| Updating a WinForms control inside `DataReceived` | Cross-thread exception (swallowed, nothing shows) | `BeginInvoke` |
| `LoadXml` after `Start()` | `InvalidOperationException` | `Stop()` → `LoadXml()` → `Start()` |
| A data type attribute on `<Write>` (`Type="Float"`) | `FormatException` | No type attributes exist; convert to raw words in code (7.4) |
