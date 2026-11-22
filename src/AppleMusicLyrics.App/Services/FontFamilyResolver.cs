using System;
using System.IO;
using System.Windows.Markup;
using System.Windows.Media;
using MediaFontFamily = System.Windows.Media.FontFamily;

namespace AppleMusicLyrics.App.Services;

public static class FontFamilyResolver
{
    /// <summary>The bundled Latin lyric font (SIL Open Font License; see licenses/Marcellus-OFL.txt).</summary>
    public const string DefaultFontName = "Marcellus";
    public const string BundledFontFileName = "Marcellus-Regular.ttf";
    /// <summary>
    /// The default before 0.11. Optima is not redistributable and is no longer bundled; a saved
    /// "Optima" still uses an installed Optima and otherwise the bundled default.
    /// </summary>
    public const string LegacyDefaultFontName = "Optima";
    public const string FallbackFontName = "Segoe UI";
    public const string DefaultChineseFontName = "Microsoft YaHei UI";

    // Built per call: a pack:// Uri parsed before WPF registers the scheme stays unusable.
    private const string BundledFontBaseUri = "pack://application:,,,/AppleMusicLyrics.App;component/Fonts/";

    // Keep Latin text independent even when its chosen family contains CJK glyphs.
    // Route kana with Han characters so Japanese lyrics use the same selected family
    // instead of mixing its kanji with the system's unrelated kana fallback.
    private const string ChineseAndJapaneseUnicodeRanges =
        "2E80-2FFF,3000-30FF,3100-312F,31A0-31FF,3400-4DBF,4E00-9FFF,F900-FAFF,FE10-FE1F,FE30-FE6F,FF00-FFEF,1AFF0-1B16F,20000-3FFFF";

    public static MediaFontFamily Resolve(string? fontName)
    {
        var targetName = string.IsNullOrWhiteSpace(fontName) ? DefaultFontName : fontName.Trim();
        var installedFirst = string.Equals(targetName, LegacyDefaultFontName, StringComparison.OrdinalIgnoreCase)
            ? $"{LegacyDefaultFontName}, "
            : null;

        if (installedFirst is not null || string.Equals(targetName, DefaultFontName, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // The pack URI loads the bundled font from application resources on machines
                // without it installed; an installed copy and then Segoe UI follow.
                return new MediaFontFamily(new Uri(BundledFontBaseUri),
                    $"{installedFirst}./#{DefaultFontName}, {DefaultFontName}, {FallbackFontName}");
            }
            catch
            {
                // Without an initialized pack scheme, the deployed font file still works.
                return new MediaFontFamily($"{installedFirst}{BundledLatinTarget()}, {FallbackFontName}");
            }
        }

        try
        {
            return new MediaFontFamily(targetName);
        }
        catch
        {
            return new MediaFontFamily(FallbackFontName);
        }
    }

    public static MediaFontFamily Resolve(string? latinFontName, string? chineseFontName)
    {
        var latinName = string.IsNullOrWhiteSpace(latinFontName) ? DefaultFontName : latinFontName.Trim();
        var chineseName = string.IsNullOrWhiteSpace(chineseFontName) ? DefaultChineseFontName : chineseFontName.Trim();
        var family = new MediaFontFamily();
        family.FamilyNames.Add(XmlLanguage.GetLanguage("en-US"), "Apple Music Lyrics Composite");
        family.FamilyMaps.Add(new FontFamilyMap
        {
            Unicode = ChineseAndJapaneseUnicodeRanges,
            Target = $"{chineseName}, {DefaultChineseFontName}, Microsoft YaHei, SimSun, Global User Interface",
        });
        family.FamilyMaps.Add(new FontFamilyMap
        {
            Unicode = "2300-23FF,2600-27BF,1F000-1FFFF",
            Target = "Segoe UI Emoji, Segoe UI Symbol, Global User Interface",
        });
        family.FamilyMaps.Add(new FontFamilyMap
        {
            Target = $"{ResolveLatinTarget(latinName)}, {FallbackFontName}, Global User Interface",
        });
        return family;
    }

    private static string ResolveLatinTarget(string fontName)
    {
        if (fontName.Equals(LegacyDefaultFontName, StringComparison.OrdinalIgnoreCase))
        {
            return $"{LegacyDefaultFontName}, {BundledLatinTarget()}";
        }

        return fontName.Equals(DefaultFontName, StringComparison.OrdinalIgnoreCase)
            ? BundledLatinTarget()
            : fontName;
    }

    // Anonymous composite maps have no base URI. WPF accepts only file: absolute font
    // references there, so a pack:// map target silently loses the bundled font.
    // The app ships the same font file as Fonts/ content as well as a resource.
    private static string BundledLatinTarget()
    {
        var fontPath = Path.Combine(AppContext.BaseDirectory, "Fonts", BundledFontFileName);
        return File.Exists(fontPath)
            ? new Uri(fontPath, UriKind.Absolute).AbsoluteUri.Replace(",", "%2C", StringComparison.Ordinal)
                + $"#{DefaultFontName}, {DefaultFontName}"
            : DefaultFontName;
    }
}
