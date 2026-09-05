using TurntableSpeed.Core.Contracts;
using TurntableSpeed.Core.Dsp;

namespace TurntableSpeed.Core.Estimators;

public sealed class GyroscopeSpeedEstimatorOptions
{
    public double WindowSeconds { get; set; } = 30.0;

    public int MaxSamples { get; set; } = 20_000;

    /// <summary>Averaging length behind <see cref="GyroscopeSpeedEstimator.InstantaneousRadPerSecond"/>.</summary>
    public double SmoothingSeconds { get; set; } = 1.0;

    public double MinimumSpanSeconds { get; set; } = 1.0;

    public int MinimumSamples { get; set; } = 50;

    public int RecomputeEverySamples { get; set; } = 20;

    /// <summary>
    /// Assumed 1σ scale-factor error of an uncalibrated MEMS gyroscope. Datasheets quote 1–3%,
    /// which dwarfs the deviation being measured, so it is carried straight into the reported
    /// standard error until the magnetometer has calibrated it away.
    /// </summary>
    public double UncalibratedScaleUncertainty { get; set; } = 0.02;

    /// <summary>Grid used for the wow spectrum, Hz. Also the trace rate for spin-up detection.</summary>
    public double AnalysisRateHz { get; set; } = 100.0;

    /// <summary>How long a spin-up can take before the app stops looking for it.</summary>
    public double SpinUpTraceSeconds { get; set; } = 120.0;

    /// <summary>Speed is "on nominal" once it stays inside this band, in percent.</summary>
    public double SettleTolerancePercent { get; set; } = 1.0;
}

/// <summary>
/// The secondary sensor estimator: high rate, instantaneous, good at transients and wow &amp;
/// flutter — and untrustworthy in absolute terms. Its scale factor is set from the outside
/// (by <see cref="SensorFusionEstimator"/>, against the magnetometer); until then the reported
/// standard error includes the full factory scale-factor uncertainty.
/// </summary>
public sealed class GyroscopeSpeedEstimator : ISpeedEstimator<Vector3Sample>
{
    private readonly GyroscopeSpeedEstimatorOptions _options;
    private readonly TimeWindow<Vector3Sample> _window;
    private readonly TimeWindow<TimedValue> _spinUpTrace;

    private int _sinceRecompute;
    private double _lastTimestamp = double.NegativeInfinity;
    private double _firstTimestamp = double.NaN;
    private double _ema = double.NaN;
    private double _lastTraceTime = double.NegativeInfinity;

    public GyroscopeSpeedEstimator(GyroscopeSpeedEstimatorOptions? options = null)
    {
        _options = options ?? new GyroscopeSpeedEstimatorOptions();
        _window = new TimeWindow<Vector3Sample>(s => s.T, _options.WindowSeconds, _options.MaxSamples);
        _spinUpTrace = new TimeWindow<TimedValue>(
            v => v.T,
            _options.SpinUpTraceSeconds,
            maxCount: (int)(_options.SpinUpTraceSeconds * 25) + 64);
    }

    public SpeedEstimate? Current { get; private set; }

    /// <summary>Zero offset subtracted from every incoming sample.</summary>
    public GyroBias Bias { get; set; } = GyroBias.Zero;

    /// <summary>
    /// Multiplicative correction for the gyroscope's scale-factor error. 1.0 until the
    /// magnetometer has provided a reference.
    /// </summary>
    public double ScaleFactor { get; private set; } = 1.0;

    public bool IsScaleCalibrated { get; private set; }

    /// <summary>Smoothed instantaneous rate, rad/s, scale correction applied.</summary>
    public double InstantaneousRadPerSecond { get; private set; } = double.NaN;

    /// <summary>Signed rate about the fitted rotation axis, rad/s.</summary>
    public double SignedRadiansPerSecond { get; private set; } = double.NaN;

    public (double X, double Y, double Z)? RotationAxis { get; private set; }

    public WowFlutterResult WowFlutter { get; private set; } = Dsp.WowFlutterResult.Empty;

    /// <summary>
    /// Seconds from the first sample until the speed settled inside the tolerance band, or
    /// null when the recording did not capture a spin-up.
    /// </summary>
    public double? SpinUpSeconds { get; private set; }

    public int SampleCount => _window.Count;

    public double WindowSpanSeconds => _window.Span;

    /// <summary>Apply a scale factor measured against the magnetometer.</summary>
    public void ApplyScaleCalibration(double scaleFactor)
    {
        if (!double.IsFinite(scaleFactor) || scaleFactor <= 0.0)
        {
            return;
        }

        ScaleFactor = scaleFactor;
        IsScaleCalibrated = true;
    }

    public void ClearScaleCalibration()
    {
        ScaleFactor = 1.0;
        IsScaleCalibrated = false;
    }

    public void Push(Vector3Sample sample)
    {
        if (!sample.IsFinite || sample.T <= _lastTimestamp)
        {
            return;
        }

        var previousTimestamp = _lastTimestamp;
        _lastTimestamp = sample.T;
        if (double.IsNaN(_firstTimestamp))
        {
            _firstTimestamp = sample.T;
        }

        var corrected = Bias.Correct(sample);
        _window.Add(corrected);
        UpdateSpinUpTrace(corrected, previousTimestamp);

        if (++_sinceRecompute < _options.RecomputeEverySamples)
        {
            return;
        }

        _sinceRecompute = 0;
        Recompute();
    }

    public void Flush()
    {
        _sinceRecompute = 0;
        Recompute();
    }

    public void Reset()
    {
        _window.Clear();
        _spinUpTrace.Clear();
        Current = null;
        InstantaneousRadPerSecond = double.NaN;
        SignedRadiansPerSecond = double.NaN;
        RotationAxis = null;
        WowFlutter = Dsp.WowFlutterResult.Empty;
        SpinUpSeconds = null;
        _sinceRecompute = 0;
        _lastTimestamp = double.NegativeInfinity;
        _firstTimestamp = double.NaN;
        _ema = double.NaN;
        _lastTraceTime = double.NegativeInfinity;
    }

    private void UpdateSpinUpTrace(in Vector3Sample corrected, double previousTimestamp)
    {
        var magnitude = corrected.Magnitude;
        if (double.IsNaN(_ema))
        {
            _ema = magnitude;
        }
        else
        {
            var dt = corrected.T - previousTimestamp;
            const double tau = 0.2;
            var alpha = dt > 0.0 ? 1.0 - Math.Exp(-dt / tau) : 1.0;
            _ema += alpha * (magnitude - _ema);
        }

        // Downsample the trace: spin-up resolution of 50 ms is plenty.
        if (corrected.T - _lastTraceTime < 0.05)
        {
            return;
        }

        _lastTraceTime = corrected.T;
        _spinUpTrace.Add(new TimedValue(corrected.T, _ema));
    }

    private void Recompute()
    {
        var count = _window.Count;
        if (count < _options.MinimumSamples || _window.Span < _options.MinimumSpanSeconds)
        {
            return;
        }

        var samples = _window.ToArray();
        var (ax, ay, az, _) = PrincipalAxes.MeanDirection(samples);

        var times = new double[count];
        var projected = new double[count];
        var magnitudes = new double[count];
        var t0 = samples[0].T;
        for (var i = 0; i < count; i++)
        {
            times[i] = samples[i].T - t0;
            projected[i] = samples[i].Dot(ax, ay, az);
            magnitudes[i] = samples[i].Magnitude;
        }

        var mean = SeriesMath.Mean(projected);
        var sd = SeriesMath.StandardDeviation(projected);
        if (!double.IsFinite(mean) || Math.Abs(mean) < 1e-9)
        {
            return;
        }

        // Wow, cogging and eccentricity make consecutive samples correlated; without this the
        // interval would shrink like 1/√n on data that is nowhere near independent.
        var lag1 = SeriesMath.Lag1Correlation(projected);
        var effectiveCount = lag1 > 0.0
            ? Math.Max(3.0, count * (1.0 - lag1) / (1.0 + lag1))
            : count;
        var statisticalError = sd / Math.Sqrt(effectiveCount);

        var scaled = mean * ScaleFactor;
        var rpm = SpeedMath.RpmFromRadiansPerSecond(Math.Abs(scaled));
        var statisticalRpmError = SpeedMath.RpmFromRadiansPerSecond(statisticalError * ScaleFactor);

        // The systematic that dominates everything else until calibration.
        var scaleError = IsScaleCalibrated ? 0.0 : rpm * _options.UncalibratedScaleUncertainty;
        var rpmError = Math.Sqrt(statisticalRpmError * statisticalRpmError + scaleError * scaleError);

        SignedRadiansPerSecond = scaled;
        RotationAxis = (ax, ay, az);
        InstantaneousRadPerSecond = ComputeInstantaneous(samples, ax, ay, az) * ScaleFactor;

        var uniform = UniformResampler.Resample(times, projected, 1.0 / _options.AnalysisRateHz);
        WowFlutter = uniform.Count >= 64 ? Dsp.WowFlutter.Analyze(uniform) : Dsp.WowFlutterResult.Empty;

        SpinUpSeconds = ComputeSpinUp(Math.Abs(mean));

        var confidence = ScoreConfidence(rpm, rpmError, sd, Math.Abs(mean), effectiveCount);

        var diagnostics = new Dictionary<string, double>
        {
            ["omegaRadPerSecond"] = Math.Abs(scaled),
            ["omegaRawRadPerSecond"] = Math.Abs(mean),
            ["magnitudeMeanRadPerSecond"] = SeriesMath.Mean(magnitudes),
            ["direction"] = Math.Sign(mean),
            ["scaleFactor"] = ScaleFactor,
            ["scaleCalibrated"] = IsScaleCalibrated ? 1.0 : 0.0,
            ["scaleUncertaintyRpm"] = scaleError,
            ["statisticalErrorRpm"] = statisticalRpmError,
            ["samples"] = count,
            ["spanSeconds"] = _window.Span,
            ["sampleRateHz"] = _window.Span > 0.0 ? (count - 1) / _window.Span : double.NaN,
            ["rateStdDev"] = sd,
            ["effectiveCount"] = effectiveCount,
            ["residualLag1"] = lag1,
            ["axisX"] = ax,
            ["axisY"] = ay,
            ["axisZ"] = az,
            ["biasMagnitude"] = Bias.Magnitude,
            ["instantaneousRadPerSecond"] = InstantaneousRadPerSecond,
        };

        if (double.IsFinite(WowFlutter.RmsPercent))
        {
            diagnostics["wowRmsPercent"] = WowFlutter.RmsPercent;
            diagnostics["wowPeakPercent"] = WowFlutter.PeakPercent;
            diagnostics["wowDominantHz"] = WowFlutter.DominantFrequency;
        }

        if (SpinUpSeconds is { } spinUp)
        {
            diagnostics["spinUpSeconds"] = spinUp;
        }

        Current = SpeedEstimate.FromRpm(rpm, rpmError, confidence, diagnostics);
    }

    private double ComputeInstantaneous(Vector3Sample[] samples, double ax, double ay, double az)
    {
        var cutoff = samples[^1].T - _options.SmoothingSeconds;
        var sum = 0.0;
        var used = 0;
        for (var i = samples.Length - 1; i >= 0; i--)
        {
            if (samples[i].T < cutoff)
            {
                break;
            }

            sum += samples[i].Dot(ax, ay, az);
            used++;
        }

        return used > 0 ? sum / used : double.NaN;
    }

    /// <summary>
    /// Time from the first sample until the rate entered the tolerance band around the plateau
    /// and never left it again. Null when the recording started with the platter already up to
    /// speed, because then there is nothing to report.
    /// </summary>
    private double? ComputeSpinUp(double plateau)
    {
        if (_spinUpTrace.Count < 4 || !(plateau > 0.0) || double.IsNaN(_firstTimestamp))
        {
            return null;
        }

        var trace = _spinUpTrace.ToArray();

        // Only meaningful if we actually saw the platter start from rest.
        if (trace[0].Value > 0.5 * plateau)
        {
            return null;
        }

        var tolerance = plateau * _options.SettleTolerancePercent / 100.0;
        var lastOutside = -1;
        for (var i = 0; i < trace.Length; i++)
        {
            if (Math.Abs(trace[i].Value - plateau) > tolerance)
            {
                lastOutside = i;
            }
        }

        if (lastOutside < 0)
        {
            return 0.0;
        }

        if (lastOutside >= trace.Length - 1)
        {
            // Still spinning up.
            return null;
        }

        return trace[lastOutside + 1].T - _firstTimestamp;
    }

    private double ScoreConfidence(double rpm, double rpmError, double sd, double mean, double effectiveCount)
    {
        // Statistical precision only; the scale-factor systematic is already inside rpmError.
        var precision = rpm > 0.0 ? Clamp01(1.0 - rpmError / rpm / 0.01) : 0.0;
        var steadiness = mean > 0.0 ? Clamp01(1.0 - sd / mean / 0.10) : 0.0;
        var coverage = Clamp01(effectiveCount / 500.0);

        // Hard ceiling while uncalibrated: an absolute reading from a raw MEMS gyroscope does
        // not deserve high confidence no matter how quiet the data looks.
        var ceiling = IsScaleCalibrated ? 1.0 : 0.45;

        var product = Math.Max(1e-9, precision) * Math.Max(1e-9, steadiness) * Math.Max(1e-9, coverage);
        return Clamp01(Math.Pow(product, 1.0 / 3.0)) * ceiling;

        static double Clamp01(double x) => double.IsFinite(x) ? Math.Clamp(x, 0.0, 1.0) : 0.0;
    }
}
