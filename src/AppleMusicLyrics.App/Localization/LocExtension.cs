using System.Windows.Data;
using System.Windows.Markup;

namespace AppleMusicLyrics.App.Localization;

[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }

    public LocExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new System.Windows.Data.Binding($"[{Key}]")
        {
            Source = LocalizationManager.Instance,
            Mode = BindingMode.OneWay
        };
        return binding.ProvideValue(serviceProvider);
    }
}
