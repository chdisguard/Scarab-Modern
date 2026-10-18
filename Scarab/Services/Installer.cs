using Scarab.Compatibility;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.IO.Abstractions;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Toolkit.HighPerformance;

namespace Scarab.Services;

public class HashMismatchException : Exception
{
    /// <summary>
    /// The SHA256 value that was received
    /// </summary>
    public string Actual { get; }

    /// <summary>
    /// Expected SHA256 value
    /// </summary>
    public string Expected { get; }
        
    /// <summary>
    ///  The name of the object being checked
    /// </summary>
    public string Name { get; }
        
    public HashMismatchException(string name, string actual, string expected)
        : base($"Контрольная сумма {name} не совпадает. Файл не установлен. Обновите каталог и повторите загрузку. Ожидалась SHA-256: {expected}; получена: {actual}")
    {
        Name = name;
        Actual = actual;
        Expected = expected;
    }

}
    
public class Installer : IInstaller
{
    private enum Update
    {
        ForceUpdate,
        LeaveUnchanged
    }

    private readonly ISettings _config;
    private readonly IModSource _installed;
    private readonly IModDatabase _db;
    private readonly IFileSystem _fs;
        
    // If we're going to have one be internal, might as well be consistent
    // ReSharper disable MemberCanBePrivate.Global 
    internal const string Modded = "Assembly-CSharp.dll.m";
    internal const string Vanilla = "Assembly-CSharp.dll.v";
    internal const string Current = "Assembly-CSharp.dll";
    // ReSharper restore MemberCanBePrivate.Global

    private readonly SemaphoreSlim _semaphore = new (1);
    private readonly HttpClient _hc;

    public Installer(ISettings config, IModSource installed, IModDatabase db, IFileSystem fs, HttpClient hc)
    {
        _config = config;
        _installed = installed;
        _db = db;
        _fs = fs;
        _hc = hc;
    }

    private void CreateNeededDirectories()
    {
        // These both no-op if the directory already exists,
        // so no need to check ourselves
        _fs.Directory.CreateDirectory(_config.DisabledFolder);

        _fs.Directory.CreateDirectory(_config.ModsFolder);
    }

    public async Task Toggle(ModItem mod)
    {
        await _semaphore.WaitAsync();
            
        try
        {
            GameProcess.EnsureClosed();
            await _Toggle(mod);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task _Toggle(ModItem mod)
    {
        ModPackage.ValidateName(mod.Name);
        if (mod.State is not InstalledState state)
            throw new InvalidOperationException("Cannot enable mod which is not installed!");
            
        // Validate the complete graph before any recursive enabling or directory moves.
        if (!state.Enabled)
        {
            var plan = DependencyPlanner.Order(mod, _db.Items);
            var missing = plan.FirstOrDefault(m => m != mod && m.State is not InstalledState);
            if (missing is not null) throw new InvalidDataException($"Зависимость «{missing.Name}» не установлена. Переустановите «{mod.Name}», чтобы загрузить зависимости.");
            foreach (ModItem dep in mod.Dependencies.Select(x => _db.Items.First(i => i.Name == x)))
            {
                if (dep.State is InstalledState { Enabled: true } or NotInstalledState)
                    continue;

                await _Toggle(dep);
            }
        }

        CreateNeededDirectories();

        var (prev, after) = !state.Enabled
            ? (_config.DisabledFolder, _config.ModsFolder)
            : (_config.ModsFolder, _config.DisabledFolder);

        (prev, after) = (
            Path.Combine(prev, mod.Name),
            Path.Combine(after, mod.Name)
        );

        if (_fs.Directory.Exists(after) && !_fs.Directory.Exists(prev))
        {
            try
            {
                mod.State = state with { Enabled = !state.Enabled };
                await _installed.RecordInstalledState(mod);
            }
            catch { mod.State = state; throw; }
            return;
        }
        if (_fs.Directory.Exists(after))
            throw new InvalidDataException($"Целевая папка мода уже существует: {after}. Уберите дублирующую копию.");
        if (!_fs.Directory.Exists(prev))
            throw new DirectoryNotFoundException($"Не найдена папка мода: {prev}. Переустановите мод.");
        _fs.Directory.Move(prev, after);
        try
        {
            mod.State = state with { Enabled = !state.Enabled };
            await _installed.RecordInstalledState(mod);
        }
        catch
        {
            mod.State = state;
            _fs.Directory.Move(after, prev);
            throw;
        }
    }

    /// <remarks> This enables the API if it's installed! </remarks>
    public async Task InstallApi(IInstaller.ReinstallPolicy policy = IInstaller.ReinstallPolicy.SkipUpToDate)
    {
        await _semaphore.WaitAsync();

        try
        {
            GameProcess.EnsureClosed();
            CheckWriteAccess();
            // Verify before toggling a disabled API or writing any files.
            string game = GameBuild.Read(_fs, _config.ManagedFolder);
            if (game == LocalApiBuilder.Game)
            {
                await InstallUnity6Api(policy);
                return;
            }
            var compatible = await OfficialApiResolver.ResolveAsync(_hc, game, _db.Api, _config.PlatformLink);
            await _InstallApi(compatible, policy);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task _InstallApi(
        (Links Link, int Version) manifest,
        IInstaller.ReinstallPolicy policy = IInstaller.ReinstallPolicy.SkipUpToDate
    )
    {
        string game = GameBuild.Read(_fs, _config.ManagedFolder);
        ApiCompatibility.EnsureCompatible(game, _config.PlatformLink(manifest.Link).URL, manifest.Version);
        bool was_vanilla = !GameAssembly.HasApi(_fs, Path.Combine(_config.ManagedFolder, Current));
        if (!was_vanilla && !ApiReceipt.VanillaMatches(_fs, _config.ManagedFolder, game))
            throw new InvalidDataException("Vanilla backup cannot be verified. Verify/repair the game in Steam or GOG, then reinstall API. Резервная DLL не подтверждена: восстановите файлы игры через Steam/GOG и переустановите API.");

        if (_installed.ApiInstall is InstalledState { Version: var version, Enabled: true } && policy is not IInstaller.ReinstallPolicy.ForceReinstall)
        {
            if (version.Major == manifest.Version &&
                ApiReceipt.Matches(_fs, _config.ManagedFolder, Current, game, manifest.Version))
                return;

        }
            
        (var links, int ver) = manifest;
        var url = _config.PlatformLink(links);

        string managed = _config.ManagedFolder;

        (ArraySegment<byte> data, string _) = await DownloadFile(url.URL, _ => { });
            
        ThrowIfInvalidHash("the API", data, url.SHA256);
        using (var archive = new ZipArchive(new MemoryStream(data.ToArray())))
        {
            var entry = archive.GetEntry(Current) ?? throw new InvalidDataException("API is missing Assembly-CSharp.dll.");
            using var input = entry.Open();
            using var bytes = new MemoryStream();
            input.CopyTo(bytes);
            if (!GameVersion.Same(GameAssembly.ReadVersion(bytes.ToArray()), game))
                throw new InvalidDataException("API assembly targets a different game version.");
        }

        GameProcess.EnsureClosed();
        // A clean current assembly is authoritative after a Steam/GOG update; replace stale .v backups.
        await ApiFileTransaction.InstallAsync(_fs, managed, data.ToArray(), was_vanilla, game, ver,
            () => _installed.RecordApiState(new InstalledState(true, new Version(ver, 0, 0), true)));
    }

    public async Task ToggleApi()
    {
        await _semaphore.WaitAsync();

        try
        {
            await _ToggleApi();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // TODO: UI
    public async Task HandlePlatformChange()
    {
        if (!_config.PlatformChanged)
            return;

        // Otherwise, we need to reinstall *the api* and some mods with platform-specific assets.
        if (_installed.ApiInstall is InstalledState st)
        {
            Log.Logger.Information("Platform changed, reinstalling API.");
        
            await InstallApi(policy: IInstaller.ReinstallPolicy.ForceReinstall);

            // Put it back where it was as InstallApi currently enables it.
            if (!st.Enabled)
                await _ToggleApi(Update.LeaveUnchanged);
        }

        foreach (var mod in _db.Items.Where(i => i.Installed))
        {
            if (mod is not { Installed: true, Link.HasPlatformSpecificLink: true, Enabled: var enabled }) 
                continue;
            
            Log.Logger.Information("Reinstalling {Mod} with platform-specific links.", mod);
                
            await Install(mod, _ => { }, enabled);
        }

        _config.Save();
    }

    private async Task InstallUnity6Api(IInstaller.ReinstallPolicy policy)
    {
        string managed = _config.ManagedFolder;
        GameProcess.EnsureClosed();
        string game = GameBuild.Read(_fs, managed);
        var receipt = ApiReceipt.Read(_fs, managed);
        if (policy != IInstaller.ReinstallPolicy.ForceReinstall && receipt is not null &&
            GameVersion.Same(receipt.Game, game) && receipt.Api == LocalApiBuilder.Api && LocalApiBuilder.IsKnownSource(receipt.Source))
        {
            if (receipt.CurrentMatches(_fs, managed, true))
            {
                await _installed.RecordApiState(new InstalledState(true, new Version(LocalApiBuilder.Api, 0, 0), true));
                return;
            }
            if (receipt.CurrentMatches(_fs, managed, false))
            {
                await ApiFileTransaction.ToggleAsync(_fs, managed, game, true,
                    () => _installed.RecordApiState(new InstalledState(true, new Version(LocalApiBuilder.Api, 0, 0), true)));
                return;
            }
            throw new InvalidDataException("API files changed since installation. Verify game files before reinstalling.");
        }
        byte[] data = await LocalApiBuilder.BuildAsync(_config, _fs);
        GameProcess.EnsureClosed();
        bool pristine = !GameAssembly.HasApi(_fs, Path.Combine(managed, Current));
        await ApiFileTransaction.InstallAsync(_fs, managed, data, pristine, game, LocalApiBuilder.Api,
            () => _installed.RecordApiState(new InstalledState(true, new Version(LocalApiBuilder.Api, 0, 0), true)), LocalApiBuilder.Source);
    }

    private async Task _ToggleApi(Update update = Update.ForceUpdate)
    {
        GameProcess.EnsureClosed();
        string managed = _config.ManagedFolder;
        if (_installed.ApiInstall is not InstalledState st)
            throw new InvalidOperationException("API is not installed.");
        await ApiFileTransaction.ToggleAsync(_fs, managed, GameBuild.Read(_fs, managed), !st.Enabled,
            () => _installed.RecordApiState(st with { Enabled = !st.Enabled }));
    }

    /// <summary>
    /// Installs the given mod.
    /// </summary>
    /// <param name="mod">Mod to install</param>
    /// <param name="setProgress">Action called to indicate progress asynchronously</param>
    /// <param name="enable">Whether the mod is enabled after installation</param>
    /// <exception cref="HashMismatchException">Thrown if the download doesn't match the given hash</exception>
    public async Task Install(ModItem mod, Action<ModProgressArgs> setProgress, bool enable)
    {
        ModPackage.ValidateName(mod.Name);
        var plan = DependencyPlanner.Order(mod, _db.Items);
        foreach (var item in plan) ModPackage.ValidateName(item.Name);
        await InstallApi();

        await _semaphore.WaitAsync();

        try
        {
            GameProcess.EnsureClosed();
            CreateNeededDirectories();

            void DownloadProgressed(DownloadProgressArgs args)
            {
                setProgress(new ModProgressArgs {
                    Download = args
                });
            }

            // Start our progress
            setProgress(new ModProgressArgs());

            foreach (var item in plan)
            {
                if (item != mod && item.State is InstalledState { Updated: true } installed)
                {
                    if (!installed.Enabled && enable) await _Toggle(item);
                    continue;
                }
                bool enabled = item == mod ? enable : enable || item.State is NotInstalledState || item.Enabled;
                await _Install(item, item == mod ? DownloadProgressed : _ => { }, enabled);
            }
                
            setProgress(new ModProgressArgs {
                Completed = true
            });
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task Uninstall(ModItem mod)
    {
        await _semaphore.WaitAsync();

        try
        {
            GameProcess.EnsureClosed();
            // Shouldn't ever not exist, but rather safe than sorry I guess.
            CreateNeededDirectories();

            await _Uninstall(mod);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task _Install(ModItem mod, Action<DownloadProgressArgs> setProgress, bool enable)
    {
        var link = _config.PlatformLink(mod.Link);
        Log.Information("Downloading mod {Name} {Version}", mod.Name, mod.Version);

        var (data, filename) = await DownloadFile(link.URL, setProgress);

        ThrowIfInvalidHash(mod.Name, data, link.SHA256);
        GameProcess.EnsureClosed();

        var files = ModPackage.Read(data, filename);
        Log.Information("Validated package for {Name}: {Count} files; staging installation", mod.Name, files.Count);
        ModState previous = mod.State;
        await ModFileTransaction.InstallAsync(_fs, _config.ManagedFolder, mod.Name, enable, files, async () =>
        {
            try
            {
                mod.State = new InstalledState(enable, mod.Version, true);
                await _installed.RecordInstalledState(mod);
            }
            catch { mod.State = previous; throw; }
        });
    }

    private static void ThrowIfInvalidHash(string name, ArraySegment<byte> data, string modSha256)
    {
        var sha = SHA256.Create();

        byte[] hash = sha.ComputeHash(data.AsMemory().AsStream());

        string strHash = BitConverter.ToString(hash).Replace("-", string.Empty);

        if (!string.Equals(strHash, modSha256, StringComparison.OrdinalIgnoreCase))
            throw new HashMismatchException(name, actual: strHash, expected: modSha256);
    }

    private Task<(ArraySegment<byte> data, string filename)> DownloadFile(string uri, Action<DownloadProgressArgs> setProgress)
        => DownloadService.FetchAsync(_hc, uri, setProgress);

    private void CheckWriteAccess()
    {
        string probe = Path.Combine(_config.ManagedFolder, ".scarab-write-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            _fs.File.WriteAllText(probe, "write-check");
            _fs.File.Delete(probe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Нет доступа на запись в папку игры: {_config.ManagedFolder}. Проверьте права этой папки и не блокирует ли её антивирус. Файлы игры не изменены.", e);
        }
    }

    private async Task _Uninstall(ModItem mod, HashSet<string>? visited = null)
    {
        visited ??= new HashSet<string>(StringComparer.Ordinal);
        if (!visited.Add(mod.Name)) return;
        ModState previous = mod.State;
        await ModFileTransaction.UninstallAsync(_fs, _config.ManagedFolder, mod.Name, mod.Enabled, async () =>
        {
            try
            {
                mod.State = new NotInstalledState();
                await _installed.RecordUninstall(mod);
            }
            catch { mod.State = previous; throw; }
        });
        if (!_config.AutoRemoveDeps) return;
        foreach (string name in mod.Dependencies)
        {
            var dep = _db.Items.FirstOrDefault(i => i.Name == name);
            if (dep is null || _db.Items.Any(x => x.State is InstalledState && x != mod && x.Dependencies.Contains(name))) continue;
            await _Uninstall(dep, visited);
        }
    }
}
