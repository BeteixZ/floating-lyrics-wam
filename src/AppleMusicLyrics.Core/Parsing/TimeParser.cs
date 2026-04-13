using System.Globalization;

namespace AppleMusicLyrics.Core.Parsing;

public static class TimeParser
{
    public static double ParseSeconds(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException("Time value cannot be empty.");
        }

        var normalized = value.Trim().TrimEnd('s', 'S');
        var parts = normalized.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Length > 3)
        {
            throw new FormatException($"Unsupported time value: {value}");
        }

        double total = 0;
        double multiplier = 1;
        for (var index = parts.Length - 1; index >= 0; index--)
        {
            var number = double.Parse(parts[index], CultureInfo.InvariantCulture);
            total += number * multiplier;
            multiplier *= 60;
        }

        return total;
    }

    public static string FormatTimestamp(double seconds)
    {
        var safe = Math.Max(0, seconds);
        var minutes = (int)(safe / 60);
        var remainder = safe - (minutes * 60);
        return $"{minutes:00}:{remainder:00.000}";
    }
}
