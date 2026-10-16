using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AppleMusicLyrics.App.Localization;

public sealed record LanguageOption(string Code, string NativeName);

/// <summary>
/// Loads data-only language packs from Languages next to the application. A new
/// pack is discovered at the next application start; missing strings use English.
/// UI language selection never changes the thread's numeric or parsing culture.
/// </summary>
public sealed class LocalizationManager : INotifyPropertyChanged
{
    private const string EnglishCode = "en-US";
    private readonly Dictionary<string, LanguagePack> _packs = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, string> _englishFallback = new Dictionary<string, string>();
    private readonly Func<CultureInfo> _systemCultureProvider;
    private State _state = null!;

    public static LocalizationManager Instance { get; } = new();

    public LocalizationManager(
        string? languagesDirectory = null,
        Func<CultureInfo>? systemCultureProvider = null,
        string? preference = null)
    {
        var initialUiCulture = CultureInfo.CurrentUICulture;
        _systemCultureProvider = systemCultureProvider ?? (() => GetWindowsUiCulture(initialUiCulture));

        // Keep a built-in English fallback even if an external pack is removed
        // or damaged. The editable external English pack may override it.
        using (var stream = typeof(LocalizationManager).Assembly.GetManifestResourceStream(
                   "AppleMusicLyrics.Localization.en-US.json"))
        {
            if (stream is not null && ReadPack(stream) is { } english)
            {
                _englishFallback = english.Strings;
                _packs[EnglishCode] = english;
            }
        }

        LoadDirectory(languagesDirectory ?? Path.Combine(AppContext.BaseDirectory, "Languages"));
        SetLanguage(preference);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;

    public string Preference => _state.Preference;
    public string EffectiveLanguage => _state.Pack.Code;
    public IReadOnlyList<LanguageOption> AvailableLanguages => _state.Languages;
    public string this[string key] => Text(key);

    public string Text(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return GetText(_state.Pack, key);
    }

    public string Format(string key, params object?[] args)
    {
        var state = _state;
        var text = GetText(state.Pack, key);
        try
        {
            return string.Format(state.Pack.Culture, text, args);
        }
        catch (FormatException)
        {
            // A translator's malformed placeholder must not break the overlay.
            if (_englishFallback.TryGetValue(key, out var fallback))
            {
                try
                {
                    return string.Format(state.Pack.Culture, fallback, args);
                }
                catch (FormatException)
                {
                    // Also tolerate a caller/pack argument mismatch.
                }
            }

            return text;
        }
    }

    public void SetLanguage(string? preference)
    {
        var normalized = NormalizePreference(preference);
        var culture = normalized == "system" ? _systemCultureProvider() : CultureInfo.GetCultureInfo(normalized);
        var pack = ResolvePack(culture);
        if (_state is { } previous && previous.Preference == normalized && previous.Pack == pack)
        {
            return;
        }

        var languages = new List<LanguageOption>
        {
            new("system", GetText(pack, "Settings.FollowWindows"))
        };
        languages.AddRange(_packs.Values
            .OrderBy(p => p.Code == EnglishCode ? 0 : 1)
            .ThenBy(p => p.Code, StringComparer.OrdinalIgnoreCase)
            .Select(p => new LanguageOption(p.Code, p.NativeName)));
        _state = new State(normalized, pack, languages.AsReadOnly());

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preference)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectiveLanguage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvailableLanguages)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private string GetText(LanguagePack pack, string key)
    {
        if (pack.Strings.TryGetValue(key, out var value))
        {
            return value;
        }

        return _packs.TryGetValue(EnglishCode, out var english) && english.Strings.TryGetValue(key, out value)
            ? value
            : key;
    }

    private LanguagePack ResolvePack(CultureInfo culture)
    {
        for (var current = culture; current.Name.Length > 0; current = current.Parent)
        {
            if (_packs.TryGetValue(current.Name, out var exact))
            {
                return exact;
            }
        }

        // Windows can report Chinese script cultures rather than a region.
        if (culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase))
        {
            var traditional = culture.Name.Contains("Hant", StringComparison.OrdinalIgnoreCase) ||
                              culture.Name.EndsWith("-TW", StringComparison.OrdinalIgnoreCase) ||
                              culture.Name.EndsWith("-HK", StringComparison.OrdinalIgnoreCase) ||
                              culture.Name.EndsWith("-MO", StringComparison.OrdinalIgnoreCase);
            if (_packs.TryGetValue(traditional ? "zh-TW" : "zh-CN", out var chinese))
            {
                return chinese;
            }
        }

        // A region-specific plug-in also serves another region of that language
        // when no exact or parent pack exists. Choose deterministically.
        var related = _packs.Values
            .Where(p => p.Culture.TwoLetterISOLanguageName.Equals(
                culture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Code, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (related is not null)
        {
            return related;
        }

        // Unsupported languages use English rather than an unrelated pack.
        if (_packs.TryGetValue(EnglishCode, out var english))
        {
            return english;
        }

        return new LanguagePack(EnglishCode, "English", CultureInfo.GetCultureInfo(EnglishCode),
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private void LoadDirectory(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.GetFiles(directory, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using var stream = File.OpenRead(path);
                    if (ReadPack(stream) is { } pack && loaded.Add(pack.Code))
                    {
                        if (pack.Code.Equals(EnglishCode, StringComparison.OrdinalIgnoreCase))
                        {
                            var strings = new Dictionary<string, string>(_englishFallback, StringComparer.Ordinal);
                            foreach (var pair in pack.Strings)
                            {
                                strings[pair.Key] = pair.Value;
                            }

                            pack = pack with { Strings = strings };
                        }

                        _packs[pack.Code] = pack;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static LanguagePack? ReadPack(Stream stream)
    {
        try
        {
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("culture", out var codeElement) || codeElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("nativeName", out var nameElement) || nameElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("strings", out var stringsElement) || stringsElement.ValueKind != JsonValueKind.Object ||
                string.IsNullOrWhiteSpace(codeElement.GetString()) || string.IsNullOrWhiteSpace(nameElement.GetString()))
            {
                return null;
            }

            var culture = CultureInfo.GetCultureInfo(codeElement.GetString()!);
            if (culture.Name.Length == 0 || culture.Name.Equals("system", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var strings = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in stringsElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Name))
                {
                    return null;
                }

                strings[property.Name] = property.Value.GetString()!;
            }

            return new LanguagePack(culture.Name, nameElement.GetString()!, culture, strings);
        }
        catch (JsonException) { return null; }
        catch (CultureNotFoundException) { return null; }
    }

    private static string NormalizePreference(string? preference)
    {
        preference = preference?.Trim();
        if (string.IsNullOrWhiteSpace(preference) || preference.Equals("system", StringComparison.OrdinalIgnoreCase))
        {
            return "system";
        }

        try
        {
            return CultureInfo.GetCultureInfo(preference).Name is { Length: > 0 } code ? code : "system";
        }
        catch (CultureNotFoundException)
        {
            return "system";
        }
    }

    private static CultureInfo GetWindowsUiCulture(CultureInfo fallback)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return CultureInfo.GetCultureInfo(GetUserDefaultUILanguage());
            }
            catch (CultureNotFoundException) { }
        }

        return fallback;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern ushort GetUserDefaultUILanguage();

    private sealed record LanguagePack(
        string Code,
        string NativeName,
        CultureInfo Culture,
        IReadOnlyDictionary<string, string> Strings);

    private sealed record State(string Preference, LanguagePack Pack, IReadOnlyList<LanguageOption> Languages);
}
