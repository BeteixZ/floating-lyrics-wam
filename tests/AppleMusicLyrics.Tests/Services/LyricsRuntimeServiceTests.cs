using AppleMusicLyrics.App.Services;
using AppleMusicLyrics.Core.Abstractions;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Parsing;
using AppleMusicLyrics.Core.Sync;
using AppleMusicLyrics.Infrastructure.Windows.Cache;
using AppleMusicLyrics.Infrastructure.Windows.Catalog;
using Xunit;

namespace AppleMusicLyrics.Tests.Services;

public sealed class LyricsRuntimeServiceTests
{
    [Fact]
    public async Task SnapshotAsync_UsesPlaybackClockToAdvanceLyricSelection()
    {
        var document = new LyricsDocument(
            LyricsId: "runtime",
            Status: "ok",
            SourceFile: "sample.json",
            UpdatedAt: DateTimeOffset.UtcNow,
            Lines:
            [
                new LyricsLine(0.0, 0.05, "line one"),
                new LyricsLine(0.05, 0.50, "line two"),
            ],
            DurationSeconds: 180);

        var lyricsProvider = new StubLyricsProvider(document);
        var playerProvider = new StubPlayerProvider(new PlayerState("Song", "Artist", null, 0.0, 180, true));
        var runtime = new LyricsRuntimeService(
            lyricsProvider,
            playerProvider,
            new LyricsSynchronizer(),
            new PlaybackClock());

        _ = await runtime.SnapshotAsync();
        await Task.Delay(120);
        var snapshot = await runtime.SnapshotAsync();

        Assert.NotNull(snapshot.EstimatedPositionSeconds);
        Assert.NotNull(snapshot.RawPositionSeconds);
        Assert.True(snapshot.EstimatedPositionSeconds > snapshot.RawPositionSeconds);
        Assert.Equal("line two", snapshot.ActiveLyric.CurrentLine?.Text);
    }

    [Fact]
    public async Task GetFrameSnapshot_AdvancesLyricsBetweenPollsWithoutCallingProviders()
    {
        var time = new ManualTimeProvider();
        var document = new LyricsDocument(
            LyricsId: "frame-runtime",
            Status: "ok",
            SourceFile: "frame.json",
            UpdatedAt: DateTimeOffset.UtcNow,
            Lines:
            [
                new LyricsLine(0.0, 0.75, "line one"),
                new LyricsLine(0.75, 2.0, "line two"),
            ],
            DurationSeconds: 180);
        var lyricsProvider = new StubLyricsProvider(document);
        var playerProvider = new StubPlayerProvider(
            new PlayerState("Song", "Artist", null, 0.0, 180, true));
        var runtime = new LyricsRuntimeService(
            lyricsProvider,
            playerProvider,
            new LyricsSynchronizer(),
            new PlaybackClock(time),
            timeProvider: time);

        var polled = await runtime.SnapshotAsync();
        Assert.Equal("line one", polled.ActiveLyric.CurrentLine?.Text);

        time.Advance(TimeSpan.FromMilliseconds(400));
        var frame = runtime.GetFrameSnapshot();

        Assert.Equal("line two", frame.ActiveLyric.CurrentLine?.Text);
        Assert.Equal(1, playerProvider.CallCount);
        Assert.Equal(1, lyricsProvider.CallCount);
    }

    [Fact]
    public async Task GetFrameSnapshot_DoesNotAdvancePausedPlayback()
    {
        var time = new ManualTimeProvider();
        var document = BuildDocument("paused", 180, "paused line", begin: 0.0, end: 5.0);
        var runtime = new LyricsRuntimeService(
            new StubLyricsProvider(document),
            new StubPlayerProvider(new PlayerState("Song", "Artist", null, 1.0, 180, false)),
            new LyricsSynchronizer(),
            new PlaybackClock(time),
            timeProvider: time);

        var polled = await runtime.SnapshotAsync();
        time.Advance(TimeSpan.FromSeconds(3));
        var frame = runtime.GetFrameSnapshot();

        Assert.Equal(polled.EstimatedPositionSeconds, frame.EstimatedPositionSeconds);
        Assert.Equal(polled.ActiveLyric.CurrentIndex, frame.ActiveLyric.CurrentIndex);
    }

    [Fact]
    public async Task GetFrameSnapshot_ClampsEstimatedPositionToDuration()
    {
        var time = new ManualTimeProvider();
        var runtime = new LyricsRuntimeService(
            new StubLyricsProvider(BuildDocument("short", 1.0, "short line", end: 1.0)),
            new StubPlayerProvider(new PlayerState("Short Song", "Artist", null, 0.0, 1.0, true)),
            new LyricsSynchronizer(),
            new PlaybackClock(time),
            timeProvider: time);

        _ = await runtime.SnapshotAsync();
        time.Advance(TimeSpan.FromSeconds(2));
        var frame = runtime.GetFrameSnapshot();

        Assert.Equal(1.0, frame.EstimatedPositionSeconds);
        Assert.Equal(1.0, frame.Player?.Position);
    }

    [Fact]
    public async Task SnapshotAsync_ClearsStaleDocumentAfterSessionChange()
    {
        var staleDocument = new LyricsDocument(
            LyricsId: "stale",
            Status: "ok",
            SourceFile: "stale.json",
            UpdatedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            Lines:
            [
                new LyricsLine(0.0, 5.0, "stale line"),
            ]);

        var lyricsProvider = new StubLyricsProvider(staleDocument);
        var playerProvider = new SequencePlayerProvider(
        [
            new PlayerState("Song A", "Artist", null, 0.0, 180, true),
            new PlayerState("Song B", "Artist", null, 0.0, 180, true),
        ]);

        var runtime = new LyricsRuntimeService(
            lyricsProvider,
            playerProvider,
            new LyricsSynchronizer(),
            new PlaybackClock());

        _ = await runtime.SnapshotAsync();
        var second = await runtime.SnapshotAsync();

        Assert.Null(second.Document);
        Assert.Null(second.ActiveLyric.CurrentLine);
    }

    [Fact]
    public async Task SnapshotAsync_UsesPlayerMatchedProviderForPreviouslyCachedSong()
    {
        var matchedDocument = BuildDocument("rap-god", 363.521, "I'm beginning to feel like a Rap God, Rap God", begin: 25.0, end: 29.0);

        var lyricsProvider = new CandidateLyricsProvider([new LyricsMatch(matchedDocument, 100, 0.0)]);
        var playerProvider = new StubPlayerProvider(
            new PlayerState("Rap God", "Eminem", "The Marshall Mathers LP2", 26.0, 363.521, true));
        var runtime = new LyricsRuntimeService(
            lyricsProvider,
            playerProvider,
            new LyricsSynchronizer(),
            new PlaybackClock());

        var snapshot = await runtime.SnapshotAsync();

        Assert.NotNull(snapshot.Document);
        Assert.Equal("rap-god", snapshot.Document!.LyricsId);
        Assert.Equal("I'm beginning to feel like a Rap God, Rap God", snapshot.ActiveLyric.CurrentLine?.Text);
    }

    [Fact]
    public async Task SnapshotAsync_KeepsTheChosenDocumentWhenAPrefetchLandsMidSong()
    {
        // Apple prefetches the *next* track's lyrics while the current one plays. That file is
        // newer, and on an album its length is routinely within a few seconds of the current song,
        // so recency plus a duration check used to swap the lyrics out mid-playback.
        var playing = BuildDocument("AP_playing", 200.0, "the song you are hearing");
        var prefetched = BuildDocument("AP_prefetched", 202.0, "the next song on the album");

        var lyricsProvider = new CandidateLyricsProvider([new LyricsMatch(playing, 100, 0.0)]);
        var playerProvider = new StubPlayerProvider(new PlayerState("Song", "Artist", "Album", 5.0, 200.0, true));
        var runtime = new LyricsRuntimeService(
            lyricsProvider,
            playerProvider,
            new LyricsSynchronizer(),
            new PlaybackClock())
        {
            MatchWindow = TimeSpan.FromMilliseconds(40),
        };

        var first = await runtime.SnapshotAsync();
        Assert.Equal("AP_playing", first.Document!.LyricsId);

        // The prefetched file appears and would outscore the current one on recency.
        lyricsProvider.Candidates =
        [
            new LyricsMatch(prefetched, 100, 0.0),
            new LyricsMatch(playing, 100, 0.0),
        ];
        await Task.Delay(80);

        var second = await runtime.SnapshotAsync();

        Assert.Equal("AP_playing", second.Document!.LyricsId);
    }

    [Fact]
    public async Task SnapshotAsync_AsksTheCatalogWhenDurationCannotSeparateTwoCachedSongs()
    {
        var wrong = BuildDocument("AP_111", 181.0, "wrong song");
        var right = BuildDocument("AP_222", 182.0, "right song");

        var lyricsProvider = new CandidateLyricsProvider(
        [
            new LyricsMatch(wrong, 80, 1.0),
            new LyricsMatch(right, 80, 2.0),
        ]);
        var playerProvider = new StubPlayerProvider(new PlayerState("Song", "Artist", "Album", 1.0, 180.0, true));
        var resolver = new StubCatalogResolver(["AP_222"]);

        var runtime = new LyricsRuntimeService(
            lyricsProvider,
            playerProvider,
            new LyricsSynchronizer(),
            new PlaybackClock(),
            catalogResolver: resolver);

        var snapshot = await runtime.SnapshotAsync();

        Assert.Equal("AP_222", snapshot.Document!.LyricsId);
        Assert.Equal(1, resolver.CallCount);
    }

    [Fact]
    public async Task SnapshotAsync_ShowsNothingWhenTheCatalogProvesTheRightFileIsNotCached()
    {
        // The real report: Madonna's "S.E.X." (AP_1862326547, 251.49s) has no cached lyrics, and
        // Magdalena Bay's "Watching T.V." (AP_1751414766, 245.41s) sits ~6s away — close enough to
        // land inside the tolerance. Once the catalog names this song's ids, a cached file carrying
        // a *different* AP_ id is proven to be another song, however near its length.
        var wrongSong = BuildDocument("AP_1751414766", 245.413, "watching t v");

        var lyricsProvider = new CandidateLyricsProvider([new LyricsMatch(wrongSong, 25, 6.0)]);
        var playerProvider = new StubPlayerProvider(new PlayerState("S.E.X.", "Madonna", "Veronica Electronica", 3.0, 251.49, true));
        var resolver = new StubCatalogResolver(["AP_1862326547"]);

        var runtime = new LyricsRuntimeService(
            lyricsProvider,
            playerProvider,
            new LyricsSynchronizer(),
            new PlaybackClock(),
            catalogResolver: resolver);

        var snapshot = await runtime.SnapshotAsync();

        Assert.Null(snapshot.Document);
        Assert.Null(snapshot.ActiveLyric.CurrentLine);
    }

    [Fact]
    public async Task SnapshotAsync_HoldsBackUnverifiableGeoCandidateByDefault()
    {
        var comparableButWrong = BuildDocument("AP_1751414766", 245.413, "another song");
        var notComparable = BuildDocument("GEO_10624307-ttml", 243.426, "possibly this song");

        var runtime = new LyricsRuntimeService(
            new CandidateLyricsProvider(
            [
                new LyricsMatch(comparableButWrong, 40, 2.0),
                new LyricsMatch(notComparable, 25, 4.0),
            ]),
            new StubPlayerProvider(new PlayerState("Song", "Artist", "Album", 3.0, 247.4, true)),
            new LyricsSynchronizer(),
            new PlaybackClock(),
            catalogResolver: new StubCatalogResolver(["AP_1862326547"]));

        var snapshot = await runtime.SnapshotAsync();

        Assert.Null(snapshot.Document);
        Assert.Equal(LyricsResolutionStatus.Unavailable, snapshot.Resolution.Status);
        Assert.Equal(LyricsResolutionConfidence.Low, snapshot.Resolution.Confidence);
        Assert.Contains("Held back", snapshot.Resolution.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SnapshotAsync_CanShowUnverifiableGeoCandidateWhenOptedIn()
    {
        var comparableButWrong = BuildDocument("AP_1751414766", 245.413, "another song");
        var notComparable = BuildDocument("GEO_10624307-ttml", 243.426, "possibly this song");

        var runtime = new LyricsRuntimeService(
            new CandidateLyricsProvider(
            [
                new LyricsMatch(comparableButWrong, 40, 2.0),
                new LyricsMatch(notComparable, 25, 4.0),
            ]),
            new StubPlayerProvider(new PlayerState("Song", "Artist", "Album", 3.0, 247.4, true)),
            new LyricsSynchronizer(),
            new PlaybackClock(),
            catalogResolver: new StubCatalogResolver(["AP_1862326547"]))
        {
            AllowLowConfidenceLyrics = true,
        };

        var snapshot = await runtime.SnapshotAsync();

        Assert.Equal("GEO_10624307-ttml", snapshot.Document!.LyricsId);
        Assert.Equal(LyricsResolutionConfidence.Low, snapshot.Resolution.Confidence);
    }

    [Fact]
    public async Task SnapshotAsync_HoldsBackDurationFallbackWhenCatalogLookupFails()
    {
        var closest = BuildDocument("AP_111", 181.0, "closest by duration");
        var ambiguous = BuildDocument("AP_222", 181.2, "ambiguous candidate");
        var runtime = new LyricsRuntimeService(
            new CandidateLyricsProvider(
            [
                new LyricsMatch(closest, 80, 1.0),
                new LyricsMatch(ambiguous, 70, 1.2),
            ]),
            new StubPlayerProvider(new PlayerState("Song", "Artist", "Album", 1.0, 180.0, true)),
            new LyricsSynchronizer(),
            new PlaybackClock(),
            catalogResolver: new StubCatalogResolver([]));

        var snapshot = await runtime.SnapshotAsync();

        Assert.Null(snapshot.Document);
        Assert.Equal(LyricsResolutionStatus.Unavailable, snapshot.Resolution.Status);
        Assert.Equal(LyricsResolutionConfidence.Low, snapshot.Resolution.Confidence);
    }

    [Fact]
    public async Task SnapshotAsync_CanUseDurationFallbackWhenOptedIn()
    {
        var closest = BuildDocument("AP_111", 181.0, "closest by duration");
        var ambiguous = BuildDocument("AP_222", 181.2, "ambiguous candidate");
        var runtime = new LyricsRuntimeService(
            new CandidateLyricsProvider(
            [
                new LyricsMatch(closest, 80, 1.0),
                new LyricsMatch(ambiguous, 70, 1.2),
            ]),
            new StubPlayerProvider(new PlayerState("Song", "Artist", "Album", 1.0, 180.0, true)),
            new LyricsSynchronizer(),
            new PlaybackClock(),
            catalogResolver: new StubCatalogResolver([]))
        {
            AllowLowConfidenceLyrics = true,
        };

        var snapshot = await runtime.SnapshotAsync();

        Assert.Equal("AP_111", snapshot.Document!.LyricsId);
        Assert.Equal(LyricsResolutionConfidence.Low, snapshot.Resolution.Confidence);
    }

    [Fact]
    public async Task SnapshotAsync_AcceptsSingleCandidateWithinMediumWindowWhenCatalogLookupFails()
    {
        var singleMedium = BuildDocument("AP_111", 182.0, "single candidate with medium delta");
        var runtime = new LyricsRuntimeService(
            new CandidateLyricsProvider(
            [
                new LyricsMatch(singleMedium, 60, 2.0, HasContentMatch: true),
            ]),
            new StubPlayerProvider(new PlayerState("Song", "Artist", "Album", 1.0, 180.0, true)),
            new LyricsSynchronizer(),
            new PlaybackClock(),
            catalogResolver: new StubCatalogResolver([]));

        var snapshot = await runtime.SnapshotAsync();

        Assert.NotNull(snapshot.Document);
        Assert.Equal("AP_111", snapshot.Document.LyricsId);
        Assert.Equal(LyricsResolutionStatus.Resolved, snapshot.Resolution.Status);
        Assert.Equal(LyricsResolutionConfidence.Medium, snapshot.Resolution.Confidence);
        Assert.Equal(LyricsResolutionSource.AppleMusicCache, snapshot.Resolution.Source);
        Assert.Contains("medium duration window", snapshot.Resolution.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SnapshotAsync_AcceptsClearWinnerCandidateWhenCatalogLookupFails()
    {
        var best = BuildDocument("AP_111", 181.0, "clear winner");
        var farBehind = BuildDocument("AP_222", 185.0, "far behind");
        var runtime = new LyricsRuntimeService(
            new CandidateLyricsProvider(
            [
                new LyricsMatch(best, 80, 1.0),
                new LyricsMatch(farBehind, 25, 5.0),
            ]),
            new StubPlayerProvider(new PlayerState("Song", "Artist", "Album", 1.0, 180.0, true)),
            new LyricsSynchronizer(),
            new PlaybackClock(),
            catalogResolver: new StubCatalogResolver([]));

        var snapshot = await runtime.SnapshotAsync();

        Assert.NotNull(snapshot.Document);
        Assert.Equal("AP_111", snapshot.Document.LyricsId);
        Assert.Equal(LyricsResolutionStatus.Resolved, snapshot.Resolution.Status);
        Assert.Equal(LyricsResolutionConfidence.Medium, snapshot.Resolution.Confidence);
        Assert.Equal(LyricsResolutionSource.AppleMusicCache, snapshot.Resolution.Source);
        Assert.Contains("significant score lead", snapshot.Resolution.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SnapshotAsync_HoldsBackSingleCandidateOutsideMediumWindow()
    {
        var singleOutside = BuildDocument("AP_111", 184.5, "single candidate outside medium window");
        var runtime = new LyricsRuntimeService(
            new CandidateLyricsProvider(
            [
                new LyricsMatch(singleOutside, 40, 4.5),
            ]),
            new StubPlayerProvider(new PlayerState("Song", "Artist", "Album", 1.0, 180.0, true)),
            new LyricsSynchronizer(),
            new PlaybackClock(),
            catalogResolver: new StubCatalogResolver([]));

        var snapshot = await runtime.SnapshotAsync();

        Assert.Null(snapshot.Document);
        Assert.Equal(LyricsResolutionStatus.Unavailable, snapshot.Resolution.Status);
        Assert.Equal(LyricsResolutionConfidence.Low, snapshot.Resolution.Confidence);
    }

    [Fact]
    public async Task SnapshotAsync_HoldsBackSingleCandidateWithinMediumWindowWithoutContentMatch()
    {
        var singleWithoutContent = BuildDocument("AP_111", 182.0, "single candidate without content match");
        var runtime = new LyricsRuntimeService(
            new CandidateLyricsProvider(
            [
                new LyricsMatch(singleWithoutContent, 60, 2.0, HasContentMatch: false),
            ]),
            new StubPlayerProvider(new PlayerState("Song", "Artist", "Album", 1.0, 180.0, true)),
            new LyricsSynchronizer(),
            new PlaybackClock(),
            catalogResolver: new StubCatalogResolver([]));

        var snapshot = await runtime.SnapshotAsync();

        Assert.Null(snapshot.Document);
        Assert.Equal(LyricsResolutionStatus.Unavailable, snapshot.Resolution.Status);
        Assert.Equal(LyricsResolutionConfidence.Low, snapshot.Resolution.Confidence);
    }

    [Fact]
    public async Task SnapshotAsync_PrefersExternalProviderOverUnverifiedDurationMatch()
    {
        var unverifiedLocal = BuildDocument("AP_WRONG", 181.9, "wrong song with similar duration");
        var verifiedExternal = BuildDocument("LRCLIB_RIGHT", 180.0, "correct lyrics from external provider");
        var externalProvider = new StubExternalProvider(verifiedExternal);

        var runtime = new LyricsRuntimeService(
            new CandidateLyricsProvider(
            [
                new LyricsMatch(unverifiedLocal, 60, 1.9, HasContentMatch: false),
            ]),
            new StubPlayerProvider(new PlayerState("Song", "Artist", "Album", 1.0, 180.0, true)),
            new LyricsSynchronizer(),
            new PlaybackClock(),
            catalogResolver: new StubCatalogResolver([]))
        {
            ExternalLyricsProviders = [externalProvider],
        };

        var first = await runtime.SnapshotAsync();
        await Task.Delay(50);
        var second = await runtime.SnapshotAsync();

        Assert.NotNull(second.Document);
        Assert.Equal("LRCLIB_RIGHT", second.Document.LyricsId);
        Assert.Equal(LyricsResolutionConfidence.Medium, second.Resolution.Confidence);
        Assert.Equal(LyricsResolutionSource.ExternalProvider, second.Resolution.Source);
    }

    [Fact]
    public async Task SnapshotAsync_CorrectlyResolvesCupcakkeGoGetEm()
    {
        var alienPussy = BuildDocument("MX_42528812-45061175", 147.92, "Ba-da-da-da, alien pussy");
        var goGetEm = BuildDocument("MX_42529556-45061221", 140.21, "I don't give a fuck, bitch, you better go, go get 'em");

        // AppleMusicCacheScanner ranks HasContentMatch=true first with score 120 vs 100
        var matches = new[]
        {
            new LyricsMatch(goGetEm, Score: 120, DurationDelta: 3.00, HasContentMatch: true),
            new LyricsMatch(alienPussy, Score: 100, DurationDelta: 0.08, HasContentMatch: false),
        };

        var lyricsProvider = new CandidateLyricsProvider(matches);
        var player = new PlayerState(
            Title: "Go Get 'em",
            Artist: "cupcakKe",
            Album: "The BakKery",
            Position: 20.0,
            Duration: 148.0,
            Playing: true,
            SourceAppId: "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App"
        );

        var runtime = new LyricsRuntimeService(
            lyricsProvider,
            new StubPlayerProvider(player),
            new LyricsSynchronizer(),
            new PlaybackClock());

        var snap = await runtime.SnapshotAsync();
        Assert.NotNull(snap.Document);
        Assert.Equal("MX_42529556-45061221", snap.Document.LyricsId);
        Assert.Equal(LyricsResolutionStatus.Resolved, snap.Resolution.Status);
        Assert.Equal(LyricsResolutionConfidence.Medium, snap.Resolution.Confidence);
        Assert.Contains(snap.Document.Lines, l => l.Text.Contains("go get 'em", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SnapshotAsync_DoesNotAskTheCatalogForAnUnambiguousMatch()
    {
        var only = BuildDocument("AP_111", 180.2, "the only candidate");

        var lyricsProvider = new CandidateLyricsProvider([new LyricsMatch(only, 100, 0.2)]);
        var playerProvider = new StubPlayerProvider(new PlayerState("Song", "Artist", "Album", 1.0, 180.0, true));
        var resolver = new StubCatalogResolver(["AP_999"]);

        var runtime = new LyricsRuntimeService(
            lyricsProvider,
            playerProvider,
            new LyricsSynchronizer(),
            new PlaybackClock(),
            catalogResolver: resolver);

        var snapshot = await runtime.SnapshotAsync();

        Assert.Equal("AP_111", snapshot.Document!.LyricsId);
        Assert.Equal(0, resolver.CallCount);
    }

    [Fact]
    public async Task SnapshotAsync_DoesNotMatchWhileTheReportedDurationIsStillZero()
    {
        // SMTC publishes the new title before the new timeline, and matching against a duration of
        // zero accepts any file at all.
        var document = BuildDocument("AP_111", 180.0, "some line");

        var lyricsProvider = new CandidateLyricsProvider([new LyricsMatch(document, 100, 0.0)]);
        var playerProvider = new SequencePlayerProvider(
        [
            new PlayerState("Song", "Artist", "Album", 0.0, 0.0, true),
            new PlayerState("Song", "Artist", "Album", 0.5, 180.0, true),
        ]);

        var runtime = new LyricsRuntimeService(
            lyricsProvider,
            playerProvider,
            new LyricsSynchronizer(),
            new PlaybackClock());

        var duringTransition = await runtime.SnapshotAsync();
        Assert.Null(duringTransition.Document);

        var afterTimelineArrives = await runtime.SnapshotAsync();
        Assert.Equal("AP_111", afterTimelineArrives.Document!.LyricsId);
    }

    [Fact]
    public async Task SnapshotAsync_FallsBackToAnExternalProviderWhenTheLocalCacheHasNothing()
    {
        var external = BuildDocument("LRCLIB_42", 251.49, "from lrclib");
        var provider = new StubExternalProvider(external);

        var runtime = new LyricsRuntimeService(
            new CandidateLyricsProvider([]),
            new StubPlayerProvider(new PlayerState("S.E.X.", "Madonna", "Veronica Electronica", 1.0, 251.49, true)),
            new LyricsSynchronizer(),
            new PlaybackClock())
        {
            ExternalLyricsProviders = [provider],
        };

        // The fetch runs off the poll, so the first snapshot only kicks it off.
        var first = await runtime.SnapshotAsync();
        Assert.Null(first.Document);

        await provider.Completed;
        var second = await runtime.SnapshotAsync();

        Assert.Equal("LRCLIB_42", second.Document!.LyricsId);
    }

    [Fact]
    public async Task SnapshotAsync_UsesExternalProviderInsteadOfLowConfidenceLocalCandidate()
    {
        var uncertainLocal = BuildDocument("AP_uncertain", 184.0, "possibly wrong");
        var external = BuildDocument("LRCLIB_verified", 180.0, "external line");
        var provider = new StubExternalProvider(external);
        var runtime = new LyricsRuntimeService(
            new CandidateLyricsProvider([new LyricsMatch(uncertainLocal, 40, 4.0)]),
            new StubPlayerProvider(new PlayerState("Song", "Artist", "Album", 1.0, 180.0, true)),
            new LyricsSynchronizer(),
            new PlaybackClock(),
            catalogResolver: new StubCatalogResolver([]))
        {
            ExternalLyricsProviders = [provider],
        };

        var pending = await runtime.SnapshotAsync();
        Assert.Null(pending.Document);
        Assert.Equal(LyricsResolutionStatus.FetchingExternal, pending.Resolution.Status);

        await provider.Completed;
        var resolved = await runtime.SnapshotAsync();

        Assert.Equal("LRCLIB_verified", resolved.Document!.LyricsId);
        Assert.Equal(LyricsResolutionSource.ExternalProvider, resolved.Resolution.Source);
    }

    [Fact]
    public async Task SnapshotAsync_DoesNotConsultExternalProvidersWhenTheLocalCacheAnswered()
    {
        var local = BuildDocument("AP_1", 180.2, "from apple");
        var provider = new StubExternalProvider(BuildDocument("LRCLIB_9", 180.0, "from lrclib"));

        var runtime = new LyricsRuntimeService(
            new CandidateLyricsProvider([new LyricsMatch(local, 100, 0.2)]),
            new StubPlayerProvider(new PlayerState("Song", "Artist", "Album", 1.0, 180.0, true)),
            new LyricsSynchronizer(),
            new PlaybackClock())
        {
            ExternalLyricsProviders = [provider],
        };

        var snapshot = await runtime.SnapshotAsync();

        Assert.Equal("AP_1", snapshot.Document!.LyricsId);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task SnapshotAsync_WaitsForTheDurationBeforeAskingAnExternalProvider()
    {
        // These services identify a track partly by length, so asking during the SMTC transition
        // would send a duration of zero.
        var provider = new StubExternalProvider(BuildDocument("LRCLIB_1", 180.0, "line"));

        var runtime = new LyricsRuntimeService(
            new CandidateLyricsProvider([]),
            new SequencePlayerProvider(
            [
                new PlayerState("Song", "Artist", "Album", 0.0, 0.0, true),
                new PlayerState("Song", "Artist", "Album", 0.5, 180.0, true),
            ]),
            new LyricsSynchronizer(),
            new PlaybackClock())
        {
            ExternalLyricsProviders = [provider],
        };

        _ = await runtime.SnapshotAsync();
        Assert.Equal(0, provider.CallCount);

        _ = await runtime.SnapshotAsync();
        await provider.Completed;
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task SnapshotAsync_DiscardsAnExternalResultThatArrivesAfterTheTrackChanged()
    {
        var provider = new StubExternalProvider(BuildDocument("LRCLIB_OLD", 180.0, "previous song"));

        var runtime = new LyricsRuntimeService(
            new CandidateLyricsProvider([]),
            new SequencePlayerProvider(
            [
                new PlayerState("Song A", "Artist", "Album", 0.0, 180.0, true),
                new PlayerState("Song B", "Artist", "Album", 0.0, 180.0, true),
            ]),
            new LyricsSynchronizer(),
            new PlaybackClock())
        {
            ExternalLyricsProviders = [provider],
        };

        _ = await runtime.SnapshotAsync();
        await provider.Completed;

        // Song B is playing now; Song A's lyrics must not land on it.
        var afterChange = await runtime.SnapshotAsync();

        Assert.Null(afterChange.Document);
    }

    [Fact]
    public async Task SnapshotAsync_ReportsWaitingForMetadataBeforeDurationArrives()
    {
        var runtime = new LyricsRuntimeService(
            new CandidateLyricsProvider([]),
            new StubPlayerProvider(new PlayerState("Song", "Artist", "Album", 0.0, 0.0, true)),
            new LyricsSynchronizer(),
            new PlaybackClock());

        var snapshot = await runtime.SnapshotAsync();

        Assert.Equal(LyricsResolutionStatus.WaitingForMetadata, snapshot.Resolution.Status);
        Assert.Equal(LyricsResolutionConfidence.None, snapshot.Resolution.Confidence);
        Assert.Contains("duration", snapshot.Resolution.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SnapshotAsync_ReportsHighConfidenceLocalEvidence()
    {
        var local = BuildDocument("AP_111", 180.2, "local line");
        var runtime = new LyricsRuntimeService(
            new CandidateLyricsProvider([new LyricsMatch(local, 100, 0.2)]),
            new StubPlayerProvider(new PlayerState("Song", "Artist", "Album", 1.0, 180.0, true)),
            new LyricsSynchronizer(),
            new PlaybackClock());

        var snapshot = await runtime.SnapshotAsync();

        Assert.Equal(LyricsResolutionStatus.Resolved, snapshot.Resolution.Status);
        Assert.Equal(LyricsResolutionConfidence.High, snapshot.Resolution.Confidence);
        Assert.Equal(LyricsResolutionSource.AppleMusicCache, snapshot.Resolution.Source);
        Assert.Equal(1, snapshot.Resolution.CandidateCount);
        var evidence = Assert.Single(snapshot.Resolution.CandidateEvidence);
        Assert.Equal("AP_111", evidence.LyricsId);
        Assert.Equal(0.2, evidence.DurationDelta, precision: 3);
        Assert.Equal("Selected.", evidence.Decision);
    }

    [Fact]
    public async Task SnapshotAsync_ReportsCatalogVerifiedSelectionAndRejectedCandidate()
    {
        var wrong = BuildDocument("AP_111", 181.0, "wrong");
        var right = BuildDocument("AP_222", 182.0, "right");
        var runtime = new LyricsRuntimeService(
            new CandidateLyricsProvider(
            [
                new LyricsMatch(wrong, 80, 1.0),
                new LyricsMatch(right, 80, 2.0),
            ]),
            new StubPlayerProvider(new PlayerState("Song", "Artist", "Album", 1.0, 180.0, true)),
            new LyricsSynchronizer(),
            new PlaybackClock(),
            catalogResolver: new StubCatalogResolver(["AP_222"]));

        var snapshot = await runtime.SnapshotAsync();

        Assert.Equal(LyricsResolutionSource.CatalogVerifiedCache, snapshot.Resolution.Source);
        Assert.Equal(LyricsResolutionConfidence.High, snapshot.Resolution.Confidence);
        Assert.Equal(2, snapshot.Resolution.CandidateCount);
        Assert.Contains(snapshot.Resolution.CandidateEvidence,
            item => item.LyricsId == "AP_111" && item.Decision.Contains("different song", StringComparison.Ordinal));
        Assert.Contains(snapshot.Resolution.CandidateEvidence,
            item => item.LyricsId == "AP_222" && item.Decision == "Selected.");
    }

    [Fact]
    public async Task SnapshotAsync_ReportsExternalFetchProgressAndResult()
    {
        var provider = new StubExternalProvider(BuildDocument("LRCLIB_1", 180.0, "external line"));
        var runtime = new LyricsRuntimeService(
            new CandidateLyricsProvider([]),
            new StubPlayerProvider(new PlayerState("Song", "Artist", "Album", 1.0, 180.0, true)),
            new LyricsSynchronizer(),
            new PlaybackClock())
        {
            ExternalLyricsProviders = [provider],
        };

        var pending = await runtime.SnapshotAsync();
        Assert.Equal(LyricsResolutionStatus.FetchingExternal, pending.Resolution.Status);

        await provider.Completed;
        var resolved = await runtime.SnapshotAsync();

        Assert.Equal(LyricsResolutionStatus.Resolved, resolved.Resolution.Status);
        Assert.Equal(LyricsResolutionConfidence.Medium, resolved.Resolution.Confidence);
        Assert.Equal(LyricsResolutionSource.ExternalProvider, resolved.Resolution.Source);
    }

    [Fact]
    public async Task SnapshotAsync_ChangesTrackIdentityWhenMetadataChanges()
    {
        var document = BuildDocument("AP_111", 180.0, "line");
        var runtime = new LyricsRuntimeService(
            new CandidateLyricsProvider([new LyricsMatch(document, 100, 0.0)]),
            new SequencePlayerProvider(
            [
                new PlayerState("Song A", "Artist", "Album", 1.0, 180.0, true),
                new PlayerState("Song B", "Artist", "Album", 1.0, 180.0, true),
            ]),
            new LyricsSynchronizer(),
            new PlaybackClock());

        var first = await runtime.SnapshotAsync();
        var second = await runtime.SnapshotAsync();

        Assert.NotEqual(first.Resolution.TrackIdentity, second.Resolution.TrackIdentity);
        Assert.Contains("Song B", second.Resolution.TrackIdentity, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SnapshotAsync_RetriesTransientCatalogFailureWithinTheSameTrack()
    {
        var wrong = BuildDocument("AP_111", 181.0, "wrong");
        var right = BuildDocument("AP_222", 182.0, "right");
        var resolver = new FlakyCatalogResolver(["AP_222"]);
        var runtime = new LyricsRuntimeService(
            new CandidateLyricsProvider(
            [
                new LyricsMatch(wrong, 80, 1.0),
                new LyricsMatch(right, 80, 2.0),
            ]),
            new StubPlayerProvider(new PlayerState("Song", "Artist", "Album", 1.0, 180.0, true)),
            new LyricsSynchronizer(),
            new PlaybackClock(),
            catalogResolver: resolver)
        {
            CatalogRetryBaseDelay = TimeSpan.Zero,
        };

        var first = await runtime.SnapshotAsync();
        var second = await runtime.SnapshotAsync();

        Assert.Null(first.Document);
        Assert.Equal("AP_222", second.Document!.LyricsId);
        Assert.Equal(2, resolver.CallCount);
    }

    [Fact]
    public async Task SnapshotAsync_DoesNotRetryDefinitiveEmptyCatalogResult()
    {
        var candidate = BuildDocument("AP_111", 184.0, "uncertain");
        var resolver = new StubCatalogResolver([]);
        var runtime = new LyricsRuntimeService(
            new CandidateLyricsProvider([new LyricsMatch(candidate, 40, 4.0)]),
            new StubPlayerProvider(new PlayerState("Song", "Artist", "Album", 1.0, 180.0, true)),
            new LyricsSynchronizer(),
            new PlaybackClock(),
            catalogResolver: resolver)
        {
            CatalogRetryBaseDelay = TimeSpan.Zero,
        };

        _ = await runtime.SnapshotAsync();
        _ = await runtime.SnapshotAsync();
        _ = await runtime.SnapshotAsync();

        Assert.Equal(1, resolver.CallCount);
    }

    [Fact]
    public async Task SnapshotAsync_RetriesTransientExternalFailureWithinTheSameTrack()
    {
        var provider = new FlakyExternalProvider(BuildDocument("LRCLIB_retry", 180.0, "recovered"));
        var runtime = new LyricsRuntimeService(
            new CandidateLyricsProvider([]),
            new StubPlayerProvider(new PlayerState("Song", "Artist", "Album", 1.0, 180.0, true)),
            new LyricsSynchronizer(),
            new PlaybackClock())
        {
            ExternalLyricsProviders = [provider],
            ExternalRetryBaseDelay = TimeSpan.Zero,
        };

        _ = await runtime.SnapshotAsync();
        _ = await runtime.SnapshotAsync();
        var resolved = await runtime.SnapshotAsync();

        Assert.Equal("LRCLIB_retry", resolved.Document!.LyricsId);
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task SnapshotAsync_StopsExternalRetriesAfterThreeTransientFailures()
    {
        var provider = new FlakyExternalProvider(
            BuildDocument("LRCLIB_never", 180.0, "unreachable"),
            failuresBeforeSuccess: int.MaxValue);
        var runtime = new LyricsRuntimeService(
            new CandidateLyricsProvider([]),
            new StubPlayerProvider(new PlayerState("Song", "Artist", "Album", 1.0, 180.0, true)),
            new LyricsSynchronizer(),
            new PlaybackClock())
        {
            ExternalLyricsProviders = [provider],
            ExternalRetryBaseDelay = TimeSpan.Zero,
        };

        for (var poll = 0; poll < 6; poll++)
        {
            _ = await runtime.SnapshotAsync();
        }

        Assert.Equal(3, provider.CallCount);
    }

    [Fact]
    public async Task SnapshotAsync_CancelsAnExternalAttemptWhenTrackChanges()
    {
        var provider = new CancellableExternalProvider();
        var runtime = new LyricsRuntimeService(
            new CandidateLyricsProvider([]),
            new SequencePlayerProvider(
            [
                new PlayerState("Song A", "Artist", "Album", 1.0, 180.0, true),
                new PlayerState("Song B", "Artist", "Album", 1.0, 180.0, true),
            ]),
            new LyricsSynchronizer(),
            new PlaybackClock())
        {
            ExternalLyricsProviders = [provider],
            ExternalRetryBaseDelay = TimeSpan.Zero,
        };

        _ = await runtime.SnapshotAsync();
        _ = await runtime.SnapshotAsync();
        await provider.FirstAttemptCanceled.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, provider.CallCount);
    }

    private static LyricsDocument BuildDocument(
        string lyricsId,
        double durationSeconds,
        string text,
        double begin = 0.0,
        double end = 5.0)
    {
        return new LyricsDocument(
            LyricsId: lyricsId,
            Status: "success",
            SourceFile: lyricsId + ".json",
            UpdatedAt: DateTimeOffset.UtcNow,
            Lines: [new LyricsLine(begin, end, text)],
            DurationSeconds: durationSeconds);
    }

    private sealed class StubLyricsProvider : ILyricsDocumentProvider
    {
        private readonly LyricsDocument _document;

        public StubLyricsProvider(LyricsDocument document)
        {
            _document = document;
        }

        public int CallCount { get; private set; }

        public Task<LyricsDocument?> GetLatestLyricsAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<LyricsDocument?>(_document);
        }
    }

    private sealed class CandidateLyricsProvider : IPlayerMatchedLyricsProvider
    {
        public CandidateLyricsProvider(IReadOnlyList<LyricsMatch> candidates)
        {
            Candidates = candidates;
        }

        public IReadOnlyList<LyricsMatch> Candidates { get; set; }

        public Task<LyricsDocument?> GetLatestLyricsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Candidates.Count > 0 ? Candidates[0].Document : null);
        }

        public Task<IReadOnlyList<LyricsMatch>> FindCandidatesAsync(
            PlayerState player,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Candidates);
        }
    }

    private sealed class StubExternalProvider : IExternalLyricsProvider
    {
        private readonly LyricsDocument _document;
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StubExternalProvider(LyricsDocument document)
        {
            _document = document;
        }

        public string Name => "Stub";

        public int CallCount { get; private set; }

        /// <summary>Lets a test wait for the background fetch instead of racing it.</summary>
        public Task Completed => _completed.Task;

        public Task<LyricsDocument?> FetchAsync(PlayerState player, CancellationToken cancellationToken = default)
        {
            CallCount++;
            _completed.TrySetResult();
            return Task.FromResult<LyricsDocument?>(_document);
        }
    }

    private sealed class FlakyExternalProvider : IExternalLyricsProvider
    {
        private readonly LyricsDocument _document;
        private readonly int _failuresBeforeSuccess;

        public FlakyExternalProvider(LyricsDocument document, int failuresBeforeSuccess = 1)
        {
            _document = document;
            _failuresBeforeSuccess = failuresBeforeSuccess;
        }

        public string Name => "Flaky";

        public int CallCount { get; private set; }

        public Task<LyricsDocument?> FetchAsync(PlayerState player, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return CallCount <= _failuresBeforeSuccess
                ? Task.FromException<LyricsDocument?>(new HttpRequestException("temporary"))
                : Task.FromResult<LyricsDocument?>(_document);
        }
    }

    private sealed class CancellableExternalProvider : IExternalLyricsProvider
    {
        private readonly TaskCompletionSource _firstAttemptCanceled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "Cancellable";

        public int CallCount { get; private set; }

        public Task FirstAttemptCanceled => _firstAttemptCanceled.Task;

        public async Task<LyricsDocument?> FetchAsync(
            PlayerState player,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (CallCount > 1)
            {
                return null;
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _firstAttemptCanceled.TrySetResult();
                throw;
            }
        }
    }

    private sealed class FlakyCatalogResolver : ICatalogSongResolver
    {
        private readonly IReadOnlyList<string> _lyricsIds;

        public FlakyCatalogResolver(IReadOnlyList<string> lyricsIds)
        {
            _lyricsIds = lyricsIds;
        }

        public int CallCount { get; private set; }

        public Task<IReadOnlyList<string>> ResolveLyricsIdCandidatesAsync(
            PlayerState player,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return CallCount == 1
                ? Task.FromException<IReadOnlyList<string>>(new HttpRequestException("temporary"))
                : Task.FromResult(_lyricsIds);
        }
    }

    private sealed class StubCatalogResolver : ICatalogSongResolver
    {
        private readonly IReadOnlyList<string> _lyricsIds;

        public StubCatalogResolver(IReadOnlyList<string> lyricsIds)
        {
            _lyricsIds = lyricsIds;
        }

        public int CallCount { get; private set; }

        public Task<IReadOnlyList<string>> ResolveLyricsIdCandidatesAsync(
            PlayerState player,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_lyricsIds);
        }
    }

    private sealed class StubPlayerProvider : IPlayerSessionProvider
    {
        private readonly PlayerState _playerState;

        public StubPlayerProvider(PlayerState playerState)
        {
            _playerState = playerState;
        }

        public int CallCount { get; private set; }

        public Task<PlayerState?> GetCurrentPlayerStateAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<PlayerState?>(_playerState);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => 1_000_000;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan amount)
        {
            _timestamp += (long)(amount.TotalSeconds * TimestampFrequency);
        }
    }

    private sealed class SequencePlayerProvider : IPlayerSessionProvider
    {
        private readonly Queue<PlayerState> _states;

        public SequencePlayerProvider(IEnumerable<PlayerState> states)
        {
            _states = new Queue<PlayerState>(states);
        }

        public Task<PlayerState?> GetCurrentPlayerStateAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<PlayerState?>(_states.Count > 1 ? _states.Dequeue() : _states.Peek());
        }
    }
}
