namespace TurntableSpeed.Core.Tests;

/// <summary>
/// Every numeric tolerance in the suite, in one place, as the spec requires (§7.1). A test that
/// wants a looser bound than these must say so at its call site with a reason — silently
/// relaxing a constant here would hide exactly the regression the suite exists to catch.
/// </summary>
public static class Tolerances
{
    // ---- DSP primitives: these are arithmetic, not estimation, so the bounds are tight. ----

    /// <summary>Round-trip and analytic-spectrum agreement for the FFT.</summary>
    public const double Fft = 1e-9;

    /// <summary>Phase unwrapping is exact up to floating-point noise.</summary>
    public const double Phase = 1e-12;

    /// <summary>Parabolic vertex recovery from three exact samples.</summary>
    public const double PeakVertex = 1e-9;

    /// <summary>Ellipse parameters recovered from noiseless points on a known ellipse.</summary>
    public const double EllipseParameter = 1e-6;

    /// <summary>Ellipse parameters recovered from points with 1% noise, relative.</summary>
    public const double EllipseParameterNoisy = 0.03;

    /// <summary>Filter magnitude response measured by sine injection, in dB.</summary>
    public const double FilterResponseDb = 0.35;

    /// <summary>Circular mean of exactly-placed angles.</summary>
    public const double CircularMean = 1e-9;

    /// <summary>Statistics helpers against naive reference implementations.</summary>
    public const double SeriesMath = 1e-9;

    /// <summary>Linear resampling of a linear function is exact.</summary>
    public const double Resampling = 1e-9;

    /// <summary>Bandlimited resampling: frequency of the resampled sine, relative.</summary>
    public const double ResamplingFrequency = 0.002;

    // ---- Estimators: relative error in percent of the true speed. ----

    /// <summary>
    /// Spec §3.2: "over 60 seconds (≈33 revolutions) the method must give better than 0.05%".
    /// This is the headline requirement of the sensor mode and is asserted directly.
    /// </summary>
    public const double MagnetometerRpmPercent60s = 0.05;

    /// <summary>Shorter magnetometer windows, where fewer revolutions have been averaged.</summary>
    public const double MagnetometerRpmPercentShort = 0.20;

    /// <summary>
    /// The gyroscope is not trusted in absolute terms (§3.1); this bounds it only after the
    /// scale factor has been calibrated away.
    /// </summary>
    public const double GyroscopeRpmPercentCalibrated = 0.30;

    /// <summary>Uncalibrated gyroscope, where a 1–3% factory scale error is expected.</summary>
    public const double GyroscopeRpmPercentUncalibrated = 3.5;

    /// <summary>Spec §1: the acoustic target is ±0.3% for capture through the air.</summary>
    public const double ClickRpmPercent = 0.30;

    /// <summary>Pitch-grid deviation, in cents. Bin interpolation limits this, not statistics.</summary>
    public const double PitchCents = 3.0;

    /// <summary>Rotation frequency recovered from the long wow spectrum, relative.</summary>
    public const double WowFrequencyPercent = 0.50;

    /// <summary>Wow &amp; flutter depth recovered from a known modulation, relative.</summary>
    public const double WowDepthRelative = 0.15;

    /// <summary>Gyroscope zero offset recovered from a still recording, rad/s.</summary>
    public const double GyroBias = 5e-4;

    /// <summary>Scale factor recovered by fusing gyroscope against magnetometer, relative.</summary>
    public const double GyroScaleFactor = 0.01;

    /// <summary>Tilt angle from the accelerometer, degrees.</summary>
    public const double TiltDegrees = 0.5;

    // ---- Uncertainty calibration: the spec insists every number carries an interval. ----

    /// <summary>
    /// How many standard errors the truth is allowed to sit from the estimate. An estimator
    /// whose interval is optimistic is worse than one that is merely imprecise, so this is a
    /// first-class assertion rather than a nicety. Used by fixed-seed tests, where the draw is
    /// the same every run and 3σ is a genuine bound rather than a coin toss.
    /// </summary>
    public const double MaxSigmaFromTruth = 3.0;

    /// <summary>
    /// The same bound for tests that draw their inputs at random.
    /// <para>
    /// A per-draw 3σ limit is the wrong instrument here: with a perfectly calibrated interval,
    /// 40 independent draws exceed 3σ at least once about 10% of the time, so the test would
    /// fail one run in ten while nothing was wrong. Four sigma brings that to roughly a quarter
    /// of a percent. Detecting a <em>systematically</em> optimistic interval is not this
    /// assertion's job — that is what the calibration tests in
    /// <c>LineFitTests</c> and <c>IntervalCalibrationTests</c> are for, and they measure the
    /// spread over many draws instead of watching for one unlucky outlier.
    /// </para>
    /// </summary>
    public const double MaxSigmaFromTruthRandomized = 4.0;
}
