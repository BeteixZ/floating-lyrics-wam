using System.Text.Json;
using AppleMusicLyrics.Core.Parsing;
using AppleMusicLyrics.Core.Sync;
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

    [Fact]
    public void ParseLyricsJson_ReadsObservedSyllableLyricsContainerAndLocalizedMetadata()
    {
        // Shape/timing minimized from the actual Apple cache; words and translations are synthetic.
        var ttml = Wrap("""
            <p begin="0.616" end="1.624" itunes:key="L32711" ttm:agent="v1"><span begin="0.616" end="1.317">Hello</span> <span begin="1.317" end="1.624">world</span></p>
            """, """
            <iTunesMetadata xmlns="http://music.apple.com/lyric-ttml-internal" leadingSilence="0.180">
              <translations><translation type="subtitle" xml:lang="zh-Hans"><text for="L32711">你好 世界</text></translation></translations>
            </iTunesMetadata>
            """);
        var document = new TtmlLyricsParser().ParseLyricsJson(NativeJson(ttml), "syllable-lyrics.json");

        Assert.Equal("AP_42", document.LyricsId);
        Assert.Equal("success", document.Status);
        Assert.Equal(232.840, document.DurationSeconds!.Value, 3);
        Assert.Equal(.180, document.LeadingSilenceSeconds, 3);
        var line = Assert.Single(document.Lines);
        Assert.Equal("Hello world", line.Text);
        Assert.Equal("你好 世界", line.Translation);
        Assert.Collection(line.Words!,
            word => { Assert.Equal(.616, word.Begin, 3); Assert.Equal(1.317, word.End, 3); Assert.Equal("Hello", word.Text); },
            word => { Assert.Equal(1.317, word.Begin, 3); Assert.Equal(1.624, word.End, 3); Assert.Equal("world", word.Text); });
        Assert.True(LyricWordTiming.IsUsable(line));
    }

    [Fact]
    public void ParseLyricsJson_PrefersLocalizedPayloadOverPlainTtml()
    {
        var localized = Wrap("<p begin=\"1\" end=\"2\" itunes:key=\"L8\">Native line</p>", """
            <iTunesMetadata xmlns="http://music.apple.com/lyric-ttml-internal"><translations><translation xml:lang="zh-Hans"><text for="L8">原生译文</text></translation></translations></iTunesMetadata>
            """);
        var plain = Wrap("<p begin=\"1\" end=\"2\">Unlocalized line</p>");
        var line = Assert.Single(new TtmlLyricsParser().ParseLyricsJson(NativeJson(localized, plain), "native.json").Lines);
        Assert.Equal("Native line", line.Text);
        Assert.Equal("原生译文", line.Translation);
        Assert.Null(line.Words); // A line-only native localization must not invent word clocks.
    }

    [Theory]
    [InlineData(null, "42")]
    [InlineData(null, null)]
    public void ParseLyricsJson_NormalizesCatalogFallbackWhenPlayIdIsAbsent(string? playId, string? catalogId)
    {
        var document = new TtmlLyricsParser().ParseLyricsJson(NativeJson(Wrap("<p begin=\"1\" end=\"2\">line</p>"),
            playId: playId, catalogId: catalogId), "native.json");
        Assert.Equal("AP_42", document.LyricsId);
    }

    [Theory]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":[{\"id\":\"42\",\"attributes\":{}}]}")]
    [InlineData("[]")]
    public void ParseLyricsJson_DoesNotTreatUnsupportedOrEmptyPayloadAsUsable(string json)
    {
        Assert.Throws<InvalidOperationException>(() => new TtmlLyricsParser().ParseLyricsJson(json, "empty.json"));
    }

    [Fact]
    public void ParseTtml_UntimedSpaceRemainsVisibleWithoutExtendingAbsoluteWordClocks()
    {
        var line = Assert.Single(new TtmlLyricsParser().ParseTtml(Wrap("""
            <p begin="10" end="12"><span begin="10" end="11">Hello</span> <span begin="11" end="12">world</span></p>
            """)));
        Assert.Equal("Hello world", line.Text);
        Assert.True(LyricWordTiming.IsUsable(line));
        Assert.Collection(line.Words!,
            word => { Assert.Equal("Hello", word.Text); Assert.Equal(10, word.Begin); Assert.Equal(11, word.End); },
            word => { Assert.Equal("world", word.Text); Assert.Equal(11, word.Begin); Assert.Equal(12, word.End); });
    }

    [Fact]
    public void ParseTtml_UntimedCommaRemainsVisibleWithoutBorrowingEitherWordClock()
    {
        var line = Assert.Single(new TtmlLyricsParser().ParseTtml(Wrap("""
            <p begin="10" end="12"><span begin="10" end="11">Hello</span>, <span begin="11" end="12">world</span></p>
            """)));
        Assert.Equal("Hello, world", line.Text);
        Assert.True(LyricWordTiming.IsUsable(line));
        Assert.Collection(line.Words!,
            word => { Assert.Equal("Hello", word.Text); Assert.Equal(10, word.Begin); Assert.Equal(11, word.End); },
            word => { Assert.Equal("world", word.Text); Assert.Equal(11, word.Begin); Assert.Equal(12, word.End); });
    }

    [Theory]
    [InlineData("Hello ", "world")]
    [InlineData("Hello", " world")]
    public void ParseTtml_WhitespaceInsideTimedSpanRetainsItsSourceClock(string first, string second)
    {
        var line = Assert.Single(new TtmlLyricsParser().ParseTtml(Wrap($"<p begin=\"10\" end=\"12\"><span begin=\"10\" end=\"11\">{first}</span><span begin=\"11\" end=\"12\">{second}</span></p>")));
        Assert.Equal("Hello world", line.Text);
        Assert.True(LyricWordTiming.IsUsable(line));
        Assert.Collection(line.Words!,
            word => { Assert.Equal(first, word.Text); Assert.Equal(10, word.Begin); Assert.Equal(11, word.End); },
            word => { Assert.Equal(second, word.Text); Assert.Equal(11, word.Begin); Assert.Equal(12, word.End); });
    }

    [Fact]
    public void ParseTtml_ExplicitlyTimedWhitespaceSpanRetainsOnlyItsOwnSourceClock()
    {
        var line = Assert.Single(new TtmlLyricsParser().ParseTtml(Wrap("""
            <p begin="10" end="12"><span begin="10" end="11">Hello</span><span begin="11" end="11.1"> </span><span begin="11.1" end="12">world</span></p>
            """)));
        Assert.Equal("Hello world", line.Text);
        Assert.True(LyricWordTiming.IsUsable(line));
        Assert.Collection(line.Words!,
            word => { Assert.Equal("Hello", word.Text); Assert.Equal(10, word.Begin); Assert.Equal(11, word.End); },
            word => { Assert.Equal(" ", word.Text); Assert.Equal(11, word.Begin); Assert.Equal(11.1, word.End); },
            word => { Assert.Equal("world", word.Text); Assert.Equal(11.1, word.Begin); Assert.Equal(12, word.End); });
    }

    [Fact]
    public void ParseTtml_UntimedBodyWordCannotMasqueradeAsAnAdmissibleSeparator()
    {
        var line = Assert.Single(new TtmlLyricsParser().ParseTtml(Wrap("""
            <p begin="10" end="12"><span begin="10" end="11">Hello</span> brave <span begin="11" end="12">world</span></p>
            """)));
        Assert.Equal("Hello brave world", line.Text);
        Assert.Null(line.Words);
        Assert.False(LyricWordTiming.IsUsable(line));
    }

    [Theory]
    [InlineData("你", "好", "你好")]
    [InlineData("part", "ing", "parting")]
    public void ParseTtml_DoesNotInsertSpacesBetweenAdjacentTimedFragments(string first, string second, string expected)
    {
        var line = Assert.Single(new TtmlLyricsParser().ParseTtml(Wrap($"<p begin=\"10\" end=\"12\"><span begin=\"10\" end=\"11\">{first}</span><span begin=\"11\" end=\"12\">{second}</span></p>")));
        Assert.Equal(expected, line.Text);
        Assert.Equal(expected, string.Concat(line.Words!.Select(word => word.Text)));
    }

    [Fact]
    public void ParseTtml_NestedStyleSpansDoNotDuplicateTimingOrText()
    {
        var line = Assert.Single(new TtmlLyricsParser().ParseTtml(Wrap("""
            <p begin="10" end="12"><span begin="10" end="12"><span>Hel</span>lo</span></p>
            """)));
        Assert.Equal("Hello", line.Text);
        var word = Assert.Single(line.Words!);
        Assert.Equal("Hello", word.Text);
        Assert.Equal(10, word.Begin);
        Assert.Equal(12, word.End);
    }

    [Fact]
    public void ParseTtml_UsesDeepestTimedFragmentsWithoutEmittingTheirContainerAgain()
    {
        var line = Assert.Single(new TtmlLyricsParser().ParseTtml(Wrap("""
            <p begin="10" end="12"><span begin="10" end="12"><span begin="10" end="11">Hel</span><span begin="11" end="12">lo</span></span></p>
            """)));
        Assert.Equal("Hello", line.Text);
        Assert.Equal(2, line.Words!.Count);
        Assert.Equal("Hello", string.Concat(line.Words.Select(word => word.Text)));
    }

    [Fact]
    public void ParseTtml_DoesNotMixBackgroundRomanizationOrTranslationIntoForegroundWords()
    {
        var line = Assert.Single(new TtmlLyricsParser().ParseTtml(Wrap("""
            <p begin="10" end="12"><span begin="10" end="11">Hello</span> <span ttm:role="x-bg" begin="10" end="12">(<span begin="10" end="12">echo</span>)</span><span begin="11" end="12">world</span><span ttm:role="x-roman">hallo</span><span ttm:role="x-translation" xml:lang="zh-Hans">你好 世界</span></p>
            """)));
        Assert.Equal("Hello world", line.Text);
        Assert.Equal("你好 世界", line.Translation);
        Assert.Equal("hallo", line.Romanization);
        Assert.True(LyricWordTiming.IsUsable(line));
        Assert.Equal(2, line.Words!.Count);
    }

    [Fact]
    public void ParseTtml_ReadsStandaloneRomanizationParagraphByKey()
    {
        var lines = new TtmlLyricsParser().ParseTtml(Wrap("""
            <p begin="1" end="2" itunes:key="L1">你好世界</p><p begin="1" end="2" itunes:key="L1" ttm:role="x-roman">ni hao shi jie</p><p begin="2" end="3" itunes:key="L2">第二句</p>
            """));
        Assert.Equal(2, lines.Count);
        Assert.Equal("ni hao shi jie", lines[0].Romanization);
        Assert.Equal("你好世界", lines[0].Text);
        Assert.Null(lines[1].Romanization);
    }

    [Fact]
    public void ParseTtml_JoinsMultipleInlineRomanizationSpans()
    {
        var line = Assert.Single(new TtmlLyricsParser().ParseTtml(Wrap("""
            <p begin="1" end="2"><span begin="1" end="1.5">東京</span><span begin="1.5" end="2">タワー</span><span ttm:role="x-roman">toukyou</span><span ttm:role="x-roman">tawaa</span></p>
            """)));
        Assert.Equal("東京タワー", line.Text);
        Assert.Equal("toukyou tawaa", line.Romanization);
    }

    [Fact]
    public void ParseTtml_WithoutRomanization_ReturnsNull()
    {
        var line = Assert.Single(new TtmlLyricsParser().ParseTtml(Wrap("""
            <p begin="1" end="2">Plain line</p>
            """)));
        Assert.Null(line.Romanization);
    }

    [Fact]
    public void ParseTtml_TranslationIdsAreExactNotPositionalAndBackgroundTranslationIsExcluded()
    {
        var ttml = Wrap("""
            <p begin="1" end="2" itunes:key="L32711">First line</p><p begin="2" end="3" itunes:key="L8">Second line</p><p begin="3" end="4" itunes:key="L9">Untranslated</p>
            """, """
            <iTunesMetadata xmlns="http://music.apple.com/lyric-ttml-internal"><translations><translation xml:lang="zh-Hans">
              <text for="L8"><span xmlns="http://www.w3.org/ns/ttml" ttm:role="x-bg">(不应混入)</span> 第二句</text>
              <text for="L32711">第一句 <span xmlns="http://www.w3.org/ns/ttml" ttm:role="x-bg">(伴唱)</span></text>
            </translation></translations></iTunesMetadata>
            """);
        var lines = new TtmlLyricsParser().ParseTtml(ttml);
        Assert.Equal("第一句", lines[0].Translation);
        Assert.Equal("第二句", lines[1].Translation);
        Assert.Null(lines[2].Translation);
        Assert.All(lines, line => Assert.Null(line.Words));
    }

    [Fact]
    public void ParseTtml_DoesNotTreatTransliterationSidecarAsTranslation()
    {
        var ttml = Wrap("<p begin=\"1\" end=\"2\" itunes:key=\"L1\">你好</p>", """
            <iTunesMetadata xmlns="http://music.apple.com/lyric-ttml-internal"><transliterations><transliteration><text for="L1">ni hao</text></transliteration></transliterations></iTunesMetadata>
            """);
        var line = Assert.Single(new TtmlLyricsParser().ParseTtml(ttml));
        Assert.Equal("你好", line.Text);
        Assert.Null(line.Translation);
    }

    [Theory]
    [InlineData("not-a-time", "12")]
    [InlineData("NaN", "12")]
    [InlineData("Infinity", "12")]
    [InlineData("12", "11")]
    [InlineData("0", "1")]
    [InlineData("10", "13")]
    [InlineData("10", "10")]
    public void ParseTtml_InvalidOrRelativeWordClocksFallBackToNativeLine(string begin, string end)
    {
        var line = Assert.Single(new TtmlLyricsParser().ParseTtml(Wrap($"<p begin=\"10\" end=\"12\"><span begin=\"{begin}\" end=\"{end}\">Hello</span></p>")));
        Assert.Equal("Hello", line.Text);
        Assert.Equal(10, line.Begin);
        Assert.Equal(12, line.End);
        Assert.Null(line.Words);
    }

    [Fact]
    public void ParseTtml_AllowsInstantPunctuationAlongsidePositiveDurationWords()
    {
        var line = Assert.Single(new TtmlLyricsParser().ParseTtml(Wrap("""
            <p begin="10" end="12"><span begin="10" end="11">Hello</span><span begin="11" end="11">,</span></p>
            """)));
        Assert.Equal("Hello,", line.Text);
        Assert.Equal(2, line.Words!.Count);
        Assert.Equal(11, line.Words[1].Begin);
        Assert.Equal(11, line.Words[1].End);
    }

    [Fact]
    public void ParseTtml_PunctuationOnlyTimingsDoNotAdvertiseUsableVocalClocks()
    {
        var line = Assert.Single(new TtmlLyricsParser().ParseTtml(Wrap("""
            <p begin="10" end="12"><span begin="10" end="11">,</span></p>
            """)));
        Assert.Equal(",", line.Text);
        Assert.Null(line.Words);
    }

    [Fact]
    public void ParseTtml_DoesNotInventClocksForPartiallyTimedVisibleText()
    {
        var line = Assert.Single(new TtmlLyricsParser().ParseTtml(Wrap("<p begin=\"10\" end=\"12\">Hello <span begin=\"11\" end=\"12\">world</span></p>")));
        Assert.Equal("Hello world", line.Text);
        Assert.Null(line.Words);
    }

    [Fact]
    public void ParseTtml_ExcludesAuxiliaryParagraphsAndMetadataParagraphs()
    {
        var lines = new TtmlLyricsParser().ParseTtml(Wrap("""
            <p begin="1" end="2">Main</p><p begin="1" end="2" ttm:role="x-bg">Background</p><p begin="1" end="2" ttm:role="x-translation">Translation only</p>
            """, "<p begin=\"0\" end=\"1\">Not a body lyric</p>"));
        Assert.Equal("Main", Assert.Single(lines).Text);
    }

    [Fact]
    public void ParseTtml_RecognizesAppleDeliverySongPartAndParagraphOverride()
    {
        var ttml = Wrap("""
            <div itunes:song-part="verse"><p begin="1" end="2">First</p><p begin="2" end="3" itunes:song-part="chorus">Second</p></div>
            """);
        var lines = new TtmlLyricsParser().ParseTtml(ttml);
        Assert.Equal("verse", lines[0].SongPart);
        Assert.Equal("chorus", lines[1].SongPart);
    }

    [Fact]
    public void ParseTtml_PreservesLineBreakAsSpaceAndDoesNotIncludeRubyPronunciation()
    {
        var line = Assert.Single(new TtmlLyricsParser().ParseTtml(Wrap("""
            <p begin="1" end="3">Hello<br/>world <span tts:ruby="container"><span tts:ruby="base">字</span><span tts:ruby="text">pronunciation</span></span></p>
            """)));
        Assert.Equal("Hello world 字", line.Text);
        Assert.Null(line.Words);
    }

    private static string Wrap(string body, string metadata = "") => $$"""
        <tt xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" xmlns:ttm="http://www.w3.org/ns/ttml#metadata" xmlns:tts="http://www.w3.org/ns/ttml#styling" itunes:timing="Word">
          <head><metadata>{{metadata}}</metadata></head><body dur="3:52.840"><div>{{body}}</div></body>
        </tt>
        """;

    private static string NativeJson(string localized, string? plain = null, string? playId = "AP_42", string? catalogId = "42")
        => JsonSerializer.Serialize(new
        {
            data = new[] { new { id = "42", type = "syllable-lyrics", attributes = new
            {
                playParams = new { id = playId, catalogId, kind = "lyric" }, ttmlLocalizations = localized, ttml = plain,
            } } },
        });
}
