using System.IO.Ports;

namespace Devinno.Communication.Secs.Serial;

// SECS-I 회선(바이트 단위 읽기·쓰기). 실제로는 SerialPort이고, 테스트는 메모리 회선 쌍을 넣는다(SecsSerial.OpenLine).
// readByte(timeoutMs): 바이트 하나(0~255), 시간 안에 없으면 -1. 회선이 닫히면 IOException·ObjectDisposedException 등.
internal sealed class SecsSerialLine(Func<int, int> readByte, Action<byte[]> write, Action close)
{
    public int ReadByte(int timeoutMs) => readByte(Math.Max(1, timeoutMs));
    public void Write(byte[] bytes) => write(bytes);
    public void Write(byte b) => write([b]);
    public void Close() => close();

    // 열린 SerialPort를 회선으로
    public static SecsSerialLine From(SerialPort sp) => new(
        timeout =>
        {
            sp.ReadTimeout = timeout;
            try { return sp.ReadByte(); }
            catch (TimeoutException) { return -1; }
        },
        bytes => sp.Write(bytes, 0, bytes.Length),
        () =>
        {
            try { sp.Close(); } catch (Exception) { }
            sp.Dispose();
        });
}
