using System.Globalization;
using System.Reflection;
using AppleMusicLyrics.Core.Abstractions;
using AppleMusicLyrics.Core.Configuration;

namespace AppleMusicLyrics.Infrastructure.Windows.Configuration;

public sealed class IniSettingsStore : ISettingsStore
{
    private const string SectionName = "apple_music_lyrics";
    private readonly string _path;

    public IniSettingsStore(string path)
    {
        _path = path;
    }

    public AppSettings Load()
    {
        if (!File.Exists(_path))
        {
            return new AppSettings();
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var insideSection = false;
        foreach (var rawLine in File.ReadAllLines(_path))
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
                && TryParseValue(property.PropertyType, rawValue, out var parsed))
            {
                property.SetValue(settings, parsed);
            }
        }

        return settings;
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

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
            var success = double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value);
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
