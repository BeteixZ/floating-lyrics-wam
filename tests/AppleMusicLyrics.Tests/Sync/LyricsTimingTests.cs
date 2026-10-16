using System.Text.Json;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Sync;
using Xunit;

namespace AppleMusicLyrics.Tests.Sync;

public sealed class LyricsTimingTests
{
    [Theory]
    [InlineData(9, 0)]
    [InlineData(10, 0)]
    [InlineData(11, 0.5)]
    [InlineData(12, 1)]
    [InlineData(14, 1)]
    public void GetWordProgress_UsesSourceTiming(double position, double expected)
    {
        Assert.Equal(expected, LyricsTiming.GetWordProgress(new LyricWord(10, 12, "word"), position));
    }

    [Fact]
    public void GetPosition_UsesTheSameOffsetAsLineSelectionIncludingPauseAndSeek()
    {
        var word = new LyricWord(10, 12, "word");
        var line = new LyricsLine(10, 12, "word", Words: [word]);
        var document = new LyricsDocument("id", "success", "test", DateTimeOffset.UtcNow, [line], NativeOffsetSeconds: 2);
        var paused = new PlayerState("Song", "Artist", null, 12, 30, false);
        var position = LyricsTiming.GetPosition(document, paused, offsetSeconds: 1);
        Assert.Equal(11, position);
        Assert.Equal(0.5, LyricsTiming.GetWordProgress(word, position));
        Assert.Same(line, new LyricsSynchronizer().Resolve(document, paused, 1).CurrentLine);
        Assert.Equal(13, LyricsTiming.GetPosition(document, paused, 1, applyNativeOffset: false));
        Assert.Equal(0, LyricsTiming.GetWordProgress(word, LyricsTiming.GetPosition(document, paused with { Position = 8 }, 1)));
    }

    [Fact]
    public void LyricsLine_OldCachedJsonRemainsReadableAndNewDataRoundTrips()
    {
        var old = JsonSerializer.Deserialize<LyricsLine>("""{"Begin":1,"End":2,"Text":"old"}""");
        Assert.NotNull(old);
        Assert.Null(old.Words);
        Assert.Null(old.Translation);
        var rich = new LyricsLine(1, 2, "word", Words: [new LyricWord(1, 2, "word")], Translation: "译文");
        var restored = JsonSerializer.Deserialize<LyricsLine>(JsonSerializer.Serialize(rich));
        Assert.Equal(rich.Translation, restored!.Translation);
        Assert.Equal(rich.Words![0], Assert.Single(restored.Words!));
    }
}
