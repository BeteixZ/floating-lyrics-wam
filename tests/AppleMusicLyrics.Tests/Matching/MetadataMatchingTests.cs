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

    [Theory]
    [InlineData("Ocean", "I crossed the ocean once")]
    [InlineData("Home", "I am going home")]
    [InlineData("You", "I will always love you")]
    [InlineData("Me", "Stay with me")]
    public void GetTitleEvidence_SingleWordTitleIsOnlyWeak(string title, string line)
    {
        Assert.Equal(
            AppleMusicLyrics.Core.Models.TitleEvidenceStrength.Weak,
            MetadataMatching.GetTitleEvidence(title, [line]));
    }

    [Fact]
    public void GetTitleEvidence_DoesNotJoinWordsOrMatchInsideAnotherWord()
    {
        Assert.Equal(
            AppleMusicLyrics.Core.Models.TitleEvidenceStrength.None,
            MetadataMatching.GetTitleEvidence("Meyou", ["Now you turn me into a nebula"]));
        Assert.Equal(
            AppleMusicLyrics.Core.Models.TitleEvidenceStrength.None,
            MetadataMatching.GetTitleEvidence("Eple", ["All the people gather around"]));
    }

    [Fact]
    public void GetTitleEvidence_ContiguousMultiWordPhraseIsStrong()
    {
        Assert.Equal(
            AppleMusicLyrics.Core.Models.TitleEvidenceStrength.Strong,
            MetadataMatching.GetTitleEvidence("Rolling in the Deep", ["We could have had it all, rolling in the deep"]));
    }

    [Fact]
    public void IsRepetitiveVocalizationOnly_DetectsMeyouCommunityTranscript()
    {
        var lines = new[]
        {
            "Me, meyou", "Me, meyou", "Me, meyou", "Me, meyou, you",
            "Me, meyou", "Meyou, meyou", "Me, meyou", "Me, meyou",
            "Me, meyou", "Me, meyou", "Meyou, meyou", "Me, meyou, ooh",
            "Me, meyou",
        };

        Assert.True(MetadataMatching.IsRepetitiveVocalizationOnly("Meyou", lines));
    }

    [Fact]
    public void IsRepetitiveVocalizationOnly_KeepsNormallyRepetitiveLyrics()
    {
        var lines = Enumerable.Repeat("Around the world", 12);

        Assert.False(MetadataMatching.IsRepetitiveVocalizationOnly("Around the World", lines));
    }
}
