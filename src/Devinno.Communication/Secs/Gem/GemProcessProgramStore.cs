using System.Text;
using Devinno.Communication.Secs.Data;

namespace Devinno.Communication.Secs.Gem;

/// <summary>Where <c>GemEquipment</c> keeps process programs (recipes). Implement it to keep recipes in your own place, or use <see cref="GemProcessProgramFolder"/>.</summary>
/// <remarks>GEM always calls the store inside one lock of its own (on the application event thread for Host requests, on the calling thread for the application's methods), so the store does not need to be thread-safe toward GEM. If the application also calls the store directly, the application synchronizes that. An exception from the store is answered as a refusal (ACKC7 1, or an empty S7F6/S7F20) and reported through <c>ProtocolError</c>.</remarks>
public interface IGemProcessProgramStore
{
    /// <summary>Returns the PPIDs of the stored process programs (S7F20).</summary>
    /// <returns>The PPIDs.</returns>
    IReadOnlyList<string> GetIds();

    /// <summary>Reads a process program.</summary>
    /// <param name="ppid">PPID</param>
    /// <returns>The process program, or null if there is none.</returns>
    GemProcessProgram? Read(string ppid);

    /// <summary>Writes a process program, replacing one with the same PPID.</summary>
    /// <param name="program">The process program.</param>
    void Write(GemProcessProgram program);

    /// <summary>Deletes a process program.</summary>
    /// <param name="ppid">PPID</param>
    /// <returns>true if it existed and was deleted, false if there was none.</returns>
    bool Delete(string ppid);
}

/// <summary>A process program store that keeps one file per PPID in a folder.</summary>
/// <remarks>The file name is the PPID with every character other than <c>a-z 0-9 . _ -</c> (upper-case letters included) written as <c>%XX</c> UTF-8 bytes, plus <c>.pp</c> (when that would exceed 200 characters, <c>~</c> and a SHA-256 of the PPID instead), so PPIDs that differ only in case or contain characters a file name cannot hold are kept apart. Each file holds the PPID and the body in SECS-II form, so the original PPID and the body format come back as they were. A file is written to a temporary file first and then moved into place. Files that cannot be read are left alone and are not listed. The folder is created at the first write.</remarks>
public sealed class GemProcessProgramFolder : IGemProcessProgramStore
{
    const string Ext = ".pp";
    const string TmpExt = ".tmp";

    /// <summary>Creates a store on a folder.</summary>
    /// <param name="directory">The folder (it does not need to exist yet).</param>
    /// <exception cref="ArgumentNullException"><paramref name="directory"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is an empty string.</exception>
    public GemProcessProgramFolder(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (directory.Length == 0) throw new ArgumentException("폴더 경로가 비었습니다.", nameof(directory));
        Directory = directory;
    }

    /// <summary>Gets the folder.</summary>
    public string Directory { get; }

    /// <inheritdoc/>
    public IReadOnlyList<string> GetIds()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var ids = new List<string>();
        foreach (var path in System.IO.Directory.GetFiles(Directory, "*" + Ext))
        {
            if (!path.EndsWith(Ext, StringComparison.Ordinal)) continue;          // *.pp.tmp 등
            var name = Path.GetFileNameWithoutExtension(path);
            // 긴 PPID(해시 이름)는 내용에서 PPID를 읽고, 그 PPID의 이름이 이 파일인지 확인한다
            var ppid = name.StartsWith(HashPrefix, StringComparison.Ordinal) ? StoredPpid(path) : Decode(name);
            if (ppid != null && FileName(ppid) == name && TryLoad(path, ppid) != null) ids.Add(ppid);
        }
        ids.Sort(StringComparer.Ordinal);
        return ids;
    }

    /// <inheritdoc/>
    public GemProcessProgram? Read(string ppid)
    {
        ArgumentNullException.ThrowIfNull(ppid);
        var path = PathOf(ppid);
        return File.Exists(path) ? TryLoad(path, ppid) : null;
    }

    /// <inheritdoc/>
    public void Write(GemProcessProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathOf(program.Ppid);
        var tmp = path + TmpExt;
        var data = SecsCodec.Encode(SecsItem.L(SecsItem.A(program.Ppid), program.Body));
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(data, 0, data.Length);
                fs.Flush(true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
            throw;
        }
    }

    /// <inheritdoc/>
    public bool Delete(string ppid)
    {
        ArgumentNullException.ThrowIfNull(ppid);
        var path = PathOf(ppid);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    // 인코딩한 이름이 이보다 길면(파일 이름 한계 255자) "~" + PPID UTF-8의 SHA-256 16진으로
    const int MaxEncodedName = 200;
    const string HashPrefix = "~";

    string PathOf(string ppid) => Path.Combine(Directory, FileName(ppid) + Ext);

    internal static string FileName(string ppid)
    {
        var e = Encode(ppid);
        return e.Length <= MaxEncodedName ? e : HashPrefix + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(ppid)));
    }

    static string? StoredPpid(string path)
    {
        try
        {
            return SecsCodec.Decode(File.ReadAllBytes(path)) is { Format: SecsFormat.List, Items.Count: 2 } l && l.Items[0].Format == SecsFormat.ASCII ? l.Items[0].GetString() : null;
        }
        catch (Exception) { return null; }
    }

    // 파일을 읽어 그 안의 PPID가 기대와 같을 때만 돌려준다(아니면 null, 파일은 건드리지 않음)
    static GemProcessProgram? TryLoad(string path, string ppid)
    {
        try
        {
            var item = SecsCodec.Decode(File.ReadAllBytes(path));
            if (item is not { Format: SecsFormat.List, Items.Count: 2 } l || l.Items[0].Format != SecsFormat.ASCII || l.Items[0].GetString() != ppid) return null;
            return new GemProcessProgram(ppid, l.Items[1]);
        }
        catch (Exception) { return null; }
    }

    static bool Safe(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-';

    // PPID → 파일 이름(안전한 문자 밖은 UTF-8 바이트마다 %XX)
    internal static string Encode(string ppid)
    {
        var sb = new StringBuilder();
        foreach (var c in ppid)
        {
            if (Safe(c)) sb.Append(c);
            else foreach (var b in Encoding.UTF8.GetBytes(c.ToString())) sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    // 파일 이름 → PPID. 규칙에 맞지 않는 이름(직접 넣은 파일 등)은 null
    internal static string? Decode(string name)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c == '%')
            {
                if (i + 2 >= name.Length || !byte.TryParse(name.AsSpan(i + 1, 2), System.Globalization.NumberStyles.AllowHexSpecifier, null, out var b)) return null;
                bytes.Add(b);
                i += 2;
            }
            else if (Safe(c)) bytes.Add((byte)c);
            else return null;
        }
        var s = Encoding.UTF8.GetString([.. bytes]);
        return Encode(s) == name ? s : null;                      // 같은 PPID의 이름은 하나뿐
    }
}
