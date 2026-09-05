using System.ComponentModel;
using System.Globalization;

namespace TurntableSpeed.App.Localization;

/// <summary>
/// The current language, and the one thing that makes switching it visible without rebuilding
/// the shell.
/// <para>
/// XAML binds to the indexer through <see cref="TranslateExtension"/>, and raising
/// <see cref="PropertyChanged"/> with an empty name is what tells every one of those bindings to
/// re-read its string.
/// </para>
/// <para>
/// C# reads strings from the generated <c>AppStrings</c> class instead, which is compile-checked;
/// setting <see cref="System.Globalization.CultureInfo"/> on it here is what points those
/// properties at the right .resx. Text already composed into a view-model property does not
/// re-read itself, so view models re-render on <see cref="LanguageChanged"/>.
/// </para>
/// </summary>
public sealed class LocalizationManager : INotifyPropertyChanged
{
    /// <summary>
    /// "Every property changed" — the only notification that actually reaches a binding on
    /// <c>[key]</c> here.
    /// <para>
    /// Deliberately not "Item[]". That is WPF's convention for invalidating all indexer
    /// bindings, and MAUI does not implement it: a binding on <c>[ButtonStart]</c> compares the
    /// incoming name against <c>string.Format("{0}[{1}]", IndexerName, Content)</c> — literally
    /// "Item[ButtonStart]" — and discards anything else that contains a bracket. "Item[]" is
    /// dropped in silence, which leaves every translated label in the XAML frozen in the
    /// language its page was built in while everything the view models push updates normally.
    /// </para>
    /// </summary>
    private static readonly PropertyChangedEventArgs EveryProperty = new(string.Empty);

    private AppLanguage _language = AppLanguage.Russian;

    private LocalizationManager() => AppStrings.Culture = AppLanguages.Culture(_language);

    public static LocalizationManager Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// For everything a binding cannot reach: composed status lines, chart captions drawn onto
    /// a canvas, picker item sources.
    /// </summary>
    public event EventHandler? LanguageChanged;

    public AppLanguage Language
    {
        get => _language;
        set
        {
            if (_language == value)
            {
                return;
            }

            _language = value;
            AppStrings.Culture = AppLanguages.Culture(value);

            PropertyChanged?.Invoke(this, EveryProperty);
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Look a key up by name. Only XAML goes through here; an unknown key returns itself rather
    /// than an empty label, so a typo shows up on screen as the key that is wrong.
    /// </summary>
    public string this[string key] =>
        AppStrings.ResourceManager.GetString(key, AppStrings.Culture) ?? key;
}

/// <summary>
/// Composition of localized format strings.
/// <para>
/// Invariant, always: the numbers being substituted are already invariant everywhere else on
/// screen, and one card mixing "33.328" with "33,328" reads as a bug.
/// </para>
/// </summary>
public static class Tr
{
    public static string Format(string format, params object?[] args) =>
        string.Format(CultureInfo.InvariantCulture, format, args);
}
