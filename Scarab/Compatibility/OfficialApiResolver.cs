using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Scarab.Compatibility;

/// <summary>Discovers stable upstream releases for the exact installed game build.</summary>
public static class OfficialApiResolver
{
    public static async Task<(Links Link, int Version)> ResolveAsync(
        HttpClient client, string game, (Links Link, int Version) fallback,
        Func<Links, Link> platformLink)
    {
        // A compatible ModLinks manifest is authoritative and also works without GitHub API access.
        try
        {
            ApiCompatibility.EnsureCompatible(game, platformLink(fallback.Link).URL, fallback.Version);
            return fallback;
        }
        catch (InvalidDataException) { }

        // ModLinks can lag behind a new release. Only stable, non-draft official releases qualify.
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/hk-modding/api/releases?per_page=100");
        request.Headers.UserAgent.ParseAdd("Scarab-Modern/2.8");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await client.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var releases = json.RootElement.EnumerateArray()
            .Where(r => !r.GetProperty("draft").GetBoolean() && !r.GetProperty("prerelease").GetBoolean())
            .Select(r => (Release: r, Tag: ApiCompatibility.ParseTag(r.GetProperty("tag_name").GetString() ?? "")))
            .Where(r => r.Tag is { } t && GameVersion.Same(t.Game, game))
            .OrderByDescending(r => r.Tag!.Value.Api);
        foreach (var release in releases)
        {
            var links = new Links();
            string body = release.Release.GetProperty("body").GetString() ?? "";
            foreach (var asset in release.Release.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                string platform = name.EndsWith(".windows.zip", StringComparison.Ordinal) ? "Windows" :
                    name.EndsWith(".macos.zip", StringComparison.Ordinal) ? "MacOS" :
                    name.EndsWith(".linux.zip", StringComparison.Ordinal) ? "Linux" : "";
                if (platform.Length == 0) continue;
                string? hash = asset.TryGetProperty("digest", out var digest) ? digest.GetString() : null;
                if (hash?.StartsWith("sha256:", StringComparison.Ordinal) == true) hash = hash[7..];
                else
                {
                    Match match = Regex.Match(body, Regex.Escape(platform) + @":\s*`?([a-fA-F0-9]{64})`?");
                    hash = match.Success ? match.Groups[1].Value : null;
                }
                if (hash is null || !Regex.IsMatch(hash, @"\A[a-fA-F0-9]{64}\z")) continue;
                var link = new Link { URL = asset.GetProperty("browser_download_url").GetString()!, SHA256 = hash };
                if (platform == "Windows") links.Windows = link;
                else if (platform == "MacOS") links.Mac = link;
                else links.Linux = link;
            }
            Link selected;
            try { selected = platformLink(links); }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException or ArgumentNullException) { continue; }
            if (selected is null) continue;
            ApiCompatibility.EnsureCompatible(game, selected.URL, release.Tag!.Value.Api);
            return (links, release.Tag.Value.Api);
        }
        throw new InvalidDataException($"No stable official Modding API for Hollow Knight {game} was found. No files were changed. Для {game} не найден совместимый официальный API. Проверьте https://github.com/hk-modding/api/releases. API v77 предназначен для 1.5.78.11833.");
    }
}
