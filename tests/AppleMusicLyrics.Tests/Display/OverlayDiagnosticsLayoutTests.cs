using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using AppleMusicLyrics.App.Presentation;
using AppleMusicLyrics.Core.Configuration;
using Xunit;

namespace AppleMusicLyrics.Tests.Display;

public sealed class OverlayDiagnosticsLayoutTests
{
    [Theory]
    [InlineData(false, true, 0.85)]
    [InlineData(false, false, 0.05)]
    [InlineData(true, true, 0.05)]
    public void VisibleNormalDiagnosticsSuppressHoverDimWithoutChangingSavedOptions(bool pureMode, bool debug, double expected)
    {
        var settings = new AppSettings
        {
            PureMode = pureMode,
            ShowDebugPanel = debug,
            HoverFadeEnabled = true,
            HoverFadeMinOpacity = 0.05,
            OverlayOpacity = 0.85,
            AutoHideNoLyrics = false,
            FadeWhenPaused = false,
        };
        Assert.Equal(expected, OverlayDiagnosticsLayout.ResolveOpacity(settings, true, false, true, false));
        Assert.True(settings.HoverFadeEnabled);
        Assert.Equal(0.05, settings.HoverFadeMinOpacity);
        Assert.Equal(pureMode, settings.PureMode);
    }

    [Theory]
    [InlineData(280)]
    [InlineData(320)]
    [InlineData(720)]
    public void ActualMainXaml_CompactSummaryUsesOneLineWithoutADiagnosticPanel(double width) => OnSta(() =>
    {
        var view = new LayoutView();
        view.Summary.Text = "QQ Music · word timing + translations · lookup complete";
        view.Layout(width, 240);

        Assert.Empty(VisualDescendants(view.Root).OfType<ScrollViewer>());
        Assert.Empty(VisualDescendants(view.Footer).OfType<Border>());
        Assert.Null(view.Footer.Background);
        Assert.Equal(Visibility.Collapsed, view.Player.Visibility);
        Assert.Equal(Visibility.Collapsed, view.Timing.Visibility);
        Assert.Null(view.Summary.ToolTip);
        Assert.Equal(TextWrapping.NoWrap, view.Summary.TextWrapping);
        Assert.Equal(TextTrimming.CharacterEllipsis, view.Summary.TextTrimming);
        Assert.InRange(OverlayDiagnosticsLayout.MeasureHeight(view.Footer, width - 52), 16, 21);
        var footer = Bounds(view.Footer, view.Root);
        var lyric = Bounds(view.Current, view.Root);
        var status = Bounds(view.Status, view.Root);
        Assert.InRange(footer.Height, 15, 20);
        Assert.True(footer.Right <= width - 25);
        Assert.True(footer.Bottom <= 240 - 25);
        Assert.True(lyric.Bottom <= footer.Top + 1);
        Assert.True(status.Right <= width - 25);
        Assert.True(view.Status.ActualWidth > 50);
        Assert.True(view.Subtitle.ActualWidth > 20);
        Assert.True(view.Summary.ActualWidth <= width - 52 + 1);

        var bitmap = new RenderTargetBitmap((int)width, 240, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view.Root);
        Assert.True(CountBrightPixels(bitmap, footer) > 20, "The compact status line should render readable text.");
    });

    [Fact]
    public void ActualMainXaml_LongDetailsCannotGrowTheFooterOrDisplaceTheLyric() => OnSta(() =>
    {
        var view = new LayoutView();
        view.Summary.Text = "QQ Music · original lyrics";
        view.Layout(320, 240);
        var shortHeight = OverlayDiagnosticsLayout.MeasureHeight(view.Footer, 268);
        var lyricBounds = Bounds(view.Current, view.Root);
        var lyricText = view.Current.Text;

        view.Summary.Text = LongDiagnostic();
        view.Player.Text = LongDiagnostic();
        view.Timing.Text = LongDiagnostic();
        view.Layout(320, 240);
        var longHeight = OverlayDiagnosticsLayout.MeasureHeight(view.Footer, 268);

        Assert.Equal(shortHeight, longHeight, 3);
        Assert.InRange(longHeight, 16, 21);
        Assert.Equal(lyricText, view.Current.Text);
        Assert.Equal(lyricBounds, Bounds(view.Current, view.Root));
        Assert.Empty(VisualDescendants(view.Root).OfType<ScrollViewer>());
        Assert.Null(view.Summary.ToolTip);
        Assert.Equal(Visibility.Collapsed, view.Player.Visibility);
        Assert.Equal(Visibility.Collapsed, view.Timing.Visibility);
    });

    [Fact]
    public void ActualMainXaml_HidingAndRestoringTheSummaryAddsOnlyOneLine() => OnSta(() =>
    {
        var view = new LayoutView();
        view.Summary.Text = "QQ Music · original lyrics";
        view.Footer.Visibility = Visibility.Collapsed;
        view.Layout(320, 180);
        Assert.Equal(0, OverlayDiagnosticsLayout.MeasureHeight(view.Footer, 268));

        view.Footer.Visibility = Visibility.Visible;
        view.Layout(320, 180);
        var footer = OverlayDiagnosticsLayout.MeasureHeight(view.Footer, 268);
        Assert.InRange(footer, 16, 21);
        Assert.InRange(OverlayDiagnosticsLayout.GetWindowHeight(80, 40, footer, 80, 540), 136, 141);
        Assert.InRange(OverlayDiagnosticsLayout.GetWindowHeight(80, 10_000, footer, 80, 540), 80, 540);
    });

    private static string LongDiagnostic() => "QQ Music | word timing 42/47 | translations 47/47\n"
        + string.Join("\n", Enumerable.Range(1, 25).Select(index =>
            $"Source {index}: synthetic details belong in the separate diagnostics window."))
        + "\nC:\\SyntheticCache\\" + new string('L', 320) + ".json\nFinal diagnostic detail";

    private static Rect Bounds(FrameworkElement element, Visual root)
        => element.TransformToAncestor(root).TransformBounds(new Rect(new Point(), element.RenderSize));

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in VisualDescendants(child)) yield return descendant;
        }
    }

    private static int CountBrightPixels(BitmapSource bitmap, Rect area)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        var count = 0;
        for (var y = Math.Max(0, (int)area.Top); y < Math.Min(bitmap.PixelHeight, (int)Math.Ceiling(area.Bottom)); y++)
            for (var x = Math.Max(0, (int)area.Left); x < Math.Min(bitmap.PixelWidth, (int)Math.Ceiling(area.Right)); x++)
            {
                var offset = (y * bitmap.PixelWidth + x) * 4;
                if (pixels[offset] > 100 && pixels[offset + 1] > 100 && pixels[offset + 2] > 100) count++;
            }
        return count;
    }

    private sealed class LayoutView
    {
        public Grid Root { get; }
        public Grid Footer { get; }
        public TextBlock Summary { get; }
        public TextBlock Current { get; }
        public TextBlock Status { get; }
        public TextBlock Subtitle { get; }
        public TextBlock Player { get; }
        public TextBlock Timing { get; }

        public LayoutView()
        {
            var source = XDocument.Load(FindMainXaml());
            XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var window = source.Root!;
            var grid = new XElement(window.Element(presentation + "Grid")!);
            foreach (var attribute in window.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
                grid.SetAttributeValue(attribute.Name, attribute.Value);
            foreach (var element in grid.DescendantsAndSelf())
                foreach (var attribute in element.Attributes().ToArray())
                {
                    if (attribute.Name.LocalName is "PreviewMouseLeftButtonDown" or "MouseLeftButtonDown") attribute.Remove();
                    else if (attribute.Value.StartsWith("{loc:Loc ", StringComparison.Ordinal)) attribute.Value = string.Empty;
                }
            // Load only the real visual tree, without MainWindow's player/network composition.
            Root = (Grid)XamlReader.Parse(grid.ToString());
            Footer = Find<Grid>("FooterPanel");
            Summary = Find<TextBlock>("PathText");
            Current = Find<TextBlock>("CurrentLyricText");
            Status = Find<TextBlock>("StatusText");
            Subtitle = Find<TextBlock>("SubtitleText");
            Player = Find<TextBlock>("PlayerText");
            Timing = Find<TextBlock>("TimingText");
            Find<Grid>("ResizeLayer").Visibility = Visibility.Collapsed;
            Find<TextBlock>("PreviousLyricText").Visibility = Visibility.Collapsed;
            Find<TextBlock>("NextLyricText").Visibility = Visibility.Collapsed;
            Find<RowDefinition>("PreviousLyricRow").Height = new GridLength(0);
            Find<RowDefinition>("NextLyricRow").Height = new GridLength(0);
            Current.Text = "Current lyric";
            Current.FontSize = 28;
            Current.Effect = null;
            Subtitle.Text = "A long synthetic artist and album title that must leave room for the status";
            Status.Text = "QQ Music | word timing + translations";
            Player.Text = "Paused: Synthetic Artist - Synthetic Song";
            Timing.Text = "Raw 00:42.00 | Clock 00:42.00 | Render 60.0 fps | Poll 5.0 Hz";
        }

        public void Layout(double width, double height)
        {
            Status.MaxWidth = OverlayDiagnosticsLayout.GetStatusMaxWidth(width - 52);
            Current.MaxWidth = Math.Max(200, width - 68);
            Root.Measure(new Size(width, height));
            Root.Arrange(new Rect(0, 0, width, height));
            Root.UpdateLayout();
            Root.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Root.UpdateLayout();
        }

        private T Find<T>(string name) where T : class => Assert.IsType<T>(Root.FindName(name));

        private static string FindMainXaml()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                var path = System.IO.Path.Combine(directory.FullName, "src", "AppleMusicLyrics.App", "MainWindow.xaml");
                if (File.Exists(path)) return path;
            }
            throw new FileNotFoundException("The actual MainWindow.xaml source was not found.");
        }
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
        if (!thread.Join(TimeSpan.FromSeconds(20))) throw new TimeoutException("Compact status layout did not finish on its STA thread.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
