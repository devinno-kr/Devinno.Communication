# Memory map: Press line controller (Mitsubishi)

PLC connection details:

- CPU: MELSEC iQ-R / Q03UDE class CPU with built-in Ethernet port
- IP address: 192.168.1.50
- Protocol: MC protocol (SLMP-compatible), **binary, 3E frame**, over TCP
- TCP port (open setting on the PLC): 5002
- All other communication parameters: defaults
- Address notation below follows the PLC's own convention (X and Y numbers are hexadecimal on this CPU, D and M are decimal).
- 32-bit (dword) values occupy two consecutive words; the low word is at the first (lower) address.

Use the Signal name as the name of the write definition for every signal marked W.

| Signal name | Address | R/W | Type | Note |
|---|---|---|---|---|
| LineStatus | D100 | R | word | 0=Stop, 1=Run, 2=Hold |
| AlarmCode | D101 | R | word | |
| ActualSpeed | D102 | R | dword | D102-D103, rpm x10 |
| ActualTemp | D104 | R | dword | D104-D105, 0.1 degC, signed |
| Pressure | D106 | R | word | 0.01 MPa |
| BatchCount | D108 | R | word | |
| ProductCount | D110 | R | dword | D110-D111 |
| RejectCount | D112 | R | word | |
| OperatorId | D114 | R | word | |
| TrendLog | D2000 | R | word | D2000-D3199, 1200 words |
| SpindleLoad | D5000 | R | word |  |
| ToolWear | D5120 | R | word |  |
| CycleTime | D7500 | R | dword | D7500-D7501, ms |
| EnergyKwh | D8100 | R | dword | D8100-D8101 |
| MotorRun | M0 | R | bit | |
| AlarmActive | M1 | R | bit | |
| DoorOpen | M2 | R | bit | |
| EStopActive | M3 | R | bit | |
| AutoMode | M10 | R | bit | |
| ServoReady | M200 | R | bit |  |
| CoolantLow | M512 | R | bit |  |
| PartPresent | X10 | R | bit | hex X10 (= 16 decimal) |
| SafetyRelayOk | X1F | R | bit | hex X1F (= 31 decimal) |
| RunCommand | D200 | W | word | 1=Start, 2=Stop |
| SpeedSet | D201 | W | word | rpm |
| TempSetpoint | D202 | W | dword | D202-D203, 0.1 degC |
| ProductSelect | D204 | W | word | |
| BatchTarget | D400 | W | dword | D400-D401 |
| Recipe | D300 | W | word | D300-D319, 20 words |
| StartPb | M100 | W | bit | |
| ResetAlarm | M101 | W | bit | |
| JogForward | M110 | W | bit | |
