using AppleMusicLyrics.Core.Matching;
using AppleMusicLyrics.Core.Models;
using Xunit;

namespace AppleMusicLyrics.Tests.Matching;

public sealed class LyricsMatchPolicyTests
{
    private static LyricsMatch CreateMatch(string id, double duration, double delta, int score, bool hasContentMatch = true)
    {
        var doc = new LyricsDocument(
            id,
            "success",
            $"{id}.json",
            DateTimeOffset.UtcNow,
            [new LyricsLine(0.0, 1.0, "text")],
            duration,
            0.0,
            0.0);

        return new LyricsMatch(doc, score, delta, HasContentMatch: hasContentMatch);
    }

    [Fact]
    public void IsPlausible_ReturnsTrueWhenWithinTolerance()
    {
        var match = CreateMatch("1", 180, 5.0, 50);
        Assert.True(LyricsMatchPolicy.IsPlausible(match));

        var border = CreateMatch("2", 180, 6.0, 10);
        Assert.True(LyricsMatchPolicy.IsPlausible(border));
    }

    [Fact]
    public void IsPlausible_ReturnsFalseWhenBeyondTolerance()
    {
        var match = CreateMatch("1", 180, 6.1, 5);
        Assert.False(LyricsMatchPolicy.IsPlausible(match));
    }

    [Fact]
    public void IsConfidentSingle_ReturnsTrueForContentMatchedSingleWithinConfidentDelta()
    {
        var confident = CreateMatch("1", 180, 0.9, 100);
        Assert.True(LyricsMatchPolicy.IsConfidentSingle([confident]));

        var border = CreateMatch("2", 180, 1.0, 80);
        Assert.True(LyricsMatchPolicy.IsConfidentSingle([border]));

        var slightlyOver = CreateMatch("3", 180, 1.1, 70);
        Assert.False(LyricsMatchPolicy.IsConfidentSingle([slightlyOver]));

        var multiple = new[]
        {
            CreateMatch("1", 180, 0.5, 100),
            CreateMatch("2", 180, 0.8, 90),
        };
        Assert.False(LyricsMatchPolicy.IsConfidentSingle(multiple));
    }

    [Fact]
    public void IsConfidentSingle_ReturnsFalseWhenCloseDurationHasNoContentMatch()
    {
        var durationOnly = CreateMatch("1", 180, 0.0, 100, hasContentMatch: false);

        Assert.False(LyricsMatchPolicy.IsConfidentSingle([durationOnly]));
    }

    [Fact]
    public void IsMediumConfidenceSingle_ReturnsTrueForSingleCandidateWithinMediumDelta()
    {
        var within = CreateMatch("1", 180, 2.5, 60);
        Assert.True(LyricsMatchPolicy.IsMediumConfidenceSingle([within]));

        var border = CreateMatch("2", 180, 3.0, 50);
        Assert.True(LyricsMatchPolicy.IsMediumConfidenceSingle([border]));

        var outside = CreateMatch("3", 180, 3.1, 40);
        Assert.False(LyricsMatchPolicy.IsMediumConfidenceSingle([outside]));

        var multiple = new[]
        {
            CreateMatch("1", 180, 2.0, 60),
            CreateMatch("2", 180, 2.5, 50),
        };
        Assert.False(LyricsMatchPolicy.IsMediumConfidenceSingle(multiple));
    }

    [Fact]
    public void IsMediumConfidenceSingle_ReturnsFalseWhenNoContentMatch()
    {
        var withinWithoutContent = CreateMatch("1", 180, 2.0, 60, hasContentMatch: false);
        Assert.False(LyricsMatchPolicy.IsMediumConfidenceSingle([withinWithoutContent]));
    }

    [Fact]
    public void HasClearWinner_ReturnsTrueWhenLeadMeetsThresholdAndDeltaWithinWindow()
    {
        var candidates = new[]
        {
            CreateMatch("1", 180, 1.0, 80),
            CreateMatch("2", 180, 5.0, 25),
        };

        Assert.True(LyricsMatchPolicy.HasClearWinner(candidates));
    }

    [Fact]
    public void HasClearWinner_ReturnsFalseWhenLeadIsInsufficient()
    {
        var candidates = new[]
        {
            CreateMatch("1", 180, 1.0, 80),
            CreateMatch("2", 180, 1.2, 70),
        };

        Assert.False(LyricsMatchPolicy.HasClearWinner(candidates));
    }

    [Fact]
    public void HasClearWinner_ReturnsFalseWhenLeadIsOnlyDurationEvidence()
    {
        var candidates = new[]
        {
            CreateMatch("1", 180, 0.2, 100, hasContentMatch: false),
            CreateMatch("2", 180, 5.0, 25, hasContentMatch: false),
        };

        Assert.False(LyricsMatchPolicy.HasClearWinner(candidates));
    }

    [Fact]
    public void HasClearWinner_ReturnsFalseWhenWinnerDeltaIsTooLarge()
    {
        var candidates = new[]
        {
            CreateMatch("1", 180, 3.5, 80),
            CreateMatch("2", 180, 7.0, 20),
        };

        Assert.False(LyricsMatchPolicy.HasClearWinner(candidates));
    }

    [Fact]
    public void HasClearWinner_ReturnsFalseWhenFewerThanTwoCandidates()
    {
        Assert.False(LyricsMatchPolicy.HasClearWinner([]));
        Assert.False(LyricsMatchPolicy.HasClearWinner([CreateMatch("1", 180, 1.0, 80)]));
    }
}
