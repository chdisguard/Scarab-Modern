using System.Diagnostics.Contracts;
using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scarab.Services;

[Serializable]
public record InstalledMods : IModSource
{
    private const string FILE_NAME = "InstalledMods.json";
        
    internal static readonly string ConfigPath = Path.Combine(Settings.GetOrCreateDirPath(), FILE_NAME);

    public Dictionary<string, InstalledState> Mods { get; init; } = new();

    public ModState ApiInstall
    {
        get => (ModState?) _ApiState ?? new NotInstalledState();
        private set => _ApiState = value as InstalledState; 
    } 

    [JsonInclude]
    // public get because System.Text.Json won't let me make both private
    public InstalledState? _ApiState { get; private set; }

    private readonly IFileSystem _fs;

    public static async Task<InstalledMods> Load(IFileSystem fs, ISettings config, ModLinks ml)
    {
        InstalledMods db;

        bool ModExists(string name, out bool enabled)
        {
            enabled = false;
                
            if (Directory.Exists(Path.Combine(config.ModsFolder, name)))
                return enabled = true;

            return Directory.Exists(Path.Combine(config.DisabledFolder, name));
        }

        bool changed = false;

        try
        {
            string text = await File.ReadAllTextAsync(ConfigPath);
                
            db = JsonSerializer.Deserialize<InstalledMods>(text) ?? throw new InvalidDataException();
        } 
        catch (Exception e) when (e is InvalidDataException or JsonException or FileNotFoundException)
        {
            // If we have malformed JSON or it's a new install, try and recover any installed mods
            db = new InstalledMods();

            foreach (string name in ml.Manifests.Select(x => x.Name))
            {
                if (!ModExists(name, out bool enabled)) 
                    continue;
                    
                // Pretend it's out of date because we aren't sure of the version.
                db.Mods.Add(name, new InstalledState(enabled, new Version(0, 0), false));
            }

            changed = true;
        }

        // Reconcile state from verified on-disk payloads after a crash or config loss.
        var receipt = Compatibility.ApiReceipt.Read(fs, config.ManagedFolder);
        if (receipt is not null)
        {
            try
            {
                string game = Compatibility.GameBuild.Read(fs, config.ManagedFolder);
                if (Scarab.Compatibility.GameVersion.Same(receipt.Game, game))
                {
                    bool enabled = receipt.CurrentMatches(fs, config.ManagedFolder, true);
                    bool disabled = !enabled && receipt.CurrentMatches(fs, config.ManagedFolder, false);
                    db.ApiInstall = enabled || disabled
                        ? new InstalledState(enabled, new Version(receipt.Api, 0, 0), true)
                        : new NotInstalledState();
                    changed = true;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        if (db.ApiInstall is not InstalledState)
        {
            if (changed) await db.SaveToDiskAsync();
            return db;
        }

        // Validate that mods are installed and in the right state in case of manual user intervention
        // We use ToList as we modify the db in the for-each
        foreach (var (name, state) in db.Mods.ToList())
        {
            if (ModExists(name, out bool enabled))
            {
                // Fix it being enabled or disabled when it's in the opposite state
                if (state.Enabled != enabled)
                {
                    Log.Warning("Fixing incorrect enabled state of {Name}, changing to {Enabled}.", name, enabled);
                        
                    db.Mods[name] = state with { Enabled = enabled };

                    changed = true;
                }

                continue;
            }

            Log.Warning("Removing missing mod {Name}!", name);
                
            db.Mods.Remove(name);

            changed = true;
        }
            
        /*
         * If the user deleted their assembly, we can deal with it at least.
         *
         * This isn't ideal, but at least we won't crash and the user will be
         * (relatively) okay, and as a budget remedy we'll just put the API in
         */
        // ReSharper disable once InvertIf
        if (
            !fs.File.Exists(Path.Combine(config.ManagedFolder, Installer.Modded)) &&
            !fs.File.Exists(Path.Combine(config.ManagedFolder, Installer.Current))
        ) 
        {
            Log.Warning("Assembly missing, marking API as uninstalled!");
            db.ApiInstall = new NotInstalledState();

            changed = true;
        }

        // If we didn't find any manual changes, we can just return
        if (!changed) 
            return db;
            
        // Otherwise, we write back our changes
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);

            await db.SaveToDiskAsync();
        } 
        catch 
        {
            // tragic.
        }

        return db;
    }

    public InstalledMods() => _fs = new FileSystem();

    public InstalledMods(IFileSystem fs) => _fs = fs;

    public async Task Reset()
    {
        Mods.Clear();
        _ApiState = null;

        await SaveToDiskAsync();
    }

    public async Task RecordApiState(ModState st)
    {
        ModState previous = ApiInstall;
        ApiInstall = st;
        try { await SaveToDiskAsync(); }
        catch
        {
            ApiInstall = previous;
            throw;
        }
    }

    public ModState FromManifest(Manifest manifest)
    {
        if (Mods.TryGetValue(manifest.Name, out var existing))
        {
            return existing with
            {
                Updated = existing.Version >= manifest.Version.Value
            };
        }

        return new NotInstalledState();
    }
        
    public async Task RecordInstalledState(ModItem item)
    {
        Contract.Assert(item.State is InstalledState);

        bool existed = Mods.TryGetValue(item.Name, out var previous);
        Mods[item.Name] = (InstalledState)item.State;
        try { await SaveToDiskAsync(); }
        catch
        {
            if (existed) Mods[item.Name] = previous!;
            else Mods.Remove(item.Name);
            throw;
        }
    }

    public async Task RecordUninstall(ModItem item)
    {
        Contract.Assert(item.State is NotInstalledState);

        bool existed = Mods.Remove(item.Name, out var previous);
        try { await SaveToDiskAsync(); }
        catch
        {
            if (existed) Mods[item.Name] = previous!;
            throw;
        }
    }

    private async Task SaveToDiskAsync()
    {
        _fs.Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        string temporary = ConfigPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (Stream stream = _fs.File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, this);
            _fs.File.Move(temporary, ConfigPath, true);
        }
        finally
        {
            if (_fs.File.Exists(temporary)) _fs.File.Delete(temporary);
        }
    }
}