using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using AppleMusicLyrics.Core.Abstractions;
using AppleMusicLyrics.Core.Matching;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Sync;

namespace AppleMusicLyrics.Application.Services;

public sealed class LyricsRuntimeService : IAsyncDisposable
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
    private DateTimeOffset _sessionChangedAt;
    private DateTimeOffset _lastScanAt = DateTimeOffset.MinValue;
    private int _unproductiveIdleRescans;
    private double? _lastRawPositionSeconds;
    private double _matchedAgainstDuration;
    private int _catalogAttemptCount;
    private bool _catalogLookupComplete;
    private DateTimeOffset _catalogNextAttemptAt = DateTimeOffset.MinValue;
    private IReadOnlyList<string> _catalogLyricsIds = Array.Empty<string>();
    private Task<CatalogFetchResult>? _catalogFetch;
    private CancellationTokenSource? _catalogFetchCancellation;
    private int _trackGeneration;
    private int _catalogFetchGeneration;
    private Task<ExternalFetchResult>? _externalFetch;
    private CancellationTokenSource? _externalFetchCancellation;
    private int _externalFetchGeneration;
    private LyricsDocument? _externalDocument;
    private int _externalAttemptCount;
    private bool _externalLookupComplete;
    private DateTimeOffset _externalNextAttemptAt = DateTimeOffset.MinValue;
    private ConcurrentQueue<ProviderDiagnosticUpdate>? _externalDiagnosticUpdates;
    private ExternalLyricsDiagnostic[] _fallbackDiagnostics = [];
    private ExternalLyricsDiagnostic[] _enrichmentDiagnostics = [];
    private ExternalLyricsQueryState _fallbackState;
    private ExternalLyricsQueryState _enrichmentState;
    private EnrichmentScope? _enrichmentScope;
    private EnrichmentAttempt? _enrichmentAttempt;
    private EnrichmentUpdate? _enrichedLyrics;
    private int _enrichmentAttemptCount;
    private bool _enrichmentLookupComplete;
    private DateTimeOffset _enrichmentNextAttemptAt = DateTimeOffset.MinValue;
    private LyricsResolution _resolution = LyricsResolution.NoPlayer;
    private bool _disposed;

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
        _sessionChangedAt = _timeProvider.GetUtcNow();
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

    /// <summary>Keep the fast base result visible while looking for real word timings.</summary>
    public bool PreferWordTimings { get; set; }

    /// <summary>Keep the fast base result visible while looking for translated lyrics.</summary>
    public bool PreferTranslations { get; set; }

    /// <summary>
    /// Consulted in order for missing local lyrics, and optionally for richer word timings or
    /// translations. Network work never stalls the position updates driving the display.
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

    /// <summary>
    /// Each idle rescan that settles with nothing to show doubles the wait before the next one, up
    /// to this ceiling. A late cache file is still picked up, but an instrumental or an
    /// externally-sourced track no longer walks the whole cache directory every second.
    /// </summary>
    public TimeSpan MaxIdleRescanInterval { get; init; } = TimeSpan.FromSeconds(8);

    public TimeSpan CatalogRetryBaseDelay { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan ExternalRetryBaseDelay { get; init; } = TimeSpan.FromSeconds(2);

    public async Task<RuntimeSnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var rawPlayer = await _playerSessionProvider.GetCurrentPlayerStateAsync(cancellationToken).ConfigureAwait(false);
        if (rawPlayer is null)
        {
            ResetClockState();
            _currentDocument = null;
            return PublishFrameBasis(CreateNoPlayerSnapshot());
        }

        var sessionChanged = UpdateClock(rawPlayer);
        var document = await ResolveCurrentDocumentAsync(rawPlayer, sessionChanged, cancellationToken).ConfigureAwait(false);

        // Reusing this track's completed external result must not depend on a diagnostic status
        // left by the preceding poll. Catalog gating applies to starting a fresh lookup only.
        if (document is null && (_externalDocument is not null || CanStartOrAdoptExternalLookup()))
        {
            document = ResolveExternalDocument(rawPlayer);
        }

        document = ResolveEnrichedDocument(rawPlayer, document);

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
            EstimatedPositionSeconds: estimatedPosition,
            ExternalDiagnostics: CreateExternalDiagnostics(document)));
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
            _trackGeneration++;
            _sessionChangedAt = _timeProvider.GetUtcNow();
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
        if (_sessionKey is not null)
        {
            _trackGeneration++;
        }

        _playbackClock.Reset();
        _sessionKey = null;
        _lastRawPositionSeconds = null;
        _sessionChangedAt = _timeProvider.GetUtcNow();
        _matchedAgainstDuration = 0;
        ResetCatalogLookup();
        CancelExternalFetch();
        ResetEnrichmentLookup();
        _fallbackDiagnostics = [];
        _enrichmentDiagnostics = [];
        _fallbackState = ExternalLyricsQueryState.NotStarted;
        _enrichmentState = ExternalLyricsQueryState.NotStarted;
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
            _unproductiveIdleRescans = 0;
            CancelExternalFetch();
            ResetEnrichmentLookup();
            _fallbackDiagnostics = CreateProviderDiagnostics(ExternalLyricsQueryStage.Fallback);
            _enrichmentDiagnostics = CreateProviderDiagnostics(ExternalLyricsQueryStage.Enrichment);
            _fallbackState = ExternalLyricsQueryState.NotStarted;
            _enrichmentState = ExternalLyricsQueryState.NotStarted;
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
        // against has changed — reopen the window and decide again. Clearing the matched duration
        // makes this fire once per change; otherwise every later poll would reset the lookups
        // again and a catalog or external answer for the corrected duration could never land.
        if (_matchedAgainstDuration > 0 && Math.Abs(player.Duration - _matchedAgainstDuration) > 1.0)
        {
            _currentDocument = null;
            _matchedAgainstDuration = 0;
            ResetCatalogLookup();
            CancelExternalFetch();
            _sessionChangedAt = _timeProvider.GetUtcNow();
            _unproductiveIdleRescans = 0;
            _resolution = CreateResolution(
                LyricsResolutionStatus.SearchingLocal,
                "Track duration changed; re-evaluating lyric candidates.");
        }

        var now = _timeProvider.GetUtcNow();
        var withinMatchWindow = now - _sessionChangedAt <= MatchWindow;
        if (_currentDocument is not null && !withinMatchWindow)
        {
            return _currentDocument;
        }

        if (!withinMatchWindow && now - _lastScanAt < GetIdleRescanDelay())
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
            RecordUnproductiveRescan(withinMatchWindow);
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
        else
        {
            RecordUnproductiveRescan(withinMatchWindow);
        }

        return _currentDocument;
    }

    private TimeSpan GetIdleRescanDelay()
    {
        var delay = GetRetryDelay(IdleRescanInterval, _unproductiveIdleRescans + 1);
        return delay < MaxIdleRescanInterval ? delay : MaxIdleRescanInterval;
    }

    // Only a settled miss backs off. While a catalog answer is pending or a retry is scheduled, the
    // rescan is also what adopts that answer, so it keeps the base cadence.
    private void RecordUnproductiveRescan(bool withinMatchWindow)
    {
        var localLookupSettled = _catalogFetch is null
            && (_catalogAttemptCount == 0 || _catalogLookupComplete);
        if (!withinMatchWindow && localLookupSettled && GetIdleRescanDelay() < MaxIdleRescanInterval)
        {
            _unproductiveIdleRescans++;
        }
    }

    private async Task<CandidateSelection> SelectCandidateAsync(
        IReadOnlyList<LyricsMatch> candidates,
        PlayerState player,
        CancellationToken cancellationToken)
    {
        var plausible = candidates
            .Where(LyricsMatchPolicy.IsPlausible)
            .ToArray();

        // One file, a close length, and title-bearing lyric content together are sufficient
        // evidence. Duration alone is not: a stale or prefetched lyric can have nearly the same
        // length as an instrumental track and must still go through catalog verification.
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

        // Do not turn an unverified duration score into a displayed lyric while the authoritative
        // catalog lookup is still running. In production that lookup is genuinely asynchronous;
        // falling through here used to replace VerifyingCatalog with a Medium result for one poll,
        // which was enough to show another same-length song's cached lyrics.
        if (_catalogFetch is not null)
        {
            return new CandidateSelection(null, _resolution);
        }

        if (_catalogLyricsIds.Count > 0)
        {
            foreach (var lyricsId in _catalogLyricsIds)
            {
                var hit = plausible.FirstOrDefault(candidate =>
                    CatalogLyricsId.Matches(candidate.Document.LyricsId, lyricsId));
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

        var now = _timeProvider.GetUtcNow();
        if (_catalogAttemptCount >= MaxCatalogAttempts || now < _catalogNextAttemptAt)
        {
            return null;
        }

        _catalogAttemptCount++;
        _catalogFetchGeneration = _trackGeneration;
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

        if (_catalogFetchGeneration != _trackGeneration)
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
            _catalogNextAttemptAt = _timeProvider.GetUtcNow()
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
    private bool CanStartOrAdoptExternalLookup()
    {
        if (ExternalLyricsProviders.Count == 0)
        {
            return false;
        }

        // An adopted external document belongs to this track until a local selection cancels it.
        // Idle rescans and a late catalog check report their own interim status, but must not
        // blank lyrics that are already on screen.
        if (_externalDocument is not null)
        {
            return true;
        }

        // No local candidates means no catalog attempt was needed. Once catalog verification did
        // start, wait for it to reach a terminal answer before allowing a community source to
        // replace an Apple-cache decision.
        return _resolution.Status is LyricsResolutionStatus.Unavailable or LyricsResolutionStatus.FetchingExternal
            && _catalogFetch is null
            && (_catalogAttemptCount == 0 || _catalogLookupComplete);
    }

    private LyricsDocument? ResolveExternalDocument(PlayerState player)
    {
        if (ExternalLyricsProviders.Count == 0)
        {
            return null;
        }

        DrainProviderDiagnostics(_externalDiagnosticUpdates, _fallbackDiagnostics);
        if (_externalFetch is { IsCompleted: true })
        {
            // Completion may race the preceding drain; no more writes can follow this drain.
            DrainProviderDiagnostics(_externalDiagnosticUpdates, _fallbackDiagnostics);
            if (_externalFetch.Status == TaskStatus.RanToCompletion &&
                _externalFetchGeneration == _trackGeneration)
            {
                var result = _externalFetch.Result;
                if (result.Document is not null)
                {
                    _externalDocument = result.Document;
                    _externalLookupComplete = true;
                    _fallbackState = ExternalLyricsQueryState.Satisfied;
                    MarkUnstartedProviders(_fallbackDiagnostics, ExternalLyricsQueryOutcome.Skipped,
                        ExternalLyricsDiagnosticReason.AlreadySatisfied);
                }
                else if (result.IsTransientFailure && _externalAttemptCount < MaxExternalAttempts)
                {
                    _externalNextAttemptAt = _timeProvider.GetUtcNow()
                        + GetRetryDelay(ExternalRetryBaseDelay, _externalAttemptCount);
                    _fallbackState = ExternalLyricsQueryState.RetryWaiting;
                    _resolution = CarryCandidateEvidence(
                        LyricsResolutionStatus.FetchingExternal,
                        LyricsResolutionConfidence.None,
                        LyricsResolutionSource.None,
                        $"External attempt {_externalAttemptCount} failed; retry scheduled. {result.Detail}");
                }
                else
                {
                    _externalLookupComplete = true;
                    _fallbackState = ExternalLyricsQueryState.Completed;
                    _resolution = CarryCandidateEvidence(
                        LyricsResolutionStatus.Unavailable,
                        LyricsResolutionConfidence.None,
                        LyricsResolutionSource.None,
                        result.IsTransientFailure
                            ? $"External providers failed after {_externalAttemptCount} attempts. {result.Detail}"
                            : "External lyric providers completed with no usable lyrics.");
                }
            }

            _externalFetchCancellation?.Dispose();
            _externalFetchCancellation = null;
            _externalFetch = null;
            _externalDiagnosticUpdates = null;
        }

        if (_externalDocument is not null)
        {
            // A later local rescan overwrites the resolution with its own miss; the external
            // document is still what is displayed, so report it again.
            if (_resolution is not
                {
                    Status: LyricsResolutionStatus.Resolved,
                    Source: LyricsResolutionSource.ExternalProvider,
                })
            {
                _resolution = CarryCandidateEvidence(
                    LyricsResolutionStatus.Resolved,
                    LyricsResolutionConfidence.Medium,
                    LyricsResolutionSource.ExternalProvider,
                    "An external lyric provider returned usable lyrics.");
            }
            return _externalDocument;
        }

        var now = _timeProvider.GetUtcNow();
        if (!_externalLookupComplete &&
            _externalFetch is null &&
            _externalAttemptCount < MaxExternalAttempts &&
            now >= _externalNextAttemptAt &&
            player.Duration > 0 &&
            !string.IsNullOrWhiteSpace(player.Title))
        {
            _externalAttemptCount++;
            _externalFetchGeneration = _trackGeneration;
            _externalFetchCancellation = new CancellationTokenSource();
            _fallbackDiagnostics = CreateProviderDiagnostics(ExternalLyricsQueryStage.Fallback, _externalAttemptCount);
            _externalDiagnosticUpdates = new ConcurrentQueue<ProviderDiagnosticUpdate>();
            _fallbackState = ExternalLyricsQueryState.Querying;
            _resolution = CarryCandidateEvidence(
                LyricsResolutionStatus.FetchingExternal,
                LyricsResolutionConfidence.None,
                LyricsResolutionSource.None,
                $"Querying external lyric providers (attempt {_externalAttemptCount}/{MaxExternalAttempts}).");
            _externalFetch = FetchExternalLyricsAsync(player, _externalFetchCancellation.Token,
                _externalDiagnosticUpdates, _externalAttemptCount);
            DrainProviderDiagnostics(_externalDiagnosticUpdates, _fallbackDiagnostics);
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
        CancellationToken cancellationToken,
        ConcurrentQueue<ProviderDiagnosticUpdate> diagnostics,
        int attempt)
    {
        var transientFailures = new List<string>();
        LyricsDocument? plainFallback = null;
        for (var index = 0; index < ExternalLyricsProviders.Count; index++)
        {
            var provider = ExternalLyricsProviders[index];
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostic = new ExternalLyricsDiagnostic(provider.Name, ExternalLyricsQueryStage.Fallback,
                ExternalLyricsQueryOutcome.Querying, attempt);
            diagnostics.Enqueue(new ProviderDiagnosticUpdate(index, diagnostic));

            try
            {
                var document = await provider.FetchAsync(player, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                diagnostics.Enqueue(new ProviderDiagnosticUpdate(index, DescribeDocument(diagnostic, document)));
                if (document is not null && document.Lines.Count > 0)
                {
                    if (string.Equals(document.Status, "plain", StringComparison.OrdinalIgnoreCase))
                    {
                        plainFallback ??= document;
                        continue;
                    }
                    return new ExternalFetchResult(document, false, null);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                diagnostics.Enqueue(new ProviderDiagnosticUpdate(index, DescribeFailure(diagnostic, ex)));
                transientFailures.Add($"{provider.Name}: {ex.GetType().Name}");
            }
        }

        return plainFallback is not null
            ? new ExternalFetchResult(plainFallback, false, null)
            : transientFailures.Count > 0
            ? new ExternalFetchResult(null, true, string.Join(", ", transientFailures))
            : new ExternalFetchResult(null, false, null);
    }

    private void CancelExternalFetch()
    {
        DrainProviderDiagnostics(_externalDiagnosticUpdates, _fallbackDiagnostics);
        if (_externalFetch is not null)
        {
            _fallbackState = ExternalLyricsQueryState.Canceled;
            MarkPendingProvidersCanceled(_fallbackDiagnostics);
        }
        _externalFetchCancellation?.Cancel();
        _externalFetchCancellation?.Dispose();
        _externalFetchCancellation = null;
        _externalFetch = null;
        _externalDiagnosticUpdates = null;
        _externalFetchGeneration = 0;
        _externalDocument = null;
        _externalAttemptCount = 0;
        _externalLookupComplete = false;
        _externalNextAttemptAt = DateTimeOffset.MinValue;
    }

    private LyricsDocument? ResolveEnrichedDocument(PlayerState player, LyricsDocument? baseDocument)
    {
        var scope = new EnrichmentScope(_trackGeneration, player.Duration, PreferWordTimings, PreferTranslations);
        if (_enrichmentScope != scope)
        {
            ResetEnrichmentLookup();
            _enrichmentScope = scope;
        }
        if ((!scope.Words && !scope.Translation) || ExternalLyricsProviders.Count == 0
            || !double.IsFinite(player.Duration) || player.Duration <= 0)
        {
            return baseDocument;
        }

        var selected = baseDocument;
        if (_enrichedLyrics is not null
            && (selected is null || IsRicherDocument(selected, _enrichedLyrics.Document, scope)))
        {
            selected = _enrichedLyrics.Document;
        }

        if (_enrichmentAttempt is { } attempt)
        {
            DrainProviderDiagnostics(attempt.Diagnostics, _enrichmentDiagnostics);
            while (attempt.Updates.TryDequeue(out var update))
            {
                if (selected is null || IsRicherDocument(selected, update.Document, scope))
                {
                    selected = update.Document;
                    _enrichedLyrics = update;
                }
            }

            if (attempt.Fetch.IsCompleted)
            {
                DrainProviderDiagnostics(attempt.Diagnostics, _enrichmentDiagnostics);
                // Completion can race the first drain. Once completed, no later update can be
                // enqueued, so drain once more before releasing this attempt's queue.
                while (attempt.Updates.TryDequeue(out var update))
                {
                    if (selected is null || IsRicherDocument(selected, update.Document, scope))
                    {
                        selected = update.Document;
                        _enrichedLyrics = update;
                    }
                }
                var transientFailure = attempt.Fetch.Status == TaskStatus.RanToCompletion
                    && attempt.Fetch.Result.IsTransientFailure;
                CancelEnrichmentAttempt();
                if (transientFailure && _enrichmentAttemptCount < MaxExternalAttempts
                    && selected is not null && !HasRequestedFeatures(selected, scope))
                {
                    _enrichmentNextAttemptAt = _timeProvider.GetUtcNow()
                        + GetRetryDelay(ExternalRetryBaseDelay, _enrichmentAttemptCount);
                    _enrichmentState = ExternalLyricsQueryState.RetryWaiting;
                }
                else
                {
                    _enrichmentLookupComplete = true;
                    _enrichmentState = ExternalLyricsQueryState.Completed;
                }
            }
        }

        if (selected is null)
        {
            return null;
        }
        if (HasRequestedFeatures(selected, scope))
        {
            CancelEnrichmentAttempt();
            _enrichmentLookupComplete = true;
            _enrichmentState = ExternalLyricsQueryState.Satisfied;
            MarkUnstartedProviders(_enrichmentDiagnostics, ExternalLyricsQueryOutcome.Skipped,
                ExternalLyricsDiagnosticReason.AlreadySatisfied);
        }
        else if (!_enrichmentLookupComplete && _enrichmentAttempt is null
            && _enrichmentAttemptCount < MaxExternalAttempts
            && _timeProvider.GetUtcNow() >= _enrichmentNextAttemptAt)
        {
            var cancellation = new CancellationTokenSource();
            var updates = new ConcurrentQueue<EnrichmentUpdate>();
            var diagnostics = new ConcurrentQueue<ProviderDiagnosticUpdate>();
            var initialDocument = selected;
            var token = cancellation.Token;
            _enrichmentAttemptCount++;
            _enrichmentDiagnostics = CreateProviderDiagnostics(ExternalLyricsQueryStage.Enrichment, _enrichmentAttemptCount);
            _enrichmentState = ExternalLyricsQueryState.Querying;
            var attemptNumber = _enrichmentAttemptCount;
            _enrichmentAttempt = new EnrichmentAttempt(cancellation, updates, diagnostics,
                Task.Run(() => FetchEnrichmentAsync(player, initialDocument, scope, updates, diagnostics, attemptNumber, token)));
        }

        if (_enrichedLyrics is not null && ReferenceEquals(selected, _enrichedLyrics.Document))
        {
            _resolution = CarryCandidateEvidence(LyricsResolutionStatus.Resolved,
                LyricsResolutionConfidence.Medium, LyricsResolutionSource.ExternalProvider,
                $"Using richer lyrics from {_enrichedLyrics.ProviderName}.");
        }
        if (!HasRequestedFeatures(selected, scope))
        {
            var missing = scope.Words && !HasUsableWords(selected)
                ? scope.Translation && !HasTranslation(selected) ? "word timings and translations" : "word timings"
                : "translations";
            var source = _enrichedLyrics is not null && ReferenceEquals(selected, _enrichedLyrics.Document)
                ? $"Lyrics from {_enrichedLyrics.ProviderName} remain visible" : "Existing lyrics remain visible";
            _resolution = _resolution with
            {
                Status = LyricsResolutionStatus.Resolved,
                Summary = _enrichmentLookupComplete
                    ? _enrichmentDiagnostics.Any(diagnostic => diagnostic.Outcome == ExternalLyricsQueryOutcome.Failed)
                        ? $"{source}; the missing {missing} remain unresolved because some source requests failed."
                        : $"{source}; checked sources did not provide an adoptable result for the missing {missing}."
                    : $"{source} while checking missing {missing}.",
            };
        }
        return selected;
    }

    private async Task<EnrichmentFetchResult> FetchEnrichmentAsync(PlayerState player, LyricsDocument initialDocument,
        EnrichmentScope scope, ConcurrentQueue<EnrichmentUpdate> updates,
        ConcurrentQueue<ProviderDiagnosticUpdate> diagnostics, int attempt, CancellationToken cancellationToken)
    {
        var best = initialDocument;
        var failed = false;
        for (var index = 0; index < ExternalLyricsProviders.Count; index++)
        {
            var provider = ExternalLyricsProviders[index];
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostic = new ExternalLyricsDiagnostic(provider.Name, ExternalLyricsQueryStage.Enrichment,
                ExternalLyricsQueryOutcome.Querying, attempt);
            diagnostics.Enqueue(new ProviderDiagnosticUpdate(index, diagnostic));
            try
            {
                // WaitAsync also scopes providers which fail to honor cancellation themselves.
                var fetch = provider.FetchAsync(player, cancellationToken);
                ObserveFault(fetch);
                var document = await fetch.WaitAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var resultDiagnostic = DescribeDocument(diagnostic, document);
                if (document is not null && IsRicherDocument(best, document, scope))
                {
                    diagnostics.Enqueue(new ProviderDiagnosticUpdate(index, resultDiagnostic));
                    best = document;
                    updates.Enqueue(new EnrichmentUpdate(document, provider.Name));
                    if (HasRequestedFeatures(document, scope))
                    {
                        return new EnrichmentFetchResult(false);
                    }
                }
                else
                {
                    if (document is { Lines.Count: > 0 })
                    {
                        resultDiagnostic = resultDiagnostic with
                        {
                            Outcome = ExternalLyricsQueryOutcome.Rejected,
                            Reason = DescribeRejection(best, document, scope),
                        };
                    }
                    diagnostics.Enqueue(new ProviderDiagnosticUpdate(index, resultDiagnostic));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                diagnostics.Enqueue(new ProviderDiagnosticUpdate(index, DescribeFailure(diagnostic, error)));
                failed = true;
            }
        }
        return new EnrichmentFetchResult(failed);
    }

    private static bool IsRicherDocument(LyricsDocument current, LyricsDocument candidate, EnrichmentScope scope)
    {
        if (candidate.Lines.Count == 0 || string.Equals(candidate.Status, "plain", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var currentWords = HasUsableWords(current);
        var candidateWords = HasUsableWords(candidate);
        var currentTranslation = HasTranslation(current);
        var candidateTranslation = HasTranslation(candidate);
        if ((scope.Words && currentWords && !candidateWords)
            || (scope.Translation && currentTranslation && !candidateTranslation))
        {
            return false;
        }
        return (scope.Words && !currentWords && candidateWords)
            || (scope.Translation && !currentTranslation && candidateTranslation);
    }

    private static bool HasRequestedFeatures(LyricsDocument document, EnrichmentScope scope) =>
        (!scope.Words || HasUsableWords(document)) && (!scope.Translation || HasTranslation(document));

    private static bool HasUsableWords(LyricsDocument document) => document.Lines.Any(HasUsableWords);

    private static bool HasUsableWords(LyricsLine line) => line.Words is { Count: > 0 } words
        && words.All(word => double.IsFinite(word.Begin) && double.IsFinite(word.End)
            && word.Begin >= 0 && word.End > word.Begin && !string.IsNullOrEmpty(word.Text))
        && string.Equals(string.Concat(words.Select(word => word.Text)).Trim(), line.Text.Trim(), StringComparison.Ordinal);

    private static bool HasTranslation(LyricsDocument document) => document.Lines.Any(HasMeaningfulTranslation);

    private static bool HasMeaningfulTranslation(LyricsLine line) => !string.IsNullOrWhiteSpace(line.Translation)
        && MetadataMatching.Normalize(line.Translation) != MetadataMatching.Normalize(line.Text);

    private ExternalLyricsDiagnostic[] CreateProviderDiagnostics(ExternalLyricsQueryStage stage, int attempt = 0) =>
        ExternalLyricsProviders.Select(provider => new ExternalLyricsDiagnostic(provider.Name, stage,
            ExternalLyricsQueryOutcome.NotStarted, attempt)).ToArray();

    private ExternalLyricsDiagnostics CreateExternalDiagnostics(LyricsDocument? document)
    {
        DrainProviderDiagnostics(_externalDiagnosticUpdates, _fallbackDiagnostics);
        if (_enrichmentAttempt is { } enrichment)
        {
            DrainProviderDiagnostics(enrichment.Diagnostics, _enrichmentDiagnostics);
        }
        return new ExternalLyricsDiagnostics(
            Array.AsReadOnly(_fallbackDiagnostics.Concat(_enrichmentDiagnostics).ToArray()),
            _fallbackState, _enrichmentState,
            _fallbackDiagnostics.Select(diagnostic => diagnostic.Attempt).DefaultIfEmpty().Max(),
            _enrichmentAttemptCount,
            PreferWordTimings && (document is null || !HasUsableWords(document)),
            PreferTranslations && (document is null || !HasTranslation(document)));
    }

    private static void DrainProviderDiagnostics(ConcurrentQueue<ProviderDiagnosticUpdate>? updates,
        ExternalLyricsDiagnostic[] diagnostics)
    {
        if (updates is null) return;
        while (updates.TryDequeue(out var update))
        {
            if ((uint)update.ProviderIndex < (uint)diagnostics.Length)
            {
                diagnostics[update.ProviderIndex] = update.Diagnostic;
            }
        }
    }

    private static void MarkUnstartedProviders(ExternalLyricsDiagnostic[] diagnostics,
        ExternalLyricsQueryOutcome outcome, ExternalLyricsDiagnosticReason reason)
    {
        for (var index = 0; index < diagnostics.Length; index++)
        {
            if (diagnostics[index].Outcome == ExternalLyricsQueryOutcome.NotStarted)
            {
                diagnostics[index] = diagnostics[index] with { Outcome = outcome, Reason = reason };
            }
        }
    }

    private static void MarkPendingProvidersCanceled(ExternalLyricsDiagnostic[] diagnostics)
    {
        for (var index = 0; index < diagnostics.Length; index++)
        {
            if (diagnostics[index].Outcome == ExternalLyricsQueryOutcome.Querying)
            {
                diagnostics[index] = diagnostics[index] with
                {
                    Outcome = ExternalLyricsQueryOutcome.Canceled,
                    Reason = ExternalLyricsDiagnosticReason.Canceled,
                };
            }
        }
    }

    private static ExternalLyricsDiagnostic DescribeDocument(ExternalLyricsDiagnostic diagnostic, LyricsDocument? document)
    {
        if (document is null)
        {
            return diagnostic with
            {
                Outcome = ExternalLyricsQueryOutcome.NoResult,
                Reason = ExternalLyricsDiagnosticReason.NoDocumentReturned,
            };
        }
        return diagnostic with
        {
            Outcome = document.Lines.Count > 0 ? ExternalLyricsQueryOutcome.Success : ExternalLyricsQueryOutcome.NoResult,
            LineCount = document.Lines.Count,
            WordLineCount = document.Lines.Count(HasUsableWords),
            TranslatedLineCount = document.Lines.Count(HasMeaningfulTranslation),
            Reason = document.Lines.Count == 0 ? ExternalLyricsDiagnosticReason.EmptyDocument
                : string.Equals(document.Status, "plain", StringComparison.OrdinalIgnoreCase)
                    ? ExternalLyricsDiagnosticReason.PlainLyricsOnly : ExternalLyricsDiagnosticReason.None,
        };
    }

    private static ExternalLyricsDiagnosticReason DescribeRejection(
        LyricsDocument current, LyricsDocument candidate, EnrichmentScope scope)
    {
        if (string.Equals(candidate.Status, "plain", StringComparison.OrdinalIgnoreCase))
        {
            return ExternalLyricsDiagnosticReason.PlainLyricsOnly;
        }
        var losesWords = scope.Words && HasUsableWords(current) && !HasUsableWords(candidate);
        var losesTranslation = scope.Translation && HasTranslation(current) && !HasTranslation(candidate);
        return losesWords && losesTranslation ? ExternalLyricsDiagnosticReason.WouldLoseWordsAndTranslations
            : losesWords ? ExternalLyricsDiagnosticReason.WouldLoseWordTimings
            : losesTranslation ? ExternalLyricsDiagnosticReason.WouldLoseTranslations
            : ExternalLyricsDiagnosticReason.NoAdditionalRequestedFeatures;
    }

    private static ExternalLyricsDiagnostic DescribeFailure(ExternalLyricsDiagnostic diagnostic, Exception error)
    {
        var reason = error switch
        {
            OperationCanceledException or TimeoutException => ExternalLyricsFailureReason.Timeout,
            HttpRequestException { StatusCode: not null } => ExternalLyricsFailureReason.HttpError,
            HttpRequestException => ExternalLyricsFailureReason.NetworkError,
            JsonException or InvalidDataException => ExternalLyricsFailureReason.InvalidResponse,
            _ => ExternalLyricsFailureReason.Other,
        };
        var status = error is HttpRequestException { StatusCode: { } httpStatus } ? (int?)httpStatus : null;
        // Never include exception messages: they can contain request URLs, cookies,
        // payload excerpts, or local paths. The type and classified reason suffice.
        var detail = reason switch
        {
            ExternalLyricsFailureReason.Timeout => "The provider request timed out.",
            ExternalLyricsFailureReason.HttpError => $"The provider returned HTTP {status}.",
            ExternalLyricsFailureReason.NetworkError => "The provider could not be reached.",
            ExternalLyricsFailureReason.InvalidResponse => "The provider returned an invalid response.",
            _ => "The provider request failed.",
        };
        return diagnostic with
        {
            Outcome = ExternalLyricsQueryOutcome.Failed,
            ExceptionType = error.GetType().Name,
            FailureReason = reason,
            HttpStatusCode = status,
            Detail = detail,
        };
    }

    private void CancelEnrichmentAttempt()
    {
        if (_enrichmentAttempt is not { } attempt) return;
        DrainProviderDiagnostics(attempt.Diagnostics, _enrichmentDiagnostics);
        if (_enrichmentState == ExternalLyricsQueryState.Querying)
        {
            _enrichmentState = ExternalLyricsQueryState.Canceled;
            MarkPendingProvidersCanceled(_enrichmentDiagnostics);
        }
        _enrichmentAttempt = null;
        attempt.Cancellation.Cancel();
        attempt.Cancellation.Dispose();
        ObserveFault(attempt.Fetch);
    }

    private void ResetEnrichmentLookup()
    {
        CancelEnrichmentAttempt();
        _enrichmentScope = null;
        _enrichedLyrics = null;
        _enrichmentAttemptCount = 0;
        _enrichmentLookupComplete = false;
        _enrichmentNextAttemptAt = DateTimeOffset.MinValue;
        _enrichmentDiagnostics = CreateProviderDiagnostics(ExternalLyricsQueryStage.Enrichment);
        _enrichmentState = ExternalLyricsQueryState.NotStarted;
    }

    private static void ObserveFault(Task task) => _ = task.ContinueWith(completed => { _ = completed.Exception; },
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

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
            !catalogLyricsIds.Any(lyricsId => CatalogLyricsId.Matches(candidate.Document.LyricsId, lyricsId)))
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
        _catalogFetchGeneration = 0;
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

    private sealed record EnrichmentScope(int TrackGeneration, double Duration, bool Words, bool Translation);
    private sealed record EnrichmentUpdate(LyricsDocument Document, string ProviderName);
    private sealed record EnrichmentFetchResult(bool IsTransientFailure);
    private sealed record EnrichmentAttempt(CancellationTokenSource Cancellation,
        ConcurrentQueue<EnrichmentUpdate> Updates, ConcurrentQueue<ProviderDiagnosticUpdate> Diagnostics,
        Task<EnrichmentFetchResult> Fetch);
    private sealed record ProviderDiagnosticUpdate(int ProviderIndex, ExternalLyricsDiagnostic Diagnostic);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var catalogFetch = _catalogFetch;
        var externalFetch = _externalFetch;
        var enrichmentFetch = _enrichmentAttempt?.Fetch;
        _catalogFetchCancellation?.Cancel();
        _externalFetchCancellation?.Cancel();
        _enrichmentAttempt?.Cancellation.Cancel();

        try
        {
            var pending = new Task?[] { catalogFetch, externalFetch, enrichmentFetch }
                .Where(task => task is not null)
                .Cast<Task>()
                .ToArray();
            if (pending.Length > 0)
            {
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            ResetCatalogLookup();
            CancelExternalFetch();
            ResetEnrichmentLookup();
        }
    }
}
