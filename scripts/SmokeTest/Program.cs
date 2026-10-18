using Scarab;
using Scarab.Models;
using Scarab.Services;
using Scarab.Compatibility;
using System.IO.Abstractions;
using System.Security.Cryptography;

if (args.Length < 2) throw new ArgumentException("Usage: SmokeTest <Managed path> <install|lifecycle|api|toggle-api> [mod name]");
if (args[1] is not ("install" or "lifecycle" or "api" or "toggle-api")) throw new ArgumentException("Unknown operation");
var settings = new Settings { ManagedFolder = Path.GetFullPath(args[0]), Platform = Settings.GamePlatform.Windows };
var fs = new FileSystem();
GameProcess.EnsureClosed();
Console.WriteLine($"Game: {GameBuild.Read(fs, settings.ManagedFolder)}");
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("Scarab-Modern-SmokeTest/2.8.4");
var links = await ModDatabase.FetchContent(http);
var source = await InstalledMods.Load(fs, settings, links.Item1);
var database = new ModDatabase(source, links);
var installer = new Installer(settings, source, database, fs, http);
var mod = database.Items.Single(x => x.Name == (args.Length > 2 ? args[2] : "DebugMod"));
if (args[1] == "toggle-api") { await installer.ToggleApi(); Console.WriteLine("API toggled"); return; }
if (args[1] == "api") { await installer.InstallApi(); Console.WriteLine("API installed"); return; }
await installer.Install(mod, _ => {}, true);
Console.WriteLine($"Installed {mod.Name} {mod.Version}; enabled={mod.Enabled}");
if (args[1] == "lifecycle")
{
    await installer.Toggle(mod); if (mod.Enabled) throw new Exception("Disable failed");
    await installer.Toggle(mod); if (!mod.Enabled) throw new Exception("Enable failed");
    await installer.Uninstall(mod); if (mod.Installed) throw new Exception("Uninstall failed");
    await installer.Install(mod, _ => {}, true);
    var receipt = ApiReceipt.Read(fs, settings.ManagedFolder) ?? throw new Exception("Receipt missing");
    await installer.ToggleApi();
    if (!receipt.CurrentMatches(fs, settings.ManagedFolder, false)) throw new Exception("Vanilla restoration failed");
    await installer.InstallApi();
    if (!receipt.CurrentMatches(fs, settings.ManagedFolder, true)) throw new Exception("API re-enable failed");
    Console.WriteLine("PASS: install, disable, enable, uninstall, reinstall, vanilla restore, API enable");
}
