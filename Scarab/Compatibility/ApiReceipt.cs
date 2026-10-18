using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text.Json;

namespace Scarab.Compatibility;

public record ApiFileRecord(string? VanillaSha256, string ModdedSha256);
public record ApiReceipt(string Game, int Api, string Source, string Batch, Dictionary<string, ApiFileRecord> Files)
{
    public const string Filename = ".scarab-api.json";
    private static bool ValidHash(string? value) => value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static bool SafeName(string name) => !string.IsNullOrWhiteSpace(name) && name != "." && name != ".." &&
        name.IndexOfAny(new[] { '/', '\\', ':' }) < 0 && Path.GetFileName(name) == name;

    public static ApiReceipt? Read(IFileSystem fs, string managed)
    {
        try
        {
            var r = JsonSerializer.Deserialize<ApiReceipt>(fs.File.ReadAllText(Path.Combine(managed, Filename)));
            if (r is null || !Version.TryParse(r.Game, out var version) || version.Major != 1 || r.Api <= 0 || r.Files is null || !r.Files.ContainsKey("Assembly-CSharp.dll") ||
                !Guid.TryParseExact(r.Batch, "N", out _) || r.Files.Keys.Any(n => !SafeName(n)) ||
                r.Files.Values.Any(v => v is null || !ValidHash(v.ModdedSha256) ||
                    (v.VanillaSha256 is not null && !ValidHash(v.VanillaSha256)))) return null;
            return r;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public string CachePath(string managed, string name, bool enabled) =>
        Path.Combine(managed, ".scarab-api", Batch, enabled ? "modded" : "vanilla", name);

    public bool CurrentMatches(IFileSystem fs, string managed, bool enabled)
    {
        foreach (var (name, record) in Files)
        {
            string? hash = enabled ? record.ModdedSha256 : record.VanillaSha256;
            string path = Path.Combine(managed, name);
            if (hash is null) { if (fs.File.Exists(path)) return false; }
            else if (!fs.File.Exists(path) || Hash(fs.File.ReadAllBytes(path)) != hash) return false;
        }
        return true;
    }

    public byte[]? Cached(IFileSystem fs, string managed, string name, bool enabled)
    {
        var record = Files[name];
        string? hash = enabled ? record.ModdedSha256 : record.VanillaSha256;
        if (hash is null) return null;
        byte[] bytes = fs.File.ReadAllBytes(CachePath(managed, name, enabled));
        if (Hash(bytes) != hash) throw new InvalidDataException($"API backup checksum mismatch: {name}");
        return bytes;
    }

    public static bool Matches(IFileSystem fs, string managed, string assembly, string game, int api)
    {
        var r = Read(fs, managed);
        return r is not null && GameVersion.Same(r.Game, game) && r.Api == api && r.CurrentMatches(fs, managed, true);
    }

    public static bool VanillaMatches(IFileSystem fs, string managed, string game)
    {
        var r = Read(fs, managed);
        if (r is null || !GameVersion.Same(r.Game, game)) return false;
        try
        {
            foreach (string name in r.Files.Keys) r.Cached(fs, managed, name, false);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
