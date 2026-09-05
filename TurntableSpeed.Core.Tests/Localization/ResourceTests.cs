using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TurntableSpeed.Core.Tests.Localization;

/// <summary>
/// The app's two .resx files, checked against each other and against the pages that use them.
/// <para>
/// These are file-level tests on purpose. TurntableSpeed.App targets net10.0-android and cannot
/// be referenced from here, and the keys XAML asks for are strings that no compiler checks — so
/// a missing translation or a mistyped key would otherwise reach the screen as a blank label.
/// The .resx files and the .xaml files are copied next to the test binaries by the project file.
/// </para>
/// </summary>
public sealed class ResourceTests
{
    /// <summary>Russian is the neutral set; English is the satellite.</summary>
    private static readonly IReadOnlyDictionary<string, string> Russian = Load("AppStrings.resx");

    private static readonly IReadOnlyDictionary<string, string> English = Load("AppStrings.en.resx");

    /// <summary>Matches {0}, and also {0:F3} or {0,8} should a format ever grow one.</summary>
    private static readonly Regex Placeholder = new(@"\{(\d+)(?:[,:][^}]*)?\}", RegexOptions.Compiled);

    private static readonly Regex TranslateKey =
        new(@"\{loc:Translate\s+([A-Za-z_][A-Za-z0-9_]*)\s*\}", RegexOptions.Compiled);

    [Fact]
    public void CatalogueIsNotEmpty()
    {
        // Guards the tests below: a failed copy would otherwise make every one of them pass.
        Assert.True(Russian.Count > 100, $"Only {Russian.Count} Russian strings were loaded.");
    }

    [Fact]
    public void BothLanguagesDeclareTheSameKeys()
    {
        Assert.Empty(Russian.Keys.Except(English.Keys, StringComparer.Ordinal));
        Assert.Empty(English.Keys.Except(Russian.Keys, StringComparer.Ordinal));
    }

    [Fact]
    public void NoStringIsEmpty()
    {
        Assert.Empty(Russian.Where(pair => string.IsNullOrWhiteSpace(pair.Value)).Select(pair => pair.Key));
        Assert.Empty(English.Where(pair => string.IsNullOrWhiteSpace(pair.Value)).Select(pair => pair.Key));
    }

    /// <summary>
    /// The one failure mode that reaches the user as an exception rather than as odd wording: a
    /// translation that drops or invents a {0} throws FormatException when the line is composed.
    /// </summary>
    [Fact]
    public void PlaceholdersMatchBetweenLanguages()
    {
        var mismatched = Russian.Keys
            .Where(English.ContainsKey)
            .Where(key => !Indexes(Russian[key]).SequenceEqual(Indexes(English[key])))
            .ToArray();

        Assert.Empty(mismatched);
    }

    /// <summary>
    /// The keys become properties on the generated AppStrings class, so anything that is not an
    /// identifier would either fail to compile or be silently renamed.
    /// </summary>
    [Fact]
    public void KeysAreValidIdentifiers()
    {
        var bad = Russian.Keys
            .Where(key => !Regex.IsMatch(key, @"^[A-Za-z_][A-Za-z0-9_]*$"))
            .ToArray();

        Assert.Empty(bad);
    }

    [Fact]
    public void EveryKeyUsedByXamlExists()
    {
        var used = XamlKeys().ToArray();

        Assert.NotEmpty(used);
        Assert.Empty(used.Select(u => u.Key).Distinct(StringComparer.Ordinal).Except(Russian.Keys, StringComparer.Ordinal));
    }

    /// <summary>Spec §3.6 and §4.3 require these two, so they have to survive translation.</summary>
    [Theory]
    [InlineData("LoadCaveat")]
    [InlineData("PitchCaveat")]
    public void MandatoryCaveatsExistInBothLanguages(string key)
    {
        Assert.True(Russian.TryGetValue(key, out var ru) && ru.Length > 80, $"{key} is missing or too short in Russian.");
        Assert.True(English.TryGetValue(key, out var en) && en.Length > 80, $"{key} is missing or too short in English.");
    }

    [Fact]
    public void TranslationsActuallyDiffer()
    {
        // A key left untranslated is easy to miss by eye. Symbols and numerals are legitimately
        // identical, so this only asserts that most of the catalogue moved.
        var identical = Russian
            .Where(pair => English.TryGetValue(pair.Key, out var en) && string.Equals(pair.Value, en, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToArray();

        Assert.True(identical.Length < Russian.Count / 10,
            "Too many strings are identical in both languages: " + string.Join(", ", identical));
    }

    private static IEnumerable<int> Indexes(string value) =>
        Placeholder.Matches(value)
            .Select(match => int.Parse(match.Groups[1].Value))
            .Distinct()
            .OrderBy(index => index);

    private static IEnumerable<(string File, string Key)> XamlKeys()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Xaml");
        Assert.True(Directory.Exists(directory), $"No XAML was copied to {directory}.");

        foreach (var file in Directory.EnumerateFiles(directory, "*.xaml", SearchOption.AllDirectories))
        {
            foreach (Match match in TranslateKey.Matches(File.ReadAllText(file)))
            {
                yield return (Path.GetFileName(file), match.Groups[1].Value);
            }
        }
    }

    private static IReadOnlyDictionary<string, string> Load(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Localization", fileName);
        Assert.True(File.Exists(path), $"{path} was not copied next to the test binaries.");

        return XDocument.Load(path)
            .Root!
            .Elements("data")
            .ToDictionary(
                data => data.Attribute("name")!.Value,
                data => data.Element("value")?.Value ?? string.Empty,
                StringComparer.Ordinal);
    }
}
