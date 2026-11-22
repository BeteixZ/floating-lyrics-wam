using AppleMusicLyrics.App.Presentation;
using AppleMusicLyrics.App.Localization;
using AppleMusicLyrics.Core.Models;
using Xunit;

namespace AppleMusicLyrics.Tests.Display;

public sealed class OverlaySnapshotPresenterTests
{
    private readonly LocalizationManager _english = new(preference: "en-US");

    [Fact]
    public void CreateLyrics_PreludeUsesTheDisplayedNextLineForWordsAndTranslation()
    {
        var line = new LyricsLine(10, 12, "original", Words: [new LyricWord(10, 12, "original")], Translation: "译文");
        var document = new LyricsDocument("id", "success", "test", DateTimeOffset.UtcNow, [line]);
        var snapshot = new RuntimeSnapshot(document, new PlayerState("Song", "Artist", null, 0, 30, true),
            new ActiveLyricState(null, null, null, line), LyricsResolution.NoPlayer);
        var presentation = OverlaySnapshotPresenter.CreateLyrics(snapshot, _english);
        Assert.Equal("original", presentation.CurrentText);
        Assert.Empty(presentation.NextText);
        Assert.Same(line, presentation.DisplayedLine);
        Assert.Equal("译文", presentation.TranslationText);
        Assert.False(presentation.Animate);
    }

    [Fact]
    public void CreatePlayer_NoPlayer_ReturnsWaitingPresentation()
    {
        var snapshot = new RuntimeSnapshot(
            null,
            null,
            new ActiveLyricState(null, null, null, null),
            LyricsResolution.NoPlayer);

        var presentation = OverlaySnapshotPresenter.CreatePlayer(snapshot, _english);

        Assert.Equal("Apple Music Lyrics", presentation.WindowTitle);
        Assert.Contains("Waiting", presentation.Subtitle);
    }

    [Theory]
    [InlineData("東京へ行った", null, true, "tōkyō e itta")]
    [InlineData("スーパーで待つ", null, true, "sūpā de matsu")]
    [InlineData("東京", null, true, "tōkyō")]
    [InlineData("東京へ行った", "supplied reading", true, "supplied reading")]
    [InlineData("東京へ行った", "supplied reading", false, null)]
    [InlineData("Hello world", null, true, null)]
    public void CreateLyrics_ShowsSuppliedOrOfflineRomajiOnlyWhenEnabled(
        string original, string? supplied, bool enabled, string? expected)
    {
        var current = new LyricsLine(1, 2, original, Translation: "译文", Romanization: supplied);
        var document = new LyricsDocument("id", "success", "netease:1", DateTimeOffset.UtcNow,
            [current, new LyricsLine(2, 3, "また会う"), new LyricsLine(3, 4, "歌を歌う")]);
        var snapshot = new RuntimeSnapshot(document, new PlayerState("Song", "Artist", null, 1.5, 30, true),
            new ActiveLyricState(0, null, current, null), LyricsResolution.NoPlayer);

        var presentation = OverlaySnapshotPresenter.CreateLyrics(snapshot, _english, showRomanization: enabled);

        Assert.Equal(expected, presentation.RomanizationText);
        Assert.Equal(original, presentation.CurrentText);
        Assert.Equal("译文", presentation.TranslationText);
    }

    [Fact]
    public void CreateLyrics_DoesNotGenerateJapaneseReadingsForChineseLyrics()
    {
        var current = new LyricsLine(1, 2, "明天見");
        var document = new LyricsDocument("id", "success", "test", DateTimeOffset.UtcNow, [current]);
        var snapshot = new RuntimeSnapshot(document, new PlayerState("Song", "Artist", null, 1.5, 30, true),
            new ActiveLyricState(0, null, current, null), LyricsResolution.NoPlayer);

        Assert.Null(OverlaySnapshotPresenter.CreateLyrics(snapshot, _english, showRomanization: true).RomanizationText);
    }

    [Fact]
    public void HasDisplayableLyrics_IsFalseForAnInstrumentalsEmptyLyricsFile()
    {
        // Instrumentals can resolve to a lyrics file without text. With auto-hide on, the overlay
        // must hide as for a song without lyrics instead of saying the file has nothing to show.
        RuntimeSnapshot Snapshot(params LyricsLine[] lines) => new(
            new LyricsDocument("id", "ok", "lyrics.ttml", DateTimeOffset.UtcNow, lines, 200),
            new PlayerState("Interlude", "Artist", "Album", 10, 200, true),
            new ActiveLyricState(-1, null, null, lines.FirstOrDefault()),
            new LyricsResolution(LyricsResolutionStatus.Resolved, LyricsResolutionConfidence.High,
                LyricsResolutionSource.AppleMusicCache, "resolved"));

        Assert.False(OverlaySnapshotPresenter.HasDisplayableLyrics(Snapshot()));
        Assert.False(OverlaySnapshotPresenter.HasDisplayableLyrics(Snapshot(new LyricsLine(0, 200, "  "))));
        Assert.True(OverlaySnapshotPresenter.HasDisplayableLyrics(Snapshot(new LyricsLine(0, 2, " "), new LyricsLine(2, 4, "Hi"))));
        Assert.False(OverlaySnapshotPresenter.HasDisplayableLyrics(Snapshot() with { Document = null }));
        // With auto-hide off (and in the tray tooltip) an instrumental shows nothing, not a message.
        Assert.Equal(string.Empty, OverlaySnapshotPresenter.CreateLyrics(Snapshot(), _english).CurrentText);
    }

    [Fact]
    public void CreateLyrics_ResolvedDocument_MapsActiveLines()
    {
        var previous = new LyricsLine(0, 1, "previous");
        var current = new LyricsLine(1, 2, "current");
        var next = new LyricsLine(2, 3, "next");
        var document = new LyricsDocument(
            "id", "ok", "lyrics.json", DateTimeOffset.UtcNow, [previous, current, next], 3);
        var player = new PlayerState("Song", "Artist", "Album", 1.5, 3, true);
        var snapshot = new RuntimeSnapshot(
            document,
            player,
            new ActiveLyricState(1, previous, current, next),
            new LyricsResolution(
                LyricsResolutionStatus.Resolved,
                LyricsResolutionConfidence.High,
                LyricsResolutionSource.AppleMusicCache,
                "resolved"));

        var presentation = OverlaySnapshotPresenter.CreateLyrics(snapshot, _english);

        Assert.Equal("previous", presentation.PreviousText);
        Assert.Equal("current", presentation.CurrentText);
        Assert.Equal("next", presentation.NextText);
        Assert.True(presentation.Animate);
        Assert.Equal("Apple cache | line timing", presentation.StatusText);
        Assert.Contains("3 lines", presentation.PathText);
        Assert.Contains("word timing 0/3 lines", presentation.PathText);
        Assert.Contains("translations 0/3 lines", presentation.PathText);
    }

    [Fact]
    public void CreateLyrics_ReportsActualSourceAndPartialWordAndTranslationCoverage()
    {
        var lines = new LyricsLine[]
        {
            new(0, 1, "first", Words: [new LyricWord(0, 1, "first")]),
            new(1, 2, "second", Translation: "译文"),
            new(2, 3, "third"),
        };
        var document = new LyricsDocument("id", "success", "qqmusic:42", DateTimeOffset.UtcNow, lines);
        // Source metadata identifies a replacement document even if the resolution still carries
        // the previous Apple-cache decision while the next runtime snapshot is being published.
        var snapshot = new RuntimeSnapshot(document, new PlayerState("Song", "Artist", null, 0, 3, true),
            new ActiveLyricState(0, null, lines[0], lines[1]),
            new LyricsResolution(LyricsResolutionStatus.Resolved, LyricsResolutionConfidence.High,
                LyricsResolutionSource.AppleMusicCache, "matched"));

        var presentation = OverlaySnapshotPresenter.CreateLyrics(snapshot, _english);

        Assert.Equal("QQ Music | word timing + translation", presentation.StatusText);
        Assert.Contains("word timing 1/3 lines", presentation.PathText);
        Assert.Contains("translations 1/3 lines", presentation.PathText);
        Assert.Contains("qqmusic:42", presentation.PathText);
    }

    [Theory]
    [InlineData("qqmusic:1", LyricsResolutionSource.ExternalProvider, "QQ Music | line timing")]
    [InlineData("netease:1", LyricsResolutionSource.ExternalProvider, "NetEase | line timing")]
    [InlineData("lrclib:1", LyricsResolutionSource.ExternalProvider, "LRCLIB | line timing")]
    [InlineData("lrclib-plain:1", LyricsResolutionSource.ExternalProvider, "LRCLIB | plain lyrics")]
    [InlineData("cache.json", LyricsResolutionSource.CatalogVerifiedCache, "Apple cache | line timing")]
    [InlineData("other:1", LyricsResolutionSource.ExternalProvider, "External | line timing")]
    public void CreateLyrics_IdentifiesLineOnlySourcesWithoutClaimingRichData(
        string sourceFile, LyricsResolutionSource source, string expectedStatus)
    {
        var line = new LyricsLine(0, 1, "original");
        var document = new LyricsDocument("id", "success", sourceFile, DateTimeOffset.UtcNow, [line]);
        var snapshot = new RuntimeSnapshot(document, new PlayerState("Song", "Artist", null, 0, 1, true),
            new ActiveLyricState(0, null, line, null),
            new LyricsResolution(LyricsResolutionStatus.Resolved, LyricsResolutionConfidence.Medium, source, "matched"));

        var presentation = OverlaySnapshotPresenter.CreateLyrics(snapshot, _english);

        Assert.Equal(expectedStatus, presentation.StatusText);
        Assert.Contains("word timing 0/1 lines", presentation.PathText);
        Assert.Contains("translations 0/1 lines", presentation.PathText);
    }

    [Fact]
    public void LanguageChange_LocalizesWaitingStatusWhilePreservingActualLyrics()
    {
        var chinese = new LocalizationManager(preference: "zh-CN");
        var waiting = new RuntimeSnapshot(null, null, new ActiveLyricState(null, null, null, null), LyricsResolution.NoPlayer);
        var englishPlaceholder = OverlaySnapshotPresenter.CreateLyrics(waiting, _english);
        var chinesePlaceholder = OverlaySnapshotPresenter.CreateLyrics(waiting, chinese);
        Assert.NotEqual(englishPlaceholder.CurrentText, chinesePlaceholder.CurrentText);
        Assert.Equal(chinese.Text("Lyrics.PlaceholderNoPlayer"), chinesePlaceholder.CurrentText);

        var line = new LyricsLine(0, 10, "Actual source lyric", Translation: "实际译文");
        var document = new LyricsDocument("42", "success", "qqmusic:42", DateTimeOffset.UtcNow, [line]);
        var snapshot = waiting with { Document = document, ActiveLyric = new ActiveLyricState(0, null, line, null) };
        var english = OverlaySnapshotPresenter.CreateLyrics(snapshot, _english);
        var translated = OverlaySnapshotPresenter.CreateLyrics(snapshot, chinese);
        Assert.NotEqual(english.StatusText, translated.StatusText);
        Assert.Equal(english.CurrentText, translated.CurrentText);
        Assert.Equal(english.TranslationText, translated.TranslationText);
        Assert.Same(line, translated.DisplayedLine);
    }

    [Fact]
    public void CreateLyrics_ReportsFailedQueriesWithoutClaimingThatTheSongHasNoTranslation()
    {
        var snapshot = BuildDiagnosticSnapshot(new ExternalLyricsDiagnostics(
            [new("QQ Music", ExternalLyricsQueryStage.Enrichment, ExternalLyricsQueryOutcome.Failed, Attempt: 3,
                ExceptionType: "HttpRequestException", FailureReason: ExternalLyricsFailureReason.HttpError, HttpStatusCode: 403),
             new("NetEase", ExternalLyricsQueryStage.Enrichment, ExternalLyricsQueryOutcome.NoResult,
                Reason: ExternalLyricsDiagnosticReason.NoDocumentReturned)],
            ExternalLyricsQueryState.Completed, ExternalLyricsQueryState.Completed, 1, 3, true, true));

        var result = OverlaySnapshotPresenter.CreateLyrics(snapshot, _english);

        Assert.Contains(_english.Text("Debug.TranslationQueryFailed"), result.PathText);
        Assert.Contains("QQ Music / feature lookup: request failed", result.PathText);
        Assert.Contains("HTTP 403", result.PathText);
        Assert.Contains("HttpRequestException", result.PathText);
        Assert.Contains("NetEase / feature lookup: no document returned", result.PathText);
        Assert.Contains("does not prove that the song has no translation", result.PathText);
        Assert.Contains(Environment.NewLine, result.PathText);
        Assert.DoesNotContain(Environment.NewLine, result.DebugSummaryText);
        Assert.DoesNotContain("HTTP", result.DebugSummaryText);
        Assert.DoesNotContain("HttpRequestException", result.DebugSummaryText);
        Assert.True(result.DebugSummaryText.Length < 100);
        Assert.Contains("LRCLIB", result.DebugSummaryText);
        Assert.Contains("translations 0/1", result.DebugSummaryText);
    }

    [Fact]
    public void CreateLyrics_ExplainsRejectedTranslationsAndRetainsPendingSourceStatus()
    {
        var snapshot = BuildDiagnosticSnapshot(new ExternalLyricsDiagnostics(
            [new("QQ Music", ExternalLyricsQueryStage.Enrichment, ExternalLyricsQueryOutcome.Rejected, Attempt: 1,
                LineCount: 50, TranslatedLineCount: 48, Reason: ExternalLyricsDiagnosticReason.WouldLoseWordTimings),
             new("NetEase", ExternalLyricsQueryStage.Enrichment, ExternalLyricsQueryOutcome.Querying, Attempt: 1)],
            ExternalLyricsQueryState.Completed, ExternalLyricsQueryState.Querying, 1, 1, false, true));

        var result = OverlaySnapshotPresenter.CreateLyrics(snapshot, _english);

        Assert.Contains(_english.Text("Debug.TranslationSearching"), result.PathText);
        Assert.Contains(_english.Text("Debug.CompactTranslationSearching"), result.DebugSummaryText);
        Assert.Contains("50 lines, words 0, translations 48", result.PathText);
        Assert.Contains("would lose the current word timing", result.PathText);
        Assert.Contains("NetEase / feature lookup: querying", result.PathText);
        var completed = snapshot with
        {
            ExternalDiagnostics = snapshot.ExternalDiagnostics! with { EnrichmentState = ExternalLyricsQueryState.Completed }
        };
        Assert.Contains(_english.Text("Debug.TranslationRejectedWords"),
            OverlaySnapshotPresenter.CreateLyrics(completed, _english).PathText);
    }

    [Theory]
    [InlineData(ExternalLyricsQueryState.Querying, "Debug.TranslationSearching")]
    [InlineData(ExternalLyricsQueryState.RetryWaiting, "Debug.TranslationRetryWaiting")]
    [InlineData(ExternalLyricsQueryState.Completed, "Debug.TranslationNotReturned")]
    public void CreateLyrics_ExplainsTheTranslationLookupState(ExternalLyricsQueryState state, string expectedKey)
    {
        var snapshot = BuildDiagnosticSnapshot(new ExternalLyricsDiagnostics(
            [new("QQ Music", ExternalLyricsQueryStage.Enrichment, ExternalLyricsQueryOutcome.NoResult,
                Reason: ExternalLyricsDiagnosticReason.NoDocumentReturned)],
            ExternalLyricsQueryState.Completed, state, 1, 1, true, true));
        Assert.Contains(_english.Text(expectedKey), OverlaySnapshotPresenter.CreateLyrics(snapshot, _english).PathText);
    }

    [Fact]
    public void CreateLyrics_DistinguishesHiddenTranslationFromUntranslatedCurrentLine()
    {
        var snapshot = BuildDiagnosticSnapshot(null);
        var translatedLine = new LyricsLine(3, 5, "Second source line", Translation: "第二行译文");
        snapshot = snapshot with { Document = snapshot.Document! with { Lines = [snapshot.Document!.Lines[0], translatedLine] } };

        Assert.Contains(_english.Text("Debug.TranslationMissingCurrentLine"),
            OverlaySnapshotPresenter.CreateLyrics(snapshot, _english).PathText);
        Assert.Contains(_english.Text("Debug.TranslationDisabled"),
            OverlaySnapshotPresenter.CreateLyrics(snapshot, _english, showTranslation: false).PathText);
        Assert.Contains(_english.Text("Debug.CompactTranslationHidden"),
            OverlaySnapshotPresenter.CreateLyrics(snapshot, _english, showTranslation: false).DebugSummaryText);

        snapshot = snapshot with { ActiveLyric = new ActiveLyricState(1, snapshot.Document.Lines[0], translatedLine, null) };
        var result = OverlaySnapshotPresenter.CreateLyrics(snapshot, _english);
        Assert.Contains(_english.Text("Debug.TranslationVisible"), result.PathText);
        Assert.Equal("第二行译文", result.TranslationText);
    }

    [Fact]
    public void CreateLyrics_PreludeDoesNotRepeatTheFirstLineWhenLaterLinesExist()
    {
        var first = new LyricsLine(10, 12, "first");
        var second = new LyricsLine(15, 18, "second");
        var snapshot = BuildDiagnosticSnapshot(null) with
        {
            Document = new LyricsDocument("intro", "ok", "test", DateTimeOffset.UtcNow, [first, second]),
            ActiveLyric = new ActiveLyricState(null, null, null, first),
        };

        var result = OverlaySnapshotPresenter.CreateLyrics(snapshot, _english);

        Assert.Equal("first", result.CurrentText);
        Assert.Empty(result.PreviousText);
        Assert.Empty(result.NextText);
        Assert.False(result.Animate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateLyrics_CompactModeSkipsDetailedProviderEnumeration(bool withoutDocument)
    {
        var providers = new TrackingReadOnlyList<ExternalLyricsDiagnostic>(
            [new("QQ Music", ExternalLyricsQueryStage.Enrichment, ExternalLyricsQueryOutcome.Failed,
                ExceptionType: "HttpRequestException", HttpStatusCode: 503)]);
        var diagnostics = new ExternalLyricsDiagnostics(providers, ExternalLyricsQueryState.Completed,
            ExternalLyricsQueryState.Completed, 1, 1, true, true);
        var snapshot = BuildDiagnosticSnapshot(diagnostics);
        if (withoutDocument) snapshot = snapshot with { Document = null };

        var compact = OverlaySnapshotPresenter.CreateLyrics(snapshot, _english, includeDetailedDiagnostics: false);

        Assert.Empty(compact.PathText);
        Assert.NotEmpty(compact.DebugSummaryText);
        Assert.Equal(0, providers.EnumerationCount);
        // The optional flag defaults to the old full-report behavior for diagnostic/API callers.
        var detailed = OverlaySnapshotPresenter.CreateLyrics(snapshot, _english);
        Assert.NotEmpty(detailed.PathText);
        Assert.Contains("HTTP 503", detailed.PathText);
        Assert.True(providers.EnumerationCount > 0);
        Assert.Equal(detailed.CurrentText, compact.CurrentText);
        Assert.Equal(detailed.StatusText, compact.StatusText);
        Assert.Equal(detailed.DebugSummaryText, compact.DebugSummaryText);
    }

    [Fact]
    public void CreateLyrics_CachesDocumentStatisticsByReferenceWithoutCachingLocalizedText()
    {
        var first = new LyricsLine(0, 2, "original", Words: [new LyricWord(0, 2, "original")], Translation: "译文");
        // A duplicate original is not a meaningful translation.
        var second = new LyricsLine(2, 4, "same", Translation: " SAME! ");
        var lines = new TrackingReadOnlyList<LyricsLine>([first, second]);
        var snapshot = BuildDiagnosticSnapshot(null) with
        {
            Document = new LyricsDocument("cached", "ok", "test", DateTimeOffset.UtcNow, lines),
            ActiveLyric = new ActiveLyricState(0, null, first, second),
        };

        var english = OverlaySnapshotPresenter.CreateLyrics(snapshot, _english, includeDetailedDiagnostics: false);
        var chinese = OverlaySnapshotPresenter.CreateLyrics(snapshot, new LocalizationManager(preference: "zh-CN"),
            showTranslation: false, includeDetailedDiagnostics: false);

        Assert.Equal(1, lines.EnumerationCount);
        Assert.Contains("words 1/2", english.DebugSummaryText);
        Assert.Contains("translations 1/2", english.DebugSummaryText);
        Assert.NotEqual(english.StatusText, chinese.StatusText);
        Assert.NotEqual(english.DebugSummaryText, chinese.DebugSummaryText);
        // A new document record is a new statistics snapshot, even if all of its values are equal.
        var replacement = snapshot with { Document = snapshot.Document! with { } };
        OverlaySnapshotPresenter.CreateLyrics(replacement, _english, includeDetailedDiagnostics: false);
        Assert.Equal(2, lines.EnumerationCount);
    }

    private sealed class TrackingReadOnlyList<T>(IReadOnlyList<T> items) : IReadOnlyList<T>
    {
        public int EnumerationCount { get; private set; }
        public int Count => items.Count;
        public T this[int index] => items[index];
        public IEnumerator<T> GetEnumerator()
        {
            EnumerationCount++;
            return items.GetEnumerator();
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static RuntimeSnapshot BuildDiagnosticSnapshot(ExternalLyricsDiagnostics? diagnostics)
    {
        var line = new LyricsLine(0, 2, "Synthetic lyric");
        return new RuntimeSnapshot(new LyricsDocument("id", "ok", "lrclib:42", DateTimeOffset.UtcNow, [line]),
            new PlayerState("Synthetic song", "Synthetic artist", null, 1, 120, false),
            new ActiveLyricState(0, null, line, null),
            new LyricsResolution(LyricsResolutionStatus.Resolved, LyricsResolutionConfidence.Medium,
                LyricsResolutionSource.ExternalProvider, "Synthetic match evidence"),
            ExternalDiagnostics: diagnostics);
    }
}
