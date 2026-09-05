using TurntableSpeed.Core.Dsp;

namespace TurntableSpeed.Core.Tests.Dsp;

public class ResamplingTests
{
    [Fact]
    public void MedianIntervalIsRobustToDroppedSamples()
    {
        var t = new double[100];
        for (var i = 0; i < t.Length; i++)
        {
            t[i] = i * 0.02;
        }

        // Three long gaps, as an Android sensor callback produces under load.
        t[50] += 0.5;
        for (var i = 51; i < t.Length; i++)
        {
            t[i] += 0.5;
        }

        TestAssertions.Close(0.02, UniformResampler.MedianInterval(t), 1e-12, "median interval");
    }

    [Fact]
    public void ResamplingALinearFunctionIsExact()
    {
        var rng = new Random(41);
        const int n = 500;
        var t = new double[n];
        var y = new double[n];

        var time = 0.0;
        for (var i = 0; i < n; i++)
        {
            // Jittered arrival times, as the real sensors deliver.
            time += 0.02 * (0.5 + rng.NextDouble());
            t[i] = time;
            y[i] = 3.0 + 7.5 * time;
        }

        var series = UniformResampler.Resample(t, y, 0.02);

        Assert.True(series.Count > 100);
        for (var i = 0; i < series.Count; i++)
        {
            var expected = 3.0 + 7.5 * series.TimeAt(i);
            TestAssertions.Close(expected, series.Values[i], Tolerances.Resampling, $"resampled value {i}");
        }
    }

    [Fact]
    public void ResampledGridStartsAtTheFirstTimestamp()
    {
        double[] t = { 10.0, 10.1, 10.2, 10.3 };
        double[] y = { 1.0, 2.0, 3.0, 4.0 };

        var series = UniformResampler.Resample(t, y, 0.05);

        TestAssertions.Close(10.0, series.StartTime, 1e-12, "grid start");
        TestAssertions.Close(0.05, series.SampleInterval, 1e-12, "grid interval");
        TestAssertions.Close(20.0, series.SampleRate, 1e-12, "grid rate");
    }

    [Fact]
    public void DegenerateInputsReturnAnEmptySeries()
    {
        Assert.Equal(0, UniformResampler.Resample(new[] { 1.0 }, new[] { 1.0 }).Count);
        Assert.Equal(0, UniformResampler.Resample(new[] { 1.0, 1.0 }, new[] { 1.0, 2.0 }).Count);
    }

    [Theory]
    [InlineData(1.01)]
    [InlineData(0.99)]
    [InlineData(1.35)]
    [InlineData(0.7407407407407407)]  // 33⅓ played at 45
    public void ChangingSpeedScalesTheFrequency(double speedFactor)
    {
        const int sampleRate = 48000;
        const double frequency = 1000.0;
        const int length = sampleRate * 2;

        var input = new float[length];
        for (var i = 0; i < length; i++)
        {
            input[i] = (float)Math.Sin(2.0 * Math.PI * frequency * i / sampleRate);
        }

        var output = Resampler.ChangeSpeed(input, speedFactor);

        // Duration shrinks by the speed factor.
        TestAssertions.WithinPercent(length / speedFactor, output.Length, 0.01, "resampled length");

        var measured = DominantFrequency(output, sampleRate);
        TestAssertions.WithinPercent(frequency * speedFactor, measured,
            Tolerances.ResamplingFrequency * 100.0, $"frequency after ×{speedFactor}");
    }

    [Fact]
    public void ChangingSpeedByOneIsCloseToIdentity()
    {
        var rng = new Random(43);
        var input = new float[10_000];
        for (var i = 0; i < input.Length; i++)
        {
            input[i] = (float)Math.Sin(2.0 * Math.PI * 440.0 * i / 48000.0);
        }

        _ = rng;
        var output = Resampler.ChangeSpeed(input, 1.0);

        Assert.Equal(input.Length, output.Length);
        for (var i = 100; i < input.Length - 100; i++)
        {
            TestAssertions.Close(input[i], output[i], 1e-4, $"identity resample at {i}");
        }
    }

    [Fact]
    public void NonPositiveSpeedFactorIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Resampler.ChangeSpeed(new float[10], 0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Resampler.ChangeSpeed(new float[10], -1.0));
    }

    private static double DominantFrequency(float[] signal, int sampleRate)
    {
        var length = Fft.NextPowerOfTwo(signal.Length / 2);
        var data = new double[length];
        var offset = signal.Length / 4;
        for (var i = 0; i < length && offset + i < signal.Length; i++)
        {
            data[i] = signal[offset + i];
        }

        var window = WindowFunctions.Create(WindowFunctions.Kind.Hann, length);
        WindowFunctions.ApplyInPlace(data, window);

        var spectrum = Fft.MagnitudeSpectrum(data);
        var (index, _) = PeakInterpolation.RefineMaximum(spectrum, 1, spectrum.Length - 2);
        return index * sampleRate / (double)length;
    }
}
