using AppleMusicLyrics.App.Services;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using Xunit;

namespace AppleMusicLyrics.Tests.Services;

public sealed class FontFamilyResolverTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Optima")]
    [InlineData("optima")]
    public void Resolve_OptimaOrEmpty_ResolvesSuccessfully(string? fontName)
    {
        var fontFamily = FontFamilyResolver.Resolve(fontName);
        Assert.NotNull(fontFamily);
        Assert.Contains("Optima", fontFamily.Source);
    }

    [Fact]
    public void Resolve_CustomFont_ResolvesTargetFont()
    {
        var fontFamily = FontFamilyResolver.Resolve("Arial");
        Assert.NotNull(fontFamily);
        Assert.Equal("Arial", fontFamily.Source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Resolve_EmptyChineseFontUsesIndependentDefault(string? chineseFont) => OnSta(() =>
    {
        if (!TryGlyphTypeface(FontFamilyResolver.DefaultChineseFontName, out var expectedChinese)
            || !TryGlyphTypeface("Arial", out var expectedLatin)) return;
        var runs = GlyphRuns(FontFamilyResolver.Resolve("Arial", chineseFont), "A中");
        Assert.Equal(expectedChinese!.FontUri, FindRun(runs, "中").GlyphTypeface.FontUri);
        Assert.Equal(expectedLatin!.FontUri, FindRun(runs, "A").GlyphTypeface.FontUri);
    });

    [Fact]
    public void Resolve_ChineseSelectionOverridesLatinFontsOwnCjkGlyphs() => OnSta(() =>
    {
        // Chinese fonts are optional Windows features. Run the physical glyph check whenever
        // both families are installed; the map contract above is independent of the OS fonts.
        if (!TryGlyphTypeface("Microsoft YaHei UI", out var yaHei)
            || !TryGlyphTypeface("SimSun", out var simSun)) return;
        var first = GlyphRuns(FontFamilyResolver.Resolve("Microsoft YaHei UI", "Microsoft YaHei UI"), "A中，Ｂ");
        var second = GlyphRuns(FontFamilyResolver.Resolve("Microsoft YaHei UI", "SimSun"), "A中，Ｂ");
        Assert.NotEqual(yaHei!.FontUri, simSun!.FontUri);
        foreach (var text in new[] { "中", "，", "Ｂ" })
        {
            Assert.Equal(yaHei.FontUri, FindRun(first, text).GlyphTypeface.FontUri);
            Assert.Equal(simSun.FontUri, FindRun(second, text).GlyphTypeface.FontUri);
        }
        Assert.Equal(yaHei.FontUri, FindRun(first, "A").GlyphTypeface.FontUri);
        Assert.Equal(yaHei.FontUri, FindRun(second, "A").GlyphTypeface.FontUri);
    });

    [Fact]
    public void Resolve_MissingChineseFontFallsBackWithoutLosingGlyphs() => OnSta(() =>
    {
        if (!TryGlyphTypeface(FontFamilyResolver.DefaultChineseFontName, out var expected)) return;
        var runs = GlyphRuns(FontFamilyResolver.Resolve("Arial", "AppleMusicLyrics Missing CJK Font 8D2093"), "A中");
        Assert.Equal(expected!.FontUri, FindRun(runs, "中").GlyphTypeface.FontUri);
        Assert.DoesNotContain((ushort)0, FindRun(runs, "中").GlyphIndices);
        Assert.DoesNotContain((ushort)0, FindRun(runs, "A").GlyphIndices);
    });

    [Fact]
    public void Resolve_CompositeKeepsDeployedOptimaLatinAndSelectedChineseAndEmoji() => OnSta(() =>
    {
        var runs = GlyphRuns(FontFamilyResolver.Resolve("Optima", "SimSun"), "Ag中🙂");
        var latin = FindRun(runs, "A");
        Assert.Equal("file", latin.GlyphTypeface.FontUri.Scheme);
        Assert.EndsWith("/Fonts/Optima.ttf", latin.GlyphTypeface.FontUri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(latin.GlyphTypeface.FamilyNames.Values, name => name.Equals("Optima", StringComparison.OrdinalIgnoreCase));
        if (TryGlyphTypeface("SimSun", out var chinese))
        {
            Assert.Equal(chinese!.FontUri, FindRun(runs, "中").GlyphTypeface.FontUri);
        }
        if (TryGlyphTypeface("Segoe UI Emoji", out var emoji)
            && emoji!.CharacterToGlyphMap.ContainsKey(0x1F642))
        {
            Assert.Equal(emoji.FontUri, FindRun(runs, "🙂").GlyphTypeface.FontUri);
        }
    });

    [Fact]
    public void Resolve_ExistingOptimaApiLoadsEmbeddedFontFromAppAssembly() => OnSta(() =>
    {
        var run = FindRun(GlyphRuns(FontFamilyResolver.Resolve("Optima"), "Ag"), "A");
        Assert.Equal("pack", run.GlyphTypeface.FontUri.Scheme);
        Assert.Contains("AppleMusicLyrics.App;component/Fonts/Optima.ttf", run.GlyphTypeface.FontUri.OriginalString,
            StringComparison.OrdinalIgnoreCase);
    });

    private static bool TryGlyphTypeface(string familyName, out GlyphTypeface? glyph)
        => new Typeface(new FontFamily(familyName), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal)
            .TryGetGlyphTypeface(out glyph);

    private static GlyphRun FindRun(IEnumerable<GlyphRun> runs, string text)
        => runs.First(run => new string(run.Characters.ToArray()).Contains(text, StringComparison.Ordinal));

    private static IReadOnlyList<GlyphRun> GlyphRuns(FontFamily family, string text)
    {
        var formatted = new FormattedText(text, CultureInfo.GetCultureInfo("en-US"), FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 32, Brushes.White, 1.0);
        var drawing = new DrawingGroup();
        using (var context = drawing.Open()) context.DrawText(formatted, new Point());
        var runs = new List<GlyphRun>();
        Collect(drawing);
        return runs;

        void Collect(Drawing value)
        {
            if (value is GlyphRunDrawing glyph && glyph.GlyphRun is { } run) runs.Add(run);
            else if (value is DrawingGroup group)
            {
                foreach (var child in group.Children) Collect(child);
            }
        }
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15))) throw new TimeoutException("Font glyph routing test did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
