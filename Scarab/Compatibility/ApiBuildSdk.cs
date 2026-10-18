using System.Text.Json;
using System.Text.RegularExpressions;

namespace Scarab.Compatibility;

/// <summary>Keep local API builds on the supported stable .NET 8 toolchain.</summary>
public static class ApiBuildSdk
{
    // Releases carry an official portable SDK; source builds can use the system SDK.
    public static string ResolveExecutable(string baseDirectory)
    {
        string bundled = Path.Combine(baseDirectory, "tools", "dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return File.Exists(bundled) ? bundled : "dotnet";
    }

    public static string Select(string installed)
    {
        var versions = Regex.Matches(installed, @"(?m)^\s*(8\.0\.\d+)\s+\[")
            .Cast<Match>().Select(m => Version.Parse(m.Groups[1].Value)).OrderByDescending(v => v);
        return versions.FirstOrDefault()?.ToString() ?? throw new IOException(
            "Для сборки API установите стабильный .NET SDK 8.0 x64: https://dotnet.microsoft.com/download/dotnet/8.0 . " +
            "Нужен SDK, одного Runtime недостаточно. SDK 9/10 не заменяет SDK 8 для этой сборки.");
    }

    public static string GlobalJson(string version) => JsonSerializer.Serialize(new
    {
        sdk = new { version, rollForward = "disable", allowPrerelease = false }
    });
}
