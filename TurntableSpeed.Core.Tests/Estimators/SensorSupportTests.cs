using TurntableSpeed.Core.Contracts;
using TurntableSpeed.Core.Estimators;
using TurntableSpeed.Core.Tests.Synth;

namespace TurntableSpeed.Core.Tests.Estimators;

/// <summary>
/// Spec §3.3 step 1: the app must actively ask for 2–3 seconds of stillness, and must be able
/// to tell whether it actually got them. A bias captured while the user was still setting the
/// phone down is worse than no bias at all.
/// </summary>
public class GyroZeroCalibratorTests
{
    [Fact]
    public void RecoversAKnownZeroOffset()
    {
        var bias = (X: 0.021, Y: -0.014, Z: 0.0075);
        var calibrator = new GyroZeroCalibrator();

        foreach (var sample in GyroSignalGenerator.Still(3.0, 200.0, bias, noiseSigma: 0.004))
        {
            calibrator.Push(sample);
        }

        var measured = calibrator.Compute();

        Assert.NotNull(measured);
        TestAssertions.Close(bias.X, measured!.X, Tolerances.GyroBias, "bias X");
        TestAssertions.Close(bias.Y, measured.Y, Tolerances.GyroBias, "bias Y");
        TestAssertions.Close(bias.Z, measured.Z, Tolerances.GyroBias, "bias Z");
        TestAssertions.Close(0.004, measured.MaxSigma, 5e-4, "reported noise");
    }

    [Fact]
    public void RefusesToProduceABiasBeforeItHasEnoughStillData()
    {
        var calibrator = new GyroZeroCalibrator();

        foreach (var sample in GyroSignalGenerator.Still(1.0, 200.0, (0.01, 0.0, 0.0), 0.002))
        {
            calibrator.Push(sample);
        }

        Assert.Null(calibrator.Compute());
        Assert.False(calibrator.HasEnoughData);
        Assert.True(calibrator.Progress < 1.0);
    }

    [Fact]
    public void ProgressReachesOneOnceTheRequiredStretchIsCollected()
    {
        var calibrator = new GyroZeroCalibrator();

        foreach (var sample in GyroSignalGenerator.Still(3.0, 200.0, (0.0, 0.0, 0.0), 0.002))
        {
            calibrator.Push(sample);
        }

        TestAssertions.Close(1.0, calibrator.Progress, 1e-9, "calibration progress");
        Assert.True(calibrator.HasEnoughData);
    }

    [Fact]
    public void DetectsThatThePlatterWasStillTurning()
    {
        var calibrator = new GyroZeroCalibrator();

        foreach (var sample in new GyroSignalGenerator { DurationSeconds = 3.0, SampleRateHz = 200.0 }.Generate())
        {
            calibrator.Push(sample);
        }

        Assert.False(calibrator.IsStill(out var reason));
        Assert.Contains("turning", reason);
    }

    [Fact]
    public void DetectsThatThePhoneWasBeingHandled()
    {
        var calibrator = new GyroZeroCalibrator();

        // Motionless on average, but far too noisy to be lying on a stopped platter.
        foreach (var sample in GyroSignalGenerator.Still(3.0, 200.0, (0.0, 0.0, 0.0), noiseSigma: 0.2))
        {
            calibrator.Push(sample);
        }

        Assert.False(calibrator.IsStill(out var reason));
        Assert.Contains("handled", reason);
    }

    [Fact]
    public void AcceptsAGenuinelyStillRecording()
    {
        var calibrator = new GyroZeroCalibrator();

        foreach (var sample in GyroSignalGenerator.Still(3.0, 200.0, (0.005, 0.0, -0.002), 0.003))
        {
            calibrator.Push(sample);
        }

        Assert.True(calibrator.IsStill(out var reason), $"expected a still verdict, got: {reason}");
    }

    [Fact]
    public void CorrectingASampleSubtractsTheOffset()
    {
        var bias = new GyroBias(1.0, 2.0, 3.0, 0, 0, 0, 10, 1.0);

        var corrected = bias.Correct(new Vector3Sample(5.0, 10.0, 20.0, 30.0));

        Assert.Equal(new Vector3Sample(5.0, 9.0, 18.0, 27.0), corrected);
    }
}

/// <summary>
/// Spec §3.4: the accelerometer is a check on the setup, not a speed instrument. The radius it
/// implies is a sanity indicator, because a = ω²·r has two unknowns.
/// </summary>
public class OrientationMonitorTests
{
    private static OrientationMonitor Run(double tiltDegrees, double horizontalAcceleration = 0.0, double seconds = 10.0)
    {
        var monitor = new OrientationMonitor();
        var tilt = tiltDegrees * Math.PI / 180.0;
        var rng = new Random(3);

        for (var i = 0; i < (int)(seconds * 100); i++)
        {
            monitor.Push(new Vector3Sample(
                i / 100.0,
                horizontalAcceleration + GyroSignalGenerator.Gaussian(rng, 0.01),
                9.80665 * Math.Sin(tilt) + GyroSignalGenerator.Gaussian(rng, 0.01),
                9.80665 * Math.Cos(tilt) + GyroSignalGenerator.Gaussian(rng, 0.01)));
        }

        return monitor;
    }

    [Fact]
    public void FlatPhoneReadsZeroTilt()
    {
        var monitor = Run(tiltDegrees: 0.0);

        TestAssertions.Close(0.0, monitor.TiltDegrees, Tolerances.TiltDegrees, "tilt of a flat phone");
        Assert.True(monitor.IsFlat);
        Assert.True(monitor.IsGravityPlausible);
    }

    [Theory]
    [InlineData(3.0, true)]
    [InlineData(12.0, false)]
    [InlineData(30.0, false)]
    public void TiltIsMeasuredAndComparedAgainstTheTolerance(double tiltDegrees, bool expectedFlat)
    {
        var monitor = Run(tiltDegrees);

        TestAssertions.Close(tiltDegrees, monitor.TiltDegrees, Tolerances.TiltDegrees, "measured tilt");
        Assert.Equal(expectedFlat, monitor.IsFlat);
    }

    [Fact]
    public void PlacementRadiusFollowsTheCentripetalRelation()
    {
        const double radius = 0.08;
        var omega = SpeedMath.RadiansPerSecondFromRpm(100.0 / 3.0);
        var monitor = Run(tiltDegrees: 0.0, horizontalAcceleration: omega * omega * radius);

        var estimated = monitor.EstimateRadiusMeters(omega);

        Assert.NotNull(estimated);
        TestAssertions.Close(radius, estimated!.Value, 0.005, "estimated placement radius");
    }

    [Fact]
    public void NoRadiusIsOfferedWhenThePhoneIsNotFlat()
    {
        var omega = SpeedMath.RadiansPerSecondFromRpm(100.0 / 3.0);
        var monitor = Run(tiltDegrees: 20.0, horizontalAcceleration: 0.1);

        Assert.Null(monitor.EstimateRadiusMeters(omega));
    }

    [Fact]
    public void AbsurdRadiusIsSuppressedRatherThanShown()
    {
        // 5 m/s² sideways at 33⅓ rpm implies a 41 cm radius: not a turntable.
        var omega = SpeedMath.RadiansPerSecondFromRpm(100.0 / 3.0);
        var monitor = Run(tiltDegrees: 0.0, horizontalAcceleration: 8.0);

        // Tilt is computed from the same vector, so a large sideways term is no longer "flat".
        Assert.Null(monitor.EstimateRadiusMeters(omega));
    }

    [Fact]
    public void NoDataMeansNoOpinion()
    {
        var monitor = new OrientationMonitor();

        Assert.False(monitor.HasData);
        Assert.True(double.IsNaN(monitor.TiltDegrees));
        Assert.False(monitor.IsFlat);
        Assert.Null(monitor.EstimateRadiusMeters(3.5));
    }
}
