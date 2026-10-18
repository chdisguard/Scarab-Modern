using System.Text.RegularExpressions;

namespace Scarab.Compatibility;

public static class ApiCompatibility
{
    // The official release tag encodes both the game build and the Modding API build.
    public static (string Game, int Api)? ParseTag(string tag)
    {
        Match m = Regex.Match(tag, @"^(1\.\d+\.\d+(?:\.\d+)?)-(\d+)$");
        return m.Success && int.TryParse(m.Groups[2].Value, out int version)
            ? (m.Groups[1].Value, version) : null;
    }

    public static void EnsureCompatible(string game, string url, int api)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "github.com")
            throw new InvalidDataException("API download must be an official HTTPS GitHub release.");
        string[] parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 6 || parts[0] != "hk-modding" || parts[1] != "api" ||
            parts[2] != "releases" || parts[3] != "download")
            throw new InvalidDataException("Unknown API release source; installation blocked.");
        var target = ParseTag(Uri.UnescapeDataString(parts[4]));
        if (target is null || !GameVersion.Same(target.Value.Game, game) || target.Value.Api != api)
            throw new InvalidDataException($"Hollow Knight {game}: this API targets {target?.Game ?? "an unknown game build"}. Installation blocked. Нужен API именно для версии {game}; старый API может вызвать вылет игры. https://github.com/hk-modding/api/releases");
    }
}
