using TurntableSpeed.Core.Contracts;
using TurntableSpeed.Core.Io;

namespace TurntableSpeed.Core.Tests.Synth;

/// <summary>
/// Surface-noise generator (spec §7.1): impulsive clicks at a given period with a given jitter,
/// over pink noise, optionally with programme material mixed in from a fixture. This is what
/// the primary acoustic estimator actually listens to.
/// </summary>
public sealed class ClickTrainGenerator
{
    public double PeriodSeconds { get; init; } = 1.8;

    public double DurationSeconds { get; init; } = 30.0;

    public int SampleRate { get; init; } = 48000;

    public double StartTime { get; init; }

    /// <summary>Uniform jitter applied to each click position, ± this many seconds.</summary>
    public double JitterSeconds { get; init; }

    /// <summary>
    /// Clicks per revolution. Two or three is common — a scratch and the joint of a repair —
    /// and is exactly the case that tempts an autocorrelation into reporting a sub-multiple.
    /// </summary>
    public int ClicksPerRevolution { get; init; } = 1;

    /// <summary>Amplitude of the once-per-revolution click.</summary>
    public double ClickAmplitude { get; init; } = 0.5;

    /// <summary>Amplitude of the additional clicks, when there is more than one.</summary>
    public double SecondaryClickAmplitude { get; init; } = 0.3;

    /// <summary>Decay time of a click, seconds. Real ones are a fraction of a millisecond.</summary>
    public double ClickDecaySeconds { get; init; } = 0.00025;

    public double PinkNoiseAmplitude { get; init; } = 0.01;

    public double WhiteNoiseAmplitude { get; init; } = 0.002;

    /// <summary>Programme material to mix under the clicks — a fixture, or a synthesised tone.</summary>
    public float[]? Programme { get; init; }

    public double ProgrammeGain { get; init; } = 1.0;

    public int Seed { get; init; } = 1;

    public double TrueRpm => 60.0 / PeriodSeconds;

    public float[] Generate()
    {
        var n = (int)Math.Round(DurationSeconds * SampleRate);
        var x = new float[n];
        var rng = new Random(Seed);
        var pink = new PinkNoise(new Random(Seed + 31));

        for (var i = 0; i < n; i++)
        {
            var value = PinkNoiseAmplitude * pink.Next()
                        + WhiteNoiseAmplitude * (rng.NextDouble() * 2.0 - 1.0);

            if (Programme is { Length: > 0 })
            {
                value += ProgrammeGain * Programme[i % Programme.Length];
            }

            x[i] = (float)value;
        }

        var clicksPerRev = Math.Max(1, ClicksPerRevolution);
        var decaySamples = Math.Max(1.0, ClickDecaySeconds * SampleRate);
        var length = (int)Math.Ceiling(decaySamples * 8.0);

        for (var revolution = 0; ; revolution++)
        {
            var revolutionStart = revolution * PeriodSeconds;
            if (revolutionStart > DurationSeconds)
            {
                break;
            }

            for (var c = 0; c < clicksPerRev; c++)
            {
                var amplitude = c == 0 ? ClickAmplitude : SecondaryClickAmplitude;
                var jitter = JitterSeconds > 0.0
                    ? (rng.NextDouble() * 2.0 - 1.0) * JitterSeconds
                    : 0.0;

                var centre = revolutionStart + c * PeriodSeconds / clicksPerRev + jitter;
                var start = (int)Math.Round(centre * SampleRate);

                for (var k = 0; k < length; k++)
                {
                    var index = start + k;
                    if (index < 0 || index >= n)
                    {
                        continue;
                    }

                    // A click is broadband and decays fast; the sign is random so a train of
                    // them has no DC component to fool the envelope follower.
                    var decay = Math.Exp(-k / decaySamples);
                    x[index] += (float)(amplitude * decay * (rng.NextDouble() * 2.0 - 1.0));
                }
            }
        }

        return x;
    }

    /// <summary>The generated signal as replayable audio blocks.</summary>
    public ReplayAudioSource ToSource(int blockSize = 4096) =>
        new(Generate(), SampleRate, blockSize, startTime: StartTime);

    public IEnumerable<AudioBlock> Blocks(int blockSize = 4096) =>
        ToSource(blockSize).Blocks();

    /// <summary>
    /// Paul Kellet's economy pink-noise filter. Deterministic given the seed, which matters:
    /// a fixture that changes between runs cannot be a golden reference.
    /// </summary>
    private sealed class PinkNoise
    {
        private readonly Random _rng;
        private double _b0, _b1, _b2;

        public PinkNoise(Random rng) => _rng = rng;

        public double Next()
        {
            var white = _rng.NextDouble() * 2.0 - 1.0;
            _b0 = 0.99765 * _b0 + white * 0.0990460;
            _b1 = 0.96300 * _b1 + white * 0.2965164;
            _b2 = 0.57000 * _b2 + white * 1.0526913;
            return (_b0 + _b1 + _b2 + white * 0.1848) * 0.3;
        }
    }
}
