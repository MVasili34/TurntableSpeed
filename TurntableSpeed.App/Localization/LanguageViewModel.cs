using System.Windows.Input;
using TurntableSpeed.App.ViewModels;

namespace TurntableSpeed.App.Localization;

/// <summary>
/// The language switch itself: the toolbar toggle on every screen and the picker on the
/// diagnostics screen both talk to this one instance.
/// <para>
/// A singleton with a static <see cref="Instance"/> because the toolbar has no binding context
/// of its own — <c>{x:Static loc:LanguageViewModel.Instance}</c> is how a ToolbarItem reaches a
/// view model at all.
/// </para>
/// </summary>
public sealed class LanguageViewModel : ObservableBase
{
    private const string PreferenceKey = "ui.language";

    private static readonly AppLanguage[] Order = { AppLanguage.Russian, AppLanguage.English };

    private LanguageViewModel()
    {
        LocalizationManager.Instance.Language = Restore();
        ToggleCommand = new Command(Toggle);

        LocalizationManager.Instance.LanguageChanged += (_, _) =>
        {
            Raise(nameof(ToggleLabel));
            Raise(nameof(SelectedIndex));
        };
    }

    public static LanguageViewModel Instance { get; } = new();

    public ICommand ToggleCommand { get; }

    /// <summary>Each language named in itself, in the order <see cref="SelectedIndex"/> uses.</summary>
    public IReadOnlyList<string> Options { get; } =
        Order.Select(AppLanguages.NativeName).ToArray();

    /// <summary>
    /// The tag of the language the toggle switches <em>to</em>, not the current one: a button
    /// reading "EN" that produces English is understood without a caption.
    /// </summary>
    public string ToggleLabel => AppLanguages.Tag(Other()).ToUpperInvariant();

    public int SelectedIndex
    {
        get => Array.IndexOf(Order, LocalizationManager.Instance.Language);
        set
        {
            // A Picker reports -1 while its item source is being replaced.
            if (value < 0 || value >= Order.Length)
            {
                return;
            }

            Apply(Order[value]);
        }
    }

    public void Toggle() => Apply(Other());

    private static AppLanguage Other() =>
        LocalizationManager.Instance.Language == AppLanguage.Russian
            ? AppLanguage.English
            : AppLanguage.Russian;

    private static void Apply(AppLanguage language)
    {
        LocalizationManager.Instance.Language = language;
        Preferences.Default.Set(PreferenceKey, AppLanguages.Tag(language));
    }

    /// <summary>
    /// A stored choice wins. Failing that, follow the phone: someone whose device is in English
    /// should not have to find the switch first.
    /// </summary>
    private static AppLanguage Restore()
    {
        var stored = Preferences.Default.Get(PreferenceKey, string.Empty);
        if (!string.IsNullOrEmpty(stored))
        {
            return AppLanguages.Parse(stored);
        }

#if ANDROID
        // Not CultureInfo.CurrentUICulture: under invariant globalization that is always the
        // invariant culture and would say nothing about the device.
        return AppLanguages.Parse(Java.Util.Locale.Default?.Language);
#else
        return AppLanguage.Russian;
#endif
    }
}
