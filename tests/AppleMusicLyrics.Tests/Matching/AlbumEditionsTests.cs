using AppleMusicLyrics.Core.Matching;
using Xunit;

namespace AppleMusicLyrics.Tests.Matching;

public sealed class AlbumEditionsTests
{
    [Theory]
    [InlineData("Butterfly: 25th Anniversary Expanded Edition", "Butterfly")]
    [InlineData("E=MC2 (Deluxe Version)", "E=MC2")]
    [InlineData("Memoirs of an Imperfect Angel (Special Edition)", "Memoirs of an Imperfect Angel")]
    [InlineData("Age Ain't Nothing But a Number (Deluxe)", "Age Ain't Nothing But a Number")]
    [InlineData("In the Lonely Hour (10th Anniversary Edition) [Deluxe]", "In the Lonely Hour")]
    [InlineData("Born To Die – The Paradise Edition (Special Version)", "Born To Die")]
    [InlineData("Confessions on a Dance Floor (Twenty Years Edition)", "Confessions on a Dance Floor")]
    [InlineData("Confessions II (After Hours Edition)", "Confessions II")]
    [InlineData("Dangerous Woman (Bonus Tracks Edition)", "Dangerous Woman")]
    [InlineData("Never Enough (Bonus Version)", "Never Enough")]
    [InlineData("Christina Aguilera (Expanded Edition)", "Christina Aguilera")]
    [InlineData("American Gurl (Super Deluxe Value Size)", "American Gurl")]
    [InlineData("Detour (Rare & Deluxe)", "Detour")]
    [InlineData("SOUR (Video Version)", "SOUR")]
    [InlineData("thank u, next (bonus videos)", "thank u, next")]
    [InlineData("Rumours (Super Deluxe)", "Rumours")]
    [InlineData("Abbey Road (Remastered 2019)", "Abbey Road")]
    [InlineData("Thriller 25 [Anniversary Edition]", "Thriller 25")]
    [InlineData("1989 - Deluxe Edition", "1989")]
    [InlineData("Discovery (Edición Deluxe)", "Discovery")]
    [InlineData("十一月的萧邦（豪华版）", "十一月的萧邦")]
    public void GetOriginalAlbum_RemovesReissueSuffixes(string album, string original) =>
        Assert.Equal(original, AlbumEditions.GetOriginalAlbum(album));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Butterfly")]
    [InlineData("Love Deluxe")] // Sade's album title, not a reissue
    [InlineData("SOS Deluxe: LANA")]
    [InlineData("brat and it's completely different but also still brat")]
    [InlineData("Red (Taylor's Version)")]
    [InlineData("1989 (Taylor's Version) [Deluxe]")]
    [InlineData("MTV Unplugged (Deluxe Edition)")]
    [InlineData("Thank U, Next (Live Edition)")]
    [InlineData("Positions (Deluxe) [Clean]")]
    [InlineData("The Remixes (Expanded Edition)")]
    [InlineData("(Deluxe)")]
    public void GetOriginalAlbum_LeavesOtherAlbumsAlone(string? album) =>
        Assert.Null(AlbumEditions.GetOriginalAlbum(album));
}
