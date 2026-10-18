using System.IO.Abstractions;
using System.Text.Json;
using Scarab.Compatibility;

namespace Scarab.Services;

public static class ModFileTransaction
{
    public static async Task InstallAsync(IFileSystem fs, string managed, string name, bool enable,
        Dictionary<string, byte[]> files, Func<Task> commit)
    {
        ModPackage.ValidateName(name);
        string mods = Path.Combine(managed, "Mods");
        string enabled = Path.Combine(mods, name);
        string disabled = Path.Combine(mods, "Disabled", name);
        if (fs.Directory.Exists(enabled) && fs.Directory.Exists(disabled))
            throw new InvalidDataException($"Мод «{name}» существует одновременно в Mods и Disabled. Переместите лишнюю копию перед установкой.");
        string source = fs.Directory.Exists(enabled) ? enabled : disabled;
        string target = enable ? enabled : disabled;
        string work = Path.Combine(managed, ".scarab-mod-stage", Guid.NewGuid().ToString("N"));
        string stage = Path.Combine(work, "new");
        string backup = Path.Combine(work, "old");
        EnsureNoLinks(fs, managed, source);
        EnsureNoLinks(fs, managed, work);
        fs.Directory.CreateDirectory(stage);
        bool movedOld = false, movedNew = false;
        try
        {
            if (fs.Directory.Exists(source))
            {
                var queue = new Queue<(string Source, string Destination)>();
                queue.Enqueue((source, stage));
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    EnsureNoLinks(fs, managed, current.Source);
                    fs.Directory.CreateDirectory(current.Destination);
                    foreach (string path in fs.Directory.GetFiles(current.Source, "*", SearchOption.TopDirectoryOnly))
                    {
                        EnsureNoLinks(fs, managed, path);
                        fs.File.Copy(path, Path.Combine(current.Destination, Path.GetFileName(path)));
                    }
                    foreach (string directory in fs.Directory.GetDirectories(current.Source))
                    {
                        EnsureNoLinks(fs, managed, directory);
                        queue.Enqueue((directory, Path.Combine(current.Destination, Path.GetFileName(directory))));
                    }
                }
            }
            string ownership = Path.Combine(stage, ModPackage.OwnershipFile);
            if (fs.File.Exists(ownership))
            {
                var owned = JsonSerializer.Deserialize<Dictionary<string, string>>(fs.File.ReadAllText(ownership)) ?? new();
                foreach (var (relative, hash) in owned)
                {
                    string safe = ModPackage.SafeRelative(relative);
                    string path = Path.Combine(stage, safe.Replace('/', Path.DirectorySeparatorChar));
                    if (fs.File.Exists(path) && ApiReceipt.Hash(fs.File.ReadAllBytes(path)) == hash) fs.File.Delete(path);
                }
            }
            foreach (var (relative, data) in files)
            {
                string safe = ModPackage.SafeRelative(relative);
                string path = Path.Combine(stage, safe.Replace('/', Path.DirectorySeparatorChar));
                fs.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                fs.File.WriteAllBytes(path, data);
            }
            fs.File.WriteAllText(ownership, JsonSerializer.Serialize(files.ToDictionary(p => p.Key, p => ApiReceipt.Hash(p.Value))));
            fs.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (fs.Directory.Exists(source)) { fs.Directory.Move(source, backup); movedOld = true; }
            fs.Directory.Move(stage, target); movedNew = true;
            await commit();
        }
        catch (Exception failure)
        {
            try
            {
                if (movedNew && fs.Directory.Exists(target)) fs.Directory.Delete(target, true);
                if (movedOld) fs.Directory.Move(backup, source);
            }
            catch (Exception rollback) { throw new AggregateException($"Откат мода не завершён. Резервная копия: {backup}", failure, rollback); }
            throw;
        }
        finally
        {
            // Keep a recoverable backup when rollback itself failed.
            if (!fs.Directory.Exists(backup))
            {
                try { if (fs.Directory.Exists(work)) fs.Directory.Delete(work, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        // Successful swap; failure to remove a staging backup must not undo a valid install.
        try { fs.Directory.Delete(work, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static async Task UninstallAsync(IFileSystem fs, string managed, string name, bool enabled, Func<Task> commit)
    {
        ModPackage.ValidateName(name);
        string source = enabled ? Path.Combine(managed, "Mods", name) : Path.Combine(managed, "Mods", "Disabled", name);
        string alternate = enabled ? Path.Combine(managed, "Mods", "Disabled", name) : Path.Combine(managed, "Mods", name);
        if (fs.Directory.Exists(source) && fs.Directory.Exists(alternate))
            throw new InvalidDataException($"Мод «{name}» существует одновременно в Mods и Disabled. Уберите дублирующую копию.");
        if (!fs.Directory.Exists(source)) source = alternate;
        string backup = Path.Combine(managed, ".scarab-mod-uninstall", Guid.NewGuid().ToString("N"));
        EnsureNoLinks(fs, managed, source);
        EnsureNoLinks(fs, managed, backup);
        bool moved = false;
        if (fs.Directory.Exists(source))
        {
            fs.Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            fs.Directory.Move(source, backup);
            moved = true;
        }
        try { await commit(); }
        catch
        {
            if (moved) fs.Directory.Move(backup, source);
            throw;
        }
        try { if (moved) fs.Directory.Delete(backup, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void EnsureNoLinks(IFileSystem fs, string managed, string path)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(managed));
        string? p = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        while (p is not null && !string.Equals(p, root, comparison))
        {
            if ((fs.File.Exists(p) || fs.Directory.Exists(p)) && fs.File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException($"Ссылка внутри папки мода не поддерживается: {p}");
            p = Path.GetDirectoryName(p);
        }
    }
}
