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
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
