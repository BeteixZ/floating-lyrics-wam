using AppleMusicLyrics.Application.Services;
using AppleMusicLyrics.Core.Abstractions;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Sync;
using Xunit;

namespace AppleMusicLyrics.Tests.Services;

public sealed class NativeLyricsRuntimePriorityTests
{
    [Fact]
    public async Task CompleteNativeWordsAndTranslation_ArePrimaryAndDoNotQueryExternalSources()
    {
        var native = Document("AP_123", words: true, translation: "hello world");
        var cache = new Cache(native);
        var external = new External("QQ Music", (_, _) => Task.FromResult<LyricsDocument?>(Document("QQ", true, "其他翻译")));
        await using var runtime = Create(cache, [external]);

        var snapshot = await runtime.SnapshotAsync();

        Assert.Same(native, snapshot.Document);
        Assert.Equal(LyricsResolutionSource.AppleMusicCache, snapshot.Resolution.Source);
        Assert.Equal(0, external.Calls);
        Assert.False(snapshot.ExternalDiagnostics!.MissingTranslations);
        Assert.False(snapshot.ExternalDiagnostics.MissingWordTimings);
    }

    [Theory]
    [InlineData("another lyric", 0)]
    [InlineData("hello world", 2)]
    public async Task UnalignedRichExternalResult_DoesNotReplaceNativeTextOrTimeline(string text, double shift)
    {
        var native = Document("AP_123");
        var externalDoc = Document("QQ", true, "外部翻译") with
        {
            Lines = [new LyricsLine(shift, 4 + shift, text, Words: [new(shift, 4 + shift, text)], Translation: "外部翻译")],
        };
        var external = new External("QQ Music", (_, _) => Task.FromResult<LyricsDocument?>(externalDoc));
        await using var runtime = Create(new Cache(native), [external]);

        var snapshot = await Wait(runtime, state => state.ExternalDiagnostics?.EnrichmentState == ExternalLyricsQueryState.Completed);

        Assert.Same(native, snapshot.Document);
        Assert.Equal(LyricsResolutionSource.AppleMusicCache, snapshot.Resolution.Source);
        Assert.Single(snapshot.ExternalDiagnostics!.Providers, item => item.Stage == ExternalLyricsQueryStage.Enrichment
            && item.Outcome == ExternalLyricsQueryOutcome.Rejected);
    }

    [Fact]
    public async Task LateCompleteNative_TakesPriorityOverAlreadyCompletedExternalFallback()
    {
        var cache = new Cache(null);
        var fallback = Document("QQ", true, "外部翻译");
        var external = new External("QQ Music", (_, _) => Task.FromResult<LyricsDocument?>(fallback));
        await using var runtime = Create(cache, [external]);
        await Wait(runtime, state => ReferenceEquals(state.Document, fallback));
        var native = Document("AP_123", true, "原生翻译") with { NativeOffsetSeconds = .35, SourceFile = "syllable-lyrics[1].json" };
        cache.Candidate = native;

        var snapshot = await runtime.SnapshotAsync();

        Assert.Same(native, snapshot.Document);
        Assert.Same(native, runtime.GetFrameSnapshot().Document);
        Assert.Equal(LyricsResolutionSource.AppleMusicCache, snapshot.Resolution.Source);
        Assert.Equal(1, external.Calls);
    }

    [Fact]
    public async Task SameIdNativeUpgrade_RebuildsScopeAndCannotBePressedDownByOldEnrichedSnapshot()
    {
        var cache = new Cache(Document("AP_123"));
        var external = new External("QQ Music", (_, _) => Task.FromResult<LyricsDocument?>(Document("QQ", true, "外部翻译")));
        await using var runtime = Create(cache, [external]);
        await Wait(runtime, state => state.Document?.Lines[0].Translation is not null);
        var nativeWords = new LyricWord[] { new(.2, 1.7, "hello"), new(1.7, 3.9, "world") };
        var upgradedNative = Document("AP_123") with
        {
            SourceFile = "syllable-lyrics[1].json",
            Lines = [new LyricsLine(0, 4, "hello world", Words: nativeWords)],
        };
        cache.Candidate = upgradedNative;

        var first = await runtime.SnapshotAsync();
        Assert.Same(upgradedNative, first.Document);
        Assert.Same(nativeWords, first.Document!.Lines[0].Words);
        var complete = await Wait(runtime, state => state.Document?.Lines[0].Translation is not null);
        Assert.Same(nativeWords, complete.Document!.Lines[0].Words);
        Assert.Equal("syllable-lyrics[1].json", complete.Document.SourceFile);
        Assert.Equal(LyricsResolutionSource.AppleMusicCache, complete.Resolution.Source);
        Assert.Equal(2, external.Calls);
    }

    [Fact]
    public async Task RichNativeRefreshAfterMatchWindow_CancelsOldEnrichmentAndRejectsItsLateResult()
    {
        var time = new ManualTime();
        var cache = new Cache(Document("AP_123"));
        var completion = new TaskCompletionSource<LyricsDocument?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var external = new External("QQ Music", (_, _) => completion.Task);
        await using var runtime = Create(cache, [external], time);
        await runtime.SnapshotAsync();
        await external.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var token = external.LastToken;
        var native = Document("AP_123", true, "原生翻译") with { SourceFile = "syllable-lyrics[1].json", NativeOffsetSeconds = .4 };
        cache.ById = native;
        time.Advance(6);

        var upgraded = await runtime.SnapshotAsync();

        Assert.Same(native, upgraded.Document);
        Assert.Equal(LyricsResolutionSource.AppleMusicCache, upgraded.Resolution.Source);
        Assert.True(token.IsCancellationRequested);
        Assert.Equal(1, cache.CandidateCalls);
        Assert.Equal(new[] { "AP_123" }, Assert.Single(cache.RequestedIds));
        completion.SetResult(Document("QQ_old", true, "不应采用的迟到翻译"));
        for (var poll = 0; poll < 5; poll++)
        {
            Assert.Same(native, (await runtime.SnapshotAsync()).Document);
            await Task.Yield();
        }
        Assert.Equal(1, external.Calls);
    }

    [Fact]
    public async Task SameIdRefresh_AdoptsMoreGranularNativeWordsWithoutNeedingMoreLines()
    {
        var time = new ManualTime();
        var native = Document("AP_123", words: true);
        var rich = Document("AP_123") with
        {
            Lines = [new LyricsLine(0, 4, "hello world", Words: [new(0, .3, "he"), new(.3, 1, "llo"), new(1, 4, "world")])],
        };
        var cache = new Cache(native) { ById = rich };
        await using var runtime = Create(cache, [], time);
        await runtime.SnapshotAsync();
        time.Advance(6);
        Assert.Same(rich, (await runtime.SnapshotAsync()).Document);
    }

    [Fact]
    public async Task SameIdRefresh_AdoptsBackgroundVocalsAndRetainsThemAcrossRefreshes()
    {
        var time = new ManualTime();
        var native = Document("AP_123", words: true);
        var rich = native with
        {
            Lines = [native.Lines[0] with { BackgroundVocals = [new LyricsLine(1, 3, "echo", Words: [new(1, 3, "echo")])] }],
        };
        var cache = new Cache(native) { ById = rich };
        await using var runtime = Create(cache, [], time);
        Assert.Same(native, (await runtime.SnapshotAsync()).Document);

        time.Advance(6);
        Assert.Same(rich, (await runtime.SnapshotAsync()).Document);

        cache.ById = Document("AP_123", words: true, translation: "新翻译");
        time.Advance(2);
        Assert.Same(rich, (await runtime.SnapshotAsync()).Document);
    }

    [Fact]
    public async Task SameIdRefresh_PrefersNativeVersionsThatIdentifyBothDuetVocalists()
    {
        var time = new ManualTime();
        var first = new LyricsLine(10, 14, "lead");
        var second = new LyricsLine(11, 15, "reply");
        var native = Document("AP_123") with { Lines = [first, second] };
        var duet = native with { Lines = [first with { VocalistId = "v1" }, second with { VocalistId = "v2" }] };
        var cache = new Cache(native) { ById = duet };
        await using var runtime = Create(cache, [], time);
        Assert.Same(native, (await runtime.SnapshotAsync()).Document);

        time.Advance(6);
        Assert.Same(duet, (await runtime.SnapshotAsync()).Document);

        cache.ById = native with { Lines = [first with { Translation = "第一句" }, second] };
        time.Advance(2);
        Assert.Same(duet, (await runtime.SnapshotAsync()).Document);
    }

    [Fact]
    public async Task SameIdRefresh_IsBoundedAndNeverReopensBroadCandidateScan()
    {
        var time = new ManualTime();
        var native = Document("AP_123");
        var cache = new Cache(native);
        await using var runtime = Create(cache, [], time);
        await runtime.SnapshotAsync();
        time.Advance(6);
        await runtime.SnapshotAsync();
        for (var poll = 0; poll < 20; poll++) Assert.Same(native, (await runtime.SnapshotAsync()).Document);
        Assert.Single(cache.RequestedIds);
        time.Advance(1.99);
        await runtime.SnapshotAsync();
        Assert.Single(cache.RequestedIds);
        time.Advance(.01);
        await runtime.SnapshotAsync();
        Assert.Equal(2, cache.RequestedIds.Count);
        Assert.Equal(1, cache.CandidateCalls);
    }

    [Theory]
    [InlineData("AP_456", 180, true, true)]
    [InlineData("AP_123", 240, true, true)]
    [InlineData("AP_123", 180, false, true)]
    public async Task SameIdRefresh_RejectsPrefetchChangedDurationAndNativeFeatureLoss(
        string id, double duration, bool words, bool translation)
    {
        var time = new ManualTime();
        var native = Document("AP_123", words: true);
        var cache = new Cache(native) { ById = Document(id, words, translation ? "新翻译" : null) with { DurationSeconds = duration } };
        await using var runtime = Create(cache, [], time);
        await runtime.SnapshotAsync();
        time.Advance(6);

        Assert.Same(native, (await runtime.SnapshotAsync()).Document);
        Assert.Equal(1, cache.CandidateCalls);
    }

    [Fact]
    public async Task VerifiedShortNative_CanRefreshAtTheSameDeclaredBodyDuration()
    {
        var time = new ManualTime();
        var native = Document("AP_123") with { DurationSeconds = 130 };
        var cache = new Cache(native) { ById = native };
        var player = new Player();
        await using var runtime = new LyricsRuntimeService(cache, player, new LyricsSynchronizer(),
            new PlaybackClock(time), catalogResolver: new Catalog(), timeProvider: time);
        await Wait(runtime, state => ReferenceEquals(state.Document, native));
        var rich = Document("AP_123", true, "原生翻译") with { DurationSeconds = 130 };
        cache.ById = rich;
        time.Advance(6);
        var refreshed = await runtime.SnapshotAsync();
        Assert.Same(rich, refreshed.Document);
    }

    [Fact]
    public async Task WithoutNative_TranslationOnlySourceStillCannotDiscardExistingExternalWords()
    {
        var words = Document("LRCLIB_words", true);
        var baseline = new External("LRCLIB", (_, _) => Task.FromResult<LyricsDocument?>(words));
        var translation = new External("QQ Music", (_, _) => Task.FromResult<LyricsDocument?>(Document("QQ_translation", translation: "翻译")));
        await using var runtime = Create(new Cache(null), [baseline, translation]);

        var completed = await Wait(runtime, state => state.ExternalDiagnostics?.EnrichmentState == ExternalLyricsQueryState.Completed);

        Assert.Same(words, completed.Document);
        Assert.Equal(LyricsResolutionSource.ExternalProvider, completed.Resolution.Source);
        var rejected = Assert.Single(completed.ExternalDiagnostics!.Providers, item => item.Stage == ExternalLyricsQueryStage.Enrichment
            && item.Source == "QQ Music");
        Assert.Equal(ExternalLyricsDiagnosticReason.WouldLoseWordTimings, rejected.Reason);
        Assert.Equal(ExternalLyricsQueryOutcome.Rejected, rejected.Outcome);
    }

    [Fact]
    public async Task NativeWithoutRequestedFeatures_IsReplacedByAlignedQQMusicLyrics()
    {
        var native = Document("AP_123");
        var qq = Document("QQ", true, "你好世界");
        var external = new External("QQ Music", (_, _) => Task.FromResult<LyricsDocument?>(qq)) { OutranksPartialAppleLyrics = true };
        await using var runtime = Create(new Cache(native), [external]);

        var snapshot = await Wait(runtime, state => ReferenceEquals(state.Document, qq));

        Assert.Equal(LyricsResolutionSource.ExternalProvider, snapshot.Resolution.Source);
        Assert.Contains("QQ Music", snapshot.Resolution.Summary);
    }

    [Fact]
    public async Task NativeWithWordsOnly_IsReplacedByQQMusicWithWordsAndTranslation()
    {
        var native = Document("AP_123", words: true);
        var qq = Document("QQ", true, "你好世界");
        var external = new External("QQ Music", (_, _) => Task.FromResult<LyricsDocument?>(qq)) { OutranksPartialAppleLyrics = true };
        await using var runtime = Create(new Cache(native), [external]);

        await Wait(runtime, state => ReferenceEquals(state.Document, qq));
    }

    [Fact]
    public async Task NetEaseOutranksQQMusicWhenBothProvideEveryFeature()
    {
        var native = Document("AP_123");
        var qq = Document("QQ", true, "QQ 翻译");
        var netEase = Document("NE", true, "网易翻译");
        var qqProvider = new External("QQ Music", (_, _) => Task.FromResult<LyricsDocument?>(qq)) { OutranksPartialAppleLyrics = true };
        var netEaseProvider = new External("NetEase Cloud Music", (_, _) => Task.FromResult<LyricsDocument?>(netEase)) { OutranksPartialAppleLyrics = true };
        await using var runtime = Create(new Cache(native), [netEaseProvider, qqProvider]);

        var snapshot = await Wait(runtime, state => state.ExternalDiagnostics?.EnrichmentState == ExternalLyricsQueryState.Satisfied);

        Assert.Same(netEase, snapshot.Document);
        Assert.Equal(0, qqProvider.Calls);
    }

    [Fact]
    public async Task QQMusicWithEveryFeature_BeatsNetEaseWithTranslationOnly()
    {
        var native = Document("AP_123");
        var qq = Document("QQ", true, "QQ 翻译");
        var netEase = Document("NE", translation: "网易翻译");
        var qqProvider = new External("QQ Music", (_, _) => Task.FromResult<LyricsDocument?>(qq)) { OutranksPartialAppleLyrics = true };
        var netEaseProvider = new External("NetEase Cloud Music", (_, _) => Task.FromResult<LyricsDocument?>(netEase)) { OutranksPartialAppleLyrics = true };
        await using var runtime = Create(new Cache(native), [netEaseProvider, qqProvider]);

        await Wait(runtime, state => ReferenceEquals(state.Document, qq));
    }

    [Fact]
    public async Task LineTimedApple_IsReplacedByQQMusicThatSplitsAndRewordsLines()
    {
        // Apple: line timing only. QQ splits Apple's lines, words some differently, and adds a
        // scat section, so only a minority of lines pair exactly and a merge stays partial.
        var native = new LyricsDocument("AP_1", "success", "apple.json", DateTimeOffset.UnixEpoch,
        [
            new LyricsLine(10, 14, "alpha bravo charlie delta echo foxtrot"),
            new LyricsLine(14, 18, "golf hotel india juliet"),
            new LyricsLine(18, 22, "kilo lima mike november oscar papa"),
            new LyricsLine(22, 26, "quebec romeo sierra tango"),
            new LyricsLine(26, 30, "uniform victor whiskey xray yankee"),
        ], 180);
        var qq = new LyricsDocument("QQ_1", "success", "qq", DateTimeOffset.UnixEpoch,
        [
            Timed(0.2, 4, "la la la"),
            Timed(10.2, 12, "alpha bravo charlie", "甲"),
            Timed(12, 14, "delta echo foxtrot", "乙"),
            Timed(14.1, 18, "golf hotel juliet", "丙"),
            Timed(18.2, 20, "kilo lima mike", "丁"),
            Timed(20, 22, "november oscar papa", "戊"),
            Timed(22.1, 26, "quebec romeo sierra tango", "己"),
            Timed(26.3, 28, "uniform victor", "庚"),
            Timed(28, 30, "whiskey xray yankee", "辛"),
            Timed(31, 33, "la la la"),
        ], 180);
        var external = new External("QQ Music", (_, _) => Task.FromResult<LyricsDocument?>(qq)) { OutranksPartialAppleLyrics = true };
        await using var runtime = Create(new Cache(native), [external]);

        var snapshot = await Wait(runtime, state => state.Document?.LyricsId == "QQ_1");

        Assert.Equal(LyricsResolutionSource.ExternalProvider, snapshot.Resolution.Source);
        // QQ's own text and words, but every Apple line now starts exactly at Apple's onset.
        var lines = snapshot.Document!.Lines;
        double BeginOf(string text) => Math.Round(lines.Single(line => line.Text == text).Begin, 6);
        Assert.Equal(10.0, BeginOf("alpha bravo charlie"));
        Assert.Equal(14.0, BeginOf("golf hotel juliet"));
        Assert.Equal(18.0, BeginOf("kilo lima mike"));
        Assert.Equal(22.0, BeginOf("quebec romeo sierra tango"));
        Assert.Equal(26.0, BeginOf("uniform victor"));
        // Split second halves are corrected by interpolation, keeping the line order.
        Assert.InRange(BeginOf("delta echo foxtrot"), 11.5, 14.0);
        Assert.All(lines.Where(line => line.Words is { Count: > 0 }),
            line => Assert.Equal(Math.Round(line.Begin, 6), Math.Round(line.Words![0].Begin, 6)));
        Assert.Equal(lines.OrderBy(line => line.Begin).ToArray(), lines.ToArray());
    }

    private static LyricsLine Timed(double begin, double end, string text, string? translation = null)
    {
        var parts = text.Split(' ');
        var step = (end - begin) / parts.Length;
        var words = parts.Select((part, index) => new LyricWord(begin + step * index, begin + step * (index + 1),
            index < parts.Length - 1 ? part + " " : part)).ToArray();
        return new LyricsLine(begin, end, text, Words: words, Translation: translation);
    }

    [Fact]
    public async Task UnalignedQQMusicLyrics_NeverReplaceAppleMusic()
    {
        var native = Document("AP_123");
        var other = Document("QQ", true, "外部翻译") with
        {
            Lines = [new LyricsLine(0, 4, "another lyric", Words: [new(0, 4, "another lyric")], Translation: "外部翻译")],
        };
        var external = new External("QQ Music", (_, _) => Task.FromResult<LyricsDocument?>(other)) { OutranksPartialAppleLyrics = true };
        await using var runtime = Create(new Cache(native), [external]);

        var snapshot = await Wait(runtime, state => state.ExternalDiagnostics?.EnrichmentState == ExternalLyricsQueryState.Completed);

        Assert.Same(native, snapshot.Document);
        Assert.Equal(LyricsResolutionSource.AppleMusicCache, snapshot.Resolution.Source);
    }

    [Fact]
    public async Task LrcLibCanOnlyFillAppleGaps_NotReplaceAppleMusic()
    {
        var native = Document("AP_123");
        var lrclib = Document("LRCLIB", true);
        var external = new External("LRCLIB", (_, _) => Task.FromResult<LyricsDocument?>(lrclib));
        await using var runtime = Create(new Cache(native), [external]);

        var snapshot = await Wait(runtime, state => state.ExternalDiagnostics?.EnrichmentState == ExternalLyricsQueryState.Completed);

        Assert.NotSame(lrclib, snapshot.Document);
        Assert.Equal("AP_123", snapshot.Document!.LyricsId);
        Assert.NotNull(snapshot.Document.Lines[0].Words);
    }

    [Fact]
    public async Task WithFeaturesDisabled_AppleMusicLyricsStayPrimaryWithoutQueryingSources()
    {
        var native = Document("AP_123");
        var external = new External("QQ Music", (_, _) => Task.FromResult<LyricsDocument?>(Document("QQ", true, "翻译")))
        {
            OutranksPartialAppleLyrics = true,
        };
        await using var runtime = Create(new Cache(native), [external]);
        runtime.PreferWordTimings = false;
        runtime.PreferTranslations = false;

        var snapshot = await runtime.SnapshotAsync();

        Assert.Same(native, snapshot.Document);
        Assert.Equal(0, external.Calls);
    }

    private static LyricsRuntimeService Create(Cache cache, IReadOnlyList<IExternalLyricsProvider> providers, ManualTime? time = null)
    {
        time ??= new ManualTime();
        return new LyricsRuntimeService(cache, new Player(), new LyricsSynchronizer(), new PlaybackClock(time), timeProvider: time)
        {
            ExternalLyricsProviders = providers,
            PreferWordTimings = true,
            PreferTranslations = true,
        };
    }

    private static LyricsDocument Document(string id, bool words = false, string? translation = null) =>
        new(id, "success", id + ".json", DateTimeOffset.UnixEpoch,
            [new LyricsLine(0, 4, "hello world", Words: words ? [new(0, 1, "hello"), new(1, 4, "world")] : null,
                Translation: translation)], 180);

    private static async Task<RuntimeSnapshot> Wait(LyricsRuntimeService runtime, Func<RuntimeSnapshot, bool> predicate)
    {
        for (var poll = 0; poll < 300; poll++)
        {
            var snapshot = await runtime.SnapshotAsync();
            if (predicate(snapshot)) return snapshot;
            await Task.Delay(5);
        }
        throw new TimeoutException("Expected native/fallback snapshot was not published.");
    }

    private sealed class Cache(LyricsDocument? candidate) : IPlayerMatchedLyricsProvider
    {
        public LyricsDocument? Candidate { get; set; } = candidate;
        public LyricsDocument? ById { get; set; }
        public int CandidateCalls { get; private set; }
        public List<string[]> RequestedIds { get; } = [];
        public Task<LyricsDocument?> GetLatestLyricsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Candidate);
        public Task<IReadOnlyList<LyricsMatch>> FindCandidatesAsync(PlayerState player, CancellationToken cancellationToken = default)
        {
            CandidateCalls++;
            return Task.FromResult<IReadOnlyList<LyricsMatch>>(Candidate is null ? [] : [new LyricsMatch(Candidate, 100,
                Math.Abs(player.Duration - (Candidate.DurationSeconds ?? player.Duration)), HasContentMatch: true)]);
        }
        public Task<LyricsDocument?> FindByLyricsIdsAsync(IReadOnlyList<string> lyricsIds, CancellationToken cancellationToken = default)
        {
            RequestedIds.Add(lyricsIds.ToArray());
            return Task.FromResult(ById);
        }
    }

    private sealed class Player : IPlayerSessionProvider
    {
        public Task<PlayerState?> GetCurrentPlayerStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<PlayerState?>(new PlayerState("Song", "Artist", "Album", 1, 180, true));
    }

    private sealed class External(string name, Func<PlayerState, CancellationToken, Task<LyricsDocument?>> fetch) : IExternalLyricsProvider
    {
        public string Name => name;
        public bool OutranksPartialAppleLyrics { get; init; }
        public int Calls;
        public CancellationToken LastToken;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<LyricsDocument?> FetchAsync(PlayerState player, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            LastToken = cancellationToken;
            Started.TrySetResult();
            return fetch(player, cancellationToken);
        }
    }

    private sealed class Catalog : ICatalogSongResolver
    {
        public Task<IReadOnlyList<string>> ResolveLyricsIdCandidatesAsync(PlayerState player, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["AP_123"]);
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        public void Advance(double seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
}
