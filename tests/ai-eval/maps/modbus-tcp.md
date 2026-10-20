# Memory map: Variable frequency drive with Modbus/TCP gateway

Device connection details:

- Modbus/TCP, device IP address: 192.168.5.100
- TCP port: 1502
- Unit ID (slave address): 3
- All other communication parameters: defaults
- Addresses use the common 5-digit Modicon notation of the drive manual: first digit = table (0 = coils, 1 = discrete inputs, 3 = input registers, 4 = holding registers), the remaining 4 digits = 1-based number in that table (so 40001 is the first holding register).
- 32-bit (dword) values occupy two consecutive registers; the low word is at the first (lower) address.

Use the Signal name as the name of the write definition for every signal marked W.

| Signal name | Address | R/W | Type | Note |
|---|---|---|---|---|
| DriveStatus | 40001 | R | word | |
| OutputFreq | 40002 | R | word | 0.01 Hz |
| MotorCurrent | 40003 | R | word | 0.1 A |
| FaultCode | 40004 | R | word | |
| EnergyTotal | 40005 | R | dword | 40005-40006, Wh |
| ParameterTable | 40101 | R | word | 40101-40400, 300 registers |
| HeatsinkTemp | 40500 | R | word |  |
| RunHours | 40750 | R | dword | 40750-40751 |
| InputVoltage | 30001 | R | word | input register |
| DcBusVoltage | 30002 | R | word | input register |
| MotorTorque | 30010 | R | word | input register |
| DiStart | 10001 | R | bit | discrete input |
| DiStop | 10002 | R | bit | discrete input |
| DiFault | 10003 | R | bit | discrete input |
| DiReady | 10004 | R | bit | discrete input |
| RunActive | 00001 | R | bit | coil status |
| DirectionFwd | 00002 | R | bit | coil status |
| FaultActive | 00003 | R | bit | coil status |
| FanOn | 00050 | R | bit | coil status |
| SpeedRef | 40201 | W | word | 0.01 Hz |
| AccelTime | 40202 | W | word | 0.1 s |
| TorqueLimit | 40203 | W | word | percent |
| PositionTarget | 40210 | W | dword | 40210-40211, signed |
| Recipe | 40600 | W | word | 40600-40609, 10 registers |
| RunCmd | 00101 | W | bit | coil |
| ResetFault | 00102 | W | bit | coil |
| JogEnable | 00103 | W | bit | coil |
