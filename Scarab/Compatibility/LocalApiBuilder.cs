using System.Diagnostics;
using System.IO.Abstractions;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace Scarab.Compatibility;

/// <summary>Pinned, reviewed Unity 6 source, built against the user's own pristine binaries.</summary>
public static class LocalApiBuilder
{
    public const string Game = "1.5.12620";
    public const int Api = 78;
    public const string Source = "unity6-f0057c4-modern-3";
    // Earlier receipts remain usable; this revision isolates patcher dependencies.
    public static bool IsKnownSource(string source) => source is "unity6-f0057c4-modern-1" or "unity6-f0057c4-modern-2" or Source;

    public static async Task<byte[]> BuildAsync(ISettings settings, IFileSystem fs)
    {
        string log = Diagnostics.BeginBuildLog();
        string managed = settings.ManagedFolder;
        await File.AppendAllTextAsync(log, $"Managed: {managed}\nTarget: {Game}\nSource: {Source}\n");
        string dotnet = ApiBuildSdk.ResolveExecutable(AppContext.BaseDirectory);
        await File.AppendAllTextAsync(log, $"SDK host: {dotnet}\n");
        string sdks = await ToolRunner.RunAsync(dotnet, new[] { "--list-sdks" }, Settings.GetOrCreateDirPath(), log, TimeSpan.FromSeconds(30));
        string sdk = ApiBuildSdk.Select(sdks);
        string game = GameBuild.Read(fs, managed);
        if (game != Game) throw new InvalidDataException($"Bundled Unity 6 API targets {Game}, not {game}.");
        var receipt = ApiReceipt.Read(fs, managed);
        bool modded = GameAssembly.HasApi(fs, Path.Combine(managed, "Assembly-CSharp.dll"));
        if (modded && (receipt is null || !Scarab.Compatibility.GameVersion.Same(receipt.Game, game) || !ApiReceipt.VanillaMatches(fs, managed, game)))
            throw new InvalidDataException("Verify game files in Steam/GOG before the first Unity 6 build / Перед первой сборкой восстановите файлы игры через Steam/GOG.");
        string root = Path.Combine(Settings.GetOrCreateDirPath(), "ApiBuilds", Source, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.AppendAllTextAsync(log, $"Build workspace: {root}\n");
        using (Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream("Scarab.Unity6Api.zip")
            ?? throw new InvalidDataException("Bundled Unity 6 API source is missing."))
        using (var archive = new ZipArchive(source)) archive.ExtractToDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "global.json"), ApiBuildSdk.GlobalJson(sdk));
        await File.AppendAllTextAsync(log, $"Pinned API SDK: {sdk}\n");
        string actualSdk = await ToolRunner.RunAsync(dotnet, new[] { "--version" }, root, log, TimeSpan.FromSeconds(30));
        if (!string.Equals(actualSdk.Trim(), sdk, StringComparison.Ordinal))
            throw new IOException($"Ожидался SDK {sdk}, выбран {actualSdk.Trim()}. Лог: {log}");
        string vanilla = Path.Combine(root, "Vanilla");
        Directory.CreateDirectory(vanilla);
        foreach (string path in fs.Directory.GetFiles(managed, "*.dll"))
        {
            string name = Path.GetFileName(path);
            byte[]? data = modded && receipt is not null && Scarab.Compatibility.GameVersion.Same(receipt.Game, game) && receipt.Files.ContainsKey(name)
                ? receipt.Cached(fs, managed, name, false) : fs.File.ReadAllBytes(path);
            if (data is not null) await File.WriteAllBytesAsync(Path.Combine(vanilla, name), data);
        }
        string original = Path.Combine(vanilla, "Assembly-CSharp.dll");
        var physical = new FileSystem();
        if (GameAssembly.HasApi(physical, original) || !GameVersion.Same(GameAssembly.ReadVersion(physical.File.ReadAllBytes(original)), Game))
            throw new InvalidDataException("Исходная DLL должна быть чистой и соответствовать игре 1.5.12620. Восстановите файлы игры через Steam/GOG.");
        if (File.ReadAllText(Path.Combine(root, "hollowknight.version")).Trim() != Game)
            throw new InvalidDataException("Bundled API source has an unexpected game version.");

        // Use the configured game platform, including Windows games under Proton on Linux.
        string platform = settings.PlatformLink(new Links
        {
            Windows = new Link { URL = "win-x64" }, Linux = new Link { URL = "linux-x64" }, Mac = new Link { URL = "osx-x64" }
        }).URL;
        string rid = platform;
        string config = Path.Combine(root, "NuGet.Config");
        await File.WriteAllTextAsync(config, "<configuration><packageSources><clear/><add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" /></packageSources></configuration>");
        await ToolRunner.RunAsync(dotnet, new[]
        {
            "restore", "Assembly-CSharp/Assembly-CSharp.csproj", "--configfile", config, "--runtime", rid,
            "--disable-parallel", "--verbosity", "normal"
        }, root, log, TimeSpan.FromMinutes(15));
        await ToolRunner.RunAsync(dotnet, new[]
        {
            "build", "Assembly-CSharp/Assembly-CSharp.csproj", "--no-restore", "--configuration", "Release",
            "--runtime", rid, "-p:CopyDir=", "-p:UseSharedCompilation=false", "-p:BuildInParallel=false", "-m:1", "--verbosity", "normal"
        }, root, log, TimeSpan.FromMinutes(15));
        string outputDir = Path.Combine(root, "OutputFinal");
        byte[] moddedAssembly = await File.ReadAllBytesAsync(Path.Combine(outputDir, "Assembly-CSharp.dll"));
        if (!GameAssembly.HasApi(physical, Path.Combine(outputDir, "Assembly-CSharp.dll")) ||
            !GameVersion.Same(GameAssembly.ReadVersion(moddedAssembly), Game))
            throw new InvalidDataException("Built API failed game-version/ModHooks validation.");
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            foreach (string file in Directory.GetFiles(outputDir).OrderBy(p => p, StringComparer.Ordinal))
                zip.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Optimal);
        return bytes.ToArray();
    }
}
