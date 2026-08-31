using System.Globalization;
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
            PureModeDragOpacity = 0.84,
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
        Assert.Equal(0.84, loaded.PureModeDragOpacity);
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
        Assert.Equal(0.8, settings.PureModeDragOpacity);
    }

    [Fact]
    public void Clone_CopiesHoverFadeSettings()
    {
        var original = new AppSettings
        {
            HoverFadeEnabled = true,
            HoverFadeDuration = 0.5,
            HoverFadeMinOpacity = 0.2,
            PureModeDragOpacity = 0.86,
        };

        var clone = original.Clone();

        Assert.True(clone.HoverFadeEnabled);
        Assert.Equal(0.5, clone.HoverFadeDuration);
        Assert.Equal(0.2, clone.HoverFadeMinOpacity);
        Assert.Equal(0.86, clone.PureModeDragOpacity);

        clone.HoverFadeEnabled = false;
        clone.PureModeDragOpacity = 0.4;
        Assert.True(original.HoverFadeEnabled);
        Assert.Equal(0.86, original.PureModeDragOpacity);
    }

    [Fact]
    public void SaveLoadAndClone_PreserveNonAppleMediaSessionOptIn()
    {
        var path = Path.Combine(_root, "settings.ini");
        var store = new IniSettingsStore(path);
        var settings = new AppSettings
        {
            AllowNonAppleMediaSessions = true,
        };

        store.Save(settings);
        var loaded = store.Load();
        var clone = loaded.Clone();

        Assert.True(loaded.AllowNonAppleMediaSessions);
        Assert.True(clone.AllowNonAppleMediaSessions);
        Assert.False(new AppSettings().AllowNonAppleMediaSessions);
    }

    [Fact]
    public void SaveLoadAndClone_PreserveLowConfidenceLyricsOptIn()
    {
        var path = Path.Combine(_root, "settings.ini");
        var store = new IniSettingsStore(path);
        var settings = new AppSettings
        {
            AllowLowConfidenceLyrics = true,
        };

        store.Save(settings);
        var loaded = store.Load();
        var clone = loaded.Clone();

        Assert.True(loaded.AllowLowConfidenceLyrics);
        Assert.True(clone.AllowLowConfidenceLyrics);
        Assert.False(new AppSettings().AllowLowConfidenceLyrics);
    }

    [Fact]
    public void SaveLoadAndClone_PreservePerMonitorPlacement()
    {
        var path = Path.Combine(_root, "settings.ini");
        var store = new IniSettingsStore(path);
        var settings = new AppSettings
        {
            WindowPlacementVersion = 1,
            WindowMonitorId = @"\\.\DISPLAY2",
            WindowRelativeCenterX = 0.8,
            WindowRelativeCenterY = 0.7,
            PureModeMonitorId = @"\\.\DISPLAY1",
            PureModeRelativeCenterX = 0.4,
            PureModeRelativeCenterY = 0.9,
        };

        store.Save(settings);
        var loaded = store.Load();
        var clone = loaded.Clone();

        Assert.Equal(1, loaded.WindowPlacementVersion);
        Assert.Equal(@"\\.\DISPLAY2", loaded.WindowMonitorId);
        Assert.Equal(0.8, loaded.WindowRelativeCenterX);
        Assert.Equal(0.7, loaded.WindowRelativeCenterY);
        Assert.Equal(@"\\.\DISPLAY1", clone.PureModeMonitorId);
        Assert.Equal(0.4, clone.PureModeRelativeCenterX);
        Assert.Equal(0.9, clone.PureModeRelativeCenterY);
    }

    [Fact]
    public void SaveAndLoad_UsesInvariantNumbersUnderNonEnglishCulture()
    {
        var path = Path.Combine(_root, "settings.ini");
        var store = new IniSettingsStore(path);
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            store.Save(new AppSettings { LyricsOffsetSeconds = 0.78 });
            var loaded = store.Load();

            Assert.Contains("LyricsOffsetSeconds=0.78", File.ReadAllText(path), StringComparison.Ordinal);
            Assert.Equal(0.78, loaded.LyricsOffsetSeconds);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void Load_IgnoresMalformedValuesAndUsesTheLastDuplicate()
    {
        var path = Path.Combine(_root, "settings.ini");
        File.WriteAllLines(
            path,
            [
                "[apple_music_lyrics]",
                "WindowX=not-a-number",
                "WindowY=240",
                "LyricsOffsetSeconds=0.25",
                "LyricsOffsetSeconds=0.91",
                "ExternalLyricsEnabled=not-a-boolean",
            ]);

        var loaded = new IniSettingsStore(path).Load();

        Assert.Equal(new AppSettings().WindowX, loaded.WindowX);
        Assert.Equal(240, loaded.WindowY);
        Assert.Equal(0.91, loaded.LyricsOffsetSeconds);
        Assert.True(loaded.ExternalLyricsEnabled);
    }

    [Fact]
    public void SaveLoadAndClone_PreserveOnlineAndNativeOffsetSettings()
    {
        var path = Path.Combine(_root, "settings.ini");
        var store = new IniSettingsStore(path);
        var settings = new AppSettings
        {
            ApplyNativeLyricOffset = false,
            CatalogLookupEnabled = false,
            CatalogStorefronts = "jp,us",
            CatalogLookupTimeoutSeconds = 4.5,
            ExternalLyricsEnabled = false,
            ExternalLyricsTimeoutSeconds = 8.5,
        };

        store.Save(settings);
        var clone = store.Load().Clone();

        Assert.False(clone.ApplyNativeLyricOffset);
        Assert.False(clone.CatalogLookupEnabled);
        Assert.Equal("jp,us", clone.CatalogStorefronts);
        Assert.Equal(4.5, clone.CatalogLookupTimeoutSeconds);
        Assert.False(clone.ExternalLyricsEnabled);
        Assert.Equal(8.5, clone.ExternalLyricsTimeoutSeconds);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
