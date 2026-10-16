using MediaFontFamily = System.Windows.Media.FontFamily;

namespace AppleMusicLyrics.App.Services;

public static class TranslationTypography
{
    public const double DefaultFontSize = 20.0;
    public const double MinimumFontSize = 10.0;
    public const double MaximumFontSize = 48.0;

    public static double GetFontSize(double fontSize)
        => double.IsFinite(fontSize) ? Math.Clamp(fontSize, MinimumFontSize, MaximumFontSize) : DefaultFontSize;

    public static MediaFontFamily ResolveFontFamily(string? fontName)
    {
        var target = string.IsNullOrWhiteSpace(fontName) ? FontFamilyResolver.DefaultChineseFontName : fontName.Trim();
        return FontFamilyResolver.Resolve(target, target);
    }
}
