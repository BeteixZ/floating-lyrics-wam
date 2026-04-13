using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.App.ViewModels;

public sealed class OverlayViewModel
{
    public string Title { get; private set; } = "Apple Music Lyrics";

    public string Subtitle { get; private set; } = "Waiting for playback";

    public string PreviousText { get; private set; } = string.Empty;

    public string CurrentText { get; private set; } = "Waiting for playback";

    public string NextText { get; private set; } = string.Empty;

    public bool HasLyrics { get; private set; }

    public void Apply(RuntimeSnapshot snapshot)
    {
        if (snapshot.Player is null)
        {
            Title = "Apple Music Lyrics";
            Subtitle = "Waiting for Apple Music session";
            PreviousText = string.Empty;
            CurrentText = "Waiting for playback";
            NextText = string.Empty;
            HasLyrics = false;
            return;
        }

        Title = snapshot.Player.Title ?? "Unknown Title";
        Subtitle = snapshot.Player.Artist ?? "Unknown Artist";
        PreviousText = snapshot.ActiveLyric.PreviousLine?.Text ?? string.Empty;
        CurrentText = snapshot.ActiveLyric.CurrentLine?.Text ?? "No synced lyrics";
        NextText = snapshot.ActiveLyric.NextLine?.Text ?? string.Empty;
        HasLyrics = snapshot.Document is not null && snapshot.Document.Lines.Count > 0;
    }
}
