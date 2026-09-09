using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Debugger.Models;
using AppleMusicLyrics.Debugger.Services;
using Xunit;

namespace AppleMusicLyrics.Tests.Debugger;

public sealed class LyricsTextComparatorTests
{
    [Fact]
    public void Evaluate_ReturnsInstrumental_WhenNeitherSoftwareNorGroundTruthHasLyrics()
    {
        var player = new PlayerState("Intro", "Composer", "Album", 120.0, 0.0, true);
        var groundTruth = new GroundTruthResult(false, "None", "Intro", "Composer", null, Array.Empty<string>());

        var (verdict, reason, _) = LyricsTextComparator.Evaluate(null, player, groundTruth);

        Assert.Equal(VerdictStatus.Instrumental, verdict);
        Assert.Contains("纯音乐", reason);
    }

    [Fact]
    public void Evaluate_ReturnsMissed_WhenGroundTruthHasLyricsButSoftwareHasNone()
    {
        var player = new PlayerState("Anti-Hero", "Taylor Swift", "Midnights", 200.0, 10.0, true);
        var groundTruth = new GroundTruthResult(
            true,
            "LRCLIB",
            "Anti-Hero",
            "Taylor Swift",
            "[00:10.00] It's me, hi, I'm the problem, it's me",
            new[] { "It's me, hi, I'm the problem, it's me" });

        var (verdict, reason, _) = LyricsTextComparator.Evaluate(null, player, groundTruth);

        Assert.Equal(VerdictStatus.Missed, verdict);
        Assert.Contains("未能识别", reason);
    }

    [Fact]
    public void Evaluate_ReturnsPass_WhenLyricsMatchGroundTruth()
    {
        var player = new PlayerState("Anti-Hero", "Taylor Swift", "Midnights", 200.0, 10.0, true);
        var groundTruth = new GroundTruthResult(
            true,
            "LRCLIB",
            "Anti-Hero",
            "Taylor Swift",
            "[00:10.00] It's me, hi, I'm the problem, it's me\n[00:15.00] At tea time, everybody agrees",
            new[] { "It's me, hi, I'm the problem, it's me", "At tea time, everybody agrees" });

        var softwareDoc = new LyricsDocument(
            LyricsId: "123",
            Status: "OK",
            SourceFile: "ttmlLyrics[1].json",
            UpdatedAt: DateTimeOffset.UtcNow,
            Lines: new[]
            {
                new LyricsLine(10.0, 14.0, "It's me, hi, I'm the problem, it's me"),
                new LyricsLine(15.0, 19.0, "At tea time, everybody agrees")
            },
            DurationSeconds: 200.0);

        var (verdict, reason, similarity) = LyricsTextComparator.Evaluate(softwareDoc, player, groundTruth);

        Assert.Equal(VerdictStatus.Pass, verdict);
        Assert.True(similarity > 0.5);
    }

    [Fact]
    public void Evaluate_ReturnsMisidentified_WhenSoftwareDisplaysCompletelyDifferentLyrics()
    {
        var player = new PlayerState("Go Get 'em", "CupcakKe", "Album", 180.0, 10.0, true);
        // Ground truth for Go Get 'em
        var groundTruth = new GroundTruthResult(
            true,
            "LRCLIB",
            "Go Get 'em",
            "CupcakKe",
            "Go get em, go get em, let's run it up\nStacks in the bank, never get enough\nPull up in the coupe, watch me strut",
            new[]
            {
                "Go get em, go get em, let's run it up",
                "Stacks in the bank, never get enough",
                "Pull up in the coupe, watch me strut",
                "Bitches talk shit, but they never show up",
                "Running to the bag, yeah we speeding up",
                "Diamond chain frozen, yeah you see it glow"
            });

        // Software mistakenly picked lyrics for "Bedbugs" / "Alien Pussy"
        var wrongSoftwareDoc = new LyricsDocument(
            LyricsId: "999",
            Status: "OK",
            SourceFile: "ttmlLyrics[bedbugs].json",
            UpdatedAt: DateTimeOffset.UtcNow,
            Lines: new[]
            {
                new LyricsLine(1.0, 5.0, "Underneath the mattress where the monsters sleep"),
                new LyricsLine(6.0, 10.0, "Crawling in the shadows six feet deep"),
                new LyricsLine(11.0, 15.0, "Scratching on the wall, nothing left to keep"),
                new LyricsLine(16.0, 20.0, "Spiders in the closet make the children weep"),
                new LyricsLine(21.0, 25.0, "Nightmare in the attic, silent when they creep"),
                new LyricsLine(26.0, 30.0, "Darkness taking over, sinking in the deep")
            },
            DurationSeconds: 180.0);

        var (verdict, reason, _) = LyricsTextComparator.Evaluate(wrongSoftwareDoc, player, groundTruth);

        Assert.Equal(VerdictStatus.Misidentified, verdict);
        Assert.Contains("错判", reason);
    }
}
