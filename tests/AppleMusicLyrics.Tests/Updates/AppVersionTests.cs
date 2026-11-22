using AppleMusicLyrics.App;
using AppleMusicLyrics.Core.Updates;
using Xunit;

namespace AppleMusicLyrics.Tests.Updates;

public sealed class AppVersionTests
{
    [Theory]
    [InlineData("v0.8.0", 0, 8, 0, null)]
    [InlineData("0.8.0", 0, 8, 0, null)]
    [InlineData("0.8.0+95afb330b228f64c548103a8385c65ad0851c0dc", 0, 8, 0, null)]
    [InlineData("0.8.0.0", 0, 8, 0, null)]
    [InlineData("V1.2", 1, 2, 0, null)]
    [InlineData("v0.9.0-beta.1", 0, 9, 0, "beta.1")]
    public void TryParse_AcceptsTagsAndAssemblyVersions(string text, int major, int minor, int patch, string? preRelease)
    {
        Assert.True(AppVersion.TryParse(text, out var version));
        Assert.Equal(new AppVersion(major, minor, patch, preRelease), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v1")]
    [InlineData("v1.2.x")]
    [InlineData("v1.2.3-")]
    [InlineData("dev-abc1234")]
    public void TryParse_RejectsNonVersions(string? text) => Assert.False(AppVersion.TryParse(text, out _));

    [Theory]
    [InlineData("0.8.1", "0.8.0")]
    [InlineData("0.10.0", "0.9.9")]
    [InlineData("1.0.0", "0.99.0")]
    [InlineData("1.0.0", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.10", "1.0.0-rc.9")]
    [InlineData("1.0.0-beta", "1.0.0-alpha")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha")]
    public void Ordering_FollowsSemanticVersioning(string higher, string lower)
    {
        Assert.True(AppVersion.TryParse(higher, out var a));
        Assert.True(AppVersion.TryParse(lower, out var b));
        Assert.True(a > b);
        Assert.True(b < a);
    }

    [Fact]
    public void ShouldOffer_OnlyNewerAndNotSkippedUnlessCheckedManually()
    {
        var current = new AppVersion(0, 8, 0);
        var next = new AppVersion(0, 9, 0);

        Assert.True(UpdatePolicy.ShouldOffer(next, current, null, manualCheck: false));
        Assert.False(UpdatePolicy.ShouldOffer(current, current, null, manualCheck: true));
        Assert.False(UpdatePolicy.ShouldOffer(new AppVersion(0, 7, 0), current, null, manualCheck: true));
        Assert.False(UpdatePolicy.ShouldOffer(next, current, "0.9.0", manualCheck: false));
        Assert.True(UpdatePolicy.ShouldOffer(next, current, "0.9.0", manualCheck: true));
        // Skipping one release does not silence the next one.
        Assert.True(UpdatePolicy.ShouldOffer(new AppVersion(0, 9, 1), current, "0.9.0", manualCheck: false));
    }

    [Fact]
    public void ShouldOffer_KeepsReleaseCandidatesAwayFromStableBuilds()
    {
        var candidate = new AppVersion(1, 0, 0, "rc.1");

        // Even if a release candidate is published without GitHub's pre-release flag.
        Assert.False(UpdatePolicy.ShouldOffer(candidate, new AppVersion(0, 10, 0), null, manualCheck: true));
        Assert.True(UpdatePolicy.ShouldOffer(new AppVersion(1, 0, 0, "rc.2"), candidate, null, manualCheck: false));
        Assert.True(UpdatePolicy.ShouldOffer(new AppVersion(1, 0, 0), candidate, null, manualCheck: false));
    }

    [Fact]
    public void CleanNotes_TurnsReleaseMarkdownIntoPlainText()
    {
        var notes = UpdateWindow.CleanNotes("## v0.9.0 - Title\r\n\r\n**Lyric sources**\r\n\r\n\r\n- First change\r\n* Second change\r\n");

        Assert.Equal("v0.9.0 - Title\r\n\r\nLyric sources\r\n\r\n• First change\r\n• Second change", notes);
        Assert.Equal(string.Empty, UpdateWindow.CleanNotes("  "));
    }
}
