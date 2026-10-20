using System.Diagnostics;

namespace Devinno.Communication.Tests;

internal static class TestUtil
{
    /// 조건이 참이 될 때까지 기다린다. 시간 안에 참이 안 되면 실패.
    public static async Task Until(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) Assert.Fail($"조건이 {timeoutMs}ms 안에 충족되지 않았습니다.");
            await Task.Delay(10);
        }
    }
}
