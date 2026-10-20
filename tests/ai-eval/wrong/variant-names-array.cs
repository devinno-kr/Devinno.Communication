using Devinno.Communication.Mitsubishi.Ethernet;

var plc = new McEthernet();
plc.LoadXml("mitsubishi-3e.xml");

var trend = new long[1200];

// Events run on the worker thread; keep handlers short.
plc.DataReceived += (s, e) =>
{
    switch (e.MessageID)
    {
        case 1:   // D100..D115
            long lineStatus = e.Values[0];
            short alarmCode = (short)e.Values[1];
            uint actualSpeed = (uint)(e.Values[2] | (e.Values[3] << 16));
            Console.WriteLine($"LineStatus={lineStatus} AlarmCode={alarmCode} ActualSpeed={actualSpeed}");
            break;
        case 2: Array.Copy(e.Values, 0, trend, 0, 960); break;     // D2000..D2959
        case 3: Array.Copy(e.Values, 0, trend, 960, 240); break;   // D2960..D3199
        case 4: Console.WriteLine($"SpindleLoad={e.Values[0]} ToolWear={e.Values[120]}"); break;
        case 7: Console.WriteLine($"MotorRun={e.Values[0] == 1} CoolantLow={e.Values[512] == 1}"); break;
        case 8: Console.WriteLine($"PartPresent={e.Values[0] == 1} SafetyRelayOk={e.Values[15] == 1}"); break;
    }
};
plc.WriteReceived += (s, e) => Console.WriteLine($"write {e.MessageID} done");
plc.NAKReceived += (s, e) => Console.WriteLine($"PLC error, id {e.MessageID}: 0x{e.ErrorCode:X4}");
plc.TimeoutReceived += (s, e) => Console.WriteLine($"no response, id {e.MessageID}");

plc.Start();

foreach (var name in new[] { "RunCommand", "SpeedSet", "ProductSelect", "StartPb", "ResetAlarm", "JogForward" })
    plc.NamedWrite(name, 1);
plc.NamedWrite("TempSetpoint", [2150, 0]);
plc.NamedWrite("BatchTarget", [500, 0]);
plc.NamedWrite("Recipe", new long[20]);

Console.ReadLine();
plc.Stop();
plc.Dispose();
