using AppleMusicLyrics.Core.Configuration;
using AppleMusicLyrics.Infrastructure.Windows.Configuration;
using Xunit;

namespace AppleMusicLyrics.Tests.Configuration;

public sealed class IniSettingsStoreTests : IDisposable
{
    private readonly string _root;

    public IniSettingsStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AppleMusicLyrics.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsOverlaySettings()
    {
        var path = Path.Combine(_root, "settings.ini");
        var store = new IniSettingsStore(path);
        var settings = new AppSettings
        {
            WindowX = 320,
            WindowY = 180,
            WindowWidth = 960,
            WindowHeight = 260,
            LyricsOffsetSeconds = 0.31,
            MaxCurrentFontSize = 52,
            ContextFontSize = 28,
            FontFamily = "Aptos",
            CurrentLineColor = "#F7F7F7",
            ContextLineColor = "#B7BDC7",
            PausedLineColor = "#8A8F99",
            GlowColor = "#FFF6BF",
            GlowOpacity = 0.58,
            SingleLineMode = true,
            TwoLineMode = false,
            ShowDebugPanel = true,
            PureMode = true,
            ClickThrough = true,
            AutoHideNoLyrics = true,
            OverlayOpacity = 0.72,
            BackgroundAlpha = 96,
            HoverFadeEnabled = true,
            HoverFadeDuration = 0.45,
            HoverFadeMinOpacity = 0.15,
        };

        store.Save(settings);
        var loaded = store.Load();

        Assert.Equal(320, loaded.WindowX);
        Assert.Equal(180, loaded.WindowY);
        Assert.Equal(960, loaded.WindowWidth);
        Assert.Equal(260, loaded.WindowHeight);
        Assert.Equal(0.31, loaded.LyricsOffsetSeconds);
        Assert.Equal(52, loaded.MaxCurrentFontSize);
        Assert.Equal(28, loaded.ContextFontSize);
        Assert.Equal("Aptos", loaded.FontFamily);
        Assert.Equal("#F7F7F7", loaded.CurrentLineColor);
        Assert.Equal("#B7BDC7", loaded.ContextLineColor);
        Assert.Equal("#8A8F99", loaded.PausedLineColor);
        Assert.Equal("#FFF6BF", loaded.GlowColor);
        Assert.Equal(0.58, loaded.GlowOpacity);
        Assert.True(loaded.SingleLineMode);
        Assert.False(loaded.TwoLineMode);
        Assert.True(loaded.ShowDebugPanel);
        Assert.True(loaded.PureMode);
        Assert.True(loaded.ClickThrough);
        Assert.True(loaded.AutoHideNoLyrics);
        Assert.Equal(0.72, loaded.OverlayOpacity);
        Assert.Equal(96, loaded.BackgroundAlpha);
        Assert.True(loaded.HoverFadeEnabled);
        Assert.Equal(0.45, loaded.HoverFadeDuration);
        Assert.Equal(0.15, loaded.HoverFadeMinOpacity);
    }

    [Fact]
    public void SaveAndLoad_HoverFadeDefaultsAreCorrect()
    {
        var settings = new AppSettings();
        Assert.True(settings.HoverFadeEnabled);
        Assert.Equal(0.3, settings.HoverFadeDuration);
        Assert.Equal(0.05, settings.HoverFadeMinOpacity);
    }

    [Fact]
    public void Clone_CopiesHoverFadeSettings()
    {
        var original = new AppSettings
        {
            HoverFadeEnabled = true,
            HoverFadeDuration = 0.5,
            HoverFadeMinOpacity = 0.2,
        };

        var clone = original.Clone();

        Assert.True(clone.HoverFadeEnabled);
        Assert.Equal(0.5, clone.HoverFadeDuration);
        Assert.Equal(0.2, clone.HoverFadeMinOpacity);

        clone.HoverFadeEnabled = false;
        Assert.True(original.HoverFadeEnabled);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
