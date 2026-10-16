using System;
using System.IO;
using System.Windows.Markup;
using System.Windows.Media;
using MediaFontFamily = System.Windows.Media.FontFamily;

namespace AppleMusicLyrics.App.Services;

public static class FontFamilyResolver
{
    public const string DefaultFontName = "Optima";
    public const string FallbackFontName = "Segoe UI";
    public const string DefaultChineseFontName = "Microsoft YaHei UI";

    // Keep Latin text independent even when its chosen family also contains Chinese glyphs.
    private const string ChineseUnicodeRanges =
        "2E80-2FFF,3000-303F,3100-312F,31A0-31EF,3400-4DBF,4E00-9FFF,F900-FAFF,FE10-FE1F,FE30-FE6F,FF00-FFEF,20000-3FFFF";

    public static MediaFontFamily Resolve(string? fontName)
    {
        var targetName = string.IsNullOrWhiteSpace(fontName) ? DefaultFontName : fontName.Trim();

        if (string.Equals(targetName, DefaultFontName, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // Pack URI allows loading the embedded Optima.ttf from application resources on machines without Optima installed.
                // It automatically falls back to system "Optima" and then "Segoe UI" if needed.
                return new MediaFontFamily(new Uri("pack://application:,,,/AppleMusicLyrics.App;component/Fonts/"), "./#Optima, Optima, Segoe UI");
            }
            catch
            {
                return new MediaFontFamily("Optima, Segoe UI");
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
            Unicode = ChineseUnicodeRanges,
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
        if (!fontName.Equals(DefaultFontName, StringComparison.OrdinalIgnoreCase))
        {
            return fontName;
        }

        // Anonymous composite maps have no base URI. WPF accepts only file: absolute font
        // references there, so a pack:// map target silently loses the embedded font.
        // The app already ships this same Optima.ttf as Fonts/ content as well as a resource.
        var fontPath = Path.Combine(AppContext.BaseDirectory, "Fonts", "Optima.ttf");
        return File.Exists(fontPath)
            ? new Uri(fontPath, UriKind.Absolute).AbsoluteUri.Replace(",", "%2C", StringComparison.Ordinal) + "#Optima, Optima"
            : "Optima";
    }
}
