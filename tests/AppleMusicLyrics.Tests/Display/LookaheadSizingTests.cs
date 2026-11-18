using AppleMusicLyrics.Core.Display;
using Xunit;

namespace AppleMusicLyrics.Tests.Display;

public sealed class LookaheadSizingTests
{
    [Fact]
    public void Growth_IsAlwaysImmediate()
    {
        Assert.Equal((600d, 220d), LookaheadSizing.Resolve((600, 220), (400, 120), (300, 100)));
    }

    [Fact]
    public void Shrink_IsImmediateWhenTheNextLinesStaySmall()
    {
        Assert.Equal((400d, 120d), LookaheadSizing.Resolve((400, 120), (600, 220), (380, 110)));
        Assert.Equal((400d, 120d), LookaheadSizing.Resolve((400, 120), (600, 220), null));
    }

    [Fact]
    public void Shrink_IsSkippedWhenAnUpcomingLineNeedsTheCurrentSizeAgain()
    {
        // A duet part ends, but both singers return two lines later.
        Assert.Equal((600d, 220d), LookaheadSizing.Resolve((400, 120), (600, 220), (650, 240)));
    }

    [Fact]
    public void Shrink_StopsAtWhatTheUpcomingLinesNeed()
    {
        Assert.Equal((500d, 120d), LookaheadSizing.Resolve((400, 120), (600, 220), (500, 110)));
    }
}
