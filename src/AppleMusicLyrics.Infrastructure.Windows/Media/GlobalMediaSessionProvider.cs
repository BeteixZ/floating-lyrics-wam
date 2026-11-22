using Windows.Media.Control;
using AppleMusicLyrics.Core.Abstractions;
using AppleMusicLyrics.Core.Matching;
using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.Infrastructure.Windows.Media;

public sealed class GlobalMediaSessionProvider : IPlayerSessionProvider
{
    private readonly SessionSampler<GlobalSystemMediaTransportControlsSessionManager, PlayerState?> _sampler;

    public GlobalMediaSessionProvider(bool allowNonAppleMediaSessions = false)
    {
        AllowNonAppleMediaSessions = allowNonAppleMediaSessions;
        // Each poll has a deadline and a stuck or disconnected manager is replaced, so one
        // unanswered SMTC call cannot freeze the lyrics until the app restarts.
        _sampler = new(
            token => GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(token),
            ReadPlayerStateAsync);
    }

    public bool AllowNonAppleMediaSessions { get; set; }

    public async Task<PlayerState?> GetCurrentPlayerStateAsync(CancellationToken cancellationToken = default) =>
        await _sampler.ReadAsync(cancellationToken).ConfigureAwait(false);

    private async Task<PlayerState?> ReadPlayerStateAsync(
        GlobalSystemMediaTransportControlsSessionManager manager, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var currentSession = manager.GetCurrentSession();
        var (currentState, currentError) = await TryCreatePlayerStateAsync(currentSession, cancellationToken).ConfigureAwait(false);
        if (MediaSessionSelectionPolicy.IsAppleMusicState(currentState))
        {
            return currentState;
        }

        var states = new List<PlayerState>();
        var sessions = 0;
        var lastError = currentError;
        var failures = currentError is null ? 0 : 1;
        foreach (var session in manager.GetSessions())
        {
            cancellationToken.ThrowIfCancellationRequested();

            sessions++;
            var (state, error) = await TryCreatePlayerStateAsync(session, cancellationToken).ConfigureAwait(false);
            if (state is not null)
            {
                states.Add(state);
            }
            else if (error is not null)
            {
                failures++;
                lastError = error;
            }
        }

        // No sessions is a normal answer (nothing is playing). Sessions that all fail to read
        // point at a broken manager, which the sampler replaces after repeated failures.
        if (states.Count == 0 && currentState is null && failures > 0 && failures >= sessions)
        {
            throw new InvalidOperationException("No media session could be read.", lastError);
        }

        return MediaSessionSelectionPolicy.Select(
            currentState,
            states,
            AllowNonAppleMediaSessions);
    }

    private static async Task<(PlayerState? State, Exception? Error)> TryCreatePlayerStateAsync(
        GlobalSystemMediaTransportControlsSession? session,
        CancellationToken cancellationToken)
    {
        if (session is null)
        {
            return (null, null);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var playbackInfo = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var mediaProperties = await session.TryGetMediaPropertiesAsync().AsTask(cancellationToken).ConfigureAwait(false);

            var start = timeline.StartTime;
            var end = timeline.EndTime;
            var position = timeline.Position;
            var duration = end > start ? (end - start).TotalSeconds : 0;
            // LastUpdatedTime is when the session published Position. Sessions that never set it
            // report a default timestamp; the clock ignores missing, future or stale ages.
            var positionAge = (DateTimeOffset.UtcNow - timeline.LastUpdatedTime).TotalSeconds;

            var (cleanArtist, cleanAlbum) = MetadataMatching.ParseArtistAndAlbum(
                mediaProperties?.Artist,
                mediaProperties?.AlbumTitle);

            return (new PlayerState(
                Title: mediaProperties?.Title,
                Artist: cleanArtist,
                Album: cleanAlbum,
                Position: Math.Max(0, position.TotalSeconds),
                Duration: Math.Max(0, duration),
                Playing: playbackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                SourceAppId: session.SourceAppUserModelId,
                PositionAgeSeconds: double.IsFinite(positionAge) && positionAge >= 0 ? positionAge : null), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // One session that cannot be read (its app is closing) must not hide the others.
            return (null, error);
        }
    }
}
