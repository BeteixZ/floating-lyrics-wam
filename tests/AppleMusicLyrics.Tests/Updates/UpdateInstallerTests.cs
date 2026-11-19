using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AppleMusicLyrics.Core.Updates;
using AppleMusicLyrics.Infrastructure.Windows.Updates;
using Xunit;

namespace AppleMusicLyrics.Tests.Updates;

public sealed class UpdateInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aml-update-tests-" + Guid.NewGuid().ToString("N"));

    public UpdateInstallerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void ParseRelease_PicksTheVersionedWindowsPackageAndItsDigest()
    {
        var digest = new string('a', 64);
        using var json = JsonDocument.Parse($$"""
            {
              "tag_name": "v0.9.0", "name": "AppleMusicLyrics v0.9.0", "draft": false,
              "html_url": "https://github.com/BeteixZ/floating-lyrics-wam/releases/tag/v0.9.0",
              "body": "## v0.9.0",
              "assets": [
                { "name": "notes.txt", "browser_download_url": "https://example.com/notes.txt", "size": 5 },
                { "name": "Other-win-x64.zip", "browser_download_url": "https://example.com/other.zip", "size": 9 },
                { "name": "AppleMusicLyrics-v0.9.0-win-x64.zip", "size": 1234, "digest": "sha256:{{digest.ToUpperInvariant()}}",
                  "browser_download_url": "https://github.com/BeteixZ/floating-lyrics-wam/releases/download/v0.9.0/AppleMusicLyrics-v0.9.0-win-x64.zip" }
              ]
            }
            """);

        var release = GitHubReleaseClient.ParseRelease(json.RootElement);

        Assert.NotNull(release);
        Assert.Equal(new AppVersion(0, 9, 0), release.Version);
        Assert.Equal("## v0.9.0", release.Notes);
        Assert.Equal("AppleMusicLyrics-v0.9.0-win-x64.zip", release.Package!.Name);
        Assert.Equal(1234, release.Package.Size);
        Assert.Equal(digest, release.Package.Sha256);
    }

    [Theory]
    [InlineData("""{ "tag_name": "v1.0.0", "draft": true }""")]
    [InlineData("""{ "tag_name": "nightly", "draft": false }""")]
    public void ParseRelease_IgnoresDraftsAndNonVersionTags(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Null(GitHubReleaseClient.ParseRelease(document.RootElement));
    }

    [Fact]
    public void ParseRelease_RejectsNonHttpsDownloads()
    {
        using var json = JsonDocument.Parse("""
            { "tag_name": "v0.9.0", "assets": [ { "name": "AppleMusicLyrics-v0.9.0-win-x64.zip", "size": 1,
              "browser_download_url": "http://example.com/AppleMusicLyrics-v0.9.0-win-x64.zip" } ] }
            """);

        Assert.Null(GitHubReleaseClient.ParseRelease(json.RootElement)!.Package);
    }

    [Fact]
    public async Task DownloadAndStage_VerifiesAndExtractsThePackage()
    {
        var zip = CreateZip(("AppleMusicLyrics.App.exe", "new exe"), ("AppleMusicLyrics.Core.dll", "core"), ("Languages/en-US.json", "{}"));
        var installer = new UpdateInstaller(Path.Combine(_root, "updates"));
        var progress = new List<double>();

        var staging = await installer.DownloadAndStageAsync(Client(zip), Release(zip, Sha256(zip)),
            new SynchronousProgress(progress.Add));

        Assert.Equal("new exe", File.ReadAllText(Path.Combine(staging, "AppleMusicLyrics.App.exe")));
        Assert.True(File.Exists(Path.Combine(staging, "Languages", "en-US.json")));
        Assert.Equal(1.0, progress[^1]);
        Assert.Empty(Directory.GetFiles(installer.UpdatesRoot, "*.part"));
    }

    [Fact]
    public async Task DownloadAndStage_RejectsAChecksumMismatch()
    {
        var zip = CreateZip(("AppleMusicLyrics.App.exe", "exe"));
        var installer = new UpdateInstaller(Path.Combine(_root, "updates"));

        var error = await Assert.ThrowsAsync<UpdateException>(() =>
            installer.DownloadAndStageAsync(Client(zip), Release(zip, new string('0', 64))));

        Assert.Equal(UpdateFailure.Verification, error.Failure);
        Assert.Empty(Directory.GetDirectories(installer.UpdatesRoot));
    }

    [Fact]
    public async Task DownloadAndStage_RejectsATruncatedDownload()
    {
        var zip = CreateZip(("AppleMusicLyrics.App.exe", "exe"));
        var installer = new UpdateInstaller(Path.Combine(_root, "updates"));
        var release = Release(zip, sha256: null) with { };
        release = release with { Package = release.Package! with { Size = zip.Length + 10 } };

        var error = await Assert.ThrowsAsync<UpdateException>(() => installer.DownloadAndStageAsync(Client(zip), release));

        Assert.Equal(UpdateFailure.Verification, error.Failure);
    }

    [Fact]
    public async Task DownloadAndStage_RejectsAPackageWithoutTheApp()
    {
        var zip = CreateZip(("readme.txt", "hello"));
        var installer = new UpdateInstaller(Path.Combine(_root, "updates"));

        var error = await Assert.ThrowsAsync<UpdateException>(() =>
            installer.DownloadAndStageAsync(Client(zip), Release(zip, Sha256(zip))));

        Assert.Equal(UpdateFailure.InvalidPackage, error.Failure);
    }

    [Fact]
    public async Task DownloadAndStage_RejectsEntriesThatEscapeTheStagingFolder()
    {
        var zip = CreateZip(("AppleMusicLyrics.App.exe", "exe"), ("../../escaped.txt", "bad"));
        var installer = new UpdateInstaller(Path.Combine(_root, "updates"));

        var error = await Assert.ThrowsAsync<UpdateException>(() =>
            installer.DownloadAndStageAsync(Client(zip), Release(zip, Sha256(zip))));

        Assert.Equal(UpdateFailure.InvalidPackage, error.Failure);
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
    }

    [Fact]
    public void Apply_CopiesTheNewVersionOverTheInstallFolderAndKeepsOtherFiles()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "staging")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(_root, "install")).FullName;
        File.WriteAllText(Path.Combine(source, UpdateInstaller.ExecutableName), "new");
        Directory.CreateDirectory(Path.Combine(source, "Languages"));
        File.WriteAllText(Path.Combine(source, "Languages", "en-US.json"), "new strings");
        File.WriteAllText(Path.Combine(target, UpdateInstaller.ExecutableName), "old");
        File.WriteAllText(Path.Combine(target, "user-notes.txt"), "keep me");
        var log = new List<string>();

        Assert.True(UpdateInstaller.Apply(source, target, processId: 0, TimeSpan.FromSeconds(1), log.Add));

        Assert.Equal("new", File.ReadAllText(Path.Combine(target, UpdateInstaller.ExecutableName)));
        Assert.Equal("new strings", File.ReadAllText(Path.Combine(target, "Languages", "en-US.json")));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(target, "user-notes.txt")));
    }

    [Fact]
    public void Apply_RefusesAFolderThatDoesNotContainTheApp()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "staging")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        File.WriteAllText(Path.Combine(source, UpdateInstaller.ExecutableName), "new");

        Assert.False(UpdateInstaller.Apply(source, target, 0, TimeSpan.Zero, _ => { }));
        Assert.Empty(Directory.GetFiles(target));
    }

    [Fact]
    public void Apply_RefusesASourceThatIsNotAnAppPackage()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "random")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(_root, "install")).FullName;
        File.WriteAllText(Path.Combine(source, "payload.txt"), "x");
        File.WriteAllText(Path.Combine(target, UpdateInstaller.ExecutableName), "old");

        Assert.False(UpdateInstaller.Apply(source, target, 0, TimeSpan.Zero, _ => { }));
        Assert.False(File.Exists(Path.Combine(target, "payload.txt")));
    }

    [Fact]
    public void PrepareHelper_CopiesTheRunningExecutableOutsideTheInstallFolder()
    {
        var install = Directory.CreateDirectory(Path.Combine(_root, "install")).FullName;
        var current = Path.Combine(install, UpdateInstaller.ExecutableName);
        File.WriteAllText(current, "current version");
        var installer = new UpdateInstaller(Path.Combine(_root, "updates"));

        var helper = installer.PrepareHelper(current);

        Assert.StartsWith(installer.UpdatesRoot, helper);
        Assert.Equal(UpdateInstaller.ExecutableName, Path.GetFileName(helper));
        Assert.Equal("current version", File.ReadAllText(helper));
    }

    [Fact]
    public void ApplyArguments_RoundTripThroughTheStartInfo()
    {
        var start = UpdateInstaller.CreateApplyStartInfo(@"C:\updates\helper-1\AppleMusicLyrics.App.exe",
            @"C:\updates\staging-0.9.0", @"D:\Apps\Apple Music Lyrics", 4242);

        Assert.Equal(@"C:\updates\helper-1\AppleMusicLyrics.App.exe", start.FileName);
        Assert.True(UpdateInstaller.TryParseApplyArguments(start.ArgumentList.ToArray(),
            out var source, out var target, out var pid));
        Assert.Equal(@"C:\updates\staging-0.9.0", source);
        Assert.Equal(@"D:\Apps\Apple Music Lyrics", target);
        Assert.Equal(4242, pid);
        Assert.False(UpdateInstaller.TryParseApplyArguments(["--updated"], out _, out _, out _));
        Assert.False(UpdateInstaller.TryParseApplyArguments(["--apply-update", "--target", "D:\\x"], out _, out _, out _));
    }

    [Fact]
    public void CleanUp_RemovesOldStagingButKeepsTheRunningFolder()
    {
        var installer = new UpdateInstaller(Path.Combine(_root, "updates"));
        var old = Directory.CreateDirectory(Path.Combine(installer.UpdatesRoot, "staging-0.8.0")).FullName;
        var oldHelper = Directory.CreateDirectory(Path.Combine(installer.UpdatesRoot, "helper-abc")).FullName;
        var running = Directory.CreateDirectory(Path.Combine(installer.UpdatesRoot, "staging-0.9.0")).FullName;
        File.WriteAllText(Path.Combine(installer.UpdatesRoot, "0.9.1.zip.part"), "partial");

        installer.CleanUp(running);

        Assert.False(Directory.Exists(old));
        Assert.False(Directory.Exists(oldHelper));
        Assert.True(Directory.Exists(running));
        Assert.Empty(Directory.GetFiles(installer.UpdatesRoot, "*.part"));
    }

    private static ReleaseInfo Release(byte[] zip, string? sha256) => new(
        new AppVersion(0, 9, 0), "v0.9.0", string.Empty, new Uri("https://example.com/release"),
        new ReleaseAsset("AppleMusicLyrics-v0.9.0-win-x64.zip", new Uri("https://example.com/package.zip"), zip.Length, sha256));

    private static HttpClient Client(byte[] body) => new(new StaticHandler(body));

    private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static byte[] CreateZip(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(content);
            }
        }
        return stream.ToArray();
    }

    private sealed class StaticHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    // Progress<T> posts to a captured context; tests need the values immediately.
    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
