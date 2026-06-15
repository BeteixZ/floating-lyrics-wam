using AppleMusicLyrics.Core.Abstractions;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Sync;

namespace AppleMusicLyrics.App.Services;

public sealed class LyricsRuntimeService
{
    private static readonly TimeSpan StartupDocumentGrace = TimeSpan.FromMinutes(10);
    private readonly ILyricsDocumentProvider _lyricsProvider;
    private readonly IPlayerSessionProvider _playerSessionProvider;
    private readonly LyricsSynchronizer _synchronizer;
    private readonly PlaybackClock _playbackClock;
    private double _lyricsOffsetSeconds;
    private LyricsDocument? _currentDocument;
    private string? _sessionKey;
    private DateTimeOffset _sessionChangedAt = DateTimeOffset.UtcNow;
    private double? _lastRawPositionSeconds;
    private bool _allowStartupDocumentFallback = true;

    public LyricsRuntimeService(
        ILyricsDocumentProvider lyricsProvider,
        IPlayerSessionProvider playerSessionProvider,
        LyricsSynchronizer synchronizer,
        PlaybackClock? playbackClock = null,
        double lyricsOffsetSeconds = 0.0)
    {
        _lyricsProvider = lyricsProvider;
        _playerSessionProvider = playerSessionProvider;
        _synchronizer = synchronizer;
        _playbackClock = playbackClock ?? new PlaybackClock();
        _lyricsOffsetSeconds = lyricsOffsetSeconds;
    }

    public double LyricsOffsetSeconds
    {
        get => _lyricsOffsetSeconds;
        set => _lyricsOffsetSeconds = value;
    }

    public async Task<RuntimeSnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        var rawPlayer = await _playerSessionProvider.GetCurrentPlayerStateAsync(cancellationToken).ConfigureAwait(false);
        if (rawPlayer is null)
        {
            ResetClockState();
            _currentDocument = null;
            return new RuntimeSnapshot(null, null, new ActiveLyricState(null, null, null, null));
        }

        var sessionChanged = UpdateClock(rawPlayer);
        var incomingDocument = await _lyricsProvider.GetLatestLyricsAsync(cancellationToken).ConfigureAwait(false);
        var document = await ResolveCurrentDocumentAsync(incomingDocument, rawPlayer, sessionChanged, cancellationToken).ConfigureAwait(false);

        var estimatedPosition = _playbackClock.GetEstimatedPosition();
        if (rawPlayer.Duration > 0)
        {
            estimatedPosition = Math.Clamp(estimatedPosition, 0, rawPlayer.Duration);
        }

        var estimatedPlayer = rawPlayer with
        {
            Position = estimatedPosition,
        };

        var activeLyric = _synchronizer.Resolve(document, estimatedPlayer, _lyricsOffsetSeconds);
        return new RuntimeSnapshot(
            document,
            estimatedPlayer,
            activeLyric,
            RawPositionSeconds: rawPlayer.Position,
            EstimatedPositionSeconds: estimatedPosition);
    }

    private bool UpdateClock(PlayerState player)
    {
        var sessionChanged = false;
        var currentSessionKey = BuildSessionKey(player);
        if (!string.Equals(_sessionKey, currentSessionKey, StringComparison.Ordinal))
        {
            if (_sessionKey is not null)
            {
                _allowStartupDocumentFallback = false;
            }

            _playbackClock.Reset();
            _sessionChangedAt = DateTimeOffset.UtcNow;
            sessionChanged = true;
        }
        else if (_lastRawPositionSeconds.HasValue && player.Position + 2.0 < _lastRawPositionSeconds.Value)
        {
            _playbackClock.Reset();
        }

        _playbackClock.Update(player.Position, player.Playing);
        _sessionKey = currentSessionKey;
        _lastRawPositionSeconds = player.Position;
        return sessionChanged;
    }

    private void ResetClockState()
    {
        _playbackClock.Reset();
        _sessionKey = null;
        _lastRawPositionSeconds = null;
        _sessionChangedAt = DateTimeOffset.UtcNow;
    }

    private async Task<LyricsDocument?> ResolveCurrentDocumentAsync(
        LyricsDocument? incomingDocument,
        PlayerState player,
        bool sessionChanged,
        CancellationToken cancellationToken)
    {
        if (sessionChanged)
        {
            _currentDocument = null;
        }

        // Only accept the latest-written cache file if it plausibly belongs to the current
        // track. Apple Music also writes/prefetches cache files for *other* songs, so trusting
        // recency alone shows the wrong lyrics. When it doesn't match, fall back to the
        // duration+title matcher which scans every cache file for the best fit.
        if (incomingDocument is not null &&
            incomingDocument.UpdatedAt >= _sessionChangedAt.AddSeconds(-1) &&
            MatchesPlayer(incomingDocument, player))
        {
            _currentDocument = incomingDocument;
        }
        else if (_currentDocument is null &&
                 _lyricsProvider is IPlayerMatchedLyricsProvider matchedLyricsProvider)
        {
            var matchedDocument = await matchedLyricsProvider.FindBestLyricsAsync(player, cancellationToken).ConfigureAwait(false);
            if (matchedDocument is not null)
            {
                _currentDocument = matchedDocument;
            }
        }

        if (_currentDocument is null &&
            incomingDocument is not null &&
            _allowStartupDocumentFallback &&
            incomingDocument.UpdatedAt >= DateTimeOffset.UtcNow.Subtract(StartupDocumentGrace) &&
            MatchesPlayer(incomingDocument, player))
        {
            _currentDocument = incomingDocument;
        }

        return _currentDocument;
    }

    // True when the lyric document's total duration is close enough to the playing track's
    // duration to be considered the same song. Unknown durations are treated as a match so we
    // never reject a valid file that simply lacks duration metadata.
    private static bool MatchesPlayer(LyricsDocument document, PlayerState player)
    {
        const double toleranceSeconds = 6.0;

        if (player.Duration <= 0)
        {
            return true;
        }

        var documentDuration = document.DurationSeconds
            ?? (document.Lines.Count > 0 ? document.Lines.Max(line => line.End) : 0.0);
        if (documentDuration <= 0)
        {
            return true;
        }

        return Math.Abs(documentDuration - player.Duration) <= toleranceSeconds;
    }

    private static string BuildSessionKey(PlayerState player)
    {
        return string.Join(
            "|",
            player.SourceAppId ?? string.Empty,
            player.Artist ?? string.Empty,
            player.Title ?? string.Empty,
            player.Album ?? string.Empty,
            Math.Round(player.Duration, 3).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
    }
}
