using TurntableSpeed.Core.Contracts;

namespace TurntableSpeed.Core.Tests.Synth;

/// <summary>
/// Magnetometer stream with known-in-advance parameters (spec §7.1): the Earth's field vector
/// as seen from a rotating phone, plus a hard-iron offset, soft-iron ellipticity, white noise
/// and occasional gross outliers of the kind a passing magnet produces.
/// </summary>
public sealed class MagnetometerSignalGenerator
{
    public double Rpm { get; init; } = 100.0 / 3.0;

    public double SampleRateHz { get; init; } = 50.0;

    public double DurationSeconds { get; init; } = 60.0;

    public double StartTime { get; init; }

    /// <summary>Horizontal field component, µT. Around 20 µT in mid-latitudes.</summary>
    public double HorizontalFieldMicroTesla { get; init; } = 20.0;

    /// <summary>Vertical field component, µT. Around 45 µT in mid-latitudes.</summary>
    public double VerticalFieldMicroTesla { get; init; } = 45.0;

    /// <summary>Initial phase of the field in the phone's frame, radians.</summary>
    public double InitialPhase { get; init; }

    /// <summary>Sign of rotation: +1 or −1.</summary>
    public double Direction { get; init; } = 1.0;

    /// <summary>Constant offset from a magnetised platter, motor or phone case, µT.</summary>
    public (double X, double Y, double Z) HardIron { get; init; }

    /// <summary>
    /// Soft-iron distortion: the circle becomes an ellipse with this axis ratio, tilted by
    /// <see cref="SoftIronAngle"/>. 1.0 means none.
    /// </summary>
    public double SoftIronAxisRatio { get; init; } = 1.0;

    public double SoftIronAngle { get; init; }

    /// <summary>White noise σ per axis, µT. A phone magnetometer sits around 0.2–0.5 µT.</summary>
    public double NoiseSigma { get; init; } = 0.3;

    /// <summary>Probability per sample of a gross outlier.</summary>
    public double OutlierProbability { get; init; }

    /// <summary>Magnitude of an outlier, µT.</summary>
    public double OutlierMagnitude { get; init; } = 30.0;

    /// <summary>Tilt of the rotation axis away from the phone's +Z, degrees.</summary>
    public double AxisTiltDegrees { get; init; }

    /// <summary>Wow depth as a fraction of the rate — the platter is not perfectly steady.</summary>
    public double WowDepth { get; init; }

    public double WowFrequencyHz { get; init; } = 0.5555555555555556;

    public int Seed { get; init; } = 1;

    public double TrueRadiansPerSecond => SpeedMath.RadiansPerSecondFromRpm(Rpm);

    public Vector3Sample[] Generate()
    {
        var count = (int)Math.Round(DurationSeconds * SampleRateHz);
        var samples = new Vector3Sample[count];
        var rng = new Random(Seed);
        var dt = 1.0 / SampleRateHz;

        var omega = TrueRadiansPerSecond * Direction;
        var tilt = AxisTiltDegrees * Math.PI / 180.0;
        var cosTilt = Math.Cos(tilt);
        var sinTilt = Math.Sin(tilt);

        var cosSoft = Math.Cos(SoftIronAngle);
        var sinSoft = Math.Sin(SoftIronAngle);

        var phase = InitialPhase;

        for (var i = 0; i < count; i++)
        {
            var t = i * dt;

            // Integrate the (possibly modulated) rate so wow shows up as phase, not as a jump.
            if (i > 0)
            {
                var rate = omega;
                if (WowDepth != 0.0)
                {
                    rate *= 1.0 + WowDepth * Math.Sin(2.0 * Math.PI * WowFrequencyHz * t);
                }

                phase += rate * dt;
            }

            // The field seen from the rotating phone: horizontal part sweeps a circle.
            var x = HorizontalFieldMicroTesla * Math.Cos(phase);
            var y = HorizontalFieldMicroTesla * Math.Sin(phase);
            var z = VerticalFieldMicroTesla;

            // Soft iron: squeeze one axis of the circle in a rotated frame.
            if (SoftIronAxisRatio != 1.0)
            {
                var u = x * cosSoft + y * sinSoft;
                var v = -x * sinSoft + y * cosSoft;
                u *= SoftIronAxisRatio;
                x = u * cosSoft - v * sinSoft;
                y = u * sinSoft + v * cosSoft;
            }

            // Tilt the whole trajectory about the phone's X axis.
            if (AxisTiltDegrees != 0.0)
            {
                var yTilted = y * cosTilt - z * sinTilt;
                var zTilted = y * sinTilt + z * cosTilt;
                y = yTilted;
                z = zTilted;
            }

            x += HardIron.X + GyroSignalGenerator.Gaussian(rng, NoiseSigma);
            y += HardIron.Y + GyroSignalGenerator.Gaussian(rng, NoiseSigma);
            z += HardIron.Z + GyroSignalGenerator.Gaussian(rng, NoiseSigma);

            if (OutlierProbability > 0.0 && rng.NextDouble() < OutlierProbability)
            {
                x += (rng.NextDouble() * 2.0 - 1.0) * OutlierMagnitude;
                y += (rng.NextDouble() * 2.0 - 1.0) * OutlierMagnitude;
                z += (rng.NextDouble() * 2.0 - 1.0) * OutlierMagnitude;
            }

            samples[i] = new Vector3Sample(StartTime + t, x, y, z);
        }

        return samples;
    }

    /// <summary>
    /// Accelerometer stream matching this rotation: gravity in the phone's frame plus the
    /// centripetal term at a given placement radius.
    /// </summary>
    public Vector3Sample[] GenerateAccelerometer(double radiusMeters, double noiseSigma = 0.02)
    {
        var count = (int)Math.Round(DurationSeconds * SampleRateHz);
        var samples = new Vector3Sample[count];
        var rng = new Random(Seed + 977);
        var dt = 1.0 / SampleRateHz;

        var omega = TrueRadiansPerSecond;
        var centripetal = omega * omega * radiusMeters;
        var tilt = AxisTiltDegrees * Math.PI / 180.0;

        for (var i = 0; i < count; i++)
        {
            // In the phone's own frame the centripetal acceleration points at a fixed direction:
            // the phone rotates with the platter, so the centre of rotation does not move.
            samples[i] = new Vector3Sample(
                StartTime + i * dt,
                centripetal + GyroSignalGenerator.Gaussian(rng, noiseSigma),
                9.80665 * Math.Sin(tilt) + GyroSignalGenerator.Gaussian(rng, noiseSigma),
                9.80665 * Math.Cos(tilt) + GyroSignalGenerator.Gaussian(rng, noiseSigma));
        }

        return samples;
    }
}
