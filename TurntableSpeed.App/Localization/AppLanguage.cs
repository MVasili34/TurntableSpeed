using System.Globalization;

namespace TurntableSpeed.App.Localization;

/// <summary>The two languages the app ships. Spelled out rather than inferred from the resx set.</summary>
public enum AppLanguage
{
    Russian,
    English,
}

public static class AppLanguages
{
    /// <summary>
    /// The cultures used for resource lookup, built once.
    /// <para>
    /// Under invariant globalization these are inert objects — they carry a name and nothing
    /// else, which is exactly what a <see cref="System.Resources.ResourceManager"/> needs. They
    /// are deliberately never assigned to CurrentCulture: every number on screen is formatted
    /// invariantly and must stay that way.
    /// </para>
    /// </summary>
    public static CultureInfo Culture(AppLanguage language) =>
        language == AppLanguage.English ? English : Russian;

    private static readonly CultureInfo Russian = new("ru");
    private static readonly CultureInfo English = new("en");

    public static string Tag(AppLanguage language) =>
        language == AppLanguage.English ? "en" : "ru";

    /// <summary>
    /// Read a language tag back. Anything that is not English — including an unknown or absent
    /// tag — is Russian, which is the neutral resource set.
    /// </summary>
    public static AppLanguage Parse(string? tag) =>
        !string.IsNullOrWhiteSpace(tag) && tag!.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.English
            : AppLanguage.Russian;

    /// <summary>The language's own name for itself, which is the same in either language.</summary>
    public static string NativeName(AppLanguage language) =>
        language == AppLanguage.English ? "English" : "Русский";
}
