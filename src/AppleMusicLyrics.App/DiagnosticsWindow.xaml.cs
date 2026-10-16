using System.Windows;

namespace AppleMusicLyrics.App;

public partial class DiagnosticsWindow : Window
{
    private string _trackTitle = "—";
    private string _summary = "—";
    private string _details = string.Empty;

    public DiagnosticsWindow()
    {
        InitializeComponent();
    }

    public void UpdateDiagnostics(string trackTitle, string summary, string details)
    {
        VerifyAccess();
        trackTitle = string.IsNullOrWhiteSpace(trackTitle) ? "—" : trackTitle;
        summary = string.IsNullOrWhiteSpace(summary) ? "—" : summary;
        details ??= string.Empty;
        var trackChanged = !string.Equals(_trackTitle, trackTitle, StringComparison.Ordinal);
        if (trackChanged)
        {
            _trackTitle = trackTitle;
            TrackTitleText.Text = trackTitle;
        }
        if (!string.Equals(_summary, summary, StringComparison.Ordinal))
        {
            _summary = summary;
            SummaryText.Text = summary;
        }
        if (!string.Equals(_details, details, StringComparison.Ordinal))
        {
            var selectionStart = DetailsTextBox.SelectionStart;
            var selectionLength = DetailsTextBox.SelectionLength;
            var verticalOffset = DetailsTextBox.VerticalOffset;
            var horizontalOffset = DetailsTextBox.HorizontalOffset;
            _details = details;
            DetailsTextBox.Text = details;
            if (!trackChanged)
            {
                // Recalculate the new document extent before restoring the user's viewport.
                DetailsTextBox.UpdateLayout();
                var restoredStart = Math.Min(selectionStart, details.Length);
                DetailsTextBox.Select(restoredStart, Math.Min(selectionLength, details.Length - restoredStart));
                DetailsTextBox.ScrollToHorizontalOffset(horizontalOffset);
                DetailsTextBox.ScrollToVerticalOffset(verticalOffset);
            }
        }
        if (trackChanged)
        {
            DetailsTextBox.Select(0, 0);
            DetailsTextBox.ScrollToHome();
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();
}
