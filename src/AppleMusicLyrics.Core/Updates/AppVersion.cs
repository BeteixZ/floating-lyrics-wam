using System.Globalization;

namespace AppleMusicLyrics.Core.Updates;

/// <summary>
/// A semantic version as used by release tags (<c>v0.8.0</c>, <c>v0.9.0-beta.1</c>) and by the
/// assembly's informational version (<c>0.8.0+commit</c>). Build metadata after <c>+</c> is
/// ignored, a pre-release sorts before its release, and pre-release identifiers compare as in
/// SemVer 2.0 (numeric identifiers numerically, others ordinally).
/// </summary>
public sealed record AppVersion(int Major, int Minor, int Patch, string? PreRelease = null) : IComparable<AppVersion>
{
    public bool IsPreRelease => !string.IsNullOrEmpty(PreRelease);

    public static bool TryParse(string? text, out AppVersion version)
    {
        version = new AppVersion(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        var plus = value.IndexOf('+');
        if (plus >= 0) value = value[..plus];
        string? preRelease = null;
        var dash = value.IndexOf('-');
        if (dash >= 0)
        {
            preRelease = value[(dash + 1)..];
            value = value[..dash];
            if (preRelease.Length == 0) return false;
        }

        var parts = value.Split('.');
        if (parts.Length is < 2 or > 4) return false;
        var numbers = new int[3];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return false;
            // A fourth component (an assembly revision such as 0.8.0.0) carries no release meaning.
            if (index < 3) numbers[index] = number;
        }

        version = new AppVersion(numbers[0], numbers[1], numbers[2], preRelease);
        return true;
    }

    public int CompareTo(AppVersion? other)
    {
        if (other is null) return 1;
        var core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (core != 0) return core;
        if (!IsPreRelease) return other.IsPreRelease ? 1 : 0;
        if (!other.IsPreRelease) return -1;

        var mine = PreRelease!.Split('.');
        var theirs = other.PreRelease!.Split('.');
        for (var index = 0; index < Math.Min(mine.Length, theirs.Length); index++)
        {
            var mineNumeric = int.TryParse(mine[index], NumberStyles.None, CultureInfo.InvariantCulture, out var a);
            var theirsNumeric = int.TryParse(theirs[index], NumberStyles.None, CultureInfo.InvariantCulture, out var b);
            var result = (mineNumeric, theirsNumeric) switch
            {
                (true, true) => a.CompareTo(b),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(mine[index], theirs[index]),
            };
            if (result != 0) return Math.Sign(result);
        }

        return mine.Length.CompareTo(theirs.Length);
    }

    public static bool operator >(AppVersion left, AppVersion right) => left.CompareTo(right) > 0;

    public static bool operator <(AppVersion left, AppVersion right) => left.CompareTo(right) < 0;

    public override string ToString() => IsPreRelease ? $"{Major}.{Minor}.{Patch}-{PreRelease}" : $"{Major}.{Minor}.{Patch}";

    /// <summary>The tag form used by releases, for example <c>v0.8.0</c>.</summary>
    public string Tag => "v" + this;
}
