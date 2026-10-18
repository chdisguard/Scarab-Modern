using System.IO;
using System.Text.Json;
using Scarab.Compatibility;
using Xunit;

namespace Scarab.Tests;

public class ApiBuildSdkTests
{
    [Fact]
    public void PortableSdkIsPreferredWithoutRequiringDotnetOnPath()
    {
        string root = Path.Combine(Path.GetTempPath(), "scarab-sdk-" + System.Guid.NewGuid().ToString("N"));
        string host = Path.Combine(root, "tools", "dotnet", System.OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        try
        {
            Assert.Equal("dotnet", ApiBuildSdk.ResolveExecutable(root));
            Directory.CreateDirectory(Path.GetDirectoryName(host)!);
            File.WriteAllBytes(host, System.Array.Empty<byte>());
            Assert.Equal(host, ApiBuildSdk.ResolveExecutable(root));
        }
        finally
        {
            if (File.Exists(host)) File.Delete(host);
            if (Directory.Exists(Path.GetDirectoryName(host))) Directory.Delete(Path.GetDirectoryName(host)!);
            if (Directory.Exists(Path.Combine(root, "tools"))) Directory.Delete(Path.Combine(root, "tools"));
            if (Directory.Exists(root)) Directory.Delete(root);
        }
    }

    [Theory]
    [InlineData("10.0.401 [C:/dotnet/sdk]\n8.0.419 [C:/dotnet/sdk]\n8.0.99 [C:/dotnet/sdk]", "8.0.419")]
    [InlineData("8.0.500-preview.1 [/dotnet/sdk]\r\n8.0.408 [/dotnet/sdk]", "8.0.408")]
    public void ChoosesStableEightDespiteNewerInstalledSdk(string installed, string expected)
    {
        string selected = ApiBuildSdk.Select(installed);
        Assert.Equal(expected, selected);
        using var config = JsonDocument.Parse(ApiBuildSdk.GlobalJson(selected));
        var sdk = config.RootElement.GetProperty("sdk");
        Assert.Equal(expected, sdk.GetProperty("version").GetString());
        Assert.Equal("disable", sdk.GetProperty("rollForward").GetString());
        Assert.False(sdk.GetProperty("allowPrerelease").GetBoolean());
    }

    [Theory]
    [InlineData("")]
    [InlineData("10.0.401 [C:/dotnet/sdk]")]
    [InlineData("8.0.500-preview.1 [/dotnet/sdk]")]
    public void RejectsMissingCompatibleStableSdkBeforeBuild(string installed) =>
        Assert.Throws<IOException>(() => ApiBuildSdk.Select(installed));
}
