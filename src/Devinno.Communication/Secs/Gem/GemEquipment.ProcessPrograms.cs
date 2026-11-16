using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

// 레시피 관리(언포맷 Process Program, S7, 6J.5): 켜기 설정, 표준 값 계산, 받기·앱 메서드
public partial class GemEquipment
{
    internal const int MaxProcessProgramLimit = 16_777_215;     // SecsItem.MaxLength

    IGemProcessProgramStore? ppStoreSetting;
    int maxProcessProgramSize = 4_194_304;

    // 아래는 레시피를 켠 실행(Start 때 ProcessProgramStore != null)에서만 쓴다
    bool processPrograms;                       // gate: Start 때 확정
    IGemProcessProgramStore? ppStore;           // 실행 중 저장소(Start 때 ppStoreSetting을 잡아 둠, 바뀌지 않음)
    readonly object ppLock = new();             // 저장소 호출을 하나씩. gate 안에서 잡지 않는다(순서 ppLock → gate)
    string? selectedPpid;                       // gate: 선택된 레시피(PPExecName)

    /// <summary>Where process programs (recipes) are kept (default: null = process programs off). Setting it turns process program management (S7) on at the next <see cref="Start"/>.</summary>
    /// <remarks>
    /// <para>When null, GEM behaves exactly as before: Stream 7 is not a supported stream (S9F3) and the application may handle it with <see cref="RegisterMessage"/>. When set, the standard variables and events PPExecName, PPFormat, PPChangeName, PPChangeStatus, ProcessProgramChange, and ProcessProgramSelected are defined and GEM handles Stream 7, so registering a Stream 7 message makes <see cref="Start"/> fail.</para>
    /// <para>Use <see cref="GemProcessProgramFolder"/> or your own <see cref="IGemProcessProgramStore"/>. A store set in code is not written by <see cref="ToXml"/>; the XML attribute <c>ProcessProgramDirectory</c> creates a <see cref="GemProcessProgramFolder"/>.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public IGemProcessProgramStore? ProcessProgramStore { get => ppStoreSetting; set => Setting(() => ppStoreSetting = value); }

    /// <summary>Upper limit of a process program body (the encoded PPBODY) in bytes, 1 to 16777215 (default: 4194304). A larger S7F1 is answered with PPGNT 5 and a larger S7F3 with ACKC7 2.</summary>
    /// <remarks>Over SECS-I a whole message is limited to about 8 MB, so a larger body is refused by the transport.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">Out of range</exception>
    /// <exception cref="InvalidOperationException">Changed while running</exception>
    public int MaxProcessProgramSize { get => maxProcessProgramSize; set => Setting(() => maxProcessProgramSize = Check(value, 1, MaxProcessProgramLimit, nameof(MaxProcessProgramSize))); }

    partial void AddProcessProgramHandlers()
    {
        standardValues[GemStandard.PPExecName] = () => SecsItem.A(selectedPpid ?? "");
        standardValues[GemStandard.PPFormat] = () => SecsItem.U1(1);
    }

    partial void ResetProcessPrograms() => selectedPpid = null;
}
