using AppleMusicLyrics.Core.Configuration;
using AppleMusicLyrics.Core.Display;
using Xunit;

namespace AppleMusicLyrics.Tests.Display;

public sealed class OverlayOpacityResolverTests
{
    [Fact]
    public void Resolve_PureModeDragOverridesHoverDimOpacity()
    {
        var settings = VisibleSettings();
        settings.PureMode = true;
        settings.PureModeDragOpacity = 0.8;
        settings.HoverFadeMinOpacity = 0.05;

        var opacity = OverlayOpacityResolver.Resolve(
            settings,
            hasLyrics: true,
            isPlaying: true,
            isHovering: true,
            isWindowDragging: true);

        Assert.Equal(0.8, opacity);
    }

    [Fact]
    public void Resolve_NormalModeIgnoresDragState()
    {
        var settings = VisibleSettings();
        settings.PureMode = false;
        settings.HoverFadeMinOpacity = 0.15;

        var opacity = OverlayOpacityResolver.Resolve(
            settings,
            hasLyrics: true,
            isPlaying: true,
            isHovering: true,
            isWindowDragging: true);

        Assert.Equal(0.15, opacity);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Resolve_AutomaticFadeRemainsHigherPriorityThanDrag(bool hasLyrics, bool isPlaying)
    {
        var settings = VisibleSettings();
        settings.PureMode = true;
        settings.AutoHideNoLyrics = true;
        settings.FadeWhenPaused = true;

        var opacity = OverlayOpacityResolver.Resolve(
            settings,
            hasLyrics,
            isPlaying,
            isHovering: true,
            isWindowDragging: true);

        Assert.Equal(0.0, opacity);
    }

    [Theory]
    [InlineData(0.05, 0.2)]
    [InlineData(0.8, 0.8)]
    [InlineData(1.5, 1.0)]
    public void Resolve_ClampsPureModeDragOpacity(double configured, double expected)
    {
        var settings = VisibleSettings();
        settings.PureMode = true;
        settings.PureModeDragOpacity = configured;

        var opacity = OverlayOpacityResolver.Resolve(
            settings,
            hasLyrics: true,
            isPlaying: true,
            isHovering: false,
            isWindowDragging: true);

        Assert.Equal(expected, opacity);
    }

    [Fact]
    public void Resolve_UsesConfiguredWindowOpacityWithoutHoverOrDrag()
    {
        var settings = VisibleSettings();
        settings.OverlayOpacity = 0.72;

        var opacity = OverlayOpacityResolver.Resolve(
            settings,
            hasLyrics: true,
            isPlaying: true,
            isHovering: false,
            isWindowDragging: false);

        Assert.Equal(0.72, opacity);
    }

    private static AppSettings VisibleSettings()
    {
        return new AppSettings
        {
            AutoHideNoLyrics = false,
            FadeWhenPaused = false,
            HoverFadeEnabled = true,
            OverlayOpacity = 1.0,
        };
    }
}
