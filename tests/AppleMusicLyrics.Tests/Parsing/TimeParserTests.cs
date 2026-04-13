using AppleMusicLyrics.Core.Parsing;
using Xunit;

namespace AppleMusicLyrics.Tests.Parsing;

public sealed class TimeParserTests
{
    [Theory]
    [InlineData("29.831", 29.831)]
    [InlineData("1:11.036", 71.036)]
    [InlineData("2:58.567", 178.567)]
    public void ParseSeconds_ParsesExpectedValues(string input, double expected)
    {
        var actual = TimeParser.ParseSeconds(input);
        Assert.Equal(expected, actual, precision: 3);
    }
}
