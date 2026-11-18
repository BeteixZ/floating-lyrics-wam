using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Parsing;
using Color = System.Windows.Media.Color;

namespace AppleMusicLyrics.App.Presentation;

public sealed class LyricLineView(TextBlock lyric, StackPanel background, TextBlock romanization, TextBlock translation)
{
    private readonly KaraokeTextController _karaoke = new(lyric, emphasizeSustainedWords: true);
    private readonly List<(TextBlock Text, KaraokeTextController Karaoke)> _backgroundRows = [];

    public LyricsLine? Line { get; private set; }
    public TextBlock MainText { get; } = lyric;
    public string DisplayText => _karaoke.DisplayText;
    public bool HasTimedWords => _karaoke.HasTimedWords || _backgroundRows.Any(row => row.Karaoke.HasTimedWords);

    public void Bind(string text, LyricsLine? line, string? reading, string? translatedText, bool highlight, Color sung)
    {
        Line = line;
        _karaoke.SetLine(text, line, highlight);
        if (MainText.Foreground is not SolidColorBrush foreground || foreground.Color != sung)
            MainText.Foreground = new SolidColorBrush(sung);
        var upcoming = Color.FromArgb((byte)Math.Round(sung.A * 0.35), sung.R, sung.G, sung.B);
        _karaoke.SetColors(sung, upcoming);
        BindBackground(line?.BackgroundVocals, highlight, sung, upcoming);
        SetAuxiliaryText(romanization, reading);
        SetAuxiliaryText(translation, LyricsTranslationFilter.Clean(translatedText));
    }

    public void UpdateHighlight(double position)
    {
        _karaoke.Update(position);
        foreach (var row in _backgroundRows) row.Karaoke.Update(position);
    }

    public void SetAuxiliaryAppearance(System.Windows.Media.FontFamily font, System.Windows.Media.Brush foreground)
    {
        foreach (var text in new[] { romanization, translation })
        {
            text.FontFamily = font;
            text.Foreground = foreground;
            text.Opacity = 0.8;
        }
    }

    public void SetFontSizes(double lyricSize, double translationSize)
    {
        MainText.FontSize = lyricSize;
        foreach (var row in _backgroundRows) SetRowSize(row.Text, GetBackgroundFontSize(lyricSize), 1.25);
        SetRowSize(romanization, translationSize, 1.35);
        SetRowSize(translation, translationSize, 1.35);
    }

    public void SetMaxWidth(double width)
    {
        MainText.MaxWidth = width;
        background.MaxWidth = width;
        romanization.MaxWidth = width;
        translation.MaxWidth = width;
    }

    public IEnumerable<(TextBlock Element, string Text, double FontSize)> GetAuxiliaryRows(double lyricSize, double translationSize)
    {
        foreach (var row in _backgroundRows)
            yield return (row.Text, row.Karaoke.DisplayText, GetBackgroundFontSize(lyricSize));
        if (romanization.Visibility == Visibility.Visible)
            yield return (romanization, romanization.Text, translationSize);
        if (translation.Visibility == Visibility.Visible)
            yield return (translation, translation.Text, translationSize);
    }

    private void BindBackground(IReadOnlyList<LyricsLine>? vocals, bool highlight, Color sung, Color upcoming)
    {
        var count = vocals?.Count ?? 0;
        while (_backgroundRows.Count > count)
        {
            var last = _backgroundRows.Count - 1;
            _backgroundRows.RemoveAt(last);
            background.Children.RemoveAt(last);
        }
        while (_backgroundRows.Count < count)
        {
            var text = new TextBlock
            {
                Margin = new Thickness(0, 0, 0, 4),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            };
            SetRowSize(text, GetBackgroundFontSize(MainText.FontSize), 1.25);
            _backgroundRows.Add((text, new KaraokeTextController(text)));
            background.Children.Add(text);
        }
        background.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        for (var index = 0; index < count; index++)
        {
            var vocal = vocals![index];
            var row = _backgroundRows[index];
            row.Text.FontFamily = MainText.FontFamily;
            row.Text.Foreground = MainText.Foreground;
            row.Karaoke.SetLine(vocal.Text, vocal, highlight);
            row.Karaoke.SetColors(sung, upcoming);
        }
    }

    private static void SetAuxiliaryText(TextBlock text, string? value)
    {
        text.Text = value ?? string.Empty;
        text.Visibility = string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static double GetBackgroundFontSize(double lyricSize) => lyricSize * 0.64;

    private static void SetRowSize(TextBlock text, double size, double lineSpacing)
    {
        text.FontSize = size;
        text.LineHeight = size * lineSpacing;
        text.MaxHeight = text.LineHeight * 2;
    }
}
