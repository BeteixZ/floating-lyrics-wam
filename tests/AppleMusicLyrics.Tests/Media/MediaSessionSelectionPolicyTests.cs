using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Infrastructure.Windows.Media;
using Xunit;

namespace AppleMusicLyrics.Tests.Media;

public sealed class MediaSessionSelectionPolicyTests
{
    [Fact]
    public void Select_KeepsCurrentAppleMusicEvenWhenAnotherPlayerIsPlaying()
    {
        var apple = State("Apple song", "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App", playing: false);
        var browser = State("Browser video", "Chrome", playing: true);

        var selected = MediaSessionSelectionPolicy.Select(apple, [browser], allowNonAppleMediaSessions: false);

        Assert.Same(apple, selected);
    }

    [Fact]
    public void Select_FindsAppleMusicWhenAnotherPlayerOwnsCurrentSession()
    {
        var browser = State("Browser video", "Chrome", playing: true);
        var apple = State("Apple song", "AppleMusic.exe", playing: true);

        var selected = MediaSessionSelectionPolicy.Select(browser, [browser, apple], allowNonAppleMediaSessions: false);

        Assert.Same(apple, selected);
    }

    [Fact]
    public void Select_ReturnsNullForOnlyNonAppleSessionsByDefault()
    {
        var browser = State("Browser video", "Chrome", playing: true);
        var spotify = State("Other song", "SpotifyAB.SpotifyMusic", playing: true);

        var selected = MediaSessionSelectionPolicy.Select(browser, [browser, spotify], allowNonAppleMediaSessions: false);

        Assert.Null(selected);
    }

    [Fact]
    public void Select_AllowsCurrentNonAppleSessionOnlyWhenOptedIn()
    {
        var browser = State("Browser video", "Chrome", playing: true);

        var selected = MediaSessionSelectionPolicy.Select(browser, [browser], allowNonAppleMediaSessions: true);

        Assert.Same(browser, selected);
    }

    [Fact]
    public void Select_PrefersPlayingAppleSessionFromAvailableSessions()
    {
        var pausedApple = State("Paused song", "AppleMusic.exe", playing: false);
        var playingApple = State("Playing song", "AppleInc.AppleMusicWin_123!App", playing: true);

        var selected = MediaSessionSelectionPolicy.Select(null, [pausedApple, playingApple], allowNonAppleMediaSessions: false);

        Assert.Same(playingApple, selected);
    }

    [Theory]
    [InlineData("AppleMusic.exe")]
    [InlineData("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App")]
    [InlineData("applemusic")]
    public void IsAppleMusicState_RecognizesKnownSourceMarkers(string sourceAppId)
    {
        Assert.True(MediaSessionSelectionPolicy.IsAppleMusicState(State("Song", sourceAppId, playing: true)));
    }

    private static PlayerState State(string title, string sourceAppId, bool playing)
    {
        return new PlayerState(title, "Artist", "Album", 1.0, 180.0, playing, sourceAppId);
    }
}
