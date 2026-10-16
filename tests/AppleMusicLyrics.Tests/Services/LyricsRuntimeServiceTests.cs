using AppleMusicLyrics.Application.Services;
using AppleMusicLyrics.App.Presentation;
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

        var lyricsProvider = new CandidateLyricsProvider(
            [new LyricsMatch(matchedDocument, 100, 0.0, HasContentMatch: true)]);
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

        var lyricsProvider = new CandidateLyricsProvider(
            [new LyricsMatch(playing, 100, 0.0, HasContentMatch: true)]);
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
    public async Task SnapshotAsync_DoesNotUseHoneyLyricsForLaFemmeDArgentInstrumental()
    {
        // Regression: an unrelated cached lyric used to be accepted immediately when it was the
        // only file within one second of the active duration. An instrumental must not inherit
        // that lyric merely because the two recordings happen to have nearly identical lengths.
        var honey = BuildDocument("AP_mariah_honey", 428.0, "Oh honey, you can have me when you want me");
        var resolver = new StubCatalogResolver(["AP_air_la_femme_dargent"]);
        var runtime = new LyricsRuntimeService(
            new CandidateLyricsProvider(
            [
                new LyricsMatch(honey, 100, 0.0, HasContentMatch: false),
            ]),
            new StubPlayerProvider(new PlayerState(
                "La femme d'argent",
                "Air",
                "Moon Safari",
                10.0,
                428.0,
                true)),
            new LyricsSynchronizer(),
            new PlaybackClock(),
            catalogResolver: resolver);

        var snapshot = await runtime.SnapshotAsync();

        Assert.Null(snapshot.Document);
        Assert.Null(snapshot.ActiveLyric.CurrentLine);
        Assert.Equal(LyricsResolutionStatus.Unavailable, snapshot.Resolution.Status);
        Assert.Equal(1, resolver.CallCount);
    }

    [Fact]
    public async Task SnapshotAsync_CatalogVerifiesAppleLyricsIdWithLanguageSuffix()
    {
        // Current Apple Music cache format: Deja Vu is AP_6808333042-en while the public catalog
        // correctly returns the language-neutral song id AP_6808333042.
        var dejaVu = BuildDocument("AP_6808333042-en", 240.28, "Baby, seem like everywhere I go");
        var runtime = new LyricsRuntimeService(
            new CandidateLyricsProvider(
            [
                new LyricsMatch(dejaVu, 80, 1.28),
            ]),
            new StubPlayerProvider(new PlayerState(
                "DEJA VU (feat. JAŸ-Z)",
                "Beyoncé",
                "B'DAY (20th ANNIVERSARY DELUXE EDITION)",
                10.0,
                239.0,
                true)),
            new LyricsSynchronizer(),
            new PlaybackClock(),
            catalogResolver: new StubCatalogResolver(["AP_6808333042"]));

        var snapshot = await runtime.SnapshotAsync();

        Assert.Equal("AP_6808333042-en", snapshot.Document!.LyricsId);
        Assert.Equal(LyricsResolutionStatus.Resolved, snapshot.Resolution.Status);
        Assert.Equal(LyricsResolutionConfidence.High, snapshot.Resolution.Confidence);
        Assert.Equal(LyricsResolutionSource.CatalogVerifiedCache, snapshot.Resolution.Source);
    }

    [Fact]
    public async Task SnapshotAsync_HoldsBackClearWinnerWhileCatalogLookupIsPending()
    {
        var durationWinner = BuildDocument("AP_111", 180.2, "Song");
        var catalogWinner = BuildDocument("AP_222", 185.0, "right song");
        var resolver = new PendingCatalogResolver();
        var runtime = new LyricsRuntimeService(
            new CandidateLyricsProvider(
            [
                new LyricsMatch(durationWinner, 160, 0.2, HasContentMatch: true),
                new LyricsMatch(catalogWinner, 25, 5.0),
            ]),
            new StubPlayerProvider(new PlayerState("Song", "Artist", "Album", 1.0, 180.0, true)),
            new LyricsSynchronizer(),
            new PlaybackClock(),
            catalogResolver: resolver);

        var pending = await runtime.SnapshotAsync();

        Assert.Null(pending.Document);
        Assert.Equal(LyricsResolutionStatus.VerifyingCatalog, pending.Resolution.Status);

        resolver.Complete(["AP_222"]);
        RuntimeSnapshot resolved = pending;
        for (var attempt = 0; attempt < 10 && resolved.Document is null; attempt++)
        {
            await Task.Yield();
            resolved = await runtime.SnapshotAsync();
        }

        Assert.Equal("AP_222", resolved.Document!.LyricsId);
        Assert.Equal(LyricsResolutionSource.CatalogVerifiedCache, resolved.Resolution.Source);
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
    public async Task SnapshotAsync_HoldsBackDurationOnlyClearWinnerWhenCatalogLookupFails()
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

        Assert.Null(snapshot.Document);
        Assert.Equal(LyricsResolutionStatus.Unavailable, snapshot.Resolution.Status);
        Assert.Equal(LyricsResolutionConfidence.Low, snapshot.Resolution.Confidence);
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

        var lyricsProvider = new CandidateLyricsProvider(
            [new LyricsMatch(only, 100, 0.2, HasContentMatch: true)]);
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

        var lyricsProvider = new CandidateLyricsProvider(
            [new LyricsMatch(document, 100, 0.0, HasContentMatch: true)]);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotAsync_RetainsExternalLyricsBetweenLocalRescansAfterMatchWindow(bool preferRichLyrics)
    {
        var time = new ManualTimeProvider();
        var document = RichDocument("LRCLIB_retained");
        var provider = new StubExternalProvider(document);
        await using var runtime = new LyricsRuntimeService(
            new CandidateLyricsProvider([]),
            new StubPlayerProvider(new PlayerState("Song", "Artist", "Album", 1, 180, true)),
            new LyricsSynchronizer(), new PlaybackClock(), timeProvider: time)
        {
            ExternalLyricsProviders = [provider],
            PreferWordTimings = preferRichLyrics,
            PreferTranslations = preferRichLyrics,
            MatchWindow = TimeSpan.FromSeconds(5),
            IdleRescanInterval = TimeSpan.FromSeconds(1),
        };

        await runtime.SnapshotAsync();
        Assert.Same(document, (await runtime.SnapshotAsync()).Document);
        if (preferRichLyrics)
        {
            // The source has no additional word timings or translation, so no enriched copy
            // can accidentally conceal a missing base document in subsequent snapshots.
            await WaitForSnapshotAsync(runtime, snapshot => snapshot.Resolution.Summary.Contains("checked sources", StringComparison.Ordinal));
        }
        time.Advance(TimeSpan.FromSeconds(6));
        Assert.Same(document, (await runtime.SnapshotAsync()).Document);

        for (var poll = 0; poll < 12; poll++)
        {
            time.Advance(TimeSpan.FromMilliseconds(200));
            var snapshot = await runtime.SnapshotAsync();

            Assert.Same(document, snapshot.Document);
            Assert.Equal(LyricsResolutionStatus.Resolved, snapshot.Resolution.Status);
            Assert.Equal(LyricsResolutionSource.ExternalProvider, snapshot.Resolution.Source);
            var presentation = OverlaySnapshotPresenter.CreateLyrics(snapshot);
            Assert.Equal("hello world", presentation.CurrentText);
            Assert.DoesNotContain("Waiting for lyrics", presentation.CurrentText);
        }
        Assert.Equal(preferRichLyrics ? 2 : 1, provider.CallCount);
    }

    [Fact]
    public async Task SnapshotAsync_PrefersTimedPlatformLyricsOverProjectedPlainLyrics()
    {
        var plain = new StubExternalProvider(BuildDocument("LRCLIB_plain", 180, "plain") with { Status = "plain" });
        var timed = new StubExternalProvider(BuildDocument("qqmusic:42", 180, "timed"));
        var runtime = new LyricsRuntimeService(
            new CandidateLyricsProvider([]),
            new StubPlayerProvider(new PlayerState("Song", "Artist", "Album", 1, 180, true)),
            new LyricsSynchronizer(), new PlaybackClock())
        {
            ExternalLyricsProviders = [plain, timed],
        };

        await runtime.SnapshotAsync();
        await timed.Completed;
        RuntimeSnapshot? snapshot = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            snapshot = await runtime.SnapshotAsync();
            if (snapshot.Document is not null) break;
            await Task.Delay(10);
        }
        Assert.Equal("qqmusic:42", snapshot!.Document!.LyricsId);
        Assert.Equal(1, plain.CallCount);
        Assert.Equal(1, timed.CallCount);
    }

    [Fact]
    public async Task SnapshotAsync_KeepsPlainFallbackWhenTheNextSourceFails()
    {
        var plain = new StubExternalProvider(BuildDocument("LRCLIB_plain", 180, "plain") with { Status = "plain" });
        var failed = new FlakyExternalProvider(BuildDocument("unavailable", 180, "timed"), failuresBeforeSuccess: 100);
        var runtime = new LyricsRuntimeService(
            new CandidateLyricsProvider([]),
            new StubPlayerProvider(new PlayerState("Song", "Artist", "Album", 1, 180, true)),
            new LyricsSynchronizer(), new PlaybackClock())
        {
            ExternalLyricsProviders = [plain, failed],
        };

        await runtime.SnapshotAsync();
        RuntimeSnapshot? snapshot = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            snapshot = await runtime.SnapshotAsync();
            if (snapshot.Document is not null) break;
            await Task.Delay(10);
        }
        Assert.Equal("LRCLIB_plain", snapshot!.Document!.LyricsId);
        Assert.Equal(1, failed.CallCount);
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
            new CandidateLyricsProvider([new LyricsMatch(local, 100, 0.2, HasContentMatch: true)]),
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
            new CandidateLyricsProvider([new LyricsMatch(local, 100, 0.2, HasContentMatch: true)]),
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
    public async Task SnapshotAsync_KeepsReportingTheExternalSourceAcrossLaterLocalRescans()
    {
        // Regression: every later local rescan reported its own miss as "Unavailable" while the
        // external lyrics stayed on screen, which also mislabelled overnight diagnostic records.
        var time = new ManualTimeProvider();
        var provider = new StubExternalProvider(BuildDocument("LRCLIB_1", 180.0, "external line"));
        var localCache = new CandidateLyricsProvider([]);
        var runtime = new LyricsRuntimeService(
            localCache,
            new StubPlayerProvider(new PlayerState("Song", "Artist", "Album", 1.0, 180.0, true)),
            new LyricsSynchronizer(),
            new PlaybackClock(time),
            timeProvider: time)
        {
            ExternalLyricsProviders = [provider],
        };

        _ = await runtime.SnapshotAsync();
        await provider.Completed;

        for (var poll = 0; poll < 3; poll++)
        {
            var snapshot = await runtime.SnapshotAsync();

            Assert.Equal("LRCLIB_1", snapshot.Document!.LyricsId);
            Assert.Equal(LyricsResolutionStatus.Resolved, snapshot.Resolution.Status);
            Assert.Equal(LyricsResolutionSource.ExternalProvider, snapshot.Resolution.Source);
        }

        Assert.Equal(4, localCache.ScanCount);
    }

    [Fact]
    public async Task SnapshotAsync_KeepsExternalLyricsBetweenIdleRescans()
    {
        // Regression: a result adopted after the match window set "Resolved"; the next poll skipped
        // the rescan, no longer qualified for the external lookup, and blanked the lyrics.
        var time = new ManualTimeProvider();
        var provider = new StubExternalProvider(BuildDocument("LRCLIB_1", 180.0, "external line"));
        var localCache = new CandidateLyricsProvider([]);
        var runtime = new LyricsRuntimeService(
            localCache,
            new StubPlayerProvider(new PlayerState("Song", "Artist", "Album", 1.0, 180.0, true)),
            new LyricsSynchronizer(),
            new PlaybackClock(time),
            timeProvider: time)
        {
            ExternalLyricsProviders = [provider],
            MatchWindow = TimeSpan.FromSeconds(1),
        };

        _ = await runtime.SnapshotAsync();
        await provider.Completed;

        time.Advance(TimeSpan.FromSeconds(2));
        var adopted = await runtime.SnapshotAsync();
        time.Advance(TimeSpan.FromMilliseconds(200));
        var betweenRescans = await runtime.SnapshotAsync();

        Assert.Equal("LRCLIB_1", adopted.Document!.LyricsId);
        Assert.Equal("LRCLIB_1", betweenRescans.Document?.LyricsId);
        Assert.Equal(LyricsResolutionSource.ExternalProvider, betweenRescans.Resolution.Source);
        Assert.Equal(2, localCache.ScanCount);
    }

    [Fact]
    public async Task SnapshotAsync_BacksOffIdleRescansButStillFindsALateCacheFile()
    {
        var time = new ManualTimeProvider();
        var localCache = new CandidateLyricsProvider([]);
        var runtime = new LyricsRuntimeService(
            localCache,
            new StubPlayerProvider(new PlayerState("Song", "Artist", "Album", 1.0, 180.0, true)),
            new LyricsSynchronizer(),
            new PlaybackClock(time),
            timeProvider: time)
        {
            MatchWindow = TimeSpan.FromSeconds(1),
            IdleRescanInterval = TimeSpan.FromSeconds(1),
            MaxIdleRescanInterval = TimeSpan.FromSeconds(4),
        };

        // 200 ms polls for 20 s. Every poll inside the match window scans (t = 0 to 1.0, six
        // scans); the settled misses after it back off to t = 2, 4, 8, 12, 16 and 20.
        for (var poll = 0; poll <= 100; poll++)
        {
            _ = await runtime.SnapshotAsync();
            time.Advance(TimeSpan.FromMilliseconds(200));
        }

        Assert.Equal(12, localCache.ScanCount);

        localCache.Candidates = [new LyricsMatch(BuildDocument("AP_late", 180.0, "late line"), 100, 0.0, HasContentMatch: true)];
        LyricsDocument? found = null;
        for (var poll = 0; poll < 25 && found is null; poll++)
        {
            found = (await runtime.SnapshotAsync()).Document;
            time.Advance(TimeSpan.FromMilliseconds(200));
        }

        Assert.Equal("AP_late", found?.LyricsId);
    }

    [Fact]
    public async Task SnapshotAsync_ReopensMatchingOnceWhenTheDurationCorrectsItself()
    {
        // Regression: the matched duration was never cleared, so once it disagreed with the player
        // every poll reset the lookups and reopened the match window, rescanning the cache on every
        // poll and cancelling any lookup for the corrected duration before it could land.
        var time = new ManualTimeProvider();
        var localCache = new CandidateLyricsProvider(
            [new LyricsMatch(BuildDocument("AP_1", 180.0, "local line"), 100, 0.0, HasContentMatch: true)]);
        var provider = new StubExternalProvider(BuildDocument("LRCLIB_1", 200.0, "external line"));
        var runtime = new LyricsRuntimeService(
            localCache,
            new SequencePlayerProvider(
            [
                new PlayerState("Song", "Artist", "Album", 1.0, 180.0, true),
                new PlayerState("Song", "Artist", "Album", 1.0, 200.0, true),
            ]),
            new LyricsSynchronizer(),
            new PlaybackClock(time),
            timeProvider: time)
        {
            ExternalLyricsProviders = [provider],
            MatchWindow = TimeSpan.FromSeconds(1),
        };

        var matched = await runtime.SnapshotAsync();
        Assert.Equal("AP_1", matched.Document!.LyricsId);

        localCache.Candidates = [];
        RuntimeSnapshot? last = null;
        for (var poll = 0; poll < 15; poll++)
        {
            time.Advance(TimeSpan.FromMilliseconds(200));
            last = await runtime.SnapshotAsync();
        }

        // One scan for the original match, one per poll inside the reopened window (t = 0.2 to
        // 1.2), then a single idle rescan at t = 2.2.
        Assert.Equal(8, localCache.ScanCount);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal("LRCLIB_1", last!.Document?.LyricsId);
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

    [Fact]
    public async Task DisposeAsync_CancelsAndObservesBackgroundFetches()
    {
        var provider = new CancellableExternalProvider();
        var runtime = new LyricsRuntimeService(
            new CandidateLyricsProvider([]),
            new StubPlayerProvider(new PlayerState("Song", "Artist", "Album", 1.0, 180.0, true)),
            new LyricsSynchronizer(),
            new PlaybackClock())
        {
            ExternalLyricsProviders = [provider],
        };

        _ = await runtime.SnapshotAsync();
        await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        await provider.FirstAttemptCanceled.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => runtime.SnapshotAsync());
    }

    [Fact]
    public async Task SnapshotAsync_EnrichesLocalLyricsWithoutDelayingTheBaseOrQueryingPastCompleteResult()
    {
        var local = RichDocument("AP_local");
        var completion = new TaskCompletionSource<LyricsDocument?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rich = RichDocument("QQ_rich", words: true, translation: true);
        var provider = new DelegateEnrichmentProvider("QQ Music", (_, _) => completion.Task);
        var skipped = new StubExternalProvider(RichDocument("NE_unneeded", words: true, translation: true));
        await using var runtime = EnrichmentRuntime(local, [provider, skipped], words: true, translation: true);

        var first = await runtime.SnapshotAsync().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Same(local, first.Document);
        Assert.Equal(LyricsResolutionStatus.Resolved, first.Resolution.Status);
        Assert.Equal(LyricsResolutionSource.AppleMusicCache, first.Resolution.Source);
        Assert.Contains("checking missing word timings and translations", first.Resolution.Summary);
        await provider.Started.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Same(local, (await runtime.SnapshotAsync()).Document);
        completion.SetResult(rich);

        var upgraded = await WaitForSnapshotAsync(runtime, snapshot => ReferenceEquals(snapshot.Document, rich));

        Assert.Equal(LyricsResolutionSource.ExternalProvider, upgraded.Resolution.Source);
        Assert.Contains("QQ Music", upgraded.Resolution.Summary);
        Assert.Equal(0, skipped.CallCount);
        // Repeated local candidate adoption must not cancel or forget the independent enrichment.
        Assert.Same(rich, (await runtime.SnapshotAsync()).Document);
    }

    [Fact]
    public async Task SnapshotAsync_EnrichesSynchronizedLrcLibFallbackFromLaterPlatform()
    {
        var basic = RichDocument("LRCLIB_base");
        var lrcLib = new StubExternalProvider(basic);
        var completion = new TaskCompletionSource<LyricsDocument?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rich = RichDocument("NETEASE_rich", words: true, translation: true);
        var platform = new DelegateEnrichmentProvider("NetEase Cloud Music", (_, _) => completion.Task);
        await using var runtime = EnrichmentRuntime(basic, [lrcLib, platform], words: true, translation: true,
            lyricsProvider: new CandidateLyricsProvider([]));

        await runtime.SnapshotAsync();
        var baseSnapshot = await WaitForSnapshotAsync(runtime, snapshot => ReferenceEquals(snapshot.Document, basic));
        Assert.Same(basic, baseSnapshot.Document);
        await platform.Started.WaitAsync(TimeSpan.FromSeconds(2));
        completion.SetResult(rich);

        var upgraded = await WaitForSnapshotAsync(runtime, snapshot => ReferenceEquals(snapshot.Document, rich));

        Assert.Contains("NetEase Cloud Music", upgraded.Resolution.Summary);
        Assert.True(lrcLib.CallCount >= 1);
    }

    [Fact]
    public async Task SnapshotAsync_AdoptsPartialRichResultWhileTheNextSourceIsStillPending()
    {
        var local = RichDocument("AP_local");
        var wordDocument = RichDocument("QQ_words", words: true);
        var words = new StubExternalProvider(wordDocument);
        var completion = new TaskCompletionSource<LyricsDocument?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeDocument = RichDocument("NE_complete", words: true, translation: true);
        var slow = new DelegateEnrichmentProvider("Slow source", (_, _) => completion.Task);
        await using var runtime = EnrichmentRuntime(local, [words, slow], words: true, translation: true);

        Assert.Same(local, (await runtime.SnapshotAsync()).Document);
        await slow.Started.WaitAsync(TimeSpan.FromSeconds(2));
        var partial = await runtime.SnapshotAsync().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Same(wordDocument, partial.Document);
        Assert.False(completion.Task.IsCompleted);
        Assert.Same(wordDocument, runtime.GetFrameSnapshot().Document);
        completion.SetResult(completeDocument);
        await WaitForSnapshotAsync(runtime, snapshot => ReferenceEquals(snapshot.Document, completeDocument));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SnapshotAsync_DoesNotTradeAwayAnExistingRequestedFeature(bool baselineWords)
    {
        var local = RichDocument("AP_local", words: baselineWords, translation: !baselineWords);
        var otherFeatureOnly = RichDocument("QQ_incomplete", words: !baselineWords, translation: baselineWords);
        var provider = new DelegateEnrichmentProvider("Platform", (_, _) => Task.FromResult<LyricsDocument?>(otherFeatureOnly));
        await using var runtime = EnrichmentRuntime(local, [provider], words: true, translation: true);

        Assert.Same(local, (await runtime.SnapshotAsync()).Document);
        await provider.Started.WaitAsync(TimeSpan.FromSeconds(2));
        for (var poll = 0; poll < 10; poll++)
        {
            Assert.Same(local, (await runtime.SnapshotAsync()).Document);
            await Task.Delay(1);
        }
        Assert.Equal(1, provider.CallCount);
        var completed = await runtime.SnapshotAsync();
        var diagnostic = Assert.Single(completed.ExternalDiagnostics!.Providers,
            item => item.Stage == ExternalLyricsQueryStage.Enrichment);
        Assert.Equal(ExternalLyricsQueryOutcome.Rejected, diagnostic.Outcome);
        Assert.Equal(baselineWords ? ExternalLyricsDiagnosticReason.WouldLoseWordTimings
            : ExternalLyricsDiagnosticReason.WouldLoseTranslations, diagnostic.Reason);
        Assert.Equal(baselineWords ? 1 : 0, diagnostic.TranslatedLineCount);
        Assert.Equal(baselineWords ? 0 : 1, diagnostic.WordLineCount);
    }

    [Fact]
    public async Task SnapshotAsync_DoesNotQueryEnrichmentWhenPreferencesAreOff()
    {
        var local = RichDocument("AP_local");
        var provider = new StubExternalProvider(RichDocument("QQ_rich", words: true, translation: true));
        await using var runtime = EnrichmentRuntime(local, [provider]);

        Assert.Same(local, (await runtime.SnapshotAsync()).Document);
        Assert.Same(local, (await runtime.SnapshotAsync()).Document);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task SnapshotAsync_DoesNotQueryWhenTheLocalDocumentAlreadyHasRequestedFeatures()
    {
        var local = RichDocument("AP_complete", words: true, translation: true);
        var provider = new StubExternalProvider(RichDocument("QQ_rich", words: true, translation: true));
        await using var runtime = EnrichmentRuntime(local, [provider], words: true, translation: true);

        Assert.Same(local, (await runtime.SnapshotAsync()).Document);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task SnapshotAsync_ReportsCompletedEnrichmentMissWithoutLosingTheBaseSource()
    {
        var local = RichDocument("AP_local");
        var provider = new DelegateEnrichmentProvider("Empty source", (_, _) => Task.FromResult<LyricsDocument?>(null));
        await using var runtime = EnrichmentRuntime(local, [provider], words: true, translation: true);

        await runtime.SnapshotAsync();
        var completed = await WaitForSnapshotAsync(runtime,
            snapshot => snapshot.Resolution.Summary.Contains("checked sources", StringComparison.Ordinal));

        Assert.Same(local, completed.Document);
        Assert.Equal(LyricsResolutionStatus.Resolved, completed.Resolution.Status);
        Assert.Equal(LyricsResolutionSource.AppleMusicCache, completed.Resolution.Source);
        Assert.Equal(LyricsResolutionConfidence.High, completed.Resolution.Confidence);
        Assert.Equal(1, provider.CallCount);
        var repeated = await runtime.SnapshotAsync();
        Assert.Equal(completed.Resolution.Summary, repeated.Resolution.Summary);
    }

    [Fact]
    public async Task SnapshotAsync_RejectsUnusableWordTimingAsAnEnrichment()
    {
        var local = RichDocument("AP_local");
        var invalid = RichDocument("QQ_invalid", words: true) with
        {
            Lines = [new LyricsLine(0, 5, "hello world", Words: [new LyricWord(0, 0, "different text")])],
        };
        var provider = new DelegateEnrichmentProvider("Platform", (_, _) => Task.FromResult<LyricsDocument?>(invalid));
        await using var runtime = EnrichmentRuntime(local, [provider], words: true);

        await runtime.SnapshotAsync();
        await provider.Started.WaitAsync(TimeSpan.FromSeconds(2));
        for (var poll = 0; poll < 5; poll++)
        {
            Assert.Same(local, (await runtime.SnapshotAsync()).Document);
            await Task.Delay(1);
        }
    }

    [Fact]
    public async Task SnapshotAsync_PreferenceChangesCancelAndRestartEnrichmentForTheSameTrack()
    {
        var local = RichDocument("AP_local");
        var oldCompletion = new TaskCompletionSource<LyricsDocument?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rich = RichDocument("QQ_words", words: true);
        var calls = 0;
        var provider = new DelegateEnrichmentProvider("Platform", (_, _) =>
            Interlocked.Increment(ref calls) == 1 ? oldCompletion.Task : Task.FromResult<LyricsDocument?>(rich));
        await using var runtime = EnrichmentRuntime(local, [provider]);

        await runtime.SnapshotAsync();
        Assert.Equal(0, provider.CallCount);
        runtime.PreferWordTimings = true;
        Assert.Same(local, (await runtime.SnapshotAsync()).Document);
        await provider.Started.WaitAsync(TimeSpan.FromSeconds(2));
        runtime.PreferWordTimings = false;
        Assert.Same(local, (await runtime.SnapshotAsync()).Document);
        await provider.FirstCanceled.WaitAsync(TimeSpan.FromSeconds(2));
        oldCompletion.SetResult(rich);
        Assert.Same(local, (await runtime.SnapshotAsync()).Document);
        runtime.PreferWordTimings = true;

        await WaitForSnapshotAsync(runtime, snapshot => ReferenceEquals(snapshot.Document, rich));
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task SnapshotAsync_DiscardsCanceledEnrichmentAfterTheTrackChanges()
    {
        var oldLocal = RichDocument("AP_old");
        var newLocal = RichDocument("AP_new");
        var candidates = new CandidateLyricsProvider([new LyricsMatch(oldLocal, 100, 0, HasContentMatch: true)]);
        var player = new MutableEnrichmentPlayer(new PlayerState("Song A", "Artist", "Album", 1, 180, true));
        var oldCompletion = new TaskCompletionSource<LyricsDocument?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new DelegateEnrichmentProvider("Platform", (state, _) => state.Title == "Song A"
            ? oldCompletion.Task : Task.FromResult<LyricsDocument?>(null));
        await using var runtime = EnrichmentRuntime(oldLocal, [provider], words: true,
            lyricsProvider: candidates, playerProvider: player);

        await runtime.SnapshotAsync();
        await provider.Started.WaitAsync(TimeSpan.FromSeconds(2));
        player.State = player.State with { Title = "Song B" };
        candidates.Candidates = [new LyricsMatch(newLocal, 100, 0, HasContentMatch: true)];
        Assert.Same(newLocal, (await runtime.SnapshotAsync()).Document);
        await provider.FirstCanceled.WaitAsync(TimeSpan.FromSeconds(2));
        oldCompletion.SetResult(RichDocument("QQ_old", words: true));
        for (var poll = 0; poll < 5; poll++)
        {
            Assert.Same(newLocal, (await runtime.SnapshotAsync()).Document);
            await Task.Delay(1);
        }
        var current = await WaitForSnapshotAsync(runtime,
            snapshot => snapshot.ExternalDiagnostics?.EnrichmentState == ExternalLyricsQueryState.Completed);
        var diagnostic = Assert.Single(current.ExternalDiagnostics!.Providers,
            item => item.Stage == ExternalLyricsQueryStage.Enrichment);
        Assert.Equal(ExternalLyricsQueryOutcome.NoResult, diagnostic.Outcome);
        Assert.Equal(0, diagnostic.LineCount);
        Assert.Same(newLocal, current.Document);
    }

    [Fact]
    public async Task SnapshotAsync_DurationCorrectionCancelsTheOldEnrichmentScope()
    {
        var local = RichDocument("AP_local");
        var candidates = new CandidateLyricsProvider([new LyricsMatch(local, 100, 0, HasContentMatch: true)]);
        var player = new MutableEnrichmentPlayer(new PlayerState("Song", "Artist", "Album", 1, 180, true));
        var oldCompletion = new TaskCompletionSource<LyricsDocument?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new DelegateEnrichmentProvider("Platform", (state, _) => state.Duration == 180
            ? oldCompletion.Task : Task.FromResult<LyricsDocument?>(null));
        await using var runtime = EnrichmentRuntime(local, [provider], words: true,
            lyricsProvider: candidates, playerProvider: player);

        await runtime.SnapshotAsync();
        await provider.Started.WaitAsync(TimeSpan.FromSeconds(2));
        player.State = player.State with { Duration = 184 };
        var corrected = local with { DurationSeconds = 184 };
        candidates.Candidates = [new LyricsMatch(corrected, 100, 0, HasContentMatch: true)];
        Assert.Same(corrected, (await runtime.SnapshotAsync()).Document);
        await provider.FirstCanceled.WaitAsync(TimeSpan.FromSeconds(2));
        oldCompletion.SetResult(RichDocument("QQ_old_duration", words: true));
        Assert.Same(corrected, (await runtime.SnapshotAsync()).Document);
    }

    [Fact]
    public async Task SnapshotAsync_EnrichmentRetriesTransientFailuresAtMostThreeTimesAndKeepsBase()
    {
        var local = RichDocument("AP_local");
        var provider = new DelegateEnrichmentProvider("Failing platform", (_, _) =>
            Task.FromException<LyricsDocument?>(new HttpRequestException("temporary")));
        await using var runtime = EnrichmentRuntime(local, [provider], words: true);

        await WaitForSnapshotAsync(runtime, _ => provider.CallCount == 3);
        for (var poll = 0; poll < 10; poll++)
        {
            Assert.Same(local, (await runtime.SnapshotAsync()).Document);
            await Task.Delay(1);
        }
        Assert.Equal(3, provider.CallCount);
    }

    [Fact]
    public async Task SnapshotAsync_EnrichmentCanRecoverAfterTransientFailure()
    {
        var local = RichDocument("AP_local");
        var rich = RichDocument("QQ_recovered", words: true);
        var calls = 0;
        var provider = new DelegateEnrichmentProvider("Platform", (_, _) => Interlocked.Increment(ref calls) == 1
            ? Task.FromException<LyricsDocument?>(new HttpRequestException("temporary"))
            : Task.FromResult<LyricsDocument?>(rich));
        await using var runtime = EnrichmentRuntime(local, [provider], words: true);

        var upgraded = await WaitForSnapshotAsync(runtime, snapshot => ReferenceEquals(snapshot.Document, rich));

        Assert.Same(rich, upgraded.Document);
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task SnapshotAsync_ReportsEveryFallbackSourceWithoutTreatingNullAsProvenNoMatch()
    {
        var empty = new DelegateEnrichmentProvider("Empty", (_, _) => Task.FromResult<LyricsDocument?>(null));
        var failed = new DelegateEnrichmentProvider("Forbidden", (_, _) => Task.FromException<LyricsDocument?>(
            new HttpRequestException("secret cookie and request URL", null, System.Net.HttpStatusCode.Forbidden)));
        var rich = RichDocument("QQ_diagnostic", words: true, translation: true);
        var success = new DelegateEnrichmentProvider("QQ Music", (_, _) => Task.FromResult<LyricsDocument?>(rich));
        var skipped = new DelegateEnrichmentProvider("Not needed", (_, _) => Task.FromResult<LyricsDocument?>(null));
        await using var runtime = EnrichmentRuntime(rich, [empty, failed, success, skipped],
            lyricsProvider: new CandidateLyricsProvider([]));

        var result = await WaitForSnapshotAsync(runtime, snapshot => ReferenceEquals(snapshot.Document, rich));
        var diagnostics = result.ExternalDiagnostics!;
        var sources = diagnostics.Providers.Where(item => item.Stage == ExternalLyricsQueryStage.Fallback).ToArray();

        Assert.Equal(ExternalLyricsQueryState.Satisfied, diagnostics.FallbackState);
        Assert.Equal(1, diagnostics.FallbackAttempt);
        Assert.Equal(ExternalLyricsQueryOutcome.NoResult, sources[0].Outcome);
        Assert.Equal(ExternalLyricsDiagnosticReason.NoDocumentReturned, sources[0].Reason);
        Assert.Equal(ExternalLyricsQueryOutcome.Failed, sources[1].Outcome);
        Assert.Equal(nameof(HttpRequestException), sources[1].ExceptionType);
        Assert.Equal(ExternalLyricsFailureReason.HttpError, sources[1].FailureReason);
        Assert.Equal(403, sources[1].HttpStatusCode);
        Assert.DoesNotContain("secret", sources[1].Detail!);
        Assert.Equal(ExternalLyricsQueryOutcome.Success, sources[2].Outcome);
        Assert.Equal((1, 1, 1), (sources[2].LineCount, sources[2].WordLineCount, sources[2].TranslatedLineCount));
        Assert.Equal(ExternalLyricsQueryOutcome.Skipped, sources[3].Outcome);
        Assert.Equal(0, skipped.CallCount);
        Assert.Same(diagnostics, runtime.GetFrameSnapshot().ExternalDiagnostics);
    }

    [Fact]
    public async Task SnapshotAsync_ReportsTranslationRejectedToRetainWordsWhileAnotherSourceIsPending()
    {
        var local = RichDocument("AP_words", words: true);
        var translated = RichDocument("NE_translation", translation: true);
        var translationSource = new DelegateEnrichmentProvider("NetEase Cloud Music", (_, _) => Task.FromResult<LyricsDocument?>(translated));
        var completion = new TaskCompletionSource<LyricsDocument?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new DelegateEnrichmentProvider("Pending", (_, _) => completion.Task);
        await using var runtime = EnrichmentRuntime(local, [translationSource, pending], words: true, translation: true);

        await runtime.SnapshotAsync();
        await pending.Started.WaitAsync(TimeSpan.FromSeconds(2));
        var during = await runtime.SnapshotAsync();
        var duringSources = during.ExternalDiagnostics!.Providers.Where(item => item.Stage == ExternalLyricsQueryStage.Enrichment).ToArray();

        Assert.Same(local, during.Document);
        Assert.Equal(ExternalLyricsQueryState.Querying, during.ExternalDiagnostics.EnrichmentState);
        Assert.Equal(ExternalLyricsQueryOutcome.Rejected, duringSources[0].Outcome);
        Assert.Equal(ExternalLyricsDiagnosticReason.WouldLoseWordTimings, duringSources[0].Reason);
        Assert.Equal(1, duringSources[0].TranslatedLineCount);
        Assert.Equal(ExternalLyricsQueryOutcome.Querying, duringSources[1].Outcome);
        Assert.True(during.ExternalDiagnostics.MissingTranslations);
        Assert.False(during.ExternalDiagnostics.MissingWordTimings);
        completion.SetResult(null);

        var complete = await WaitForSnapshotAsync(runtime,
            snapshot => snapshot.ExternalDiagnostics?.EnrichmentState == ExternalLyricsQueryState.Completed);
        var completedSource = complete.ExternalDiagnostics!.Providers.Single(item =>
            item.Stage == ExternalLyricsQueryStage.Enrichment && item.Source == "Pending");
        Assert.Equal(ExternalLyricsQueryOutcome.NoResult, completedSource.Outcome);
        // Published snapshots are immutable even as the next poll adopts more evidence.
        Assert.Equal(ExternalLyricsQueryOutcome.Querying, duringSources[1].Outcome);
    }

    [Fact]
    public async Task SnapshotAsync_DiagnosticsExplainRetryWaitingAndFinalFailureWithoutLosingLocalLyrics()
    {
        var local = RichDocument("AP_local");
        var time = new ManualTimeProvider();
        var failing = new DelegateEnrichmentProvider("Timed out", (_, _) =>
            Task.FromException<LyricsDocument?>(new TimeoutException("private response data")));
        await using var runtime = new LyricsRuntimeService(
            new CandidateLyricsProvider([new LyricsMatch(local, 100, 0, HasContentMatch: true)]),
            new StubPlayerProvider(new PlayerState("Song", "Artist", "Album", 1, 180, true)),
            new LyricsSynchronizer(), timeProvider: time)
        {
            PreferTranslations = true,
            ExternalLyricsProviders = [failing],
            ExternalRetryBaseDelay = TimeSpan.FromSeconds(2),
        };

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var expectedState = attempt < 3 ? ExternalLyricsQueryState.RetryWaiting : ExternalLyricsQueryState.Completed;
            var snapshot = await WaitForSnapshotAsync(runtime, item =>
                item.ExternalDiagnostics is { } state && state.EnrichmentAttempt == attempt && state.EnrichmentState == expectedState);
            Assert.Same(local, snapshot.Document);
            var source = Assert.Single(snapshot.ExternalDiagnostics!.Providers,
                item => item.Stage == ExternalLyricsQueryStage.Enrichment);
            Assert.Equal(attempt, source.Attempt);
            Assert.Equal(ExternalLyricsQueryOutcome.Failed, source.Outcome);
            Assert.Equal(ExternalLyricsFailureReason.Timeout, source.FailureReason);
            Assert.Equal(nameof(TimeoutException), source.ExceptionType);
            Assert.DoesNotContain("private", source.Detail!);
            if (attempt < 3) time.Advance(TimeSpan.FromSeconds(10));
            else Assert.Contains("source requests failed", snapshot.Resolution.Summary);
        }
        Assert.Equal(3, failing.CallCount);
    }

    [Fact]
    public async Task DisposeAsync_CancelsIndependentEnrichment()
    {
        var pending = new TaskCompletionSource<LyricsDocument?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new DelegateEnrichmentProvider("Platform", (_, _) => pending.Task);
        var runtime = EnrichmentRuntime(RichDocument("AP_local"), [provider], words: true);

        await runtime.SnapshotAsync();
        await provider.Started.WaitAsync(TimeSpan.FromSeconds(2));
        await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await provider.FirstCanceled.WaitAsync(TimeSpan.FromSeconds(2));
        pending.SetResult(null);
    }

    private static LyricsRuntimeService EnrichmentRuntime(LyricsDocument local,
        IReadOnlyList<IExternalLyricsProvider> providers, bool words = false, bool translation = false,
        ILyricsDocumentProvider? lyricsProvider = null, IPlayerSessionProvider? playerProvider = null) => new(
            lyricsProvider ?? new CandidateLyricsProvider([new LyricsMatch(local, 100, 0, HasContentMatch: true)]),
            playerProvider ?? new StubPlayerProvider(new PlayerState("Song", "Artist", "Album", 1, 180, true)),
            new LyricsSynchronizer(), new PlaybackClock())
        {
            ExternalLyricsProviders = providers,
            PreferWordTimings = words,
            PreferTranslations = translation,
            ExternalRetryBaseDelay = TimeSpan.Zero,
        };

    private static LyricsDocument RichDocument(string id, bool words = false, bool translation = false) =>
        BuildDocument(id, 180, "hello world") with
        {
            Lines = [new LyricsLine(0, 5, "hello world",
                Words: words ? [new LyricWord(0, 2, "hello "), new LyricWord(2, 5, "world")] : null,
                Translation: translation ? "你好，世界" : null)],
        };

    private static async Task<RuntimeSnapshot> WaitForSnapshotAsync(LyricsRuntimeService runtime, Func<RuntimeSnapshot, bool> predicate)
    {
        for (var poll = 0; poll < 150; poll++)
        {
            var snapshot = await runtime.SnapshotAsync();
            if (predicate(snapshot)) return snapshot;
            await Task.Delay(10);
        }
        throw new TimeoutException("The expected background lyric result was not adopted.");
    }

    private sealed class DelegateEnrichmentProvider(string name,
        Func<PlayerState, CancellationToken, Task<LyricsDocument?>> fetch) : IExternalLyricsProvider
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstCanceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public string Name => name;
        public int CallCount => Volatile.Read(ref _calls);
        public Task Started => _started.Task;
        public Task FirstCanceled => _firstCanceled.Task;

        public async Task<LyricsDocument?> FetchAsync(PlayerState player, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            _started.TrySetResult();
            using var registration = cancellationToken.Register(() => _firstCanceled.TrySetResult());
            return await fetch(player, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class MutableEnrichmentPlayer(PlayerState state) : IPlayerSessionProvider
    {
        public PlayerState State { get; set; } = state;
        public Task<PlayerState?> GetCurrentPlayerStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<PlayerState?>(State);
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

        public int ScanCount { get; private set; }

        public Task<LyricsDocument?> GetLatestLyricsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Candidates.Count > 0 ? Candidates[0].Document : null);
        }

        public Task<IReadOnlyList<LyricsMatch>> FindCandidatesAsync(
            PlayerState player,
            CancellationToken cancellationToken = default)
        {
            ScanCount++;
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

    private sealed class PendingCatalogResolver : ICatalogSongResolver
    {
        private readonly TaskCompletionSource<IReadOnlyList<string>> _result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<string>> ResolveLyricsIdCandidatesAsync(
            PlayerState player,
            CancellationToken cancellationToken = default)
        {
            return _result.Task.WaitAsync(cancellationToken);
        }

        public void Complete(IReadOnlyList<string> lyricsIds)
        {
            _result.TrySetResult(lyricsIds);
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
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

        public override long TimestampFrequency => 1_000_000;

        public override long GetTimestamp() => _timestamp;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount)
        {
            _timestamp += (long)(amount.TotalSeconds * TimestampFrequency);
            _utcNow += amount;
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
