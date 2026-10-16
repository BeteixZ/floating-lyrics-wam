using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using AppleMusicLyrics.App;
using AppleMusicLyrics.App.Localization;
using Xunit;

namespace AppleMusicLyrics.Tests.Display;

[Collection("Localization")]
public sealed class DiagnosticsWindowTests
{
    [Theory]
    [InlineData(760, 560)]
    [InlineData(540, 360)]
    public void StandaloneWindow_LaysOutReadableContentWithoutStartingAnApplication(double width, double height) => OnSta(() =>
    {
        var application = System.Windows.Application.Current;
        var window = new DiagnosticsWindow();
        try
        {
            window.UpdateDiagnostics("Synthetic track · 合成曲目", "QQ Music · translation available", "Synthetic diagnostic details\n第二行诊断");
            Layout(window, width, height);
            var details = Find<TextBox>(window, "DetailsTextBox");
            Assert.Same(application, System.Windows.Application.Current);
            Assert.False(window.IsVisible);
            Assert.False(window.Topmost);
            Assert.False(window.AllowsTransparency);
            Assert.Equal(1, window.Opacity);
            Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
            Assert.Equal(WindowStyle.SingleBorderWindow, window.WindowStyle);
            Assert.Equal(540, window.MinWidth);
            Assert.Equal(360, window.MinHeight);
            Assert.Null(window.Owner);
            Assert.Equal("Synthetic track · 合成曲目", Find<TextBlock>(window, "TrackTitleText").Text);
            Assert.Equal("QQ Music · translation available", Find<TextBlock>(window, "SummaryText").Text);
            Assert.Equal("Synthetic diagnostic details\n第二行诊断", details.Text);
            Assert.True(details.IsReadOnly);
            Assert.Equal(ScrollBarVisibility.Auto, details.VerticalScrollBarVisibility);
            Assert.Equal(ScrollBarVisibility.Disabled, details.HorizontalScrollBarVisibility);
            Assert.True(details.ActualWidth > 400);
            Assert.True(details.ActualHeight > 100);
            details.Select(0, 9);
            Assert.Equal("Synthetic", details.SelectedText);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void UpdateDiagnostics_IdenticalDetailsDoNotResetSelectionScrollOrText() => OnSta(() =>
    {
        var window = new DiagnosticsWindow();
        try
        {
            var text = LongDetails();
            window.UpdateDiagnostics("Track", "Summary", text);
            Layout(window);
            var box = Find<TextBox>(window, "DetailsTextBox");
            box.Select(text.IndexOf("row 080", StringComparison.Ordinal), 7);
            box.ScrollToLine(80);
            Layout(window);
            var offset = box.VerticalOffset;
            Assert.True(offset > 0);
            var start = box.SelectionStart;
            var changed = 0;
            box.TextChanged += (_, _) => changed++;

            window.UpdateDiagnostics("Track", "Changed summary", text);
            Layout(window);

            Assert.Equal(0, changed);
            Assert.Equal(start, box.SelectionStart);
            Assert.Equal("row 080", box.SelectedText);
            Assert.Equal(offset, box.VerticalOffset, 3);
            Assert.Equal("Changed summary", Find<TextBlock>(window, "SummaryText").Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void UpdateDiagnostics_SameTrackChangedDetailsPreserveScrollAndSelection() => OnSta(() =>
    {
        var window = new DiagnosticsWindow();
        try
        {
            var text = LongDetails();
            window.UpdateDiagnostics("Track", "Summary", text);
            Layout(window);
            var box = Find<TextBox>(window, "DetailsTextBox");
            box.Select(text.IndexOf("row 080", StringComparison.Ordinal), 7);
            box.ScrollToLine(80);
            Layout(window);
            var offset = box.VerticalOffset;
            var start = box.SelectionStart;
            Assert.True(offset > 0);

            window.UpdateDiagnostics("Track", "New summary", text + "\nAdditional synthetic detail");
            Layout(window);

            Assert.Equal(start, box.SelectionStart);
            Assert.Equal("row 080", box.SelectedText);
            Assert.Equal(offset, box.VerticalOffset, 3);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void UpdateDiagnostics_TrackChangeReturnsToTopEvenWhenDetailsAreUnchanged() => OnSta(() =>
    {
        var window = new DiagnosticsWindow();
        try
        {
            var text = LongDetails();
            window.UpdateDiagnostics("Track A", "Summary", text);
            Layout(window);
            var box = Find<TextBox>(window, "DetailsTextBox");
            box.Select(100, 8);
            box.ScrollToLine(80);
            Layout(window);
            Assert.True(box.VerticalOffset > 0);

            window.UpdateDiagnostics("Track B", "Summary", text);
            Layout(window);

            Assert.Equal(0, box.VerticalOffset);
            Assert.Equal(0, box.SelectionLength);
            Assert.Equal("Track B", Find<TextBlock>(window, "TrackTitleText").Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void LanguageChange_UpdatesExistingBindingsWithoutChangingDiagnosticData() => OnSta(() =>
    {
        var localization = LocalizationManager.Instance;
        var originalLanguage = localization.Preference;
        localization.SetLanguage("en-US");
        var window = new DiagnosticsWindow();
        try
        {
            window.UpdateDiagnostics("Synthetic track", "Synthetic summary", "Synthetic details");
            Layout(window);
            var title = window.Title;
            Assert.NotNull(BindingOperations.GetBinding(window, Window.TitleProperty));

            localization.SetLanguage("zh-CN");
            Layout(window);

            Assert.Equal(localization.Text("Diagnostics.Title"), window.Title);
            Assert.NotEqual(title, window.Title);
            Assert.Equal(localization.Text("Diagnostics.SummaryLabel"), Find<TextBlock>(window, "SummaryLabelText").Text);
            Assert.Equal(localization.Text("Diagnostics.DetailsLabel"), Find<TextBlock>(window, "DetailsLabelText").Text);
            Assert.Equal(localization.Text("Diagnostics.Close"), Find<Button>(window, "CloseButton").Content);
            Assert.Equal("Synthetic track", Find<TextBlock>(window, "TrackTitleText").Text);
            Assert.Equal("Synthetic summary", Find<TextBlock>(window, "SummaryText").Text);
            Assert.Equal("Synthetic details", Find<TextBox>(window, "DetailsTextBox").Text);
        }
        finally { window.Close(); localization.SetLanguage(originalLanguage); }
    });

    [Fact]
    public void CloseButton_ClosesTheWindowAndANewInstanceCanBeCreated() => OnSta(() =>
    {
        var window = new DiagnosticsWindow();
        var closed = false;
        window.Closed += (_, _) => closed = true;
        Find<Button>(window, "CloseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(closed);
        var reopened = new DiagnosticsWindow();
        try
        {
            reopened.UpdateDiagnostics("Reopened", "Current summary", "Current details");
            Layout(reopened);
            Assert.Equal("Current details", Find<TextBox>(reopened, "DetailsTextBox").Text);
        }
        finally { reopened.Close(); }
    });

    private static string LongDetails() => string.Join('\n', Enumerable.Range(0, 180)
        .Select(index => $"Synthetic row {index:000}: independent diagnostic details."));

    private static T Find<T>(Window window, string name) where T : FrameworkElement => Assert.IsType<T>(window.FindName(name));

    private static void Layout(Window window, double width = 760, double height = 560)
    {
        var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15))) throw new TimeoutException("Diagnostics window test did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
