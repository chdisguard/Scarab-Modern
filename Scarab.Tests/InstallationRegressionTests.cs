using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Abstractions.TestingHelpers;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mono.Cecil;
using Scarab.Compatibility;
using Scarab.Models;
using Scarab.Services;
using Scarab.Util;
using Xunit;

namespace Scarab.Tests;

public class InstallationRegressionTests
{
    private static byte[] Assembly(string version, bool api = false)
    {
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Game", new Version(1, 0)), "Game", ModuleKind.Dll);
        var module = assembly.MainModule;
        var constants = new TypeDefinition("", "Constants", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        constants.Fields.Add(new FieldDefinition("GAME_VERSION", FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault,
            module.TypeSystem.String) { Constant = version });
        module.Types.Add(constants);
        if (api) module.Types.Add(new TypeDefinition("Modding", "ModHooks", TypeAttributes.Public, module.TypeSystem.Object));
        using var output = new MemoryStream();
        assembly.Write(output);
        return output.ToArray();
    }
    private static byte[] GameData(string version)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        writer.Write(new byte[32]); writer.Write(version.Length); writer.Write(Encoding.UTF8.GetBytes(version)); writer.Write(new byte[32]);
        return output.ToArray();
    }
    private static ModItem Mod(string name, params string[] dependencies) => new(new NotInstalledState(), new Version(1, 0), dependencies,
        Links.FromSingle("https://example.test/mod.zip", new string('a', 64)), name, "", "", ImmutableArray<Tag>.Empty, Array.Empty<string>(), Array.Empty<string>());
    private static string Root => Path.GetFullPath("game/Managed");

    [Theory]
    [InlineData("1.5.12620", "1.5.12620.0")]
    [InlineData("1.5.78.11833", "1.5.78.11833")]
    public void EquivalentGameVersionsAreAccepted(string a, string b) => Assert.True(GameVersion.Same(a, b));

    [Fact]
    public void CleanAssemblyWorksWhenUnityVersionStringCannotBeRead()
    {
        var fs = new MockFileSystem();
        fs.AddFile(Path.Combine(Root, "Assembly-CSharp.dll"), new MockFileData(Assembly("1.5.12620.0")));
        fs.AddFile(Path.GetFullPath(Path.Combine(Root, "..", "globalgamemanagers")), new MockFileData("Unity data without a version string"));
        Assert.Equal("1.5.12620", GameBuild.Read(fs, Root));
    }

    [Fact]
    public void WrongApiAssemblyCannotOverrideNewGameData()
    {
        var fs = new MockFileSystem();
        fs.AddFile(Path.Combine(Root, "Assembly-CSharp.dll"), new MockFileData(Assembly("1.5.78.11833", true)));
        fs.AddFile(Path.GetFullPath(Path.Combine(Root, "..", "globalgamemanagers")), new MockFileData(GameData("1.5.12620.0")));
        Assert.Throws<InvalidDataException>(() => GameBuild.Read(fs, Root));
    }

    [Fact]
    public void DependencyCycleIsReportedBeforeInstallation()
    {
        var a = Mod("A", "B"); var b = Mod("B", "A");
        Assert.Throws<InvalidDataException>(() => DependencyPlanner.Order(a, new[] { a, b }));
    }
    [Fact]
    public void MissingDependencyHasReadableError()
    {
        var a = Mod("A", "Missing");
        var error = Assert.Throws<InvalidDataException>(() => DependencyPlanner.Order(a, new[] { a }));
        Assert.Contains("Missing", error.Message);
    }
    [Fact]
    public void SharedDependenciesAreInstalledOnceInOrder()
    {
        var root = Mod("Root", "A", "B"); var a = Mod("A", "Shared"); var b = Mod("B", "Shared"); var shared = Mod("Shared");
        Assert.Equal(new[] { "Shared", "A", "B", "Root" }, DependencyPlanner.Order(root, new[] { root, a, b, shared }).Select(m => m.Name));
    }

    [Fact]
    public void DllUsesOnlyTheDownloadedSegment()
    {
        byte[] bytes = { 9, 9, (byte)'M', (byte)'Z', 1, 2, 9, 9 };
        var files = ModPackage.Read(new ArraySegment<byte>(bytes, 2, 4), "Actual.dll");
        Assert.Equal(new byte[] { (byte)'M', (byte)'Z', 1, 2 }, files["Actual.dll"]);
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("C:\\escape.dll")]
    [InlineData("/escape.dll")]
    [InlineData("safe/../../escape.dll")]
    [InlineData(".. /escape.dll")]
    [InlineData("NUL.dll")]
    [InlineData("folder/COM1.txt")]
    public void ModArchiveCannotEscapeItsDirectory(string name) => Assert.Throws<InvalidDataException>(() => ModPackage.SafeRelative(name));

    [Fact]
    public async Task FailedInstallRestoresPreviousDirectoryAndSettings()
    {
        var fs = new MockFileSystem();
        string old = Path.Combine(Root, "Mods", "Example");
        fs.AddFile(Path.Combine(old, "Example.dll"), new MockFileData("old"));
        fs.AddFile(Path.Combine(old, "custom.json"), new MockFileData("settings"));
        await Assert.ThrowsAsync<IOException>(() => ModFileTransaction.InstallAsync(fs, Root, "Example", true,
            new Dictionary<string, byte[]> { ["Example.dll"] = Encoding.UTF8.GetBytes("new") }, () => throw new IOException("Disk full")));
        Assert.Equal("old", fs.File.ReadAllText(Path.Combine(old, "Example.dll")));
        Assert.Equal("settings", fs.File.ReadAllText(Path.Combine(old, "custom.json")));
    }

    [Fact]
    public async Task UpdateRemovesRetiredOwnedFilesAndPreservesUserAssets()
    {
        var fs = new MockFileSystem(); fs.AddDirectory(Root);
        await ModFileTransaction.InstallAsync(fs, Root, "Example", false, new Dictionary<string, byte[]>
        {
            ["Example.dll"] = Encoding.UTF8.GetBytes("old"), ["Retired.dll"] = Encoding.UTF8.GetBytes("retired")
        }, () => Task.CompletedTask);
        fs.AddFile(Path.Combine(Root, "Mods", "Disabled", "Example", "Skins", "mine.png"), new MockFileData("skin"));
        await ModFileTransaction.InstallAsync(fs, Root, "Example", true,
            new Dictionary<string, byte[]> { ["Example.dll"] = Encoding.UTF8.GetBytes("new") }, () => Task.CompletedTask);
        string target = Path.Combine(Root, "Mods", "Example");
        Assert.Equal("new", fs.File.ReadAllText(Path.Combine(target, "Example.dll")));
        Assert.Equal("skin", fs.File.ReadAllText(Path.Combine(target, "Skins", "mine.png")));
        Assert.False(fs.File.Exists(Path.Combine(target, "Retired.dll")));
        Assert.False(fs.Directory.Exists(Path.Combine(Root, "Mods", "Disabled", "Example")));
    }

    [Fact]
    public async Task FailedUninstallRestoresMod()
    {
        var fs = new MockFileSystem(); string path = Path.Combine(Root, "Mods", "Example", "Example.dll");
        fs.AddFile(path, new MockFileData("old"));
        await Assert.ThrowsAsync<IOException>(() => ModFileTransaction.UninstallAsync(fs, Root, "Example", true, () => throw new IOException("state failure")));
        Assert.Equal("old", fs.File.ReadAllText(path));
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Count;
        public Func<int, HttpResponseMessage> Reply;
        public Handler(Func<int, HttpResponseMessage> reply) => Reply = reply;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(Reply(++Count));
    }
    [Fact]
    public async Task DownloadFilenameIgnoresQueryAndReturnedBytesHaveNoPadding()
    {
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) }));
        var result = await DownloadService.FetchAsync(client, "https://example.test/Actual.dll?download=1", _ => { });
        Assert.Equal("Actual.dll", result.Filename);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Data.ToArray());
    }
    [Fact]
    public async Task TemporaryServerErrorIsRetried()
    {
        var handler = new Handler(n => new HttpResponseMessage(n == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1 }) });
        using var client = new HttpClient(handler);
        await DownloadService.FetchAsync(client, "https://example.test/mod.zip", _ => { });
        Assert.Equal(2, handler.Count);
    }
    [Fact]
    public async Task NotFoundIsNotRetried()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => DownloadService.FetchAsync(client, "https://example.test/missing.zip", _ => { }));
        Assert.Equal(1, handler.Count);
    }
    [Fact]
    public async Task TruncatedDownloadIsRejected()
    {
        using var client = new HttpClient(new Handler(_ =>
        {
            var content = new ByteArrayContent(new byte[] { 1, 2, 3 }); content.Headers.ContentLength = 9;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));
        await Assert.ThrowsAsync<IOException>(() => client.DownloadBytesWithProgressAsync(new Uri("https://example.test/file"), new Progress<DownloadProgressArgs>()));
    }
}
