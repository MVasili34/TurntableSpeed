using System.Numerics;
using TurntableSpeed.Core.Dsp;

namespace TurntableSpeed.Core.Tests.Dsp;

/// <summary>Spec §7.3: the FFT is checked against analytically known spectra.</summary>
public class FftTests
{
    [Theory]
    [InlineData(1, 1.0)]
    [InlineData(5, 0.25)]
    [InlineData(37, 2.5)]
    [InlineData(255, 0.001)]
    public void BinCentredCosineReadsItsOwnAmplitude(int bin, double amplitude)
    {
        const int n = 1024;
        var x = new double[n];
        for (var i = 0; i < n; i++)
        {
            x[i] = amplitude * Math.Cos(2.0 * Math.PI * bin * i / n);
        }

        var spectrum = Fft.MagnitudeSpectrum(x);

        TestAssertions.Close(amplitude, spectrum[bin], Tolerances.Fft, $"amplitude at bin {bin}");

        for (var k = 0; k < spectrum.Length; k++)
        {
            if (k != bin)
            {
                TestAssertions.Close(0.0, spectrum[k], Tolerances.Fft, $"leakage into bin {k}");
            }
        }
    }

    [Fact]
    public void DcComponentReadsTheConstant()
    {
        var x = new double[512];
        Array.Fill(x, 3.25);

        var spectrum = Fft.MagnitudeSpectrum(x);

        TestAssertions.Close(3.25, spectrum[0], Tolerances.Fft, "DC bin");
    }

    [Fact]
    public void NyquistBinIsNotDoubleCounted()
    {
        const int n = 256;
        var x = new double[n];
        for (var i = 0; i < n; i++)
        {
            // Alternating ±A is exactly the Nyquist frequency.
            x[i] = i % 2 == 0 ? 1.5 : -1.5;
        }

        var spectrum = Fft.MagnitudeSpectrum(x);

        TestAssertions.Close(1.5, spectrum[n / 2], Tolerances.Fft, "Nyquist bin");
    }

    [Fact]
    public void SumOfTwoToneAmplitudesIsRecoveredIndependently()
    {
        const int n = 2048;
        var x = new double[n];
        for (var i = 0; i < n; i++)
        {
            x[i] = 0.7 * Math.Cos(2.0 * Math.PI * 10 * i / n)
                   + 0.2 * Math.Sin(2.0 * Math.PI * 200 * i / n);
        }

        var spectrum = Fft.MagnitudeSpectrum(x);

        TestAssertions.Close(0.7, spectrum[10], Tolerances.Fft, "first tone");
        TestAssertions.Close(0.2, spectrum[200], Tolerances.Fft, "second tone");
    }

    [Fact]
    public void ParsevalHolds()
    {
        var rng = new Random(4);
        const int n = 1024;
        var x = new double[n];
        for (var i = 0; i < n; i++)
        {
            x[i] = rng.NextDouble() * 2.0 - 1.0;
        }

        var timeEnergy = 0.0;
        foreach (var v in x)
        {
            timeEnergy += v * v;
        }

        var spectrum = Fft.Forward(x);
        var frequencyEnergy = 0.0;
        foreach (var c in spectrum)
        {
            frequencyEnergy += c.Real * c.Real + c.Imaginary * c.Imaginary;
        }

        TestAssertions.WithinPercent(timeEnergy, frequencyEnergy / n, 1e-9, "Parseval");
    }

    [Fact]
    public void InverseUndoesForward()
    {
        var rng = new Random(11);
        const int n = 512;
        var original = new Complex[n];
        for (var i = 0; i < n; i++)
        {
            original[i] = new Complex(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5);
        }

        var buffer = (Complex[])original.Clone();
        Fft.ForwardInPlace(buffer);
        Fft.InverseInPlace(buffer);

        for (var i = 0; i < n; i++)
        {
            TestAssertions.Close(original[i].Real, buffer[i].Real, Tolerances.Fft, $"real part at {i}");
            TestAssertions.Close(original[i].Imaginary, buffer[i].Imaginary, Tolerances.Fft, $"imaginary part at {i}");
        }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(1024, 1024)]
    [InlineData(1025, 2048)]
    public void NextPowerOfTwoIsTheSmallestOne(int input, int expected)
    {
        Assert.Equal(expected, Fft.NextPowerOfTwo(input));
        Assert.True(Fft.IsPowerOfTwo(Fft.NextPowerOfTwo(input)));
    }

    [Fact]
    public void HannCoherentGainIsAHalf()
    {
        var window = WindowFunctions.Create(WindowFunctions.Kind.Hann, 4096);

        // The symmetric Hann window's mean is 0.5·N/(N−1); at 4096 points that is 0.50006.
        TestAssertions.Close(0.5, WindowFunctions.CoherentGain(window), 1e-3, "Hann coherent gain");
    }

    [Fact]
    public void ZeroPadPreservesTheSignalAndPadsTheRest()
    {
        double[] source = { 1.0, 2.0, 3.0 };

        var padded = Fft.ZeroPad(source, 8);

        Assert.Equal(8, padded.Length);
        Assert.Equal(new[] { 1.0, 2.0, 3.0, 0.0, 0.0, 0.0, 0.0, 0.0 }, padded);
    }

    [Fact]
    public void BinFrequencyMatchesTheSampleRate()
    {
        TestAssertions.Close(0.0, Fft.BinFrequency(0, 1024, 48000), Tolerances.Fft, "bin 0");
        TestAssertions.Close(46.875, Fft.BinFrequency(1, 1024, 48000), Tolerances.Fft, "bin 1");
        TestAssertions.Close(24000.0, Fft.BinFrequency(512, 1024, 48000), Tolerances.Fft, "Nyquist bin");
        TestAssertions.Close(46.875, Fft.BinWidth(1024, 48000), Tolerances.Fft, "bin width");
    }
}
