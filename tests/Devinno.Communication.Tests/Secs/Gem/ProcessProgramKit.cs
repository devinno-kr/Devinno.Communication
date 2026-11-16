using System.Collections.Concurrent;
using Devinno.Communication.Secs.Gem;

namespace Devinno.Communication.Tests.Secs.Gem;

/// 테스트용 메모리 레시피 저장소: 넣은 순서를 지키고, 호출을 기록하며, 부를 때마다 훅(지연·예외·스레드 검사)을 부른다
internal sealed class InMemoryProcessProgramStore : IGemProcessProgramStore
{
    readonly List<GemProcessProgram> items = [];

    /// 호출 기록("GetIds", "Read R1", "Write R1", "Delete R1")
    public ConcurrentQueue<string> Calls { get; } = new();

    /// 메서드마다 들어가기 직전에 부른다(인자: 기록 문자열). 예외를 던지면 그 호출이 실패한다
    public Action<string>? Hook { get; set; }

    void Enter(string call)
    {
        Calls.Enqueue(call);
        Hook?.Invoke(call);
    }

    public IReadOnlyList<string> GetIds()
    {
        Enter("GetIds");
        lock (items) return [.. items.Select(p => p.Ppid)];
    }

    public GemProcessProgram? Read(string ppid)
    {
        Enter("Read " + ppid);
        lock (items) return items.FirstOrDefault(p => p.Ppid == ppid);
    }

    public void Write(GemProcessProgram program)
    {
        Enter("Write " + program.Ppid);
        lock (items)
        {
            var i = items.FindIndex(p => p.Ppid == program.Ppid);
            if (i >= 0) items[i] = program;
            else items.Add(program);
        }
    }

    public bool Delete(string ppid)
    {
        Enter("Delete " + ppid);
        lock (items) return items.RemoveAll(p => p.Ppid == ppid) > 0;
    }

    /// 훅·기록 없이 바로 넣기(준비용)
    public void Put(GemProcessProgram program)
    {
        lock (items)
        {
            items.RemoveAll(p => p.Ppid == program.Ppid);
            items.Add(program);
        }
    }

    /// 훅·기록 없이 바로 읽기(확인용)
    public GemProcessProgram? Peek(string ppid)
    {
        lock (items) return items.FirstOrDefault(p => p.Ppid == ppid);
    }

    public int Count { get { lock (items) return items.Count; } }
}
