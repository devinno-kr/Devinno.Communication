using System.Net;
using System.Runtime.CompilerServices;
using Devinno.Communication.Modbus.TCP;
using Devinno.Communication.TextComm.TCP;

namespace Devinno.Communication.Tests;

/// 테스트에서 띄우는 TCP Slave는 루프백(127.0.0.1)에만 바인드한다.
/// 모든 인터페이스에 바인드하면 빌드 경로가 바뀔 때마다 Windows 방화벽이 허용 여부를 묻는다.
internal static class LoopbackBinding
{
    [ModuleInitializer]
    internal static void Init()
    {
        ModbusTCPSlave.BindAddress = IPAddress.Loopback;
        TextCommTCPSlave.BindAddress = IPAddress.Loopback;
    }
}

public class LoopbackBindingTests
{
    [Fact]
    public void TcpSlaves_BindToLoopbackInTests()
    {
        Assert.Equal(IPAddress.Loopback, ModbusTCPSlave.BindAddress);
        Assert.Equal(IPAddress.Loopback, TextCommTCPSlave.BindAddress);
        using var slave = new ModbusTCPSlave { LocalPort = 0 };
        slave.Start();
        Assert.Contains(System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(),
            ep => ep.Port == slave.BoundPort && ep.Address.Equals(IPAddress.Loopback));
    }
}
