# Memory map: Packaging machine (Siemens S7-300)

PLC connection details:

- CPU 315-2 PN/DP, IP address 10.20.30.40
- Rack 0, Slot 2
- S7 communication over TCP (ISO-on-TCP, default port 102)
- All other communication parameters: defaults
- DB10, DB20 and DB101-DB116 are non-optimized (standard access) data blocks; PUT/GET access is permitted.
- Byte order is big-endian (Siemens standard). REAL is a 32-bit IEEE float, DINT a 32-bit integer.

Use the Signal name as the name of the write definition for every signal marked W.

| Signal name | Address | R/W | Type | Note |
|---|---|---|---|---|
| Status | DB10.DBW0 | R | word | |
| Temperature | DB10.DBD2 | R | dword | REAL, degC |
| Running | DB10.DBX6.0 | R | bit | |
| Alarm | DB10.DBX6.1 | R | bit | |
| ProductCount | DB10.DBD8 | R | dword | DINT |
| TrendBuffer | DB10.DBW20 | R | word | DB10.DBW20-DB10.DBW1218, 600 words (1200 bytes) |
| Zone1Temp | DB101.DBD0 | R | dword | REAL (one DB per zone) |
| Zone2Temp | DB102.DBD0 | R | dword | REAL |
| Zone3Temp | DB103.DBD0 | R | dword | REAL |
| Zone4Temp | DB104.DBD0 | R | dword | REAL |
| Zone5Temp | DB105.DBD0 | R | dword | REAL |
| Zone6Temp | DB106.DBD0 | R | dword | REAL |
| Zone7Temp | DB107.DBD0 | R | dword | REAL |
| Zone8Temp | DB108.DBD0 | R | dword | REAL |
| Zone9Temp | DB109.DBD0 | R | dword | REAL |
| Zone10Temp | DB110.DBD0 | R | dword | REAL |
| Zone11Temp | DB111.DBD0 | R | dword | REAL |
| Zone12Temp | DB112.DBD0 | R | dword | REAL |
| Zone13Temp | DB113.DBD0 | R | dword | REAL |
| Zone14Temp | DB114.DBD0 | R | dword | REAL |
| Zone15Temp | DB115.DBD0 | R | dword | REAL |
| Zone16Temp | DB116.DBD0 | R | dword | REAL |
| Inputs0 | IB0 | R | byte | process image inputs |
| Inputs1 | IB1 | R | byte | process image inputs |
| PressureRaw | MW20 | R | word | merker word |
| FlowRaw | MW22 | R | word | merker word |
| SpeedSet | DB20.DBW0 | W | word | |
| SetTemp | DB20.DBD2 | W | dword | REAL, degC |
| StartCmd | DB20.DBX6.0 | W | bit | |
| StopCmd | DB20.DBX6.1 | W | bit | |
| ModeSelect | DB20.DBW8 | W | word | |
| Recipe | DB20.DBW10 | W | word | DB20.DBW10-DB20.DBW48, 20 words |
| BatchTarget | DB20.DBD50 | W | dword | DINT |
| ResetAlarm | M30.0 | W | bit | |
