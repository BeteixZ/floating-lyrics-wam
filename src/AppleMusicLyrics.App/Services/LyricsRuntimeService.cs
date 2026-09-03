using AppleMusicLyrics.Core.Abstractions;
using AppleMusicLyrics.Core.Matching;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Sync;

namespace AppleMusicLyrics.App.Services;

public sealed class LyricsRuntimeService
{
    private const int MaxCatalogAttempts = 3;
    private const int MaxExternalAttempts = 3;

    private readonly ILyricsDocumentProvider _lyricsProvider;
    private readonly IPlayerSessionProvider _playerSessionProvider;
    private readonly ICatalogSongResolver? _catalogResolver;
    private readonly LyricsSynchronizer _synchronizer;
    private readonly PlaybackClock _playbackClock;
    private readonly TimeProvider _timeProvider;
    private FrameBasis _frameBasis;
    private double _lyricsOffsetSeconds;
    private LyricsDocument? _currentDocument;
    private string? _sessionKey;
    private DateTimeOffset _sessionChangedAt = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastScanAt = DateTimeOffset.MinValue;
    private double? _lastRawPositionSeconds;
    private double _matchedAgainstDuration;
    private int _catalogAttemptCount;
    private bool _catalogLookupComplete;
    private DateTimeOffset _catalogNextAttemptAt = DateTimeOffset.MinValue;
    private IReadOnlyList<string> _catalogLyricsIds = Array.Empty<string>();
    private Task<CatalogFetchResult>? _catalogFetch;
    private CancellationTokenSource? _catalogFetchCancellation;
    private string? _catalogFetchSessionKey;
    private Task<ExternalFetchResult>? _externalFetch;
    private CancellationTokenSource? _externalFetchCancellation;
    private string? _externalFetchSessionKey;
    private LyricsDocument? _externalDocument;
    private int _externalAttemptCount;
    private bool _externalLookupComplete;
    private DateTimeOffset _externalNextAttemptAt = DateTimeOffset.MinValue;
    private LyricsResolution _resolution = LyricsResolution.NoPlayer;

    public LyricsRuntimeService(
        ILyricsDocumentProvider lyricsProvider,
        IPlayerSessionProvider playerSessionProvider,
        LyricsSynchronizer synchronizer,
        PlaybackClock? playbackClock = null,
        double lyricsOffsetSeconds = 0.0,
        ICatalogSongResolver? catalogResolver = null,
        TimeProvider? timeProvider = null)
    {
        _lyricsProvider = lyricsProvider;
        _playerSessionProvider = playerSessionProvider;
        _synchronizer = synchronizer;
        _playbackClock = playbackClock ?? new PlaybackClock();
        _lyricsOffsetSeconds = lyricsOffsetSeconds;
        _catalogResolver = catalogResolver;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _frameBasis = new FrameBasis(CreateNoPlayerSnapshot(), _timeProvider.GetTimestamp());
    }

    public double LyricsOffsetSeconds
    {
        get => _lyricsOffsetSeconds;
        set => _lyricsOffsetSeconds = value;
    }

    /// <summary>
    /// Whether to honour the <c>lyricOffset</c> Apple ships inside a TTML document.
    /// </summary>
    public bool ApplyNativeLyricOffset { get; set; } = true;

    /// <summary>
    /// Allows duration-only or otherwise unverifiable local candidates to be displayed. Disabled
    /// by default because a missing lyric is safer than confidently showing another song.
    /// </summary>
    public bool AllowLowConfidenceLyrics { get; set; }

    /// <summary>
    /// Consulted in order, and only for songs Apple's own cache has nothing for. These reach the
    /// network, so they are fetched on a background task rather than inside the poll: a lyrics
    /// service being slow must never stall the position updates driving the display.
    /// </summary>
    public IReadOnlyList<IExternalLyricsProvider> ExternalLyricsProviders { get; init; } = Array.Empty<IExternalLyricsProvider>();

    /// <summary>
    /// How long after a track change we keep re-evaluating which cached file belongs to the new
    /// song. Apple writes the real file at (or just after) the switch and also prefetches the
    /// <em>next</em> track's file; once this window closes the chosen document is frozen, so a
    /// later prefetch can no longer replace the lyrics mid-song.
    /// </summary>
    public TimeSpan MatchWindow { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// While no document has been chosen we keep looking indefinitely — the cache file can land
    /// seconds late — but at this interval rather than on every player poll.
    /// </summary>
    public TimeSpan IdleRescanInterval { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan CatalogRetryBaseDelay { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan ExternalRetryBaseDelay { get; init; } = TimeSpan.FromSeconds(2);

    public async Task<RuntimeSnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        var rawPlayer = await _playerSessionProvider.GetCurrentPlayerStateAsync(cancellationToken).ConfigureAwait(false);
        if (rawPlayer is null)
        {
            ResetClockState();
            _currentDocument = null;
            return PublishFrameBasis(CreateNoPlayerSnapshot());
        }

        var sessionChanged = UpdateClock(rawPlayer);
        var document = await ResolveCurrentDocumentAsync(rawPlayer, sessionChanged, cancellationToken).ConfigureAwait(false);

        // Apple's own cache is authoritative when it has the song, so an external source is only
        // consulted once the local search has come up empty.
        document ??= ResolveExternalDocument(rawPlayer);

        var estimatedPosition = _playbackClock.GetEstimatedPosition();
        if (rawPlayer.Duration > 0)
        {
            estimatedPosition = Math.Clamp(estimatedPosition, 0, rawPlayer.Duration);
        }

        var estimatedPlayer = rawPlayer with
        {
            Position = estimatedPosition,
        };

        var activeLyric = _synchronizer.Resolve(document, estimatedPlayer, _lyricsOffsetSeconds, ApplyNativeLyricOffset);
        return PublishFrameBasis(new RuntimeSnapshot(
            document,
            estimatedPlayer,
            activeLyric,
            _resolution,
            RawPositionSeconds: rawPlayer.Position,
            EstimatedPositionSeconds: estimatedPosition));
    }

    /// <summary>
    /// Creates a visual-frame snapshot from the last completed poll without performing media,
    /// disk, catalog, or network I/O. The immutable basis keeps a track's player, lyrics, and
    /// resolution coherent while a later asynchronous poll is still in progress.
    /// </summary>
    public RuntimeSnapshot GetFrameSnapshot()
    {
        var basis = Volatile.Read(ref _frameBasis);
        var snapshot = basis.Snapshot;
        if (snapshot.Player is null || snapshot.EstimatedPositionSeconds is not double estimatedPosition)
        {
            return snapshot;
        }

        if (snapshot.Player.Playing)
        {
            estimatedPosition += Math.Max(
                0,
                _timeProvider.GetElapsedTime(basis.CapturedTimestamp, _timeProvider.GetTimestamp()).TotalSeconds);
        }

        if (snapshot.Player.Duration > 0)
        {
            estimatedPosition = Math.Clamp(estimatedPosition, 0, snapshot.Player.Duration);
        }

        var estimatedPlayer = snapshot.Player with { Position = estimatedPosition };
        return snapshot with
        {
            Player = estimatedPlayer,
            ActiveLyric = _synchronizer.Resolve(
                snapshot.Document,
                estimatedPlayer,
                _lyricsOffsetSeconds,
                ApplyNativeLyricOffset),
            EstimatedPositionSeconds = estimatedPosition,
        };
    }

    private RuntimeSnapshot PublishFrameBasis(RuntimeSnapshot snapshot)
    {
        Volatile.Write(
            ref _frameBasis,
            new FrameBasis(snapshot, _timeProvider.GetTimestamp()));
        return snapshot;
    }

    private static RuntimeSnapshot CreateNoPlayerSnapshot()
    {
        return new RuntimeSnapshot(
            null,
            null,
            new ActiveLyricState(null, null, null, null),
            LyricsResolution.NoPlayer);
    }

    private bool UpdateClock(PlayerState player)
    {
        var sessionChanged = false;
        var currentSessionKey = BuildSessionKey(player);
        if (!string.Equals(_sessionKey, currentSessionKey, StringComparison.Ordinal))
        {
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
        _matchedAgainstDuration = 0;
        ResetCatalogLookup();
        CancelExternalFetch();
        _resolution = LyricsResolution.NoPlayer;
    }

    private async Task<LyricsDocument?> ResolveCurrentDocumentAsync(
        PlayerState player,
        bool sessionChanged,
        CancellationToken cancellationToken)
    {
        if (sessionChanged)
        {
            _currentDocument = null;
            _matchedAgainstDuration = 0;
            ResetCatalogLookup();
            _lastScanAt = DateTimeOffset.MinValue;
            CancelExternalFetch();
            _resolution = CreateResolution(
                LyricsResolutionStatus.SearchingLocal,
                "Track changed; searching the Apple Music lyric cache.");
        }

        if (_lyricsProvider is not IPlayerMatchedLyricsProvider matchedLyricsProvider)
        {
            return await ResolveWithoutMatchingAsync(player, cancellationToken).ConfigureAwait(false);
        }

        // SMTC publishes the new title and the new timeline independently, so right after a track
        // change the duration is briefly 0 or still the previous song's. Matching on that produces
        // a confident wrong answer, so hold whatever we have until it settles.
        if (player.Duration <= 0)
        {
            _resolution = CreateResolution(
                LyricsResolutionStatus.WaitingForMetadata,
                "Waiting for Apple Music to publish a stable track duration.");
            return _currentDocument;
        }

        // The duration arriving (or correcting itself) after the title means the input we matched
        // against has changed — reopen the window and decide again.
        if (_matchedAgainstDuration > 0 && Math.Abs(player.Duration - _matchedAgainstDuration) > 1.0)
        {
            _currentDocument = null;
            ResetCatalogLookup();
            _sessionChangedAt = DateTimeOffset.UtcNow;
            _resolution = CreateResolution(
                LyricsResolutionStatus.SearchingLocal,
                "Track duration changed; re-evaluating lyric candidates.");
        }

        var now = DateTimeOffset.UtcNow;
        var withinMatchWindow = now - _sessionChangedAt <= MatchWindow;
        if (_currentDocument is not null && !withinMatchWindow)
        {
            return _currentDocument;
        }

        if (!withinMatchWindow && now - _lastScanAt < IdleRescanInterval)
        {
            return _currentDocument;
        }

        _lastScanAt = now;
        _resolution = CreateResolution(
            LyricsResolutionStatus.SearchingLocal,
            "Scanning the Apple Music lyric cache.");
        var candidates = await matchedLyricsProvider.FindCandidatesAsync(player, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            _resolution = CreateResolution(
                LyricsResolutionStatus.Unavailable,
                "No local lyric candidate matched the active track.");
            return _currentDocument;
        }

        var selection = await SelectCandidateAsync(candidates, player, cancellationToken).ConfigureAwait(false);
        _resolution = selection.Resolution;
        if (selection.Document is not null)
        {
            _currentDocument = selection.Document;
            _matchedAgainstDuration = player.Duration;
            CancelExternalFetch();
        }

        return _currentDocument;
    }

    private async Task<CandidateSelection> SelectCandidateAsync(
        IReadOnlyList<LyricsMatch> candidates,
        PlayerState player,
        CancellationToken cancellationToken)
    {
        var plausible = candidates
            .Where(LyricsMatchPolicy.IsPlausible)
            .ToArray();

        // One file, and it agrees closely on length: duration is sufficient evidence.
        if (LyricsMatchPolicy.IsConfidentSingle(plausible))
        {
            var selected = plausible[0];
            return ResolvedSelection(
                candidates,
                selected,
                LyricsResolutionConfidence.High,
                LyricsResolutionSource.AppleMusicCache,
                "Selected the only local candidate with a close duration match.");
        }

        // Either several cached files fit this song's length or none fits well. Duration has run
        // out of discriminating power, so ask which catalog song is actually playing. The request
        // runs off the poll and every result is scoped to the track generation that started it.
        var catalogFailureSummary = await UpdateCatalogLookupAsync(player, candidates, cancellationToken).ConfigureAwait(false);

        if (_catalogLyricsIds.Count > 0)
        {
            foreach (var lyricsId in _catalogLyricsIds)
            {
                var hit = plausible.FirstOrDefault(candidate =>
                    string.Equals(candidate.Document.LyricsId, lyricsId, StringComparison.OrdinalIgnoreCase));
                if (hit is not null)
                {
                    return ResolvedSelection(
                        candidates,
                        hit,
                        LyricsResolutionConfidence.High,
                        LyricsResolutionSource.CatalogVerifiedCache,
                        "The Apple catalog verified the selected local lyric id.",
                        _catalogLyricsIds);
                }
            }

            // The catalog named this song's ids and none of them is cached. Every candidate that
            // carries a comparable "AP_<catalog id>" is therefore positively a different song.
            var unrulable = plausible
                .Where(candidate => CatalogLyricsId.ToCatalogId(candidate.Document.LyricsId) is null)
                .ToArray();

            if (unrulable.Length > 0)
            {
                return LowConfidenceSelection(
                    candidates,
                    unrulable[0],
                    "Catalog ids rejected comparable candidates; the remaining local id cannot be verified.",
                    _catalogLyricsIds);
            }

            return new CandidateSelection(
                null,
                CreateResolution(
                    LyricsResolutionStatus.Unavailable,
                    "The catalog identified the track, but its lyric file is not cached.",
                    candidates,
                    catalogLyricsIds: _catalogLyricsIds));
        }

        if (plausible.Length > 0)
        {
            var bestCandidate = plausible[0];
            if (LyricsMatchPolicy.IsMediumConfidenceSingle(plausible))
            {
                return ResolvedSelection(
                    candidates,
                    bestCandidate,
                    LyricsResolutionConfidence.Medium,
                    LyricsResolutionSource.AppleMusicCache,
                    "Selected the only local candidate within the medium duration window.");
            }

            if (LyricsMatchPolicy.HasClearWinner(plausible))
            {
                return ResolvedSelection(
                    candidates,
                    bestCandidate,
                    LyricsResolutionConfidence.Medium,
                    LyricsResolutionSource.AppleMusicCache,
                    "Selected the best local candidate with a significant score lead.");
            }

            return LowConfidenceSelection(
                candidates,
                plausible[0],
                catalogFailureSummary ?? (_catalogResolver is null
                    ? "The closest duration candidate has no catalog verification."
                    : _catalogLookupComplete
                        ? "Catalog verification found no ids; the closest duration candidate remains unverified."
                        : "Catalog verification will retry; the closest duration candidate remains unverified."));
        }

        return new CandidateSelection(
            null,
            CreateResolution(
                LyricsResolutionStatus.Unavailable,
                "Local cache files were found, but all were outside the duration tolerance.",
                candidates));
    }

    private async Task<string?> UpdateCatalogLookupAsync(
        PlayerState player,
        IReadOnlyList<LyricsMatch> candidates,
        CancellationToken cancellationToken)
    {
        if (_catalogResolver is null || _catalogLookupComplete)
        {
            return null;
        }

        var completedAtEntry = _catalogFetch is { IsCompleted: true };
        if (completedAtEntry)
        {
            var summary = await AdoptCatalogResultAsync(cancellationToken).ConfigureAwait(false);
            // A transient completion gets its next attempt on the next player poll, even when tests
            // configure a zero delay. This prevents a synchronous failure from consuming all tries.
            if (summary is not null || _catalogLookupComplete)
            {
                return summary;
            }
        }

        if (_catalogFetch is not null)
        {
            _resolution = CreateResolution(
                LyricsResolutionStatus.VerifyingCatalog,
                $"Waiting for catalog attempt {_catalogAttemptCount}/{MaxCatalogAttempts}.",
                candidates);
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (_catalogAttemptCount >= MaxCatalogAttempts || now < _catalogNextAttemptAt)
        {
            return null;
        }

        _catalogAttemptCount++;
        _catalogFetchSessionKey = _sessionKey;
        _catalogFetchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _resolution = CreateResolution(
            LyricsResolutionStatus.VerifyingCatalog,
            $"Checking the Apple catalog (attempt {_catalogAttemptCount}/{MaxCatalogAttempts}).",
            candidates);
        _catalogFetch = FetchCatalogAsync(player, _catalogFetchCancellation.Token);

        return _catalogFetch.IsCompleted
            ? await AdoptCatalogResultAsync(cancellationToken).ConfigureAwait(false)
            : null;
    }

    private async Task<string?> AdoptCatalogResultAsync(CancellationToken cancellationToken)
    {
        if (_catalogFetch is not { IsCompleted: true } fetch)
        {
            return null;
        }

        CatalogFetchResult result;
        try
        {
            result = await fetch.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _catalogFetchCancellation?.Dispose();
            _catalogFetchCancellation = null;
            _catalogFetch = null;
        }

        if (!string.Equals(_catalogFetchSessionKey, _sessionKey, StringComparison.Ordinal))
        {
            return null;
        }

        if (!result.IsTransientFailure)
        {
            _catalogLyricsIds = result.LyricsIds;
            _catalogLookupComplete = true;
            return null;
        }

        var summary = $"Catalog attempt {_catalogAttemptCount} failed. {result.Detail}";
        if (_catalogAttemptCount >= MaxCatalogAttempts)
        {
            _catalogLookupComplete = true;
        }
        else
        {
            _catalogNextAttemptAt = DateTimeOffset.UtcNow
                + GetRetryDelay(CatalogRetryBaseDelay, _catalogAttemptCount);
        }

        return summary;
    }

    private async Task<CatalogFetchResult> FetchCatalogAsync(
        PlayerState player,
        CancellationToken cancellationToken)
    {
        try
        {
            var lyricsIds = await _catalogResolver!
                .ResolveLyricsIdCandidatesAsync(player, cancellationToken)
                .ConfigureAwait(false);
            return new CatalogFetchResult(lyricsIds, false, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CatalogFetchResult(
                Array.Empty<string>(),
                true,
                ex.GetType().Name);
        }
    }

    /// <summary>
    /// Adopts a finished background fetch and starts a due attempt without ever awaiting network
    /// work on the player poll. Completed misses are final; transient failures retry at most three
    /// times and every result is scoped to the track generation that started it.
    /// </summary>
    private LyricsDocument? ResolveExternalDocument(PlayerState player)
    {
        if (ExternalLyricsProviders.Count == 0)
        {
            return null;
        }

        if (_externalFetch is { IsCompleted: true })
        {
            if (_externalFetch.Status == TaskStatus.RanToCompletion &&
                string.Equals(_externalFetchSessionKey, _sessionKey, StringComparison.Ordinal))
            {
                var result = _externalFetch.Result;
                if (result.Document is not null)
                {
                    _externalDocument = result.Document;
                    _externalLookupComplete = true;
                    _resolution = CarryCandidateEvidence(
                        LyricsResolutionStatus.Resolved,
                        LyricsResolutionConfidence.Medium,
                        LyricsResolutionSource.ExternalProvider,
                        "An external lyric provider returned timed lyrics.");
                }
                else if (result.IsTransientFailure && _externalAttemptCount < MaxExternalAttempts)
                {
                    _externalNextAttemptAt = DateTimeOffset.UtcNow
                        + GetRetryDelay(ExternalRetryBaseDelay, _externalAttemptCount);
                    _resolution = CarryCandidateEvidence(
                        LyricsResolutionStatus.FetchingExternal,
                        LyricsResolutionConfidence.None,
                        LyricsResolutionSource.None,
                        $"External attempt {_externalAttemptCount} failed; retry scheduled. {result.Detail}");
                }
                else
                {
                    _externalLookupComplete = true;
                    _resolution = CarryCandidateEvidence(
                        LyricsResolutionStatus.Unavailable,
                        LyricsResolutionConfidence.None,
                        LyricsResolutionSource.None,
                        result.IsTransientFailure
                            ? $"External providers failed after {_externalAttemptCount} attempts. {result.Detail}"
                            : "External lyric providers completed with no timed lyrics.");
                }
            }

            _externalFetchCancellation?.Dispose();
            _externalFetchCancellation = null;
            _externalFetch = null;
        }

        if (_externalDocument is not null)
        {
            return _externalDocument;
        }

        var now = DateTimeOffset.UtcNow;
        if (!_externalLookupComplete &&
            _externalFetch is null &&
            _externalAttemptCount < MaxExternalAttempts &&
            now >= _externalNextAttemptAt &&
            player.Duration > 0 &&
            !string.IsNullOrWhiteSpace(player.Title))
        {
            _externalAttemptCount++;
            _externalFetchSessionKey = _sessionKey;
            _externalFetchCancellation = new CancellationTokenSource();
            _resolution = CarryCandidateEvidence(
                LyricsResolutionStatus.FetchingExternal,
                LyricsResolutionConfidence.None,
                LyricsResolutionSource.None,
                $"Querying external lyric providers (attempt {_externalAttemptCount}/{MaxExternalAttempts}).");
            _externalFetch = FetchExternalLyricsAsync(player, _externalFetchCancellation.Token);
        }
        else if (_externalFetch is not null)
        {
            _resolution = CarryCandidateEvidence(
                LyricsResolutionStatus.FetchingExternal,
                LyricsResolutionConfidence.None,
                LyricsResolutionSource.None,
                $"Waiting for external attempt {_externalAttemptCount}/{MaxExternalAttempts}.");
        }

        return null;
    }

    private async Task<ExternalFetchResult> FetchExternalLyricsAsync(
        PlayerState player,
        CancellationToken cancellationToken)
    {
        var transientFailures = new List<string>();
        foreach (var provider in ExternalLyricsProviders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var document = await provider.FetchAsync(player, cancellationToken).ConfigureAwait(false);
                if (document is not null && document.Lines.Count > 0)
                {
                    return new ExternalFetchResult(document, false, null);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                transientFailures.Add($"{provider.Name}: {ex.GetType().Name}");
            }
        }

        return transientFailures.Count > 0
            ? new ExternalFetchResult(null, true, string.Join(", ", transientFailures))
            : new ExternalFetchResult(null, false, null);
    }

    private void CancelExternalFetch()
    {
        _externalFetchCancellation?.Cancel();
        _externalFetchCancellation?.Dispose();
        _externalFetchCancellation = null;
        _externalFetch = null;
        _externalFetchSessionKey = null;
        _externalDocument = null;
        _externalAttemptCount = 0;
        _externalLookupComplete = false;
        _externalNextAttemptAt = DateTimeOffset.MinValue;
    }

    private async Task<LyricsDocument?> ResolveWithoutMatchingAsync(
        PlayerState player,
        CancellationToken cancellationToken)
    {
        var incomingDocument = await _lyricsProvider.GetLatestLyricsAsync(cancellationToken).ConfigureAwait(false);
        if (incomingDocument is not null && MatchesPlayer(incomingDocument, player))
        {
            _currentDocument = incomingDocument;
            _resolution = new LyricsResolution(
                LyricsResolutionStatus.Resolved,
                LyricsResolutionConfidence.High,
                LyricsResolutionSource.AppleMusicCache,
                "The lyrics provider returned a document matching the active duration.",
                _sessionKey);
        }
        else
        {
            _resolution = CreateResolution(
                LyricsResolutionStatus.Unavailable,
                "The lyrics provider did not return a document matching the active track.");
        }

        return _currentDocument;
    }

    private CandidateSelection LowConfidenceSelection(
        IReadOnlyList<LyricsMatch> candidates,
        LyricsMatch candidate,
        string evidenceSummary,
        IReadOnlyList<string>? catalogLyricsIds = null)
    {
        var selection = ResolvedSelection(
            candidates,
            candidate,
            LyricsResolutionConfidence.Low,
            LyricsResolutionSource.AppleMusicCache,
            AllowLowConfidenceLyrics
                ? $"Low-confidence display is enabled. {evidenceSummary}"
                : $"Held back a low-confidence local candidate. {evidenceSummary}",
            catalogLyricsIds);

        return AllowLowConfidenceLyrics
            ? selection
            : new CandidateSelection(
                null,
                selection.Resolution with { Status = LyricsResolutionStatus.Unavailable });
    }

    private CandidateSelection ResolvedSelection(
        IReadOnlyList<LyricsMatch> candidates,
        LyricsMatch selected,
        LyricsResolutionConfidence confidence,
        LyricsResolutionSource source,
        string summary,
        IReadOnlyList<string>? catalogLyricsIds = null)
    {
        return new CandidateSelection(
            selected.Document,
            new LyricsResolution(
                LyricsResolutionStatus.Resolved,
                confidence,
                source,
                summary,
                _sessionKey,
                candidates.Count,
                BuildEvidence(candidates, selected, catalogLyricsIds)));
    }

    private LyricsResolution CreateResolution(
        LyricsResolutionStatus status,
        string summary,
        IReadOnlyList<LyricsMatch>? candidates = null,
        IReadOnlyList<string>? catalogLyricsIds = null)
    {
        return new LyricsResolution(
            status,
            LyricsResolutionConfidence.None,
            LyricsResolutionSource.None,
            summary,
            _sessionKey,
            candidates?.Count ?? 0,
            candidates is null ? null : BuildEvidence(candidates, null, catalogLyricsIds));
    }

    private LyricsResolution CarryCandidateEvidence(
        LyricsResolutionStatus status,
        LyricsResolutionConfidence confidence,
        LyricsResolutionSource source,
        string summary)
    {
        return new LyricsResolution(
            status,
            confidence,
            source,
            summary,
            _sessionKey,
            _resolution.CandidateCount,
            _resolution.CandidateEvidence);
    }

    private static IReadOnlyList<LyricsMatchEvidence> BuildEvidence(
        IReadOnlyList<LyricsMatch> candidates,
        LyricsMatch? selected,
        IReadOnlyList<string>? catalogLyricsIds)
    {
        return candidates
            .Select(candidate => new LyricsMatchEvidence(
                candidate.Document.LyricsId ?? "(missing id)",
                candidate.Score,
                candidate.DurationDelta,
                DescribeCandidate(candidate, selected, catalogLyricsIds)))
            .ToArray();
    }

    private static string DescribeCandidate(
        LyricsMatch candidate,
        LyricsMatch? selected,
        IReadOnlyList<string>? catalogLyricsIds)
    {
        if (ReferenceEquals(candidate, selected) ||
            (selected is not null && string.Equals(
                candidate.Document.SourceFile,
                selected.Document.SourceFile,
                StringComparison.OrdinalIgnoreCase)))
        {
            return "Selected.";
        }

        if (candidate.DurationDelta > LyricsMatchPolicy.DurationToleranceSeconds)
        {
            return "Rejected: duration outside tolerance.";
        }

        if (catalogLyricsIds is { Count: > 0 } &&
            CatalogLyricsId.ToCatalogId(candidate.Document.LyricsId) is not null &&
            !catalogLyricsIds.Contains(candidate.Document.LyricsId, StringComparer.OrdinalIgnoreCase))
        {
            return "Rejected: catalog identified a different song.";
        }

        return "Not selected.";
    }

    private void ResetCatalogLookup()
    {
        _catalogFetchCancellation?.Cancel();
        _catalogFetchCancellation?.Dispose();
        _catalogFetchCancellation = null;
        _catalogFetch = null;
        _catalogFetchSessionKey = null;
        _catalogAttemptCount = 0;
        _catalogLookupComplete = false;
        _catalogNextAttemptAt = DateTimeOffset.MinValue;
        _catalogLyricsIds = Array.Empty<string>();
    }

    private static TimeSpan GetRetryDelay(TimeSpan baseDelay, int completedAttemptCount)
    {
        if (baseDelay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var multiplier = Math.Pow(2, Math.Max(0, completedAttemptCount - 1));
        return TimeSpan.FromTicks((long)Math.Min(TimeSpan.MaxValue.Ticks, baseDelay.Ticks * multiplier));
    }

    // True when the lyric document's total duration is close enough to the playing track's
    // duration to be considered the same song. Unknown durations are treated as a match so we
    // never reject a valid file that simply lacks duration metadata.
    private static bool MatchesPlayer(LyricsDocument document, PlayerState player)
    {
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

        return Math.Abs(documentDuration - player.Duration) <= LyricsMatchPolicy.DurationToleranceSeconds;
    }

    // Duration is deliberately absent: SMTC updates the metadata and the timeline separately, and
    // including a duration that flickers to 0 mid-switch would report a track change twice and
    // reset the playback clock for no reason.
    private static string BuildSessionKey(PlayerState player)
    {
        return string.Join(
            "|",
            player.SourceAppId ?? string.Empty,
            player.Artist ?? string.Empty,
            player.Title ?? string.Empty,
            player.Album ?? string.Empty);
    }

    private sealed record FrameBasis(RuntimeSnapshot Snapshot, long CapturedTimestamp);

    private sealed record CatalogFetchResult(
        IReadOnlyList<string> LyricsIds,
        bool IsTransientFailure,
        string? Detail);

    private sealed record ExternalFetchResult(
        LyricsDocument? Document,
        bool IsTransientFailure,
        string? Detail);

    private sealed record CandidateSelection(LyricsDocument? Document, LyricsResolution Resolution);
}
