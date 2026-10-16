using System.Runtime.ExceptionServices;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using System.Windows.Media;
using AppleMusicLyrics.App;
using AppleMusicLyrics.App.Localization;
using AppleMusicLyrics.Core.Configuration;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Infrastructure.Windows.External;
using Xunit;

namespace AppleMusicLyrics.Tests.Display;

[Collection("Localization")]
public sealed class SettingsWindowBindingTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void LyricFeatureSwitches_LoadAndApplyTheActualSettings(bool words, bool translations) => OnSta(() =>
    {
        var original = new AppSettings { WordHighlightEnabled = words, ShowTranslation = translations };
        AppSettings? applied = null;
        var window = new SettingsWindow(original, settings => applied = settings);
        try
        {
            SelectTab(window, "LyricsTab");
            var toggles = Descendants(window).OfType<CheckBox>().ToArray();
            var wordSwitch = Assert.Single(toggles, toggle =>
                BindingOperations.GetBinding(toggle, ToggleButton.IsCheckedProperty)?.Path.Path == "Settings.WordHighlightEnabled");
            var translationSwitch = Assert.Single(toggles, toggle =>
                BindingOperations.GetBinding(toggle, ToggleButton.IsCheckedProperty)?.Path.Path == "Settings.ShowTranslation");
            Assert.Equal(words, wordSwitch.IsChecked);
            Assert.Equal(translations, translationSwitch.IsChecked);

            wordSwitch.SetCurrentValue(ToggleButton.IsCheckedProperty, !words);
            translationSwitch.SetCurrentValue(ToggleButton.IsCheckedProperty, !translations);
            wordSwitch.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
            translationSwitch.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
            ((Button)window.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.NotNull(applied);
            Assert.Equal(!words, applied.WordHighlightEnabled);
            Assert.Equal(!translations, applied.ShowTranslation);
            Assert.Equal(words, original.WordHighlightEnabled);
            Assert.Equal(translations, original.ShowTranslation);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Apply_PreservesSettingsOnTabsTheUserHasNotOpened() => OnSta(() =>
    {
        var original = new AppSettings
        {
            FontFamily = "Arial", ChineseFontFamily = "SimSun", MaxCurrentFontSize = 52,
            TranslationFontFamily = "KaiTi", TranslationFontSize = 27,
            WordHighlightEnabled = false, ShowTranslation = true,
            ExternalLyricsEnabled = false, CatalogLookupEnabled = false,
            PersistExternalLyricsCache = true, ExternalLyricsCacheMaxMegabytes = 128, AllowNonAppleMediaSessions = true,
            AllowLowConfidenceLyrics = true, ApplyNativeLyricOffset = false,
            ShowDebugPanel = true, PureMode = false, ClickThrough = false,
            HoverFadeEnabled = false, HoverFadeDuration = 0.45, BackgroundAlpha = 96,
        };
        AppSettings? applied = null;
        var window = new SettingsWindow(original, settings => applied = settings);
        try
        {
            SelectTab(window, "AppearanceTab");
            ((Button)window.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.NotNull(applied);
            Assert.Equal("Arial", applied.FontFamily);
            Assert.Equal("SimSun", applied.ChineseFontFamily);
            Assert.Equal(52, applied.MaxCurrentFontSize);
            Assert.Equal("KaiTi", applied.TranslationFontFamily);
            Assert.Equal(27, applied.TranslationFontSize);
            Assert.False(applied.WordHighlightEnabled);
            Assert.True(applied.ShowTranslation);
            Assert.False(applied.ExternalLyricsEnabled);
            Assert.False(applied.CatalogLookupEnabled);
            Assert.True(applied.PersistExternalLyricsCache);
            Assert.Equal(128, applied.ExternalLyricsCacheMaxMegabytes);
            Assert.True(applied.AllowNonAppleMediaSessions);
            Assert.True(applied.AllowLowConfidenceLyrics);
            Assert.False(applied.ApplyNativeLyricOffset);
            Assert.True(applied.ShowDebugPanel);
            Assert.False(applied.PureMode);
            Assert.False(applied.ClickThrough);
            Assert.False(applied.HoverFadeEnabled);
            Assert.Equal(0.45, applied.HoverFadeDuration);
            Assert.Equal(96, applied.BackgroundAlpha);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TabNavigation_RetainsPendingEditsAndAppliesBothFontChoices() => OnSta(() =>
    {
        var original = new AppSettings { FontFamily = "Arial", ChineseFontFamily = "SimSun", WordHighlightEnabled = false };
        AppSettings? applied = null;
        var window = new SettingsWindow(original, settings => applied = settings);
        try
        {
            SelectTab(window, "AppearanceTab");
            var latin = (ComboBox)window.FindName("FontFamilyComboBox");
            var chinese = (ComboBox)window.FindName("ChineseFontFamilyComboBox");
            EditFont(latin, "Segoe UI");
            EditFont(chinese, "Microsoft YaHei UI");
            ((Slider)window.FindName("MaxCurrentFontSlider")).Value = 48;

            SelectTab(window, "LyricsTab");
            var wordSwitch = Assert.Single(Descendants(window).OfType<CheckBox>(), toggle =>
                BindingOperations.GetBinding(toggle, ToggleButton.IsCheckedProperty)?.Path.Path == "Settings.WordHighlightEnabled");
            wordSwitch.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            wordSwitch.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
            SelectTab(window, "WindowTab");
            ((CheckBox)window.FindName("ClickThroughCheckBox")).IsChecked = false;
            SelectTab(window, "InteractionTab");
            ((Slider)window.FindName("HoverFadeDurationSlider")).Value = 0.55;
            SelectTab(window, "AdvancedTab");
            ((CheckBox)window.FindName("ShowDebugPanelCheckBox")).IsChecked = true;
            SelectTab(window, "AppearanceTab");

            Assert.Equal("Segoe UI", latin.Text);
            Assert.Contains("Microsoft YaHei UI", chinese.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(48, ((Slider)window.FindName("MaxCurrentFontSlider")).Value);
            ((Button)window.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.NotNull(applied);
            Assert.Equal("Segoe UI", applied.FontFamily);
            Assert.Equal("Microsoft YaHei UI", applied.ChineseFontFamily);
            Assert.Equal(48, applied.MaxCurrentFontSize);
            Assert.True(applied.WordHighlightEnabled);
            Assert.False(applied.ClickThrough);
            Assert.Equal(0.55, applied.HoverFadeDuration);
            Assert.True(applied.ShowDebugPanel);
            Assert.Equal("Arial", original.FontFamily);
            Assert.Equal("SimSun", original.ChineseFontFamily);
            Assert.False(original.WordHighlightEnabled);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TabPages_UseTheAvailableWidthEvenWhenTheirLabelsAreShort() => OnSta(() =>
    {
        var window = new SettingsWindow(new AppSettings());
        try
        {
            var tabs = (TabControl)window.FindName("SettingsTabs");
            foreach (var name in new[] { "AppearanceTab", "LyricsTab", "WindowTab", "InteractionTab", "AdvancedTab" })
            {
                SelectTab(window, name);
                var page = Assert.IsType<ScrollViewer>(((TabItem)window.FindName(name)).Content);
                Assert.InRange(page.ActualWidth, tabs.ActualWidth * 0.95, tabs.ActualWidth + 1);
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TranslationTypography_EditsAndPreviewsRemainIndependentOfOriginalText() => OnSta(() =>
    {
        var original = new AppSettings
        {
            FontFamily = "Arial", ChineseFontFamily = "Microsoft YaHei UI", MaxCurrentFontSize = 38,
            TranslationFontFamily = "SimSun", TranslationFontSize = 24,
        };
        AppSettings? applied = null;
        var window = new SettingsWindow(original, settings => applied = settings);
        try
        {
            SelectTab(window, "AppearanceTab");
            var sourcePreview = (TextBlock)window.FindName("FontPreviewText");
            var translationPreview = (TextBlock)window.FindName("TranslationFontPreviewText");
            var initialTranslationFont = GlyphFontUri(translationPreview);
            Assert.Equal(24, translationPreview.FontSize);

            EditFont((ComboBox)window.FindName("FontFamilyComboBox"), "Times New Roman");
            EditFont((ComboBox)window.FindName("ChineseFontFamilyComboBox"), "SimSun");
            ((Slider)window.FindName("MaxCurrentFontSlider")).Value = 44;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Assert.Equal(24, translationPreview.FontSize);
            Assert.Equal(initialTranslationFont, GlyphFontUri(translationPreview));
            var sourceFont = GlyphFontUri(sourcePreview);

            EditFont((ComboBox)window.FindName("TranslationFontFamilyComboBox"), "Microsoft YaHei UI");
            ((Slider)window.FindName("TranslationFontSizeSlider")).Value = 30;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Assert.Equal(30, translationPreview.FontSize);
            Assert.NotEqual(initialTranslationFont, GlyphFontUri(translationPreview));
            Assert.Equal(44, sourcePreview.FontSize);
            Assert.Equal(sourceFont, GlyphFontUri(sourcePreview));

            SelectTab(window, "AdvancedTab");
            ((Button)window.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.NotNull(applied);
            Assert.Equal("Times New Roman", applied.FontFamily);
            Assert.Equal("SimSun", applied.ChineseFontFamily);
            Assert.Equal(44, applied.MaxCurrentFontSize);
            Assert.Equal("Microsoft YaHei UI", applied.TranslationFontFamily);
            Assert.Equal(30, applied.TranslationFontSize);
            Assert.Equal("SimSun", original.TranslationFontFamily);
            Assert.Equal(24, original.TranslationFontSize);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void LanguageSelection_UpdatesVisibleTextAndKeepsPendingEdits() => OnSta(() =>
    {
        LocalizationManager.Instance.SetLanguage("en-US");
        var original = new AppSettings { UiLanguage = "en-US", FontFamily = "Arial" };
        AppSettings? applied = null;
        var window = new SettingsWindow(original, value => applied = value);
        try
        {
            SelectTab(window, "AppearanceTab");
            var font = (ComboBox)window.FindName("FontFamilyComboBox");
            EditFont(font, "Times New Roman");
            ((Slider)window.FindName("MaxCurrentFontSlider")).Value = 46;
            ((TextBox)window.FindName("CurrentColorTextBox")).Text = "#EFEFEF";
            SelectTab(window, "AdvancedTab");
            ((Slider)window.FindName("ExternalLyricsCacheMaxMegabytesSlider")).Value = 256;

            SelectLanguage(window, "zh-CN");
            Assert.Equal("歌词浮层设置", window.Title);
            Assert.Equal("外观", ((TabItem)window.FindName("AppearanceTab")).Header);
            Assert.Equal("应用", ((Button)window.FindName("ApplyButton")).Content);
            SelectLanguage(window, "zh-TW");
            Assert.Equal("外觀", ((TabItem)window.FindName("AppearanceTab")).Header);
            SelectLanguage(window, "en-US");
            Assert.Equal("Appearance", ((TabItem)window.FindName("AppearanceTab")).Header);

            SelectTab(window, "AppearanceTab");
            Assert.Same(font, window.FindName("FontFamilyComboBox"));
            Assert.Equal("Times New Roman", font.Text);
            Assert.Equal(46, ((Slider)window.FindName("MaxCurrentFontSlider")).Value);
            Assert.Equal("#EFEFEF", ((TextBox)window.FindName("CurrentColorTextBox")).Text);
            ((Button)window.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.NotNull(applied);
            Assert.Equal("Times New Roman", applied.FontFamily);
            Assert.Equal(46, applied.MaxCurrentFontSize);
            Assert.Equal("#EFEFEF", applied.CurrentLineColor);
            Assert.Equal(256, applied.ExternalLyricsCacheMaxMegabytes);
            Assert.Equal("Arial", original.FontFamily);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void LanguageSelection_RendersNativeNamesAndTheLocalizedSystemLabel() => OnSta(() =>
    {
        var window = new SettingsWindow(new AppSettings { UiLanguage = "en-US" });
        try
        {
            SelectTab(window, "AppearanceTab");
            var selector = (ComboBox)window.FindName("LanguageComboBox");
            Assert.Equal("English", RenderedSelectionText(selector));
            foreach (var (language, nativeName) in new[]
                     { ("zh-CN", "简体中文"), ("zh-TW", "繁體中文"), ("en-US", "English") })
            {
                SelectLanguage(window, language);
                Assert.Equal(nativeName, RenderedSelectionText(selector));
                SelectLanguage(window, "system");
                var expected = LocalizationManager.Instance.Text("Settings.FollowWindows");
                Assert.Equal(expected, RenderedSelectionText(selector));
                Assert.Equal("system", selector.SelectedValue);
                var selected = Assert.IsType<LanguageOption>(selector.SelectedItem);
                Assert.Equal("system", selected.Code);
                Assert.Equal(expected, selected.NativeName);
                Assert.Equal("system", LocalizationManager.Instance.Preference);
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public void LanguagePreview_CancelRestoresTheOriginalLanguage() => OnSta(() =>
    {
        LocalizationManager.Instance.SetLanguage("en-US");
        var original = new AppSettings { UiLanguage = "en-US" };
        var window = new SettingsWindow(original);
        try
        {
            SelectLanguage(window, "zh-CN");
            Assert.Equal("zh-CN", LocalizationManager.Instance.Preference);
            Assert.Equal("歌词浮层设置", window.Title);
            ((Button)window.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("en-US", LocalizationManager.Instance.Preference);
            Assert.Equal("en-US", original.UiLanguage);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void LanguagePreview_ApplyCommitsTheLanguageThatCancelRestores() => OnSta(() =>
    {
        LocalizationManager.Instance.SetLanguage("zh-CN");
        AppSettings? applied = null;
        var window = new SettingsWindow(new AppSettings { UiLanguage = "zh-CN" }, value => applied = value);
        try
        {
            SelectLanguage(window, "en-US");
            ((Button)window.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("en-US", Assert.IsType<AppSettings>(applied).UiLanguage);
            SelectLanguage(window, "zh-TW");
            Assert.Equal("zh-TW", LocalizationManager.Instance.Preference);
            ((Button)window.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("en-US", LocalizationManager.Instance.Preference);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void LanguageSelection_ShowsTheEffectiveLanguageWithoutOverwritingAnUnsupportedPreference() => OnSta(() =>
    {
        AppSettings? applied = null;
        var window = new SettingsWindow(new AppSettings { UiLanguage = "en" }, value => applied = value);
        try
        {
            Assert.Equal("en-US", ((ComboBox)window.FindName("LanguageComboBox")).SelectedValue);
            ((Button)window.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("en", Assert.IsType<AppSettings>(applied).UiLanguage);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(1024)]
    public void LyricsCache_LoadsAndAppliesTheCapacityAndPersistence(int capacity) => OnSta(() =>
    {
        var original = new AppSettings { ExternalLyricsCacheMaxMegabytes = capacity, PersistExternalLyricsCache = false };
        AppSettings? applied = null;
        var window = new SettingsWindow(original, value => applied = value);
        try
        {
            SelectTab(window, "AdvancedTab");
            var size = (Slider)window.FindName("ExternalLyricsCacheMaxMegabytesSlider");
            var persist = (CheckBox)window.FindName("PersistExternalLyricsCacheCheckBox");
            Assert.Equal(capacity, size.Value);
            Assert.False(persist.IsChecked);
            size.Value = 192;
            persist.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            persist.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
            SelectTab(window, "LyricsTab");
            SelectLanguage(window, "en-US");
            ((Button)window.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.NotNull(applied);
            Assert.True(applied.PersistExternalLyricsCache);
            Assert.Equal(192, applied.ExternalLyricsCacheMaxMegabytes);
            Assert.False(original.PersistExternalLyricsCache);
            Assert.Equal(capacity, original.ExternalLyricsCacheMaxMegabytes);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void LyricsCache_ShowsRealUsageAndRefreshesWhileTheAdvancedPageIsOpen() => OnSta(() =>
    {
        using var temporary = new TemporaryLyricsCache();
        temporary.Store("First");
        var window = new SettingsWindow(new AppSettings { UiLanguage = "en-US" }, lyricsCache: temporary.Cache);
        try
        {
            var usage = (TextBlock)window.FindName("ExternalLyricsCacheUsageValueText");
            AssertUsageKilobytes(usage.Text, temporary.SizeBytes);
            temporary.Store("Second");
            SelectTab(window, "AdvancedTab");
            AssertUsageKilobytes(usage.Text, temporary.SizeBytes);
            var previous = usage.Text;
            temporary.Store("Third");
            PumpUntil(() => usage.Text != previous);
            AssertUsageKilobytes(usage.Text, temporary.SizeBytes);

            var clear = (Button)window.FindName("ClearExternalLyricsCacheButton");
            Assert.Equal("Clear cache", clear.Content);
            SelectLanguage(window, "zh-CN");
            Assert.Equal("清空缓存", clear.Content);
            SelectLanguage(window, "zh-TW");
            Assert.Equal("清除快取", clear.Content);
            AssertUsageKilobytes(usage.Text, temporary.SizeBytes);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void LyricsCache_ClearWorksWhenDisabledAndPreservesPendingEdits() => OnSta(() =>
    {
        using var temporary = new TemporaryLyricsCache();
        temporary.Store("First");
        temporary.Cache.Configure(false, 64);
        var generation = temporary.Cache.Generation;
        var original = new AppSettings { UiLanguage = "en-US", PersistExternalLyricsCache = false, FontFamily = "Arial" };
        var window = new SettingsWindow(original, lyricsCache: temporary.Cache);
        try
        {
            SelectTab(window, "AppearanceTab");
            EditFont((ComboBox)window.FindName("FontFamilyComboBox"), "Times New Roman");
            SelectTab(window, "AdvancedTab");
            ((Slider)window.FindName("ExternalLyricsCacheMaxMegabytesSlider")).Value = 192;
            var clear = (Button)window.FindName("ClearExternalLyricsCacheButton");
            var status = (TextBlock)window.FindName("ExternalLyricsCacheStatusText");
            var usage = (TextBlock)window.FindName("ExternalLyricsCacheUsageValueText");
            Assert.True(clear.IsEnabled);
            clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(clear.IsEnabled);
            Assert.Equal("Clearing…", status.Text);
            // A second queued click must not start another clear operation.
            clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => status.Visibility == Visibility.Collapsed && usage.Text == "0 B");
            Assert.False(clear.IsEnabled);
            Assert.Equal(0, temporary.SizeBytes);
            Assert.Empty(Directory.GetFiles(temporary.DirectoryPath));
            Assert.Equal(generation + 1, temporary.Cache.Generation);
            Assert.False(temporary.Cache.Enabled);
            Assert.Equal(64, temporary.Cache.MaxMegabytes);
            Assert.Equal(192, ((Slider)window.FindName("ExternalLyricsCacheMaxMegabytesSlider")).Value);
            SelectTab(window, "AppearanceTab");
            Assert.Equal("Times New Roman", ((ComboBox)window.FindName("FontFamilyComboBox")).Text);
            Assert.Equal("Arial", original.FontFamily);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void LyricsCache_ApplyRefreshesUsageAfterTheNewCapacityEvictsEntries() => OnSta(() =>
    {
        using var temporary = new TemporaryLyricsCache();
        temporary.Store("First", 600_000);
        temporary.Store("Second", 600_000);
        var window = new SettingsWindow(
            new AppSettings { UiLanguage = "en-US", PersistExternalLyricsCache = true },
            settings => temporary.Cache.Configure(settings.PersistExternalLyricsCache, settings.ExternalLyricsCacheMaxMegabytes),
            temporary.Cache);
        try
        {
            SelectTab(window, "AdvancedTab");
            var usage = (TextBlock)window.FindName("ExternalLyricsCacheUsageValueText");
            Assert.EndsWith(" MB", usage.Text);
            ((Slider)window.FindName("ExternalLyricsCacheMaxMegabytesSlider")).Value = 1;
            ((Button)window.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, temporary.Cache.GetUsage().EntryCount);
            AssertUsageKilobytes(usage.Text, temporary.SizeBytes);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void LyricsCache_PartialClearShowsTheRealRemainingSizeAndLocalizedFailure() => OnSta(() =>
    {
        using var temporary = new TemporaryLyricsCache();
        temporary.Store("Locked");
        temporary.Store("Removable");
        var lockedPath = Directory.GetFiles(temporary.DirectoryPath).First();
        using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var window = new SettingsWindow(new AppSettings { UiLanguage = "en-US" }, lyricsCache: temporary.Cache);
        try
        {
            SelectTab(window, "AdvancedTab");
            var clear = (Button)window.FindName("ClearExternalLyricsCacheButton");
            var status = (TextBlock)window.FindName("ExternalLyricsCacheStatusText");
            clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => status.Text == "Some cache files could not be cleared");
            Assert.True(clear.IsEnabled);
            Assert.Single(Directory.GetFiles(temporary.DirectoryPath));
            AssertUsageKilobytes(((TextBlock)window.FindName("ExternalLyricsCacheUsageValueText")).Text, new FileInfo(lockedPath).Length);
            SelectLanguage(window, "zh-CN");
            Assert.Equal("部分缓存未清除", status.Text);
            Assert.True(File.Exists(lockedPath));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void LyricsCache_AZeroByteEntryCanBeClearedButAnUninjectedCacheCannot() => OnSta(() =>
    {
        using var temporary = new TemporaryLyricsCache();
        Directory.CreateDirectory(temporary.DirectoryPath);
        var emptyEntry = Path.Combine(temporary.DirectoryPath, "v1-" + new string('A', 64) + ".json");
        File.WriteAllBytes(emptyEntry, []);
        var window = new SettingsWindow(new AppSettings { UiLanguage = "en-US" }, lyricsCache: temporary.Cache);
        try
        {
            SelectTab(window, "AdvancedTab");
            var usage = (TextBlock)window.FindName("ExternalLyricsCacheUsageValueText");
            var clear = (Button)window.FindName("ClearExternalLyricsCacheButton");
            Assert.Equal("0 B", usage.Text);
            Assert.True(clear.IsEnabled);
            clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => ((TextBlock)window.FindName("ExternalLyricsCacheStatusText")).Visibility == Visibility.Collapsed);
            Assert.False(File.Exists(emptyEntry));
            Assert.Equal("0 B", usage.Text);
            Assert.False(clear.IsEnabled);
        }
        finally { window.Close(); }

        var noCache = new SettingsWindow(new AppSettings { UiLanguage = "en-US" });
        try
        {
            SelectTab(noCache, "AdvancedTab");
            Assert.Equal("0 B", ((TextBlock)noCache.FindName("ExternalLyricsCacheUsageValueText")).Text);
            Assert.False(((Button)noCache.FindName("ClearExternalLyricsCacheButton")).IsEnabled);
        }
        finally { noCache.Close(); }
    });

    private static void AssertUsageKilobytes(string text, long bytes)
    {
        Assert.EndsWith(" KB", text);
        var displayed = double.Parse(text[..^3], CultureInfo.InvariantCulture);
        Assert.InRange(displayed, bytes / 1024.0 - 0.0051, bytes / 1024.0 + 0.0051);
    }

    private static void PumpUntil(Func<bool> condition)
    {
        if (condition()) return;
        var elapsed = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(10),
        };
        timer.Tick += (_, _) =>
        {
            if (condition() || elapsed.Elapsed > TimeSpan.FromSeconds(10)) frame.Continue = false;
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.True(condition(), "The cache operation did not finish on the settings dispatcher.");
    }

    private sealed class TemporaryLyricsCache : IDisposable
    {
        private static readonly string ParentPath = Path.Combine(Path.GetTempPath(), "AppleMusicLyrics.SettingsCache.Tests");
        public string DirectoryPath { get; } = Path.Combine(ParentPath, Guid.NewGuid().ToString("N"));
        public ExternalLyricsCache Cache { get; }
        public long SizeBytes => Directory.Exists(DirectoryPath)
            ? Directory.EnumerateFiles(DirectoryPath).Sum(path => new FileInfo(path).Length) : 0;

        public TemporaryLyricsCache()
        {
            Cache = new ExternalLyricsCache(DirectoryPath);
            Cache.Configure(true, 64);
        }

        public void Store(string title, int textLength = 2048)
        {
            Cache.Store("QQ Music", new PlayerState(title, "Synthetic Artist", "Synthetic Album", 1, 180, false),
                new LyricsDocument("qqmusic:test", "ok", "qqmusic:test", DateTimeOffset.UtcNow,
                    [new LyricsLine(1, 2, new string('x', textLength))], 180));
        }

        public void Dispose()
        {
            var path = Path.GetFullPath(DirectoryPath);
            var parent = Path.GetFullPath(ParentPath) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The test cache is outside its temporary parent.");
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    private static void SelectLanguage(SettingsWindow window, string language)
    {
        var selector = (ComboBox)window.FindName("LanguageComboBox");
        selector.SelectedValue = language;
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        Assert.Equal(language, selector.SelectedValue);
    }

    [Fact]
    public void ViewDiagnostics_OnlyInvokesTheExplicitActionAndKeepsPendingSettings() => OnSta(() =>
    {
        var opens = 0;
        var applies = 0;
        var window = new SettingsWindow(new AppSettings { UiLanguage = "en-US", ShowDebugPanel = false },
            applyPreview: _ => applies++, openDiagnostics: () => opens++);
        try
        {
            Assert.Equal(0, opens);
            SelectTab(window, "AppearanceTab");
            EditFont((ComboBox)window.FindName("FontFamilyComboBox"), "Arial");
            SelectTab(window, "AdvancedTab");
            ((Button)window.FindName("OpenDiagnosticsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, opens);
            Assert.Equal(0, applies);
            Assert.False(window.Settings.ShowDebugPanel);
            Assert.Equal("Optima", window.Settings.FontFamily);
            SelectTab(window, "AppearanceTab");
            Assert.Equal("Arial", ((ComboBox)window.FindName("FontFamilyComboBox")).Text);
        }
        finally { window.Close(); }
    });

    private static string RenderedSelectionText(ComboBox selector)
    {
        selector.ApplyTemplate();
        selector.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        selector.UpdateLayout();
        var presenter = Assert.IsType<ContentPresenter>(selector.Template.FindName("SelectionContent", selector));
        Assert.Equal(Visibility.Visible, presenter.Visibility);
        // These windows stay hidden during tests; explicitly lay out the data template
        // just as the visible selector does before inspecting its rendered text.
        presenter.ApplyTemplate();
        presenter.Measure(new Size(145, 38));
        presenter.Arrange(new Rect(0, 0, 145, 38));
        presenter.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        presenter.UpdateLayout();
        return Assert.Single(VisualDescendants(presenter).OfType<TextBlock>()).Text;
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in VisualDescendants(child)) yield return descendant;
        }
    }

    private static Uri GlyphFontUri(TextBlock preview)
    {
        var text = new FormattedText("中", CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
            new Typeface(preview.FontFamily, preview.FontStyle, preview.FontWeight, preview.FontStretch),
            preview.FontSize, Brushes.White, 1);
        var drawing = new DrawingGroup();
        using (var context = drawing.Open()) context.DrawText(text, new Point());
        return Find(drawing) ?? throw new InvalidOperationException("The preview did not render a Chinese glyph.");

        static Uri? Find(Drawing value)
        {
            if (value is GlyphRunDrawing glyph) return glyph.GlyphRun.GlyphTypeface.FontUri;
            if (value is DrawingGroup group)
            {
                foreach (var child in group.Children) if (Find(child) is { } uri) return uri;
            }
            return null;
        }
    }

    private static void SelectTab(SettingsWindow window, string tabName)
    {
        var tabs = (TabControl)window.FindName("SettingsTabs");
        tabs.SelectedItem = (TabItem)window.FindName(tabName);
        window.Measure(new Size(740, 720));
        window.Arrange(new Rect(0, 0, 740, 720));
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
    }

    private static void EditFont(ComboBox combo, string fontName)
    {
        Assert.True(combo.IsEditable);
        combo.ApplyTemplate();
        var editor = Assert.IsType<TextBox>(combo.Template.FindName("PART_EditableTextBox", combo));
        editor.Text = fontName;
        combo.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        Assert.Contains(fontName, combo.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var language = LocalizationManager.Instance.Preference;
            try { action(); }
            catch (Exception error) { failure = error; }
            finally { LocalizationManager.Instance.SetLanguage(language); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("Settings bindings did not finish on the STA thread.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
