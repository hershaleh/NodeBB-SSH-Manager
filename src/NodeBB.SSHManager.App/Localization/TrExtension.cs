using System.Windows.Data;
using System.Windows.Markup;

namespace NodeBB.SSHManager.Localization;

[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }
    public TrExtension(string key) => Key = key;
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationService.Instance,
            Mode = BindingMode.OneWay,
            FallbackValue = Key,
            TargetNullValue = Key
        };

        // A nested Binding must be converted to a BindingExpression for the actual
        // dependency-property target. Returning the Binding object directly causes
        // WPF XAML loading to fail when the main window is constructed.
        return binding.ProvideValue(serviceProvider);
    }
}
