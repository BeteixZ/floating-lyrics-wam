using System.Text.Json;
using AppleMusicLyrics.Application.Services;
using AppleMusicLyrics.Core.Abstractions;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Sync;
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
    public async Task GetLatestLyricsAsync_ParsesNewestLyricsFile()
    {
        _ = CreateLyricsFile("ttmlLyrics-old.json", "old", minutesAgo: 1, text: "old line");
        var expected = CreateLyricsFile("ttmlLyrics-new.json", "new", minutesAgo: 0, text: "new line");

        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), roots: [_root]);

        var document = await scanner.GetLatestLyricsAsync();

        Assert.NotNull(document);
        Assert.Equal(expected, document!.SourceFile);
        Assert.Equal("new", document.LyricsId);
        Assert.Equal("new line", Assert.Single(document.Lines).Text);
    }

    [Fact]
    public async Task GetLatestLyricsAsync_ReturnsTheSameFileWhenAskedRepeatedly()
    {
        // Regression: lyricsIds used to be blacklisted once returned, so replaying a track (repeat,
        // or skipping back) silently fell through to whatever else happened to be in the cache.
        var expected = CreateLyricsFile("ttmlLyrics-1.json", "AP_1", minutesAgo: 0, text: "only line");

        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), roots: [_root]);

        var first = await scanner.GetLatestLyricsAsync();
        var second = await scanner.GetLatestLyricsAsync();

        Assert.Equal(expected, first?.SourceFile);
        Assert.Equal(expected, second?.SourceFile);
    }

    [Fact]
    public async Task RepeatedQueriesWithinTtl_ReuseTheFileIndex()
    {
        _ = CreateLyricsFile("ttmlLyrics-1.json", "AP_1", minutesAgo: 0);
        var time = new ManualTimeProvider();
        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), roots: [_root], timeProvider: time)
        {
            FileIndexTtl = TimeSpan.FromMinutes(1),
        };

        _ = await scanner.GetLatestLyricsAsync();
        _ = await scanner.GetLatestLyricsAsync();

        Assert.Equal(1, scanner.IndexRefreshCount);
    }

    [Fact]
    public async Task ExpiredIndex_DiscoversNewAndDeletedFiles()
    {
        var oldPath = CreateLyricsFile("ttmlLyrics-old.json", "old", minutesAgo: 1, text: "old");
        var time = new ManualTimeProvider();
        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), roots: [_root], timeProvider: time)
        {
            FileIndexTtl = TimeSpan.FromSeconds(1),
        };

        Assert.Equal("old", (await scanner.GetLatestLyricsAsync())?.LyricsId);
        File.Delete(oldPath);
        _ = CreateLyricsFile("ttmlLyrics-new.json", "new", minutesAgo: 0, text: "new");
        time.Advance(TimeSpan.FromSeconds(2));

        var refreshed = await scanner.GetLatestLyricsAsync();

        Assert.Equal("new", refreshed?.LyricsId);
        Assert.Equal(2, scanner.IndexRefreshCount);
    }

    [Fact]
    public async Task FindCandidatesAsync_PrefersCachedSongThatMatchesPlayerDurationAndTitle()
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
        var player = new PlayerState(
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

    [Fact]
    public async Task FindCandidatesAsync_KeepsEveryFileWithinToleranceSoTiesStaySeparable()
    {
        // Two songs three seconds apart is an ordinary album; the scanner must surface both rather
        // than silently commit to one, because only the catalog id can tell them apart.
        _ = CreateLyricsFile("ttmlLyrics-a.json", "AP_1", minutesAgo: 0, bodyDuration: "3:00.000");
        _ = CreateLyricsFile("ttmlLyrics-b.json", "AP_2", minutesAgo: 5, bodyDuration: "3:03.000");

        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), roots: [_root]);
        var player = new PlayerState("Song", "Artist", "Album", 0, 181.0, true);

        var candidates = await scanner.FindCandidatesAsync(player);

        Assert.Equal(2, candidates.Count);
        Assert.Equal("AP_1", candidates[0].Document.LyricsId);
        Assert.Contains(candidates, candidate => candidate.Document.LyricsId == "AP_2");
    }

    [Fact]
    public async Task FindCandidatesAsync_DropsFilesFarOutsideTolerance()
    {
        _ = CreateLyricsFile("ttmlLyrics-far.json", "AP_far", minutesAgo: 0, bodyDuration: "6:00.000");

        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), roots: [_root]);
        var player = new PlayerState("Song", "Artist", "Album", 0, 180.0, true);

        var candidates = await scanner.FindCandidatesAsync(player);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task FindCandidatesAsync_StrongTitleDoesNotHideLargeDurationMismatch()
    {
        _ = CreateLyricsFile(
            "ttmlLyrics-wrong.json",
            "AP_wrong",
            minutesAgo: 0,
            text: "This is definitely Rolling in the Deep",
            bodyDuration: "2:20.000");

        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), roots: [_root]);
        var player = new PlayerState("Rolling in the Deep", "Artist", "Album", 0, 180.0, true);

        var candidates = await scanner.FindCandidatesAsync(player);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task FindCandidatesAsync_SingleWordTitleRemainsWeakEvidence()
    {
        _ = CreateLyricsFile(
            "ttmlLyrics-ocean.json",
            "AP_ocean",
            minutesAgo: 0,
            text: "I crossed the ocean once",
            bodyDuration: "3:00.000");

        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), roots: [_root]);
        var player = new PlayerState("Ocean", "Artist", "Album", 0, 180.0, true);

        var candidate = Assert.Single(await scanner.FindCandidatesAsync(player));
        Assert.False(candidate.HasContentMatch);
        Assert.Equal(TitleEvidenceStrength.Weak, candidate.TitleEvidence);
        Assert.Equal(0, candidate.DurationDelta);
    }

    [Fact]
    public async Task FindCandidatesAsync_ReturnsNothingWhileTheDurationIsUnknown()
    {
        _ = CreateLyricsFile("ttmlLyrics-a.json", "AP_1", minutesAgo: 0, bodyDuration: "3:00.000");

        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), roots: [_root]);
        var player = new PlayerState("Song", "Artist", "Album", 0, 0, true);

        var candidates = await scanner.FindCandidatesAsync(player);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task GetLatestLyricsAsync_ReturnsNullForMalformedJsonInsteadOfThrowing()
    {
        var path = Path.Combine(CreateDirectory("INetCache", "A"), "ttmlLyrics-bad.json");
        File.WriteAllText(path, "{ bad json");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);

        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), roots: [_root]);

        var document = await scanner.GetLatestLyricsAsync();

        Assert.Null(document);
    }

    [Fact]
    public async Task FindSyllableLyrics_DiscoversCurrentApiAndItsCatalogIdentity()
    {
        var path = CreateSyllableFile("syllable-lyrics[1].json", "AP_123", words: true, translation: true);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "account.json"), "{}");
        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), [_root]);

        Assert.Equal(path, Assert.Single(scanner.FindLyricsFiles([_root])));
        var document = await scanner.GetLatestLyricsAsync();
        Assert.Equal("AP_123", document?.LyricsId);
        Assert.NotEmpty(Assert.Single(document!.Lines).Words!);
        Assert.Equal("你好，世界", document.Lines[0].Translation);
        Assert.Equal("AP_123", scanner.LoadLyricsMetadata(path)?.LyricsId);
        Assert.Equal(["AP_123"], await scanner.GetCachedLyricsIdsAsync());
    }

    [Fact]
    public async Task FindCandidates_DeduplicatesLegacyAndSyllableVersionsOfTheSameTrack()
    {
        _ = CreateLyricsFile("ttmlLyrics-newer.json", "AP_123", 0, "hello world", "180.000");
        var rich = CreateSyllableFile("syllable-lyrics[1].json", "AP_123", words: true, translation: true);
        File.SetLastWriteTimeUtc(rich, DateTime.UtcNow.AddMinutes(-10));
        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), [_root]);

        var candidate = Assert.Single(await scanner.FindCandidatesAsync(
            new PlayerState("hello world", "Artist", "Album", 10, 180, true)));

        Assert.Equal(rich, candidate.Document.SourceFile);
        Assert.NotNull(candidate.Document.Lines[0].Words);
        Assert.Equal("你好，世界", candidate.Document.Lines[0].Translation);
    }

    [Fact]
    public async Task FindByLyricsIds_PrefersNativeFeaturesOverANewerBasicDocument()
    {
        _ = CreateLyricsFile("ttmlLyrics-newer.json", "AP_123", 0, "hello world", "180.000");
        var rich = CreateSyllableFile("syllable-lyrics[1].json", "AP_123", words: true, translation: true);
        File.SetLastWriteTimeUtc(rich, DateTime.UtcNow.AddHours(-1));
        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), [_root]);

        Assert.Equal(rich, (await scanner.FindByLyricsIdsAsync(["AP_123"]))?.SourceFile);
    }

    [Fact]
    public async Task FindByLyricsIds_PrefersBackgroundVocalsWhenLeadLyricsHaveTheSameFeatures()
    {
        _ = CreateSyllableFile("syllable-lyrics[1].json", "AP_123", words: true, translation: false);
        var rich = CreateSyllableFile("syllable-lyrics[2].json", "AP_123", words: true, translation: false,
            backgroundVocal: """<span role="x-bg"><span begin="11" end="12">(echo)</span></span>""");
        File.SetLastWriteTimeUtc(rich, DateTime.UtcNow.AddMinutes(-10));
        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), [_root]);

        var selected = await scanner.FindByLyricsIdsAsync(["AP_123"]);

        Assert.Equal(rich, selected?.SourceFile);
        Assert.Equal("echo", Assert.Single(Assert.Single(selected!.Lines).BackgroundVocals!).Text);
    }

    [Fact]
    public async Task SameNativeFeatures_PrefersFinerWordTimingBeforeFileRecency()
    {
        var coarse = CreateLyricsFile("ttmlLyrics-coarse.json", "AP_123", 0, "hello world", "180.000");
        File.WriteAllText(coarse, BuildLyricsJson("AP_123", "10.000", "12.000",
            "<span begin=\\\"10.000\\\" end=\\\"12.000\\\">hello world</span>", "180.000"));
        var fine = CreateSyllableFile("syllable-lyrics[1].json", "AP_123", words: true, translation: false);
        File.SetLastWriteTimeUtc(fine, DateTime.UtcNow.AddMinutes(-10));
        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), [_root]);

        var byId = await scanner.FindByLyricsIdsAsync(["AP_123"]);
        Assert.Equal(fine, byId?.SourceFile);
        Assert.Equal(2, byId!.Lines[0].Words!.Count);
        var candidate = Assert.Single(await scanner.FindCandidatesAsync(
            new PlayerState("hello world", "Artist", "Album", 10, 180, true)));
        Assert.Equal(fine, candidate.Document.SourceFile);
    }

    [Theory]
    [InlineData("180.000", 0, 160)]
    [InlineData("185.000", 5, 85)]
    public async Task SameCatalogVersion_KeepsIdentityEvidenceButUsesPreferredDocumentsOwnDuration(
        string nativeDuration, double expectedDelta, int expectedScore)
    {
        _ = CreateLyricsFile("ttmlLyrics-title.json", "AP_123", 0, "hello world", "180.000");
        var rich = CreateSyllableFile("syllable-lyrics[1].json", "AP_123", words: true, translation: false);
        var json = File.ReadAllText(rich).Replace("hello", "你好").Replace("world", "世界")
            .Replace("180.000", nativeDuration);
        File.WriteAllText(rich, json);
        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), [_root]);

        var candidate = Assert.Single(await scanner.FindCandidatesAsync(
            new PlayerState("hello world", "Artist", "Album", 10.5, 180, true)));

        Assert.Equal(rich, candidate.Document.SourceFile);
        Assert.Equal("你好 世界", candidate.Document.Lines[0].Text);
        Assert.True(candidate.HasContentMatch);
        Assert.Equal(TitleEvidenceStrength.Strong, candidate.TitleEvidence);
        Assert.Equal(expectedDelta, candidate.DurationDelta);
        Assert.Equal(expectedScore, candidate.Score);

        if (expectedDelta == 0)
        {
            var external = new CountingExternalProvider();
            await using var runtime = new LyricsRuntimeService(scanner, new NativePlayerProvider(),
                new LyricsSynchronizer(), new PlaybackClock())
            {
                PreferWordTimings = true,
                ExternalLyricsProviders = [external],
            };
            var snapshot = await runtime.SnapshotAsync();
            Assert.Equal(rich, snapshot.Document?.SourceFile);
            Assert.Equal(LyricsResolutionSource.AppleMusicCache, snapshot.Resolution.Source);
            Assert.Equal(0, external.CallCount);
        }
    }

    [Fact]
    public async Task DifferentCatalogIds_NeverShareTitleEvidence()
    {
        _ = CreateLyricsFile("ttmlLyrics-title.json", "AP_123", 0, "hello world", "180.000");
        var other = CreateSyllableFile("syllable-lyrics[1].json", "AP_456", words: true, translation: false);
        File.WriteAllText(other, File.ReadAllText(other).Replace("hello", "你好").Replace("world", "世界"));
        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), [_root]);

        var candidates = await scanner.FindCandidatesAsync(new PlayerState("hello world", "Artist", "Album", 1, 180, true));

        Assert.Equal(2, candidates.Count);
        var different = Assert.Single(candidates, candidate => candidate.Document.LyricsId == "AP_456");
        Assert.False(different.HasContentMatch);
        Assert.NotEqual(TitleEvidenceStrength.Strong, different.TitleEvidence);
    }

    [Fact]
    public async Task FindByLyricsIds_NeverLetsRichAnotherTrackOverrideTheRequestedIdOrder()
    {
        var first = CreateLyricsFile("ttmlLyrics.json", "AP_1", 0, "hello world", "180.000");
        _ = CreateSyllableFile("syllable-lyrics[1].json", "AP_2", words: true, translation: true);
        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), [_root]);

        Assert.Equal(first, (await scanner.FindByLyricsIdsAsync(["AP_1", "AP_2"]))?.SourceFile);
        var candidates = await scanner.FindCandidatesAsync(new PlayerState("hello world", "Artist", null, 1, 180, true));
        Assert.Equal(2, candidates.Count);
    }

    [Fact]
    public async Task RewrittenLocalizedCache_ReparsesWhenHttpTimestampIsUnchangedButSizeChanges()
    {
        var path = CreateSyllableFile("syllable-lyrics[1].json", "AP_123", words: false, translation: false);
        var time = File.GetLastWriteTimeUtc(path);
        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), [_root]);
        var first = await scanner.GetLatestLyricsAsync();
        Assert.Null(first!.Lines[0].Words);
        Assert.Null(first.Lines[0].Translation);

        File.WriteAllText(path, BuildSyllableJson("AP_123", words: true, translation: true));
        File.SetLastWriteTimeUtc(path, time);
        var upgraded = await scanner.GetLatestLyricsAsync();

        Assert.NotSame(first, upgraded);
        Assert.NotEmpty(upgraded!.Lines[0].Words!);
        Assert.Equal("你好，世界", upgraded.Lines[0].Translation);
    }

    [Fact]
    public async Task IndexRefresh_DiscoversLateSyllableEndpointWhileRetainingLegacyCache()
    {
        _ = CreateLyricsFile("ttmlLyrics.json", "AP_123", 0, "hello world", "180.000");
        var time = new ManualTimeProvider();
        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), [_root], time)
        {
            FileIndexTtl = TimeSpan.FromSeconds(1),
        };
        Assert.Null((await scanner.FindByLyricsIdsAsync(["AP_123"]))!.Lines[0].Words);
        var rich = CreateSyllableFile("syllable-lyrics[1].json", "AP_123", words: true, translation: true);
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(rich, (await scanner.FindByLyricsIdsAsync(["AP_123"]))?.SourceFile);
        Assert.Equal(2, scanner.IndexRefreshCount);
    }

    [Fact]
    public async Task NativeApiToRuntime_MatchesTheCatalogEditionWhenBothEditionsShareOneLyricAsset()
    {
        var correct = CreateSyllableFile("syllable-lyrics[1].json", "AP_123", words: true, translation: false,
            catalogId: "456");
        _ = CreateSyllableFile("syllable-lyrics[2].json", "AP_123", words: true, translation: true,
            catalogId: "789");
        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), [_root]);
        await using var runtime = new LyricsRuntimeService(scanner, new NativePlayerProvider("Track from this edition"),
            new LyricsSynchronizer(), catalogResolver: new FixedCatalogResolver("AP_456"));

        var snapshot = await runtime.SnapshotAsync();

        Assert.Equal(LyricsResolutionSource.CatalogVerifiedCache, snapshot.Resolution.Source);
        Assert.Equal(correct, snapshot.Document!.SourceFile);
        Assert.Equal("AP_123", snapshot.Document.LyricsId);
        Assert.Equal(456L, snapshot.Document.CatalogTrackId);
        Assert.Null(await scanner.FindByLyricsIdsAsync(["AP_123"]));
        Assert.Equal(new[] { "AP_456", "AP_789" }, (await scanner.GetCachedLyricsIdsAsync()).Order());
    }

    [Fact]
    public async Task NativeApiToRuntime_UsesAppleWordTimesAndTranslationsWithoutExternalRequests()
    {
        _ = CreateSyllableFile("syllable-lyrics[1].json", "AP_123", words: true, translation: true);
        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), [_root]);
        var external = new CountingExternalProvider();
        await using var runtime = new LyricsRuntimeService(scanner, new NativePlayerProvider(),
            new LyricsSynchronizer(), new PlaybackClock())
        {
            PreferWordTimings = true,
            PreferTranslations = true,
            ExternalLyricsProviders = [external],
        };

        var snapshot = await runtime.SnapshotAsync();

        Assert.Equal(LyricsResolutionSource.AppleMusicCache, snapshot.Resolution.Source);
        Assert.Equal("AP_123", snapshot.Document!.LyricsId);
        var line = Assert.Single(snapshot.Document.Lines);
        Assert.Equal(10, line.Begin);
        Assert.Equal(10, line.Words![0].Begin);
        Assert.True(LyricWordTiming.IsUsable(line));
        Assert.Equal("你好，世界", line.Translation);
        Assert.Equal(ExternalLyricsQueryState.Satisfied, snapshot.ExternalDiagnostics!.EnrichmentState);
        Assert.Equal(0, external.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeApiToRuntime_UsesSynchronizedLrcLibOnlyWhenAppleHasNoTimeline(bool appleHasLineTiming)
    {
        if (appleHasLineTiming)
        {
            _ = CreateSyllableFile("syllable-lyrics[1].json", "AP_123", words: false, translation: false);
        }
        else
        {
            const string untimedTtml = """
                <tt xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" itunes:timing="None">
                  <body dur="180.000"><div><p>hello world</p></div></body>
                </tt>
                """;
            File.WriteAllText(Path.Combine(_root, "ttmlLyrics[1].json"), JsonSerializer.Serialize(new
            {
                lyricsId = "AP_123",
                status = "success",
                ttml = untimedTtml,
            }));
        }

        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), [_root]);
        var lrc = new LrcLyricsParser().Parse("[00:10.250]hello world", "lrclib:123", "LRCLIB_123", 180);
        var external = new CountingExternalProvider(lrc);
        await using var runtime = new LyricsRuntimeService(scanner, new NativePlayerProvider(), new LyricsSynchronizer())
        {
            ExternalLyricsProviders = [external],
        };

        _ = await runtime.SnapshotAsync();
        var snapshot = await runtime.SnapshotAsync();

        Assert.Equal(appleHasLineTiming ? LyricsResolutionSource.AppleMusicCache : LyricsResolutionSource.ExternalProvider,
            snapshot.Resolution.Source);
        Assert.Equal(appleHasLineTiming ? 10.0 : 10.25, Assert.Single(snapshot.Document!.Lines).Begin);
        Assert.Equal(appleHasLineTiming ? 0 : 1, external.CallCount);
    }

    private sealed class NativePlayerProvider(string title = "hello world") : IPlayerSessionProvider
    {
        public Task<PlayerState?> GetCurrentPlayerStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<PlayerState?>(new PlayerState(title, "Artist", "Album", 10.5, 180, true));
    }

    private sealed class FixedCatalogResolver(string id) : ICatalogSongResolver
    {
        public Task<IReadOnlyList<string>> ResolveLyricsIdCandidatesAsync(PlayerState player,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([id]);
    }

    private sealed class CountingExternalProvider(LyricsDocument? lyrics = null) : IExternalLyricsProvider
    {
        public string Name => "LRCLIB";
        public int CallCount { get; private set; }
        public Task<LyricsDocument?> FetchAsync(PlayerState player, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(lyrics);
        }
    }

    private string CreateSyllableFile(string name, string id, bool words, bool translation,
        string? backgroundVocal = null, string? catalogId = null)
    {
        var path = Path.Combine(CreateDirectory("INetCache", "B"), name);
        File.WriteAllText(path, BuildSyllableJson(id, words, translation, backgroundVocal, catalogId));
        return path;
    }

    private static string BuildSyllableJson(string id, bool words, bool translation,
        string? backgroundVocal = null, string? catalogId = null)
    {
        catalogId ??= id[3..];
        var text = words
            ? "<span begin=\"10.000\" end=\"11.000\">hello</span> <span begin=\"11.000\" end=\"12.000\">world</span>"
            : "hello world";
        var localized = translation
            ? "<head><metadata><itunes:iTunesMetadata><itunes:translations><itunes:translation type=\"subtitle\" xml:lang=\"zh-Hans\"><itunes:text for=\"native-27\">你好，世界</itunes:text></itunes:translation></itunes:translations></itunes:iTunesMetadata></metadata></head>"
            : "";
        var ttml = "<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\">"
            + localized + "<body dur=\"180.000\"><div><p begin=\"10.000\" end=\"12.000\" itunes:key=\"native-27\">"
            + text + backgroundVocal + "</p></div></body></tt>";
        return JsonSerializer.Serialize(new
        {
            data = new[] { new { id = catalogId, type = "syllable-lyrics", attributes = new
            {
                playParams = new { id, catalogId, kind = "lyric" },
                ttmlLocalizations = ttml,
            } } },
        });
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

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow += amount;
    }
}
