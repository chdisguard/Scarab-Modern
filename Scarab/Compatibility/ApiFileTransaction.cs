using System.IO.Abstractions;
using System.IO.Compression;
using System.Text.Json;

namespace Scarab.Compatibility;

/// <summary>Stages complete API bundles, including Unity 6 localization and native dependencies.
/// Retains immutable vanilla/modded payloads and rolls back write or state failures.</summary>
public static class ApiFileTransaction
{
    public static async Task InstallAsync(IFileSystem fs, string managed, byte[] archiveBytes,
        bool backupVanilla, string game, int api, Func<Task> commit, string source = "official")
    {
        using var archive = new ZipArchive(new MemoryStream(archiveBytes), ZipArchiveMode.Read);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            string name = entry.FullName;
            bool allowed = name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || name == "README.md" ||
                name.EndsWith(".so", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase);
            if (!ApiReceipt.SafeName(name) || !allowed || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException($"Unexpected API archive entry: {name}");
            total += entry.Length;
            if (total > 256L * 1024 * 1024 || files.Count >= 512)
                throw new InvalidDataException("API archive exceeds the size limit.");
            using var input = entry.Open();
            using var output = new MemoryStream();
            byte[] buffer = new byte[81920];
            int n;
            while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + n > entry.Length) throw new InvalidDataException("Invalid ZIP entry size.");
                output.Write(buffer, 0, n);
            }
            if (!files.TryAdd(name, output.ToArray())) throw new InvalidDataException("Duplicate API archive entry.");
        }
        if (!files.ContainsKey("Assembly-CSharp.dll")) throw new InvalidDataException("API archive contains no Assembly-CSharp.dll.");
        ApiReceipt? old = ApiReceipt.Read(fs, managed);
        if (!backupVanilla && (old is null || !GameVersion.Same(old.Game, game) || !ApiReceipt.VanillaMatches(fs, managed, game)))
            throw new InvalidDataException("Verified vanilla backups are required before replacing an existing API.");

        string batch = Guid.NewGuid().ToString("N");
        var records = new Dictionary<string, ApiFileRecord>(StringComparer.OrdinalIgnoreCase);
        var changes = new Dictionary<string, byte[]?>();
        foreach (var (name, data) in files)
        {
            string path = Path.Combine(managed, name);
            byte[]? vanilla = old is not null && GameVersion.Same(old.Game, game) && old.Files.ContainsKey(name) && !backupVanilla
                ? old.Cached(fs, managed, name, false)
                : fs.File.Exists(path) ? fs.File.ReadAllBytes(path) : null;
            records[name] = new ApiFileRecord(vanilla is null ? null : ApiReceipt.Hash(vanilla), ApiReceipt.Hash(data));
            var tempReceipt = new ApiReceipt(game, api, source, batch, records);
            WriteCache(fs, tempReceipt.CachePath(managed, name, true), data, managed);
            if (vanilla is not null) WriteCache(fs, tempReceipt.CachePath(managed, name, false), vanilla, managed);
            changes[path] = data;
        }
        // Files removed by the new API bundle return to their original state.
        if (!backupVanilla && old is not null)
            foreach (string name in old.Files.Keys.Where(n => !files.ContainsKey(n)))
                changes[Path.Combine(managed, name)] = old.Cached(fs, managed, name, false);
        var receipt = new ApiReceipt(game, api, source, batch, records);
        changes[Path.Combine(managed, ApiReceipt.Filename)] = JsonSerializer.SerializeToUtf8Bytes(receipt);
        await ApplyAsync(fs, managed, changes, commit);
    }

    public static async Task ToggleAsync(IFileSystem fs, string managed, string game, bool enabled, Func<Task> commit)
    {
        var receipt = ApiReceipt.Read(fs, managed);
        if (receipt is null || !GameVersion.Same(receipt.Game, game) || !receipt.CurrentMatches(fs, managed, !enabled))
            throw new InvalidDataException("API files changed or belong to another game build. Verify game files / Файлы API изменены; восстановите игру через Steam/GOG.");
        var changes = receipt.Files.Keys.ToDictionary(name => Path.Combine(managed, name),
            name => receipt.Cached(fs, managed, name, enabled));
        await ApplyAsync(fs, managed, changes, commit);
    }

    private static void WriteCache(IFileSystem fs, string path, byte[] bytes, string managed)
    {
        EnsureNoLinks(fs, path, managed);
        fs.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        fs.File.WriteAllBytes(path, bytes);
    }

    private static void EnsureNoLinks(IFileSystem fs, string path, string managed)
    {
        string? p = Path.GetFullPath(path);
        string boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(managed));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        while (p is not null && !string.Equals(p, boundary, comparison))
        {
            if ((fs.File.Exists(p) || fs.Directory.Exists(p)) && fs.File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException($"Symbolic link in API destination: {p}");
            // Check within Managed and the new cache; legitimate Steam root aliases remain usable.
            p = Path.GetDirectoryName(p);
        }
    }

    public static async Task ApplyAsync(IFileSystem fs, string managed, Dictionary<string, byte[]?> changes, Func<Task> commit)
    {
        string backup = Path.Combine(managed, ".scarab-backups", Guid.NewGuid().ToString("N"));
        EnsureNoLinks(fs, backup, managed);
        foreach (string path in changes.Keys)
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(managed)), comparison))
                throw new InvalidDataException("API transaction must stay inside Managed's root.");
            EnsureNoLinks(fs, path, managed);
        }
        fs.Directory.CreateDirectory(backup);
        var originals = new Dictionary<string, string?>();
        foreach (string path in changes.Keys)
        {
            string? copy = fs.File.Exists(path) ? Path.Combine(backup, Path.GetFileName(path)) : null;
            if (copy is not null) fs.File.Copy(path, copy);
            originals[path] = copy;
        }
        fs.File.WriteAllText(Path.Combine(backup, "recovery.json"), JsonSerializer.Serialize(originals));
        try
        {
            foreach (var (path, data) in changes)
            {
                if (data is null) { if (fs.File.Exists(path)) fs.File.Delete(path); }
                else fs.File.WriteAllBytes(path, data);
            }
            await commit();
        }
        catch (Exception failure)
        {
            var failures = new List<Exception> { failure };
            foreach (var (path, copy) in originals)
                try
                {
                    if (copy is not null) fs.File.Copy(copy, path, true);
                    else if (fs.File.Exists(path)) fs.File.Delete(path);
                }
                catch (Exception e) { failures.Add(e); }
            if (failures.Count > 1) throw new AggregateException($"Rollback incomplete. Recovery copies: {backup}", failures);
            throw;
        }
    }
}
