using AppleMusicLyrics.Core.Parsing;
using AppleMusicLyrics.Infrastructure.Windows.Cache;
using Xunit;

namespace AppleMusicLyrics.Tests.Cache;

public sealed class AppleMusicCacheScannerTests : IDisposable
{
    private readonly string _root;

    public AppleMusicCacheScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AppleMusicLyrics.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void FindLyricsFiles_FindsNestedJsonFiles()
    {
        var inetCache = CreateDirectory("INetCache", "A", "B");
        var lyricsFile = Path.Combine(inetCache, "ttmlLyrics-1.json");
        File.WriteAllText(lyricsFile, BuildLyricsJson("a", "0.000", "1.000", "hello"));
        File.WriteAllText(Path.Combine(inetCache, "not-lyrics.json"), "{}");

        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), roots: [_root]);

        var files = scanner.FindLyricsFiles([_root]);

        Assert.Single(files);
        Assert.Equal(lyricsFile, files[0]);
    }

    [Fact]
    public void GetLatestRecentFile_SkipsSeenLyricsIds()
    {
        var first = CreateLyricsFile("ttmlLyrics-1.json", "seen", minutesAgo: 1);
        var second = CreateLyricsFile("ttmlLyrics-2.json", "new", minutesAgo: 0);

        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), roots: [_root], startedAt: startedAt);

        var latest = scanner.GetLatestRecentFile([first, second], startedAt, new HashSet<string> { "seen" });

        Assert.Equal(second, latest);
    }

    [Fact]
    public async Task GetLatestLyricsAsync_ParsesNewestLyricsFile()
    {
        _ = CreateLyricsFile("ttmlLyrics-old.json", "old", minutesAgo: 1, text: "old line");
        var expected = CreateLyricsFile("ttmlLyrics-new.json", "new", minutesAgo: 0, text: "new line");

        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), roots: [_root], startedAt: startedAt);

        var document = await scanner.GetLatestLyricsAsync();

        Assert.NotNull(document);
        Assert.Equal(expected, document!.SourceFile);
        Assert.Equal("new", document.LyricsId);
        Assert.Equal("new line", Assert.Single(document.Lines).Text);
    }

    [Fact]
    public async Task GetLatestLyricsAsync_ReturnsNullForMalformedJsonInsteadOfThrowing()
    {
        var path = Path.Combine(CreateDirectory("INetCache", "A"), "ttmlLyrics-bad.json");
        File.WriteAllText(path, "{ bad json");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);

        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), roots: [_root], startedAt: startedAt);

        var document = await scanner.GetLatestLyricsAsync();

        Assert.Null(document);
    }

    [Fact]
    public async Task FindBestLyricsAsync_PrefersCachedSongThatMatchesPlayerDurationAndTitle()
    {
        _ = CreateLyricsFile(
            "ttmlLyrics-other.json",
            "other",
            minutesAgo: 10,
            text: "some other intro",
            bodyDuration: "3:30.000");

        var expected = CreateLyricsFile(
            "ttmlLyrics-rap-god.json",
            "rap-god",
            minutesAgo: 120,
            text: "I'm beginning to feel like a Rap God, Rap God",
            bodyDuration: "6:03.521");

        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), roots: [_root]);
        var player = new AppleMusicLyrics.Core.Models.PlayerState(
            Title: "Rap God",
            Artist: "Eminem",
            Album: "The Marshall Mathers LP2",
            Position: 12,
            Duration: 363.521,
            Playing: true);

        var document = await scanner.FindBestLyricsAsync(player);

        Assert.NotNull(document);
        Assert.Equal(expected, document!.SourceFile);
        Assert.Equal("rap-god", document.LyricsId);
    }

    private string CreateLyricsFile(
        string fileName,
        string lyricsId,
        int minutesAgo,
        string text = "hello",
        string bodyDuration = "1.000")
    {
        var path = Path.Combine(CreateDirectory("INetCache", "A"), fileName);
        File.WriteAllText(path, BuildLyricsJson(lyricsId, "0.000", "1.000", text, bodyDuration));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutesAgo));
        return path;
    }

    private string CreateDirectory(params string[] parts)
    {
        var path = parts.Aggregate(_root, Path.Combine);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string BuildLyricsJson(string lyricsId, string begin, string end, string text, string bodyDuration = "1.000")
    {
        return $$"""
        {
          "lyricsId": "{{lyricsId}}",
          "status": "ok",
          "ttml": "<tt xmlns=\"http://www.w3.org/ns/ttml\"><body dur=\"{{bodyDuration}}\"><div><p begin=\"{{begin}}\" end=\"{{end}}\">{{text}}</p></div></body></tt>"
        }
        """;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
