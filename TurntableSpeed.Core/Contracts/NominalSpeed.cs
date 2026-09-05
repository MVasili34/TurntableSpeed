namespace TurntableSpeed.Core.Contracts;

/// <summary>The three nominal turntable speeds.</summary>
public enum NominalSpeed
{
    /// <summary>33⅓ rpm.</summary>
    Rpm33 = 33,

    /// <summary>45 rpm.</summary>
    Rpm45 = 45,

    /// <summary>78 rpm.</summary>
    Rpm78 = 78,
}

/// <summary>Exact figures for one nominal speed. All derived from the rpm value, no rounding.</summary>
public sealed record NominalSpeedInfo(NominalSpeed Speed, double Rpm)
{
    public double RevolutionsPerSecond => Rpm / 60.0;

    public double PeriodSeconds => 60.0 / Rpm;

    public double DegreesPerSecond => Rpm * 6.0;

    public double RadiansPerSecond => Rpm * (2.0 * Math.PI) / 60.0;

    public string Label => Speed switch
    {
        NominalSpeed.Rpm33 => "33⅓",
        NominalSpeed.Rpm45 => "45",
        NominalSpeed.Rpm78 => "78",
        _ => Rpm.ToString("0.###"),
    };
}

/// <summary>Result of matching a measured rpm against the nominal grid.</summary>
/// <param name="Nominal">Null when nothing is close enough to claim.</param>
/// <param name="DeviationPercent">Signed deviation from <paramref name="Nominal"/>, in percent.</param>
/// <param name="Confidence">
/// 0..1. Driven by the margin between the best and the runner-up candidate, so a speed
/// sitting halfway between 33⅓ and 45 scores near zero and is never claimed confidently.
/// </param>
public readonly record struct NominalClassification(
    NominalSpeed? Nominal,
    double DeviationPercent,
    double Confidence)
{
    public static NominalClassification None { get; } = new(null, 0.0, 0.0);
}

/// <summary>Domain constants and the speed/pitch conversions used everywhere else.</summary>
public static class NominalSpeeds
{
    public static NominalSpeedInfo Rpm33 { get; } = new(NominalSpeed.Rpm33, 100.0 / 3.0);

    public static NominalSpeedInfo Rpm45 { get; } = new(NominalSpeed.Rpm45, 45.0);

    public static NominalSpeedInfo Rpm78 { get; } = new(NominalSpeed.Rpm78, 78.0);

    public static IReadOnlyList<NominalSpeedInfo> All { get; } = new[] { Rpm33, Rpm45, Rpm78 };

    /// <summary>
    /// Beyond this the reading is not a mis-speeding turntable, it is something else.
    /// Chosen so that the midpoint between 33⅓ and 45 falls outside every band.
    /// </summary>
    public const double DefaultTolerancePercent = 8.0;

    public static NominalSpeedInfo Info(NominalSpeed speed) => speed switch
    {
        NominalSpeed.Rpm33 => Rpm33,
        NominalSpeed.Rpm45 => Rpm45,
        NominalSpeed.Rpm78 => Rpm78,
        _ => throw new ArgumentOutOfRangeException(nameof(speed)),
    };

    public static double Rpm(NominalSpeed speed) => Info(speed).Rpm;

    /// <summary>Match <paramref name="rpm"/> against the nominal grid.</summary>
    public static NominalClassification Classify(double rpm, double tolerancePercent = DefaultTolerancePercent)
    {
        if (!double.IsFinite(rpm) || rpm <= 0.0)
        {
            return NominalClassification.None;
        }

        NominalSpeedInfo? best = null;
        var bestDeviation = double.PositiveInfinity;
        var runnerUpDeviation = double.PositiveInfinity;

        foreach (var candidate in All)
        {
            var deviation = (rpm - candidate.Rpm) / candidate.Rpm * 100.0;
            var magnitude = Math.Abs(deviation);
            if (magnitude < Math.Abs(bestDeviation))
            {
                runnerUpDeviation = bestDeviation;
                bestDeviation = deviation;
                best = candidate;
            }
            else if (magnitude < Math.Abs(runnerUpDeviation))
            {
                runnerUpDeviation = deviation;
            }
        }

        if (best is null || Math.Abs(bestDeviation) > tolerancePercent)
        {
            return NominalClassification.None;
        }

        // Separation between the winner and the runner-up, normalised. Equidistant => 0.
        var d1 = Math.Abs(bestDeviation);
        var d2 = Math.Abs(runnerUpDeviation);
        var separation = double.IsFinite(d2) && d1 + d2 > 1e-12
            ? (d2 - d1) / (d2 + d1)
            : 1.0;

        // Being far from the winner is also a reason to doubt the label.
        var proximity = 1.0 - Math.Min(1.0, d1 / tolerancePercent);
        var confidence = Math.Clamp(separation * (0.5 + 0.5 * proximity), 0.0, 1.0);

        return new NominalClassification(best.Speed, bestDeviation, confidence);
    }
}

/// <summary>Speed ↔ pitch conversions.</summary>
public static class SpeedMath
{
    /// <summary>cents = 1200·log₂(1 + e), where <paramref name="relativeDeviation"/> is e.</summary>
    public static double CentsFromRelativeDeviation(double relativeDeviation) =>
        1200.0 * Math.Log(1.0 + relativeDeviation, 2.0);

    /// <summary>Inverse of <see cref="CentsFromRelativeDeviation"/>.</summary>
    public static double RelativeDeviationFromCents(double cents) =>
        Math.Pow(2.0, cents / 1200.0) - 1.0;

    public static double CentsFromPercent(double percent) =>
        CentsFromRelativeDeviation(percent / 100.0);

    public static double PercentFromCents(double cents) =>
        RelativeDeviationFromCents(cents) * 100.0;

    public static double RpmFromRadiansPerSecond(double radiansPerSecond) =>
        radiansPerSecond * 60.0 / (2.0 * Math.PI);

    public static double RadiansPerSecondFromRpm(double rpm) =>
        rpm * (2.0 * Math.PI) / 60.0;

    public static double RpmFromPeriod(double periodSeconds) =>
        periodSeconds > 0.0 ? 60.0 / periodSeconds : double.NaN;

    public static double PeriodFromRpm(double rpm) =>
        rpm > 0.0 ? 60.0 / rpm : double.NaN;
}
