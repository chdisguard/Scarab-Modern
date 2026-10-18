using System.IO;
using System.Text.Json;
using Scarab.Compatibility;
using Xunit;

namespace Scarab.Tests;

public class ApiBuildSdkTests
{
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
