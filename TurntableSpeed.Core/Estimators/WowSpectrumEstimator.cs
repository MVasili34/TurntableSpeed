using TurntableSpeed.Core.Contracts;
using TurntableSpeed.Core.Dsp;

namespace TurntableSpeed.Core.Estimators;

/// <summary>Outcome of the long acoustic wow analysis.</summary>
/// <param name="Speed">
/// Speed implied by the once-per-revolution modulation peak. Coarse compared with the click
/// estimator — the frequency resolution is the limit — but derived from an entirely different
/// mechanism, which is what makes it worth comparing.
/// </param>
/// <param name="WowFlutter">Modulation spectrum in percent of nominal speed.</param>
/// <param name="Progress">0..1 toward the recommended capture length.</param>
public sealed record WowSpectrumResult(
    SpeedEstimate? Speed,
    WowFlutterResult WowFlutter,
    double PeakFrequencyHz,
    double AnalyzedSeconds,
    double ResolutionHz,
    double Progress)
{
    public static WowSpectrumResult Empty { get; } =
        new(null, WowFlutterResult.Empty, double.NaN, 0.0, double.NaN, 0.0);

    public bool HasResult => Speed is not null;
}

public sealed class WowSpectrumEstimatorOptions
{
    /// <summary>History retained, seconds. The spec asks for a 2–3 minute capture.</summary>
    public double WindowSeconds { get; set; } = 210.0;

    /// <summary>Nothing is reported before this much data: the peak would not be resolved.</summary>
    public double MinimumSeconds { get; set; } = 60.0;

    /// <summary>Capture length the progress indicator counts toward. 3 min ≈ 0.006 Hz resolution.</summary>
    public double TargetSeconds { get; set; } = 180.0;

    /// <summary>Uniform grid the cents series is resampled onto, Hz.</summary>
    public double AnalysisRateHz { get; set; } = 20.0;

    /// <summary>Band searched for the rotation peak: 0.5556 Hz (33⅓) … 1.3 Hz (78).</summary>
    public double RotationBandLowHz { get; set; } = 0.3;

    public double RotationBandHighHz { get; set; } = 2.0;

    /// <summary>Band summed into the reported wow &amp; flutter figure.</summary>
    public double WowBandLowHz { get; set; } = 0.2;

    public double WowBandHighHz { get; set; } = 10.0;

    /// <summary>Frames below this concentration are dropped before resampling.</summary>
    public double MinimumFrameResultant { get; set; } = 0.15;

    public int RecomputeEveryFrames { get; set; } = 100;
}

/// <summary>
/// The long acoustic mode (spec §4.4): take the pitch-deviation series from
/// <see cref="PitchGridEstimator"/> over two or three minutes, de-trend it and transform it.
/// Eccentricity of the pressing and of the spindle hole modulate the pitch exactly once per
/// revolution, so the spectrum carries a sharp line at the rotation frequency — 0.5556 Hz at
/// 33⅓, 0.75 Hz at 45. Not a real-time mode: it is the "put the phone down and leave it" one.
/// </summary>
/// <remarks>
/// The pitch series it feeds on inherits <see cref="PitchGridEstimator.Caveat"/> for its
/// <em>absolute</em> value, but not for this analysis: a constant tuning offset is exactly what
/// de-trending removes, and a modulation frequency does not care what the record was tuned to.
/// </remarks>
public sealed class WowSpectrumEstimator
{
    private readonly WowSpectrumEstimatorOptions _options;
    private readonly List<PitchFrame> _sink = new(64);
    private readonly TimeWindow<TimedValue> _cents;
    private int _sinceRecompute;

    /// <param name="pitch">
    /// Pitch estimator to feed on. When null a private one is created and driven by
    /// <see cref="Push"/>; when supplied, the caller drives it and this class only collects.
    /// </param>
    public WowSpectrumEstimator(
        PitchGridEstimator? pitch = null,
        WowSpectrumEstimatorOptions? options = null)
    {
        _options = options ?? new WowSpectrumEstimatorOptions();
        OwnsPitchEstimator = pitch is null;
        Pitch = pitch ?? new PitchGridEstimator();
        Pitch.FrameSink = _sink;

        _cents = new TimeWindow<TimedValue>(
            v => v.T,
            _options.WindowSeconds,
            maxCount: (int)(_options.WindowSeconds * _options.AnalysisRateHz) + 512);
    }

    public PitchGridEstimator Pitch { get; }

    /// <summary>False when the pitch estimator is shared and driven by someone else.</summary>
    public bool OwnsPitchEstimator { get; }

    public WowSpectrumResult Result { get; private set; } = WowSpectrumResult.Empty;

    public SpeedEstimate? Current => Result.Speed;

    public double AnalyzedSeconds => _cents.Span;

    public double Progress => Math.Clamp(_cents.Span / _options.TargetSeconds, 0.0, 1.0);

    public void Push(AudioBlock block)
    {
        if (OwnsPitchEstimator)
        {
            Pitch.Push(block);
        }

        Collect();
    }

    /// <summary>
    /// Drain whatever the shared pitch estimator has produced since the last call. Call this
    /// after pushing a block into an externally owned <see cref="Pitch"/>.
    /// </summary>
    public void Collect()
    {
        if (_sink.Count == 0)
        {
            return;
        }

        var accepted = 0;
        foreach (var frame in _sink)
        {
            if (!double.IsFinite(frame.Cents) || frame.Resultant < _options.MinimumFrameResultant)
            {
                continue;
            }

            _cents.Add(new TimedValue(frame.T, frame.Cents));
            accepted++;
        }

        _sink.Clear();

        _sinceRecompute += accepted;
        if (_sinceRecompute < _options.RecomputeEveryFrames)
        {
            return;
        }

        _sinceRecompute = 0;
        Recompute();
    }

    /// <summary>Feed the cents series directly — used when replaying a recorded analysis.</summary>
    public void PushCents(double t, double cents)
    {
        if (!double.IsFinite(t) || !double.IsFinite(cents))
        {
            return;
        }

        _cents.Add(new TimedValue(t, cents));
        if (++_sinceRecompute < _options.RecomputeEveryFrames)
        {
            return;
        }

        _sinceRecompute = 0;
        Recompute();
    }

    public void Flush()
    {
        Collect();
        _sinceRecompute = 0;
        Recompute();
    }

    public void Reset()
    {
        _sink.Clear();
        _cents.Clear();
        _sinceRecompute = 0;
        Result = WowSpectrumResult.Empty;
        if (OwnsPitchEstimator)
        {
            Pitch.Reset();
        }
    }

    private void Recompute()
    {
        var span = _cents.Span;
        if (_cents.Count < 64 || span < _options.MinimumSeconds)
        {
            Result = WowSpectrumResult.Empty with
            {
                AnalyzedSeconds = span,
                Progress = Progress,
            };
            return;
        }

        var times = _cents.Select(v => v.T);
        var values = _cents.Select(v => v.Value);

        // Cents come back folded into ±50; a slow drift across the fold would otherwise appear
        // as a 100-cent step. Unwrap over the semitone period before anything else.
        var scale = PhaseMath.TwoPi / 100.0;
        var angles = new double[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            angles[i] = values[i] * scale;
        }

        var unwrapped = PhaseMath.Unwrap(angles);
        for (var i = 0; i < unwrapped.Length; i++)
        {
            unwrapped[i] /= scale;
        }

        var series = UniformResampler.Resample(times, unwrapped, 1.0 / _options.AnalysisRateHz);
        if (series.Count < 64)
        {
            Result = WowSpectrumResult.Empty with { AnalyzedSeconds = span, Progress = Progress };
            return;
        }

        // De-trend in cents: this is what removes the record's own tuning offset and any slow
        // speed drift, leaving the periodic modulation the analysis is after.
        var detrended = series.Values;
        SeriesMath.DetrendLinearInPlace(detrended);

        var percent = new double[detrended.Length];
        for (var i = 0; i < percent.Length; i++)
        {
            percent[i] = SpeedMath.PercentFromCents(detrended[i]);
        }

        var spectrum = Spectrum.FromSeries(
            new UniformSeries(series.StartTime, series.SampleInterval, percent),
            detrend: false);

        var resolution = spectrum.Resolution;
        if (spectrum.Count < 8 || !double.IsFinite(resolution) || resolution <= 0.0)
        {
            Result = WowSpectrumResult.Empty with { AnalyzedSeconds = span, Progress = Progress };
            return;
        }

        var wowHigh = Math.Min(_options.WowBandHighHz, _options.AnalysisRateHz / 2.0 * 0.9);
        var wowRms = spectrum.BandRms(_options.WowBandLowHz, wowHigh);

        var from = Math.Max(1, (int)Math.Floor(_options.RotationBandLowHz / resolution));
        var to = Math.Min(spectrum.Count - 2, (int)Math.Ceiling(_options.RotationBandHighHz / resolution));
        if (from >= to)
        {
            Result = WowSpectrumResult.Empty with { AnalyzedSeconds = span, Progress = Progress };
            return;
        }

        var (peakIndex, peak) = PeakInterpolation.RefineMaximum(spectrum.Amplitudes, from, to);
        if (double.IsNaN(peakIndex) || !(peak.Value > 0.0))
        {
            Result = WowSpectrumResult.Empty with { AnalyzedSeconds = span, Progress = Progress };
            return;
        }

        var peakFrequency = peakIndex * resolution;
        var exclusion = Math.Max(2, (int)Math.Round(0.05 / resolution));
        var noiseFloor = BandNoiseFloor(spectrum.Amplitudes, from, to, (int)Math.Round(peakIndex), exclusion);

        var wowFlutter = new WowFlutterResult(wowRms, peak.Value, peakFrequency, spectrum);

        var rpm = peakFrequency * 60.0;
        var frequencyError = FrequencyUncertainty(peak, noiseFloor, resolution);
        var rpmError = frequencyError * 60.0;

        var peakToNoise = noiseFloor > 0.0 ? peak.Value / noiseFloor : double.NaN;
        var confidence = ScoreConfidence(rpm, rpmError, span, peakToNoise);

        var diagnostics = new Dictionary<string, double>
        {
            ["rotationFrequencyHz"] = peakFrequency,
            ["frequencyErrorHz"] = frequencyError,
            ["resolutionHz"] = resolution,
            ["spanSeconds"] = span,
            ["frames"] = _cents.Count,
            ["peakAmplitudePercent"] = peak.Value,
            ["peakToNoise"] = peakToNoise,
            ["wowRmsPercent"] = wowRms,
            ["analysisRateHz"] = _options.AnalysisRateHz,
        };

        Result = new WowSpectrumResult(
            SpeedEstimate.FromRpm(rpm, rpmError, confidence, diagnostics),
            wowFlutter,
            peakFrequency,
            span,
            resolution,
            Progress);
    }

    /// <summary>
    /// Frequency uncertainty of an interpolated spectral peak: the parabolic vertex moves by
    /// about ε/(2c) for a perturbation ε, and the in-band scatter is that perturbation. Floored
    /// at a twentieth of a bin, which is roughly where interpolation stops being meaningful.
    /// </summary>
    private static double FrequencyUncertainty(ParabolicPeak peak, double noiseFloor, double resolution)
    {
        if (!(peak.Curvature > 0.0) || !double.IsFinite(noiseFloor) || !(noiseFloor > 0.0))
        {
            return resolution * 0.5;
        }

        var bins = noiseFloor / (2.0 * peak.Curvature);
        var hertz = bins * resolution;
        return double.IsFinite(hertz)
            ? Math.Clamp(hertz, resolution * 0.05, resolution * 4.0)
            : resolution * 0.5;
    }

    private static double BandNoiseFloor(
        ReadOnlySpan<double> amplitudes,
        int from,
        int to,
        int peakIndex,
        int exclusionRadius)
    {
        var values = new List<double>(Math.Max(0, to - from + 1));
        for (var i = from; i <= to; i++)
        {
            if (Math.Abs(i - peakIndex) <= exclusionRadius)
            {
                continue;
            }

            values.Add(amplitudes[i]);
        }

        return values.Count >= 8 ? SeriesMath.StandardDeviation(values.ToArray()) : double.NaN;
    }

    private double ScoreConfidence(double rpm, double rpmError, double span, double peakToNoise)
    {
        var precision = rpm > 0.0 ? Clamp01(1.0 - rpmError / rpm / 0.01) : 0.0;
        var prominence = double.IsFinite(peakToNoise) ? Clamp01((peakToNoise - 4.0) / 8.0) : 0.3;
        var coverage = Clamp01(span / _options.TargetSeconds);

        var product = Math.Max(1e-9, precision) *
                      Math.Max(1e-9, prominence) *
                      Math.Max(1e-9, coverage);

        return Clamp01(Math.Pow(product, 1.0 / 3.0));

        static double Clamp01(double x) => double.IsFinite(x) ? Math.Clamp(x, 0.0, 1.0) : 0.0;
    }
}
