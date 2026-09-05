namespace TurntableSpeed.App.Localization;

/// <summary>
/// <c>Text="{loc:Translate ButtonStart}"</c>.
/// <para>
/// Returns a binding rather than a string on purpose. A markup extension that resolved to text
/// would be evaluated once, when the page is inflated, and the label would keep the language it
/// was born with; a binding on the localization manager's indexer re-reads itself whenever the
/// language changes.
/// </para>
/// </summary>
[ContentProperty(nameof(Key))]
public sealed class TranslateExtension : IMarkupExtension<BindingBase>
{
    /// <summary>A key from AppStrings.resx. Not checked by the compiler — see the resx tests.</summary>
    public string Key { get; set; } = string.Empty;

    public BindingBase ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]", BindingMode.OneWay, source: LocalizationManager.Instance);

    object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider) =>
        ProvideValue(serviceProvider);
}
