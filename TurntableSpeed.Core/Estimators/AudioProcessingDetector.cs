using TurntableSpeed.Core.Contracts;
using TurntableSpeed.Core.Dsp;

namespace TurntableSpeed.Core.Estimators;

/// <summary>What the input diagnostics found wrong with the captured audio.</summary>
[Flags]
public enum AudioInputWarning
{
    None = 0,

    /// <summary>Not enough audio analysed yet to say anything.</summary>
    NotEnoughData = 1 << 0,

    /// <summary>The platform itself admitted it could not turn some processing off.</summary>
    ProcessingNotDisabled = 1 << 1,

    /// <summary>
    /// The signal shows the signature of a noise gate: quiet passages collapsing to a floor far
    /// below anything a microphone in a room can produce. Exactly the processing that eats the
    /// clicks the primary acoustic algorithm lives on.
    /// </summary>
    NoiseGateSuspected = 1 << 2,

    /// <summary>Long-term level is being held constant while the material itself is dynamic.</summary>
    AutomaticGainControlSuspected = 1 << 3,

    Clipping = 1 << 4,

    SignalTooQuiet = 1 << 5,

    DcOffset = 1 << 6,

    /// <summary>Neither 44100 nor 48000 — the platform substituted something of its own.</summary>
    UnexpectedSampleRate = 1 << 7,
}

/// <summary>One short-term level measurement. Also what the UI's input meter draws.</summary>
public readonly record struct AudioLevelFrame(
    double T,
    double Rms,
    double Peak,
    double Mean,
    double ClippedFraction);

/// <summary>Verdict on the captured audio, refreshed as the stream runs.</summary>
/// <param name="PeakDbfs">Loudest sample in the analysis window, dBFS.</param>
/// <param name="RmsDbfs">Overall RMS over the analysis window, dBFS.</param>
/// <param name="NoiseFloorDbfs">5th percentile of the short-term level — the quiet passages.</param>
/// <param name="DynamicRangeDb">Peak minus noise floor.</param>
/// <param name="GateDepthDb">Median level minus noise floor. A gate makes this implausibly large.</param>
/// <param name="SilentFrameFraction">Fraction of frames at digital silence. A microphone never does this.</param>
/// <param name="SlowLevelStdDb">Spread of the 1 s-smoothed level.</param>
/// <param name="FrameLevelStdDb">Spread of the raw short-term level.</param>
/// <param name="SlowLevelExcess">
/// <see cref="SlowLevelStdDb"/> divided by what averaging that many frames would produce on its
/// own. Near 1 means the long-term level carries no variation beyond the arithmetic of
/// smoothing — which is what an AGC leaves behind. Real material scores several times that.
/// </param>
public sealed record AudioInputQuality(
    double PeakDbfs,
    double RmsDbfs,
    double NoiseFloorDbfs,
    double DynamicRangeDb,
    double GateDepthDb,
    double SilentFrameFraction,
    double ClippedSampleFraction,
    double DcOffset,
    double SlowLevelStdDb,
    double FrameLevelStdDb,
    double SlowLevelExcess,
    double AnalyzedSeconds,
    int SampleRate,
    AudioInputWarning Warnings)
{
    public static AudioInputQuality Unknown { get; } = new(
        double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
        double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
        0.0, 0, AudioInputWarning.NotEnoughData);

    /// <summary>Nothing suspicious at all.</summary>
    public bool IsClean => Warnings == AudioInputWarning.None;

    /// <summary>
    /// Measurement may proceed. A platform that merely refused the "unprocessed" flag is not
    /// fatal on its own — what matters is whether the signal actually looks processed.
    /// </summary>
    public bool IsUsable => (Warnings & (
        AudioInputWarning.NotEnoughData |
        AudioInputWarning.NoiseGateSuspected |
        AudioInputWarning.Clipping |
        AudioInputWarning.SignalTooQuiet)) == AudioInputWarning.None;
}

public sealed class AudioProcessingDetectorOptions
{
    /// <summary>Short-term level frame length, seconds.</summary>
    public double FrameSeconds { get; set; } = 0.02;

    public double WindowSeconds { get; set; } = 20.0;

    public double MinimumSpanSeconds { get; set; } = 2.0;

    public int MinimumFrames { get; set; } = 64;

    /// <summary>Frames between re-evaluations. Deterministic — never time-based.</summary>
    public int RecomputeEveryFrames { get; set; } = 25;

    /// <summary>Smoothing length behind the AGC test, seconds.</summary>
    public double SlowLevelSeconds { get; set; } = 1.0;

    /// <summary>Below this a frame counts as digital silence (≈ −90 dBFS).</summary>
    public double SilenceThreshold { get; set; } = 3e-5;

    public double SilentFractionThreshold { get; set; } = 0.02;

    /// <summary>A room recorded through a phone never has a floor this low.</summary>
    public double ImplausibleNoiseFloorDbfs { get; set; } = -80.0;

    public double GateDepthThresholdDb { get; set; } = 45.0;

    /// <summary>
    /// Slow-level excess below this, on material that is dynamic in the short term, means the
    /// long-term level is being held. Comparing against the spread that averaging produces by
    /// itself rather than against a fixed number of decibels: a fixed threshold sits right on
    /// the noise floor of the statistic and cannot separate the two cases.
    /// </summary>
    public double AgcSlowLevelExcess { get; set; } = 2.5;

    public double AgcFrameLevelStdDb { get; set; } = 4.0;

    public double ClipThreshold { get; set; } = 0.999;

    public double ClippedFractionThreshold { get; set; } = 1e-4;

    public double QuietRmsDbfs { get; set; } = -55.0;

    public double DcOffsetThreshold { get; set; } = 0.01;
}

/// <summary>
/// Section 4.1 of the spec, and a precondition for the whole acoustic mode: the platform is
/// asked to hand over an unprocessed input, but it is free to lie. This watches the signal
/// itself for the fingerprints of a noise gate, an AGC or a clipped front end, so the app can
/// warn instead of silently measuring a mangled signal.
/// </summary>
public sealed class AudioProcessingDetector
{
    private readonly AudioProcessingDetectorOptions _options;
    private readonly TimeWindow<AudioLevelFrame> _frames;

    private int _sampleRate;
    private int _frameSamples;
    private int _filled;
    private int _sinceRecompute;
    private double _frameStart;
    private double _sumSquares;
    private double _sum;
    private double _peak;
    private int _clipped;

    public AudioProcessingDetector(AudioProcessingDetectorOptions? options = null)
    {
        _options = options ?? new AudioProcessingDetectorOptions();
        var expectedRate = Math.Max(8.0, 1.0 / _options.FrameSeconds);
        _frames = new TimeWindow<AudioLevelFrame>(
            f => f.T,
            _options.WindowSeconds,
            maxCount: (int)(_options.WindowSeconds * expectedRate) + 256);
    }

    public AudioInputQuality Current { get; private set; } = AudioInputQuality.Unknown;

    /// <summary>What the platform claims it did. Null until capture reports back.</summary>
    public AudioCaptureReport? CaptureReport { get; private set; }

    /// <summary>Short-term levels retained for the input meter, oldest first.</summary>
    public IReadOnlyList<AudioLevelFrame> Frames => _frames;

    public long FrameCount => _frames.TotalAdded;

    public void SetCaptureReport(AudioCaptureReport report)
    {
        CaptureReport = report;
        Recompute();
    }

    public void Push(AudioBlock block)
    {
        var span = block.Samples.Span;
        if (block.SampleRate <= 0 || span.Length == 0)
        {
            return;
        }

        if (block.SampleRate != _sampleRate)
        {
            _sampleRate = block.SampleRate;
            _frameSamples = Math.Max(16, (int)Math.Round(_sampleRate * _options.FrameSeconds));
            ResetAccumulator();
        }

        var dt = 1.0 / _sampleRate;
        for (var i = 0; i < span.Length; i++)
        {
            if (_filled == 0)
            {
                _frameStart = block.T + i * dt;
            }

            var x = span[i];
            if (!float.IsFinite(x))
            {
                x = 0f;
            }

            var magnitude = Math.Abs((double)x);
            _sumSquares += (double)x * x;
            _sum += x;
            if (magnitude > _peak)
            {
                _peak = magnitude;
            }

            if (magnitude >= _options.ClipThreshold)
            {
                _clipped++;
            }

            if (++_filled < _frameSamples)
            {
                continue;
            }

            _frames.Add(new AudioLevelFrame(
                _frameStart,
                Math.Sqrt(_sumSquares / _frameSamples),
                _peak,
                _sum / _frameSamples,
                _clipped / (double)_frameSamples));

            ResetAccumulator();

            if (++_sinceRecompute >= _options.RecomputeEveryFrames)
            {
                _sinceRecompute = 0;
                Recompute();
            }
        }
    }

    public void Reset()
    {
        _frames.Clear();
        Current = AudioInputQuality.Unknown;
        _sampleRate = 0;
        _sinceRecompute = 0;
        ResetAccumulator();
    }

    private void ResetAccumulator()
    {
        _filled = 0;
        _sumSquares = 0.0;
        _sum = 0.0;
        _peak = 0.0;
        _clipped = 0;
    }

    private void Recompute()
    {
        var count = _frames.Count;
        var platformWarnings = PlatformWarnings();

        if (count < _options.MinimumFrames || _frames.Span < _options.MinimumSpanSeconds)
        {
            Current = AudioInputQuality.Unknown with
            {
                SampleRate = _sampleRate,
                AnalyzedSeconds = _frames.Span,
                Warnings = AudioInputWarning.NotEnoughData | platformWarnings,
            };
            return;
        }

        var rms = _frames.Select(f => f.Rms);
        var levelsDb = new double[count];
        for (var i = 0; i < count; i++)
        {
            levelsDb[i] = Db(rms[i]);
        }

        var sorted = (double[])rms.Clone();
        Array.Sort(sorted);

        var noiseFloor = Db(SeriesMath.PercentileOfSorted(sorted, 0.05));
        var median = Db(SeriesMath.PercentileOfSorted(sorted, 0.5));
        var peak = Db(SeriesMath.Max(_frames.Select(f => f.Peak)));
        var overallRms = Db(SeriesMath.RootMeanSquare(rms));

        var silent = 0;
        for (var i = 0; i < count; i++)
        {
            if (rms[i] < _options.SilenceThreshold)
            {
                silent++;
            }
        }

        var silentFraction = silent / (double)count;
        var clippedFraction = SeriesMath.Mean(_frames.Select(f => f.ClippedFraction));
        var dcOffset = SeriesMath.Mean(_frames.Select(f => f.Mean));

        var slowWindow = Math.Max(3, (int)Math.Round(_options.SlowLevelSeconds / _options.FrameSeconds));
        var slow = SeriesMath.MovingAverage(levelsDb, slowWindow);
        var slowStd = SeriesMath.StandardDeviation(slow);
        var frameStd = SeriesMath.StandardDeviation(levelsDb);

        // Averaging w frames of scatter σ leaves σ/√w behind even on a perfectly steady source.
        // Anything at that level is the smoothing talking, not the material.
        var expectedSlowStd = frameStd / Math.Sqrt(slowWindow);
        var slowExcess = expectedSlowStd > 0.0 ? slowStd / expectedSlowStd : double.NaN;

        var gateDepth = median - noiseFloor;

        var warnings = platformWarnings;

        // A gate leaves two fingerprints: frames pushed to digital silence, and an implausibly
        // deep gap between the programme level and the "floor" between transients.
        if (silentFraction > _options.SilentFractionThreshold ||
            (noiseFloor < _options.ImplausibleNoiseFloorDbfs && gateDepth > _options.GateDepthThresholdDb))
        {
            warnings |= AudioInputWarning.NoiseGateSuspected;
        }

        // AGC holds the long-term level still. Only meaningful when the material has dynamics
        // of its own to flatten, hence the second condition.
        if (double.IsFinite(slowExcess) &&
            slowExcess < _options.AgcSlowLevelExcess &&
            frameStd > _options.AgcFrameLevelStdDb)
        {
            warnings |= AudioInputWarning.AutomaticGainControlSuspected;
        }

        if (clippedFraction > _options.ClippedFractionThreshold)
        {
            warnings |= AudioInputWarning.Clipping;
        }

        if (overallRms < _options.QuietRmsDbfs)
        {
            warnings |= AudioInputWarning.SignalTooQuiet;
        }

        if (Math.Abs(dcOffset) > _options.DcOffsetThreshold)
        {
            warnings |= AudioInputWarning.DcOffset;
        }

        Current = new AudioInputQuality(
            peak,
            overallRms,
            noiseFloor,
            peak - noiseFloor,
            gateDepth,
            silentFraction,
            clippedFraction,
            dcOffset,
            slowStd,
            frameStd,
            slowExcess,
            _frames.Span,
            _sampleRate,
            warnings);
    }

    private AudioInputWarning PlatformWarnings()
    {
        var warnings = AudioInputWarning.None;

        if (CaptureReport is { } report && !report.IsClean)
        {
            warnings |= AudioInputWarning.ProcessingNotDisabled;
        }

        if (_sampleRate > 0 && _sampleRate != 44100 && _sampleRate != 48000)
        {
            warnings |= AudioInputWarning.UnexpectedSampleRate;
        }

        return warnings;
    }

    private static double Db(double amplitude) =>
        20.0 * Math.Log10(Math.Max(amplitude, 1e-12));
}
