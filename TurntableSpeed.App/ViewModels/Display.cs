using System.Globalization;

namespace TurntableSpeed.App.ViewModels;

/// <summary>
/// Numbers on their way into a localized sentence.
/// <para>
/// Every one of them is formatted invariantly, deliberately: the headline reading has always
/// been invariant (see <see cref="Presentation.SpeedDisplay"/>), and a card that printed
/// "33.328" in one line and "33,328" in the next would read as a fault in the measurement
/// rather than a locale setting.
/// </para>
/// </summary>
internal static class Display
{
    /// <summary>Stands in for a reading that does not exist yet. An em dash, not a hyphen.</summary>
    public const string Dash = "—";

    public static string Number(double value, string format) =>
        value.ToString(format, CultureInfo.InvariantCulture);

    public static string Number(int value) =>
        value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A 0..1 fraction as a whole percentage, for progress read as text.</summary>
    public static string Percent(double fraction) =>
        fraction.ToString("P0", CultureInfo.InvariantCulture);
}
