using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace TurntableSpeed.Core.Dsp;

/// <summary>
/// Thin, allocation-explicit wrapper over MathNet's FFT with a fixed normalisation
/// convention: <see cref="MagnitudeSpectrum"/> of a pure sinusoid of amplitude A whose
/// frequency falls on a bin returns A in that bin.
/// </summary>
public static class Fft
{
    public static int NextPowerOfTwo(int n)
    {
        if (n <= 1)
        {
            return 1;
        }

        var p = 1;
        while (p < n)
        {
            p <<= 1;
        }

        return p;
    }

    public static bool IsPowerOfTwo(int n) => n > 0 && (n & (n - 1)) == 0;

    /// <summary>Forward transform of a real signal. Returns the full length-N spectrum, unscaled.</summary>
    public static Complex[] Forward(ReadOnlySpan<double> real)
    {
        var buffer = new Complex[real.Length];
        for (var i = 0; i < real.Length; i++)
        {
            buffer[i] = new Complex(real[i], 0.0);
        }

        Fourier.Forward(buffer, FourierOptions.NoScaling);
        return buffer;
    }

    /// <summary>In-place forward transform, unscaled.</summary>
    public static void ForwardInPlace(Complex[] buffer) =>
        Fourier.Forward(buffer, FourierOptions.NoScaling);

    /// <summary>In-place inverse transform, scaled by 1/N so that inverse(forward(x)) == x.</summary>
    public static void InverseInPlace(Complex[] buffer)
    {
        Fourier.Inverse(buffer, FourierOptions.NoScaling);
        var scale = 1.0 / buffer.Length;
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] *= scale;
        }
    }

    /// <summary>
    /// One-sided amplitude spectrum, length N/2+1. A sinusoid of amplitude A sitting on
    /// bin k reads A at index k; DC and Nyquist read their true (unfolded) amplitude.
    /// </summary>
    public static double[] MagnitudeSpectrum(ReadOnlySpan<double> real)
    {
        var n = real.Length;
        if (n == 0)
        {
            return [];
        }

        var spectrum = Forward(real);
        var half = n / 2 + 1;
        var result = new double[half];
        for (var k = 0; k < half; k++)
        {
            var scale = (k == 0 || (n % 2 == 0 && k == n / 2)) ? 1.0 / n : 2.0 / n;
            result[k] = spectrum[k].Magnitude * scale;
        }

        return result;
    }

    /// <summary>One-sided power spectrum (magnitude squared) using the same normalisation.</summary>
    public static double[] PowerSpectrum(ReadOnlySpan<double> real)
    {
        var magnitude = MagnitudeSpectrum(real);
        for (var i = 0; i < magnitude.Length; i++)
        {
            magnitude[i] *= magnitude[i];
        }

        return magnitude;
    }

    /// <summary>Centre frequency of bin <paramref name="bin"/> for an N-point transform.</summary>
    public static double BinFrequency(int bin, int length, double sampleRate) =>
        length > 0 ? bin * sampleRate / length : double.NaN;

    /// <summary>Spacing between adjacent bins, i.e. the frequency resolution.</summary>
    public static double BinWidth(int length, double sampleRate) =>
        length > 0 ? sampleRate / length : double.NaN;

    /// <summary>Copy <paramref name="source"/> into a zero-padded buffer of the given length.</summary>
    public static double[] ZeroPad(ReadOnlySpan<double> source, int length)
    {
        var padded = new double[length];
        source.Slice(0, Math.Min(source.Length, length)).CopyTo(padded);
        return padded;
    }
}

/// <summary>Analysis windows plus the gain corrections needed to keep amplitudes honest.</summary>
public static class WindowFunctions
{
    public enum Kind
    {
        Rectangular,
        Hann,
        Hamming,
        Blackman,
    }

    public static double[] Create(Kind kind, int length)
    {
        var w = new double[length];
        if (length == 1)
        {
            w[0] = 1.0;
            return w;
        }

        for (var i = 0; i < length; i++)
        {
            var x = 2.0 * Math.PI * i / (length - 1);
            w[i] = kind switch
            {
                Kind.Rectangular => 1.0,
                Kind.Hann => 0.5 - 0.5 * Math.Cos(x),
                Kind.Hamming => 0.54 - 0.46 * Math.Cos(x),
                Kind.Blackman => 0.42 - 0.5 * Math.Cos(x) + 0.08 * Math.Cos(2.0 * x),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
        }

        return w;
    }

    /// <summary>Mean of the window; divide amplitudes by this to undo the window's attenuation.</summary>
    public static double CoherentGain(ReadOnlySpan<double> window)
    {
        if (window.Length == 0)
        {
            return 1.0;
        }

        var sum = 0.0;
        foreach (var v in window)
        {
            sum += v;
        }

        return sum / window.Length;
    }

    public static void ApplyInPlace(Span<double> signal, ReadOnlySpan<double> window)
    {
        var n = Math.Min(signal.Length, window.Length);
        for (var i = 0; i < n; i++)
        {
            signal[i] *= window[i];
        }
    }
}
