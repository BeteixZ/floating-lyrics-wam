using System.Diagnostics;
using System.IO;
using AppleMusicLyrics.Application.Services;
using AppleMusicLyrics.Core.Abstractions;
using AppleMusicLyrics.Core.Configuration;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Parsing;
using AppleMusicLyrics.Core.Sync;
using AppleMusicLyrics.Debugger.Models;
using AppleMusicLyrics.Infrastructure.Windows.Cache;
using AppleMusicLyrics.Infrastructure.Windows.Catalog;
using AppleMusicLyrics.Infrastructure.Windows.External;
using AppleMusicLyrics.Infrastructure.Windows.Media;

namespace AppleMusicLyrics.Debugger.Services;

public sealed class OvernightMonitorEngine : IDisposable
{
    private readonly AppSettings _settings;
    private readonly DiagnosticReportLogger _logger;
    private readonly IGroundTruthVerifier _groundTruthVerifier;
    private readonly IPlayerSessionProvider _playerProvider;
    private readonly IPlayerMatchedLyricsProvider _cacheScanner;
    private readonly ITunesCatalogSongResolver? _catalogResolver;
    private readonly LrcLibLyricsProvider? _lrcLibProvider;
    private readonly LyricsRuntimeService _runtimeService;

    private readonly TimeSpan _stabilizationDelay;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _resolutionTimeout;
    private readonly TimeSpan _resolutionPollInterval;
    private readonly TimeSpan _departureDebounce;
    private readonly TimeProvider _timeProvider;

    private TrackIdentity? _currentAuditedIdentity;
    private DateTimeOffset? _playerMissingSince;

    public OvernightMonitorEngine(AppSettings settings, DiagnosticReportLogger logger)
    {
        _settings = settings;
        _logger = logger;
        _stabilizationDelay = TimeSpan.FromMilliseconds(2500);
        _pollInterval = TimeSpan.FromMilliseconds(1200);
        _resolutionTimeout = TimeSpan.FromSeconds(10);
        _resolutionPollInterval = TimeSpan.FromMilliseconds(400);
        _departureDebounce = TimeSpan.FromSeconds(3);
        _timeProvider = TimeProvider.System;

        _groundTruthVerifier = new OnlineGroundTruthVerifier(timeoutSeconds: 8.0);
        var parser = new TtmlLyricsParser();
        _cacheScanner = new AppleMusicCacheScanner(parser);
        _playerProvider = new GlobalMediaSessionProvider(_settings.AllowNonAppleMediaSessions);

        var catalogCachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AppleMusicLyrics",
            "catalog-cache.json");

        _catalogResolver = _settings.CatalogLookupEnabled
            ? new ITunesCatalogSongResolver(
                _settings.CatalogStorefronts,
                catalogCachePath,
                _settings.CatalogLookupTimeoutSeconds)
            : null;

        _lrcLibProvider = _settings.ExternalLyricsEnabled
            ? new LrcLibLyricsProvider(timeoutSeconds: _settings.ExternalLyricsTimeoutSeconds)
            : null;

        var synchronizer = new LyricsSynchronizer();
        var playbackClock = new PlaybackClock();

        _runtimeService = new LyricsRuntimeService(
            _cacheScanner,
            _playerProvider,
            synchronizer,
            playbackClock,
            _settings.LyricsOffsetSeconds,
            _catalogResolver)
        {
            ApplyNativeLyricOffset = _settings.ApplyNativeLyricOffset,
            AllowLowConfidenceLyrics = _settings.AllowLowConfidenceLyrics,
            ExternalLyricsProviders = _lrcLibProvider is null
                ? Array.Empty<IExternalLyricsProvider>()
                : [_lrcLibProvider],
        };
    }

    internal OvernightMonitorEngine(
        AppSettings settings,
        DiagnosticReportLogger logger,
        IPlayerSessionProvider playerProvider,
        IPlayerMatchedLyricsProvider cacheScanner,
        IGroundTruthVerifier groundTruthVerifier,
        LyricsRuntimeService runtimeService,
        TimeSpan? stabilizationDelay = null,
        TimeSpan? pollInterval = null,
        TimeSpan? resolutionTimeout = null,
        TimeSpan? resolutionPollInterval = null,
        TimeSpan? departureDebounce = null,
        TimeProvider? timeProvider = null)
    {
        _settings = settings;
        _logger = logger;
        _playerProvider = playerProvider;
        _cacheScanner = cacheScanner;
        _groundTruthVerifier = groundTruthVerifier;
        _runtimeService = runtimeService;

        _stabilizationDelay = stabilizationDelay ?? TimeSpan.FromMilliseconds(2500);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(1200);
        _resolutionTimeout = resolutionTimeout ?? TimeSpan.FromSeconds(10);
        _resolutionPollInterval = resolutionPollInterval ?? TimeSpan.FromMilliseconds(400);
        _departureDebounce = departureDebounce ?? TimeSpan.FromSeconds(3);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.LogInfo("已接入系统媒体传输控制 (SMTC)，开始持续监听播放事件...");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await StepAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogInfo($"[ERROR] 循环检测异常: {ex.Message}");
                await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogInfo("整夜监控已停止。正在生成最终汇总报告...");
        _logger.WriteMarkdownReport();
    }

    internal async Task<bool> StepAsync(CancellationToken cancellationToken = default)
    {
        var player = await _playerProvider.GetCurrentPlayerStateAsync(cancellationToken).ConfigureAwait(false);
        if (player is null || string.IsNullOrWhiteSpace(player.Title))
        {
            _playerMissingSince ??= _timeProvider.GetUtcNow();
            return false;
        }

        if (_playerMissingSince is DateTimeOffset missingSince)
        {
            if (_timeProvider.GetUtcNow() - missingSince >= _departureDebounce)
            {
                _currentAuditedIdentity = null;
            }

            _playerMissingSince = null;
        }

        var identity = TrackIdentity.Create(player);
        if (identity.IsEmpty)
        {
            _currentAuditedIdentity = null;
            return false;
        }

        if (identity.Equals(_currentAuditedIdentity))
        {
            return false;
        }

        _logger.LogInfo($"检测到切歌: \"{player.Title}\" — {player.Artist} (等待元数据及缓存写入稳定)...");

        var stabilizedPlayer = await WaitForMetadataStabilizationAsync(player, identity, cancellationToken).ConfigureAwait(false);
        if (stabilizedPlayer is null)
        {
            return false;
        }

        var stabilizedIdentity = TrackIdentity.Create(stabilizedPlayer);
        if (stabilizedIdentity.Equals(_currentAuditedIdentity))
        {
            return false;
        }

        var auditSuccess = await AuditTrackAsync(stabilizedPlayer, stabilizedIdentity, cancellationToken).ConfigureAwait(false);
        if (auditSuccess)
        {
            _currentAuditedIdentity = stabilizedIdentity;
            return true;
        }

        return false;
    }

    private async Task<PlayerState?> WaitForMetadataStabilizationAsync(
        PlayerState initialPlayer,
        TrackIdentity initialIdentity,
        CancellationToken cancellationToken)
    {
        var targetIdentity = initialIdentity;
        var consecutiveCount = 1;
        var lastPlayer = initialPlayer;

        // Allow Apple Music to write cache file and publish accurate duration
        await Task.Delay(_stabilizationDelay, cancellationToken).ConfigureAwait(false);

        var stabilizationDeadline = _timeProvider.GetUtcNow() + TimeSpan.FromSeconds(10);
        while (!cancellationToken.IsCancellationRequested && _timeProvider.GetUtcNow() < stabilizationDeadline)
        {
            var player = await _playerProvider.GetCurrentPlayerStateAsync(cancellationToken).ConfigureAwait(false);
            if (player is null || string.IsNullOrWhiteSpace(player.Title))
            {
                return null;
            }

            var identity = TrackIdentity.Create(player);
            if (identity.IsEmpty)
            {
                return null;
            }

            if (!identity.Equals(targetIdentity))
            {
                _logger.LogInfo($"曲目在稳定期切换: \"{targetIdentity.Title}\" -> \"{identity.Title}\"，重新开始稳定流程...");
                targetIdentity = identity;
                lastPlayer = player;
                consecutiveCount = 1;

                await Task.Delay(_stabilizationDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            consecutiveCount++;
            lastPlayer = player;

            if (player.Duration > 0 && consecutiveCount >= 2)
            {
                return player;
            }

            await Task.Delay(Math.Min(200, Math.Max(1, (int)_pollInterval.TotalMilliseconds)), cancellationToken).ConfigureAwait(false);
        }

        if (lastPlayer.Duration > 0 && consecutiveCount >= 2)
        {
            return lastPlayer;
        }

        _logger.LogInfo($"曲目 \"{lastPlayer.Title}\" 元数据未能稳定 (Duration: {lastPlayer.Duration:F1}, 连续匹配: {consecutiveCount})，跳过本次审计。");
        return null;
    }

    private async Task<bool> AuditTrackAsync(
        PlayerState player,
        TrackIdentity targetIdentity,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        // 1. Run our software's runtime pipeline snapshot
        var snapshot = await _runtimeService.SnapshotAsync(cancellationToken).ConfigureAwait(false);

        // Verify that runtime snapshot matches target identity
        if (snapshot.Player is null || !targetIdentity.Equals(TrackIdentity.Create(snapshot.Player)))
        {
            _logger.LogInfo($"[AUDIT-ABORT] 运行时快照曲目 (\"{snapshot.Player?.Title}\") 与审计目标 (\"{player.Title}\") 不一致，放弃本次审计。");
            return false;
        }

        // A catalog or external lookup is intentionally asynchronous. Keep polling its state for a
        // bounded period even when an interim candidate exists.
        var resolutionDeadline = _timeProvider.GetUtcNow() + _resolutionTimeout;
        while (IsResolutionPending(snapshot.Resolution.Status) &&
               player.Duration > 0 &&
               _timeProvider.GetUtcNow() < resolutionDeadline)
        {
            await Task.Delay(_resolutionPollInterval, cancellationToken).ConfigureAwait(false);
            snapshot = await _runtimeService.SnapshotAsync(cancellationToken).ConfigureAwait(false);

            // Re-verify snapshot identity during polling
            if (snapshot.Player is null || !targetIdentity.Equals(TrackIdentity.Create(snapshot.Player)))
            {
                _logger.LogInfo($"[AUDIT-ABORT] 解析等待期间曲目已切换 (\"{snapshot.Player?.Title}\")，放弃本次审计。");
                return false;
            }
        }

        var auditPlayer = snapshot.Player!;

        // 2. Discover raw local candidates
        var candidates = auditPlayer.Duration > 0
            ? await _cacheScanner.FindCandidatesAsync(auditPlayer, cancellationToken).ConfigureAwait(false)
            : Array.Empty<LyricsMatch>();

        var topCandidate = candidates.Count > 0 ? candidates[0] : null;
        var selectedCandidate = snapshot.Resolution.CandidateEvidence.FirstOrDefault(evidence =>
            string.Equals(evidence.Decision, "Selected.", StringComparison.Ordinal));

        // 3. Query online ground truth in parallel
        var groundTruth = await _groundTruthVerifier.QueryGroundTruthAsync(auditPlayer, cancellationToken).ConfigureAwait(false);

        // 4. Compare and evaluate
        VerdictStatus verdict;
        string reason;
        double similarity;

        if (IsResolutionPending(snapshot.Resolution.Status))
        {
            verdict = VerdictStatus.TimedOut;
            reason = $"歌词解析超时（状态仍为 {snapshot.Resolution.Status}: {snapshot.Resolution.Summary}），未能在规定时间内完成解析。";
            similarity = 0.0;
        }
        else
        {
            (verdict, reason, similarity) = LyricsTextComparator.Evaluate(snapshot.Document, auditPlayer, groundTruth);
        }

        // 5. Determine software source string
        var softwareSource = "None";
        if (snapshot.Document != null)
        {
            if (!string.IsNullOrWhiteSpace(snapshot.Document.SourceFile))
            {
                softwareSource = Path.GetFileName(snapshot.Document.SourceFile);
            }
            else
            {
                softwareSource = "LRCLIB (External)";
            }
        }

        sw.Stop();

        var record = new TrackDiagnosticRecord
        {
            Identity = targetIdentity,
            SongKey = targetIdentity.ToSongKey(),
            Title = auditPlayer.Title ?? "Unknown Title",
            Artist = auditPlayer.Artist ?? "Unknown Artist",
            Album = auditPlayer.Album,
            Duration = auditPlayer.Duration,
            Timestamp = DateTimeOffset.Now,
            ResolutionStatus = snapshot.Resolution.Status,
            ResolutionReason = snapshot.Resolution.Summary,
            SoftwareConfidence = snapshot.Resolution.Confidence.ToString(),
            SoftwareSource = softwareSource,
            SoftwareDoc = snapshot.Document,
            SoftwareLyricSample = snapshot.Document?.Lines.Take(3).Select(l => l.Text).ToArray() ?? Array.Empty<string>(),
            CandidateCount = snapshot.Resolution.CandidateCount > 0
                ? snapshot.Resolution.CandidateCount
                : candidates.Count,
            TopCandidateScore = selectedCandidate?.Score ?? topCandidate?.Score ?? 0,
            TopCandidateDurationDelta = selectedCandidate?.DurationDelta ?? topCandidate?.DurationDelta ?? 0.0,
            GroundTruthSource = groundTruth.Source,
            GroundTruthHasLyrics = groundTruth.HasLyrics,
            GroundTruthLyricSample = groundTruth.Lines.Take(3).ToArray(),
            SimilarityScore = similarity,
            Verdict = verdict,
            VerdictReason = reason,
            Elapsed = sw.Elapsed
        };

        _logger.LogTrack(record);
        return true;
    }

    private static bool IsResolutionPending(LyricsResolutionStatus status)
    {
        return status is LyricsResolutionStatus.WaitingForMetadata
            or LyricsResolutionStatus.SearchingLocal
            or LyricsResolutionStatus.VerifyingCatalog
            or LyricsResolutionStatus.FetchingExternal;
    }

    public void Dispose()
    {
        _runtimeService.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (_groundTruthVerifier is IDisposable disposableGt)
        {
            disposableGt.Dispose();
        }
        _lrcLibProvider?.Dispose();
        _catalogResolver?.Dispose();
    }
}
