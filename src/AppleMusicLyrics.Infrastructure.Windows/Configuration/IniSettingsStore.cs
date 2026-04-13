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

        var values = File.ReadAllLines(_path)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#') && !line.StartsWith(';'))
            .SkipWhile(line => !line.Equals($"[{SectionName}]", StringComparison.OrdinalIgnoreCase))
            .Skip(1)
            .TakeWhile(line => !line.StartsWith('['))
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);

        var settings = new AppSettings();
        var properties = typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var property in properties)
        {
            if (!values.TryGetValue(property.Name, out var rawValue))
            {
                continue;
            }

            object? parsed = property.PropertyType switch
            {
                var type when type == typeof(string) => rawValue,
                var type when type == typeof(int) => int.Parse(rawValue, CultureInfo.InvariantCulture),
                var type when type == typeof(int?) => string.IsNullOrWhiteSpace(rawValue) ? null : int.Parse(rawValue, CultureInfo.InvariantCulture),
                var type when type == typeof(double) => double.Parse(rawValue, CultureInfo.InvariantCulture),
                var type when type == typeof(bool) => bool.Parse(rawValue),
                _ => null,
            };

            if (parsed is not null || property.PropertyType == typeof(int?))
            {
                property.SetValue(settings, parsed);
            }
        }

        return settings;
    }

    public void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var lines = new List<string> { $"[{SectionName}]" };

        foreach (var property in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var value = property.GetValue(settings);
            lines.Add($"{property.Name}={value}");
        }

        File.WriteAllLines(_path, lines);
    }
}
