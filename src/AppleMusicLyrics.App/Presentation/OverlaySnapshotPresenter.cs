using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.App.Presentation;

public sealed record PlayerPresentation(string WindowTitle, string Subtitle, string PlayerText);

public sealed record LyricsPresentation(
    string StatusText,
    string PathText,
    string PreviousText,
    string CurrentText,
    string NextText,
    bool IsPlaying,
    bool Animate);

public static class OverlaySnapshotPresenter
{
    public static PlayerPresentation CreatePlayer(RuntimeSnapshot snapshot)
    {
        if (snapshot.Player is null)
        {
            return new PlayerPresentation(
                "Apple Music Lyrics",
                "Waiting for Apple Music session",
                "No active media session");
        }

        var status = snapshot.Player.Playing ? "Playing" : "Paused";
        var artist = snapshot.Player.Artist ?? "Unknown Artist";
        var title = snapshot.Player.Title ?? "Unknown Title";
        var subtitle = snapshot.Player.Album is { Length: > 0 }
            ? $"{artist} | {snapshot.Player.Album}"
            : artist;
        return new PlayerPresentation(
            $"{artist} - {title}",
            subtitle,
            $"{status}: {artist} - {title}");
    }

    public static LyricsPresentation CreateLyrics(RuntimeSnapshot snapshot)
    {
        var resolutionLabel = FormatResolutionLabel(snapshot.Resolution);
        if (snapshot.Document is null)
        {
            return new LyricsPresentation(
                resolutionLabel,
                snapshot.Resolution.Summary,
                string.Empty,
                GetResolutionPlaceholder(snapshot.Resolution),
                snapshot.Resolution.Status == LyricsResolutionStatus.FetchingExternal
                    ? "The current track will be checked again automatically."
                    : "Open the debug panel for the current match decision.",
                snapshot.Player?.Playing ?? false,
                Animate: false);
        }

        var currentText = snapshot.ActiveLyric.CurrentLine?.Text
            ?? snapshot.ActiveLyric.NextLine?.Text
            ?? snapshot.Document.Lines.FirstOrDefault()?.Text
            ?? "Lyrics file has no displayable lines.";
        return new LyricsPresentation(
            $"{snapshot.Document.Lines.Count} lines | {resolutionLabel}",
            $"{snapshot.Resolution.Summary} | {snapshot.Document.SourceFile}",
            snapshot.ActiveLyric.PreviousLine?.Text ?? string.Empty,
            currentText,
            snapshot.ActiveLyric.NextLine?.Text ?? string.Empty,
            snapshot.Player?.Playing ?? false,
            Animate: snapshot.ActiveLyric.CurrentLine is not null);
    }

    internal static string FormatResolutionLabel(LyricsResolution resolution)
    {
        var status = resolution.Status switch
        {
            LyricsResolutionStatus.NoPlayer => "No player",
            LyricsResolutionStatus.WaitingForMetadata => "Waiting for metadata",
            LyricsResolutionStatus.SearchingLocal => "Searching cache",
            LyricsResolutionStatus.VerifyingCatalog => "Verifying catalog",
            LyricsResolutionStatus.FetchingExternal => "Fetching external",
            LyricsResolutionStatus.Resolved => "Lyrics resolved",
            LyricsResolutionStatus.Unavailable => "Lyrics unavailable",
            LyricsResolutionStatus.Error => "Resolution error",
            _ => resolution.Status.ToString(),
        };

        return resolution.Confidence == LyricsResolutionConfidence.None
            ? status
            : $"{status} ({resolution.Confidence.ToString().ToLowerInvariant()})";
    }

    internal static string GetResolutionPlaceholder(LyricsResolution resolution)
    {
        return resolution.Status switch
        {
            LyricsResolutionStatus.NoPlayer => "Waiting for Apple Music session...",
            LyricsResolutionStatus.WaitingForMetadata => "Waiting for track metadata...",
            LyricsResolutionStatus.SearchingLocal => "Searching the Apple Music lyric cache...",
            LyricsResolutionStatus.VerifyingCatalog => "Verifying the matching song...",
            LyricsResolutionStatus.FetchingExternal => "Looking for lyrics from external providers...",
            LyricsResolutionStatus.Unavailable => "No verified lyrics found.",
            LyricsResolutionStatus.Error => "Lyrics resolution failed.",
            _ => "Waiting for lyrics...",
        };
    }
}
