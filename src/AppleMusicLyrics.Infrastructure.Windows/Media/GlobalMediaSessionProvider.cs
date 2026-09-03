using Windows.Media.Control;
using AppleMusicLyrics.Core.Abstractions;
using AppleMusicLyrics.Core.Matching;
using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.Infrastructure.Windows.Media;

public sealed class GlobalMediaSessionProvider : IPlayerSessionProvider
{
    private readonly Lazy<Task<GlobalSystemMediaTransportControlsSessionManager>> _managerTask;

    public GlobalMediaSessionProvider(bool allowNonAppleMediaSessions = false)
    {
        AllowNonAppleMediaSessions = allowNonAppleMediaSessions;
        _managerTask = new Lazy<Task<GlobalSystemMediaTransportControlsSessionManager>>(
            () => GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask());
    }

    public bool AllowNonAppleMediaSessions { get; set; }

    public async Task<PlayerState?> GetCurrentPlayerStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var manager = await _managerTask.Value.ConfigureAwait(false);
            var currentSession = manager.GetCurrentSession();
            var currentState = await TryCreatePlayerStateAsync(currentSession, cancellationToken).ConfigureAwait(false);
            if (MediaSessionSelectionPolicy.IsAppleMusicState(currentState))
            {
                return currentState;
            }

            var states = new List<PlayerState>();
            foreach (var session in manager.GetSessions())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var state = await TryCreatePlayerStateAsync(session, cancellationToken).ConfigureAwait(false);
                if (state is not null)
                {
                    states.Add(state);
                }
            }

            return MediaSessionSelectionPolicy.Select(
                currentState,
                states,
                AllowNonAppleMediaSessions);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<PlayerState?> TryCreatePlayerStateAsync(
        GlobalSystemMediaTransportControlsSession? session,
        CancellationToken cancellationToken)
    {
        if (session is null)
        {
            return null;
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

            var (cleanArtist, cleanAlbum) = MetadataMatching.ParseArtistAndAlbum(
                mediaProperties?.Artist,
                mediaProperties?.AlbumTitle);

            return new PlayerState(
                Title: mediaProperties?.Title,
                Artist: cleanArtist,
                Album: cleanAlbum,
                Position: Math.Max(0, position.TotalSeconds),
                Duration: Math.Max(0, duration),
                Playing: playbackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                SourceAppId: session.SourceAppUserModelId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
