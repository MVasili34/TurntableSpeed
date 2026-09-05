using TurntableSpeed.Core.Contracts;

namespace TurntableSpeed.Core.Tests.Synth;

/// <summary>
/// Gyroscope stream with known-in-advance parameters (spec §7.1): a constant rate, sinusoidal
/// wow of a given depth and frequency, white noise of a given σ, and a linearly drifting zero
/// offset. Also models the factory scale-factor error, which is the whole reason the gyroscope
/// is the secondary instrument.
/// </summary>
public sealed class GyroSignalGenerator
{
    /// <summary>True platter speed. Everything else is expressed relative to this.</summary>
    public double Rpm { get; init; } = 100.0 / 3.0;

    public double SampleRateHz { get; init; } = 200.0;

    public double DurationSeconds { get; init; } = 30.0;

    public double StartTime { get; init; }

    /// <summary>Wow depth as a fraction of the rate, e.g. 0.005 for ±0.5%.</summary>
    public double WowDepth { get; init; }

    public double WowFrequencyHz { get; init; } = 0.5555555555555556;

    public double WowPhase { get; init; }

    /// <summary>White noise standard deviation on each axis, rad/s.</summary>
    public double NoiseSigma { get; init; }

    /// <summary>Constant zero offset, rad/s.</summary>
    public (double X, double Y, double Z) Bias { get; init; }

    /// <summary>Linear zero drift, rad/s per second.</summary>
    public (double X, double Y, double Z) BiasDriftPerSecond { get; init; }

    /// <summary>Rotation axis in phone coordinates; normalised internally.</summary>
    public (double X, double Y, double Z) Axis { get; init; } = (0.0, 0.0, 1.0);

    /// <summary>Factory scale-factor error, e.g. 0.02 for a gyroscope reading 2% high.</summary>
    public double ScaleFactorError { get; init; }

    /// <summary>
    /// Seconds spent accelerating from rest at the start of the recording. Zero means the
    /// platter is already up to speed.
    /// </summary>
    public double SpinUpSeconds { get; init; }

    public int Seed { get; init; } = 1;

    public double TrueRadiansPerSecond => SpeedMath.RadiansPerSecondFromRpm(Rpm);

    public Vector3Sample[] Generate()
    {
        var count = (int)Math.Round(DurationSeconds * SampleRateHz);
        var samples = new Vector3Sample[count];
        var rng = new Random(Seed);
        var dt = 1.0 / SampleRateHz;

        var (ax, ay, az) = Normalize(Axis);
        var omega0 = TrueRadiansPerSecond;

        for (var i = 0; i < count; i++)
        {
            var t = i * dt;

            var rate = omega0;

            if (SpinUpSeconds > 0.0)
            {
                // A belt drive approaches speed exponentially, not linearly; τ chosen so the
                // rate is within 1% of nominal at SpinUpSeconds.
                var tau = SpinUpSeconds / Math.Log(100.0);
                rate *= 1.0 - Math.Exp(-t / tau);
            }

            if (WowDepth != 0.0)
            {
                rate *= 1.0 + WowDepth * Math.Sin(2.0 * Math.PI * WowFrequencyHz * t + WowPhase);
            }

            var measured = rate * (1.0 + ScaleFactorError);

            samples[i] = new Vector3Sample(
                StartTime + t,
                measured * ax + Bias.X + BiasDriftPerSecond.X * t + Gaussian(rng, NoiseSigma),
                measured * ay + Bias.Y + BiasDriftPerSecond.Y * t + Gaussian(rng, NoiseSigma),
                measured * az + Bias.Z + BiasDriftPerSecond.Z * t + Gaussian(rng, NoiseSigma));
        }

        return samples;
    }

    /// <summary>A stationary recording — what the zero calibration is supposed to consume.</summary>
    public static Vector3Sample[] Still(
        double seconds,
        double sampleRateHz,
        (double X, double Y, double Z) bias,
        double noiseSigma,
        int seed = 1,
        double startTime = 0.0)
    {
        var count = (int)Math.Round(seconds * sampleRateHz);
        var samples = new Vector3Sample[count];
        var rng = new Random(seed);

        for (var i = 0; i < count; i++)
        {
            samples[i] = new Vector3Sample(
                startTime + i / sampleRateHz,
                bias.X + Gaussian(rng, noiseSigma),
                bias.Y + Gaussian(rng, noiseSigma),
                bias.Z + Gaussian(rng, noiseSigma));
        }

        return samples;
    }

    internal static (double X, double Y, double Z) Normalize((double X, double Y, double Z) v)
    {
        var length = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return length > 0.0 ? (v.X / length, v.Y / length, v.Z / length) : (0.0, 0.0, 1.0);
    }

    /// <summary>Box–Muller, deterministic for a given <see cref="Random"/> sequence.</summary>
    internal static double Gaussian(Random rng, double sigma)
    {
        if (sigma <= 0.0)
        {
            return 0.0;
        }

        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return sigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
