using System.Globalization;
using System.Reflection;
using AppleMusicLyrics.Core.Abstractions;
using AppleMusicLyrics.Core.Configuration;

namespace AppleMusicLyrics.Infrastructure.Windows.Configuration;

public sealed class IniSettingsStore : ISettingsStore
{
    private const string SectionName = "apple_music_lyrics";
    private const int ReadAttempts = 4;
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(75);

    // Hand-edited or corrupted values outside these bounds keep their defaults. They are wider
    // than the Settings sliders so values saved by earlier versions are kept.
    private static readonly Dictionary<string, (double Min, double Max)> Bounds = new(StringComparer.Ordinal)
    {
        [nameof(AppSettings.PlayerPollInterval)] = (0.01, 5),
        [nameof(AppSettings.LyricsOffsetSeconds)] = (-60, 60),
        [nameof(AppSettings.CatalogLookupTimeoutSeconds)] = (0.1, 120),
        [nameof(AppSettings.ExternalLyricsTimeoutSeconds)] = (0.1, 120),
        [nameof(AppSettings.ExternalLyricsCacheMaxMegabytes)] = (0, 100_000),
        [nameof(AppSettings.WindowWidth)] = (1, 100_000),
        [nameof(AppSettings.WindowHeight)] = (1, 100_000),
        [nameof(AppSettings.MinWindowWidth)] = (1, 100_000),
        [nameof(AppSettings.MinWindowHeight)] = (1, 100_000),
        // The overlay clamps context lines to at least 12 with this value as the maximum.
        [nameof(AppSettings.MaxCurrentFontSize)] = (12, 400),
        [nameof(AppSettings.ContextFontSize)] = (12, 400),
        [nameof(AppSettings.TranslationFontSize)] = (4, 400),
        [nameof(AppSettings.GlowOpacity)] = (0, 1),
        [nameof(AppSettings.OverlayOpacity)] = (0, 1),
        [nameof(AppSettings.PureModeDragOpacity)] = (0, 1),
        [nameof(AppSettings.BackgroundAlpha)] = (0, 255),
        [nameof(AppSettings.HoverFadeDuration)] = (0, 10),
        [nameof(AppSettings.HoverFadeMinOpacity)] = (0, 1),
    };

    private readonly string _path;

    public IniSettingsStore(string path)
    {
        _path = path;
    }

    /// <summary>
    /// The settings file exists but could not be read. The app then runs on defaults, and
    /// <see cref="Save"/> leaves the unread file alone instead of replacing it with them.
    /// </summary>
    public bool LoadFailed { get; private set; }

    /// <summary>Why the last <see cref="Load"/> fell back to defaults, if it did.</summary>
    public Exception? LoadError { get; private set; }

    public AppSettings Load()
    {
        LoadFailed = false;
        LoadError = null;
        string[] lines;
        try
        {
            lines = ReadAllLinesWithRetry(_path);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            // Only a file that is really absent means a first run. File.Exists also answers
            // false when the file cannot be checked, which must not lead to overwriting it.
            return new AppSettings();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A file locked by antivirus, sync, or another instance must not crash startup.
            LoadFailed = true;
            LoadError = error;
            return new AppSettings();
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var insideSection = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith('['))
            {
                insideSection = line.Equals($"[{SectionName}]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!insideSection)
            {
                continue;
            }

            var parts = line.Split('=', 2);
            if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
            {
                // Last value wins so a hand-edited duplicate cannot prevent startup.
                values[parts[0].Trim()] = parts[1].Trim();
            }
        }

        var settings = new AppSettings();
        var properties = typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var property in properties)
        {
            if (values.TryGetValue(property.Name, out var rawValue)
                && TryParseValue(property.PropertyType, rawValue, out var parsed)
                && IsWithinBounds(property.Name, parsed))
            {
                property.SetValue(settings, parsed);
            }
        }

        // A file written before per-monitor placement holds only absolute coordinates. Keep using
        // them instead of the fresh-install anchor; the placement code migrates them on startup.
        if (!values.ContainsKey(nameof(AppSettings.WindowRelativeCenterX))
            && values.ContainsKey(nameof(AppSettings.WindowX)))
        {
            settings.WindowRelativeCenterX = settings.WindowRelativeCenterY = -1.0;
        }
        if (!values.ContainsKey(nameof(AppSettings.PureModeRelativeCenterX))
            && values.ContainsKey(nameof(AppSettings.PureModeWindowX)))
        {
            settings.PureModeRelativeCenterX = settings.PureModeRelativeCenterY = -1.0;
        }

        return settings;
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (LoadFailed)
        {
            throw new IOException(
                $"The settings file could not be read at startup and was left unchanged: {_path}", LoadError);
        }

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var lines = new List<string> { $"[{SectionName}]" };
        foreach (var property in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            lines.Add($"{property.Name}={FormatValue(property.GetValue(settings))}");
        }

        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllLines(temporaryPath, lines);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool TryParseValue(Type propertyType, string rawValue, out object? parsed)
    {
        parsed = null;
        if (propertyType == typeof(string))
        {
            parsed = rawValue;
            return true;
        }

        if (propertyType == typeof(int))
        {
            var success = int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value);
            parsed = value;
            return success;
        }

        if (propertyType == typeof(int?))
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return true;
            }

            var success = int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value);
            parsed = value;
            return success;
        }

        if (propertyType == typeof(double))
        {
            // NaN and infinities parse, but would reach TimeSpan and layout code and fail there.
            var success = double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                && double.IsFinite(value);
            parsed = value;
            return success;
        }

        if (propertyType == typeof(bool))
        {
            var success = bool.TryParse(rawValue, out var value);
            parsed = value;
            return success;
        }

        return false;
    }

    private static bool IsWithinBounds(string name, object? value)
    {
        if (value is null || !Bounds.TryGetValue(name, out var bounds)) return true;
        var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        return number >= bounds.Min && number <= bounds.Max;
    }

    private static string[] ReadAllLinesWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllLines(path);
            }
            catch (IOException error) when (attempt < ReadAttempts
                && error is not FileNotFoundException and not DirectoryNotFoundException)
            {
                // Antivirus and sync clients usually hold the file only briefly.
                Thread.Sleep(ReadRetryDelay);
            }
        }
    }

    private static string FormatValue(object? value)
    {
        return value switch
        {
            null => string.Empty,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
    }
}
