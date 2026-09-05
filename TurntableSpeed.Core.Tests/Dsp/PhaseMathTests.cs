using System.Numerics;
using TurntableSpeed.Core.Dsp;

namespace TurntableSpeed.Core.Tests.Dsp;

/// <summary>
/// Spec §7.3: phase unwrapping across ±π, and circular means on data that crosses the wrap
/// boundary. Both are load-bearing — the magnetometer estimator is a phase regression, and the
/// pitch estimator is a circular mean over a semitone.
/// </summary>
public class PhaseMathTests
{
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(Math.PI, Math.PI)]
    [InlineData(-Math.PI, Math.PI)]
    [InlineData(3.0 * Math.PI, Math.PI)]
    [InlineData(2.0 * Math.PI, 0.0)]
    [InlineData(-2.0 * Math.PI, 0.0)]
    [InlineData(1.5 * Math.PI, -0.5 * Math.PI)]
    public void WrapToPiFoldsIntoTheHalfOpenInterval(double input, double expected)
    {
        TestAssertions.Close(expected, PhaseMath.WrapToPi(input), 1e-12, $"WrapToPi({input})");
    }

    [Fact]
    public void WrapToPiNeverLeavesTheInterval()
    {
        var rng = new Random(9);
        for (var i = 0; i < 10_000; i++)
        {
            var angle = (rng.NextDouble() - 0.5) * 1000.0;
            var wrapped = PhaseMath.WrapToPi(angle);
            Assert.InRange(wrapped, -Math.PI, Math.PI);
        }
    }

    [Fact]
    public void UnwrapReconstructsAContinuousRamp()
    {
        const int n = 5000;
        var truth = new double[n];
        var wrapped = new double[n];

        for (var i = 0; i < n; i++)
        {
            // 0.37 rad per step: never a multiple of π, so the wrap points land awkwardly.
            truth[i] = 1.1 + 0.37 * i;
            wrapped[i] = PhaseMath.WrapToPi(truth[i]);
        }

        var unwrapped = PhaseMath.Unwrap(wrapped);

        // Unwrapping fixes the shape, not the absolute branch; compare after removing the offset.
        var offset = unwrapped[0] - truth[0];
        for (var i = 0; i < n; i++)
        {
            TestAssertions.Close(truth[i] + offset, unwrapped[i], Tolerances.Phase, $"unwrapped sample {i}");
        }
    }

    [Fact]
    public void UnwrapHandlesADescendingRamp()
    {
        const int n = 1000;
        var wrapped = new double[n];
        for (var i = 0; i < n; i++)
        {
            wrapped[i] = PhaseMath.WrapToPi(-0.9 * i);
        }

        var unwrapped = PhaseMath.Unwrap(wrapped);

        for (var i = 1; i < n; i++)
        {
            TestAssertions.Close(-0.9, unwrapped[i] - unwrapped[i - 1], Tolerances.Phase, $"step {i}");
        }
    }

    [Fact]
    public void UnwrapStepAgreesWithTheBatchVersion()
    {
        var rng = new Random(5);
        var wrapped = new double[500];
        for (var i = 0; i < wrapped.Length; i++)
        {
            wrapped[i] = PhaseMath.WrapToPi(rng.NextDouble() * 0.4 * i);
        }

        var batch = PhaseMath.Unwrap(wrapped);

        var streamed = wrapped[0];
        for (var i = 1; i < wrapped.Length; i++)
        {
            streamed = PhaseMath.UnwrapStep(streamed, wrapped[i]);
            TestAssertions.Close(batch[i], streamed, Tolerances.Phase, $"streaming step {i}");
        }
    }

    [Fact]
    public void CircularMeanWorksAcrossTheWrapBoundary()
    {
        // Angles straddling ±π. A naive arithmetic mean would give roughly 0 — the opposite side.
        double[] angles = { 3.10, 3.13, -3.13, -3.10, Math.PI };

        var (mean, resultant) = PhaseMath.CircularMean(angles);

        TestAssertions.CloseModulo(Math.PI, mean, PhaseMath.TwoPi, 0.02, "circular mean near ±π");
        Assert.True(resultant > 0.99, $"resultant should be near 1 for tightly clustered angles, was {resultant}");
    }

    [Fact]
    public void CircularMeanOfOpposedAnglesHasNoDirection()
    {
        double[] angles = { 0.0, Math.PI, 0.5 * Math.PI, -0.5 * Math.PI };

        var (_, resultant) = PhaseMath.CircularMean(angles);

        TestAssertions.Close(0.0, resultant, 1e-12, "resultant of four opposed angles");
    }

    [Fact]
    public void CircularMeanRespectsWeights()
    {
        double[] angles = { 0.0, Math.PI / 2.0 };
        double[] weights = { 3.0, 1.0 };

        var (mean, _) = PhaseMath.CircularMean(angles, weights);

        // atan2(1, 3): the weighted resultant, not the weighted arithmetic mean of the angles.
        TestAssertions.Close(Math.Atan2(1.0, 3.0), mean, Tolerances.CircularMean, "weighted circular mean");
    }

    [Fact]
    public void CircularMeanOfPhasorsUsesTheirMagnitudes()
    {
        Complex[] phasors =
        {
            Complex.FromPolarCoordinates(10.0, 0.2),
            Complex.FromPolarCoordinates(0.1, -2.9),
        };

        var (mean, _) = PhaseMath.CircularMean(phasors);

        TestAssertions.Close(0.2, mean, 0.05, "phasor mean dominated by the strong component");
    }

    [Fact]
    public void CircularMeanOverPeriodFoldsSemitonesCorrectly()
    {
        // Cents that straddle the ±50 fold: −49 and +49 are two cents apart, not 98.
        double[] cents = { -49.0, 49.0, 50.0, -50.0 };

        var (mean, resultant) = PhaseMath.CircularMeanOverPeriod(cents, 100.0);

        TestAssertions.CloseModulo(50.0, mean, 100.0, 1e-9, "semitone-folded mean");
        Assert.True(resultant > 0.99, $"resultant was {resultant}");
    }

    [Fact]
    public void CircularMeanOverPeriodRecoversAKnownOffset()
    {
        var rng = new Random(17);
        var cents = new double[500];
        for (var i = 0; i < cents.Length; i++)
        {
            // A true offset of +30 cents, scattered by ±5, folded into ±50.
            var value = 30.0 + (rng.NextDouble() * 2.0 - 1.0) * 5.0;
            cents[i] = value - 100.0 * Math.Round(value / 100.0);
        }

        var (mean, _) = PhaseMath.CircularMeanOverPeriod(cents, 100.0);

        TestAssertions.CloseModulo(30.0, mean, 100.0, 0.5, "recovered semitone offset");
    }

    [Theory]
    [InlineData(7.0, 3.0, 1.0)]
    [InlineData(-1.0, 3.0, 2.0)]
    [InlineData(-0.25, 1.0, 0.75)]
    public void ModIsAlwaysNonNegative(double value, double period, double expected)
    {
        TestAssertions.Close(expected, PhaseMath.Mod(value, period), 1e-12, $"Mod({value}, {period})");
    }

    [Theory]
    [InlineData(3.490658503988659)]
    [InlineData(-4.71238898038469)]
    [InlineData(8.168140899333463)]
    public void MedianRateRecoversTheRotationRate(double rate)
    {
        const double dt = 0.02;
        var times = new double[3000];
        var wrapped = new double[3000];
        for (var i = 0; i < times.Length; i++)
        {
            times[i] = i * dt;
            wrapped[i] = PhaseMath.WrapToPi(0.4 + rate * times[i]);
        }

        TestAssertions.Close(rate, PhaseMath.MedianRate(times, wrapped), 1e-9, "median phase rate");
    }

    [Fact]
    public void MedianRateIgnoresScatteredBadSamples()
    {
        const double rate = 3.4906585;
        const double dt = 0.02;
        var rng = new Random(61);
        var times = new double[3000];
        var wrapped = new double[3000];

        for (var i = 0; i < times.Length; i++)
        {
            times[i] = i * dt;
            wrapped[i] = PhaseMath.WrapToPi(rate * times[i]);
            if (rng.NextDouble() < 0.05)
            {
                wrapped[i] = PhaseMath.WrapToPi(rng.NextDouble() * PhaseMath.TwoPi);
            }
        }

        TestAssertions.WithinPercent(rate, PhaseMath.MedianRate(times, wrapped), 0.5,
            "median phase rate with 5% of samples destroyed");
    }

    [Fact]
    public void UnwrapSteadySurvivesOutliersThatDefeatChainedUnwrapping()
    {
        // This is the failure mode that cost the magnetometer estimator 4% of its reading: an
        // outlier further than π away makes the chained unwrap take the short way round and
        // silently lose a whole turn, shifting every later sample with it.
        const double rate = 3.4906585;
        const double dt = 0.02;
        const int n = 3000;

        var rng = new Random(67);
        var times = new double[n];
        var wrapped = new double[n];
        var truth = new double[n];

        for (var i = 0; i < n; i++)
        {
            times[i] = i * dt;
            truth[i] = rate * times[i];
            wrapped[i] = PhaseMath.WrapToPi(truth[i]);

            if (rng.NextDouble() < 0.02)
            {
                wrapped[i] = PhaseMath.WrapToPi(rng.NextDouble() * PhaseMath.TwoPi);
            }
        }

        var chained = PhaseMath.Unwrap(wrapped);
        var steady = PhaseMath.UnwrapSteady(times, wrapped);

        var chainedTotal = chained[^1] - chained[0];
        var steadyTotal = steady[^1] - steady[0];
        var expectedTotal = truth[^1] - truth[0];

        TestAssertions.WithinPercent(expectedTotal, steadyTotal, 0.5, "total phase from branch snapping");

        Assert.True(Math.Abs(chainedTotal - expectedTotal) > Math.Abs(steadyTotal - expectedTotal),
            $"branch snapping should beat chaining: chained {chainedTotal:F2}, snapped {steadyTotal:F2}, " +
            $"true {expectedTotal:F2}");
    }

    [Fact]
    public void UnwrapSteadyAgreesWithChainedUnwrappingOnCleanData()
    {
        const double rate = 4.712389;
        var times = new double[1000];
        var wrapped = new double[1000];
        for (var i = 0; i < times.Length; i++)
        {
            times[i] = i * 0.01;
            wrapped[i] = PhaseMath.WrapToPi(1.2 + rate * times[i]);
        }

        var chained = PhaseMath.Unwrap(wrapped);
        var steady = PhaseMath.UnwrapSteady(times, wrapped);

        // The two may sit on different branches, but their shape must be identical.
        var offset = steady[0] - chained[0];
        for (var i = 0; i < times.Length; i++)
        {
            TestAssertions.Close(chained[i] + offset, steady[i], 1e-9, $"clean-data agreement at {i}");
        }
    }

    [Fact]
    public void UnwrapAroundSnapsToTheNearestBranch()
    {
        double[] wrapped = { 0.1, -3.0, 3.0 };
        double[] predicted = { 0.0, 3.3, -3.3 };

        var result = PhaseMath.UnwrapAround(wrapped, predicted);

        TestAssertions.Close(0.1, result[0], 1e-12, "no shift needed");
        TestAssertions.Close(-3.0 + PhaseMath.TwoPi, result[1], 1e-12, "shifted up one branch");
        TestAssertions.Close(3.0 - PhaseMath.TwoPi, result[2], 1e-12, "shifted down one branch");
    }
}
