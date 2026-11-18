using AppleMusicLyrics.Core.Abstractions;
using AppleMusicLyrics.Core.Matching;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Parsing;

namespace AppleMusicLyrics.Application.Services;

public sealed partial class LyricsRuntimeService
{
    public bool RequestLyricsRefresh(int trackGeneration) => QueueLyricsRequest(new(trackGeneration, Refresh: true));

    public bool RequestLyricsSelection(int trackGeneration, LyricsChoice? choice) =>
        (choice is null || choice.Document.Lines.Count > 0)
        && QueueLyricsRequest(new(trackGeneration, Choice: choice));

    private bool QueueLyricsRequest(LyricsRequest request)
    {
        var snapshot = Volatile.Read(ref _frameBasis).Snapshot;
        if (_disposed || !snapshot.CanManageLyrics || snapshot.TrackGeneration != request.TrackGeneration) return false;
        Interlocked.Exchange(ref _pendingLyricsRequest, request);
        return true;
    }

    private void ApplyPendingLyricsRequest()
    {
        // The poll owns all matching state. Track changes or duration corrections that happened
        // after a menu click take precedence over the request made for the earlier snapshot.
        var request = Interlocked.Exchange(ref _pendingLyricsRequest, null);
        if (request is null || request.TrackGeneration != _trackGeneration) return;
        if (!request.Refresh)
        {
            _manualLyrics = request.Choice;
            return;
        }

        var previous = Volatile.Read(ref _frameBasis).Snapshot;
        RestartLyricsLookup("Searching again for the current track.");
        _sessionChangedAt = _timeProvider.GetUtcNow();
        _refreshLyrics = true;
        if (!previous.IsManualSelection && previous.Document is not null) _refreshFallback = previous;
        (_lyricsProvider as IPlayerMatchedLyricsProvider)?.InvalidateFileIndex();
    }

    private void RestartLyricsLookup(string summary)
    {
        _currentDocument = null;
        _manualLyrics = null;
        _refreshFallback = null;
        _refreshLyrics = false;
        _lastScanAt = DateTimeOffset.MinValue;
        _lastNativeRefreshAt = DateTimeOffset.MinValue;
        _unproductiveIdleRescans = 0;
        ResetCatalogLookup();
        CancelExternalFetch();
        ResetEnrichmentLookup();
        _fallbackDiagnostics = CreateProviderDiagnostics(ExternalLyricsQueryStage.Fallback);
        _fallbackState = ExternalLyricsQueryState.NotStarted;
        _resolution = CreateResolution(LyricsResolutionStatus.SearchingLocal, summary);
    }

    /// <summary>Lists source documents for the supplied track without changing the displayed lyrics.</summary>
    public async Task<LyricsChoiceResults> SearchLyricsChoicesAsync(RuntimeSnapshot target, bool refresh,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!target.CanManageLyrics || target.Player is not { } player) return new([], 0);

        var apple = SearchAppleChoicesAsync(player, refresh, cancellationToken);
        var searches = ExternalLyricsProviders.Select(provider => SearchExternalChoiceAsync(provider, player, refresh, cancellationToken));
        var results = await Task.WhenAll(searches.Prepend(apple)).ConfigureAwait(false);
        var choices = results.SelectMany(result => result.Choices);
        if (target.Document is { Lines.Count: > 0 } current)
        {
            var choice = new LyricsChoice(current, target.Resolution.Source);
            choices = refresh ? choices.Append(choice) : choices.Prepend(choice);
        }
        var displayable = choices.DistinctBy(choice => choice.Document.SourceFile, StringComparer.OrdinalIgnoreCase)
            .Select(choice => choice with { Document = LyricsContentFilter.Clean(choice.Document, player) })
            .Where(choice => choice.Document.Lines.Count > 0).ToArray();
        return new LyricsChoiceResults(displayable, results.Sum(result => result.FailedSources));
    }

    private async Task<LyricsChoiceResults> SearchAppleChoicesAsync(PlayerState player, bool refresh, CancellationToken token)
    {
        try
        {
            if (_lyricsProvider is not IPlayerMatchedLyricsProvider matched)
            {
                var latest = await _lyricsProvider.GetLatestLyricsAsync(token).ConfigureAwait(false);
                return latest is { Lines.Count: > 0 } && MatchesPlayer(latest, player)
                    ? new([new(latest, LyricsResolutionSource.AppleMusicCache)], 0) : new([], 0);
            }
            if (refresh) matched.InvalidateFileIndex();
            var local = await matched.FindCandidatesAsync(player, token).ConfigureAwait(false);
            var catalog = _catalogResolver is null ? null : await FetchCatalogAsync(player, token, refresh).ConfigureAwait(false);
            var ids = catalog?.LyricsIds ?? [];
            var choices = new List<LyricsChoice>();
            if (ids.Count > 0 && await matched.FindByLyricsIdsAsync(ids, token).ConfigureAwait(false) is { Lines.Count: > 0 } verified)
                choices.Add(new(verified, LyricsResolutionSource.CatalogVerifiedCache));
            foreach (var candidate in local)
            {
                var documentId = CatalogLyricsId.ForDocument(candidate.Document);
                var source = ids.Any(id => CatalogLyricsId.Matches(documentId, id))
                    ? LyricsResolutionSource.CatalogVerifiedCache : LyricsResolutionSource.AppleMusicCache;
                choices.Add(new(candidate.Document, source));
            }
            return new(choices, catalog?.IsTransientFailure == true ? 1 : 0);
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            return new([], 1);
        }
    }

    private static async Task<LyricsChoiceResults> SearchExternalChoiceAsync(IExternalLyricsProvider provider,
        PlayerState player, bool refresh, CancellationToken token)
    {
        try
        {
            var fetch = FetchFromProviderAsync(provider, player, refresh, token);
            ObserveFault(fetch);
            var document = await fetch.WaitAsync(token).ConfigureAwait(false);
            return document is { Lines.Count: > 0 } && LyricsMatchPolicy.IsRecordingDurationCompatible(player.Duration, document.DurationSeconds)
                ? new([new(document, LyricsResolutionSource.ExternalProvider, provider.Name)], 0) : new([], 0);
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            return new([], 1);
        }
    }

    private static Task<LyricsDocument?> FetchFromProviderAsync(IExternalLyricsProvider provider, PlayerState player,
        bool refresh, CancellationToken token) =>
        refresh ? provider.FetchFreshAsync(player, token) : provider.FetchAsync(player, token);

    private sealed record LyricsRequest(int TrackGeneration, bool Refresh = false, LyricsChoice? Choice = null);
}
