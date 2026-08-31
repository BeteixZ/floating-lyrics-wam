using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.Infrastructure.Windows.Media;

/// <summary>
/// Selects which SMTC state is allowed to drive lyric matching. Keeping this independent from
/// WinRT makes multi-player competition deterministic and unit-testable.
/// </summary>
public static class MediaSessionSelectionPolicy
{
    public static PlayerState? Select(
        PlayerState? currentState,
        IReadOnlyList<PlayerState> availableStates,
        bool allowNonAppleMediaSessions)
    {
        if (IsAppleMusicState(currentState))
        {
            return currentState;
        }

        var appleState = availableStates
            .Where(IsAppleMusicState)
            .OrderByDescending(state => state.Playing)
            .ThenByDescending(state => !string.IsNullOrWhiteSpace(state.Title))
            .FirstOrDefault();
        if (appleState is not null)
        {
            return appleState;
        }

        if (!allowNonAppleMediaSessions)
        {
            return null;
        }

        if (IsUsableState(currentState))
        {
            return currentState;
        }

        return availableStates
            .Where(IsUsableState)
            .OrderByDescending(state => state.Playing)
            .FirstOrDefault();
    }

    public static bool IsAppleMusicState(PlayerState? state)
    {
        return state is not null && ContainsAppleMusicMarker(state.SourceAppId);
    }

    private static bool IsUsableState(PlayerState? state)
    {
        return state is not null
            && state.Position >= 0
            && !string.IsNullOrWhiteSpace(state.Title);
    }

    private static bool ContainsAppleMusicMarker(string? sourceAppId)
    {
        return !string.IsNullOrWhiteSpace(sourceAppId)
            && (sourceAppId.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase)
                || sourceAppId.Contains("AppleInc.AppleMusicWin", StringComparison.OrdinalIgnoreCase));
    }
}
