using Windows.Media.Control;
using AppleMusicLyrics.Core.Abstractions;
using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.Infrastructure.Windows.Media;

public sealed class GlobalMediaSessionProvider : IPlayerSessionProvider
{
    private readonly Lazy<Task<GlobalSystemMediaTransportControlsSessionManager>> _managerTask;

    public GlobalMediaSessionProvider()
    {
        _managerTask = new Lazy<Task<GlobalSystemMediaTransportControlsSessionManager>>(
            () => GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask());
    }

    public async Task<PlayerState?> GetCurrentPlayerStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var manager = await _managerTask.Value.ConfigureAwait(false);
            var currentSession = manager.GetCurrentSession();
            var currentState = await TryCreatePlayerStateAsync(currentSession, cancellationToken).ConfigureAwait(false);
            if (IsPreferredState(currentState))
            {
                return currentState;
            }

            var sessions = manager.GetSessions();
            PlayerState? fallback = null;

            foreach (var session in sessions)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var state = await TryCreatePlayerStateAsync(session, cancellationToken).ConfigureAwait(false);
                if (state is null)
                {
                    continue;
                }

                if (IsAppleMusicState(state))
                {
                    return state;
                }

                if (fallback is null && state.Playing && !string.IsNullOrWhiteSpace(state.Title))
                {
                    fallback = state;
                }
            }

            return currentState ?? fallback;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPreferredState(PlayerState? state)
    {
        return state is not null
            && state.Position >= 0
            && (!string.IsNullOrWhiteSpace(state.Title) || IsAppleMusicState(state));
    }

    private static bool IsAppleMusicState(PlayerState state)
    {
        return ContainsAppleMusicMarker(state.SourceAppId);
    }

    private static bool ContainsAppleMusicMarker(string? sourceAppId)
    {
        return !string.IsNullOrWhiteSpace(sourceAppId)
            && (sourceAppId.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase)
                || sourceAppId.Contains("AppleInc.AppleMusicWin", StringComparison.OrdinalIgnoreCase));
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

            return new PlayerState(
                Title: mediaProperties?.Title,
                Artist: mediaProperties?.Artist,
                Album: mediaProperties?.AlbumTitle,
                Position: Math.Max(0, position.TotalSeconds),
                Duration: Math.Max(0, duration),
                Playing: playbackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                SourceAppId: session.SourceAppUserModelId);
        }
        catch
        {
            return null;
        }
    }
}
