using CsCheck;
using TurntableSpeed.Core.Contracts;
using TurntableSpeed.Core.Estimators;
using TurntableSpeed.Core.Tests.Synth;

namespace TurntableSpeed.Core.Tests.Properties;

/// <summary>
/// Spec §7.2: property-based tests. For any rate in [3.0, 5.0] rad/s, any reasonable noise
/// level and any initial phase, the estimate must converge to the truth with the stated
/// accuracy — and the boundary cases must refuse to be confident.
/// </summary>
public class MagnetometerPropertyTests
{
    [Fact]
    public void ConvergesForAnyRateNoiseAndPhase()
    {
        Gen.Select(
                Gen.Double[3.0, 5.0],           // rad/s, spanning 33⅓ through 45 and beyond
                Gen.Double[0.05, 1.5],          // magnetometer noise σ, µT
                Gen.Double[-Math.PI, Math.PI],  // initial phase
                Gen.Int[1, 10_000])             // noise seed
            .Sample(t =>
            {
                var (radiansPerSecond, noiseSigma, phase, seed) = t;

                var estimator = new MagnetometerSpeedEstimator();
                foreach (var sample in new MagnetometerSignalGenerator
                         {
                             Rpm = SpeedMath.RpmFromRadiansPerSecond(radiansPerSecond),
                             DurationSeconds = 60.0,
                             SampleRateHz = 50.0,
                             NoiseSigma = noiseSigma,
                             InitialPhase = phase,
                             Seed = seed,
                         }.Generate())
                {
                    estimator.Push(sample);
                }

                estimator.Flush();

                var truth = SpeedMath.RpmFromRadiansPerSecond(radiansPerSecond);
                TestAssertions.SpeedIs(truth, estimator.Current, Tolerances.MagnetometerRpmPercent60s,
                    $"ω={radiansPerSecond:F4} rad/s, σ={noiseSigma:F2} µT, φ₀={phase:F2}, seed={seed}",
                    Tolerances.MaxSigmaFromTruthRandomized);
            },
            iter: 40);
    }

    [Fact]
    public void DirectionOfRotationNeverChangesTheReportedSpeed()
    {
        Gen.Select(Gen.Double[3.0, 5.0], Gen.Int[1, 10_000])
            .Sample(t =>
            {
                var (radiansPerSecond, seed) = t;
                var rpm = SpeedMath.RpmFromRadiansPerSecond(radiansPerSecond);

                double Measure(double direction)
                {
                    var estimator = new MagnetometerSpeedEstimator();
                    foreach (var sample in new MagnetometerSignalGenerator
                             {
                                 Rpm = rpm,
                                 DurationSeconds = 30.0,
                                 Direction = direction,
                                 Seed = seed,
                             }.Generate())
                    {
                        estimator.Push(sample);
                    }

                    estimator.Flush();
                    return estimator.Current!.RevolutionsPerMinute;
                }

                TestAssertions.WithinPercent(Measure(1.0), Measure(-1.0), 0.05,
                    $"direction independence at {rpm:F3} rpm");
            },
            iter: 15);
    }

    [Fact]
    public void HardIronOffsetOfAnySizeIsRemoved()
    {
        Gen.Select(
                Gen.Double[3.0, 5.0],
                Gen.Double[-60.0, 60.0],
                Gen.Double[-60.0, 60.0],
                Gen.Int[1, 10_000])
            .Sample(t =>
            {
                var (radiansPerSecond, offsetX, offsetY, seed) = t;
                var rpm = SpeedMath.RpmFromRadiansPerSecond(radiansPerSecond);

                var estimator = new MagnetometerSpeedEstimator();
                foreach (var sample in new MagnetometerSignalGenerator
                         {
                             Rpm = rpm,
                             DurationSeconds = 60.0,
                             HardIron = (offsetX, offsetY, 0.0),
                             Seed = seed,
                         }.Generate())
                {
                    estimator.Push(sample);
                }

                estimator.Flush();

                TestAssertions.SpeedIs(rpm, estimator.Current, Tolerances.MagnetometerRpmPercent60s,
                    $"hard iron ({offsetX:F1}, {offsetY:F1}) µT at {rpm:F3} rpm",
                    Tolerances.MaxSigmaFromTruthRandomized);
            },
            iter: 25);
    }
}

public class ClassificationPropertyTests
{
    [Fact]
    public void AnySpeedWithinThreePercentOfANominalIsClassifiedAsThatNominal()
    {
        Gen.Select(Gen.Const(0), Gen.Double[-3.0, 3.0])
            .Sample(t =>
            {
                var (_, deviationPercent) = t;

                foreach (var nominal in NominalSpeeds.All)
                {
                    var rpm = nominal.Rpm * (1.0 + deviationPercent / 100.0);
                    var classification = NominalSpeeds.Classify(rpm);

                    Assert.Equal(nominal.Speed, classification.Nominal);
                    TestAssertions.Close(deviationPercent, classification.DeviationPercent, 1e-9,
                        $"deviation at {rpm:F4} rpm");
                    Assert.True(classification.Confidence > 0.4,
                        $"{rpm:F4} rpm is clearly {nominal.Label} but confidence was {classification.Confidence:F3}");
                }
            },
            iter: 60);
    }

    [Fact]
    public void NoSpeedInTheGapBetweenNominalsIsEverClassified()
    {
        // Spec §7.2 singles this out: a speed halfway between 33⅓ and 45 must not be claimed.
        Gen.Double[100.0 / 3.0 * 1.09, 45.0 * 0.91]
            .Sample(rpm =>
            {
                var classification = NominalSpeeds.Classify(rpm);

                Assert.Null(classification.Nominal);
                Assert.Equal(0.0, classification.Confidence);
            },
            iter: 200);
    }

    [Fact]
    public void ConfidenceNeverExceedsItsBounds()
    {
        Gen.Double[1.0, 200.0]
            .Sample(rpm =>
            {
                var classification = NominalSpeeds.Classify(rpm);

                Assert.InRange(classification.Confidence, 0.0, 1.0);

                var estimate = SpeedEstimate.FromRpm(rpm, 0.01, 1.0);
                Assert.InRange(estimate.Confidence, 0.0, 1.0);
            },
            iter: 500);
    }

    [Fact]
    public void CentsAndPercentAreExactInverses()
    {
        Gen.Double[-50.0, 100.0]
            .Sample(percent =>
            {
                var roundTrip = SpeedMath.PercentFromCents(SpeedMath.CentsFromPercent(percent));

                TestAssertions.Close(percent, roundTrip, 1e-9, $"round trip of {percent}%");
            },
            iter: 500);
    }

    [Fact]
    public void PeriodAndRpmAreExactInverses()
    {
        Gen.Double[10.0, 120.0]
            .Sample(rpm =>
            {
                TestAssertions.Close(rpm, SpeedMath.RpmFromPeriod(SpeedMath.PeriodFromRpm(rpm)), 1e-9,
                    $"period round trip of {rpm} rpm");
                TestAssertions.Close(rpm, SpeedMath.RpmFromRadiansPerSecond(
                    SpeedMath.RadiansPerSecondFromRpm(rpm)), 1e-9, $"rate round trip of {rpm} rpm");
            },
            iter: 500);
    }
}

public class ClickPeriodicityPropertyTests
{
    [Fact]
    public void RecoversAnySpeedAPlayerCanPlausiblyBeRunningAt()
    {
        // Any of the three nominals, off by up to ±6% — which spans everything from a stretched
        // belt to a mis-set pitch control, and stays inside the band the classifier will claim.
        Gen.Select(Gen.Int[0, 2], Gen.Double[-6.0, 6.0], Gen.Int[1, 10_000])
            .Sample(t =>
            {
                var (nominalIndex, deviationPercent, seed) = t;

                var truth = NominalSpeeds.All[nominalIndex].Rpm * (1.0 + deviationPercent / 100.0);
                var estimator = new ClickPeriodicityEstimator();

                new ClickTrainGenerator
                {
                    PeriodSeconds = 60.0 / truth,
                    DurationSeconds = 30.0,
                    Seed = seed,
                }.ToSource().PushAll(estimator.Push);

                estimator.Flush();

                TestAssertions.SpeedIs(truth, estimator.Current, Tolerances.ClickRpmPercent,
                    $"{truth:F4} rpm ({deviationPercent:+0.00;-0.00}% off {NominalSpeeds.All[nominalIndex].Label}), " +
                    $"seed {seed}",
                    Tolerances.MaxSigmaFromTruthRandomized);
            },
            iter: 15);
    }

    [Fact]
    public void AnAmbiguousCombIsResolvedTowardASpeedATurntableCanActuallyRunAt()
    {
        // Clicks 0.9 s apart are either a 66.7 rpm platter clicking once per revolution, or a
        // 33⅓ one clicking twice — the autocorrelation looks all but identical either way. No
        // turntable runs at 66.7, so the estimator resolves it as 33⅓. That prior is deliberate
        // and is the reason the nominal grid takes part in the decision at all; without it the
        // far commoner case of two clicks per revolution would read as double speed.
        var estimator = new ClickPeriodicityEstimator();

        new ClickTrainGenerator
        {
            PeriodSeconds = 0.9,
            DurationSeconds = 30.0,
        }.ToSource().PushAll(estimator.Push);

        estimator.Flush();

        Assert.NotNull(estimator.Current);
        Assert.Equal(NominalSpeed.Rpm33, estimator.Current!.Nominal);
        TestAssertions.Close(1.8, estimator.PeriodSeconds, 0.01, "resolved revolution period");
    }
}
