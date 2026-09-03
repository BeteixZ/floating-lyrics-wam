using System;
using MediaFontFamily = System.Windows.Media.FontFamily;

namespace AppleMusicLyrics.App.Services;

public static class FontFamilyResolver
{
    public const string DefaultFontName = "Optima";
    public const string FallbackFontName = "Segoe UI";

    public static MediaFontFamily Resolve(string? fontName)
    {
        var targetName = string.IsNullOrWhiteSpace(fontName) ? DefaultFontName : fontName.Trim();

        if (string.Equals(targetName, DefaultFontName, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // Pack URI allows loading the embedded Optima.ttf from application resources on machines without Optima installed.
                // It automatically falls back to system "Optima" and then "Segoe UI" if needed.
                return new MediaFontFamily(new Uri("pack://application:,,,/Fonts/"), "./#Optima, Optima, Segoe UI");
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
}
