using AppleMusicLyrics.Core.Matching;
using Xunit;

namespace AppleMusicLyrics.Tests.Matching;

public sealed class MetadataMatchingTests
{
    [Theory]
    [InlineData("cupcakKe — The BakKery", null, "cupcakKe", "The BakKery")]
    [InlineData("Taylor Swift – 1989", null, "Taylor Swift", "1989")]
    [InlineData("Linkin Park - Meteora", null, "Linkin Park", "Meteora")]
    [InlineData("cupcakKe — The BakKery", "The BakKery", "cupcakKe", "The BakKery")]
    [InlineData("Solo Artist", null, "Solo Artist", null)]
    [InlineData("Solo Artist", "Solo Album", "Solo Artist", "Solo Album")]
    public void ParseArtistAndAlbum_SeparatesArtistAndAlbum(
        string rawArtist,
        string? rawAlbum,
        string expectedArtist,
        string? expectedAlbum)
    {
        var (artist, album) = MetadataMatching.ParseArtistAndAlbum(rawArtist, rawAlbum);
        Assert.Equal(expectedArtist, artist);
        Assert.Equal(expectedAlbum, album);
    }

    [Fact]
    public void DocumentContainsTitle_MatchesCensoredLyricsLines()
    {
        var lines = new[]
        {
            "Ayo, get the fuck out the bed, motherfucker, go grab it",
            "A-E-I-O-U, **** now",
            "I smelled a broke ****, now I got bronchitis",
        };

        Assert.True(MetadataMatching.DocumentContainsTitle("New Nigga Now", lines));
    }

    [Fact]
    public void DocumentContainsTitle_MatchesExactTitle()
    {
        var lines = new[]
        {
            "Hello world",
            "Rolling in the deep",
            "Another line",
        };

        Assert.True(MetadataMatching.DocumentContainsTitle("Rolling in the Deep", lines));
        Assert.False(MetadataMatching.DocumentContainsTitle("Someone Like You", lines));
    }

    [Fact]
    public void DocumentContainsTitle_RejectsScatteredWordsAcrossDifferentLines()
    {
        var lines = new[]
        {
            "Gotta get it, get it, feel it, bust it",
            "Said you finna go, boy, so go 'head",
            "Remember what we had together",
        };

        // "go", "get", and "em" (in "remember") appear in different lines, but NOT together in the same line
        Assert.False(MetadataMatching.DocumentContainsTitle("Go Get 'em", lines));
    }

    [Fact]
    public void DocumentContainsTitle_MatchesPhraseInSameLine()
    {
        var lines = new[]
        {
            "Haha-haha-haha-haha-haha",
            "I don't give a fuck, bitch, you better go, go get 'em",
        };

        Assert.True(MetadataMatching.DocumentContainsTitle("Go Get 'em", lines));
    }
}
