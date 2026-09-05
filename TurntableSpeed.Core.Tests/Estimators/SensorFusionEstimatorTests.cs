using TurntableSpeed.Core.Estimators;
using TurntableSpeed.Core.Tests.Synth;

namespace TurntableSpeed.Core.Tests.Estimators;

/// <summary>
/// Spec §3.5 and §3.6: the magnetometer is the reference, the gyroscope's scale factor is
/// derived from it, disagreement beyond 2% is flagged, and the user is always told that the
/// phone's own weight is part of what is being measured.
/// </summary>
public class SensorFusionEstimatorTests
{
    private static SensorFusionEstimator Run(
        double rpm,
        double gyroScaleError = 0.0,
        double seconds = 60.0,
        double tiltDegrees = 0.0,
        double radiusMeters = 0.07,
        bool withGyro = true,
        bool withAccelerometer = true,
        GyroBias? bias = null)
    {
        var magnetometerGenerator = new MagnetometerSignalGenerator
        {
            Rpm = rpm,
            DurationSeconds = seconds,
            SampleRateHz = 50.0,
            AxisTiltDegrees = tiltDegrees,
        };

        var gyroGenerator = new GyroSignalGenerator
        {
            Rpm = rpm,
            DurationSeconds = seconds,
            SampleRateHz = 200.0,
            NoiseSigma = 0.005,
            ScaleFactorError = gyroScaleError,
        };

        var fusion = new SensorFusionEstimator { GyroscopeAvailable = withGyro };
        if (bias is not null)
        {
            fusion.ApplyGyroBias(bias);
        }

        foreach (var sample in magnetometerGenerator.Generate())
        {
            fusion.PushMagnetometer(sample);
        }

        if (withGyro)
        {
            foreach (var sample in gyroGenerator.Generate())
            {
                fusion.PushGyroscope(sample);
            }
        }

        if (withAccelerometer)
        {
            foreach (var sample in magnetometerGenerator.GenerateAccelerometer(radiusMeters))
            {
                fusion.PushAccelerometer(sample);
            }
        }

        fusion.Flush();
        return fusion;
    }

    [Fact]
    public void MagnetometerIsThePrimaryInstrument()
    {
        var fusion = Run(100.0 / 3.0, gyroScaleError: 0.015);

        var result = fusion.Result;

        Assert.NotNull(result.Primary);
        Assert.NotNull(result.Magnetometer);
        TestAssertions.Close(0.0, result.Primary!.Diagnostics["source"], 1e-9, "primary source is the magnetometer");
        TestAssertions.SpeedIs(100.0 / 3.0, result.Primary, Tolerances.MagnetometerRpmPercent60s, "fused speed");
    }

    [Fact]
    public void GyroscopeScaleFactorIsCalibratedAgainstTheMagnetometer()
    {
        const double scaleError = 0.015;
        var fusion = Run(100.0 / 3.0, gyroScaleError: scaleError);

        Assert.True(fusion.Result.GyroScaleCalibrated, "the scale factor should have been measured by now");
        TestAssertions.Close(1.0 / (1.0 + scaleError), fusion.Result.GyroScaleFactor,
            Tolerances.GyroScaleFactor, "calibrated gyroscope scale factor");
    }

    [Fact]
    public void CalibrationIsNotAttemptedFromAShortRecording()
    {
        // Five seconds is under the revolutions the calibration insists on.
        var fusion = Run(100.0 / 3.0, gyroScaleError: 0.02, seconds: 5.0);

        Assert.False(fusion.Result.GyroScaleCalibrated);
        TestAssertions.Close(1.0, fusion.Result.GyroScaleFactor, 1e-12, "uncalibrated scale factor stays at one");
    }

    [Fact]
    public void SmallDisagreementIsToleratedAndReported()
    {
        var fusion = Run(100.0 / 3.0, gyroScaleError: 0.01);

        var result = fusion.Result;

        TestAssertions.Close(1.0, result.DisagreementPercent, 0.3, "reported disagreement");
        Assert.True(result.IsReliable);
        Assert.False(result.Warnings.HasFlag(SensorWarning.MethodsDisagree));
    }

    [Fact]
    public void LargeDisagreementIsFlaggedRatherThanAveragedAway()
    {
        var fusion = Run(100.0 / 3.0, gyroScaleError: 0.06);

        var result = fusion.Result;

        TestAssertions.Close(6.0, result.DisagreementPercent, 0.5, "reported disagreement");
        Assert.False(result.IsReliable);
        Assert.True(result.Warnings.HasFlag(SensorWarning.MethodsDisagree));

        // The magnetometer reading itself must not be dragged toward the gyroscope.
        TestAssertions.SpeedIs(100.0 / 3.0, result.Primary, Tolerances.MagnetometerRpmPercent60s,
            "speed is still the magnetometer's, not a compromise");
    }

    [Fact]
    public void DisagreementCostsConfidence()
    {
        var agreeing = Run(100.0 / 3.0, gyroScaleError: 0.005);
        var arguing = Run(100.0 / 3.0, gyroScaleError: 0.06);

        Assert.True(arguing.Result.Primary!.Confidence < agreeing.Result.Primary!.Confidence,
            "a contradicted reading must not look as trustworthy as a corroborated one");
    }

    [Fact]
    public void LoadWarningIsAlwaysPresent()
    {
        // Spec §3.6: this mode measures the platter with a phone sitting on it, which is not
        // the speed during playback. The app must say so every single time.
        var fusion = Run(100.0 / 3.0);

        Assert.True(fusion.Result.Warnings.HasFlag(SensorWarning.LoadAffectsMeasurement));
        Assert.True(new SensorFusionEstimator().Result.Warnings.HasFlag(SensorWarning.LoadAffectsMeasurement),
            "even before any data arrives");
    }

    [Fact]
    public void MissingMagnetometerFallsBackToTheGyroscopeAndSaysSo()
    {
        var fusion = new SensorFusionEstimator { MagnetometerAvailable = false };

        foreach (var sample in new GyroSignalGenerator
                 {
                     Rpm = 100.0 / 3.0,
                     DurationSeconds = 30.0,
                     SampleRateHz = 200.0,
                     NoiseSigma = 0.005,
                     ScaleFactorError = 0.02,
                 }.Generate())
        {
            fusion.PushGyroscope(sample);
        }

        fusion.Flush();

        var result = fusion.Result;

        Assert.True(result.Warnings.HasFlag(SensorWarning.NoMagnetometer));
        Assert.NotNull(result.Primary);
        TestAssertions.Close(1.0, result.Primary!.Diagnostics["source"], 1e-9, "primary source is the gyroscope");

        // And the reading, though 2% off, owns up to it.
        TestAssertions.IntervalCovers(100.0 / 3.0, result.Primary, "gyroscope-only fallback");
        Assert.False(result.GyroScaleCalibrated);
    }

    [Fact]
    public void UncalibratedZeroIsReported()
    {
        var fusion = Run(100.0 / 3.0);

        Assert.True(fusion.Result.Warnings.HasFlag(SensorWarning.GyroZeroNotCalibrated));
    }

    [Fact]
    public void CalibratedZeroClearsTheWarning()
    {
        var bias = new GyroBias(0.01, -0.005, 0.002, 0.001, 0.001, 0.001, 500, 2.5);
        var fusion = Run(100.0 / 3.0, bias: bias);

        Assert.False(fusion.Result.Warnings.HasFlag(SensorWarning.GyroZeroNotCalibrated));
    }

    [Fact]
    public void TiltedPhoneIsFlagged()
    {
        var flat = Run(100.0 / 3.0, tiltDegrees: 0.0);
        var tilted = Run(100.0 / 3.0, tiltDegrees: 20.0);

        Assert.False(flat.Result.Warnings.HasFlag(SensorWarning.PhoneNotFlat));
        Assert.True(tilted.Result.Warnings.HasFlag(SensorWarning.PhoneNotFlat));
    }

    [Fact]
    public void OffCentrePlacementDoesNotLookLikeATiltedPhone()
    {
        // The centripetal term at 78 rpm eight centimetres out is over 5 m/s², which would read
        // as a 28° tilt if the check used the total acceleration vector.
        var fusion = Run(78.0, radiusMeters: 0.08, seconds: 30.0);

        Assert.False(fusion.Result.Warnings.HasFlag(SensorWarning.PhoneNotFlat),
            $"a flat phone placed off-centre must not be reported as tilted (tilt read " +
            $"{fusion.Result.TiltDegrees:F1}°)");
    }

    [Fact]
    public void PlacementRadiusIsOfferedAsASanityIndicator()
    {
        const double radius = 0.07;
        var fusion = Run(100.0 / 3.0, radiusMeters: radius);

        Assert.NotNull(fusion.Result.EstimatedRadiusMeters);
        TestAssertions.Close(radius, fusion.Result.EstimatedRadiusMeters!.Value, 0.01, "estimated placement radius");
    }

    [Fact]
    public void NotEnoughRevolutionsIsFlaggedEarlyOn()
    {
        var fusion = Run(100.0 / 3.0, seconds: 4.0);

        Assert.True(fusion.Result.Warnings.HasFlag(SensorWarning.NotEnoughRevolutions));
    }

    [Fact]
    public void MagneticDisturbanceIsFlagged()
    {
        var fusion = new SensorFusionEstimator();

        foreach (var sample in new MagnetometerSignalGenerator
                 {
                     Rpm = 100.0 / 3.0,
                     DurationSeconds = 60.0,
                     SampleRateHz = 50.0,
                     NoiseSigma = 4.0,
                     OutlierProbability = 0.05,
                     OutlierMagnitude = 25.0,
                 }.Generate())
        {
            fusion.PushMagnetometer(sample);
        }

        fusion.Flush();

        Assert.True(fusion.Result.Warnings.HasFlag(SensorWarning.MagneticDisturbance),
            $"phase residual was {fusion.Result.Magnetometer?.Diagnostics["phaseResidualDeg"]:F2}°");
    }

    [Fact]
    public void ResetClearsEverythingIncludingTheCalibration()
    {
        var fusion = Run(100.0 / 3.0, gyroScaleError: 0.015);
        Assert.True(fusion.Result.GyroScaleCalibrated);

        fusion.Reset();

        Assert.Null(fusion.Current);
        Assert.False(fusion.Result.GyroScaleCalibrated);
        TestAssertions.Close(1.0, fusion.Result.GyroScaleFactor, 1e-12, "scale factor after reset");
        Assert.True(fusion.Result.Warnings.HasFlag(SensorWarning.LoadAffectsMeasurement));
    }
}
