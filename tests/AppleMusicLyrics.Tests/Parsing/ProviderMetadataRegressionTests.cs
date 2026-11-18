using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Parsing;
using Xunit;

namespace AppleMusicLyrics.Tests.Parsing;

public sealed class ProviderMetadataRegressionTests
{
    // Formats found by auditing the user's complete cache, with names shortened where irrelevant.
    public static TheoryData<string> CachedMetadata => new()
    {
        "编曲 Arrangement：焦安溥 anpu / 陈珊妮 Chan, Sandee",
        "和声 Backing Vocals：陈珊妮 Chan, Sandee",
        "录音师 Recording Engineer：陈文骏 Chen, AJ",
        "混音 Mixing Engineer：陈文骏@强力录音室",
        "母带后期处理 Mastering：Greg Calbi, Mastering Engineer",
        "制作助理 Producer Assistant：林思彤 Lin, Pada",
        "Piano : Ioshua Wan",
        "String Arrangement : Bang Wen Fu/蔡健雅",
        "Recording Engineers : Caveman/Glow",
        "Recording Studio : Boss Studio (Singapore)",
        "Editing Engineer : Edwin",
        "歌曲：Glory Box",
        "Artist: Olivia Rodrigo",
        "歌 手： Aaliyah",
        "Engineered by：Charles Moniz",
        "Assistant Engineered by：Alex Resoagli",
        "弦乐录音 String Recording：林尚伯 Shang-Po Lin",
        "人声录音 Vocal Recording：许哲珮 Peggy Hsu",
        "制作助理 Production Assistant：朱彩蓁 Cai-Jhen Jhu",
        "Track Produced by：ZENTA",
        "Album: Spice",
        "Title: Say You'll Be There",
        "制作助理：郑瀚椲、蔡坤奇",
        "Songs Title：have you ever",
        "Recorded at：Highmoor Farm/Angelic Studios/Henson Studios",
        "Background Vocals：Delacey",
        "Samples：Hot Shit(2001)—Big Hawk",
        "歌名:say somethin'",
        "专辑:the emancipation of mimi",
        "作词作曲：伊秩 弘将",
        "Song:Heaven On Earth",
        "Singer:Britney Spears",
        "钢琴 Piano：李宜玲 Lee, Elin",
        "吉他 Guitar：徐平 Hsu, Ping",
        "鼓 Drums：廖柏钧 Liao, Suyeh",
        "唢呐 Suona：许家铭 Hsu, Chia Ming",
        "合成 Programming：陈珊妮 Chan, Sandee",
        "和声编写 Backing Vocals Arrangement：陈珊妮 Chan, Sandee",
        "Vocal Recording Engineer : 蔡健雅",
        "Vocal Recording Studio : Tangy Studio",
        "Mixing Studio : Yohu Studio",
        "制作人 Producers：王希文 Owen Wang / 许哲珮 Peggy Hsu",
        "小提琴 Violin：苏子茵 Daphne Su",
        "大提琴 Cello：唐莺绮 Ying-Chi Tang",
        "电吉他：钟成虎",
        "小提琴1：廖维琪",
        "小提琴2：侯勇光",
        "中提琴：黄维琪",
        "大提琴：吴世杰",
        "和声编写：陈绮贞",
        "录音室：林暐哲音乐社",
        "混音室：林暐哲音乐社",
        "Mixing Assistant：Matt Wolach",
        "Assistant Engineers：Katie May/Luke Gibbs",
        "Vocals Tubular Bells：Harry Styles",
        "Drum Programming/Synths：Kid Harpoon/Tyler Johnson",
        "Bass Guitar/Synth Bass：Kid Harpoon",
        "Percussion Programming：Doug Showalter",
        "Electric Guitars：Kid Harpoon/Doug Showalter",
        "Upright Piano：Tyler Johnson",
        "Vocal Programming：Jeremy Hatcher",
        "Voice Memo：Ruby Winston",
        "Assistant Mixing Engineer：John Hanes",
        "Engineering：Louis Bell",
        "Studio Personnel：John Hanes/Serban Ghenea",
        "A&R：Jeremy Vuernick",
        "A&R Admin：Ryan Del Vecchio",
        "Vocal Production：Dylan William",
        "Interpolates：Cry Me a River by Justin Timberlake",
        "Sung By \"Brandy\"",
        "{more Carly Rae Jepsen lyrics at lyricsify.com/u/KPopLRCFiles}",
        "此歌曲为没有填词的纯音乐，请您欣赏",
        "暂不支持歌词",
    };

    [Theory]
    [MemberData(nameof(CachedMetadata))]
    public void Clean_RemovesMetadataFormatsFoundInTheCache(string text)
    {
        Assert.True(LyricsTranslationFilter.IsCreditOrNotice(text));
        Assert.Null(LyricsTranslationFilter.Clean(text));
    }

    [Theory]
    [InlineData("Time: now")]
    [InlineData("Subject: me and you")]
    [InlineData("Location: my place")]
    [InlineData("Music like: Lay Lady Lay")]
    [InlineData("All of the people start to rush start to rush by")]
    [InlineData("Thank you for recording")]
    [InlineData("Mixed with amber and waves of gold")]
    [InlineData("Save this recording because I'm never coming back home")]
    [InlineData("DJ Mustard制作")]
    [InlineData("(制作人水印)")]
    [InlineData("[Producer Tay Keith的水印]")]
    public void Clean_KeepsSungLinesAndProducerTagsFoundInTheCache(string text)
    {
        Assert.Equal(text, LyricsTranslationFilter.Clean(text));
    }

    [Theory]
    [InlineData("Song - Artist", "Song", "Artist", true)]
    [InlineData("Song (Explicit) - Artist (艺人)", "Song", "Artist", true)]
    [InlineData("Artist - Song", "Song", "Artist", true)]
    [InlineData("Song - Artist/Guest", "Song", "Artist", true)]
    [InlineData("Blow Your Mind (Mwah) (Explicit) - Dua Lipa", "Blow Your Mind (Mwah)", "Dua Lipa", true)]
    [InlineData("Black Widow (Explicit) - Iggy Azalea/Rita Ora", "Black Widow (feat. Rita Ora)", "Iggy Azalea", true)]
    [InlineData("Age Ain't Nothing but a Number (LP Mix - No Intro) - Aaliyah", "Age Ain't Nothing but a Number", "Aaliyah", true)]
    [InlineData("We shared a name - a home", "Song", "Artist", false)]
    [InlineData("Song - Someone Else", "Song", "Artist", false)]
    [InlineData("Song", "Song", "Artist", false)]
    public void Clean_RemovesOnlyAnOpeningHeaderVerifiedAgainstBothTrackFields(string text, string title, string artist, bool remove)
    {
        var opening = new LyricsLine(0, 3, text);
        var sung = new LyricsLine(5, 8, "Hello", Words: [new(5, 8, "Hello")]);
        var document = new LyricsDocument("id", "success", "qqmusic:1", DateTimeOffset.UtcNow, [opening, sung]);
        var player = new PlayerState(title, artist, null, 0, 30, false);

        var cleaned = LyricsContentFilter.Clean(document, player);

        Assert.Equal(remove ? 1 : 2, cleaned.Lines.Count);
        Assert.Same(sung, cleaned.Lines[^1]);
        Assert.Same(cleaned, LyricsContentFilter.Clean(document, player with { Position = 10 }));
        Assert.Same(document, LyricsContentFilter.Clean(document));
    }

    [Theory]
    [InlineData("[Verse 1: Troye Sivan]", true)]
    [InlineData("[Chorus: Troye Sivan & Charli xcx]", true)]
    [InlineData("【Part I: Playboy】", true)]
    [InlineData("副歌：", true)]
    [InlineData("Madonna：", true)]
    [InlineData("Macy Gray：Dangerous love", false)]
    [InlineData("Listen closely:", false)]
    [InlineData("Step 1:", false)]
    [InlineData("Unknown Singer:", false)]
    public void Clean_SkipsStandaloneSectionAndVerifiedPerformerMarkersWhileKeepingSungWords(string text, bool remove)
    {
        var line = new LyricsLine(0, 3, text);
        var document = new LyricsDocument("id", "success", "qqmusic:1", DateTimeOffset.UtcNow, [line]);
        var player = new PlayerState("Song", "Madonna", null, 0, 30, false);

        Assert.Equal(remove ? 0 : 1, LyricsContentFilter.Clean(document, player).Lines.Count);
    }

    [Fact]
    public void Clean_RemovesVerifiedClosingHeadersWithoutRemovingDashLyricsInTheBody()
    {
        var sung = new LyricsLine(0, 3, "We shared a name - a home");
        var document = new LyricsDocument("id", "success", "qqmusic:1", DateTimeOffset.UtcNow,
            [sung, new(3, 6, "Sunflowers For Alfred Roy - Mariah Carey")]);
        var player = new PlayerState("Sunflowers For Alfred Roy", "Mariah Carey", null, 0, 30, false);

        Assert.Same(sung, Assert.Single(LyricsContentFilter.Clean(document, player).Lines));
    }
}
