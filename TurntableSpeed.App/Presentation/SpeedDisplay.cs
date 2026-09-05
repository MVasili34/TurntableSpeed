using System.Globalization;
using TurntableSpeed.App.Localization;
using TurntableSpeed.Core.Contracts;

namespace TurntableSpeed.App.Presentation;

/// <summary>
/// A speed reading formatted for the screen.
/// <para>
/// Lives in the core, not the MAUI project, for the same reason the DSP does: it can be tested
/// without an emulator. Nothing here touches a platform API.
/// </para>
/// </summary>
/// <param name="Rpm">The reading, printed to as many digits as its interval justifies.</param>
/// <param name="Interval">Half-width of the 95% interval, same number of digits.</param>
/// <param name="DeviationPercent">Signed deviation from the nominal, or an em dash.</param>
/// <param name="DeviationCents">The same deviation as a pitch error.</param>
/// <param name="Nominal">"33⅓", "45", "78", or an em dash when nothing is close enough.</param>
/// <param name="Confidence">0..1, for a progress indicator.</param>
public sealed record SpeedDisplay(
    string Rpm,
    string Interval,
    string DeviationPercent,
    string DeviationCents,
    string Nominal,
    double Confidence,
    string ConfidenceLabel,
    bool HasReading)
{
    private const string Dash = "—";

    /// <summary>
    /// Computed rather than cached: the confidence label is localized, and a static instance
    /// built at startup would hold the language the app happened to launch in.
    /// </summary>
    public static SpeedDisplay Empty =>
        new(Dash, string.Empty, Dash, Dash, Dash, 0.0, ConfidenceLabels.NoSignal, false);

    /// <summary>"33.328 ± 0.019 об/мин" — the whole reading on one line.</summary>
    public string RpmWithInterval =>
        HasReading ? $"{Rpm} ± {Interval}" : Dash;

    /// <summary>
    /// "± 0.019 об/мин (95%)". Composed here rather than by a StringFormat in XAML, because a
    /// StringFormat is a literal in the page and could not follow the language.
    /// </summary>
    public string Interval95 =>
        HasReading ? Tr.Format(AppStrings.SpeedInterval95, Interval) : string.Empty;

    public static SpeedDisplay From(SpeedEstimate? estimate)
    {
        if (estimate is null || !double.IsFinite(estimate.RevolutionsPerMinute))
        {
            return Empty;
        }

        var digits = DecimalsFor(estimate.Margin95);

        return new SpeedDisplay(
            Format(estimate.RevolutionsPerMinute, digits),
            Format(estimate.Margin95, digits),
            estimate.Nominal is null ? Dash : Signed(estimate.DeviationPercent, 2) + "%",
            estimate.Nominal is null ? Dash : Signed(estimate.DeviationCents, 1) + " ¢",
            estimate.Nominal is { } nominal ? NominalSpeeds.Info(nominal).Label : Dash,
            Math.Clamp(estimate.Confidence, 0.0, 1.0),
            ConfidenceLabels.For(estimate.Confidence),
            true);
    }

    /// <summary>
    /// How many decimals a reading with this uncertainty may be shown to.
    /// <para>
    /// Printing 33.32812 next to ± 0.02 claims four digits the measurement does not have. The
    /// rule is one digit past the leading digit of the uncertainty, capped so the display stays
    /// readable, which is the same convention the interval itself is printed with.
    /// </para>
    /// </summary>
    public static int DecimalsFor(double uncertainty)
    {
        if (!double.IsFinite(uncertainty) || uncertainty <= 0.0)
        {
            return 3;
        }

        var decimals = (int)Math.Ceiling(-Math.Log10(uncertainty));
        return Math.Clamp(decimals, 0, 4);
    }

    private static string Format(double value, int decimals) =>
        value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static string Signed(double value, int decimals)
    {
        if (!double.IsFinite(value))
        {
            return Dash;
        }

        var text = Math.Abs(value).ToString(
            "F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

        // A minus sign, not a hyphen: this sits next to a large number on screen.
        return value < 0.0 ? "−" + text : "+" + text;
    }
}

public static class ConfidenceLabels
{
    // Properties, not constants: each one is read out of the resource set on every access, so
    // the label follows the current language.
    public static string NoSignal => AppStrings.ConfidenceNoSignal;
    public static string Collecting => AppStrings.ConfidenceCollecting;
    public static string Converging => AppStrings.ConfidenceConverging;
    public static string Confident => AppStrings.ConfidenceConfident;

    public static string For(double confidence) => confidence switch
    {
        < 0.15 => Collecting,
        < 0.6 => Converging,
        _ => Confident,
    };
}
