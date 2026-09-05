using TurntableSpeed.Core.Contracts;
using TurntableSpeed.Core.Estimators;
using TurntableSpeed.Core.Tests.Synth;

namespace TurntableSpeed.Core.Tests.Estimators;

/// <summary>
/// The reference instrument of the sensor mode. Spec §3.2 states the requirement outright:
/// over 60 seconds the method must be better than 0.05%.
/// </summary>
public class MagnetometerSpeedEstimatorTests
{
    private static SpeedEstimate? Run(MagnetometerSignalGenerator generator, MagnetometerSpeedEstimator? estimator = null)
    {
        estimator ??= new MagnetometerSpeedEstimator();
        foreach (var sample in generator.Generate())
        {
            estimator.Push(sample);
        }

        estimator.Flush();
        return estimator.Current;
    }

    [Theory]
    [InlineData(100.0 / 3.0)]
    [InlineData(45.0)]
    [InlineData(78.0)]
    public void MeetsTheSpecifiedAccuracyOverSixtySeconds(double rpm)
    {
        var generator = new MagnetometerSignalGenerator { Rpm = rpm, DurationSeconds = 60.0 };

        var estimate = Run(generator);

        TestAssertions.SpeedIs(rpm, estimate, Tolerances.MagnetometerRpmPercent60s, $"{rpm} rpm over 60 s");
    }

    [Fact]
    public void ClassifiesTheNominalSpeedItMeasured()
    {
        var estimate = Run(new MagnetometerSignalGenerator { Rpm = 100.0 / 3.0, DurationSeconds = 60.0 });

        Assert.Equal(NominalSpeed.Rpm33, estimate!.Nominal);
        TestAssertions.Close(0.0, estimate.DeviationPercent, 0.05, "deviation from nominal");
    }

    [Fact]
    public void MeasuresAPlatterRunningFast()
    {
        // A turntable 1.2% fast — the kind of error the app exists to find.
        const double truth = 100.0 / 3.0 * 1.012;
        var estimate = Run(new MagnetometerSignalGenerator { Rpm = truth, DurationSeconds = 60.0 });

        TestAssertions.SpeedIs(truth, estimate, Tolerances.MagnetometerRpmPercent60s, "1.2% fast platter");
        Assert.Equal(NominalSpeed.Rpm33, estimate!.Nominal);
        TestAssertions.Close(1.2, estimate.DeviationPercent, 0.05, "reported deviation");
        TestAssertions.Close(20.65, estimate.DeviationCents, 0.5, "reported pitch error");
    }

    [Fact]
    public void HardIronOffsetDoesNotBiasTheSpeedAndIsItselfRecovered()
    {
        var generator = new MagnetometerSignalGenerator
        {
            Rpm = 100.0 / 3.0,
            DurationSeconds = 60.0,
            HardIron = (35.0, -18.0, 12.0),
        };

        var estimator = new MagnetometerSpeedEstimator();
        var estimate = Run(generator, estimator);

        TestAssertions.SpeedIs(100.0 / 3.0, estimate, Tolerances.MagnetometerRpmPercent60s, "speed under hard iron");

        Assert.NotNull(estimator.HardIronOffset);
        var offset = estimator.HardIronOffset!.Value;
        TestAssertions.Close(35.0, offset.X, 1.0, "recovered hard-iron X");
        TestAssertions.Close(-18.0, offset.Y, 1.0, "recovered hard-iron Y");
    }

    [Fact]
    public void SoftIronEllipticityDoesNotBiasTheSpeed()
    {
        var generator = new MagnetometerSignalGenerator
        {
            Rpm = 45.0,
            DurationSeconds = 60.0,
            SoftIronAxisRatio = 0.65,
            SoftIronAngle = 0.6,
        };

        var estimator = new MagnetometerSpeedEstimator();
        var estimate = Run(generator, estimator);

        TestAssertions.SpeedIs(45.0, estimate, Tolerances.MagnetometerRpmPercent60s, "speed under soft iron");

        Assert.NotNull(estimator.Ellipse);
        TestAssertions.Close(1.0 / 0.65, estimator.Ellipse!.AxisRatio, 0.05, "recovered soft-iron axis ratio");
    }

    [Fact]
    public void SoftIronCorrectionActuallyMattersForPhaseLinearity()
    {
        // Turn the correction off and the phase of an elliptical trajectory is no longer linear
        // in time. The slope survives averaging, but the residual — and hence the reported
        // uncertainty — must visibly worsen.
        var generator = new MagnetometerSignalGenerator
        {
            Rpm = 100.0 / 3.0,
            DurationSeconds = 60.0,
            SoftIronAxisRatio = 0.6,
            NoiseSigma = 0.05,
        };

        var corrected = Run(generator, new MagnetometerSpeedEstimator());
        var uncorrected = Run(generator, new MagnetometerSpeedEstimator(
            new MagnetometerSpeedEstimatorOptions { ApplySoftIronCorrection = false }));

        var correctedResidual = corrected!.Diagnostics["phaseResidualDeg"];
        var uncorrectedResidual = uncorrected!.Diagnostics["phaseResidualDeg"];

        Assert.True(uncorrectedResidual > 5.0 * correctedResidual,
            $"soft-iron correction should dominate the residual: {correctedResidual:F3}° corrected " +
            $"against {uncorrectedResidual:F3}° uncorrected");
    }

    [Fact]
    public void RobustRegressionSurvivesMagneticOutliers()
    {
        var generator = new MagnetometerSignalGenerator
        {
            Rpm = 100.0 / 3.0,
            DurationSeconds = 60.0,
            OutlierProbability = 0.02,
            OutlierMagnitude = 40.0,
            Seed = 5,
        };

        var estimate = Run(generator);

        TestAssertions.WithinPercent(100.0 / 3.0, estimate!.RevolutionsPerMinute,
            Tolerances.MagnetometerRpmPercentShort, "speed with 2% gross outliers");
        Assert.True(estimate.Diagnostics["outlierFraction"] > 0.0, "outliers should be reported, not hidden");
    }

    [Fact]
    public void OutliersAreVisibleInTheDiagnosticsAndCostConfidence()
    {
        var clean = Run(new MagnetometerSignalGenerator { DurationSeconds = 60.0, Seed = 5 });
        var dirty = Run(new MagnetometerSignalGenerator
        {
            DurationSeconds = 60.0,
            OutlierProbability = 0.05,
            OutlierMagnitude = 60.0,
            Seed = 5,
        });

        Assert.True(dirty!.Confidence < clean!.Confidence,
            $"a disturbed recording must not look as trustworthy: {dirty.Confidence:F3} vs {clean.Confidence:F3}");
    }

    [Theory]
    [InlineData(10.0)]
    [InlineData(30.0)]
    public void TiltedRotationAxisIsHandledByThePlaneFit(double tiltDegrees)
    {
        var generator = new MagnetometerSignalGenerator
        {
            Rpm = 100.0 / 3.0,
            DurationSeconds = 60.0,
            AxisTiltDegrees = tiltDegrees,
        };

        var estimator = new MagnetometerSpeedEstimator();
        var estimate = Run(generator, estimator);

        TestAssertions.SpeedIs(100.0 / 3.0, estimate, Tolerances.MagnetometerRpmPercent60s,
            $"speed with a {tiltDegrees}° axis tilt");

        Assert.NotNull(estimator.RotationAxis);
        var axis = estimator.RotationAxis!.Value;
        var expected = (0.0, -Math.Sin(tiltDegrees * Math.PI / 180.0), Math.Cos(tiltDegrees * Math.PI / 180.0));
        var alignment = Math.Abs(axis.X * expected.Item1 + axis.Y * expected.Item2 + axis.Z * expected.Item3);
        TestAssertions.Close(1.0, alignment, 0.02, "recovered rotation axis");
    }

    [Fact]
    public void DirectionOfRotationIsReportedButDoesNotChangeTheSpeed()
    {
        var forward = Run(new MagnetometerSignalGenerator { DurationSeconds = 30.0, Direction = 1.0 });
        var backward = Run(new MagnetometerSignalGenerator { DurationSeconds = 30.0, Direction = -1.0 });

        TestAssertions.WithinPercent(forward!.RevolutionsPerMinute, backward!.RevolutionsPerMinute, 0.05,
            "speed is independent of direction");
        TestAssertions.Close(1.0, forward.Diagnostics["direction"], 1e-9, "forward direction flag");
        TestAssertions.Close(-1.0, backward.Diagnostics["direction"], 1e-9, "reverse direction flag");
    }

    [Fact]
    public void RefusesToAnswerBeforeAFullRevolution()
    {
        // One second at 33⅓ rpm is barely half a turn: the ellipse centre is not identifiable.
        var estimate = Run(new MagnetometerSignalGenerator { DurationSeconds = 1.0, SampleRateHz = 100.0 });

        Assert.Null(estimate);
    }

    [Fact]
    public void PrecisionImprovesWithLongerObservation()
    {
        var short5 = Run(new MagnetometerSignalGenerator { DurationSeconds = 5.0 });
        var long60 = Run(new MagnetometerSignalGenerator { DurationSeconds = 60.0 });

        Assert.True(long60!.StandardError < short5!.StandardError,
            $"60 s must beat 5 s: {long60.StandardError:E3} against {short5.StandardError:E3}");
        Assert.True(long60.Confidence > short5.Confidence, "and it should say so in the confidence");
    }

    [Fact]
    public void WowInThePlatterDoesNotBiasTheAverageSpeed()
    {
        var generator = new MagnetometerSignalGenerator
        {
            Rpm = 100.0 / 3.0,
            DurationSeconds = 60.0,
            WowDepth = 0.01,
        };

        var estimate = Run(generator);

        TestAssertions.SpeedIs(100.0 / 3.0, estimate, Tolerances.MagnetometerRpmPercent60s, "mean speed under 1% wow");
    }

    [Fact]
    public void ResetReturnsTheEstimatorToItsInitialState()
    {
        var estimator = new MagnetometerSpeedEstimator();
        Run(new MagnetometerSignalGenerator { DurationSeconds = 30.0 }, estimator);

        Assert.NotNull(estimator.Current);

        estimator.Reset();

        Assert.Null(estimator.Current);
        Assert.Null(estimator.Ellipse);
        Assert.Null(estimator.RotationAxis);
        Assert.Equal(0, estimator.SampleCount);
    }

    [Fact]
    public void OutOfOrderAndNonFiniteSamplesAreIgnored()
    {
        var estimator = new MagnetometerSpeedEstimator();
        var samples = new MagnetometerSignalGenerator { DurationSeconds = 30.0 }.Generate();

        foreach (var sample in samples)
        {
            estimator.Push(sample);
            estimator.Push(sample with { T = sample.T - 1.0 });          // out of order
            estimator.Push(sample with { X = double.NaN });              // not finite
        }

        estimator.Flush();

        TestAssertions.SpeedIs(100.0 / 3.0, estimator.Current, Tolerances.MagnetometerRpmPercent60s,
            "speed with junk samples interleaved");
        Assert.Equal(samples.Length, estimator.SampleCount);
    }

    [Fact]
    public void EveryReadingCarriesAnInterval()
    {
        var estimate = Run(new MagnetometerSignalGenerator { DurationSeconds = 30.0 });

        Assert.NotNull(estimate);
        Assert.True(estimate!.StandardError > 0.0, "a point value with no interval is meaningless here");
        Assert.True(estimate.Margin95 > estimate.StandardError, "the 95% margin must be wider than 1σ");
        Assert.True(estimate.LowerBound95 < estimate.RevolutionsPerMinute);
        Assert.True(estimate.UpperBound95 > estimate.RevolutionsPerMinute);
    }
}
