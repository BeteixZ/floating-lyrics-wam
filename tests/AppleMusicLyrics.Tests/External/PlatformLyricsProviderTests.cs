using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Infrastructure.Windows.External;
using Xunit;

namespace AppleMusicLyrics.Tests.External;

public sealed class PlatformLyricsProviderTests
{
    private static readonly PlayerState Player = new("Song", "Artist", "Album", 0, 180, true);

    [Theory]
    [InlineData("Other Song", "Artist", 180)]
    [InlineData("Song", "Other Artist", 180)]
    [InlineData("Song (Live)", "Artist", 180)]
    [InlineData("Song", "Artist", 210)]
    public void ScoreCandidate_RejectsDifferentRecordings(string title, string artist, double duration)
    {
        Assert.Equal(int.MinValue, PlatformLyricsSupport.ScoreCandidate(Player, title, artist, "Album", duration));
    }

    [Fact]
    public void ScoreCandidate_AcceptsAnArtistAmongMultipleSingers()
    {
        Assert.NotEqual(int.MinValue, PlatformLyricsSupport.ScoreCandidate(Player, "Song", "Artist, Guest", "Album", 180));
    }

    [Fact]
    public void ScoreCandidate_PrefersDurationOverAnAlbumEditionSuffix()
    {
        var sameRecording = PlatformLyricsSupport.ScoreCandidate(Player, "Song", "Artist", "Album Deluxe", 180.1);
        var differentMaster = PlatformLyricsSupport.ScoreCandidate(Player, "Song", "Artist", "Album", 182.6);
        Assert.True(sameRecording > differentMaster);
    }

    [Theory]
    [InlineData("immature", "immature (Album Version)")]
    [InlineData("immature (Album Version)", "immature")]
    [InlineData("immature", "immature [album version]")]
    [InlineData("immature", "immature（Album Version）")]
    public void ScoreCandidate_AcceptsAlbumVersionAliasWithMatchingRecordingEvidence(string playingTitle, string candidateTitle)
    {
        var player = new PlayerState(playingTitle, "浜崎あゆみ", "loveppears", 0, 283, true);
        Assert.NotEqual(int.MinValue, PlatformLyricsSupport.ScoreCandidate(player,
            candidateTitle, "浜崎あゆみ", "LOVEppears", 283.366));
    }

    [Theory]
    [InlineData("Other Artist", "Album", 180.0, 180.0)]
    [InlineData("Artist", "Other Album", 180.0, 180.0)]
    [InlineData("Artist", "Album Deluxe", 180.0, 180.0)]
    [InlineData("Artist", null, 180.0, 180.0)]
    [InlineData("Artist", "Album", null, 180.0)]
    [InlineData("Artist", "Album", 0.0, 180.0)]
    [InlineData("Artist", "Album", 180.0, 0.0)]
    [InlineData("Artist", "Album", 180.0, double.NaN)]
    [InlineData("Artist", "Album", double.NaN, 180.0)]
    [InlineData("Artist", "Album", 182.01, 180.0)]
    public void ScoreCandidate_AlbumVersionAliasRequiresExactArtistAlbumAndCloseKnownDuration(
        string artist, string? album, double? candidateDuration, double playingDuration)
    {
        Assert.Equal(int.MinValue, PlatformLyricsSupport.ScoreCandidate(Player with { Duration = playingDuration },
            "Song (Album Version)", artist, album, candidateDuration));
    }

    [Theory]
    [InlineData("Song", "Song (JT Original CM Version)")]
    [InlineData("Song", "Song (Acoustic Version)")]
    [InlineData("Song", "Song (Live) (Album Version)")]
    [InlineData("Song (Live)", "Song (Album Version)")]
    [InlineData("Song", "Song (Remix) (Album Version)")]
    [InlineData("Song", "Other Song (Album Version)")]
    public void ScoreCandidate_AlbumVersionAliasDoesNotAdmitOtherTitlesOrVersions(string playingTitle, string candidateTitle)
    {
        Assert.Equal(int.MinValue, PlatformLyricsSupport.ScoreCandidate(Player with { Title = playingTitle },
            candidateTitle, "Artist", "Album", 180));
    }

    [Theory]
    [InlineData("One and Only (feat. Twista)", "One And Only")]
    [InlineData("One and Only [featuring Twista]", "One And Only")]
    [InlineData("One and Only（ft. Twista）", "One And Only")]
    [InlineData("One and Only feat. Twista", "One And Only")]
    [InlineData("One and Only - ft Twista", "One And Only")]
    [InlineData("One and Only", "One And Only (feat. Twista)")]
    [InlineData("One and Only (feat. Twista)", "One And Only featuring TWISTA")]
    public void ScoreCandidate_AcceptsExplicitGuestMetadataWithoutChangingTitleThreshold(string playingTitle, string candidateTitle)
    {
        Assert.NotEqual(int.MinValue, PlatformLyricsSupport.ScoreCandidate(Player with { Title = playingTitle },
            candidateTitle, "Artist", "Album", 180));
    }

    [Theory]
    [InlineData("One and Only (feat. Twista)", "One and Only (feat. Another Guest)")]
    [InlineData("One and Only feat. Twista", "One and Only featuring Another Guest")]
    [InlineData("One and Only (Live)", "One and Only (feat. Twista)")]
    [InlineData("One and Only (feat. Twista) (Live)", "One and Only")]
    [InlineData("One and Only feat. Twista (Live)", "One and Only")]
    [InlineData("One and Only (feat. Twista - Remix)", "One and Only")]
    [InlineData("One and Only (feat. Twista) (Intro)", "One and Only")]
    [InlineData("One and Only (feat. Twista) (Album Version)", "One and Only")]
    [InlineData("One and Only", "A Completely Different One and Only")]
    [InlineData("(feat. Twista)", "One and Only")]
    public void ScoreCandidate_GuestMetadataDoesNotAdmitDifferentCreditsOrRecordingVariants(string playingTitle, string candidateTitle)
    {
        Assert.Equal(int.MinValue, PlatformLyricsSupport.ScoreCandidate(Player with { Title = playingTitle },
            candidateTitle, "Artist", "Album", 180));
    }

    [Theory]
    [InlineData("One and Only (feat. Twista) (Live)")]
    [InlineData("One and Only feat. Twista (Live)")]
    public void SearchTerm_RemovesOnlyExplicitGuestCreditsAndRetainsTheRecordingVariant(string title)
    {
        Assert.Equal("One and Only (Live) Artist", PlatformLyricsSupport.SearchTerm(Player with { Title = title }));
        Assert.NotEqual(int.MinValue, PlatformLyricsSupport.ScoreCandidate(Player with { Title = title },
            "One and Only (Live)", "Artist", "Album", 180));
    }

    [Theory]
    [InlineData("The Feature Presentation")]
    [InlineData("Defeating The Night")]
    [InlineData("One and Only (feat. Twista - Live)")]
    public void SearchTerm_LeavesOrdinaryWordsAndAmbiguousVariantCreditsIntact(string title)
    {
        Assert.Equal(title + " Artist", PlatformLyricsSupport.SearchTerm(Player with { Title = title }));
    }

    [Fact]
    public async Task FetchAsync_CachesCompletedLyricsAndHonorsCancellationEvenOnACacheHit()
    {
        using var provider = new ProbeProvider();
        var first = await provider.FetchAsync(Player);
        Assert.Same(first, await provider.FetchAsync(Player));
        Assert.Equal(1, provider.Calls);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.FetchAsync(Player, cancellation.Token));
    }

    [Fact]
    public async Task FetchAsync_DoesNotCacheTransientFailures()
    {
        using var provider = new ProbeProvider { FailOnce = true };
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.FetchAsync(Player));
        Assert.NotNull(await provider.FetchAsync(Player));
        Assert.Equal(2, provider.Calls);
    }

    private sealed class ProbeProvider : PlatformLyricsProvider
    {
        public override string Name => "Probe";
        public int Calls { get; private set; }
        public bool FailOnce { get; init; }

        protected override Task<LyricsDocument?> FetchCoreAsync(PlayerState player, CancellationToken cancellationToken)
        {
            Calls++;
            if (FailOnce && Calls == 1)
            {
                throw new HttpRequestException("temporary");
            }
            return Task.FromResult<LyricsDocument?>(new LyricsDocument("probe:1", "success", "probe:1",
                DateTimeOffset.UtcNow, [new LyricsLine(1, 2, "synthetic line")], player.Duration));
        }
    }
}
