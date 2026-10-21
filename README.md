# Devinno.Communication

One .NET 8 library for industrial PLC and device communication protocols
(Modbus, LS Electric, Mitsubishi, Siemens, Omron, MQTT, SECS/GEM)

> **Under development (0.x)**: the API may change between versions.

## Install

```bash
dotnet add package Devinno.Communication
```

## Getting started

Poll holding registers 0 to 9 of a Modbus TCP device continuously, and write one value to address 100 once.

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

The other Master classes follow the same flow: create, attach events, register `Auto...` / `Manual...` jobs, `Start()`.

## Features

  * 9 protocol families, with Master/Slave and Host/Equipment roles
  * **Auto** (repeated polling) and **Manual** (one-shot) job queues; automatic reconnect when the connection is lost
  * Save and load the communication settings and the schedule as XML; XSD schemas generated from the code
  * Named write: define a write once in the XML with a name, then call it from code by that name
  * Usable from WinForms, WPF, Skia+OpenTK and ASP.NET Core services
  * English IntelliSense (XML documentation comments on every public API) with Korean (ko) localization

## Supported protocols

| Family | Class | Transport | Role |
|---|---|---|---|
| Modbus | `ModbusRTUMaster` / `ModbusRTUSlave` | Serial | Master / Slave |
| Modbus | `ModbusTCPMaster` / `ModbusTCPSlave` | TCP | Master / Slave |
| LS Electric PLC | `LsCNet` (XGT, RSS/RSB/WSS/WSB) | Serial | Master |
| LS Electric PLC | `LsFEnet` (XGT, TCP 2004) | TCP | Master |
| Mitsubishi PLC | `McSerial` (MC 1C format 1 and 4, BR/WR/BW/WW) | Serial | Master |
| Mitsubishi PLC | `McEthernet` (MC 3E/1E binary, BR/WR/BW/WW) | TCP | Master |
| Siemens S7 | `S7Ethernet` (S7comm, ISO-on-TCP 102) | TCP | Master |
| Omron PLC | `FinsUdp` / `FinsTcp` (FINS 9600) | UDP / TCP | Master |
| Omron PLC | `FinsSerial` (Host Link FINS) | Serial | Master |
| TextComm | `TextCommRTUMaster` / `TextCommRTUSlave` | Serial | Master / Slave |
| TextComm | `TextCommTCPMaster` / `TextCommTCPSlave` | TCP | Master / Slave |
| MQTT | `MQClient` (MQTTnet wrapper) | TCP | Client |
| SECS/GEM | `SecsHsms` (HSMS-SS, SEMI E37) | TCP | Host / Equipment |
| SECS/GEM | `SecsSerial` (SECS-I, SEMI E4) | Serial | Host / Equipment |
| SECS/GEM | `GemMessages` (stateless GEM message builders/parsers) | — | Host / Equipment |
| SECS/GEM | `GemEquipment` (SEMI E30, equipment side) | HSMS / SECS-I | Equipment |
| SECS/GEM | `GemHost` (GEM host side) | HSMS / SECS-I | Host |

SECS/GEM has three layers: transport (`SecsHsms`/`SecsSerial`), stateless messaging (`GemMessages`), and GEM modules (`GemEquipment`/`GemHost`).

* Modbus functions: FC1, FC2, FC3, FC4, FC5, FC6, FC15, FC16, FC26
* **S7-1200/1500 need "PUT/GET" access permitted and "Optimized block access" turned off in the PLC.**

## Key concepts

  * **Schedule**: Auto jobs are registered once and polled repeatedly. Manual jobs run once and are processed before the Auto list. Responses and errors (timeout, sum check, NAK) arrive as events, and you tell them apart by the message ID you gave when you registered the job.
  * **Threading**: events are raised on the library's worker thread. Keep handlers short, and marshal UI updates yourself (WinForms `BeginInvoke`, WPF `Dispatcher.BeginInvoke`, and so on).
  * **Connection events**: the Master classes raise `DeviceOpened` / `DeviceClosed` (`MQClient`: `Connected` / `Disconnected`); reconnect is automatic unless turned off.
  * **XML configuration**: every class has `SaveXml` / `LoadXml` and a configuration object (`...Config`, for example `ModbusTCPMasterConfig`). `ConfigSchema.Get<T>()` returns the XSD. One instance is one XML file, and the root element name is the class name. Loading is strict mode: an unknown attribute or element throws `FormatException`. Load only while stopped.

### Write safety

  * By default, a write made while there is no connection is not sent: it is reported through the `WriteDiscarded` event instead (`NotConnected`). Writes still pending when the connection is lost are discarded too (`Disconnected`).
  * `Stop()` always empties the pending Manual writes (`WriteDiscarded`, `Stopped`).
  * Set `DiscardWritesWhileDisconnected="false"` (property or XML attribute) to restore the old behaviour of keeping writes queued until a connection exists.
  * Failure events (`TimeoutReceived` and the checksum error events) carry `Attempt`, `IsFinal` and `RequestSent`. If `RequestSent` is true, the device may already have executed the request: read it back before writing again.
  * Every write ends in exactly one of: a success event, a NAK or exception event, a failure event with `IsFinal` true, or `WriteDiscarded`.
  * Do not use `RepeatCount` for pulse, counter or incremental writes: a retry resends a write the device may already have executed.
  * Modbus FC6 / FC16 Manual writes throw `ArgumentOutOfRangeException` for a value outside -32768..65535.
  * S7 and FINS block writes are not atomic: when one fails part-way, `WrittenBytes` (S7) / `WrittenCount` (FINS) tell how much was already written.
  * An FX5 over 3E uses octal X/Y numbers: set `XYNotation="Octal"`; with `Auto` they are read as hexadecimal and address the wrong points.
  * For the TextComm masters every `ManualSend` counts as a write (the library does not know what a command means), so it is also discarded while there is no connection.
  * Write right after the connection is up (inside `DeviceOpened`), not right after `Start()`.

### XML configuration and named write

The XML holds the connection settings, the Auto (polling) list and a list of named writes. Values are never stored in it.

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

Named write is available on the 10 PLC masters: `ModbusRTUMaster`, `ModbusTCPMaster`, `LsCNet`, `LsFEnet`, `McSerial`, `McEthernet`, `S7Ethernet`, `FinsUdp`, `FinsTcp` and `FinsSerial`. The result arrives as an event whose message ID is the `Id` of the `<Write>` element.

## Limitations and verification status

  * Siemens S7: absolute addresses only. Symbols, optimized DBs, timers and counters are not supported.
  * Omron FINS: C-mode Host Link, CIP tags, timers and counters are not supported.
  * GEM equipment: trace, limits and recipes (S7) are not implemented yet. Spooling (SEMI E30 7.12) is available but off by default (`GemEquipment.SpoolDirectory`).
  * GEM equipment and host: not checked against the original SEMI E30 text; only interoperability with secsgem was verified. Not tested with a real MES or equipment.

## Requirements

  * .NET 8.0
  * Dependencies: [System.IO.Ports](https://www.nuget.org/packages/System.IO.Ports), [MQTTnet](https://www.nuget.org/packages/MQTTnet/5.2.0.1603) 5.2.0.1603

## Documentation

The package contains these documents in its `docs` folder:

  * the package's `docs/llms.txt` (after a build: `.devinno/Devinno.Communication/llms.txt`): index of the documents, read it first
  * the package's `docs/llm-rules.md` (after a build: `.devinno/Devinno.Communication/llm-rules.md`): rules, which class to use, memory map to XML procedure
  * the package's `docs/comm-xml-reference.md` (after a build: `.devinno/Devinno.Communication/comm-xml-reference.md`): every XML root element, attribute, default and range
  * the package's `docs/schemas/*.xsd` (after a build: `.devinno/Devinno.Communication/schemas/*.xsd`): exact schema of each root element
  * C# API: XML documentation comments on every public API (IntelliSense; `Devinno.Communication.xml` in the package)

## Documentation for AI tools

After one `dotnet build` of an SDK-style project, the package copies its docs to `.devinno/Devinno.Communication/` (a restore alone does not). Add this line to your AI instruction file (`CLAUDE.md`, `AGENTS.md`, ...):

```
Devinno library docs: read .devinno/*/llms.txt first.
```

  * To opt out of the copy, set `<DevinnoCopyDocs>false</DevinnoCopyDocs>` in the project file.
  * Add `.devinno/` to your `.gitignore`.
  * Fallback location: `%UserProfile%\.nuget\packages\devinno.communication\<version>\docs\`.

## Disclaimer

Devinno.Communication is provided "as is", without warranty of any kind (see LICENSE). It is a communication library and is not designed or certified for safety functions. Emergency stops, interlocks and other safety functions must be implemented in safety-rated equipment, not through this library. Writing to a PLC or equipment changes the physical behaviour of a machine: validate addresses, values and timing on a simulator or test equipment before connecting to a live machine. You are responsible for testing and validating any system you build with it.

## License

MIT license.
