using TurntableSpeed.Core.Contracts;
using TurntableSpeed.Core.Dsp;
using TurntableSpeed.Core.Io;

namespace TurntableSpeed.Core.Tests.Synth;

/// <summary>
/// Tonal material with a known detuning (spec §7.1): a sine or a chord, offset by a given
/// number of cents, optionally put through the bandlimited resampler so the detuning arises
/// the way a real off-speed turntable produces it rather than being written in by hand.
/// </summary>
public sealed class TonalSignalGenerator
{
    /// <summary>Equal-tempered A3 / C#4 / E4 — an A major triad at A=440.</summary>
    public static double[] AMajorTriad { get; } = { 220.0, 277.1826309768721, 329.6275569128699 };

    public double[] Frequencies { get; init; } = { 220.0 };

    /// <summary>Number of harmonics per note, including the fundamental.</summary>
    public int Harmonics { get; init; } = 3;

    public double DurationSeconds { get; init; } = 12.0;

    public int SampleRate { get; init; } = 48000;

    public double StartTime { get; init; }

    /// <summary>Detuning written directly into the frequencies, cents.</summary>
    public double DetuneCents { get; init; }

    /// <summary>
    /// Playback speed error applied by resampling after synthesis. 1.01 means the platter runs
    /// 1% fast, which raises the pitch by 1200·log₂(1.01) ≈ 17.2 cents.
    /// </summary>
    public double SpeedFactor { get; init; } = 1.0;

    /// <summary>Once-per-revolution pitch modulation depth, cents. Feeds the wow spectrum.</summary>
    public double VibratoDepthCents { get; init; }

    public double VibratoFrequencyHz { get; init; } = 0.5555555555555556;

    public double NoiseAmplitude { get; init; } = 0.002;

    public double Amplitude { get; init; } = 0.25;

    public int Seed { get; init; } = 1;

    /// <summary>Total detuning the estimator should see, cents, including the speed error.</summary>
    public double TrueDetuneCents =>
        DetuneCents + 1200.0 * Math.Log(SpeedFactor, 2.0);

    public float[] Generate()
    {
        // Synthesise long enough that resampling still leaves the requested duration.
        var factor = Math.Max(1e-6, SpeedFactor);
        var synthesisSeconds = DurationSeconds * factor;
        var n = (int)Math.Round(synthesisSeconds * SampleRate);
        var x = new float[n];
        var rng = new Random(Seed);

        var detuneRatio = Math.Pow(2.0, DetuneCents / 1200.0);
        var dt = 1.0 / SampleRate;

        // Phase accumulation, not sin(2πft): with vibrato the two are not the same thing, and
        // only the accumulated form produces a genuine frequency modulation.
        var phases = new double[Frequencies.Length * Math.Max(1, Harmonics)];

        for (var i = 0; i < n; i++)
        {
            var t = i * dt;
            var modulation = VibratoDepthCents != 0.0
                ? Math.Pow(2.0, VibratoDepthCents * Math.Sin(2.0 * Math.PI * VibratoFrequencyHz * t) / 1200.0)
                : 1.0;

            var value = 0.0;
            var slot = 0;

            foreach (var fundamental in Frequencies)
            {
                for (var h = 1; h <= Math.Max(1, Harmonics); h++)
                {
                    var frequency = fundamental * detuneRatio * modulation * h;
                    phases[slot] += 2.0 * Math.PI * frequency * dt;
                    value += Amplitude / h * Math.Sin(phases[slot]);
                    slot++;
                }
            }

            x[i] = (float)(value + NoiseAmplitude * (rng.NextDouble() * 2.0 - 1.0));
        }

        return SpeedFactor == 1.0 ? x : Resampler.ChangeSpeed(x, factor);
    }

    public ReplayAudioSource ToSource(int blockSize = 4096) =>
        new(Generate(), SampleRate, blockSize, startTime: StartTime);

    public IEnumerable<AudioBlock> Blocks(int blockSize = 4096) =>
        ToSource(blockSize).Blocks();
}
