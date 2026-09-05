using TurntableSpeed.Core.Contracts;
using TurntableSpeed.Core.Dsp;

namespace TurntableSpeed.Core.Estimators;

/// <summary>
/// The autocorrelation curve with its chosen peak — spec §6 asks for this on screen, and it is
/// the most honest thing the acoustic mode can show: either there is a spike once per
/// revolution or there is not.
/// </summary>
/// <param name="Lags">Lag axis, seconds.</param>
/// <param name="Values">Normalised autocorrelation at each lag.</param>
/// <param name="PeakLagSeconds">Chosen period, sub-sample refined. NaN when nothing was chosen.</param>
/// <param name="NoiseFloor">Standard deviation of the curve away from the peak.</param>
public sealed record AutocorrelationView(
    double[] Lags,
    double[] Values,
    double PeakLagSeconds,
    double PeakValue,
    double NoiseFloor)
{
    public static AutocorrelationView Empty { get; } =
        new([], [], double.NaN, double.NaN, double.NaN);

    /// <summary>Peak height in units of the noise floor. Below ~4 the peak is not a peak.</summary>
    public double PeakToNoise =>
        NoiseFloor > 0.0 ? PeakValue / NoiseFloor : double.NaN;
}

public sealed class ClickPeriodicityEstimatorOptions
{
    /// <summary>High-pass corner, Hz. Above the programme material, inside the click spectrum.</summary>
    public double HighPassHz { get; set; } = 5000.0;

    /// <summary>Butterworth order of the high-pass. Even, ≥ 2.</summary>
    public int FilterOrder { get; set; } = 4;

    /// <summary>RMS envelope frame, seconds. Sets the raw lag resolution before interpolation.</summary>
    public double EnvelopeSeconds { get; set; } = 0.005;

    /// <summary>History retained. Longer means a lower autocorrelation noise floor.</summary>
    public double WindowSeconds { get; set; } = 40.0;

    /// <summary>Shortest lag searched: 2.2 s covers 27 rpm, 0.6 s covers 100 rpm.</summary>
    public double MinLagSeconds { get; set; } = 0.6;

    public double MaxLagSeconds { get; set; } = 2.2;

    /// <summary>
    /// Running-median length used to strip the stationary programme material, seconds. Longer
    /// than a click, far shorter than a revolution.
    /// </summary>
    public double MedianWindowSeconds { get; set; } = 0.25;

    /// <summary>A confident 33-or-45 answer is expected in 10–15 s; below this, say nothing.</summary>
    public double MinimumSpanSeconds { get; set; } = 8.0;

    /// <summary>Envelope samples between re-fits. Deterministic — never time-based.</summary>
    public int RecomputeEveryEnvelopeSamples { get; set; } = 100;

    /// <summary>
    /// How much autocorrelation a half- or double-lag candidate must show before it is allowed
    /// to displace the raw maximum. High on purpose: a single click per revolution leaves
    /// nothing at half the period, so this only fires on genuine ambiguity.
    /// </summary>
    public double MinimumHarmonicSupport { get; set; } = 0.45;

    /// <summary>
    /// How much better a longer candidate period must score before it displaces a shorter one.
    /// Without this the estimator can report half the speed on a record whose clicks happen to
    /// correlate as strongly at two revolutions as at one.
    /// </summary>
    public double LongerPeriodMargin { get; set; } = 0.1;

    /// <summary>Relative precision treated as fully converged. The spec's acoustic target is ±0.3%.</summary>
    public double TargetRelativePrecision { get; set; } = 0.003;

    /// <summary>
    /// How far above the autocorrelation background the peak must stand before it counts as a
    /// detection at all. Below this the estimator stays silent rather than reporting a period
    /// picked out of the noise.
    /// <para>
    /// Measured, not guessed. On a real recording of music with no surface noise to lock onto,
    /// the tallest bump in the search band reaches 5–7σ purely by chance: the rectified envelope
    /// residual is strongly correlated, so the maximum over several hundred lags sits far above
    /// what independent samples would give. A genuine click train scores 690–780σ on the golden
    /// fixtures. The gate belongs in the empty space between those two, not at the edge of the
    /// noise — at 5.0 it admitted 55, 72 and 86 rpm readings from a record turning at 33⅓.
    /// </para>
    /// </summary>
    public double MinimumPeakToNoise { get; set; } = 12.0;

    /// <summary>
    /// How closely two successive fits must agree before the period is published, in percent.
    /// Wide enough to let a platter drift or spin up, far tighter than the jump to a neighbouring
    /// autocorrelation bump.
    /// </summary>
    public double PeriodAgreementPercent { get; set; } = 1.0;

    /// <summary>
    /// Successive fits that must agree before a new period reaches the screen. At the default
    /// recompute cadence this is about two seconds of holding still.
    /// <para>
    /// This is the discriminator that survives contact with real recordings. A genuine lock
    /// holds one period for the whole capture; the false locks measured on a record with no
    /// usable clicks held theirs for a second or so before the sliding window preferred a
    /// different bump. Confidence alone does not separate them — one of those false locks
    /// briefly reached 0.42, against 0.75 for a real one — but persistence does.
    /// </para>
    /// </summary>
    public int ConfirmationsRequired { get; set; } = 4;
}

/// <summary>
/// The primary acoustic estimator (spec §4.2). Dust, scratches and groove wear produce
/// impulsive clicks that repeat exactly once per revolution; their period is the revolution
/// period. No reference recording is needed, and it works on the lead-in groove, in pauses and
/// underneath music.
/// </summary>
public sealed class ClickPeriodicityEstimator : ISpeedEstimator<AudioBlock>
{
    /// <summary>
    /// Residual bias of a three-point parabolic fit on a peak only a frame or two wide, as a
    /// fraction of the envelope frame. Measured on synthetic click trains at all three nominal
    /// speeds; it is a systematic, so it floors the reported uncertainty rather than shrinking
    /// with capture length.
    /// </summary>
    private const double InterpolationBiasFraction = 0.15;

    private readonly ClickPeriodicityEstimatorOptions _options;
    private readonly TimeWindow<TimedValue> _envelope;
    private readonly List<double> _emitted = new(64);

    private BiquadCascade? _highPass;
    private RmsEnvelopeFollower? _follower;
    private double[] _filtered = [];
    private int _sampleRate;
    private int _sinceRecompute;
    private double _lastBlockEnd = double.NegativeInfinity;
    private double _candidatePeriod = double.NaN;
    private int _confirmations;

    public ClickPeriodicityEstimator(ClickPeriodicityEstimatorOptions? options = null)
    {
        _options = options ?? new ClickPeriodicityEstimatorOptions();
        var envelopeRate = 1.0 / _options.EnvelopeSeconds;
        _envelope = new TimeWindow<TimedValue>(
            v => v.T,
            _options.WindowSeconds,
            maxCount: (int)(_options.WindowSeconds * envelopeRate) + 256);
    }

    public SpeedEstimate? Current { get; private set; }

    /// <summary>Chosen revolution period, seconds. NaN before the first successful fit.</summary>
    public double PeriodSeconds { get; private set; } = double.NaN;

    /// <summary>The curve behind the answer, for the UI.</summary>
    public AutocorrelationView Autocorrelation { get; private set; } = AutocorrelationView.Empty;

    /// <summary>Envelope sample rate, Hz. NaN until the first block arrives.</summary>
    public double EnvelopeRateHz =>
        _follower is null || _sampleRate <= 0 ? double.NaN : _sampleRate / (double)_follower.Hop;

    public double WindowSpanSeconds => _envelope.Span;

    public int EnvelopeSampleCount => _envelope.Count;

    /// <summary>Envelope trace retained, oldest first. Useful for a click-visualiser.</summary>
    public IReadOnlyList<TimedValue> Envelope => _envelope;

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

        // A gap or a rewind in the timeline invalidates the filter state and the window; a few
        // milliseconds of ordinary timestamp jitter does not.
        var tolerance = Math.Max(0.05, 0.5 * block.Duration);
        if (double.IsFinite(_lastBlockEnd) && Math.Abs(block.T - _lastBlockEnd) > tolerance)
        {
            _envelope.Clear();
            _highPass!.Reset();
            _follower!.Reset();
            ForgetCandidate();
        }

        _lastBlockEnd = block.EndTime;

        if (_filtered.Length < span.Length)
        {
            _filtered = new double[span.Length];
        }

        var filtered = _filtered.AsSpan(0, span.Length);
        _highPass!.Process(span, filtered);

        var hop = _follower!.Hop;
        var consumedBefore = _follower.SamplesConsumed;

        _emitted.Clear();
        var emitted = _follower.Push(filtered, _emitted);

        for (var j = 0; j < emitted; j++)
        {
            // The j-th emission of this block completes at this global input-sample count.
            var completedAt = (consumedBefore / hop + j + 1) * hop;
            var offsetInBlock = completedAt - consumedBefore - 1;
            var t = block.T + offsetInBlock / (double)block.SampleRate;
            _envelope.Add(new TimedValue(t, _emitted[j]));
        }

        _sinceRecompute += emitted;
        if (_sinceRecompute < _options.RecomputeEveryEnvelopeSamples)
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
        _envelope.Clear();
        _highPass?.Reset();
        _follower?.Reset();
        Current = null;
        PeriodSeconds = double.NaN;
        Autocorrelation = AutocorrelationView.Empty;
        _sinceRecompute = 0;
        _lastBlockEnd = double.NegativeInfinity;
        ForgetCandidate();
    }

    private void ForgetCandidate()
    {
        _candidatePeriod = double.NaN;
        _confirmations = 0;
    }

    /// <summary>
    /// Count how many successive fits have landed on the same period, and say whether that is
    /// enough to publish. A period that survives only one fit is a bump the sliding window
    /// happened to favour, and putting it on screen is how a reading jumps between speeds no
    /// turntable has.
    /// </summary>
    private bool IsConfirmed(double period)
    {
        var agrees = double.IsFinite(_candidatePeriod) &&
                     _candidatePeriod > 0.0 &&
                     Math.Abs(period - _candidatePeriod) / _candidatePeriod * 100.0
                         <= _options.PeriodAgreementPercent;

        if (agrees)
        {
            // Track the latest fit, not the first: the period is allowed to follow a drifting
            // platter, one agreement step at a time.
            _candidatePeriod = period;
            _confirmations++;
        }
        else
        {
            _candidatePeriod = period;
            _confirmations = 1;
        }

        return _confirmations >= Math.Max(1, _options.ConfirmationsRequired);
    }

    private void Configure(int sampleRate)
    {
        _sampleRate = sampleRate;
        _envelope.Clear();

        // Keep the corner inside the band even on a device that hands back 16 kHz.
        var cutoff = Math.Min(_options.HighPassHz, sampleRate * 0.4);
        _highPass = Butterworth.HighPass(cutoff, sampleRate, _options.FilterOrder);
        _follower = new RmsEnvelopeFollower(
            RmsEnvelopeFollower.HopForPeriod(sampleRate, _options.EnvelopeSeconds));
        _sinceRecompute = 0;
    }

    private void Recompute()
    {
        var count = _envelope.Count;
        var interval = _follower is null || _sampleRate <= 0
            ? double.NaN
            : _follower.Hop / (double)_sampleRate;

        if (!double.IsFinite(interval) || interval <= 0.0)
        {
            return;
        }

        var span = _envelope.Span;
        if (span < _options.MinimumSpanSeconds)
        {
            return;
        }

        var minLag = (int)Math.Ceiling(_options.MinLagSeconds / interval);
        var maxLag = Math.Min((int)Math.Floor(_options.MaxLagSeconds / interval), count - 8);
        if (maxLag <= minLag + 4 || minLag < 2)
        {
            return;
        }

        // Strip the stationary component: the running median tracks the music, the positive
        // residual keeps the transients. Negative excursions carry no click information.
        var values = _envelope.Select(v => v.Value);
        var medianWindow = Math.Max(3, (int)Math.Round(_options.MedianWindowSeconds / interval));
        var residual = SeriesMath.SubtractRunningMedian(values, medianWindow);
        for (var i = 0; i < residual.Length; i++)
        {
            residual[i] = Math.Max(0.0, residual[i]);
        }

        // Biased normalisation (divide by n, not by the overlap) on purpose: it makes the comb
        // decay with lag, so the autocorrelation of a click train is strongest at the true
        // period rather than at a multiple of it. Unbiased normalisation over-corrects for the
        // shrinking overlap and can put the maximum an octave down — a 60 rpm platter then
        // reads 30.
        var acf = Dsp.Autocorrelation.Compute(residual, maxLag, unbiased: false);
        if (acf.Length <= maxLag)
        {
            return;
        }

        var (rawIndex, rawPeak) = PeakInterpolation.RefineMaximum(acf, minLag, maxLag);
        if (double.IsNaN(rawIndex) || !(rawPeak.Value > 0.0))
        {
            return;
        }

        var choice = ChoosePeriod(acf, rawIndex, rawPeak, minLag, maxLag, interval);
        var lagIndex = choice.Index;
        var peak = choice.Peak;

        var period = lagIndex * interval;
        if (!(period > 0.0) || !double.IsFinite(period))
        {
            return;
        }

        var exclusion = Math.Max(3, (int)Math.Round(0.05 / interval));
        var noiseFloor = Dsp.Autocorrelation.NoiseFloor(acf, minLag, maxLag, (int)Math.Round(lagIndex), exclusion);
        var detectionFloor = Dsp.Autocorrelation.RobustNoiseFloor(
            acf, minLag, maxLag, (int)Math.Round(lagIndex), exclusion);

        // A bump a few standard deviations above the background is not a detection. Better to
        // keep saying nothing than to publish a period picked out of the noise with a tight
        // interval around it. Measured against the robust background, so that a record with
        // several clicks per revolution is not penalised for the comb it legitimately produces.
        if (double.IsFinite(detectionFloor) && detectionFloor > 0.0 &&
            peak.Value / detectionFloor < _options.MinimumPeakToNoise)
        {
            ForgetCandidate();
            return;
        }

        // Hold the previous answer — or none — until this period has shown itself twice. The
        // check costs one recompute interval on first lock and nothing thereafter.
        if (!IsConfirmed(period))
        {
            return;
        }

        var revolutions = span / period;
        var periodError = LagUncertainty(peak, noiseFloor, interval, revolutions);

        var rpm = SpeedMath.RpmFromPeriod(period);
        var rpmError = rpm / period * periodError;
        if (!double.IsFinite(rpm) || !double.IsFinite(rpmError) || rpm <= 0.0)
        {
            return;
        }

        PeriodSeconds = period;
        Autocorrelation = BuildView(acf, minLag, maxLag, interval, period, peak.Value, noiseFloor);

        var peakToNoise = noiseFloor > 0.0 ? peak.Value / noiseFloor : double.NaN;
        var confidence = ScoreConfidence(rpm, rpmError, revolutions, peakToNoise, choice.Support);

        var diagnostics = new Dictionary<string, double>
        {
            ["periodSeconds"] = period,
            ["periodErrorSeconds"] = periodError,
            ["revolutions"] = revolutions,
            ["spanSeconds"] = span,
            ["envelopeRateHz"] = 1.0 / interval,
            ["envelopeSamples"] = count,
            ["acfPeak"] = peak.Value,
            ["acfNoiseFloor"] = noiseFloor,
            ["acfPeakToNoise"] = peakToNoise,
            ["acfDetectionFloor"] = detectionFloor,
            ["acfPeakToDetectionFloor"] = detectionFloor > 0.0 ? peak.Value / detectionFloor : double.NaN,
            ["acfCurvature"] = peak.Curvature,
            ["harmonicRatio"] = choice.Ratio,
            ["harmonicSupport"] = choice.Support,
            ["highPassHz"] = _highPass is null ? double.NaN : Math.Min(_options.HighPassHz, _sampleRate * 0.4),
        };

        Current = SpeedEstimate.FromRpm(rpm, rpmError, confidence, diagnostics);
    }

    /// <summary>
    /// Decide which autocorrelation peak is the revolution (spec §4.2, step 6).
    /// <para>
    /// With <em>c</em> clicks per revolution the autocorrelation is a comb whose teeth sit at
    /// multiples of the click spacing <em>s = T/c</em>, not of the period. The raw maximum can
    /// land on any tooth: at 45 rpm with three clicks per revolution it lands on 1.333 s worth
    /// of teeth away, and simply doubling or halving it never reaches the truth, because the
    /// period is 3·s while the maximum sat at 2·s.
    /// </para>
    /// <para>
    /// So the comb spacing is measured first — the largest <em>s</em> of which every detected
    /// tooth is a multiple — and the candidates are its multiples. Support says a periodicity is
    /// really there; the nominal grid breaks the remaining tie, which it can because no two of
    /// 1.8 / 1.333 / 0.769 s are related by a small integer ratio.
    /// </para>
    /// </summary>
    private (double Index, ParabolicPeak Peak, double Ratio, double Support) ChoosePeriod(
        double[] acf,
        double rawIndex,
        ParabolicPeak rawPeak,
        int minLag,
        int maxLag,
        double interval)
    {
        var searchRadius = Math.Max(2, (int)Math.Round(0.03 / interval));
        var teeth = FindTeeth(acf, minLag, maxLag, rawPeak.Value);

        var fallback = (Index: rawIndex, Peak: rawPeak, Ratio: 1.0, Support: 1.0);

        var spacing = CombSpacing(teeth, rawIndex);
        if (!(spacing > 0.0))
        {
            return fallback;
        }

        // Shortest first. A peak at 2T is a harmonic of the period, not the period, so a longer
        // candidate has to be clearly better before it is allowed to displace a shorter one.
        (double Index, ParabolicPeak Peak, double Ratio, double Support)? best = null;
        var bestScore = double.NegativeInfinity;

        for (var multiple = 1; multiple * spacing <= maxLag; multiple++)
        {
            var found = Probe(multiple * spacing);
            if (found is not { } candidate)
            {
                continue;
            }

            var score = Score(candidate.Index * interval, candidate.Support);
            if (best is null)
            {
                best = (candidate.Index, candidate.Peak, candidate.Index / rawIndex, candidate.Support);
                bestScore = score;
                continue;
            }

            if (score > bestScore * (1.0 + _options.LongerPeriodMargin))
            {
                best = (candidate.Index, candidate.Peak, candidate.Index / rawIndex, candidate.Support);
                bestScore = score;
            }
        }

        return best ?? fallback;

        // Refine the local maximum near a target lag and report how much of the raw peak it carries.
        (double Index, ParabolicPeak Peak, double Support)? Probe(double target)
        {
            if (target < minLag + 1 || target > maxLag - 1)
            {
                return null;
            }

            var centre = (int)Math.Round(target);
            var (index, peak) = PeakInterpolation.RefineMaximum(
                acf,
                Math.Max(minLag, centre - searchRadius),
                Math.Min(maxLag, centre + searchRadius));

            if (double.IsNaN(index) || !(peak.Value > 0.0))
            {
                return null;
            }

            var support = peak.Value / rawPeak.Value;
            return support < _options.MinimumHarmonicSupport ? null : (index, peak, support);
        }

        // Support says "there really is a periodicity here"; the nominal grid says "and this is
        // the one a turntable can actually be running at".
        static double Score(double period, double support)
        {
            var classification = NominalSpeeds.Classify(SpeedMath.RpmFromPeriod(period));
            return support * (0.25 + 0.75 * classification.Confidence);
        }
    }

    /// <summary>Lags of every autocorrelation peak strong enough to be a comb tooth.</summary>
    private List<double> FindTeeth(double[] acf, int minLag, int maxLag, double rawPeakValue)
    {
        var threshold = rawPeakValue * _options.MinimumHarmonicSupport;
        var teeth = new List<double>();

        foreach (var index in PeakInterpolation.LocalMaxima(acf, minLag, maxLag))
        {
            if (acf[index] < threshold)
            {
                continue;
            }

            var refined = PeakInterpolation.Parabolic(acf[index - 1], acf[index], acf[index + 1]);
            teeth.Add(index + refined.Offset);
        }

        return teeth;
    }

    /// <summary>
    /// Largest spacing of which every detected tooth is a multiple. Searched over the divisors
    /// of the shortest tooth, because the true spacing may itself be below the shortest lag the
    /// estimator looks at — at 78 rpm with three clicks per revolution it is 0.26 s.
    /// </summary>
    private static double CombSpacing(List<double> teeth, double fallback)
    {
        if (teeth.Count == 0)
        {
            return fallback;
        }

        var shortest = teeth[0];
        foreach (var tooth in teeth)
        {
            if (tooth < shortest)
            {
                shortest = tooth;
            }
        }

        if (!(shortest > 0.0))
        {
            return fallback;
        }

        var bestSpacing = shortest;
        var bestError = double.PositiveInfinity;

        // On an exact tie the first divisor tried wins, i.e. the finest spacing. That is the
        // safe direction: a finer spacing only proposes more candidate periods, and every one
        // of them still has to show real autocorrelation before it is considered.
        for (var divisor = 4; divisor >= 1; divisor--)
        {
            var spacing = shortest / divisor;
            var error = 0.0;
            foreach (var tooth in teeth)
            {
                var multiple = Math.Round(tooth / spacing);
                if (multiple < 1.0)
                {
                    error += 1.0;
                    continue;
                }

                error += Math.Abs(tooth / spacing - multiple);
            }

            error /= teeth.Count;

            if (error < bestError - 1e-9)
            {
                bestError = error;
                bestSpacing = spacing;
            }
        }

        return bestSpacing;
    }

    /// <summary>
    /// How far the located peak can be from the true period. Three effects, combined by taking
    /// the worst, because they do not average each other away:
    /// <list type="bullet">
    /// <item>background scatter — a perturbation ε of the curve moves a parabolic vertex by
    /// about ε/(2c), so the autocorrelation noise floor and the curvature give this directly;</item>
    /// <item>click jitter — a peak of half-width w built from N revolutions locates its centre
    /// to roughly w·√2/√N, which is what dominates on a worn or eccentric record;</item>
    /// <item>interpolation bias — an autocorrelation peak only a frame or two wide is not a
    /// parabola, and fitting one pulls the answer toward the sample grid by a fixed fraction of
    /// a frame no matter how long the capture runs.</item>
    /// </list>
    /// </summary>
    private double LagUncertainty(ParabolicPeak peak, double noiseFloor, double interval, double revolutions)
    {
        // The grid-attraction floor: not statistical, so it never shrinks with more data.
        var floor = interval * InterpolationBiasFraction;
        var fallback = interval * 0.5 / Math.Sqrt(Math.Max(1.0, revolutions));

        if (!(peak.Curvature > 0.0) || !(peak.Value > 0.0))
        {
            return Math.Max(fallback, floor);
        }

        var worst = floor;

        if (double.IsFinite(noiseFloor) && noiseFloor > 0.0)
        {
            var fromNoise = noiseFloor / (2.0 * peak.Curvature) * interval;
            if (double.IsFinite(fromNoise) && fromNoise > worst)
            {
                worst = fromNoise;
            }
        }

        // Half-width of the peak, from height and curvature: c = A / (2w²).
        var halfWidth = Math.Sqrt(peak.Value / (2.0 * peak.Curvature));
        var fromJitter = halfWidth * Math.Sqrt(2.0) / Math.Sqrt(Math.Max(1.0, revolutions)) * interval;
        if (double.IsFinite(fromJitter) && fromJitter > worst)
        {
            worst = fromJitter;
        }

        return worst;
    }

    private static AutocorrelationView BuildView(
        double[] acf,
        int minLag,
        int maxLag,
        double interval,
        double peakLag,
        double peakValue,
        double noiseFloor)
    {
        var length = maxLag - minLag + 1;
        var lags = new double[length];
        var values = new double[length];
        for (var i = 0; i < length; i++)
        {
            lags[i] = (minLag + i) * interval;
            values[i] = acf[minLag + i];
        }

        return new AutocorrelationView(lags, values, peakLag, peakValue, noiseFloor);
    }

    private double ScoreConfidence(
        double rpm,
        double rpmError,
        double revolutions,
        double peakToNoise,
        double harmonicSupport)
    {
        var precision = rpm > 0.0
            ? Clamp01(1.0 - rpmError / rpm / _options.TargetRelativePrecision * 0.5)
            : 0.0;

        // Below ~4σ the "peak" is a fluctuation; by 12σ it is unmistakable.
        var prominence = double.IsFinite(peakToNoise)
            ? Clamp01((peakToNoise - 4.0) / 8.0)
            : 0.3;

        var coverage = Clamp01(revolutions / 12.0);

        // A period picked over a rival harmonic deserves less confidence than an unopposed one.
        var unambiguous = Clamp01(1.0 - Math.Abs(harmonicSupport - 1.0) * 0.5);

        var product = Math.Max(1e-9, precision) *
                      Math.Max(1e-9, prominence) *
                      Math.Max(1e-9, coverage) *
                      Math.Max(1e-9, unambiguous);

        return Clamp01(Math.Pow(product, 0.25));

        static double Clamp01(double x) => double.IsFinite(x) ? Math.Clamp(x, 0.0, 1.0) : 0.0;
    }
}
