using AppleMusicLyrics.App.Presentation;
using AppleMusicLyrics.Core.Models;
using Xunit;

namespace AppleMusicLyrics.Tests.Display;

public sealed class OverlaySnapshotPresenterTests
{
    [Fact]
    public void CreatePlayer_NoPlayer_ReturnsWaitingPresentation()
    {
        var snapshot = new RuntimeSnapshot(
            null,
            null,
            new ActiveLyricState(null, null, null, null),
            LyricsResolution.NoPlayer);

        var presentation = OverlaySnapshotPresenter.CreatePlayer(snapshot);

        Assert.Equal("Apple Music Lyrics", presentation.WindowTitle);
        Assert.Contains("Waiting", presentation.Subtitle);
    }

    [Fact]
    public void CreateLyrics_ResolvedDocument_MapsActiveLines()
    {
        var previous = new LyricsLine(0, 1, "previous");
        var current = new LyricsLine(1, 2, "current");
        var next = new LyricsLine(2, 3, "next");
        var document = new LyricsDocument(
            "id", "ok", "lyrics.json", DateTimeOffset.UtcNow, [previous, current, next], 3);
        var player = new PlayerState("Song", "Artist", "Album", 1.5, 3, true);
        var snapshot = new RuntimeSnapshot(
            document,
            player,
            new ActiveLyricState(1, previous, current, next),
            new LyricsResolution(
                LyricsResolutionStatus.Resolved,
                LyricsResolutionConfidence.High,
                LyricsResolutionSource.AppleMusicCache,
                "resolved"));

        var presentation = OverlaySnapshotPresenter.CreateLyrics(snapshot);

        Assert.Equal("previous", presentation.PreviousText);
        Assert.Equal("current", presentation.CurrentText);
        Assert.Equal("next", presentation.NextText);
        Assert.True(presentation.Animate);
        Assert.Contains("3 lines", presentation.StatusText);
    }
}
