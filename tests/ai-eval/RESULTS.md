# AI evaluation results

## Method

- Fresh AI agents with no repository access and no prior context. Each agent's only file access was a copy of the package docs folder (`llms.txt`, `llm-rules.md`, `comm-xml-reference.md`, `schemas/`, as described in `PROMPT.md`). The English IntelliSense XML (`lib/net8.0/Devinno.Communication.xml`) was in that folder too: it was extracted from the package together with `docs/` and `README.md` for both rounds.
- One agent per memory map (`maps/mitsubishi-3e.md`, `maps/modbus-tcp.md`, `maps/siemens-s7.md`). The prompt was `PROMPT.md` with the map pasted in. Each agent produced `<map>.xml` and `<map>.cs`.
- Each pair was graded with `Grader` (see `README.md`):
  - the library's `FromXml` loader and the XSD;
  - coverage and per-request limits against `expected/<map>.json`;
  - the `<Write>` names, addresses and functions;
  - Id uniqueness and connection values;
  - text checks of the C# and a build of the C#.
- The score is the number of passed Grader checks / the number of checks.

## Rounds

| Round | Agent model | Docs at commit | mitsubishi-3e | modbus-tcp | siemens-s7 |
|---|---|---|---|---|---|
| 1 | Claude Sonnet | cbf19c1 | 58/58 PASS | 55/55 PASS | 56/56 PASS |
| 2 | Claude Haiku | 6d44d08 | 57/57 PASS | 55/55 PASS | 56/56 PASS |

(The number of checks per map depends on the agent's answer, for example how many read entries it made.)

## Round 1 feedback → changes (commit 6d44d08)

All agents passed, but they reported these gaps in the docs. All were fixed:

| # | Gap reported | Change |
|---|---|---|
| 1 | Event list incomplete; argument member names/types unclear | `llm-rules` §7.2: one table per PLC master listing event → nested args type → members with types (taken from the compiled assembly) |
| 2 | `NamedWrite` overloads not listed | §5.1: the three signatures; `int` widens to `long`; `[a, b]` binds to the list overload |
| 3 | No Modbus C# example | §6.2: Modbus TCP worked example (125-word split, coils, FC5/FC6/FC16, 32-bit pair) |
| 4 | XSD lists enum values the loader rejects, while the docs said "XSD wins" | The XSD is authoritative for names, structure and types; the loader is stricter. Table of the stricter cases (Modbus `Func`, `StopBits="None"`, `GrantedFailure`, `AttemptOnline`, transport `Role`, ranges) |
| 5 | `<Write>` `Name` rules only in llm-rules | Rules added to every `<Write>` table in the reference |
| 6 | Unclear what happens to writes while not connected | §7.1.1, verified in code and at runtime: the write is queued and sent first after `DeviceOpened`; a request cut off by a lost connection raises `TimeoutReceived` once and is dropped |
| 7 | Stop vs Dispose | `Dispose()` alone is enough; examples made consistent |
| 8 | Relative `LoadXml` path | Resolved against the current directory; use `Path.Combine(AppContext.BaseDirectory, …)` and copy the XML to the output folder |
| 9 | No concrete gap read-through rule | Rule given: merge only if the result fits one request and the gap addresses exist. Notes per protocol; Modbus is device-dependent |
| 10 | Mitsubishi `<BR>` indexing | Count is in points, `Values[i]` = start + i in the device radix (X10…X1F hex) |
| 11 | S7 write limits vs `NamedWrite` | DBD/MD = one value. Random write: 12 values / 11 dwords. List write: block of 1–65535 bytes |
| 12 | IntelliSense XML skipped | `llms.txt` says it is part of the package and should be read |
| 13 | Device-code list in the wrong section | Moved back under §4.3 |

## Round 2 feedback

Fixed in the final doc round (commit `docs: AI 검수 2회차 반영·검수 결과(RESULTS.md)·스펙 9A 구현 결정`):

| # | Gap reported | Change |
|---|---|---|
| A | `ModbusFunction` member names (`WORDREAD_F3`) vs XML names (`FC3`) | Mapping table in `llm-rules` §4.5 and reference §1 |
| B | Modbus args inheritance | Stated that every args type of a class derives from its `ResultEventArgs` (checked by reflection: 55 events, 0 exceptions) |
| C | S7 IntelliSense said "up to 20 … in practice 19" | English `///` and Korean `ko/` XML now say "at most 19 addresses (20 rejected)" and "at most 12 values / 11 dwords" |
| D | `TimeoutReceived` cases | Both cases spelled out: every attempt for a normal timeout; once (then dropped) for a lost connection |
| E | csproj item for copying the XML | The `<ItemGroup><None Update="plc.xml" CopyToOutputDirectory="PreserveNewest" /></ItemGroup>` snippet added to §7.1 |
| F | Mitsubishi "applied at registration" vs `NamedWrite` "at call time" | Both are true: Auto jobs are registered at load, `NamedWrite` registers a Manual job at the call |

Recorded only (needs hardware, vendor manuals or a user decision):

| # | Item |
|---|---|
| G1 | iQ-R CPUs with the 3E subcommands 0000/0001: compatibility and limits not checked |
| G2 | How to learn the PLC's configured area sizes (needed for safe gap merging) — from the PLC project, not the library |
| G3 | Momentary-bit (pulse) write semantics: the library writes one value; set/reset timing is the app's job |
| G4 | Signedness and units are often missing from memory maps — app/user decision |
| G5 | Korean `///` of internal members appears in the IntelliSense XML (harmless for an IDE, noise for an AI reader) |

## Limits of this evaluation

- The Grader's C# checks are text-based (patterns on the code with comments removed) plus a build of the code by the Grader. The agents themselves could not build or run their code.
- No real devices: the XML and C# were checked against the library and the expected data, not against a PLC.
- Only three vendor maps (Mitsubishi 3E, Modbus TCP, Siemens S7). LS, Omron, the serial classes, TextComm, MQTT and SECS/GEM were not evaluated.
- One agent per map per round. The round-2 score shows the docs also work for a smaller model, not that every answer is optimal (the Grader accepts any correct split or merge).
