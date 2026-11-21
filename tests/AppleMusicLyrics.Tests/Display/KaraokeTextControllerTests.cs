using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AppleMusicLyrics.App.Presentation;
using AppleMusicLyrics.Core.Models;
using Xunit;

namespace AppleMusicLyrics.Tests.Display;

public sealed class KaraokeTextControllerTests
{
    [Fact]
    public void Update_SweepsActiveWordsRetainsCompletedWordsAndResetsAfterASeek() => OnSta(() =>
    {
        var text = new TextBlock();
        var controller = new KaraokeTextController(text);
        var line = new LyricsLine(1, 6, "One Two", Words:
            [new LyricWord(1, 3, "One"), new LyricWord(4, 6, "Two")]);
        controller.SetLine(line.Text, line, enabled: true);
        var runs = text.Inlines.OfType<Run>().ToArray();
        var first = Assert.IsType<LinearGradientBrush>(runs[0].Foreground);
        var second = Assert.IsType<LinearGradientBrush>(runs[2].Foreground);

        controller.Update(2);
        Assert.Equal(new[] { 0.0, 0.5, 0.5, 1.0 }, first.GradientStops.Select(stop => stop.Offset));
        Assert.Equal(new[] { 0.0, 0.0, 0.0, 1.0 }, second.GradientStops.Select(stop => stop.Offset));

        controller.Update(5);
        Assert.Equal(1, first.GradientStops[1].Offset);
        Assert.Equal(0.5, second.GradientStops[1].Offset);
        controller.Update(0);
        Assert.Equal(0, first.GradientStops[1].Offset);
        Assert.Equal(0, second.GradientStops[1].Offset);
        var brushChanges = 0;
        first.Changed += (_, _) => brushChanges++;
        second.Changed += (_, _) => brushChanges++;
        controller.Update(0); // A paused clock should leave the same objects and offsets alone.
        Assert.Equal(0, brushChanges);
        Assert.Equal(runs, text.Inlines.OfType<Run>().ToArray());
        Assert.Equal(line.Text, controller.DisplayText);
        Assert.Equal(line.Text, string.Concat(text.Inlines.OfType<Run>().Select(run => run.Text)));
    });

    [Theory]
    [InlineData(74.0)]
    [InlineData(170.0)]
    public void Update_PreservesRealWrappedTextGeometryAndRunIdentity(double width) => OnSta(() =>
    {
        const string lyric = "supercalifragilisticexpialidocious / 你好世界 😀!";
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 22,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
        };
        var controller = new KaraokeTextController(text);
        var line = new LyricsLine(0, 9, lyric, Words:
            [new LyricWord(0, 3, "supercalifragilisticexpialidocious"),
             new LyricWord(3, 6, "你好世界"), new LyricWord(6, 9, "😀")]);
        controller.SetLine(lyric, line, enabled: true);
        var runs = text.Inlines.OfType<Run>().ToArray();
        Assert.Equal(lyric, string.Concat(runs.Select(run => run.Text)));
        Assert.Equal(lyric, controller.DisplayText);
        var initial = Render(text, width);
        var initialSize = text.DesiredSize;
        Assert.True(initial.PixelHeight > 30); // Both a long word and Unicode fragments really wrap.

        controller.Update(1.5);
        controller.SetLine(lyric, line, enabled: true);
        var changed = Render(text, width);

        Assert.Equal(initialSize, text.DesiredSize);
        Assert.Equal(initial.PixelWidth, changed.PixelWidth);
        Assert.Equal(initial.PixelHeight, changed.PixelHeight);
        Assert.Equal(runs, text.Inlines.OfType<Run>().ToArray());
        Assert.Equal(lyric, controller.DisplayText);
        Assert.Equal(lyric, string.Concat(text.Inlines.OfType<Run>().Select(run => run.Text)));
    });

    [Fact]
    public void SetLine_PreservesUntimedPunctuationAndTrimsOnlyTheOuterWordEdges() => OnSta(() =>
    {
        const string lyric = "你好， world 😀!";
        var text = new TextBlock();
        var controller = new KaraokeTextController(text);
        var line = new LyricsLine(0, 6, lyric, Words:
            [new LyricWord(0, 2, " 你好"), new LyricWord(2, 4, "world"), new LyricWord(4, 6, "😀! ")]);

        controller.SetLine(lyric, line, enabled: true);

        Assert.True(controller.HasTimedWords);
        Assert.Equal(lyric, controller.DisplayText);
        Assert.Equal(lyric, string.Concat(text.Inlines.OfType<Run>().Select(run => run.Text)));
        Assert.Equal(new[] { "你好", "， ", "world", " ", "😀!" }, text.Inlines.OfType<Run>().Select(run => run.Text));
    });

    [Fact]
    public void Update_KeepsSourceTextAndRunsAfterFontAndEllipsisStyleChanges() => OnSta(() =>
    {
        const string lyric = "A long original lyric";
        var text = new TextBlock { FontSize = 18, TextWrapping = TextWrapping.Wrap };
        var controller = new KaraokeTextController(text);
        var line = new LyricsLine(0, 5, lyric, Words:
            [new LyricWord(0, 1, "A "), new LyricWord(1, 2, "long "),
             new LyricWord(2, 4, "original "), new LyricWord(4, 5, "lyric")]);
        controller.SetLine(lyric, line, enabled: true);
        var runs = text.Inlines.OfType<Run>().ToArray();
        Render(text, 90);

        text.FontSize = 30;
        text.TextWrapping = TextWrapping.NoWrap;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        controller.Update(3);
        var bitmap = Render(text, 90);

        Assert.Equal(90, bitmap.PixelWidth);
        Assert.Equal(lyric, controller.DisplayText);
        Assert.Equal(lyric, string.Concat(text.Inlines.OfType<Run>().Select(run => run.Text)));
        Assert.Equal(runs, text.Inlines.OfType<Run>().ToArray());
        Assert.Equal(0.5, Assert.IsType<LinearGradientBrush>(runs[2].Foreground).GradientStops[1].Offset);
    });

    [Fact]
    public void SetColors_ReusesBrushesAndPreservesCurrentProgress() => OnSta(() =>
    {
        var text = new TextBlock();
        var controller = new KaraokeTextController(text);
        var line = new LyricsLine(1, 3, "Word", Words: [new LyricWord(1, 3, "Word")]);
        controller.SetLine(line.Text, line, enabled: true);
        controller.Update(2.5);
        var run = Assert.Single(text.Inlines.OfType<Run>());
        var brush = Assert.IsType<LinearGradientBrush>(run.Foreground);

        controller.SetColors(Colors.Gold, Colors.DarkSlateGray);

        Assert.Same(run, Assert.Single(text.Inlines.OfType<Run>()));
        Assert.Same(brush, run.Foreground);
        Assert.Equal(new[] { Colors.Gold, Colors.Gold, Colors.DarkSlateGray, Colors.DarkSlateGray },
            brush.GradientStops.Select(stop => stop.Color));
        Assert.Equal(0.75, brush.GradientStops[1].Offset);
    });

    [Fact]
    public void SetLine_MatchesTheTrimmedFirstWordBeforeALaterRepeatedOccurrence() => OnSta(() =>
    {
        var text = new TextBlock();
        var controller = new KaraokeTextController(text);
        var line = new LyricsLine(0, 4, "Hello Hello", Words:
            [new LyricWord(0, 2, " Hello"), new LyricWord(2, 4, " Hello ")]);

        controller.SetLine(line.Text, line, enabled: true);
        controller.Update(1);

        Assert.True(controller.HasTimedWords);
        Assert.Equal(line.Text, controller.DisplayText);
        Assert.Equal(line.Text, string.Concat(text.Inlines.OfType<Run>().Select(run => run.Text)));
        var runs = text.Inlines.OfType<Run>().ToArray();
        Assert.Equal(new[] { "Hello", " Hello" }, runs.Select(run => run.Text));
        Assert.Equal(0.5, Assert.IsType<LinearGradientBrush>(runs[0].Foreground).GradientStops[1].Offset);
        Assert.Equal(0, Assert.IsType<LinearGradientBrush>(runs[1].Foreground).GradientStops[1].Offset);
    });

    [Fact]
    public void SetLine_RebindsRepeatedTextWhenTheLineOrSettingChanges() => OnSta(() =>
    {
        var foreground = Brushes.CornflowerBlue;
        var text = new TextBlock { Foreground = foreground };
        var controller = new KaraokeTextController(text);
        var first = new LyricsLine(0, 2, "Repeat", Words: [new LyricWord(0, 2, "Repeat")]);
        var next = new LyricsLine(5, 7, "Repeat", Words: [new LyricWord(5, 7, "Repeat")]);
        controller.SetLine(first.Text, first, enabled: true);
        controller.Update(2);
        var firstRun = Assert.Single(text.Inlines.OfType<Run>());

        controller.SetLine(next.Text, next, enabled: true);
        controller.Update(5);
        var nextRun = Assert.Single(text.Inlines.OfType<Run>());
        Assert.NotSame(firstRun, nextRun);
        Assert.Equal(0, Assert.IsType<LinearGradientBrush>(nextRun.Foreground).GradientStops[1].Offset);

        controller.SetLine(next.Text, next, enabled: false);
        controller.Update(6);
        Assert.False(controller.HasTimedWords);
        Assert.Equal("Repeat", text.Text);
        Assert.Same(foreground, Assert.Single(text.Inlines.OfType<Run>()).Foreground);
    });

    [Theory]
    [InlineData(double.NaN, 2)]
    [InlineData(0, double.PositiveInfinity)]
    [InlineData(3, 2)]
    public void SetLine_UsesOrdinaryFullTextForInvalidTimings(double begin, double end) => OnSta(() =>
    {
        var text = new TextBlock { Foreground = Brushes.White };
        var controller = new KaraokeTextController(text);
        var line = new LyricsLine(0, 5, "Full text", Words: [new LyricWord(begin, end, "Full")]);

        controller.SetLine(line.Text, line, enabled: true);
        controller.Update(1);

        Assert.False(controller.HasTimedWords);
        Assert.Equal(line.Text, text.Text);
        Assert.IsType<SolidColorBrush>(Assert.Single(text.Inlines.OfType<Run>()).Foreground);
    });

    [Fact]
    public void SetLine_UsesOrdinaryFullTextForMissingWordsOrMismatchedFragments() => OnSta(() =>
    {
        var text = new TextBlock();
        var controller = new KaraokeTextController(text);
        var lineOnly = new LyricsLine(0, 5, "Line only");
        controller.SetLine(lineOnly.Text, lineOnly, enabled: true);
        Assert.False(controller.HasTimedWords);
        Assert.Equal(lineOnly.Text, text.Text);

        var mismatched = lineOnly with { Words = [new LyricWord(0, 5, "Absent")] };
        controller.SetLine(mismatched.Text, mismatched, enabled: true);
        Assert.False(controller.HasTimedWords);
        Assert.Equal(lineOnly.Text, text.Text);

        var partialMatch = lineOnly with { Words = [new LyricWord(0, 5, "only")] };
        controller.SetLine("Different line only", partialMatch, enabled: true);
        Assert.False(controller.HasTimedWords);
        Assert.Equal("Different line only", text.Text);
    });

    [Fact]
    public void Update_HandlesInstantFragmentsAndIgnoresAnInvalidPosition() => OnSta(() =>
    {
        var text = new TextBlock();
        var controller = new KaraokeTextController(text);
        var line = new LyricsLine(0, 5, "Instant", Words: [new LyricWord(2, 2, "Instant")]);
        controller.SetLine(line.Text, line, enabled: true);
        var brush = Assert.IsType<LinearGradientBrush>(Assert.Single(text.Inlines.OfType<Run>()).Foreground);
        controller.Update(1);
        Assert.Equal(0, brush.GradientStops[1].Offset);
        controller.Update(2);
        Assert.Equal(1, brush.GradientStops[1].Offset);
        controller.Update(double.NaN);
        Assert.Equal(1, brush.GradientStops[1].Offset);
    });

    [Fact]
    public void Update_SwellsAndGlowsOnlyAHeldSingleWordWhileItIsSung() => OnSta(() =>
    {
        var text = new TextBlock { FontSize = 30 };
        var controller = new KaraokeTextController(text, emphasizeSustainedWords: true);
        var line = new LyricsLine(1, 5, "Oh baby", Words:
            [new LyricWord(1, 1.3, "Oh "), new LyricWord(1.3, 4, "baby")]);
        controller.SetLine(line.Text, line, enabled: true);

        // The short word is one floating element; the held one becomes one element per letter.
        var containers = text.Inlines.OfType<InlineUIContainer>().ToArray();
        Assert.Equal(2, containers.Length);
        Assert.Equal("Oh", Assert.IsType<TextBlock>(containers[0].Child).Text);
        var letters = Assert.IsType<StackPanel>(containers[1].Child).Children.Cast<TextBlock>().ToArray();
        Assert.Equal(["b", "a", "b", "y"], letters.Select(letter => letter.Text));
        Assert.Equal(" ", Assert.Single(text.Inlines.OfType<Run>()).Text);
        Assert.Equal(line.Text, controller.DisplayText);
        static ScaleTransform Scale(TextBlock letter) =>
            Assert.IsType<TransformGroup>(letter.RenderTransform).Children.OfType<ScaleTransform>().Single();
        static double Wipe(TextBlock letter) => ((LinearGradientBrush)letter.Foreground).GradientStops[1].Offset;

        controller.Update(1.0);
        Assert.All(letters, letter => Assert.Null(letter.Effect));

        // 44% through "baby": the wipe has passed "b", is inside "a", and has not reached "by".
        controller.Update(2.5);
        Assert.True(Wipe(letters[0]) >= 0.999);
        Assert.InRange(Wipe(letters[1]), 0.01, 0.99);
        Assert.True(Wipe(letters[2]) < 0 && Wipe(letters[3]) < 0);
        // The letters it reached have risen and glow; the rest wait for it, like a wave.
        Assert.True(Scale(letters[0]).ScaleX > 1.05);
        Assert.NotNull(letters[0].Effect);
        Assert.True(Scale(letters[1]).ScaleX > 1.05);
        Assert.Equal(1, Scale(letters[2]).ScaleX);
        Assert.Equal(1, Scale(letters[3]).ScaleX);
        Assert.Null(letters[3].Effect);

        controller.Update(6);
        Assert.All(letters, letter =>
        {
            Assert.Equal(1, Scale(letter).ScaleX);
            Assert.Null(letter.Effect);
            Assert.True(Wipe(letter) >= 0.999);
        });
    });

    [Fact]
    public void Update_FloatsEachSungWordUpWithASoftWipe() => OnSta(() =>
    {
        var text = new TextBlock { FontSize = 40 };
        var controller = new KaraokeTextController(text, emphasizeSustainedWords: true);
        var line = new LyricsLine(1, 3, "Then love", Words:
            [new LyricWord(1, 1.4, "Then "), new LyricWord(1.4, 1.8, "love")]);
        controller.SetLine(line.Text, line, enabled: true);
        var words = text.Inlines.OfType<InlineUIContainer>().Select(container => (TextBlock)container.Child).ToArray();
        static double Lift(TextBlock word) => ((TranslateTransform)word.RenderTransform).Y;
        static LinearGradientBrush Brush(TextBlock word) => (LinearGradientBrush)word.Foreground;

        controller.Update(1.0);
        Assert.All(words, word => Assert.Equal(0, Lift(word)));
        // Not yet reached: the soft edge waits fully before the word.
        Assert.True(Brush(words[0]).GradientStops[2].Offset <= 0);

        controller.Update(1.6);
        Assert.InRange(Lift(words[0]), -40 * 0.045 - 0.01, -1);
        Assert.InRange(Lift(words[1]), Lift(words[0]), 0);
        // Half-way through "love": a soft edge straddles the middle instead of a hard cut.
        var stops = Brush(words[1]).GradientStops;
        Assert.True(stops[1].Offset < 0.5 && stops[2].Offset > 0.5);

        // Sung words stay up with the line rather than dropping back.
        controller.Update(2.9);
        Assert.All(words, word => Assert.Equal(-40 * 0.045, Lift(word), 3));
        Assert.True(Brush(words[1]).GradientStops[1].Offset >= 0.999);
    });

    [Theory]
    [InlineData(0, 6, 1.2, false)] // a drawn-out opening pickup stays plain
    [InlineData(0, 6, 2.2, true)]  // ...unless it is held very long
    [InlineData(3, 6, 1.2, false)] // mid-line words still need a long hold
    [InlineData(5, 6, 1.0, true)]  // the held word that ends a phrase swells
    [InlineData(5, 6, 0.6, false)]
    public void GetEmphasisStrength_NeedsALongerHoldEarlierInTheLine(int index, int count, double seconds, bool emphasized)
    {
        var strength = KaraokeTextController.GetEmphasisStrength(new LyricWord(10, 10 + seconds, "word"), index, count);

        Assert.Equal(emphasized, strength > 0);
    }

    [Fact]
    public void GetEmphasisStrength_GrowsWithTheLengthOfTheHold()
    {
        var shorter = KaraokeTextController.GetEmphasisStrength(new LyricWord(0, 1.0, "word"), 5, 6);
        var longer = KaraokeTextController.GetEmphasisStrength(new LyricWord(0, 2.0, "word"), 5, 6);

        Assert.InRange(shorter, 0.4, 0.6);
        Assert.True(longer > shorter);
    }

    [Fact]
    public void SetLine_DoesNotEmphasizeALineTimedAsOnePhrase() => OnSta(() =>
    {
        var text = new TextBlock();
        var controller = new KaraokeTextController(text, emphasizeSustainedWords: true);
        var line = new LyricsLine(1, 5, "Keep singing", Words: [new LyricWord(1, 5, "Keep singing")]);

        controller.SetLine(line.Text, line, enabled: true);

        Assert.Empty(text.Inlines.OfType<InlineUIContainer>());
        Assert.Single(text.Inlines.OfType<Run>());
    });

    private static RenderTargetBitmap Render(TextBlock text, double width)
    {
        text.Measure(new Size(width, double.PositiveInfinity));
        text.Arrange(new Rect(0, 0, width, text.DesiredSize.Height));
        text.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width),
            Math.Max(1, (int)Math.Ceiling(text.DesiredSize.Height)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(text);
        return bitmap;
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15)))
        {
            throw new TimeoutException("WPF rendering test did not complete on its STA thread.");
        }
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
