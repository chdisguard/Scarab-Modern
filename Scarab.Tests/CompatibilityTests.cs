using FakeItEasy;
using System;
using System.IO;
using System.IO.Abstractions.TestingHelpers;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Scarab.Compatibility;
using Scarab.Models;
using Xunit;

namespace Scarab.Tests;

public class CompatibilityTests
{
    private const string Url = "https://github.com/hk-modding/api/releases/download/1.5.78.11833-77/moddingapi.v77.windows.zip";

    [Theory]
    [InlineData("1.5.78.11833")]
    [InlineData("1.5.12459")]
    public void ReadsLengthPrefixedBuild(string game)
    {
        using var data = new MemoryStream();
        using var writer = new BinaryWriter(data, Encoding.UTF8, true);
        writer.Write(new byte[48]);
        writer.Write(game.Length);
        writer.Write(Encoding.UTF8.GetBytes(game));
        writer.Write(new byte[16]);
        Assert.Equal(game, GameBuild.Parse(data.ToArray()));
    }

    [Fact]
    public void DoesNotGuessFromUnstructuredVersionString() =>
        Assert.Throws<InvalidDataException>(() => GameBuild.Parse(Encoding.UTF8.GetBytes("garbage1.5.12459")));

    [Fact]
    public void RejectsNewGameWithOldApi() =>
        Assert.Throws<InvalidDataException>(() => ApiCompatibility.EnsureCompatible("1.5.12459", Url, 77));

    [Fact]
    public void AcceptsExactTarget() => ApiCompatibility.EnsureCompatible("1.5.78.11833", Url, 77);

    [Theory]
    [InlineData("https://evil.example/hk-modding/api/releases/download/1.5.78.11833-77/a.zip")]
    [InlineData("http://github.com/hk-modding/api/releases/download/1.5.78.11833-77/a.zip")]
    [InlineData("https://github.com/other/api/releases/download/1.5.78.11833-77/a.zip")]
    public void RejectsUnknownSource(string url) =>
        Assert.Throws<InvalidDataException>(() => ApiCompatibility.EnsureCompatible("1.5.78.11833", url, 77));

    private static byte[] Zip(params (string Name, string Content)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            foreach (var file in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(file.Name).Open());
                writer.Write(file.Content);
            }
        return buffer.ToArray();
    }

    [Fact]
    public async Task CommitFailureRestoresEveryFileAndDeletesNewOnes()
    {
        var fs = new MockFileSystem();
        string root = Path.GetFullPath("game/Managed");
        fs.AddFile(Path.Combine(root, "Assembly-CSharp.dll"), new MockFileData("vanilla"));
        fs.AddFile(Path.Combine(root, "UnityEngine.dll"), new MockFileData("unity"));
        await Assert.ThrowsAsync<IOException>(() => ApiFileTransaction.InstallAsync(fs, root,
            Zip(("Assembly-CSharp.dll", "modded"), ("New.dll", "new"), ("UnityEngine.dll", "patched")),
            true, "1.5.78.11833", 77, () => throw new IOException("State write failed")));
        Assert.Equal("vanilla", fs.File.ReadAllText(Path.Combine(root, "Assembly-CSharp.dll")));
        Assert.Equal("unity", fs.File.ReadAllText(Path.Combine(root, "UnityEngine.dll")));
        Assert.False(fs.File.Exists(Path.Combine(root, "New.dll")));
        Assert.False(fs.File.Exists(Path.Combine(root, "Assembly-CSharp.dll.v")));
        Assert.False(fs.File.Exists(Path.Combine(root, ApiReceipt.Filename)));
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("..\\escape.dll")]
    [InlineData("/absolute.dll")]
    [InlineData("Assembly-CSharp.dll.v")]
    public async Task RejectsEntireArchiveBeforeWriting(string malicious)
    {
        var fs = new MockFileSystem();
        string root = Path.GetFullPath("game/Managed");
        fs.AddFile(Path.Combine(root, "Assembly-CSharp.dll"), new MockFileData("vanilla"));
        await Assert.ThrowsAsync<InvalidDataException>(() => ApiFileTransaction.InstallAsync(fs, root,
            Zip(("Assembly-CSharp.dll", "modded"), (malicious, "bad")), true, "1.5.78.11833", 77, () => Task.CompletedTask));
        Assert.Equal("vanilla", fs.File.ReadAllText(Path.Combine(root, "Assembly-CSharp.dll")));
    }

    [Fact]
    public async Task SuccessfulInstallHasBackupAndVerifiableReceipt()
    {
        var fs = new MockFileSystem();
        string root = Path.GetFullPath("game/Managed");
        fs.AddFile(Path.Combine(root, "Assembly-CSharp.dll"), new MockFileData("vanilla"));
        await ApiFileTransaction.InstallAsync(fs, root, Zip(("Assembly-CSharp.dll", "modded")),
            true, "1.5.78.11833", 77, () => Task.CompletedTask);
        var receipt = ApiReceipt.Read(fs, root);
        Assert.NotNull(receipt);
        Assert.Equal("vanilla", Encoding.UTF8.GetString(receipt.Cached(fs, root, "Assembly-CSharp.dll", false)));
        Assert.True(ApiReceipt.Matches(fs, root, "Assembly-CSharp.dll", "1.5.78.11833", 77));
        Assert.False(ApiReceipt.Matches(fs, root, "Assembly-CSharp.dll", "1.5.12459", 77));
        fs.File.WriteAllText(Path.Combine(root, "Assembly-CSharp.dll"), "changed by Steam");
        Assert.False(ApiReceipt.Matches(fs, root, "Assembly-CSharp.dll", "1.5.78.11833", 77));
    }

    [Fact]
    public async Task Unity6ToggleRestoresLocalizationAndNativeFiles()
    {
        var fs = new MockFileSystem();
        string root = Path.GetFullPath("game/Managed");
        fs.AddFile(Path.Combine(root, "Assembly-CSharp.dll"), new MockFileData("vanilla-main"));
        fs.AddFile(Path.Combine(root, "TeamCherry.Localization.dll"), new MockFileData("vanilla-language"));
        byte[] bundle = Zip(("Assembly-CSharp.dll", "api-main"), ("TeamCherry.Localization.dll", "api-language"),
            ("libunityscenerepacker.so", "native"), ("MonoMod.Core.dll", "core"));
        await ApiFileTransaction.InstallAsync(fs, root, bundle, true, "1.5.12620", 78, () => Task.CompletedTask);
        await ApiFileTransaction.ToggleAsync(fs, root, "1.5.12620", false, () => Task.CompletedTask);
        Assert.Equal("vanilla-main", fs.File.ReadAllText(Path.Combine(root, "Assembly-CSharp.dll")));
        Assert.Equal("vanilla-language", fs.File.ReadAllText(Path.Combine(root, "TeamCherry.Localization.dll")));
        Assert.False(fs.File.Exists(Path.Combine(root, "libunityscenerepacker.so")));
        Assert.False(fs.File.Exists(Path.Combine(root, "MonoMod.Core.dll")));
        await ApiFileTransaction.ToggleAsync(fs, root, "1.5.12620", true, () => Task.CompletedTask);
        Assert.Equal("api-language", fs.File.ReadAllText(Path.Combine(root, "TeamCherry.Localization.dll")));
        Assert.Equal("native", fs.File.ReadAllText(Path.Combine(root, "libunityscenerepacker.so")));
    }

    [Fact]
    public async Task FailedToggleRestoresCompleteApi()
    {
        var fs = new MockFileSystem();
        string root = Path.GetFullPath("game/Managed");
        fs.AddFile(Path.Combine(root, "Assembly-CSharp.dll"), new MockFileData("vanilla"));
        await ApiFileTransaction.InstallAsync(fs, root,
            Zip(("Assembly-CSharp.dll", "modded"), ("TeamCherry.Localization.dll", "language")),
            true, "1.5.12620", 78, () => Task.CompletedTask);
        await Assert.ThrowsAsync<IOException>(() => ApiFileTransaction.ToggleAsync(fs, root, "1.5.12620", false,
            () => throw new IOException("state write failed")));
        Assert.Equal("modded", fs.File.ReadAllText(Path.Combine(root, "Assembly-CSharp.dll")));
        Assert.Equal("language", fs.File.ReadAllText(Path.Combine(root, "TeamCherry.Localization.dll")));
    }

    [Fact]
    public async Task ReinstallKeepsOriginalVanillaAndRestoresRemovedDependencies()
    {
        var fs = new MockFileSystem();
        string root = Path.GetFullPath("game/Managed");
        fs.AddFile(Path.Combine(root, "Assembly-CSharp.dll"), new MockFileData("vanilla"));
        await ApiFileTransaction.InstallAsync(fs, root, Zip(("Assembly-CSharp.dll", "api1"), ("Old.dll", "old")),
            true, "1.5.12620", 78, () => Task.CompletedTask);
        await ApiFileTransaction.InstallAsync(fs, root, Zip(("Assembly-CSharp.dll", "api2")),
            false, "1.5.12620", 78, () => Task.CompletedTask);
        Assert.False(fs.File.Exists(Path.Combine(root, "Old.dll")));
        await ApiFileTransaction.ToggleAsync(fs, root, "1.5.12620", false, () => Task.CompletedTask);
        Assert.Equal("vanilla", fs.File.ReadAllText(Path.Combine(root, "Assembly-CSharp.dll")));
    }

    [Fact]
    public async Task ChangedGameFileOrBackupBlocksToggleBeforeWriting()
    {
        var fs = new MockFileSystem();
        string root = Path.GetFullPath("game/Managed");
        fs.AddFile(Path.Combine(root, "Assembly-CSharp.dll"), new MockFileData("vanilla"));
        await ApiFileTransaction.InstallAsync(fs, root, Zip(("Assembly-CSharp.dll", "modded")),
            true, "1.5.12620", 78, () => Task.CompletedTask);
        var receipt = ApiReceipt.Read(fs, root);
        fs.File.WriteAllText(receipt.CachePath(root, "Assembly-CSharp.dll", false), "corrupted");
        await Assert.ThrowsAsync<InvalidDataException>(() => ApiFileTransaction.ToggleAsync(fs, root, "1.5.12620", false,
            () => Task.CompletedTask));
        Assert.Equal("modded", fs.File.ReadAllText(Path.Combine(root, "Assembly-CSharp.dll")));
    }

    [Fact]
    public async Task FailedStateWriteDoesNotChangeApiStateInMemory()
    {
        var fs = FakeItEasy.A.Fake<System.IO.Abstractions.IFileSystem>();
        FakeItEasy.A.CallTo(() => fs.File.Create(FakeItEasy.A<string>._)).Throws(new IOException("Disk full"));
        var state = new Scarab.Services.InstalledMods(fs);
        await Assert.ThrowsAsync<IOException>(() => state.RecordApiState(new InstalledState(true, new Version(78, 0, 0), true)));
        Assert.IsType<NotInstalledState>(state.ApiInstall);
    }

    private class ReleasesHandler : HttpMessageHandler
    {
        private readonly string _body;
        public ReleasesHandler(string body) => _body = body;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body) });
    }

    [Fact]
    public async Task FindsFutureApiForExactGameWithoutChangingCode()
    {
        string hash = new string('a', 64);
        string json = "[{\"tag_name\":\"1.5.12459-78\",\"draft\":false,\"prerelease\":false,\"body\":\"\",\"assets\":[{\"name\":\"moddingapi.v78.windows.zip\",\"digest\":\"sha256:" + hash + "\",\"browser_download_url\":\"https://github.com/hk-modding/api/releases/download/1.5.12459-78/moddingapi.v78.windows.zip\"}]}]";
        using var client = new HttpClient(new ReleasesHandler(json));
        var resolved = await OfficialApiResolver.ResolveAsync(client, "1.5.12459", (Links.FromSingle(Url, hash), 77), l => l.Windows);
        Assert.Equal(78, resolved.Version);
        Assert.Equal(hash, resolved.Link.Windows.SHA256);
    }

    [Fact]
    public async Task MissingReleaseNeverFallsBackToIncompatibleApi()
    {
        using var client = new HttpClient(new ReleasesHandler("[]"));
        await Assert.ThrowsAsync<InvalidDataException>(() => OfficialApiResolver.ResolveAsync(client, "1.5.12459",
            (Links.FromSingle(Url, new string('a', 64)), 77), l => l.Windows));
    }
}
