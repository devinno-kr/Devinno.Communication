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
        ModbusTCPSlave.DefaultLocalIP = "127.0.0.1";
        TextCommTCPSlave.DefaultLocalIP = "127.0.0.1";
    }
}

public class LoopbackBindingTests
{
    [Fact]
    public void TcpSlaves_BindToLoopbackInTests()
    {
        Assert.Equal("127.0.0.1", new ModbusTCPSlave().LocalIP);
        Assert.Equal("127.0.0.1", new TextCommTCPSlave().LocalIP);
        using var slave = new ModbusTCPSlave { LocalPort = 0 };
        slave.Start();
        Assert.Contains(System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(),
            ep => ep.Port == slave.BoundPort && ep.Address.Equals(IPAddress.Loopback));
    }

    [Fact]
    public void TextCommSlave_LocalIP_IsUsedForBinding()
    {
        using var slave = new TextCommTCPSlave { LocalIP = "127.0.0.1", LocalPort = 0 };
        slave.Start();
        Assert.Contains(System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(),
            ep => ep.Port == slave.BoundPort && ep.Address.Equals(IPAddress.Loopback));
    }

    [Fact]
    public void TcpSlaves_InvalidLocalIP_StartThrows()
    {
        using var m = new ModbusTCPSlave { LocalIP = "abc", LocalPort = 0 };
        Assert.Throws<FormatException>(m.Start);
        Assert.False(m.IsStart);
        using var t = new TextCommTCPSlave { LocalIP = "abc", LocalPort = 0 };
        Assert.Throws<FormatException>(t.Start);
        Assert.False(t.IsStart);
    }
}
