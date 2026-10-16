using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Threading;
using AppleMusicLyrics.App.Localization;
using Xunit;

namespace AppleMusicLyrics.Tests.Localization;

[CollectionDefinition("Localization", DisableParallelization = true)]
public sealed class LocalizationCollection { }

[Collection("Localization")]
public sealed class LocalizationTests
{
    [Fact]
    public void ShippedPacks_HaveMatchingCompleteKeysAndNativeNames()
    {
        var packs = new[] { "en-US", "zh-CN", "zh-TW" }
            .Select(code => JsonDocument.Parse(File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "Languages", code + ".json"))))
            .ToArray();
        try
        {
            var englishKeys = Keys(packs[0]);
            Assert.True(englishKeys.Length >= 137);
            Assert.Equal(englishKeys, Keys(packs[1]));
            Assert.Equal(englishKeys, Keys(packs[2]));
            Assert.All(packs, pack => Assert.All(pack.RootElement.GetProperty("strings").EnumerateObject(),
                pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value.GetString()), pair.Name)));

            var manager = new LocalizationManager(preference: "en-US");
            Assert.Contains(manager.AvailableLanguages, language => language is { Code: "en-US", NativeName: "English" });
            Assert.Contains(manager.AvailableLanguages, language => language is { Code: "zh-CN", NativeName: "简体中文" });
            Assert.Contains(manager.AvailableLanguages, language => language is { Code: "zh-TW", NativeName: "繁體中文" });
            Assert.Equal("Follow Windows", manager.AvailableLanguages[0].NativeName);
            manager.SetLanguage("zh-TW");
            Assert.Equal("跟隨 Windows", manager.AvailableLanguages[0].NativeName);
        }
        finally
        {
            foreach (var pack in packs) pack.Dispose();
        }

        static string[] Keys(JsonDocument pack) => pack.RootElement.GetProperty("strings")
            .EnumerateObject().Select(pair => pair.Name).Order(StringComparer.Ordinal).ToArray();
    }

    [Theory]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("zh-Hant", "zh-TW")]
    [InlineData("zh-HK", "zh-TW")]
    [InlineData("zh-MO", "zh-TW")]
    [InlineData("en-GB", "en-US")]
    [InlineData("de-DE", "en-US")]
    public void SystemPreference_UsesInjectedUserUiCultureAndFallsBack(string systemCulture, string expected)
    {
        var manager = new LocalizationManager(systemCultureProvider: () => CultureInfo.GetCultureInfo(systemCulture));
        Assert.Equal("system", manager.Preference);
        Assert.Equal(expected, manager.EffectiveLanguage);
    }

    [Fact]
    public void SetLanguage_NotifiesIndexerAndSystemChoiceWithoutChangingThreadCultures()
    {
        var numericCulture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        var manager = new LocalizationManager(preference: "en-US");
        var properties = new List<string?>();
        var changed = 0;
        manager.PropertyChanged += (_, change) => properties.Add(change.PropertyName);
        manager.LanguageChanged += (_, _) => changed++;

        manager.SetLanguage("zh-CN");

        Assert.Equal("zh-CN", manager.Preference);
        Assert.Equal("zh-CN", manager.EffectiveLanguage);
        Assert.Equal("歌词浮层设置", manager["Settings.Title"]);
        Assert.Contains("Item[]", properties);
        Assert.Contains(nameof(LocalizationManager.Preference), properties);
        Assert.Contains(nameof(LocalizationManager.EffectiveLanguage), properties);
        Assert.Contains(nameof(LocalizationManager.AvailableLanguages), properties);
        Assert.Equal(1, changed);
        manager.SetLanguage("zh-CN");
        Assert.Equal(1, changed);
        Assert.Same(numericCulture, CultureInfo.CurrentCulture);
        Assert.Same(uiCulture, CultureInfo.CurrentUICulture);
    }

    [Fact]
    public void Preference_RetainsValidUnsupportedAndNeutralCultures()
    {
        using var directory = new TemporaryDirectory();
        var manager = new LocalizationManager(directory.Path, preference: "fr-CA");
        Assert.Equal("fr-CA", manager.Preference);
        Assert.Equal("en-US", manager.EffectiveLanguage);
        manager.SetLanguage("en");
        Assert.Equal("en", manager.Preference);
        Assert.Equal("en-US", manager.EffectiveLanguage);
        manager.SetLanguage(" SYSTEM ");
        Assert.Equal("system", manager.Preference);
    }

    [Fact]
    public void NewPack_IsDiscoveredAfterRestartAndRelatedRegionUsesIt()
    {
        using var directory = new TemporaryDirectory();
        var beforeInstall = new LocalizationManager(directory.Path, preference: "fr-CA");
        WritePack(directory.Path, "French.json", "fr-FR", "Français", new()
        {
            ["Settings.Title"] = "Paramètres des paroles",
            ["Probe.Number"] = "{0:0.00}"
        });
        Assert.DoesNotContain(beforeInstall.AvailableLanguages, language => language.Code == "fr-FR");

        var restarted = new LocalizationManager(directory.Path,
            systemCultureProvider: () => CultureInfo.GetCultureInfo("fr-CA"));

        Assert.Equal("system", restarted.Preference);
        Assert.Equal("fr-FR", restarted.EffectiveLanguage);
        Assert.Equal("Paramètres des paroles", restarted.Text("Settings.Title"));
        Assert.Equal("12,50", restarted.Format("Probe.Number", 12.5));
        Assert.Equal("1,25s", restarted.Format("Settings.Unit.Seconds", 1.25));
        Assert.Equal("Apply", restarted.Text("Settings.Action.Apply"));
        Assert.Equal("Unknown.Key", restarted.Text("Unknown.Key"));
    }

    [Fact]
    public void ExactAndParentPacks_TakePriorityOverRelatedLanguagePacks()
    {
        using var directory = new TemporaryDirectory();
        WritePack(directory.Path, "a-French.json", "fr-FR", "Français", new() { ["Probe"] = "France" });
        WritePack(directory.Path, "b-Canadian.json", "fr-CA", "Français canadien", new() { ["Probe"] = "Canada" });
        WritePack(directory.Path, "c-Parent.json", "fr", "Français neutre", new() { ["Probe"] = "Parent" });
        var manager = new LocalizationManager(directory.Path, preference: "fr-CA");
        Assert.Equal("Canada", manager.Text("Probe"));
        manager.SetLanguage("fr-BE");
        Assert.Equal("Parent", manager.Text("Probe"));
    }

    [Fact]
    public void BadPacks_AreSkippedAndValidDuplicateSelectionIsDeterministic()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "broken.json"), "{ invalid json");
        File.WriteAllText(Path.Combine(directory.Path, "wrong-schema.json"),
            """{"culture":"ja-JP","nativeName":"日本語","strings":{"Settings.Title":42}}""");
        File.WriteAllText(Path.Combine(directory.Path, "missing-name.json"),
            """{"culture":"ko-KR","strings":{}}""");
        WritePack(directory.Path, "a-Spanish.json", "es-ES", "Español", new() { ["Probe"] = "First" });
        WritePack(directory.Path, "z-Spanish.json", "es-ES", "Duplicate", new() { ["Probe"] = "Second" });

        var manager = new LocalizationManager(directory.Path, preference: "es-ES");

        Assert.Equal("First", manager.Text("Probe"));
        Assert.Equal(new[] { "system", "en-US", "es-ES" }, manager.AvailableLanguages.Select(language => language.Code));
        Assert.Equal("Apply", manager.Text("Settings.Action.Apply"));
    }

    [Fact]
    public void MissingOrMalformedEnglishPack_StillUsesEmbeddedFallback()
    {
        using var directory = new TemporaryDirectory();
        var missing = new LocalizationManager(directory.Path, preference: "en-US");
        Assert.Equal("Lyrics Overlay Settings", missing.Text("Settings.Title"));
        File.WriteAllText(Path.Combine(directory.Path, "en-US.json"), "not JSON");
        var broken = new LocalizationManager(directory.Path, preference: "en-US");
        Assert.Equal("Apply", broken.Text("Settings.Action.Apply"));
    }

    [Fact]
    public void PartialEnglishPack_OverridesPresentKeysAndRetainsEmbeddedMissingKeys()
    {
        using var directory = new TemporaryDirectory();
        WritePack(directory.Path, "en-US.json", "en-US", "English", new() { ["Settings.Title"] = "Custom title" });

        var manager = new LocalizationManager(directory.Path, preference: "en-US");

        Assert.Equal("Custom title", manager.Text("Settings.Title"));
        Assert.Equal("Apply", manager.Text("Settings.Action.Apply"));
    }

    [Fact]
    public void InvalidLocalizedFormat_FallsBackToEmbeddedEnglishTemplate()
    {
        using var directory = new TemporaryDirectory();
        WritePack(directory.Path, "en-US.json", "en-US", "English", new() { ["Lyrics.LineCount"] = "{ malformed" });
        WritePack(directory.Path, "ja-JP.json", "ja-JP", "日本語", new() { ["Lyrics.LineCount"] = "{4} 行" });
        var manager = new LocalizationManager(directory.Path, preference: "ja-JP");

        Assert.Equal("3 lines", manager.Format("Lyrics.LineCount", 3));
        manager.SetLanguage("en-US");
        Assert.Equal("3 lines", manager.Format("Lyrics.LineCount", 3));
    }

    [Fact]
    public void LocMarkupExtension_RefreshesExistingWpfBindingOnLanguageChange() => OnSta(() =>
    {
        var manager = LocalizationManager.Instance;
        var originalPreference = manager.Preference;
        try
        {
            manager.SetLanguage("en-US");
            var text = Assert.IsType<TextBlock>(XamlReader.Parse("""
                <TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                           xmlns:loc="clr-namespace:AppleMusicLyrics.App.Localization;assembly=AppleMusicLyrics.App"
                           Text="{loc:Loc Settings.Title}" />
                """));
            Assert.NotNull(BindingOperations.GetBinding(text, TextBlock.TextProperty));
            Assert.Equal("Lyrics Overlay Settings", text.Text);

            manager.SetLanguage("zh-TW");
            text.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);

            Assert.Equal("歌詞浮層設定", text.Text);
        }
        finally { manager.SetLanguage(originalPreference); }
    });

    private static void WritePack(string directory, string filename, string culture, string name,
        Dictionary<string, string> strings) => File.WriteAllText(Path.Combine(directory, filename),
        JsonSerializer.Serialize(new { culture, nativeName = name, strings }));

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15))) throw new TimeoutException("Localization binding test did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AppleMusicLyrics.Localization." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
