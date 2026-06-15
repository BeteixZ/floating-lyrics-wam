using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AppleMusicLyrics.Core.Configuration;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using WpfButton = System.Windows.Controls.Button;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;

namespace AppleMusicLyrics.App;

public partial class SettingsWindow : Window
{
    private readonly Action<AppSettings>? _applyPreview;

    public SettingsWindow(AppSettings settings, Action<AppSettings>? applyPreview = null)
    {
        InitializeComponent();
        _applyPreview = applyPreview;
        Settings = settings.Clone();
        LoadFontFamilies();
        LoadValues();
    }

    public AppSettings Settings { get; }

    private void LoadFontFamilies()
    {
        var fontFamilies = Fonts.SystemFontFamilies
            .Select(font => font.Source)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(font => font, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        FontFamilyComboBox.ItemsSource = fontFamilies;
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
        BackgroundAlphaSlider.Value = Math.Round(((Settings.BackgroundAlpha ?? 200) / 255.0) * 100, 0);
        MaxCurrentFontSlider.Value = Settings.MaxCurrentFontSize;
        ContextFontSlider.Value = Settings.ContextFontSize;
        GlowOpacitySlider.Value = Settings.GlowOpacity;
        FontFamilyComboBox.Text = Settings.FontFamily;
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

        DialogResult = true;
    }

    private void ApplyButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryUpdateSettingsFromEditors(showErrors: true))
        {
            return;
        }

        _applyPreview?.Invoke(Settings.Clone());
    }

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
                    "Colors must be valid hex values such as #FFFFFF or #CCFFFFFF.",
                    "Invalid color",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            return false;
        }

        Settings.PureMode = PureModeCheckBox.IsChecked == true;
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
        Settings.BackgroundAlpha = (int)Math.Round((BackgroundAlphaSlider.Value / 100.0) * 255);
        Settings.MaxCurrentFontSize = Math.Round(MaxCurrentFontSlider.Value, 1);
        Settings.ContextFontSize = Math.Round(ContextFontSlider.Value, 1);
        Settings.GlowOpacity = Math.Round(GlowOpacitySlider.Value, 2);
        Settings.FontFamily = string.IsNullOrWhiteSpace(FontFamilyComboBox.Text)
            ? "Segoe UI"
            : FontFamilyComboBox.Text.Trim();
        Settings.CurrentLineColor = currentColor;
        Settings.ContextLineColor = contextColor;
        Settings.PausedLineColor = pausedColor;
        Settings.GlowColor = glowColor;
        UpdateColorPreviews();
        return true;
    }

    private void LayoutModeCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not System.Windows.Controls.CheckBox changedCheckBox || changedCheckBox.IsChecked != true)
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
        if (!IsLoaded)
        {
            return;
        }

        UpdateSliderLabels();
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
        LyricsOffsetValueText.Text = $"{LyricsOffsetSlider.Value:+0.00;-0.00;0.00}s";
        OverlayOpacityValueText.Text = $"{OverlayOpacitySlider.Value:P0}";
        BackgroundAlphaValueText.Text = $"{BackgroundAlphaSlider.Value:0}%";
        MaxCurrentFontValueText.Text = $"{Math.Round(MaxCurrentFontSlider.Value):0}px";
        ContextFontValueText.Text = $"{Math.Round(ContextFontSlider.Value):0}px";
        GlowOpacityValueText.Text = $"{GlowOpacitySlider.Value:P0}";
        HoverFadeDurationValueText.Text = $"{HoverFadeDurationSlider.Value:F2}s";
        HoverFadeMinOpacityValueText.Text = $"{HoverFadeMinOpacitySlider.Value:P0}";
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
}
