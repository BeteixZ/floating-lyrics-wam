using System.IO;
using AppleMusicLyrics.Application.Services;
using AppleMusicLyrics.Core.Abstractions;
using AppleMusicLyrics.Core.Configuration;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Parsing;
using AppleMusicLyrics.Core.Sync;
using AppleMusicLyrics.Debugger.Models;
using AppleMusicLyrics.Debugger.Services;
using Xunit;

namespace AppleMusicLyrics.Tests.Debugger;

public sealed class OvernightMonitorEngineTests
{
    [Fact]
    public void TrackIdentity_NormalizationAndEquality_IgnoresDuration()
    {
        var p1 = new PlayerState("  Song Title  ", "Artist Name ", "Album Name", 0.0, 0, true, "com.apple.Music");
        var p2 = new PlayerState("song title", "artist name", "album name", 50.0, 180, true, "com.apple.Music");

        var id1 = TrackIdentity.Create(p1);
        var id2 = TrackIdentity.Create(p2);

        Assert.Equal(id1, id2);
        Assert.Equal("song title|artist name", id1.ToSongKey());
        Assert.False(id1.IsEmpty);
    }

    [Fact]
    public void TrackIdentity_EmptyWhenTitleAndArtistMissing()
    {
        var p = new PlayerState(string.Empty, "   ", null, 0.0, 0, false, null);
        var id = TrackIdentity.Create(p);
        Assert.True(id.IsEmpty);
    }

    [Fact]
    public async Task Duration_UpdatesFromZeroToNormal_ProducesSingleRecord()
    {
        var tempDir = CreateTempDir();
        try
        {
            var logger = new DiagnosticReportLogger(tempDir);
            var state0 = new PlayerState("Track 1", "Artist 1", "Album 1", 0.0, 0, true, "com.apple.Music");
            var state180 = new PlayerState("Track 1", "Artist 1", "Album 1", 1.0, 180, true, "com.apple.Music");

            var states = new Queue<PlayerState>([state0, state180, state180, state180, state180]);
            var playerProvider = new QueuedPlayerProvider(states);
            var cacheScanner = new StubCacheScanner();
            var groundTruthVerifier = new StubGroundTruthVerifier(
                new GroundTruthResult(true, "LRCLIB", "Track 1", "Artist 1", "lyrics", ["line 1"]));

            var runtime = new LyricsRuntimeService(
                cacheScanner,
                playerProvider,
                new LyricsSynchronizer(),
                new PlaybackClock());

            using var engine = new OvernightMonitorEngine(
                new AppSettings(),
                logger,
                playerProvider,
                cacheScanner,
                groundTruthVerifier,
                runtime,
                stabilizationDelay: TimeSpan.FromMilliseconds(5),
                pollInterval: TimeSpan.FromMilliseconds(5),
                resolutionTimeout: TimeSpan.FromMilliseconds(50),
                resolutionPollInterval: TimeSpan.FromMilliseconds(10));

            // First step: player initially had duration 0. Stabilization waits, reads duration 180, audits.
            var stepped = await engine.StepAsync();
            Assert.True(stepped);

            Assert.Single(logger.Records);
            Assert.Equal(180, logger.Records[0].Duration);
            Assert.Equal("Track 1", logger.Records[0].Title);
            Assert.Equal(1, logger.CurrentStats.TotalTracksPlayed);

            // Next step with same track: must be deduplicated
            var secondStep = await engine.StepAsync();
            Assert.False(secondStep);
            Assert.Single(logger.Records);
            Assert.Equal(1, logger.CurrentStats.TotalTracksPlayed);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public async Task Stabilization_IdentityChangesDuringWait_RestartsAndAuditsNewTrack()
    {
        var tempDir = CreateTempDir();
        try
        {
            var logger = new DiagnosticReportLogger(tempDir);
            var songA = new PlayerState("Song A", "Artist A", "Album A", 0.0, 0, true, "com.apple.Music");
            var songB = new PlayerState("Song B", "Artist B", "Album B", 1.0, 190, true, "com.apple.Music");

            // Sequence: song A first, but during wait it becomes song B
            var states = new Queue<PlayerState>([songA, songB, songB, songB, songB]);
            var playerProvider = new QueuedPlayerProvider(states);
            var cacheScanner = new StubCacheScanner();
            var groundTruthVerifier = new StubGroundTruthVerifier(
                new GroundTruthResult(true, "LRCLIB", "Song B", "Artist B", "lyrics", ["line B"]));

            var runtime = new LyricsRuntimeService(
                cacheScanner,
                playerProvider,
                new LyricsSynchronizer(),
                new PlaybackClock());

            using var engine = new OvernightMonitorEngine(
                new AppSettings(),
                logger,
                playerProvider,
                cacheScanner,
                groundTruthVerifier,
                runtime,
                stabilizationDelay: TimeSpan.FromMilliseconds(5),
                pollInterval: TimeSpan.FromMilliseconds(5),
                resolutionTimeout: TimeSpan.FromMilliseconds(50),
                resolutionPollInterval: TimeSpan.FromMilliseconds(10));

            var stepped = await engine.StepAsync();
            Assert.True(stepped);

            // Exactly 1 record for Song B, Song A was never audited
            Assert.Single(logger.Records);
            Assert.Equal("Song B", logger.Records[0].Title);
            Assert.Equal(190, logger.Records[0].Duration);
            Assert.Equal(1, logger.CurrentStats.TotalTracksPlayed);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public async Task ContinuousPolling_SameSong_DoesNotDuplicateAudit()
    {
        var tempDir = CreateTempDir();
        try
        {
            var logger = new DiagnosticReportLogger(tempDir);
            var state = new PlayerState("Track 1", "Artist 1", "Album 1", 10.0, 200, true, "com.apple.Music");
            var playerProvider = new MutablePlayerProvider(state);
            var cacheScanner = new StubCacheScanner();
            var groundTruthVerifier = new StubGroundTruthVerifier(
                new GroundTruthResult(true, "LRCLIB", "Track 1", "Artist 1", "lyrics", ["line 1"]));

            var runtime = new LyricsRuntimeService(
                cacheScanner,
                playerProvider,
                new LyricsSynchronizer(),
                new PlaybackClock());

            using var engine = new OvernightMonitorEngine(
                new AppSettings(),
                logger,
                playerProvider,
                cacheScanner,
                groundTruthVerifier,
                runtime,
                stabilizationDelay: TimeSpan.FromMilliseconds(5),
                pollInterval: TimeSpan.FromMilliseconds(5),
                resolutionTimeout: TimeSpan.FromMilliseconds(50),
                resolutionPollInterval: TimeSpan.FromMilliseconds(10));

            // Poll multiple times
            var first = await engine.StepAsync();
            Assert.True(first);

            for (var i = 0; i < 5; i++)
            {
                var next = await engine.StepAsync();
                Assert.False(next);
            }

            Assert.Single(logger.Records);
            Assert.Equal(1, logger.CurrentStats.TotalTracksPlayed);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public async Task TrackSwitchDuringAudit_DoesNotMixDataAndAborts()
    {
        var tempDir = CreateTempDir();
        try
        {
            var logger = new DiagnosticReportLogger(tempDir);
            var song1 = new PlayerState("Song 1", "Artist 1", "Album 1", 1.0, 180, true, "com.apple.Music");
            var song2 = new PlayerState("Song 2", "Artist 2", "Album 2", 1.0, 210, true, "com.apple.Music");

            // Engine provider reports song 1 initially
            var enginePlayerProvider = new MutablePlayerProvider(song1);

            // But runtime service is playing song 2
            var runtimePlayerProvider = new MutablePlayerProvider(song2);
            var cacheScanner = new StubCacheScanner();
            var groundTruthVerifier = new StubGroundTruthVerifier(
                new GroundTruthResult(true, "LRCLIB", "Song 1", "Artist 1", "lyrics", ["line 1"]));

            var runtime = new LyricsRuntimeService(
                cacheScanner,
                runtimePlayerProvider,
                new LyricsSynchronizer(),
                new PlaybackClock());

            using var engine = new OvernightMonitorEngine(
                new AppSettings(),
                logger,
                enginePlayerProvider,
                cacheScanner,
                groundTruthVerifier,
                runtime,
                stabilizationDelay: TimeSpan.FromMilliseconds(5),
                pollInterval: TimeSpan.FromMilliseconds(5),
                resolutionTimeout: TimeSpan.FromMilliseconds(50),
                resolutionPollInterval: TimeSpan.FromMilliseconds(10));

            // StepAsync tries to audit Song 1, but runtime snapshot returns Song 2.
            // It must detect identity mismatch and abort without logging mixed data.
            var result = await engine.StepAsync();
            Assert.False(result);

            Assert.Empty(logger.Records);
            Assert.Equal(0, logger.CurrentStats.TotalTracksPlayed);
            Assert.Equal(0, logger.CurrentStats.MisidentifiedCount);
            Assert.Equal(0, logger.CurrentStats.MissedCount);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public async Task FetchingExternal_SuccessfullyResolves_RecordsSinglePassWithoutPrematureMissed()
    {
        var tempDir = CreateTempDir();
        try
        {
            var logger = new DiagnosticReportLogger(tempDir);
            var song = new PlayerState("Track 1", "Artist 1", "Album 1", 1.0, 180, true, "com.apple.Music");
            var playerProvider = new MutablePlayerProvider(song);
            var cacheScanner = new StubCacheScanner();

            var document = new LyricsDocument(
                LyricsId: "ext-1",
                Status: "ok",
                SourceFile: string.Empty,
                UpdatedAt: DateTimeOffset.UtcNow,
                Lines:
                [
                    new LyricsLine(0.0, 1.0, "Hello world"),
                    new LyricsLine(1.0, 2.0, "Second line")
                ],
                DurationSeconds: 180);

            var tcs = new TaskCompletionSource<LyricsDocument?>();
            var externalProvider = new DeferredExternalLyricsProvider("LRCLIB", tcs.Task);

            var groundTruthVerifier = new StubGroundTruthVerifier(
                new GroundTruthResult(true, "LRCLIB", "Track 1", "Artist 1", "lyrics", ["Hello world", "Second line"]));

            var runtime = new LyricsRuntimeService(
                cacheScanner,
                playerProvider,
                new LyricsSynchronizer(),
                new PlaybackClock())
            {
                AllowLowConfidenceLyrics = true,
                ExternalLyricsProviders = [externalProvider]
            };

            using var engine = new OvernightMonitorEngine(
                new AppSettings(),
                logger,
                playerProvider,
                cacheScanner,
                groundTruthVerifier,
                runtime,
                stabilizationDelay: TimeSpan.FromMilliseconds(5),
                pollInterval: TimeSpan.FromMilliseconds(5),
                resolutionTimeout: TimeSpan.FromMilliseconds(500),
                resolutionPollInterval: TimeSpan.FromMilliseconds(20));

            // Start step asynchronously while external fetch is pending
            var stepTask = Task.Run(() => engine.StepAsync());

            await Task.Delay(60);

            // Complete external provider with matching lyrics
            tcs.SetResult(document);

            var success = await stepTask;
            Assert.True(success);

            Assert.Single(logger.Records);
            Assert.Equal(VerdictStatus.Pass, logger.Records[0].Verdict);
            Assert.Equal(1, logger.CurrentStats.PassCount);
            Assert.Equal(0, logger.CurrentStats.MissedCount);
            Assert.Equal(1, logger.CurrentStats.TotalTracksPlayed);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public async Task TrackDepartureAndReplay_AuditedAsNewPlaybackEvent()
    {
        var tempDir = CreateTempDir();
        try
        {
            var logger = new DiagnosticReportLogger(tempDir);
            var song = new PlayerState("Track 1", "Artist 1", "Album 1", 1.0, 180, true, "com.apple.Music");
            var playerProvider = new MutablePlayerProvider(song);
            var cacheScanner = new StubCacheScanner();
            var groundTruthVerifier = new StubGroundTruthVerifier(
                new GroundTruthResult(true, "LRCLIB", "Track 1", "Artist 1", "lyrics", ["line 1"]));

            var runtime = new LyricsRuntimeService(
                cacheScanner,
                playerProvider,
                new LyricsSynchronizer(),
                new PlaybackClock());

            using var engine = new OvernightMonitorEngine(
                new AppSettings(),
                logger,
                playerProvider,
                cacheScanner,
                groundTruthVerifier,
                runtime,
                stabilizationDelay: TimeSpan.FromMilliseconds(5),
                pollInterval: TimeSpan.FromMilliseconds(5),
                resolutionTimeout: TimeSpan.FromMilliseconds(50),
                resolutionPollInterval: TimeSpan.FromMilliseconds(10),
                departureDebounce: TimeSpan.Zero);

            // 1. Song plays first time
            var step1 = await engine.StepAsync();
            Assert.True(step1);
            Assert.Single(logger.Records);
            Assert.Equal(1, logger.CurrentStats.TotalTracksPlayed);

            // 2. Track leaves (stopped or null)
            playerProvider.CurrentState = null;
            var step2 = await engine.StepAsync();
            Assert.False(step2);
            Assert.Single(logger.Records);

            // 3. Same song plays again
            playerProvider.CurrentState = song;
            var step3 = await engine.StepAsync();
            Assert.True(step3);
            Assert.Equal(2, logger.Records.Count);
            Assert.Equal(2, logger.CurrentStats.TotalTracksPlayed);
            Assert.Equal("Track 1", logger.Records[1].Title);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public async Task TransientNoPlayerPoll_DoesNotDuplicateSameTrack()
    {
        var tempDir = CreateTempDir();
        try
        {
            var logger = new DiagnosticReportLogger(tempDir);
            var song = new PlayerState("Track 1", "Artist 1", "Album 1", 1.0, 180, true, "com.apple.Music");
            var playerProvider = new MutablePlayerProvider(song);
            var cacheScanner = new StubCacheScanner();
            var verifier = new StubGroundTruthVerifier(
                new GroundTruthResult(false, "None", null, null, null, Array.Empty<string>()));
            var runtime = new LyricsRuntimeService(cacheScanner, playerProvider, new LyricsSynchronizer(), new PlaybackClock());

            using var engine = new OvernightMonitorEngine(
                new AppSettings(), logger, playerProvider, cacheScanner, verifier, runtime,
                stabilizationDelay: TimeSpan.FromMilliseconds(1),
                pollInterval: TimeSpan.FromMilliseconds(1),
                resolutionTimeout: TimeSpan.FromMilliseconds(20),
                resolutionPollInterval: TimeSpan.FromMilliseconds(1),
                departureDebounce: TimeSpan.FromSeconds(5));

            Assert.True(await engine.StepAsync());
            playerProvider.CurrentState = null;
            Assert.False(await engine.StepAsync());
            playerProvider.CurrentState = song;
            Assert.False(await engine.StepAsync());
            Assert.Single(logger.Records);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public async Task Audit_UsesFinalSnapshotPlayerAsCanonicalMetadata()
    {
        var tempDir = CreateTempDir();
        try
        {
            var logger = new DiagnosticReportLogger(tempDir);
            var stablePlayer = new PlayerState("Track 1", "Artist 1", "Album 1", 1.0, 180, true, "com.apple.Music");
            var runtimePlayer = stablePlayer with { Duration = 182 };
            var engineProvider = new MutablePlayerProvider(stablePlayer);
            var runtimeProvider = new MutablePlayerProvider(runtimePlayer);
            var cacheScanner = new StubCacheScanner();
            var verifier = new StubGroundTruthVerifier(
                new GroundTruthResult(false, "None", null, null, null, Array.Empty<string>()));
            var runtime = new LyricsRuntimeService(cacheScanner, runtimeProvider, new LyricsSynchronizer(), new PlaybackClock());

            using var engine = new OvernightMonitorEngine(
                new AppSettings(), logger, engineProvider, cacheScanner, verifier, runtime,
                stabilizationDelay: TimeSpan.FromMilliseconds(1),
                pollInterval: TimeSpan.FromMilliseconds(1),
                resolutionTimeout: TimeSpan.FromMilliseconds(20),
                resolutionPollInterval: TimeSpan.FromMilliseconds(1));

            Assert.True(await engine.StepAsync());
            Assert.Equal(182, logger.Records[0].Duration);
            Assert.Equal(182, verifier.LastPlayer?.Duration);
            Assert.Equal(182, cacheScanner.LastCandidatePlayer?.Duration);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void MisidentifiedSource_IsNotCountedAsSuccessfulSourceHit()
    {
        var tempDir = CreateTempDir();
        try
        {
            var logger = new DiagnosticReportLogger(tempDir);
            logger.LogTrack(new TrackDiagnosticRecord
            {
                SongKey = "wrong|artist",
                Title = "Wrong",
                Artist = "Artist",
                SoftwareSource = "ttmlLyricsWrong.json",
                Verdict = VerdictStatus.Misidentified,
            });

            Assert.Equal(0, logger.CurrentStats.PassCount);
            Assert.Equal(0, logger.CurrentStats.LocalCacheHits);
            Assert.Equal(1, logger.CurrentStats.MisidentifiedCount);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public async Task PendingStatus_Timeout_RecordedAsTimedOutNotMissed()
    {
        var tempDir = CreateTempDir();
        try
        {
            var logger = new DiagnosticReportLogger(tempDir);
            var song = new PlayerState("Track 1", "Artist 1", "Album 1", 1.0, 180, true, "com.apple.Music");
            var playerProvider = new MutablePlayerProvider(song);
            var cacheScanner = new StubCacheScanner();

            // Task that never completes within resolution timeout
            var tcs = new TaskCompletionSource<LyricsDocument?>();
            var externalProvider = new DeferredExternalLyricsProvider("LRCLIB", tcs.Task);

            var groundTruthVerifier = new StubGroundTruthVerifier(
                new GroundTruthResult(true, "LRCLIB", "Track 1", "Artist 1", "lyrics", ["line 1"]));

            var runtime = new LyricsRuntimeService(
                cacheScanner,
                playerProvider,
                new LyricsSynchronizer(),
                new PlaybackClock())
            {
                AllowLowConfidenceLyrics = true,
                ExternalLyricsProviders = [externalProvider]
            };

            using var engine = new OvernightMonitorEngine(
                new AppSettings(),
                logger,
                playerProvider,
                cacheScanner,
                groundTruthVerifier,
                runtime,
                stabilizationDelay: TimeSpan.FromMilliseconds(5),
                pollInterval: TimeSpan.FromMilliseconds(5),
                resolutionTimeout: TimeSpan.FromMilliseconds(60),
                resolutionPollInterval: TimeSpan.FromMilliseconds(10));

            var stepped = await engine.StepAsync();
            Assert.True(stepped);

            Assert.Single(logger.Records);
            Assert.Equal(VerdictStatus.TimedOut, logger.Records[0].Verdict);
            Assert.Equal(1, logger.CurrentStats.InconclusiveCount);
            Assert.Equal(0, logger.CurrentStats.MissedCount);
            Assert.Equal(1, logger.CurrentStats.TotalTracksPlayed);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AML_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CleanupTempDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch { }
    }

    private sealed class MutablePlayerProvider : IPlayerSessionProvider
    {
        public PlayerState? CurrentState { get; set; }

        public MutablePlayerProvider(PlayerState? initial)
        {
            CurrentState = initial;
        }

        public Task<PlayerState?> GetCurrentPlayerStateAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CurrentState);
        }
    }

    private sealed class QueuedPlayerProvider : IPlayerSessionProvider
    {
        private readonly Queue<PlayerState> _states;

        public QueuedPlayerProvider(IEnumerable<PlayerState> states)
        {
            _states = new Queue<PlayerState>(states);
        }

        public Task<PlayerState?> GetCurrentPlayerStateAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<PlayerState?>(_states.Count > 1 ? _states.Dequeue() : _states.Peek());
        }
    }

    private sealed class StubCacheScanner : IPlayerMatchedLyricsProvider
    {
        public PlayerState? LastCandidatePlayer { get; private set; }

        public Task<LyricsDocument?> GetLatestLyricsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LyricsDocument?>(null);
        }

        public Task<IReadOnlyList<LyricsMatch>> FindCandidatesAsync(PlayerState player, CancellationToken cancellationToken = default)
        {
            LastCandidatePlayer = player;
            return Task.FromResult<IReadOnlyList<LyricsMatch>>(Array.Empty<LyricsMatch>());
        }
    }

    private sealed class StubGroundTruthVerifier : IGroundTruthVerifier
    {
        public GroundTruthResult Result { get; set; }
        public PlayerState? LastPlayer { get; private set; }

        public StubGroundTruthVerifier(GroundTruthResult result)
        {
            Result = result;
        }

        public Task<GroundTruthResult> QueryGroundTruthAsync(PlayerState player, CancellationToken cancellationToken = default)
        {
            LastPlayer = player;
            return Task.FromResult(Result);
        }
    }

    private sealed class DeferredExternalLyricsProvider : IExternalLyricsProvider
    {
        private readonly Task<LyricsDocument?> _task;

        public DeferredExternalLyricsProvider(string name, Task<LyricsDocument?> task)
        {
            Name = name;
            _task = task;
        }

        public string Name { get; }

        public Task<LyricsDocument?> FetchAsync(PlayerState player, CancellationToken cancellationToken = default)
        {
            return _task.WaitAsync(cancellationToken);
        }
    }
}
