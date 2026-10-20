# AI evaluation kit

Checks whether an AI agent that has **only the package docs** can turn a memory map into a correct Devinno.Communication XML and C# program.

| Folder | Content |
|---|---|
| `maps/` | Three synthetic memory maps (`mitsubishi-3e`, `modbus-tcp`, `siemens-s7`). The target class is not named; the agent must choose `McEthernet` (Frame E3), `ModbusTCPMaster`, `S7Ethernet`. |
| `expected/` | Grading data per map (class, connection values, request limits, every read signal as area/start/count, every write signal). **Never give this folder to the agent.** |
| `Grader/` | Console project (references the library project; not in the solution). |
| `reference/` | Human-written correct answers (XML for all three maps, C# for `mitsubishi-3e`). Used to prove the Grader passes correct answers. |
| `wrong/` | Deliberately wrong answers used to prove the Grader fails (missing write, over-limit read, wrong root, C# without Start/NamedWrite, C# that does not compile). |
| `PROMPT.md` | The prompt template for the agent. |

## Run an evaluation

1. Prepare the docs folder the agent will see: copy the `docs/` folder of the package (or of the repo build output: `llms.txt`, `llm-rules.md`, `comm-xml-reference.md`, `schemas/`) to a fresh empty directory, for example `C:\eval\docs`. Optionally add `Devinno.Communication.xml` from `lib/net8.0/`. To test the real package: `dotnet build -c Release` then `dotnet pack src/Devinno.Communication -c Release --no-build -o out`, unzip `out/*.nupkg`, take `docs/` and `lib/net8.0/Devinno.Communication.xml`.
2. Start a fresh agent (no repo access, no prior context) whose file access is limited to that folder.
3. Take `PROMPT.md`, fill in `<docs-path>`, `<map-name>` (`mitsubishi-3e`, `modbus-tcp` or `siemens-s7`) and paste the content of `maps/<map-name>.md` in place of `<MAP-CONTENT>`. Give the agent only this text.
4. Save the agent's two outputs as `<map-name>.xml` and `<map-name>.cs`.
5. Grade (from the repository root):

   ```
   dotnet run --project tests/ai-eval/Grader -- <map-name> <path-to>/<map-name>.xml <path-to>/<map-name>.cs
   ```

   The C# argument is optional (without it only the XML checks run). The first run builds the library; building the C# (check 7.9) takes a few seconds more.
6. Read the output: one `PASS`/`FAIL` line per check, then `N/M checks passed`. Exit code 0 only if every check passed. A FAIL points to a place where the docs were missing, unclear or misleading; fix the docs, not the grader, and repeat with a fresh agent.

Repeat for all three maps. Record the scores per map, per agent and per docs version.

## What the Grader checks

1. Root element is the expected class and `FromXml` of that class succeeds (strict library loader).
2. The XML validates against `schemas/<Root>.xsd`.
3. Coverage and limits: every read signal lies inside the union of read entries of the right area/function (and slave, for Modbus); a range split into several entries is accepted. Every read entry respects its request limit (MC 3E `WR` 960 / `BR` 3584, Modbus FC1/FC2 2000 and FC3/FC4 125, S7 `Read` 19 addresses; S7 `ReadBlock` is split by the library). S7 coverage is byte based: every byte of a signal must lie in a `<ReadBlock>` of the area or in a `B`/`W`/`D` item of a `<Read>` (a bit signal may also be covered by a `<Read>` of exactly its bit address).
4. Every write signal has a `<Write>` with the exact `Name` from the map, the right address (device/address text is normalized like the library does: case-insensitive, hex/octal radix of the device) and, for Modbus, an accepted function code (single word: FC6 or FC16; single bit: FC5 or FC15; dword and recipe block: FC16 only).
5. No duplicate `Id` across read and `<Write>` entries.
6. Connection values (IP, port, rack/slot, frame) match; omitted attributes count as the library default.
7. With a C# file (text checks on the code with comments removed, string literals kept; the checks follow the variable of the expected class type, whatever way it is declared: `var x = new Cls()`, `Cls x = new();`, a field):
   - 7.1 a variable of the expected class type is created; 7.2 `LoadXml`/`FromXml` is called on it; 7.3 a read-data event of it is subscribed (`DataReceived`/`BlockReceived`/`WordReadReceived`/`BitReadReceived`); 7.4 `Start()` is called on it; 7.5 the subscription comes before `Start()`.
   - 7.6 at least one `NamedWrite(` call exists; 7.7 every literal first argument of `NamedWrite` is a `<Write Name>` of the XML.
   - 7.8 every write signal name appears somewhere in the code as a string literal (a names array, a constant or a direct call all count), or some `NamedWrite` call takes a non-literal name (then the name list is assumed to be built at run time).
   - 7.9 `dotnet build` (timeout 5 minutes) of a temporary console project that references the library succeeds with no errors; the project is deleted afterwards.

The Grader is a regression and scoring aid, not a proof of correctness: it does not simulate a PLC. Limits of the checks: the C# static checks are text based (comments are ignored), and values written by the code are not checked.

## Known synthetic-data choices

- All addresses, names and IPs are made up. Maps contain a read range larger than one request limit, scattered single signals, a multi-word recipe block, 32-bit values, bits and (Modbus) coils/discrete inputs/input registers/holding registers.
- To add a map: add `maps/<name>.md`, `expected/<name>.json` (the `root` must be one of `McEthernet`, `ModbusTCPMaster`, `S7Ethernet`; other classes need a new protocol adapter in `Grader/Program.cs`), and a reference XML under `reference/`.
