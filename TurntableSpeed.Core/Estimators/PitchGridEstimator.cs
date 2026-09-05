using TurntableSpeed.Core.Contracts;
using TurntableSpeed.Core.Dsp;

namespace TurntableSpeed.Core.Estimators;

/// <summary>One analysis frame's verdict on where the music sits relative to the semitone grid.</summary>
/// <param name="T">Centre time of the frame, seconds.</param>
/// <param name="Cents">Deviation from equal temperament, folded into (−50, +50].</param>
/// <param name="Resultant">
/// 0..1 concentration of the circular mean. Near 0 means the frame had no tonal content and
/// its <paramref name="Cents"/> is meaningless.
/// </param>
/// <param name="Energy">RMS of the frame, for weighting and for the level display.</param>
public readonly record struct PitchFrame(double T, double Cents, double Resultant, double Energy);

public sealed class PitchGridEstimatorOptions
{
    /// <summary>FFT length. Must be a power of two. 4096 @ 48 kHz ≈ 85 ms.</summary>
    public int FrameSize { get; set; } = 4096;

    /// <summary>Analysis hop, seconds. The spec asks for ~50 ms.</summary>
    public double HopSeconds { get; set; } = 0.05;

    /// <summary>Below this the bin spacing is too coarse for the peak interpolation to mean much.</summary>
    public double MinFrequency { get; set; } = 100.0;

    public double MaxFrequency { get; set; } = 4000.0;

    /// <summary>Reference pitch of the equal-tempered grid, Hz.</summary>
    public double ReferencePitch { get; set; } = 440.0;

    /// <summary>Sliding average length over frames, seconds.</summary>
    public double WindowSeconds { get; set; } = 6.0;

    public int MinimumFrames { get; set; } = 20;

    public int RecomputeEveryFrames { get; set; } = 4;

    /// <summary>Spectral peaks weaker than this fraction of the frame maximum are ignored.</summary>
    public double PeakThresholdRelative { get; set; } = 0.02;

    public int MaxPeaksPerFrame { get; set; } = 40;

    /// <summary>Frames below this concentration contribute nothing to the average.</summary>
    public double MinimumFrameResultant { get; set; } = 0.15;

    /// <summary>
    /// The unknown that this method cannot escape: the record's own tuning. Bands played sharp,
    /// tape machines ran off, masterings drifted. Carried as a systematic in the reported
    /// standard error so that inverse-variance fusion weights this estimator honestly against
    /// the click-periodicity one instead of letting a tight statistical interval win.
    /// </summary>
    public double RecordTuningUncertaintyCents { get; set; } = 15.0;

    /// <summary>Ceiling on the confidence this estimator may claim, for the same reason.</summary>
    public double ConfidenceCeiling { get; set; } = 0.5;
}

/// <summary>
/// The secondary acoustic estimator (spec §4.3): fold the spectrum onto the twelve pitch
/// classes and read how far the music sits from equal temperament. Fast — an indication within
/// seconds — but it measures the pitch of what was pressed onto the record, not the speed of
/// the platter alone.
/// </summary>
/// <remarks>
/// <see cref="Caveat"/> is not decoration. This estimator cannot separate a turntable running
/// 1% fast from a band that tuned 17 cents sharp, and the UI is required to say so.
/// </remarks>
public sealed class PitchGridEstimator : ISpeedEstimator<AudioBlock>
{
    /// <summary>The warning the UI must show next to any number this estimator produces.</summary>
    public const string Caveat =
        "Pitch deviation mixes the turntable's speed error with the record's own tuning: " +
        "the band may not have played at A=440, and the mastering may have drifted.";

    private const double SemitoneCents = 100.0;

    private readonly PitchGridEstimatorOptions _options;
    private readonly TimeWindow<PitchFrame> _frames;
    private readonly double[] _ring;
    private readonly double[] _window;
    private readonly double _windowGain;

    private int _sampleRate;
    private int _hopSamples;
    private int _write;
    private long _totalSamples;
    private int _sinceHop;
    private int _sinceRecompute;
    private double _lastBlockEnd = double.NegativeInfinity;

    public PitchGridEstimator(PitchGridEstimatorOptions? options = null)
    {
        _options = options ?? new PitchGridEstimatorOptions();

        if (!Fft.IsPowerOfTwo(_options.FrameSize) || _options.FrameSize < 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "FrameSize must be a power of two of at least 256.");
        }

        _ring = new double[_options.FrameSize];
        _window = WindowFunctions.Create(WindowFunctions.Kind.Hann, _options.FrameSize);
        _windowGain = WindowFunctions.CoherentGain(_window);

        var frameRate = 1.0 / Math.Max(1e-3, _options.HopSeconds);
        _frames = new TimeWindow<PitchFrame>(
            f => f.T,
            _options.WindowSeconds,
            maxCount: (int)(_options.WindowSeconds * frameRate) + 64);
    }

    public SpeedEstimate? Current { get; private set; }

    /// <summary>
    /// Averaged deviation from equal temperament, cents, folded into (−50, +50]. Available
    /// even when <see cref="AssumedNominal"/> is unset — this is the instantaneous indication.
    /// </summary>
    public double DeviationCents { get; private set; } = double.NaN;

    /// <summary>1σ of <see cref="DeviationCents"/> including the record-tuning systematic.</summary>
    public double DeviationCentsStandardError { get; private set; } = double.NaN;

    /// <summary>Concentration of the averaged estimate, 0..1.</summary>
    public double Resultant { get; private set; } = double.NaN;

    /// <summary>
    /// Nominal speed the deviation is applied to. Pitch alone cannot tell 33⅓ from 45, so
    /// without this there is no rpm to report and <see cref="Current"/> stays null. Normally
    /// set from the click-periodicity estimator, or by the user.
    /// </summary>
    public NominalSpeed? AssumedNominal { get; set; }

    /// <summary>Optional sink receiving every analysis frame — how the wow spectrum is fed.</summary>
    public ICollection<PitchFrame>? FrameSink { get; set; }

    /// <summary>Frames retained in the sliding window, oldest first.</summary>
    public IReadOnlyList<PitchFrame> Frames => _frames;

    public long FrameCount => _frames.TotalAdded;

    public double WindowSpanSeconds => _frames.Span;

    /// <summary>Latest amplitude spectrum, for the UI's spectrum display.</summary>
    public Spectrum LastSpectrum { get; private set; } = Spectrum.Empty;

    public void Push(AudioBlock block)
    {
        var span = block.Samples.Span;
        if (block.SampleRate <= 0 || span.Length == 0)
        {
            return;
        }

        if (block.SampleRate != _sampleRate)
        {
            Configure(block.SampleRate);
        }

        // Tolerant of ordinary timestamp jitter, intolerant of an actual dropout: a gap in the
        // audio would put a false discontinuity into the pitch series the wow spectrum reads.
        var tolerance = Math.Max(0.05, 0.5 * block.Duration);
        if (double.IsFinite(_lastBlockEnd) && Math.Abs(block.T - _lastBlockEnd) > tolerance)
        {
            ClearBuffers();
        }

        _lastBlockEnd = block.EndTime;

        var dt = 1.0 / _sampleRate;
        for (var i = 0; i < span.Length; i++)
        {
            var x = span[i];
            _ring[_write] = float.IsFinite(x) ? x : 0.0;
            _write = _write + 1 == _ring.Length ? 0 : _write + 1;
            _totalSamples++;

            if (++_sinceHop < _hopSamples || _totalSamples < _ring.Length)
            {
                continue;
            }

            _sinceHop = 0;

            // Timestamp the frame at its centre: the wow spectrum downstream cares about when
            // the music was, not when the buffer filled.
            var endTime = block.T + i * dt;
            AnalyzeFrame(endTime - 0.5 * _ring.Length * dt);
        }
    }

    public void Flush()
    {
        _sinceRecompute = 0;
        Recompute();
    }

    public void Reset()
    {
        _frames.Clear();
        ClearBuffers();
        Current = null;
        DeviationCents = double.NaN;
        DeviationCentsStandardError = double.NaN;
        Resultant = double.NaN;
        LastSpectrum = Spectrum.Empty;
        _sinceRecompute = 0;
        _lastBlockEnd = double.NegativeInfinity;
    }

    private void Configure(int sampleRate)
    {
        _sampleRate = sampleRate;
        _hopSamples = Math.Max(1, (int)Math.Round(sampleRate * _options.HopSeconds));
        _frames.Clear();
        ClearBuffers();
    }

    private void ClearBuffers()
    {
        Array.Clear(_ring, 0, _ring.Length);
        _write = 0;
        _totalSamples = 0;
        _sinceHop = 0;
    }

    private void AnalyzeFrame(double centreTime)
    {
        var n = _ring.Length;
        var frame = new double[n];
        for (var k = 0; k < n; k++)
        {
            frame[k] = _ring[(_write + k) % n];
        }

        var energy = SeriesMath.RootMeanSquare(frame);

        SeriesMath.RemoveMeanInPlace(frame);
        WindowFunctions.ApplyInPlace(frame, _window);

        var amplitudes = Fft.MagnitudeSpectrum(frame);
        var correction = 1.0 / Math.Max(1e-12, _windowGain);
        for (var k = 0; k < amplitudes.Length; k++)
        {
            amplitudes[k] *= correction;
        }

        var binWidth = Fft.BinWidth(n, _sampleRate);
        var frequencies = new double[amplitudes.Length];
        for (var k = 0; k < frequencies.Length; k++)
        {
            frequencies[k] = k * binWidth;
        }

        LastSpectrum = new Spectrum(frequencies, amplitudes);

        var frameResult = FoldToPitchGrid(amplitudes, binWidth);
        var pitchFrame = new PitchFrame(centreTime, frameResult.Cents, frameResult.Resultant, energy);

        _frames.Add(pitchFrame);
        FrameSink?.Add(pitchFrame);

        if (++_sinceRecompute < _options.RecomputeEveryFrames)
        {
            return;
        }

        _sinceRecompute = 0;
        Recompute();
    }

    /// <summary>
    /// Collapse the spectral peaks onto one semitone. Peaks rather than all bins: a bin between
    /// partials carries leakage, not pitch. Weights are logarithmic in amplitude, so a loud
    /// bass note cannot outvote the rest of the chord.
    /// </summary>
    private (double Cents, double Resultant) FoldToPitchGrid(double[] amplitudes, double binWidth)
    {
        if (!(binWidth > 0.0) || amplitudes.Length < 8)
        {
            return (double.NaN, 0.0);
        }

        var from = Math.Max(1, (int)Math.Floor(_options.MinFrequency / binWidth));
        var to = Math.Min(amplitudes.Length - 2, (int)Math.Ceiling(_options.MaxFrequency / binWidth));
        if (from >= to)
        {
            return (double.NaN, 0.0);
        }

        var maxima = PeakInterpolation.LocalMaxima(amplitudes, from, to);
        if (maxima.Count == 0)
        {
            return (double.NaN, 0.0);
        }

        var strongest = 0.0;
        foreach (var index in maxima)
        {
            if (amplitudes[index] > strongest)
            {
                strongest = amplitudes[index];
            }
        }

        if (!(strongest > 0.0))
        {
            return (double.NaN, 0.0);
        }

        var threshold = strongest * _options.PeakThresholdRelative;
        var candidates = new List<(double Cents, double Weight)>(maxima.Count);

        foreach (var index in maxima)
        {
            if (amplitudes[index] < threshold)
            {
                continue;
            }

            // Interpolate on log magnitude: for a Hann-windowed peak this is markedly less
            // biased than interpolating the raw amplitudes.
            var refined = PeakInterpolation.Parabolic(
                Log(amplitudes[index - 1]),
                Log(amplitudes[index]),
                Log(amplitudes[index + 1]));

            var frequency = (index + refined.Offset) * binWidth;
            if (!(frequency >= _options.MinFrequency) || frequency > _options.MaxFrequency)
            {
                continue;
            }

            var semitones = 12.0 * Math.Log(frequency / _options.ReferencePitch, 2.0);
            var cents = (semitones - Math.Round(semitones)) * SemitoneCents;

            var weight = Math.Log(1.0 + amplitudes[index] / threshold);
            if (weight > 0.0 && double.IsFinite(cents))
            {
                candidates.Add((cents, weight));
            }
        }

        if (candidates.Count == 0)
        {
            return (double.NaN, 0.0);
        }

        // Keep the strongest peaks only; a dense noisy spectrum otherwise dilutes the mean.
        if (candidates.Count > _options.MaxPeaksPerFrame)
        {
            candidates.Sort((a, b) => b.Weight.CompareTo(a.Weight));
            candidates.RemoveRange(_options.MaxPeaksPerFrame, candidates.Count - _options.MaxPeaksPerFrame);
        }

        var values = new double[candidates.Count];
        var weights = new double[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            values[i] = candidates[i].Cents;
            weights[i] = candidates[i].Weight;
        }

        return PhaseMath.CircularMeanOverPeriod(values, SemitoneCents, weights);

        static double Log(double amplitude) => Math.Log(Math.Max(amplitude, 1e-12));
    }

    private void Recompute()
    {
        var count = _frames.Count;
        if (count < _options.MinimumFrames)
        {
            return;
        }

        var frames = _frames.ToArray();
        var values = new List<double>(count);
        var weights = new List<double>(count);

        foreach (var frame in frames)
        {
            if (!double.IsFinite(frame.Cents) || frame.Resultant < _options.MinimumFrameResultant)
            {
                continue;
            }

            values.Add(frame.Cents);
            weights.Add(frame.Resultant * frame.Resultant);
        }

        if (values.Count < _options.MinimumFrames / 2)
        {
            return;
        }

        var valueArray = values.ToArray();
        var weightArray = weights.ToArray();
        var (mean, resultant) = PhaseMath.CircularMeanOverPeriod(valueArray, SemitoneCents, weightArray);
        if (!double.IsFinite(mean) || !(resultant > 0.0))
        {
            return;
        }

        // Circular standard deviation, converted from radians back into cents.
        var sigmaAngle = Math.Sqrt(Math.Max(0.0, -2.0 * Math.Log(Math.Min(1.0, resultant))));
        var sigmaCents = sigmaAngle * SemitoneCents / PhaseMath.TwoPi;

        // Frames overlap heavily, so counting them all would understate the interval. Only
        // non-overlapping frames carry independent information.
        var frameDuration = _ring.Length / (double)Math.Max(1, _sampleRate);
        var effectiveCount = Math.Max(1.0, _frames.Span / Math.Max(1e-6, frameDuration));
        var statisticalError = sigmaCents / Math.Sqrt(effectiveCount);

        var tuning = _options.RecordTuningUncertaintyCents;
        var totalCentsError = Math.Sqrt(statisticalError * statisticalError + tuning * tuning);

        DeviationCents = mean;
        DeviationCentsStandardError = totalCentsError;
        Resultant = resultant;

        if (AssumedNominal is not { } nominal)
        {
            // No rpm without a nominal — but the cents indication above is still live.
            Current = null;
            return;
        }

        var nominalRpm = NominalSpeeds.Rpm(nominal);
        var relative = SpeedMath.RelativeDeviationFromCents(mean);
        var rpm = nominalRpm * (1.0 + relative);

        // d(relative)/d(cents) = ln2/1200 · 2^(cents/1200).
        var sensitivity = Math.Log(2.0) / 1200.0 * Math.Pow(2.0, mean / 1200.0);
        var rpmError = nominalRpm * sensitivity * totalCentsError;

        var confidence = ScoreConfidence(resultant, statisticalError, values.Count);

        var diagnostics = new Dictionary<string, double>
        {
            ["deviationCents"] = mean,
            ["centsStandardError"] = totalCentsError,
            ["centsStatisticalError"] = statisticalError,
            ["centsTuningUncertainty"] = tuning,
            ["resultant"] = resultant,
            ["frames"] = count,
            ["usableFrames"] = values.Count,
            ["effectiveCount"] = effectiveCount,
            ["spanSeconds"] = _frames.Span,
            ["assumedNominalRpm"] = nominalRpm,

            // Set on every reading: the UI must never show this number without the caveat.
            ["mixesRecordTuning"] = 1.0,
        };

        Current = SpeedEstimate.FromRpm(rpm, rpmError, confidence, diagnostics);
    }

    private double ScoreConfidence(double resultant, double statisticalError, int usableFrames)
    {
        var concentration = Clamp01((resultant - 0.2) / 0.6);
        var precision = Clamp01(1.0 - statisticalError / 10.0);
        var coverage = Clamp01(usableFrames / 100.0);

        var product = Math.Max(1e-9, concentration) *
                      Math.Max(1e-9, precision) *
                      Math.Max(1e-9, coverage);

        // Hard ceiling: however concentrated the pitch is, the record's own tuning is unknown.
        return Clamp01(Math.Pow(product, 1.0 / 3.0)) * _options.ConfidenceCeiling;

        static double Clamp01(double x) => double.IsFinite(x) ? Math.Clamp(x, 0.0, 1.0) : 0.0;
    }
}
