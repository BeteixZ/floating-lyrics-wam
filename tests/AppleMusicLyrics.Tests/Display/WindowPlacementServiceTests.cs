using AppleMusicLyrics.Core.Display;
using Xunit;

namespace AppleMusicLyrics.Tests.Display;

public sealed class WindowPlacementServiceTests
{
    private static readonly MonitorDescriptor Primary = new(
        "DISPLAY1",
        new PixelRect(0, 0, 1920, 1080),
        new PixelRect(0, 0, 1920, 1040),
        96,
        96,
        true);

    private static readonly MonitorDescriptor Secondary = new(
        "DISPLAY2",
        new PixelRect(-2560, 0, 0, 1440),
        new PixelRect(-2560, 0, 0, 1400),
        144,
        144,
        false);

    [Fact]
    public void Resolve_RestoresRelativePositionOnPreferredMixedDpiMonitor()
    {
        var result = WindowPlacementService.Resolve(
            [Primary, Secondary],
            "DISPLAY2",
            0.8,
            0.75,
            800,
            220,
            new PixelRect(100, 100, 900, 320));

        Assert.Equal("DISPLAY2", result.Monitor.DeviceName);
        Assert.Equal(1200, result.WindowRectPx.Width);
        Assert.Equal(330, result.WindowRectPx.Height);
        Assert.InRange(result.RelativeCenterX, 0.76, 0.77);
        Assert.InRange(result.RelativeCenterY, 0.74, 0.76);
    }

    [Fact]
    public void Resolve_FallsBackToPrimaryButKeepsRelativeAnchorWhenMonitorIsMissing()
    {
        var result = WindowPlacementService.Resolve(
            [Primary],
            "DISCONNECTED",
            0.9,
            0.8,
            600,
            200,
            new PixelRect(-2000, 100, -1400, 300));

        Assert.Equal("DISPLAY1", result.Monitor.DeviceName);
        Assert.InRange(result.RelativeCenterX, 0.84, 0.91);
        Assert.InRange(result.RelativeCenterY, 0.79, 0.81);
    }

    [Fact]
    public void Resolve_MigratesLegacyNegativeCoordinatesToIntersectingMonitor()
    {
        var result = WindowPlacementService.Resolve(
            [Primary, Secondary],
            null,
            -1,
            -1,
            600,
            200,
            new PixelRect(-2200, 200, -1600, 400));

        Assert.Equal("DISPLAY2", result.Monitor.DeviceName);
    }

    [Fact]
    public void Resolve_ClampsWindowFullyInsideWorkArea()
    {
        var result = WindowPlacementService.Resolve(
            [Primary],
            "DISPLAY1",
            1.0,
            1.0,
            800,
            300,
            new PixelRect(0, 0, 800, 300));

        Assert.Equal(Primary.WorkAreaPx.Right, result.WindowRectPx.Right);
        Assert.Equal(Primary.WorkAreaPx.Bottom, result.WindowRectPx.Bottom);
    }

    [Fact]
    public void Resolve_ClampsOversizedWindowToWorkArea()
    {
        var result = WindowPlacementService.Resolve(
            [Primary],
            "DISPLAY1",
            0.5,
            0.5,
            4000,
            3000,
            default);

        Assert.Equal(Primary.WorkAreaPx, result.WindowRectPx);
    }

    [Fact]
    public void Capture_PreservesCenterAsWorkAreaRatio()
    {
        var captured = WindowPlacementService.Capture(
            Primary,
            new PixelRect(1440, 780, 1920, 1040));

        Assert.Equal("DISPLAY1", captured.MonitorId);
        Assert.InRange(captured.RelativeCenterX, 0.87, 0.88);
        Assert.InRange(captured.RelativeCenterY, 0.87, 0.88);
    }

    [Fact]
    public void ResolveDpiSuggestedRect_PreservesPhysicalBoundsWithoutDoubleScaling()
    {
        var suggestedRect = new PixelRect(-1800, 120, -600, 420);

        var result = WindowPlacementService.ResolveDpiSuggestedRect(Secondary, suggestedRect);

        Assert.Equal(suggestedRect, result.WindowRectPx);
        Assert.Equal(800, result.WidthDip);
        Assert.Equal(200, result.HeightDip);
    }

    [Fact]
    public void ResolveDpiSuggestedRect_RejectsInvalidBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WindowPlacementService.ResolveDpiSuggestedRect(
                Primary,
                new PixelRect(100, 100, 100, 200)));
    }

    [Fact]
    public void Resolve_ReconcilesChangedWorkAreaAndKeepsWindowVisible()
    {
        var resizedPrimary = Primary with
        {
            BoundsPx = new PixelRect(0, 0, 1600, 900),
            WorkAreaPx = new PixelRect(0, 0, 1600, 860),
        };

        var result = WindowPlacementService.Resolve(
            [resizedPrimary],
            "DISPLAY1",
            0.9,
            0.9,
            900,
            300,
            new PixelRect(1200, 700, 2100, 1000));

        Assert.Equal(resizedPrimary.WorkAreaPx.Right, result.WindowRectPx.Right);
        Assert.Equal(resizedPrimary.WorkAreaPx.Bottom, result.WindowRectPx.Bottom);
    }
}
