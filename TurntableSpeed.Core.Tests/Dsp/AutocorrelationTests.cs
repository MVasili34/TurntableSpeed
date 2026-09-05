using TurntableSpeed.Core.Dsp;

namespace TurntableSpeed.Core.Tests.Dsp;

/// <summary>
/// The FFT-based autocorrelation must be <em>linear</em>, not circular: a circular one would
/// wrap the end of the recording onto the beginning and invent a periodicity that is not there.
/// </summary>
public class AutocorrelationTests
{
    /// <summary>Textbook O(n²) autocorrelation, used as the reference.</summary>
    private static double[] Naive(double[] x, int maxLag, bool removeMean, bool unbiased, bool normalize)
    {
        var data = (double[])x.Clone();
        if (removeMean)
        {
            var mean = SeriesMath.Mean(data);
            for (var i = 0; i < data.Length; i++)
            {
                data[i] -= mean;
            }
        }

        var result = new double[maxLag + 1];
        for (var lag = 0; lag <= maxLag; lag++)
        {
            var sum = 0.0;
            for (var i = 0; i + lag < data.Length; i++)
            {
                sum += data[i] * data[i + lag];
            }

            result[lag] = unbiased ? sum / (data.Length - lag) : sum / data.Length;
        }

        if (normalize && result[0] > 0.0)
        {
            var zero = result[0];
            for (var lag = 0; lag <= maxLag; lag++)
            {
                result[lag] /= zero;
            }
        }

        return result;
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void MatchesTheNaiveImplementation(bool unbiased, bool removeMean)
    {
        var rng = new Random(19);
        var x = new double[997];
        for (var i = 0; i < x.Length; i++)
        {
            x[i] = rng.NextDouble() * 4.0 - 1.0;
        }

        const int maxLag = 200;

        var actual = Autocorrelation.Compute(x, maxLag, removeMean, unbiased);
        var expected = Naive(x, maxLag, removeMean, unbiased, normalize: true);

        for (var lag = 0; lag <= maxLag; lag++)
        {
            TestAssertions.Close(expected[lag], actual[lag], 1e-9, $"autocorrelation at lag {lag}");
        }
    }

    [Fact]
    public void LagZeroIsOneWhenNormalized()
    {
        var rng = new Random(2);
        var x = new double[512];
        for (var i = 0; i < x.Length; i++)
        {
            x[i] = rng.NextDouble();
        }

        var acf = Autocorrelation.Compute(x, 100);

        TestAssertions.Close(1.0, acf[0], 1e-12, "normalised lag 0");
    }

    [Fact]
    public void PeriodicSignalPeaksAtItsPeriod()
    {
        const int period = 137;
        var x = new double[4000];
        for (var i = 0; i < x.Length; i++)
        {
            x[i] = Math.Sin(2.0 * Math.PI * i / period);
        }

        var acf = Autocorrelation.Compute(x, 400);
        var (index, _) = PeakInterpolation.RefineMaximum(acf, period / 2, 400);

        TestAssertions.Close(period, index, 0.05, "autocorrelation peak of a sine");
    }

    [Fact]
    public void ImpulseTrainPeaksAtEveryMultipleOfItsPeriod()
    {
        const int period = 90;
        var x = new double[3000];
        for (var i = 0; i < x.Length; i += period)
        {
            x[i] = 1.0;
        }

        var acf = Autocorrelation.Compute(x, 300);

        foreach (var multiple in new[] { 1, 2, 3 })
        {
            var lag = period * multiple;
            Assert.True(acf[lag] > 0.5 * acf[period],
                $"expected a strong peak at lag {lag}, found {acf[lag]} against {acf[period]} at the fundamental");
        }

        // And nothing in between.
        Assert.True(acf[period / 2] < 0.2 * acf[period],
            $"half the period should be empty, found {acf[period / 2]}");
    }

    [Fact]
    public void WhiteNoiseHasNoStructureAwayFromZero()
    {
        var rng = new Random(23);
        var x = new double[50_000];
        for (var i = 0; i < x.Length; i++)
        {
            x[i] = Synth.GyroSignalGenerator.Gaussian(rng, 1.0);
        }

        var acf = Autocorrelation.Compute(x, 500);

        for (var lag = 1; lag <= 500; lag++)
        {
            Assert.True(Math.Abs(acf[lag]) < 0.05,
                $"white noise should be uncorrelated, but lag {lag} gave {acf[lag]}");
        }
    }

    [Fact]
    public void ResultIsLinearNotCircular()
    {
        // A single impulse at the very start. A circular autocorrelation would show a peak at
        // the wrap-around lag; a linear one must not.
        var x = new double[1000];
        x[0] = 1.0;
        x[1] = 1.0;

        var acf = Autocorrelation.Compute(x, 500, removeMean: false);

        for (var lag = 2; lag <= 500; lag++)
        {
            TestAssertions.Close(0.0, acf[lag], 1e-12, $"circular leakage at lag {lag}");
        }
    }

    [Fact]
    public void MaxLagIsClampedToTheAvailableData()
    {
        var x = new double[50];
        Array.Fill(x, 1.0);

        var acf = Autocorrelation.Compute(x, 500);

        Assert.Equal(50, acf.Length);
    }

    [Fact]
    public void NoiseFloorIgnoresTheProtectedRegionAroundThePeak()
    {
        var acf = new double[500];
        var rng = new Random(31);
        for (var i = 0; i < acf.Length; i++)
        {
            acf[i] = Synth.GyroSignalGenerator.Gaussian(rng, 0.01);
        }

        for (var i = 195; i <= 205; i++)
        {
            acf[i] = 1.0;
        }

        var floor = Autocorrelation.NoiseFloor(acf, 50, 450, peakIndex: 200, exclusionRadius: 10);

        TestAssertions.Close(0.01, floor, 0.002, "noise floor with the peak excluded");
    }

    [Fact]
    public void RobustFloorMeasuresTheBackgroundRatherThanTheCombItSitsIn()
    {
        // A record with several clicks per revolution puts real teeth at multiples of the click
        // spacing. Measuring the background with a standard deviation counts those teeth as
        // noise, so the better the evidence the weaker the detection looks — which is how a
        // legitimate 45 rpm record with two clicks per revolution came to score 11 against the
        // 1589 of the same record with one.
        var acf = new double[500];
        var rng = new Random(17);
        for (var i = 0; i < acf.Length; i++)
        {
            acf[i] = Synth.GyroSignalGenerator.Gaussian(rng, 0.01);
        }

        acf[100] = 1.0;
        acf[200] = 0.8;
        acf[300] = 0.6;
        acf[400] = 0.5;

        var standard = Autocorrelation.NoiseFloor(acf, 50, 450, peakIndex: 100, exclusionRadius: 5);
        var robust = Autocorrelation.RobustNoiseFloor(acf, 50, 450, peakIndex: 100, exclusionRadius: 5);

        TestAssertions.Close(0.01, robust, 0.003, "robust background of a comb");
        Assert.True(standard > robust * 5.0,
            $"the comb's own teeth must be what separates the two measures: " +
            $"standard {standard:F4}, robust {robust:F4}");
    }

    [Fact]
    public void RobustFloorAgreesWithTheStandardDeviationOnPlainNoise()
    {
        // Without structure to reject there is nothing to disagree about, so the threshold this
        // feeds keeps meaning the same thing in the ordinary case.
        var acf = new double[500];
        var rng = new Random(53);
        for (var i = 0; i < acf.Length; i++)
        {
            acf[i] = Synth.GyroSignalGenerator.Gaussian(rng, 0.02);
        }

        var standard = Autocorrelation.NoiseFloor(acf, 50, 450, peakIndex: 200, exclusionRadius: 10);
        var robust = Autocorrelation.RobustNoiseFloor(acf, 50, 450, peakIndex: 200, exclusionRadius: 10);

        TestAssertions.WithinPercent(standard, robust, 15.0, "robust against standard on plain noise");
    }
}
