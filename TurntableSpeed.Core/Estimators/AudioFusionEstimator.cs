using TurntableSpeed.Core.Contracts;
using TurntableSpeed.Core.Dsp;

namespace TurntableSpeed.Core.Estimators;

[Flags]
public enum AudioWarning
{
    None = 0,

    /// <summary>Nothing usable yet — keep the needle in the groove and wait.</summary>
    NotEnoughData = 1 << 0,

    /// <summary>The input looks gated, compressed or otherwise processed. See §4.1.</summary>
    InputProcessingSuspected = 1 << 1,

    Clipping = 1 << 2,

    SignalTooQuiet = 1 << 3,

    /// <summary>
    /// No nominal speed known yet, so the pitch estimator has nothing to apply its deviation
    /// to. Resolved automatically once the click estimator locks on, or by the user choosing.
    /// </summary>
    NoNominalYet = 1 << 4,

    /// <summary>
    /// Always set whenever a pitch-grid reading is in play. That estimator cannot separate the
    /// turntable's error from the record's own tuning, and the UI must say so.
    /// </summary>
    PitchMixesRecordTuning = 1 << 5,

    /// <summary>Independent acoustic methods disagree beyond the threshold — shown, not averaged.</summary>
    MethodsDisagree = 1 << 6,

    /// <summary>
    /// Enough audio has gone by without a periodic click being found. A spotless record in a
    /// quiet passage genuinely has nothing to lock onto; the pitch grid must carry the mode.
    /// </summary>
    NoClickPeriodicity = 1 << 7,
}

/// <summary>Everything the acoustic-mode screen needs, in one immutable snapshot.</summary>
public sealed record AudioFusionResult(
    SpeedEstimate? Primary,
    SpeedEstimate? Click,
    SpeedEstimate? Pitch,
    SpeedEstimate? WowSpectrum,
    double DeviationCents,
    double DisagreementPercent,
    bool IsReliable,
    AudioWarning Warnings,
    AudioInputQuality Quality,
    AutocorrelationView Autocorrelation,
    WowFlutterResult WowFlutter,
    double WowProgress)
{
    public static AudioFusionResult Empty { get; } = new(
        null, null, null, null, double.NaN, double.NaN, false,
        AudioWarning.NotEnoughData, AudioInputQuality.Unknown,
        AutocorrelationView.Empty, WowFlutterResult.Empty, 0.0);

    public bool HasResult => Primary is not null;
}

public sealed class AudioFusionEstimatorOptions
{
    /// <summary>
    /// Disagreement above which the methods are reported as inconsistent instead of combined.
    /// The acoustic target is ±0.3%, so 1% is comfortably outside the expected scatter.
    /// </summary>
    public double DisagreementThresholdPercent { get; set; } = 1.0;

    /// <summary>Confidence a combined reading must reach before the UI calls it reliable.</summary>
    public double MinimumReliableConfidence { get; set; } = 0.5;

    /// <summary>
    /// Confidence the click estimator must reach before its reading is treated as the reference
    /// (§4.5) rather than as one more opinion.
    /// <para>
    /// The click method leads because it measures the platter directly, not because it is
    /// infallible; a weak lock is precisely the case where the autocorrelation peak was noise.
    /// Measured on both sides: a genuine lock is already at 0.75 when it first publishes, around
    /// nine seconds in, and settles at 0.78–0.94, while the sustained false locks taken from a
    /// real recording with no clicks in it scored 0.16–0.21. This sits between them with room on
    /// each side rather than shaved to either.
    /// </para>
    /// </summary>
    public double MinimumClickConfidence { get; set; } = 0.4;

    /// <summary>Audio that has gone by without a click lock before the warning is raised, seconds.</summary>
    public double ClickTimeoutSeconds { get; set; } = 25.0;

    /// <summary>
    /// Independent methods agreeing is the single best reliability indicator there is (§4.5),
    /// so agreement is allowed to raise the combined confidence by this factor.
    /// </summary>
    public double AgreementBonus { get; set; } = 1.15;
}

/// <summary>
/// The acoustic mode as a whole (spec §4.5). Runs the input diagnostics, the click-periodicity
/// estimator, the pitch-grid estimator and the long wow analysis over one audio stream, and
/// combines whatever they produce by inverse variance — but only while they agree. When they
/// do not, the disagreement itself is the output: nothing is silently averaged away.
/// </summary>
public sealed class AudioFusionEstimator : ISpeedEstimator<AudioBlock>
{
    private readonly AudioFusionEstimatorOptions _options;

    public AudioFusionEstimator(
        AudioFusionEstimatorOptions? options = null,
        ClickPeriodicityEstimatorOptions? clickOptions = null,
        PitchGridEstimatorOptions? pitchOptions = null,
        WowSpectrumEstimatorOptions? wowOptions = null,
        AudioProcessingDetectorOptions? detectorOptions = null)
    {
        _options = options ?? new AudioFusionEstimatorOptions();
        Detector = new AudioProcessingDetector(detectorOptions);
        Click = new ClickPeriodicityEstimator(clickOptions);
        Pitch = new PitchGridEstimator(pitchOptions);

        // The wow estimator shares the pitch analysis rather than repeating the FFTs; it only
        // collects the frames the pitch estimator emits.
        Wow = new WowSpectrumEstimator(Pitch, wowOptions);
    }

    public AudioProcessingDetector Detector { get; }

    public ClickPeriodicityEstimator Click { get; }

    public PitchGridEstimator Pitch { get; }

    public WowSpectrumEstimator Wow { get; }

    /// <summary>
    /// Nominal speed chosen by the user. Overrides the click estimator's own classification,
    /// which is what lets the pitch indication work from the first seconds of a side.
    /// </summary>
    public NominalSpeed? ManualNominal { get; set; }

    public AudioFusionResult Result { get; private set; } = AudioFusionResult.Empty;

    public SpeedEstimate? Current => Result.Primary;

    /// <summary>
    /// The click reading when it is strong enough to lead on, otherwise null. Everything that
    /// treats the click estimator as the reference — the nominal it hands the pitch grid, and
    /// the choice of primary — goes through this rather than through <see cref="Click"/>.
    /// </summary>
    private SpeedEstimate? CredibleClick =>
        Click.Current is { } click && click.Confidence >= _options.MinimumClickConfidence
            ? click
            : null;

    /// <summary>Tell the diagnostics what the platform admitted about the capture chain.</summary>
    public void SetCaptureReport(AudioCaptureReport report)
    {
        Detector.SetCaptureReport(report);
        Recompute();
    }

    public void Push(AudioBlock block)
    {
        Detector.Push(block);
        Click.Push(block);

        // The pitch grid measures a deviation, not a speed; it needs to be told which nominal
        // that deviation is a deviation from. The click estimator is what normally knows — but
        // only when its own reading stands up, otherwise a false lock silently switches the
        // pitch estimator off and takes the disagreement warning down with it.
        Pitch.AssumedNominal = ManualNominal ?? CredibleClick?.Nominal;

        Pitch.Push(block);
        Wow.Collect();

        Recompute();
    }

    /// <summary>Force every sub-estimator to re-fit. Used at end of capture.</summary>
    public void Flush()
    {
        Click.Flush();
        Pitch.AssumedNominal = ManualNominal ?? CredibleClick?.Nominal;
        Pitch.Flush();
        Wow.Flush();
        Recompute();
    }

    public void Reset()
    {
        Detector.Reset();
        Click.Reset();
        Pitch.Reset();
        Wow.Reset();
        Result = AudioFusionResult.Empty;
    }

    private void Recompute()
    {
        var quality = Detector.Current;
        var click = Click.Current;
        var pitch = Pitch.Current;
        var wow = Wow.Result.Speed;

        var disagreement = Disagreement(click, pitch);
        var agree = !double.IsFinite(disagreement) ||
                    Math.Abs(disagreement) <= _options.DisagreementThresholdPercent;

        var primary = ChoosePrimary(click, pitch, wow, disagreement, agree);
        var warnings = CollectWarnings(quality, click, pitch, disagreement, agree);

        var reliable =
            primary is not null &&
            quality.IsUsable &&
            agree &&
            primary.Confidence >= _options.MinimumReliableConfidence;

        Result = new AudioFusionResult(
            primary,
            click,
            pitch,
            wow,
            Pitch.DeviationCents,
            disagreement,
            reliable,
            warnings,
            quality,
            Click.Autocorrelation,
            Wow.Result.WowFlutter,
            Wow.Progress);
    }

    private static double Disagreement(SpeedEstimate? click, SpeedEstimate? pitch)
    {
        if (click is null || pitch is null || !(click.RevolutionsPerMinute > 0.0))
        {
            return double.NaN;
        }

        return (pitch.RevolutionsPerMinute - click.RevolutionsPerMinute)
               / click.RevolutionsPerMinute * 100.0;
    }

    /// <summary>
    /// Inverse-variance combination of the methods that agree. The click estimator is the
    /// reference: when the others contradict it, they are dropped from the combination and the
    /// contradiction is reported instead.
    /// <para>
    /// That leadership is conditional on the click reading being credible. An estimator that has
    /// locked onto noise reports a low confidence and an impossible speed, and letting it
    /// displace a live pitch reading on the strength of being the click estimator is what puts
    /// 86 rpm on the screen under a record turning at 33⅓. A weak lock is still reported in
    /// <see cref="AudioFusionResult.Click"/> and still counted against the disagreement — it
    /// simply does not get to be the headline.
    /// </para>
    /// </summary>
    private SpeedEstimate? ChoosePrimary(
        SpeedEstimate? click,
        SpeedEstimate? pitch,
        SpeedEstimate? wow,
        double disagreement,
        bool agree)
    {
        var credibleClick = CredibleClick;

        // No fallback to the weak click reading. When it is the only thing on offer there is
        // nothing to sanity-check it against, which is exactly when a false lock reaches the
        // screen unopposed — and an impossible speed shown as the answer is worse than no answer.
        // The reading stays visible as the click row and the autocorrelation curve.
        var reference = credibleClick ?? pitch;
        if (reference is null)
        {
            return null;
        }

        var parts = new List<SpeedEstimate> { reference };

        // Only ever reached with the click reading leading: a weak one is not combined with the
        // pitch grid, it is set aside.
        if (credibleClick is not null && pitch is not null && agree)
        {
            parts.Add(pitch);
        }

        if (wow is not null && Consistent(wow, reference))
        {
            parts.Add(wow);
        }

        var combined = InverseVariance(parts);
        if (combined is null)
        {
            return null;
        }

        var (rpm, standardError, weightSum) = combined.Value;

        var confidence = 0.0;
        foreach (var part in parts)
        {
            confidence = Math.Max(confidence, part.Confidence);
        }

        // Agreement between mechanisms that share no failure mode is the strongest evidence
        // available here; contradiction is the opposite and is not averaged away.
        if (parts.Count > 1)
        {
            confidence *= _options.AgreementBonus;
        }

        if (click is not null && pitch is not null && !agree)
        {
            confidence *= 0.4;
        }

        var diagnostics = new Dictionary<string, double>
        {
            ["methodsCombined"] = parts.Count,
            ["weightSum"] = weightSum,
            ["usedClick"] = click is not null && parts.Contains(click) ? 1.0 : 0.0,
            ["clickCredible"] = credibleClick is not null ? 1.0 : 0.0,
            ["usedPitch"] = parts.Contains(pitch!) ? 1.0 : 0.0,
            ["usedWowSpectrum"] = wow is not null && parts.Contains(wow) ? 1.0 : 0.0,
        };

        if (click is not null)
        {
            diagnostics["clickRpm"] = click.RevolutionsPerMinute;
            diagnostics["clickErrorRpm"] = click.StandardError;
        }

        if (pitch is not null)
        {
            diagnostics["pitchRpm"] = pitch.RevolutionsPerMinute;
            diagnostics["pitchErrorRpm"] = pitch.StandardError;
            diagnostics["deviationCents"] = Pitch.DeviationCents;
        }

        if (wow is not null)
        {
            diagnostics["wowSpectrumRpm"] = wow.RevolutionsPerMinute;
            diagnostics["wowSpectrumErrorRpm"] = wow.StandardError;
        }

        if (double.IsFinite(disagreement))
        {
            diagnostics["disagreementPercent"] = disagreement;
        }

        if (double.IsFinite(Detector.Current.NoiseFloorDbfs))
        {
            diagnostics["noiseFloorDbfs"] = Detector.Current.NoiseFloorDbfs;
            diagnostics["inputRmsDbfs"] = Detector.Current.RmsDbfs;
        }

        return SpeedEstimate.FromRpm(rpm, standardError, Math.Clamp(confidence, 0.0, 1.0), diagnostics);
    }

    private bool Consistent(SpeedEstimate candidate, SpeedEstimate reference) =>
        reference.RevolutionsPerMinute > 0.0 &&
        Math.Abs(candidate.RevolutionsPerMinute - reference.RevolutionsPerMinute)
        / reference.RevolutionsPerMinute * 100.0 <= _options.DisagreementThresholdPercent;

    private static (double Rpm, double StandardError, double WeightSum)? InverseVariance(
        IReadOnlyList<SpeedEstimate> parts)
    {
        var weightSum = 0.0;
        var weighted = 0.0;

        foreach (var part in parts)
        {
            if (!(part.StandardError > 0.0) ||
                !double.IsFinite(part.StandardError) ||
                !double.IsFinite(part.RevolutionsPerMinute))
            {
                continue;
            }

            var weight = 1.0 / (part.StandardError * part.StandardError);
            weightSum += weight;
            weighted += weight * part.RevolutionsPerMinute;
        }

        if (!(weightSum > 0.0) || !double.IsFinite(weightSum))
        {
            // No usable interval anywhere: fall back to the first reading rather than inventing one.
            var first = parts.Count > 0 ? parts[0] : null;
            return first is null ? null : (first.RevolutionsPerMinute, first.StandardError, 0.0);
        }

        return (weighted / weightSum, Math.Sqrt(1.0 / weightSum), weightSum);
    }

    private AudioWarning CollectWarnings(
        AudioInputQuality quality,
        SpeedEstimate? click,
        SpeedEstimate? pitch,
        double disagreement,
        bool agree)
    {
        var warnings = AudioWarning.None;

        // Judged on what may actually be reported, not on what the sub-estimators hold: a click
        // reading too weak to lead is, as far as the screen is concerned, no reading at all.
        var credibleClick = CredibleClick;

        if (credibleClick is null && pitch is null)
        {
            warnings |= AudioWarning.NotEnoughData;
        }

        if ((quality.Warnings & (
                AudioInputWarning.ProcessingNotDisabled |
                AudioInputWarning.NoiseGateSuspected |
                AudioInputWarning.AutomaticGainControlSuspected)) != AudioInputWarning.None)
        {
            warnings |= AudioWarning.InputProcessingSuspected;
        }

        if ((quality.Warnings & AudioInputWarning.Clipping) != AudioInputWarning.None)
        {
            warnings |= AudioWarning.Clipping;
        }

        if ((quality.Warnings & AudioInputWarning.SignalTooQuiet) != AudioInputWarning.None)
        {
            warnings |= AudioWarning.SignalTooQuiet;
        }

        if (Pitch.AssumedNominal is null && double.IsFinite(Pitch.DeviationCents))
        {
            warnings |= AudioWarning.NoNominalYet;
        }

        if (pitch is not null)
        {
            warnings |= AudioWarning.PitchMixesRecordTuning;
        }

        if (click is not null && pitch is not null && double.IsFinite(disagreement) && !agree)
        {
            warnings |= AudioWarning.MethodsDisagree;
        }

        if (credibleClick is null && Click.WindowSpanSeconds >= _options.ClickTimeoutSeconds)
        {
            warnings |= AudioWarning.NoClickPeriodicity;
        }

        return warnings;
    }
}
