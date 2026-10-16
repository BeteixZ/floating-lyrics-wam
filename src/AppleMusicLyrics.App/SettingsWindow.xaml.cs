using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AppleMusicLyrics.App.Localization;
using AppleMusicLyrics.App.Services;
using AppleMusicLyrics.Core.Configuration;
using AppleMusicLyrics.Infrastructure.Windows.External;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using WpfButton = System.Windows.Controls.Button;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using WpfComboBox = System.Windows.Controls.ComboBox;

namespace AppleMusicLyrics.App;

public partial class SettingsWindow : Window
{
    private readonly Action<AppSettings>? _applyPreview;
    private readonly Action? _openDiagnostics;
    private readonly ExternalLyricsCache? _lyricsCache;
    private readonly DispatcherTimer _cacheUsageTimer;
    private readonly LocalizationManager _localization = LocalizationManager.Instance;
    private static readonly Lazy<IReadOnlyList<FontChoice>> FontChoices = new(CreateFontChoices);
    private string _languageOnCancel;
    private bool _backgroundAlphaEdited;
    private bool _loadingValues = true;
    private bool _fontPreviewScheduled;
    private bool _updatingLanguage;
    private bool _languageRefreshScheduled;
    private bool _closed;
    private bool _saveAccepted;
    private bool _cacheClearing;
    private ExternalLyricsCacheUsage _cacheUsage = new(0, 0, true);
    private string? _cacheStatusKey;

    public SettingsWindow(AppSettings settings, Action<AppSettings>? applyPreview = null, ExternalLyricsCache? lyricsCache = null,
        Action? openDiagnostics = null)
    {
        _languageOnCancel = _localization.Preference;
        Settings = settings.Clone();
        _applyPreview = applyPreview;
        _openDiagnostics = openDiagnostics;
        _lyricsCache = lyricsCache;
        _localization.SetLanguage(Settings.UiLanguage);
        InitializeComponent();
        _cacheUsageTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _cacheUsageTimer.Tick += CacheUsageTimer_OnTick;
        LoadFontFamilies();
        LoadValues();
        RefreshLanguageChoices();
        FontFamilyComboBox.AddHandler(System.Windows.Controls.TextBox.TextChangedEvent,
            new TextChangedEventHandler(FontEditor_OnTextChanged));
        ChineseFontFamilyComboBox.AddHandler(System.Windows.Controls.TextBox.TextChangedEvent,
            new TextChangedEventHandler(FontEditor_OnTextChanged));
        TranslationFontFamilyComboBox.AddHandler(System.Windows.Controls.TextBox.TextChangedEvent,
            new TextChangedEventHandler(FontEditor_OnTextChanged));
        _loadingValues = false;
        _localization.LanguageChanged += Localization_OnLanguageChanged;
        Loaded += SettingsWindow_OnLoaded;
        UpdateFontPreview();
        RefreshCacheUsage();
    }

    public AppSettings Settings { get; }

    private void OpenDiagnosticsButton_OnClick(object sender, RoutedEventArgs e) => _openDiagnostics?.Invoke();

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _cacheUsageTimer.Stop();
        _cacheUsageTimer.Tick -= CacheUsageTimer_OnTick;
        Loaded -= SettingsWindow_OnLoaded;
        _localization.LanguageChanged -= Localization_OnLanguageChanged;
        if (!_saveAccepted)
        {
            _localization.SetLanguage(_languageOnCancel);
        }
        base.OnClosed(e);
    }

    private void RefreshLanguageChoices()
    {
        _updatingLanguage = true;
        try
        {
            LanguageComboBox.ItemsSource = _localization.AvailableLanguages;
            var preference = _localization.AvailableLanguages.Any(option =>
                string.Equals(option.Code, _localization.Preference, StringComparison.OrdinalIgnoreCase))
                ? _localization.Preference : _localization.EffectiveLanguage;
            LanguageComboBox.SelectedValue = preference;
        }
        finally { _updatingLanguage = false; }
    }

    private void LanguageComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingValues || _updatingLanguage || LanguageComboBox.SelectedValue is not string language) return;
        _localization.SetLanguage(language);
    }

    private void Localization_OnLanguageChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Localization_OnLanguageChanged(sender, e),
                System.Windows.Threading.DispatcherPriority.DataBind);
            return;
        }
        if (_closed || _languageRefreshScheduled) return;
        _languageRefreshScheduled = true;
        // The system option's label changes with the language. Replacing that selected
        // item during SelectionChanged lets ComboBox overwrite the new display afterward.
        Dispatcher.BeginInvoke(() =>
        {
            _languageRefreshScheduled = false;
            if (_closed) return;
            RefreshLanguageChoices();
            UpdateSliderLabels();
            RenderCacheUsage();
        }, System.Windows.Threading.DispatcherPriority.DataBind);
    }

    private void SettingsWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshCacheUsage();
        UpdateCacheUsageTimer();
    }

    private void SettingsTabs_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingValues || !ReferenceEquals(e.OriginalSource, SettingsTabs)) return;
        UpdateCacheUsageTimer();
        if (AdvancedTab.IsSelected) RefreshCacheUsage();
    }

    private void UpdateCacheUsageTimer()
    {
        if (!_closed && AdvancedTab.IsSelected && _lyricsCache is not null) _cacheUsageTimer.Start();
        else _cacheUsageTimer.Stop();
    }

    private void CacheUsageTimer_OnTick(object? sender, EventArgs e) => RefreshCacheUsage();

    private void RefreshCacheUsage()
    {
        if (_closed || _cacheClearing) return;
        _cacheUsage = _lyricsCache?.GetUsage() ?? new ExternalLyricsCacheUsage(0, 0, true);
        RenderCacheUsage();
    }

    private void RenderCacheUsage()
    {
        ExternalLyricsCacheUsageValueText.Text = _cacheUsage.IsAvailable
            ? FormatCacheSize(_cacheUsage.SizeBytes)
            : _localization.Text("Settings.Cache.Unavailable");
        ClearExternalLyricsCacheButton.IsEnabled = _lyricsCache is not null && !_cacheClearing
            && (!_cacheUsage.IsAvailable || _cacheUsage.SizeBytes > 0 || _cacheUsage.EntryCount > 0);
        ExternalLyricsCacheStatusText.Text = _cacheStatusKey is null ? string.Empty : _localization.Text(_cacheStatusKey);
        ExternalLyricsCacheStatusText.Visibility = _cacheStatusKey is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private string FormatCacheSize(long sizeBytes)
    {
        var keys = new[] { "Settings.Cache.SizeBytes", "Settings.Cache.SizeKilobytes", "Settings.Cache.SizeMegabytes", "Settings.Cache.SizeGigabytes" };
        var value = (double)Math.Max(0, sizeBytes);
        var unit = 0;
        while (value >= 1024 && unit < keys.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return _localization.Format(keys[unit], value);
    }

    private async void ClearExternalLyricsCacheButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_cacheClearing || _lyricsCache is null) return;
        _cacheClearing = true;
        _cacheStatusKey = "Settings.Cache.Clearing";
        RenderCacheUsage();
        var result = await Task.Run(_lyricsCache.Clear).ConfigureAwait(false);
        if (_closed || Dispatcher.HasShutdownStarted) return;
        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (_closed) return;
                _cacheClearing = false;
                _cacheUsage = result.RemainingUsage;
                _cacheStatusKey = result.Success ? null : "Settings.Cache.ClearFailed";
                RenderCacheUsage();
            });
        }
        catch (TaskCanceledException) when (_closed || Dispatcher.HasShutdownStarted) { }
    }

    private void LoadFontFamilies()
    {
        var latin = FontChoices.Value.ToList();
        AddConfiguredFont(latin, FontFamilyResolver.DefaultFontName);
        AddConfiguredFont(latin, Settings.FontFamily);
        var chinese = FontChoices.Value.Where(choice => choice.SupportsChinese).ToList();
        AddConfiguredFont(chinese, Settings.ChineseFontFamily);
        var translation = FontChoices.Value.ToList();
        AddConfiguredFont(translation, FontFamilyResolver.DefaultChineseFontName);
        AddConfiguredFont(translation, Settings.TranslationFontFamily);
        FontFamilyComboBox.ItemsSource = latin.OrderBy(choice => choice.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
        ChineseFontFamilyComboBox.ItemsSource = chinese.OrderBy(choice => choice.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
        TranslationFontFamilyComboBox.ItemsSource = translation.OrderBy(choice => choice.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static IReadOnlyList<FontChoice> CreateFontChoices()
    {
        var choices = new List<FontChoice>();
        foreach (var family in Fonts.SystemFontFamilies)
        {
            var english = family.FamilyNames.FirstOrDefault(pair =>
                string.Equals(pair.Key.IetfLanguageTag, "en-US", StringComparison.OrdinalIgnoreCase)).Value;
            var canonical = string.IsNullOrWhiteSpace(english) ? family.Source : english;
            var localized = family.FamilyNames.FirstOrDefault(pair =>
                pair.Key.IetfLanguageTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase)).Value;
            var display = string.IsNullOrWhiteSpace(localized)
                || string.Equals(localized, canonical, StringComparison.OrdinalIgnoreCase)
                ? canonical : $"{localized} · {canonical}";
            var supportsChinese = false;
            try
            {
                var face = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                supportsChinese = face.TryGetGlyphTypeface(out var glyph)
                    && glyph.CharacterToGlyphMap.ContainsKey(0x4E2D);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.IO.IOException)
            {
                // A broken installed face should not prevent the settings window from opening.
            }
            choices.Add(new FontChoice(canonical, display, supportsChinese));
        }
        return choices.DistinctBy(choice => choice.CanonicalName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddConfiguredFont(List<FontChoice> choices, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || choices.Any(choice => string.Equals(choice.CanonicalName, name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        choices.Add(new FontChoice(name, name, false));
    }

    private static void SelectFont(WpfComboBox editor, string name)
    {
        var choice = editor.Items.OfType<FontChoice>().FirstOrDefault(item =>
            string.Equals(item.CanonicalName, name, StringComparison.OrdinalIgnoreCase));
        editor.SelectedItem = choice;
        editor.Text = choice?.DisplayName ?? name;
    }

    private static string ReadFontName(WpfComboBox editor, string fallback)
    {
        var text = editor.Text.Trim();
        if (text.Length == 0) return fallback;
        var choice = editor.Items.OfType<FontChoice>().FirstOrDefault(item =>
            string.Equals(item.DisplayName, text, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.CanonicalName, text, StringComparison.OrdinalIgnoreCase));
        return choice?.CanonicalName ?? text;
    }

    private void LoadValues()
    {
        PureModeCheckBox.IsChecked = Settings.PureMode;
        ClickThroughCheckBox.IsChecked = Settings.ClickThrough;
        AutoHideCheckBox.IsChecked = Settings.AutoHideNoLyrics;
        FadeWhenPausedCheckBox.IsChecked = Settings.FadeWhenPaused;
        SingleLineModeCheckBox.IsChecked = Settings.SingleLineMode;
        TwoLineModeCheckBox.IsChecked = Settings.TwoLineMode;
        ShowDebugPanelCheckBox.IsChecked = Settings.ShowDebugPanel;
        HoverFadeCheckBox.IsChecked = Settings.HoverFadeEnabled;
        HoverFadeDurationSlider.Value = Settings.HoverFadeDuration;
        HoverFadeMinOpacitySlider.Value = Settings.HoverFadeMinOpacity;
        LyricsOffsetSlider.Value = Settings.LyricsOffsetSeconds;
        OverlayOpacitySlider.Value = Settings.OverlayOpacity;
        PureModeDragOpacitySlider.Value = Settings.PureModeDragOpacity;
        BackgroundAlphaSlider.Value = Math.Round(((Settings.BackgroundAlpha ?? (Settings.PureMode ? 72 : 200)) / 255.0) * 100, 0);
        MaxCurrentFontSlider.Value = Settings.MaxCurrentFontSize;
        ContextFontSlider.Value = Settings.ContextFontSize;
        TranslationFontSizeSlider.Value = TranslationTypography.GetFontSize(Settings.TranslationFontSize);
        ExternalLyricsCacheMaxMegabytesSlider.Value = Math.Clamp(Settings.ExternalLyricsCacheMaxMegabytes, 1, 1024);
        GlowOpacitySlider.Value = Settings.GlowOpacity;
        SelectFont(FontFamilyComboBox, Settings.FontFamily);
        SelectFont(ChineseFontFamilyComboBox, Settings.ChineseFontFamily);
        SelectFont(TranslationFontFamilyComboBox, Settings.TranslationFontFamily);
        CurrentColorTextBox.Text = Settings.CurrentLineColor;
        ContextColorTextBox.Text = Settings.ContextLineColor;
        PausedColorTextBox.Text = Settings.PausedLineColor;
        GlowColorTextBox.Text = Settings.GlowColor;
        UpdateSliderLabels();
        UpdateColorPreviews();
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryUpdateSettingsFromEditors(showErrors: true))
        {
            return;
        }

        _saveAccepted = true;
        DialogResult = true;
    }

    private void ApplyButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryUpdateSettingsFromEditors(showErrors: true))
        {
            return;
        }

        _applyPreview?.Invoke(Settings.Clone());
        _languageOnCancel = _localization.Preference;
        RefreshCacheUsage();
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private bool TryUpdateSettingsFromEditors(bool showErrors)
    {
        if (!TryNormalizeColor(CurrentColorTextBox.Text, out var currentColor) ||
            !TryNormalizeColor(ContextColorTextBox.Text, out var contextColor) ||
            !TryNormalizeColor(PausedColorTextBox.Text, out var pausedColor) ||
            !TryNormalizeColor(GlowColorTextBox.Text, out var glowColor))
        {
            if (showErrors)
            {
                System.Windows.MessageBox.Show(
                    this,
                    _localization.Text("Settings.InvalidColor.Message"),
                    _localization.Text("Settings.InvalidColor.Title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            return false;
        }

        Settings.PureMode = PureModeCheckBox.IsChecked == true;
        Settings.UiLanguage = _localization.Preference;
        Settings.ExternalLyricsCacheMaxMegabytes = Math.Clamp((int)Math.Round(ExternalLyricsCacheMaxMegabytesSlider.Value), 1, 1024);
        Settings.ClickThrough = ClickThroughCheckBox.IsChecked == true;
        Settings.AutoHideNoLyrics = AutoHideCheckBox.IsChecked == true;
        Settings.FadeWhenPaused = FadeWhenPausedCheckBox.IsChecked == true;
        Settings.SingleLineMode = SingleLineModeCheckBox.IsChecked == true;
        Settings.TwoLineMode = TwoLineModeCheckBox.IsChecked == true;
        if (Settings.SingleLineMode && Settings.TwoLineMode)
        {
            Settings.TwoLineMode = false;
        }

        Settings.ShowDebugPanel = ShowDebugPanelCheckBox.IsChecked == true;
        Settings.HoverFadeEnabled = HoverFadeCheckBox.IsChecked == true;
        Settings.HoverFadeDuration = Math.Round(HoverFadeDurationSlider.Value, 2);
        Settings.HoverFadeMinOpacity = Math.Round(HoverFadeMinOpacitySlider.Value, 2);
        Settings.LyricsOffsetSeconds = Math.Round(LyricsOffsetSlider.Value, 2);
        Settings.OverlayOpacity = Math.Round(OverlayOpacitySlider.Value, 2);
        Settings.PureModeDragOpacity = Math.Round(PureModeDragOpacitySlider.Value, 2);
        if (_backgroundAlphaEdited)
        {
            Settings.BackgroundAlpha = (int)Math.Round((BackgroundAlphaSlider.Value / 100.0) * 255);
        }

        Settings.MaxCurrentFontSize = Math.Round(MaxCurrentFontSlider.Value, 1);
        Settings.ContextFontSize = Math.Round(ContextFontSlider.Value, 1);
        Settings.TranslationFontSize = TranslationTypography.GetFontSize(Math.Round(TranslationFontSizeSlider.Value, 1));
        Settings.GlowOpacity = Math.Round(GlowOpacitySlider.Value, 2);
        Settings.FontFamily = ReadFontName(FontFamilyComboBox, FontFamilyResolver.DefaultFontName);
        Settings.ChineseFontFamily = ReadFontName(ChineseFontFamilyComboBox, FontFamilyResolver.DefaultChineseFontName);
        Settings.TranslationFontFamily = ReadFontName(TranslationFontFamilyComboBox, FontFamilyResolver.DefaultChineseFontName);
        Settings.CurrentLineColor = currentColor;
        Settings.ContextLineColor = contextColor;
        Settings.PausedLineColor = pausedColor;
        Settings.GlowColor = glowColor;
        UpdateColorPreviews();
        return true;
    }

    private void LayoutModeCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingValues || sender is not System.Windows.Controls.CheckBox changedCheckBox || changedCheckBox.IsChecked != true)
        {
            return;
        }

        if (ReferenceEquals(changedCheckBox, SingleLineModeCheckBox))
        {
            TwoLineModeCheckBox.IsChecked = false;
        }
        else if (ReferenceEquals(changedCheckBox, TwoLineModeCheckBox))
        {
            SingleLineModeCheckBox.IsChecked = false;
        }
    }

    private void SliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingValues)
        {
            return;
        }

        if (ReferenceEquals(sender, BackgroundAlphaSlider))
        {
            _backgroundAlphaEdited = true;
        }

        UpdateSliderLabels();
        QueueFontPreview();
    }

    private void FontChoice_OnChanged(object sender, SelectionChangedEventArgs e) => QueueFontPreview();

    private void FontEditor_OnTextChanged(object sender, TextChangedEventArgs e) => QueueFontPreview();

    private void QueueFontPreview()
    {
        if (_loadingValues || _fontPreviewScheduled) return;
        _fontPreviewScheduled = true;
        Dispatcher.BeginInvoke(() =>
        {
            _fontPreviewScheduled = false;
            UpdateFontPreview();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void UpdateFontPreview()
    {
        var family = FontFamilyResolver.Resolve(
            ReadFontName(FontFamilyComboBox, FontFamilyResolver.DefaultFontName),
            ReadFontName(ChineseFontFamilyComboBox, FontFamilyResolver.DefaultChineseFontName));
        FontPreviewText.FontFamily = family;
        FontPreviewContextText.FontFamily = family;
        FontPreviewText.FontSize = Math.Clamp(MaxCurrentFontSlider.Value, 14, 44);
        FontPreviewContextText.FontSize = Math.Clamp(ContextFontSlider.Value, 12, 28);
        TranslationFontPreviewText.FontFamily = TranslationTypography.ResolveFontFamily(
            ReadFontName(TranslationFontFamilyComboBox, FontFamilyResolver.DefaultChineseFontName));
        TranslationFontPreviewText.FontSize = TranslationTypography.GetFontSize(TranslationFontSizeSlider.Value);
    }

    private void PickColorButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton button)
        {
            return;
        }

        var textBox = button.Tag?.ToString() switch
        {
            "Current" => CurrentColorTextBox,
            "Context" => ContextColorTextBox,
            "Paused" => PausedColorTextBox,
            "Glow" => GlowColorTextBox,
            _ => null,
        };

        if (textBox is null)
        {
            return;
        }

        using var colorDialog = new Forms.ColorDialog
        {
            AllowFullOpen = true,
            FullOpen = true,
            AnyColor = true,
        };

        if (TryNormalizeColor(textBox.Text, out var currentColor))
        {
            var parsed = ParseColor(currentColor, Colors.White);
            colorDialog.Color = Drawing.Color.FromArgb(parsed.A, parsed.R, parsed.G, parsed.B);
        }

        if (colorDialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        var color = colorDialog.Color;
        textBox.Text = FormattableString.Invariant($"#{color.R:X2}{color.G:X2}{color.B:X2}");
        UpdateColorPreviews();
    }

    private void UpdateSliderLabels()
    {
        LyricsOffsetValueText.Text = _localization.Format("Settings.Unit.SignedSeconds", LyricsOffsetSlider.Value);
        OverlayOpacityValueText.Text = _localization.Format("Settings.Unit.Percent", OverlayOpacitySlider.Value);
        PureModeDragOpacityValueText.Text = _localization.Format("Settings.Unit.Percent", PureModeDragOpacitySlider.Value);
        BackgroundAlphaValueText.Text = _localization.Format("Settings.Unit.WholePercent", BackgroundAlphaSlider.Value);
        MaxCurrentFontValueText.Text = _localization.Format("Settings.Unit.Pixels", MaxCurrentFontSlider.Value);
        ContextFontValueText.Text = _localization.Format("Settings.Unit.Pixels", ContextFontSlider.Value);
        TranslationFontSizeValueText.Text = _localization.Format("Settings.Unit.Pixels", TranslationFontSizeSlider.Value);
        ExternalLyricsCacheMaxMegabytesValueText.Text = _localization.Format("Settings.Unit.Megabytes", ExternalLyricsCacheMaxMegabytesSlider.Value);
        GlowOpacityValueText.Text = _localization.Format("Settings.Unit.Percent", GlowOpacitySlider.Value);
        HoverFadeDurationValueText.Text = _localization.Format("Settings.Unit.Seconds", HoverFadeDurationSlider.Value);
        HoverFadeMinOpacityValueText.Text = _localization.Format("Settings.Unit.Percent", HoverFadeMinOpacitySlider.Value);
    }

    private void UpdateColorPreviews()
    {
        CurrentColorPreview.Background = new SolidColorBrush(ParseColor(CurrentColorTextBox.Text, Colors.White));
        ContextColorPreview.Background = new SolidColorBrush(ParseColor(ContextColorTextBox.Text, Colors.LightGray));
        PausedColorPreview.Background = new SolidColorBrush(ParseColor(PausedColorTextBox.Text, Colors.Gray));
        GlowColorPreview.Background = new SolidColorBrush(ParseColor(GlowColorTextBox.Text, Colors.White));
    }

    private static MediaColor ParseColor(string value, MediaColor fallback)
    {
        try
        {
            return (MediaColor)MediaColorConverter.ConvertFromString(value.Trim())!;
        }
        catch
        {
            return fallback;
        }
    }

    private static bool TryNormalizeColor(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            var color = (MediaColor)MediaColorConverter.ConvertFromString(value.Trim())!;
            normalized = color.A == byte.MaxValue
                ? FormattableString.Invariant($"#{color.R:X2}{color.G:X2}{color.B:X2}")
                : FormattableString.Invariant($"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed record FontChoice(string CanonicalName, string DisplayName, bool SupportsChinese);
}
