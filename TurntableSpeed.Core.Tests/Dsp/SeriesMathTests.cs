using TurntableSpeed.Core.Dsp;

namespace TurntableSpeed.Core.Tests.Dsp;

/// <summary>Statistics helpers, checked against naive reference implementations.</summary>
public class SeriesMathTests
{
    private static double[] RandomSeries(int count, int seed = 1)
    {
        var rng = new Random(seed);
        var x = new double[count];
        for (var i = 0; i < count; i++)
        {
            x[i] = rng.NextDouble() * 20.0 - 10.0;
        }

        return x;
    }

    [Fact]
    public void MeanAndVarianceMatchTheDefinitions()
    {
        double[] x = { 2.0, 4.0, 4.0, 4.0, 5.0, 5.0, 7.0, 9.0 };

        TestAssertions.Close(5.0, SeriesMath.Mean(x), Tolerances.SeriesMath, "mean");

        // Sample variance divides by n−1: 32/7.
        TestAssertions.Close(32.0 / 7.0, SeriesMath.Variance(x), Tolerances.SeriesMath, "sample variance");
        TestAssertions.Close(Math.Sqrt(32.0 / 7.0), SeriesMath.StandardDeviation(x), Tolerances.SeriesMath, "σ");
    }

    [Fact]
    public void RootMeanSquareOfASineIsAmplitudeOverRootTwo()
    {
        const int n = 10_000;
        var x = new double[n];
        for (var i = 0; i < n; i++)
        {
            x[i] = 2.0 * Math.Sin(2.0 * Math.PI * 50 * i / n);
        }

        TestAssertions.Close(2.0 / Math.Sqrt(2.0), SeriesMath.RootMeanSquare(x), 1e-9, "RMS of a sine");
    }

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(0.5, 3.0)]
    [InlineData(1.0, 5.0)]
    [InlineData(0.25, 2.0)]
    public void PercentileInterpolatesLinearly(double p, double expected)
    {
        double[] sorted = { 1.0, 2.0, 3.0, 4.0, 5.0 };

        TestAssertions.Close(expected, SeriesMath.Percentile(sorted, p), Tolerances.SeriesMath, $"percentile {p}");
    }

    [Fact]
    public void MedianAbsoluteDeviationIsRobustToOutliers()
    {
        var clean = RandomSeries(1001, 7);
        var contaminated = (double[])clean.Clone();
        for (var i = 0; i < 50; i++)
        {
            contaminated[i * 20] = 10_000.0;
        }

        var cleanMad = SeriesMath.MedianAbsoluteDeviation(clean);
        var contaminatedMad = SeriesMath.MedianAbsoluteDeviation(contaminated);

        TestAssertions.WithinPercent(cleanMad, contaminatedMad, 15.0, "MAD under 5% contamination");

        // The standard deviation, by contrast, is destroyed.
        Assert.True(SeriesMath.StandardDeviation(contaminated) > 10.0 * SeriesMath.StandardDeviation(clean));
    }

    [Fact]
    public void MedianAbsoluteDeviationEstimatesSigmaForNormalData()
    {
        var rng = new System.Random(3);
        var x = new double[20_000];
        for (var i = 0; i < x.Length; i++)
        {
            x[i] = Synth.GyroSignalGenerator.Gaussian(rng, 2.5);
        }

        TestAssertions.WithinPercent(2.5, SeriesMath.MedianAbsoluteDeviation(x), 3.0, "MAD as a σ estimate");
    }

    [Fact]
    public void DetrendRemovesAKnownLineExactly()
    {
        const int n = 500;
        var y = new double[n];
        for (var i = 0; i < n; i++)
        {
            y[i] = 3.5 + 0.25 * i;
        }

        SeriesMath.DetrendLinearInPlace(y);

        foreach (var value in y)
        {
            TestAssertions.Close(0.0, value, 1e-9, "residual after detrending an exact line");
        }
    }

    [Fact]
    public void DetrendPreservesTheOscillationUnderneathTheTrend()
    {
        const int n = 1024;
        var y = new double[n];
        for (var i = 0; i < n; i++)
        {
            y[i] = 10.0 + 0.05 * i + 2.0 * Math.Sin(2.0 * Math.PI * 8 * i / n);
        }

        var detrended = SeriesMath.DetrendLinear(y);

        TestAssertions.Close(0.0, SeriesMath.Mean(detrended), 1e-9, "mean after detrending");
        TestAssertions.Close(2.0 / Math.Sqrt(2.0), SeriesMath.RootMeanSquare(detrended), 0.02, "surviving oscillation");
    }

    [Fact]
    public void RunningMedianMatchesANaiveImplementation()
    {
        var x = RandomSeries(500, 13);
        const int window = 11;

        var actual = SeriesMath.RunningMedian(x, window);

        var half = window / 2;
        for (var i = 0; i < x.Length; i++)
        {
            var from = Math.Max(0, i - half);
            var to = Math.Min(x.Length - 1, i + half);
            var slice = x[from..(to + 1)];
            Array.Sort(slice);
            var expected = slice.Length % 2 == 1
                ? slice[slice.Length / 2]
                : 0.5 * (slice[slice.Length / 2 - 1] + slice[slice.Length / 2]);

            TestAssertions.Close(expected, actual[i], Tolerances.SeriesMath, $"running median at {i}");
        }
    }

    [Fact]
    public void SubtractRunningMedianKeepsTransientsAndRemovesTheFloor()
    {
        var x = new double[1000];
        Array.Fill(x, 1.0);
        x[500] = 5.0;

        var residual = SeriesMath.SubtractRunningMedian(x, 21);

        TestAssertions.Close(4.0, residual[500], 1e-9, "the transient survives");
        TestAssertions.Close(0.0, residual[100], 1e-9, "the stationary floor is removed");
    }

    [Fact]
    public void MovingAverageSmearsAnImpulseSymmetricallyAndPreservesItsArea()
    {
        var x = new double[201];
        x[100] = 1.0;

        var smoothed = SeriesMath.MovingAverage(x, 9);

        // A box filter spreads the impulse over exactly the window, centred, with no shift.
        for (var i = 96; i <= 104; i++)
        {
            TestAssertions.Close(1.0 / 9.0, smoothed[i], 1e-12, $"smeared impulse at {i}");
        }

        TestAssertions.Close(0.0, smoothed[95], 1e-12, "just outside the window");
        TestAssertions.Close(0.0, smoothed[105], 1e-12, "just outside the window");

        // Area is conserved, so the smoothing introduces no gain.
        var area = 0.0;
        foreach (var value in smoothed)
        {
            area += value;
        }

        TestAssertions.Close(1.0, area, 1e-12, "area after smoothing");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(0.9)]
    [InlineData(-0.6)]
    public void Lag1CorrelationRecoversAnAutoregressiveCoefficient(double rho)
    {
        var rng = new System.Random(29);
        var x = new double[200_000];
        for (var i = 1; i < x.Length; i++)
        {
            x[i] = rho * x[i - 1] + Synth.GyroSignalGenerator.Gaussian(rng, 1.0);
        }

        TestAssertions.Close(rho, SeriesMath.Lag1Correlation(x), 0.01, $"lag-1 correlation of AR(1) with ρ={rho}");
    }

    [Fact]
    public void EmptyInputsReturnNaNRatherThanThrowing()
    {
        Assert.True(double.IsNaN(SeriesMath.Mean([])));
        Assert.True(double.IsNaN(SeriesMath.Variance([1.0])));
        Assert.True(double.IsNaN(SeriesMath.Median([])));
    }
}
