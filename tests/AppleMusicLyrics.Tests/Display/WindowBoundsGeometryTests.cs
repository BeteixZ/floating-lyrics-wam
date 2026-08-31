using AppleMusicLyrics.Core.Display;
using Xunit;

namespace AppleMusicLyrics.Tests.Display;

public sealed class WindowBoundsGeometryTests
{
    private static readonly MonitorDescriptor MixedDpiMonitor = new(
        "DISPLAY2",
        new PixelRect(-2560, -200, 0, 1440),
        new PixelRect(-2560, -160, 0, 1400),
        144,
        144,
        false);

    [Fact]
    public void ResizeAroundCenter_ConvertsDipOnceAndPreservesCenter()
    {
        var current = new PixelRect(-1900, 200, -700, 500);

        var target = WindowBoundsGeometry.ResizeAroundCenter(
            current,
            MixedDpiMonitor,
            targetWidthDip: 400,
            targetHeightDip: 100);

        Assert.Equal(600, target.Width);
        Assert.Equal(150, target.Height);
        Assert.InRange(Math.Abs(target.CenterX - current.CenterX), 0, 0.5);
        Assert.InRange(Math.Abs(target.CenterY - current.CenterY), 0, 0.5);
    }

    [Fact]
    public void ResizeAroundCenter_ClampsToNegativeCoordinateWorkArea()
    {
        var current = new PixelRect(-2660, -260, -2460, -60);

        var target = WindowBoundsGeometry.ResizeAroundCenter(
            current,
            MixedDpiMonitor,
            targetWidthDip: 800,
            targetHeightDip: 300);

        Assert.Equal(MixedDpiMonitor.WorkAreaPx.Left, target.Left);
        Assert.Equal(MixedDpiMonitor.WorkAreaPx.Top, target.Top);
        Assert.True(target.Right <= MixedDpiMonitor.WorkAreaPx.Right);
        Assert.True(target.Bottom <= MixedDpiMonitor.WorkAreaPx.Bottom);
    }

    [Fact]
    public void Interpolate_GrowAndShrinkKeepFixedCenter()
    {
        var start = new PixelRect(600, 400, 1400, 600);
        var target = new PixelRect(800, 450, 1200, 550);

        var halfway = WindowBoundsGeometry.Interpolate(start, target, 0.5);

        Assert.Equal(600, halfway.Width);
        Assert.Equal(150, halfway.Height);
        Assert.InRange(Math.Abs(halfway.CenterX - start.CenterX), 0, 0.5);
        Assert.InRange(Math.Abs(halfway.CenterY - start.CenterY), 0, 0.5);
        Assert.Equal(start, WindowBoundsGeometry.Interpolate(start, target, -1));
        Assert.Equal(target, WindowBoundsGeometry.Interpolate(start, target, 2));
    }

    [Fact]
    public void Interpolate_RestartingFromCurrentFrameHasNoFirstFrameJump()
    {
        var original = new PixelRect(400, 300, 1200, 500);
        var interruptedTarget = new PixelRect(600, 350, 1000, 450);
        var currentFrame = WindowBoundsGeometry.Interpolate(original, interruptedTarget, 0.4);
        var newTarget = new PixelRect(500, 325, 1100, 475);

        Assert.Equal(
            currentFrame,
            WindowBoundsGeometry.Interpolate(currentFrame, newTarget, 0));
    }
}
