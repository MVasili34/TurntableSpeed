using TurntableSpeed.Core.Contracts;
using TurntableSpeed.Core.Dsp;

namespace TurntableSpeed.Core.Estimators;

public sealed class MagnetometerSpeedEstimatorOptions
{
    /// <summary>Longest history kept. 60 s ≈ 33 revolutions at 33⅓ rpm.</summary>
    public double WindowSeconds { get; set; } = 60.0;

    /// <summary>Hard cap on retained samples, independent of rate.</summary>
    public int MaxSamples { get; set; } = 12_000;

    /// <summary>Below this the phase ramp is too short to regress meaningfully.</summary>
    public double MinimumSpanSeconds { get; set; } = 2.0;

    /// <summary>
    /// The ellipse fit needs a closed loop. Under one revolution the hard-iron offset and the
    /// arc curvature are not separable and the fit will happily invent a centre.
    /// </summary>
    public double MinimumRevolutions { get; set; } = 1.0;

    public int MinimumSamples { get; set; } = 64;

    /// <summary>Samples between re-fits. Deterministic by construction — never time-based.</summary>
    public int RecomputeEverySamples { get; set; } = 25;

    /// <summary>Huber-weighted regression, so a passing magnet cannot tilt the ramp.</summary>
    public bool UseRobustRegression { get; set; } = true;

    /// <summary>Apply the fitted ellipse as a soft-iron correction before taking the phase.</summary>
    public bool ApplySoftIronCorrection { get; set; } = true;

    /// <summary>Relative precision treated as "fully converged" for the confidence score.</summary>
    public double TargetRelativePrecision { get; set; } = 0.0005;

    /// <summary>
    /// How far off the fitted ellipse a sample may sit before it is discarded, as a fraction of
    /// the radius. A magnetic disturbance changes the field magnitude, so it shows up here
    /// before it ever reaches the phase.
    /// </summary>
    public double OutlierRadiusTolerance { get; set; } = 0.35;

    /// <summary>
    /// Above this rejected fraction the ellipse — not the samples — is what looks wrong, so the
    /// gate is not applied and the bad fit is reported honestly through the diagnostics.
    /// </summary>
    public double MaximumRejectedFraction { get; set; } = 0.5;
}

/// <summary>
/// The primary sensor estimator. It measures a <em>frequency</em>, not an amplitude: the phase
/// of the Earth's field as seen in the phone's own axes advances by exactly 2π per revolution,
/// and the time base comes from the phone's crystal (a few ppm). Unlike the gyroscope there is
/// no scale-factor error to calibrate away.
/// </summary>
public sealed class MagnetometerSpeedEstimator : ISpeedEstimator<Vector3Sample>
{
    private readonly MagnetometerSpeedEstimatorOptions _options;
    private readonly TimeWindow<Vector3Sample> _window;
    private int _sinceRecompute;
    private double _lastTimestamp = double.NegativeInfinity;

    public MagnetometerSpeedEstimator(MagnetometerSpeedEstimatorOptions? options = null)
    {
        _options = options ?? new MagnetometerSpeedEstimatorOptions();
        _window = new TimeWindow<Vector3Sample>(s => s.T, _options.WindowSeconds, _options.MaxSamples);
    }

    public SpeedEstimate? Current { get; private set; }

    /// <summary>Latest hard-iron / soft-iron model, in the fitted plane's coordinates.</summary>
    public EllipseFitResult? Ellipse { get; private set; }

    /// <summary>Hard-iron offset expressed in the phone's own axes, µT.</summary>
    public (double X, double Y, double Z)? HardIronOffset { get; private set; }

    /// <summary>Unit normal of the plane swept by the field vector — the rotation axis.</summary>
    public (double X, double Y, double Z)? RotationAxis { get; private set; }

    /// <summary>Signed angular rate, rad/s. The sign encodes the direction of rotation.</summary>
    public double SignedRadiansPerSecond { get; private set; } = double.NaN;

    public int SampleCount => _window.Count;

    public double WindowSpanSeconds => _window.Span;

    public void Push(Vector3Sample sample)
    {
        if (!sample.IsFinite || sample.T <= _lastTimestamp)
        {
            return;
        }

        _lastTimestamp = sample.T;
        _window.Add(sample);

        if (++_sinceRecompute < _options.RecomputeEverySamples)
        {
            return;
        }

        _sinceRecompute = 0;
        Recompute();
    }

    /// <summary>Force a re-fit without waiting for the sample counter. Used at end of capture.</summary>
    public void Flush()
    {
        _sinceRecompute = 0;
        Recompute();
    }

    public void Reset()
    {
        _window.Clear();
        Current = null;
        Ellipse = null;
        HardIronOffset = null;
        RotationAxis = null;
        SignedRadiansPerSecond = double.NaN;
        _sinceRecompute = 0;
        _lastTimestamp = double.NegativeInfinity;
    }

    private void Recompute()
    {
        var count = _window.Count;
        if (count < _options.MinimumSamples || _window.Span < _options.MinimumSpanSeconds)
        {
            return;
        }

        var samples = _window.ToArray();
        var axes = PrincipalAxes.Compute(samples);
        if (axes is null)
        {
            return;
        }

        var (us, vs) = Project(samples, axes);
        var ellipse = _options.ApplySoftIronCorrection ? EllipseFit.Fit(us, vs) : null;

        // Drop samples that have left the fitted ellipse before taking any phase. Robust
        // regression alone cannot save us here: unwrapping is cumulative, so one sample thrown
        // half a turn off by a passing magnet shifts every later phase by 2π, and a level shift
        // is not something Huber weights can reject.
        var rejectedFraction = 0.0;
        if (ellipse is not null)
        {
            var kept = FilterToEllipse(samples, us, vs, ellipse);
            if (kept is not null)
            {
                rejectedFraction = 1.0 - kept.Length / (double)samples.Length;
                samples = kept;
                axes = PrincipalAxes.Compute(samples) ?? axes;
                (us, vs) = Project(samples, axes);
                ellipse = EllipseFit.Fit(us, vs) ?? ellipse;
            }
        }

        count = samples.Length;
        var span = samples[count - 1].T - samples[0].T;
        if (count < _options.MinimumSamples || span < _options.MinimumSpanSeconds)
        {
            return;
        }

        var phases = new double[count];
        if (ellipse is not null)
        {
            for (var i = 0; i < count; i++)
            {
                phases[i] = ellipse.Phase(us[i], vs[i]);
            }
        }
        else
        {
            for (var i = 0; i < count; i++)
            {
                phases[i] = Math.Atan2(vs[i], us[i]);
            }
        }

        var t0 = samples[0].T;
        var times = new double[count];
        for (var i = 0; i < count; i++)
        {
            times[i] = samples[i].T - t0;
        }

        // Branch-snapping rather than chained unwrapping: the platter turns at a steady rate, so
        // every sample can be placed on the branch nearest that ramp. A disturbed sample then
        // costs only its own residual instead of shifting the whole remainder of the recording.
        var unwrapped = PhaseMath.UnwrapSteady(times, phases);

        var revolutions = Math.Abs(unwrapped[count - 1] - unwrapped[0]) / PhaseMath.TwoPi;
        if (revolutions < _options.MinimumRevolutions)
        {
            return;
        }

        var fit = _options.UseRobustRegression
            ? RobustLineFit.Huber(times, unwrapped)
            : WeightedLeastSquares.Fit(times, unwrapped);

        if (!fit.IsValid || !(Math.Abs(fit.Slope) > 0.0))
        {
            return;
        }

        var omega = fit.Slope;
        var rpm = SpeedMath.RpmFromRadiansPerSecond(Math.Abs(omega));
        var rpmError = SpeedMath.RpmFromRadiansPerSecond(Math.Abs(fit.SlopeStandardErrorCorrected));

        if (!double.IsFinite(rpm) || !double.IsFinite(rpmError) || rpm <= 0.0)
        {
            return;
        }

        var outlierFraction = RobustLineFit.OutlierFraction(times, unwrapped, fit);
        var confidence = ScoreConfidence(rpm, rpmError, revolutions, ellipse, axes.Flatness, outlierFraction);

        SignedRadiansPerSecond = omega;
        Ellipse = ellipse;
        RotationAxis = axes.Normal;
        HardIronOffset = ComputeHardIron(axes, ellipse);

        var diagnostics = new Dictionary<string, double>
        {
            ["omegaRadPerSecond"] = Math.Abs(omega),
            ["direction"] = Math.Sign(omega),
            ["revolutions"] = revolutions,
            ["samples"] = count,
            ["windowSamples"] = _window.Count,
            ["rejectedFraction"] = rejectedFraction,
            ["spanSeconds"] = span,
            ["sampleRateHz"] = span > 0.0 ? (count - 1) / span : double.NaN,
            ["phaseResidualDeg"] = fit.ResidualStandardDeviation * 180.0 / Math.PI,
            ["phaseRSquared"] = fit.RSquared,
            ["outlierFraction"] = outlierFraction,
            ["effectiveCount"] = fit.EffectiveCount,
            ["residualLag1"] = fit.Lag1ResidualCorrelation,
            ["planeFlatness"] = axes.Flatness,
            ["fieldStrengthMicroTesla"] = Math.Sqrt(Math.Max(0.0, axes.Axes.Value0 + axes.Axes.Value1)),
        };

        if (ellipse is not null)
        {
            diagnostics["ellipseAxisRatio"] = ellipse.AxisRatio;
            diagnostics["ellipseResidualRms"] = ellipse.ResidualRms;
            diagnostics["ellipseAngleDeg"] = ellipse.Angle * 180.0 / Math.PI;
        }

        if (HardIronOffset is { } hardIron)
        {
            diagnostics["hardIronX"] = hardIron.X;
            diagnostics["hardIronY"] = hardIron.Y;
            diagnostics["hardIronZ"] = hardIron.Z;
        }

        if (RotationAxis is { } axis)
        {
            diagnostics["axisX"] = axis.X;
            diagnostics["axisY"] = axis.Y;
            diagnostics["axisZ"] = axis.Z;
        }

        Current = SpeedEstimate.FromRpm(rpm, rpmError, confidence, diagnostics);
    }

    private static (double[] U, double[] V) Project(Vector3Sample[] samples, PrincipalAxesResult axes)
    {
        var us = new double[samples.Length];
        var vs = new double[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            (us[i], vs[i]) = axes.Project(samples[i]);
        }

        return (us, vs);
    }

    /// <summary>
    /// Keep the samples whose normalised radius is near 1, i.e. those still on the fitted
    /// ellipse. Returns null when nothing was rejected, or when so much was rejected that the
    /// ellipse itself is the thing in doubt — in that case the gate is not applied at all and
    /// the poor fit is reported through the diagnostics instead of being papered over.
    /// </summary>
    private Vector3Sample[]? FilterToEllipse(
        Vector3Sample[] samples,
        double[] us,
        double[] vs,
        EllipseFitResult ellipse)
    {
        var kept = new List<Vector3Sample>(samples.Length);
        for (var i = 0; i < samples.Length; i++)
        {
            var (nx, ny) = ellipse.Normalize(us[i], vs[i]);
            var radius = Math.Sqrt(nx * nx + ny * ny);
            if (double.IsFinite(radius) && Math.Abs(radius - 1.0) <= _options.OutlierRadiusTolerance)
            {
                kept.Add(samples[i]);
            }
        }

        if (kept.Count == samples.Length || kept.Count < _options.MinimumSamples)
        {
            return null;
        }

        var rejected = 1.0 - kept.Count / (double)samples.Length;
        return rejected > _options.MaximumRejectedFraction ? null : kept.ToArray();
    }

    private (double X, double Y, double Z)? ComputeHardIron(PrincipalAxesResult axes, EllipseFitResult? ellipse)
    {
        if (ellipse is null)
        {
            return axes.Mean;
        }

        // The centre lives in plane coordinates; lift it back into the phone's axes.
        var u = axes.PlaneU;
        var v = axes.PlaneV;
        return (
            axes.Mean.X + ellipse.CenterX * u.X + ellipse.CenterY * v.X,
            axes.Mean.Y + ellipse.CenterX * u.Y + ellipse.CenterY * v.Y,
            axes.Mean.Z + ellipse.CenterX * u.Z + ellipse.CenterY * v.Z);
    }

    private double ScoreConfidence(
        double rpm,
        double rpmError,
        double revolutions,
        EllipseFitResult? ellipse,
        double flatness,
        double outlierFraction)
    {
        var precision = rpm > 0.0
            ? Clamp01(1.0 - rpmError / rpm / _options.TargetRelativePrecision * 0.5)
            : 0.0;

        // Under ~3 revolutions the phase ramp has not averaged out cogging and eccentricity.
        var coverage = Clamp01(revolutions / 6.0);

        var geometry = ellipse is null
            ? 0.5
            : Clamp01(1.0 - ellipse.ResidualRms / 0.15) * Clamp01(1.0 - (ellipse.AxisRatio - 1.0) / 1.5);

        // A non-flat trajectory means the phone is not rigidly following one axis of rotation.
        var planarity = double.IsFinite(flatness) ? Clamp01(1.0 - flatness / 0.05) : 0.5;

        var cleanliness = double.IsFinite(outlierFraction) ? Clamp01(1.0 - outlierFraction / 0.2) : 0.5;

        // Geometric mean: any single bad factor should drag the score down, not be averaged away.
        var product = Math.Max(1e-9, precision) *
                      Math.Max(1e-9, coverage) *
                      Math.Max(1e-9, geometry) *
                      Math.Max(1e-9, planarity) *
                      Math.Max(1e-9, cleanliness);

        return Clamp01(Math.Pow(product, 1.0 / 5.0));

        static double Clamp01(double x) => double.IsFinite(x) ? Math.Clamp(x, 0.0, 1.0) : 0.0;
    }
}
