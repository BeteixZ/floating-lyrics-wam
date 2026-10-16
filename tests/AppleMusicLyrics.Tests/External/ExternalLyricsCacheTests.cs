using AppleMusicLyrics.Core.Abstractions;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Infrastructure.Windows.External;
using System.Text.Json.Nodes;
using Xunit;

namespace AppleMusicLyrics.Tests.External;

public sealed class ExternalLyricsCacheTests : IDisposable
{
    private const long Megabyte = 1024L * 1024L;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AppleMusicLyrics.Tests", Guid.NewGuid().ToString("N"));
    private static readonly PlayerState Player = new("Synthetic Song", "Synthetic Artist", "Synthetic Album", 2, 180, true);

    [Theory]
    [InlineData("LRCLIB")]
    [InlineData("QQ Music")]
    [InlineData("NetEase Cloud Music")]
    public async Task FetchAsync_NewOfflineProviderRestoresTheEntireRichDocument(string source)
    {
        var expected = RichDocument();
        var firstCache = CreateCache();
        using (var online = new CachedExternalLyricsProvider(new StubProvider(source, () => expected), firstCache))
        {
            Assert.Same(expected, await online.FetchAsync(Player));
        }

        var offline = new StubProvider(source, () => throw new HttpRequestException("Offline"));
        using var provider = new CachedExternalLyricsProvider(offline, CreateCache());
        var actual = await provider.FetchAsync(Player with { Position = 77, Playing = false });

        Assert.NotNull(actual);
        Assert.Equal(0, offline.Calls);
        Assert.Equal(expected.LyricsId, actual.LyricsId);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.SourceFile, actual.SourceFile);
        Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
        Assert.Equal(expected.DurationSeconds, actual.DurationSeconds);
        Assert.Equal(expected.LeadingSilenceSeconds, actual.LeadingSilenceSeconds);
        Assert.Equal(expected.NativeOffsetSeconds, actual.NativeOffsetSeconds);
        Assert.Equal(2, actual.Lines.Count);
        for (var i = 0; i < expected.Lines.Count; i++)
        {
            var original = expected.Lines[i];
            var restored = actual.Lines[i];
            Assert.Equal(original.Begin, restored.Begin);
            Assert.Equal(original.End, restored.End);
            Assert.Equal(original.Text, restored.Text);
            Assert.Equal(original.SongPart, restored.SongPart);
            Assert.Equal(original.Translation, restored.Translation);
            if (original.Words is null) Assert.Null(restored.Words);
            else Assert.Equal(original.Words.ToArray(), restored.Words!.ToArray());
        }
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void TryGet_IsolatesSourcesAndAllTrackIdentityFields()
    {
        var cache = CreateCache();
        cache.Store("QQ Music", Player, RichDocument() with { LyricsId = "QQ" });
        cache.Store("LRCLIB", Player, RichDocument() with { LyricsId = "LRC" });

        Assert.True(cache.TryGet("QQ Music", Player, out var qq));
        Assert.Equal("QQ", qq!.LyricsId);
        Assert.True(cache.TryGet("LRCLIB", Player, out var lrc));
        Assert.Equal("LRC", lrc!.LyricsId);
        Assert.False(cache.TryGet("NetEase Cloud Music", Player, out _));
        Assert.False(cache.TryGet("QQ Music", Player with { Title = "Other Song" }, out _));
        Assert.False(cache.TryGet("QQ Music", Player with { Artist = "Other Artist" }, out _));
        Assert.False(cache.TryGet("QQ Music", Player with { Album = "Other Album" }, out _));
        Assert.False(cache.TryGet("QQ Music", Player with { Duration = 181 }, out _));
        Assert.True(cache.TryGet("QQ Music", Player with { Title = "SYNTHETIC SONG", Position = 10 }, out _));
        Assert.Equal(2, Directory.GetFiles(_root, "v1-*.json").Length);
    }

    [Fact]
    public void Configure_DisablingImmediatelyGatesExistingReadsAndWritesAndPreservesFiles()
    {
        var cache = CreateCache();
        cache.Store("QQ Music", Player, RichDocument());
        var existingPath = Assert.Single(Directory.GetFiles(_root));
        var existingContent = File.ReadAllBytes(existingPath);

        cache.Configure(false, 64);
        Assert.False(cache.Enabled);
        Assert.False(cache.TryGet("QQ Music", Player, out _));
        cache.Store("LRCLIB", Player, RichDocument());
        Assert.Equal(existingContent, File.ReadAllBytes(existingPath));
        Assert.Single(Directory.GetFiles(_root));

        cache.Configure(true, 64);
        Assert.True(cache.TryGet("QQ Music", Player, out _));
        Assert.False(cache.TryGet("LRCLIB", Player, out _));
    }

    [Fact]
    public void DisabledCache_DoesNotCreateItsDirectory()
    {
        var cache = new ExternalLyricsCache(_root);
        cache.Store("QQ Music", Player, RichDocument());
        Assert.False(cache.TryGet("QQ Music", Player, out _));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void Store_EnforcesOneQuotaAcrossProviders()
    {
        var cache = CreateCache(1);
        cache.Store("QQ Music", Player, LargeDocument(600_000));
        var oldestPath = Assert.Single(Directory.GetFiles(_root));
        File.SetLastWriteTimeUtc(oldestPath, DateTime.UtcNow.AddDays(-1));

        cache.Store("NetEase Cloud Music", Player, LargeDocument(600_000));

        Assert.False(File.Exists(oldestPath));
        Assert.False(cache.TryGet("QQ Music", Player, out _));
        Assert.True(cache.TryGet("NetEase Cloud Music", Player, out _));
        Assert.True(Directory.GetFiles(_root).Sum(path => new FileInfo(path).Length) <= Megabyte);
    }

    [Fact]
    public void Configure_LoweringCapacityEvictsOldestExistingEntriesImmediately()
    {
        var cache = CreateCache(2);
        cache.Store("QQ Music", Player, LargeDocument(600_000));
        var oldestPath = Assert.Single(Directory.GetFiles(_root));
        File.SetLastWriteTimeUtc(oldestPath, DateTime.UtcNow.AddDays(-1));
        cache.Store("LRCLIB", Player, LargeDocument(600_000));
        Assert.Equal(2, Directory.GetFiles(_root).Length);

        cache.Configure(true, 1);

        Assert.False(File.Exists(oldestPath));
        Assert.True(Directory.GetFiles(_root).Sum(path => new FileInfo(path).Length) <= Megabyte);
        Assert.True(cache.TryGet("LRCLIB", Player, out _));
        cache.Configure(true, int.MinValue);
        Assert.Equal(1, cache.MaxMegabytes);
        cache.Configure(true, int.MaxValue);
        Assert.Equal(1024, cache.MaxMegabytes);
    }

    [Fact]
    public void Store_SkipsOversizedEntriesAndRemovesTemporaryFiles()
    {
        var cache = CreateCache(1);
        cache.Store("QQ Music", Player, LargeDocument(1_100_000));
        Assert.False(cache.TryGet("QQ Music", Player, out _));
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData("{broken json")]
    [InlineData("{\"Version\":1,\"Key\":null,\"Document\":null}")]
    public async Task FetchAsync_CorruptedEntryIsRefetchedAndReplaced(string corruptContent)
    {
        var cache = CreateCache();
        cache.Store("QQ Music", Player, RichDocument());
        var path = Assert.Single(Directory.GetFiles(_root));
        File.WriteAllText(path, corruptContent);
        Assert.False(cache.TryGet("QQ Music", Player, out _));
        var underlying = new StubProvider("QQ Music", RichDocument);
        using var provider = new CachedExternalLyricsProvider(underlying, cache);

        Assert.NotNull(await provider.FetchAsync(Player));
        Assert.Equal(1, underlying.Calls);
        Assert.True(CreateCache().TryGet("QQ Music", Player, out _));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Theory]
    [InlineData("Lines")]
    [InlineData("SourceFile")]
    public void TryGet_IncompleteDocumentWithAValidCacheKeyIsAMiss(string missingProperty)
    {
        var cache = CreateCache();
        cache.Store("QQ Music", Player, RichDocument());
        var path = Assert.Single(Directory.GetFiles(_root));
        var entry = JsonNode.Parse(File.ReadAllText(path))!;
        entry["Document"]![missingProperty] = null;
        File.WriteAllText(path, entry.ToJsonString());

        Assert.False(cache.TryGet("QQ Music", Player, out _));
    }

    [Fact]
    public async Task FetchAsync_FailuresAndMissingLyricsAreNeverPersisted()
    {
        var cache = CreateCache();
        var response = 0;
        var underlying = new StubProvider("QQ Music", () => ++response switch
        {
            1 => throw new HttpRequestException("Temporary failure"),
            2 => null,
            3 => RichDocument() with { Lines = [] },
            _ => RichDocument(),
        });
        using var provider = new CachedExternalLyricsProvider(underlying, cache);

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.FetchAsync(Player));
        Assert.Null(await provider.FetchAsync(Player));
        Assert.Empty((await provider.FetchAsync(Player))!.Lines);
        Assert.False(Directory.Exists(_root));
        Assert.NotNull(await provider.FetchAsync(Player));
        Assert.NotNull(await provider.FetchAsync(Player));
        Assert.Equal(4, underlying.Calls);
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task FetchAsync_UnwritableCacheDoesNotHideUsableOnlineLyrics()
    {
        var parent = Path.GetDirectoryName(_root)!;
        Directory.CreateDirectory(parent);
        File.WriteAllText(_root, "A file occupies the configured cache directory");
        var underlying = new StubProvider("QQ Music", RichDocument);
        using var provider = new CachedExternalLyricsProvider(underlying, CreateCache());

        Assert.NotNull(await provider.FetchAsync(Player));
        Assert.Equal(1, underlying.Calls);
        Assert.True(File.Exists(_root));
    }

    [Fact]
    public async Task FetchAsync_CanceledCacheHitDoesNotReturnOrCallUnderlyingProvider()
    {
        var cache = CreateCache();
        cache.Store("QQ Music", Player, RichDocument());
        var underlying = new StubProvider("QQ Music", RichDocument);
        using var provider = new CachedExternalLyricsProvider(underlying, cache);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.FetchAsync(Player, cancellation.Token));
        Assert.Equal(0, underlying.Calls);
    }

    [Fact]
    public void GetUsage_MeasuresActualManagedFileBytesIncludingTemporaryFiles()
    {
        var cache = CreateCache();
        cache.Store("QQ Music", Player, RichDocument());
        cache.Store("NetEase Cloud Music", Player, RichDocument() with { LyricsId = "netease:456" });
        var entries = Directory.GetFiles(_root);
        var temporary = entries[0] + "." + new string('a', 32) + ".tmp";
        File.WriteAllBytes(temporary, [0, 128, 255, 10, 20]);
        File.WriteAllText(Path.Combine(_root, "unrelated.json"), new string('x', 1000));

        var usage = cache.GetUsage();

        Assert.True(usage.IsAvailable);
        Assert.Equal(2, usage.EntryCount);
        Assert.Equal(entries.Sum(path => new FileInfo(path).Length) + 5, usage.SizeBytes);
    }

    [Fact]
    public void Clear_DisabledCacheRemovesAllSourcesButPreservesUnknownFilesSubdirectoriesAndConfiguration()
    {
        var cache = CreateCache(19);
        foreach (var source in new[] { "LRCLIB", "QQ Music", "NetEase Cloud Music" })
            cache.Store(source, Player, RichDocument());
        var ownEntry = Directory.GetFiles(_root)[0];
        File.WriteAllText(ownEntry + "." + new string('b', 32) + ".tmp", "Interrupted cache write");
        var retained = new[]
        {
            "settings.ini", "lrclib-cache.json", "unrelated.json",
            "v1-" + new string('g', 64) + ".json", // Same length as our names, but not a hash.
            Path.GetFileName(ownEntry) + ".not-a-guid.tmp",
        };
        foreach (var name in retained) File.WriteAllText(Path.Combine(_root, name), "Preserve this file");
        var child = Path.Combine(_root, "subdirectory");
        Directory.CreateDirectory(child);
        var nestedEntry = Path.Combine(child, Path.GetFileName(ownEntry));
        File.WriteAllText(nestedEntry, "Preserve nested files, even with a cache filename");
        cache.Configure(false, 19);
        Assert.Equal(3, cache.GetUsage().EntryCount);
        Assert.True(cache.GetUsage().SizeBytes > 0);

        var result = cache.Clear();

        Assert.True(result.Success);
        Assert.Equal(new ExternalLyricsCacheUsage(0, 0, true), result.RemainingUsage);
        Assert.Equal(result.RemainingUsage, cache.GetUsage());
        Assert.False(cache.Enabled);
        Assert.Equal(19, cache.MaxMegabytes);
        Assert.True(Directory.Exists(_root));
        Assert.True(File.Exists(nestedEntry));
        foreach (var name in retained) Assert.True(File.Exists(Path.Combine(_root, name)), name);
        Assert.Equal(retained.Length, Directory.GetFiles(_root).Length);
    }

    [Fact]
    public void GetUsageAndClear_MissingDirectoryStayEmptyWithoutCreatingIt()
    {
        var cache = new ExternalLyricsCache(_root);
        Assert.Equal(new ExternalLyricsCacheUsage(0, 0, true), cache.GetUsage());

        var result = cache.Clear();

        Assert.True(result.Success);
        Assert.Equal(new ExternalLyricsCacheUsage(0, 0, true), result.RemainingUsage);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void Clear_LockedFileReportsTheActualRemainingBytesAndDeletesOtherEntries()
    {
        var cache = CreateCache();
        cache.Store("QQ Music", Player, RichDocument());
        var lockedPath = Assert.Single(Directory.GetFiles(_root));
        cache.Store("LRCLIB", Player, RichDocument());
        var expectedBytes = new FileInfo(lockedPath).Length;
        using (var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = cache.Clear();

            Assert.False(result.Success);
            Assert.True(result.RemainingUsage.IsAvailable);
            Assert.Equal(1, result.RemainingUsage.EntryCount);
            Assert.Equal(expectedBytes, result.RemainingUsage.SizeBytes);
            Assert.Equal(lockedPath, Assert.Single(Directory.GetFiles(_root)));
        }
        Assert.True(cache.Clear().Success);
        Assert.Equal(new ExternalLyricsCacheUsage(0, 0, true), cache.GetUsage());
    }

    [Fact]
    public void GetUsageAndClear_UnusableDirectoryIsUnavailableRatherThanAReportedSuccessfulClear()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_root)!);
        File.WriteAllText(_root, "A file occupies the cache directory");
        var cache = new ExternalLyricsCache(_root);

        Assert.False(cache.GetUsage().IsAvailable);
        var result = cache.Clear();
        Assert.False(result.Success);
        Assert.False(result.RemainingUsage.IsAvailable);
        Assert.True(File.Exists(_root));
    }

    [Fact]
    public async Task FetchAsync_ClearDuringFetchDoesNotRepersistTheOldResultButANewFetchCanCache()
    {
        var cache = CreateCache();
        cache.Store("LRCLIB", Player, RichDocument());
        var response = new TaskCompletionSource<LyricsDocument?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = RichDocument();
        var inner = new WaitingProvider(response.Task, expected);
        using var provider = new CachedExternalLyricsProvider(inner, cache);
        var pending = provider.FetchAsync(Player);
        Assert.Equal(1, inner.Calls);
        Assert.False(pending.IsCompleted);

        Assert.True(cache.Clear().Success);
        response.SetResult(expected);

        Assert.Same(expected, await pending);
        Assert.Equal(new ExternalLyricsCacheUsage(0, 0, true), cache.GetUsage());
        Assert.Same(expected, await provider.FetchAsync(Player));
        Assert.Equal(2, inner.Calls);
        Assert.True(cache.TryGet("QQ Music", Player, out _));
        Assert.Equal(1, cache.GetUsage().EntryCount);
    }

    [Fact]
    public void Store_OldGenerationIsRejectedWhileCurrentDocumentBackfillStillWorks()
    {
        var cache = CreateCache();
        var beforeClear = cache.Generation;
        Assert.True(cache.Clear().Success);
        Assert.NotEqual(beforeClear, cache.Generation);

        cache.Store("QQ Music", Player, RichDocument(), beforeClear);
        Assert.False(cache.TryGet("QQ Music", Player, out _));
        cache.Store("QQ Music", Player, RichDocument());
        Assert.True(cache.TryGet("QQ Music", Player, out _));
    }

    private ExternalLyricsCache CreateCache(int capacity = 64)
    {
        var cache = new ExternalLyricsCache(_root);
        cache.Configure(true, capacity);
        return cache;
    }

    private static LyricsDocument RichDocument() => new(
        "qqmusic:123", "ok", "qqmusic:123", new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.FromHours(8)),
        [new LyricsLine(1.125, 3.625, "Hello 世界!", "verse",
            [new LyricWord(1.125, 1.625, "Hello "), new LyricWord(1.875, 3.625, "世界!")], "你好，世界！"),
         new LyricsLine(5, 7, "A line without word clocks", "chorus", Translation: "另一行")],
        180, 0.125, -0.09);

    private static LyricsDocument LargeDocument(int textLength) => RichDocument() with
    {
        Lines = [new LyricsLine(1, 2, new string('x', textLength))],
    };

    private sealed class StubProvider(string name, Func<LyricsDocument?> response) : IExternalLyricsProvider, IDisposable
    {
        public string Name => name;
        public int Calls { get; private set; }

        public Task<LyricsDocument?> FetchAsync(PlayerState player, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(response());
        }

        public void Dispose() { }
    }

    private sealed class WaitingProvider(Task<LyricsDocument?> pending, LyricsDocument next) : IExternalLyricsProvider
    {
        public string Name => "QQ Music";
        public int Calls { get; private set; }
        public Task<LyricsDocument?> FetchAsync(PlayerState player, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ++Calls == 1 ? pending : Task.FromResult<LyricsDocument?>(next);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        else if (File.Exists(_root)) File.Delete(_root);
    }
}
