using AppleMusicLyrics.Core.Parsing;
using Xunit;

namespace AppleMusicLyrics.Tests.Parsing;

public sealed class TtmlLyricsParserTests
{
    [Fact]
    public void ParseTtml_InheritsSongPartFromDiv()
    {
        var parser = new TtmlLyricsParser();
        const string ttml = """
            <tt xmlns="http://www.w3.org/ns/ttml">
              <body>
                <div itunes:songPart="verse" xmlns:itunes="http://www.itunes.com/dtds/itunes">
                  <p begin="0.000" end="1.000">hello world</p>
                </div>
              </body>
            </tt>
            """;

        var lines = parser.ParseTtml(ttml);

        var line = Assert.Single(lines);
        Assert.Equal("hello world", line.Text);
        Assert.Equal("verse", line.SongPart);
    }

    [Fact]
    public void ParseLyricsJson_ExtractsDurationAndNativeOffsets()
    {
        var parser = new TtmlLyricsParser();
        const string json = """
            {
              "lyricsId": "rap-god",
              "status": "success",
              "ttml": "<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\"><head><metadata><iTunesMetadata xmlns=\"http://music.apple.com/lyric-ttml-internal\" leadingSilence=\"0.120\"><audio lyricOffset=\"-0.090\" /></iTunesMetadata></metadata></head><body dur=\"6:03.521\"><div><p begin=\"1.160\" end=\"4.488\">Look, I was gonna go easy on you</p></div></body></tt>"
            }
            """;

        var document = parser.ParseLyricsJson(json, "rap-god.json");

        Assert.NotNull(document.DurationSeconds);
        Assert.Equal(363.521, document.DurationSeconds!.Value, 3);
        Assert.Equal(0.120, document.LeadingSilenceSeconds, 3);
        Assert.Equal(-0.090, document.NativeOffsetSeconds, 3);
    }
}
