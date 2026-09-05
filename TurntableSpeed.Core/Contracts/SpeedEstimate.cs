namespace TurntableSpeed.Core.Contracts;

/// <summary>
/// A speed reading. Never produced without <see cref="StandardError"/>: a point value with
/// no interval is meaningless for this problem.
/// </summary>
/// <param name="RevolutionsPerMinute">Measured speed.</param>
/// <param name="StandardError">1σ uncertainty of <paramref name="RevolutionsPerMinute"/>, same unit.</param>
/// <param name="Nominal">Nearest nominal speed, or null when nothing is close enough.</param>
/// <param name="DeviationPercent">Signed deviation from <paramref name="Nominal"/> in percent.</param>
/// <param name="Confidence">0..1, how much the estimator trusts itself.</param>
/// <param name="Diagnostics">Estimator-specific internals, surfaced on the diagnostics screen.</param>
public sealed record SpeedEstimate(
    double RevolutionsPerMinute,
    double StandardError,
    NominalSpeed? Nominal,
    double DeviationPercent,
    double Confidence,
    IReadOnlyDictionary<string, double> Diagnostics)
{
    public static IReadOnlyDictionary<string, double> NoDiagnostics { get; } =
        new Dictionary<string, double>(0);

    /// <summary>Deviation expressed as pitch error.</summary>
    public double DeviationCents => SpeedMath.CentsFromPercent(DeviationPercent);

    /// <summary>Half-width of the 95% interval (≈1.96σ), in rpm.</summary>
    public double Margin95 => 1.959963984540054 * StandardError;

    public double LowerBound95 => RevolutionsPerMinute - Margin95;

    public double UpperBound95 => RevolutionsPerMinute + Margin95;

    /// <summary>Standard error relative to the reading, in percent.</summary>
    public double StandardErrorPercent =>
        RevolutionsPerMinute > 0.0 ? StandardError / RevolutionsPerMinute * 100.0 : double.NaN;

    /// <summary>Build an estimate, classifying against the nominal grid automatically.</summary>
    public static SpeedEstimate FromRpm(
        double rpm,
        double standardError,
        double confidence,
        IReadOnlyDictionary<string, double>? diagnostics = null,
        double tolerancePercent = NominalSpeeds.DefaultTolerancePercent)
    {
        var classification = NominalSpeeds.Classify(rpm, tolerancePercent);
        return new SpeedEstimate(
            rpm,
            standardError,
            classification.Nominal,
            classification.DeviationPercent,
            Math.Clamp(confidence * (0.35 + 0.65 * classification.Confidence), 0.0, 1.0),
            diagnostics ?? NoDiagnostics);
    }

    public override string ToString() =>
        $"{RevolutionsPerMinute:F4} ± {StandardError:F4} rpm" +
        (Nominal is { } n ? $" ({NominalSpeeds.Info(n).Label}, {DeviationPercent:+0.00;-0.00;0.00}%)" : " (unclassified)");
}

/// <summary>
/// A streaming, incremental estimator. No blocking calls, no threads of its own: everything
/// a test needs is to push samples and read <see cref="Current"/>.
/// </summary>
public interface ISpeedEstimator<TSample>
{
    void Push(TSample sample);

    SpeedEstimate? Current { get; }

    void Reset();
}
