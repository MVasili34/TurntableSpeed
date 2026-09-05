using TurntableSpeed.Core.Contracts;
using TurntableSpeed.Core.Dsp;

namespace TurntableSpeed.Core.Estimators;

/// <summary>Measured gyroscope zero offset with its per-axis noise.</summary>
public sealed record GyroBias(
    double X,
    double Y,
    double Z,
    double SigmaX,
    double SigmaY,
    double SigmaZ,
    int Count,
    double SpanSeconds)
{
    public static GyroBias Zero { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);

    public double Magnitude => Math.Sqrt(X * X + Y * Y + Z * Z);

    public double MaxSigma => Math.Max(SigmaX, Math.Max(SigmaY, SigmaZ));

    public Vector3Sample Correct(in Vector3Sample sample) =>
        new(sample.T, sample.X - X, sample.Y - Y, sample.Z - Z);
}

public sealed class GyroZeroCalibratorOptions
{
    /// <summary>How much still data to require. The spec asks the app to insist on 2–3 s.</summary>
    public double RequiredSeconds { get; set; } = 2.5;

    public int MinimumSamples { get; set; } = 40;

    /// <summary>Above this the disc is clearly not stopped, rad/s.</summary>
    public double StillnessRateThreshold { get; set; } = 0.05;

    /// <summary>Above this per-axis σ the phone is being handled, rad/s.</summary>
    public double StillnessNoiseThreshold { get; set; } = 0.03;
}

/// <summary>
/// Averages a stretch of stationary gyroscope data into a zero offset. Also decides whether
/// the data really was stationary — a bias captured while the user was still putting the phone
/// down is worse than no bias at all.
/// </summary>
public sealed class GyroZeroCalibrator
{
    private readonly GyroZeroCalibratorOptions _options;
    private readonly TimeWindow<Vector3Sample> _window;
    private double _lastTimestamp = double.NegativeInfinity;

    public GyroZeroCalibrator(GyroZeroCalibratorOptions? options = null)
    {
        _options = options ?? new GyroZeroCalibratorOptions();
        _window = new TimeWindow<Vector3Sample>(s => s.T, _options.RequiredSeconds, maxCount: 20_000);
    }

    public int SampleCount => _window.Count;

    public double SpanSeconds => _window.Span;

    /// <summary>0..1, how much of the required still period has been collected.</summary>
    public double Progress => Math.Clamp(_window.Span / _options.RequiredSeconds, 0.0, 1.0);

    public bool HasEnoughData =>
        _window.Count >= _options.MinimumSamples && _window.Span >= _options.RequiredSeconds;

    public void Push(Vector3Sample sample)
    {
        if (!sample.IsFinite || sample.T <= _lastTimestamp)
        {
            return;
        }

        _lastTimestamp = sample.T;
        _window.Add(sample);
    }

    public void Reset()
    {
        _window.Clear();
        _lastTimestamp = double.NegativeInfinity;
    }

    /// <summary>Bias over the collected window, or null when there is not enough data yet.</summary>
    public GyroBias? Compute()
    {
        if (!HasEnoughData)
        {
            return null;
        }

        var xs = _window.Select(s => s.X);
        var ys = _window.Select(s => s.Y);
        var zs = _window.Select(s => s.Z);

        return new GyroBias(
            SeriesMath.Mean(xs),
            SeriesMath.Mean(ys),
            SeriesMath.Mean(zs),
            SeriesMath.StandardDeviation(xs),
            SeriesMath.StandardDeviation(ys),
            SeriesMath.StandardDeviation(zs),
            _window.Count,
            _window.Span);
    }

    /// <summary>
    /// True when the collected data looks genuinely motionless: small mean rate and small
    /// spread. False means the UI should ask the user to stop the platter and try again.
    /// </summary>
    public bool IsStill(out string reason)
    {
        var bias = Compute();
        if (bias is null)
        {
            reason = "not enough data";
            return false;
        }

        if (bias.Magnitude > _options.StillnessRateThreshold)
        {
            reason = $"platter still turning ({bias.Magnitude:F3} rad/s)";
            return false;
        }

        if (bias.MaxSigma > _options.StillnessNoiseThreshold)
        {
            reason = $"phone is being handled (σ = {bias.MaxSigma:F3} rad/s)";
            return false;
        }

        reason = "ok";
        return true;
    }
}
