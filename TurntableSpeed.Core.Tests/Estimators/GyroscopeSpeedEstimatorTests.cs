using TurntableSpeed.Core.Contracts;
using TurntableSpeed.Core.Estimators;
using TurntableSpeed.Core.Tests.Synth;

namespace TurntableSpeed.Core.Tests.Estimators;

/// <summary>
/// The secondary sensor instrument. Spec §3.1: a MEMS gyroscope's factory scale-factor error is
/// 1–3%, which is larger than the quantity being measured — so the interesting assertions here
/// are about honesty, not accuracy.
/// </summary>
public class GyroscopeSpeedEstimatorTests
{
    private static GyroscopeSpeedEstimator Run(GyroSignalGenerator generator, GyroscopeSpeedEstimator? estimator = null)
    {
        estimator ??= new GyroscopeSpeedEstimator();
        foreach (var sample in generator.Generate())
        {
            estimator.Push(sample);
        }

        estimator.Flush();
        return estimator;
    }

    [Theory]
    [InlineData(100.0 / 3.0)]
    [InlineData(45.0)]
    [InlineData(78.0)]
    public void MeasuresSpeedWhenTheScaleFactorIsPerfect(double rpm)
    {
        var estimator = Run(new GyroSignalGenerator { Rpm = rpm, DurationSeconds = 30.0, NoiseSigma = 0.01 });

        TestAssertions.SpeedIs(rpm, estimator.Current, Tolerances.GyroscopeRpmPercentCalibrated,
            $"{rpm} rpm with an ideal gyroscope");
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(0.02)]
    [InlineData(-0.025)]
    public void UncalibratedScaleErrorIsCoveredByTheReportedInterval(double scaleError)
    {
        const double truth = 100.0 / 3.0;
        var estimator = Run(new GyroSignalGenerator
        {
            Rpm = truth,
            DurationSeconds = 30.0,
            NoiseSigma = 0.01,
            ScaleFactorError = scaleError,
        });

        var estimate = estimator.Current!;

        // The reading is wrong by roughly the scale error — that is expected and unavoidable.
        TestAssertions.WithinPercent(truth * (1.0 + scaleError), estimate.RevolutionsPerMinute,
            Tolerances.GyroscopeRpmPercentCalibrated, "raw uncalibrated reading");

        // What is not negotiable: the interval must own up to it.
        TestAssertions.IntervalCovers(truth, estimate, $"uncalibrated gyroscope with {scaleError:P0} scale error");
    }

    [Fact]
    public void ConfidenceIsCappedWhileTheScaleFactorIsUnknown()
    {
        var estimator = Run(new GyroSignalGenerator { DurationSeconds = 30.0, NoiseSigma = 0.005 });

        Assert.False(estimator.IsScaleCalibrated);
        Assert.True(estimator.Current!.Confidence <= 0.45,
            $"an uncalibrated gyroscope must not look authoritative, confidence was {estimator.Current.Confidence:F3}");
    }

    [Fact]
    public void CalibratingTheScaleFactorRemovesTheErrorAndTightensTheInterval()
    {
        const double truth = 100.0 / 3.0;
        const double scaleError = 0.02;
        var generator = new GyroSignalGenerator
        {
            Rpm = truth,
            DurationSeconds = 30.0,
            NoiseSigma = 0.01,
            ScaleFactorError = scaleError,
        };

        var uncalibrated = Run(generator);

        var calibrated = new GyroscopeSpeedEstimator();
        calibrated.ApplyScaleCalibration(1.0 / (1.0 + scaleError));
        Run(generator, calibrated);

        TestAssertions.SpeedIs(truth, calibrated.Current, Tolerances.GyroscopeRpmPercentCalibrated,
            "speed after scale calibration");
        Assert.True(calibrated.Current!.StandardError < uncalibrated.Current!.StandardError / 5.0,
            "removing the dominant systematic must visibly tighten the interval");
        Assert.True(calibrated.Current.Confidence > uncalibrated.Current.Confidence);
    }

    [Fact]
    public void ZeroOffsetIsSubtractedBeforeMeasuring()
    {
        const double truth = 100.0 / 3.0;
        var bias = (X: 0.02, Y: -0.015, Z: 0.03);

        var generator = new GyroSignalGenerator
        {
            Rpm = truth,
            DurationSeconds = 30.0,
            NoiseSigma = 0.005,
            Bias = bias,
        };

        var uncorrected = Run(generator);
        var corrected = Run(generator, new GyroscopeSpeedEstimator
        {
            Bias = new GyroBias(bias.X, bias.Y, bias.Z, 0, 0, 0, 100, 2.5),
        });

        TestAssertions.SpeedIs(truth, corrected.Current, Tolerances.GyroscopeRpmPercentCalibrated,
            "speed with the zero offset removed");

        // Without the correction the bias leaks straight into the rate.
        Assert.True(Math.Abs(uncorrected.Current!.RevolutionsPerMinute - truth) >
                    Math.Abs(corrected.Current!.RevolutionsPerMinute - truth),
            "removing the bias must help");
    }

    [Fact]
    public void WowAndFlutterSpectrumFindsTheInjectedModulation()
    {
        const double wowFrequency = 0.5555555555555556;
        const double wowDepth = 0.005;

        var estimator = Run(new GyroSignalGenerator
        {
            Rpm = 100.0 / 3.0,
            DurationSeconds = 30.0,
            SampleRateHz = 200.0,
            NoiseSigma = 0.002,
            WowDepth = wowDepth,
            WowFrequencyHz = wowFrequency,
        });

        var wow = estimator.WowFlutter;

        TestAssertions.WithinPercent(wowFrequency, wow.DominantFrequency, 2.0, "wow peak frequency");
        TestAssertions.WithinPercent(wowDepth * 100.0, wow.PeakPercent,
            Tolerances.WowDepthRelative * 100.0, "wow depth in percent");
    }

    [Fact]
    public void SteadyPlatterShowsNoWowPeakWorthReporting()
    {
        var estimator = Run(new GyroSignalGenerator
        {
            DurationSeconds = 30.0,
            SampleRateHz = 200.0,
            NoiseSigma = 0.002,
            WowDepth = 0.0,
        });

        Assert.True(estimator.WowFlutter.PeakPercent < 0.1,
            $"a steady platter should show almost no modulation, found {estimator.WowFlutter.PeakPercent:F3}%");
    }

    [Fact]
    public void SpinUpTimeIsMeasuredWhenTheRecordingStartsFromRest()
    {
        const double spinUp = 6.0;
        var estimator = Run(new GyroSignalGenerator
        {
            Rpm = 100.0 / 3.0,
            DurationSeconds = 40.0,
            SampleRateHz = 100.0,
            NoiseSigma = 0.002,
            SpinUpSeconds = spinUp,
        });

        Assert.NotNull(estimator.SpinUpSeconds);
        TestAssertions.Close(spinUp, estimator.SpinUpSeconds!.Value, 1.5, "measured spin-up time");
    }

    [Fact]
    public void SpinUpIsNotInventedWhenThePlatterWasAlreadyTurning()
    {
        var estimator = Run(new GyroSignalGenerator
        {
            DurationSeconds = 30.0,
            SampleRateHz = 100.0,
            NoiseSigma = 0.002,
            SpinUpSeconds = 0.0,
        });

        Assert.Null(estimator.SpinUpSeconds);
    }

    [Fact]
    public void RotationAxisIsFoundEvenWhenItIsNotTheZAxis()
    {
        var axis = GyroSignalGenerator.Normalize((0.3, -0.2, 1.0));
        var estimator = Run(new GyroSignalGenerator
        {
            DurationSeconds = 20.0,
            NoiseSigma = 0.005,
            Axis = axis,
        });

        Assert.NotNull(estimator.RotationAxis);
        var found = estimator.RotationAxis!.Value;
        var alignment = found.X * axis.X + found.Y * axis.Y + found.Z * axis.Z;
        TestAssertions.Close(1.0, alignment, 1e-3, "recovered rotation axis");
    }

    [Fact]
    public void InstantaneousRateTracksTheSmoothedSignal()
    {
        var estimator = Run(new GyroSignalGenerator
        {
            Rpm = 45.0,
            DurationSeconds = 20.0,
            NoiseSigma = 0.01,
        });

        TestAssertions.WithinPercent(SpeedMath.RadiansPerSecondFromRpm(45.0),
            estimator.InstantaneousRadPerSecond, 1.0, "instantaneous rate");
    }

    [Fact]
    public void BiasDriftShowsUpAsAWorseFitRatherThanASilentError()
    {
        const double truth = 100.0 / 3.0;
        var steady = Run(new GyroSignalGenerator { Rpm = truth, DurationSeconds = 30.0, NoiseSigma = 0.005 });
        var drifting = Run(new GyroSignalGenerator
        {
            Rpm = truth,
            DurationSeconds = 30.0,
            NoiseSigma = 0.005,
            BiasDriftPerSecond = (0.0, 0.0, 0.001),
        });

        Assert.True(drifting.Current!.StandardError > steady.Current!.StandardError,
            "a drifting zero must widen the interval");
    }

    [Fact]
    public void ResetReturnsTheEstimatorToItsInitialState()
    {
        var estimator = Run(new GyroSignalGenerator { DurationSeconds = 20.0 });
        Assert.NotNull(estimator.Current);

        estimator.Reset();

        Assert.Null(estimator.Current);
        Assert.Null(estimator.SpinUpSeconds);
        Assert.Equal(0, estimator.SampleCount);
        Assert.True(double.IsNaN(estimator.InstantaneousRadPerSecond));
    }
}
