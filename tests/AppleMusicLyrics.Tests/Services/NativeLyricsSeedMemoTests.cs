using System.Collections;
using AppleMusicLyrics.Application.Services;
using AppleMusicLyrics.Core.Abstractions;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Sync;
using Xunit;

namespace AppleMusicLyrics.Tests.Services;

public sealed class NativeLyricsSeedMemoTests
{
    [Theory]
    [InlineData("hello world")]
    [InlineData("unrelated source lyrics")]
    public async Task LateCompleteNative_ProcessesTheRetainedSeedOnlyOnceIncludingAlignmentMisses(string seedText)
    {
        await using var scenario = new Scenario(seedText);
        await scenario.WaitForFallbackAsync();
        var beforeNative = scenario.SeedLines.ReadCount;
        var native = CompleteNative();
        scenario.Cache.Candidate = native;

        var snapshot = await scenario.Runtime.SnapshotAsync();

        Assert.Same(native, snapshot.Document);
        Assert.Equal(LyricsResolutionSource.AppleMusicCache, snapshot.Resolution.Source);
        Assert.True(scenario.SeedLines.ReadCount > beforeNative);
        await AssertStableNativePollsAsync(scenario, native);
    }

    [Fact]
    public async Task LateNativeWithMissingWords_ReusesTheSuccessfullyMergedSeedSnapshot()
    {
        await using var scenario = new Scenario("hello world");
        await scenario.WaitForFallbackAsync();
        var beforeNative = scenario.SeedLines.ReadCount;
        var native = CompleteNative() with
        {
            Lines = [new LyricsLine(0, 4, "hello world", Translation: "原生翻译")],
        };
        scenario.Cache.Candidate = native;

        var snapshot = await scenario.Runtime.SnapshotAsync();

        var merged = Assert.IsType<LyricsDocument>(snapshot.Document);
        Assert.NotSame(native, merged);
        Assert.Equal(native.LyricsId, merged.LyricsId);
        Assert.Equal(native.SourceFile, merged.SourceFile);
        Assert.Equal("原生翻译", merged.Lines[0].Translation);
        Assert.True(LyricWordTiming.IsUsable(merged.Lines[0]));
        Assert.True(scenario.SeedLines.ReadCount > beforeNative);
        Assert.Null(native.Lines[0].Words);
        await AssertStableNativePollsAsync(scenario, merged);
    }

    [Fact]
    public async Task SameIdWithANewNativeAnchorReference_ProcessesTheSeedOnceForTheNewAnchor()
    {
        await using var scenario = new Scenario("hello world");
        await scenario.WaitForFallbackAsync();
        var native = CompleteNative();
        scenario.Cache.Candidate = native;
        Assert.Same(native, (await scenario.Runtime.SnapshotAsync()).Document);
        await AssertStableNativePollsAsync(scenario, native);
        var beforeNewAnchor = scenario.SeedLines.ReadCount;
        var updatedNative = native with { SourceFile = "syllable-lyrics-updated.json" };
        scenario.Cache.Candidate = updatedNative;

        var snapshot = await scenario.Runtime.SnapshotAsync();

        Assert.Same(updatedNative, snapshot.Document);
        Assert.True(scenario.SeedLines.ReadCount > beforeNewAnchor);
        await AssertStableNativePollsAsync(scenario, updatedNative);
    }

    [Fact]
    public async Task PreferenceScopeChanges_ProcessTheSeedOnceForEachNewScope()
    {
        await using var scenario = new Scenario("hello world");
        await scenario.WaitForFallbackAsync();
        var native = CompleteNative();
        scenario.Cache.Candidate = native;
        Assert.Same(native, (await scenario.Runtime.SnapshotAsync()).Document);
        await AssertStableNativePollsAsync(scenario, native);
        var beforePreferenceChange = scenario.SeedLines.ReadCount;
        scenario.Runtime.PreferTranslations = false;

        Assert.Same(native, (await scenario.Runtime.SnapshotAsync()).Document);

        Assert.True(scenario.SeedLines.ReadCount > beforePreferenceChange);
        await AssertStableNativePollsAsync(scenario, native);
        var beforeRestoringPreference = scenario.SeedLines.ReadCount;
        scenario.Runtime.PreferTranslations = true;

        Assert.Same(native, (await scenario.Runtime.SnapshotAsync()).Document);

        Assert.True(scenario.SeedLines.ReadCount > beforeRestoringPreference);
        await AssertStableNativePollsAsync(scenario, native);
    }

    private static async Task AssertStableNativePollsAsync(Scenario scenario, LyricsDocument expected)
    {
        var seedReads = scenario.SeedLines.ReadCount;
        for (var poll = 0; poll < 20; poll++)
        {
            var snapshot = await scenario.Runtime.SnapshotAsync();
            Assert.Same(expected, snapshot.Document);
            Assert.Equal(LyricsResolutionSource.AppleMusicCache, snapshot.Resolution.Source);
        }
        Assert.Equal(seedReads, scenario.SeedLines.ReadCount);
        Assert.Equal(1, scenario.External.Calls);
    }

    private static LyricsDocument CompleteNative() => new("AP_123", "success", "syllable-lyrics-native.json",
        DateTimeOffset.UnixEpoch,
        [new LyricsLine(0, 4, "hello world", Words: [new(0, 1, "hello"), new(1, 4, "world")], Translation: "原生翻译")], 180);

    private sealed class Scenario : IAsyncDisposable
    {
        private readonly LyricsDocument _fallback;
        public CountingLines SeedLines { get; }
        public CacheProvider Cache { get; } = new();
        public ExternalProvider External { get; }
        public LyricsRuntimeService Runtime { get; }

        public Scenario(string seedText)
        {
            SeedLines = new CountingLines([new LyricsLine(0, 4, seedText,
                Words: [new LyricWord(0, 4, seedText)], Translation: "外源翻译")]);
            _fallback = new LyricsDocument("QQ_seed", "success", "qqmusic:seed", DateTimeOffset.UnixEpoch, SeedLines, 180);
            External = new ExternalProvider(_fallback);
            var time = new FixedTime();
            Runtime = new LyricsRuntimeService(Cache, new PlayerProvider(), new LyricsSynchronizer(),
                new PlaybackClock(time), timeProvider: time)
            {
                ExternalLyricsProviders = [External], PreferWordTimings = true, PreferTranslations = true,
            };
        }

        public async Task WaitForFallbackAsync()
        {
            for (var poll = 0; poll < 300; poll++)
            {
                var snapshot = await Runtime.SnapshotAsync();
                if (ReferenceEquals(snapshot.Document, _fallback))
                {
                    Assert.Equal(LyricsResolutionSource.ExternalProvider, snapshot.Resolution.Source);
                    Assert.Equal(1, External.Calls);
                    return;
                }
                await Task.Delay(5);
            }
            throw new TimeoutException("Expected the external fallback to be accepted before the native cache arrives.");
        }

        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }

    private sealed class CountingLines(LyricsLine[] lines) : IReadOnlyList<LyricsLine>
    {
        private int _readCount;
        public int ReadCount => Volatile.Read(ref _readCount);
        public int Count
        {
            get
            {
                Interlocked.Increment(ref _readCount);
                return lines.Length;
            }
        }
        public LyricsLine this[int index]
        {
            get
            {
                Interlocked.Increment(ref _readCount);
                return lines[index];
            }
        }
        public IEnumerator<LyricsLine> GetEnumerator()
        {
            Interlocked.Increment(ref _readCount);
            return ((IEnumerable<LyricsLine>)lines).GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CacheProvider : IPlayerMatchedLyricsProvider
    {
        public LyricsDocument? Candidate { get; set; }
        public Task<LyricsDocument?> GetLatestLyricsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Candidate);
        public Task<IReadOnlyList<LyricsMatch>> FindCandidatesAsync(PlayerState player, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LyricsMatch>>(Candidate is null
                ? [] : [new LyricsMatch(Candidate, 160, 0, HasContentMatch: true)]);
        public Task<LyricsDocument?> FindByLyricsIdsAsync(IReadOnlyList<string> lyricsIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<LyricsDocument?>(Candidate is { LyricsId: { } id } document
                && lyricsIds.Contains(id, StringComparer.OrdinalIgnoreCase) ? document : null);
    }

    private sealed class ExternalProvider(LyricsDocument document) : IExternalLyricsProvider
    {
        private int _calls;
        public string Name => "QQ Music seed";
        public int Calls => Volatile.Read(ref _calls);
        public Task<LyricsDocument?> FetchAsync(PlayerState player, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult<LyricsDocument?>(document);
        }
    }

    private sealed class PlayerProvider : IPlayerSessionProvider
    {
        public Task<PlayerState?> GetCurrentPlayerStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<PlayerState?>(new PlayerState("hello world", "Artist", "Album", 1, 180, true));
    }

    private sealed class FixedTime : TimeProvider
    {
        // Keep the tests inside MatchWindow so same-id immutable cache replacements are adopted
        // through normal public candidate selection, without requiring a private-field hook.
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => 0;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }
}
