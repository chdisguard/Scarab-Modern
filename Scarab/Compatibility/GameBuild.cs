using System.Buffers.Binary;
using System.IO.Abstractions;
using System.Text;
using System.Text.RegularExpressions;

namespace Scarab.Compatibility;

/// <summary>Reads Unity's length-prefixed application version from unmodified game data.
/// Never infer the installed game build from the modded Assembly-CSharp or a stale .v backup.</summary>
public static class GameBuild
{
    public static string Read(IFileSystem fs, string managed)
    {
        string path = Path.GetFullPath(Path.Combine(managed, "..", "globalgamemanagers"));
        string? dataVersion = null;
        if (fs.File.Exists(path))
        {
            try { dataVersion = Parse(fs.File.ReadAllBytes(path)); }
            catch (InvalidDataException) { /* Some Unity builds don't expose a unique serialized version. */ }
        }
        string assemblyPath = Path.Combine(managed, "Assembly-CSharp.dll");
        string? assemblyVersion = null;
        bool hasApi = false;
        if (fs.File.Exists(assemblyPath))
        {
            try
            {
                assemblyVersion = GameVersion.Canonical(GameAssembly.ReadVersion(fs.File.ReadAllBytes(assemblyPath)));
                hasApi = GameAssembly.HasApi(fs, assemblyPath);
            }
            catch (BadImageFormatException) { }
            catch (InvalidDataException) { }
        }
        if (dataVersion is not null && assemblyVersion is not null && !GameVersion.Same(dataVersion, assemblyVersion))
            throw new InvalidDataException($"Game data targets {dataVersion}, but Assembly-CSharp.dll targets {assemblyVersion}. Restore game files through Steam/GOG / Версии файлов игры не совпадают; восстановите игру.");
        if (dataVersion is not null) return GameVersion.Canonical(dataVersion);
        // A clean assembly is an authoritative fallback when the Unity data format cannot be read.
        if (assemblyVersion is not null && !hasApi) return assemblyVersion;
        var receipt = ApiReceipt.Read(fs, managed);
        if (assemblyVersion is not null && receipt is not null && GameVersion.Same(receipt.Game, assemblyVersion) &&
            receipt.CurrentMatches(fs, managed, true)) return assemblyVersion;
        throw new InvalidDataException("Не удалось подтвердить версию игры. Выберите папку Managed; если API установлен сторонним менеджером, восстановите файлы игры в Steam/GOG.");
    }

    public static string Parse(byte[] bytes)
    {
        // Latin1 preserves byte offsets. Unity strings use a 32-bit byte count and UTF-8.
        string text = Encoding.Latin1.GetString(bytes);
        var versions = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(text, @"(?<![\d.])1\.[0-9]+\.[0-9]+(?:\.[0-9]+)?(?![\d.])"))
        {
            if (m.Index < 4) continue;
            ReadOnlySpan<byte> prefix = bytes.AsSpan(m.Index - 4, 4);
            if (BinaryPrimitives.ReadInt32LittleEndian(prefix) == m.Length ||
                BinaryPrimitives.ReadInt32BigEndian(prefix) == m.Length)
                versions.Add(GameVersion.Canonical(m.Value));
        }
        if (versions.Count != 1)
            throw new InvalidDataException("Game version is missing or ambiguous. No game files were changed / Версия игры не определена однозначно; файлы игры не изменены.");
        return versions.Single();
    }
}
