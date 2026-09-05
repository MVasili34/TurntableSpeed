using System.Numerics;

namespace TurntableSpeed.Core.Dsp;

/// <summary>Analytic-signal utilities.</summary>
public static class Hilbert
{
    /// <summary>
    /// Analytic signal via the FFT: zero the negative frequencies, double the positive ones.
    /// The imaginary part is the Hilbert transform of the input.
    /// </summary>
    public static Complex[] Analytic(ReadOnlySpan<double> x)
    {
        var n = x.Length;
        if (n == 0)
        {
            return [];
        }

        var buffer = new Complex[n];
        for (var i = 0; i < n; i++)
        {
            buffer[i] = new Complex(x[i], 0.0);
        }

        Fft.ForwardInPlace(buffer);

        if (n % 2 == 0)
        {
            for (var k = 1; k < n / 2; k++)
            {
                buffer[k] *= 2.0;
            }

            for (var k = n / 2 + 1; k < n; k++)
            {
                buffer[k] = Complex.Zero;
            }
        }
        else
        {
            for (var k = 1; k <= (n - 1) / 2; k++)
            {
                buffer[k] *= 2.0;
            }

            for (var k = (n + 1) / 2; k < n; k++)
            {
                buffer[k] = Complex.Zero;
            }
        }

        Fft.InverseInPlace(buffer);
        return buffer;
    }

    /// <summary>Instantaneous amplitude |analytic(x)|.</summary>
    public static double[] Envelope(ReadOnlySpan<double> x)
    {
        var analytic = Analytic(x);
        var result = new double[analytic.Length];
        for (var i = 0; i < analytic.Length; i++)
        {
            result[i] = analytic[i].Magnitude;
        }

        return result;
    }

    /// <summary>Unwrapped instantaneous phase of the analytic signal.</summary>
    public static double[] InstantaneousPhase(ReadOnlySpan<double> x)
    {
        var analytic = Analytic(x);
        var wrapped = new double[analytic.Length];
        for (var i = 0; i < analytic.Length; i++)
        {
            wrapped[i] = analytic[i].Phase;
        }

        return PhaseMath.Unwrap(wrapped);
    }
}

/// <summary>
/// Block-wise RMS envelope follower. Consumes audio in arbitrary chunk sizes and emits one
/// envelope sample per <see cref="Hop"/> input samples, so the envelope rate stays exactly
/// <c>sampleRate / hop</c> regardless of how the platform decides to slice the stream.
/// </summary>
public sealed class RmsEnvelopeFollower
{
    private double _sumSquares;
    private int _filled;

    public RmsEnvelopeFollower(int hop)
    {
        if (hop <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hop));
        }

        Hop = hop;
    }

    public int Hop { get; }

    /// <summary>Number of input samples consumed since the last <see cref="Reset"/>.</summary>
    public long SamplesConsumed { get; private set; }

    /// <summary>Number of envelope samples emitted since the last <see cref="Reset"/>.</summary>
    public long EnvelopeSamplesEmitted { get; private set; }

    /// <summary>Choose a hop giving the requested envelope period; never returns 0.</summary>
    public static int HopForPeriod(double sampleRate, double periodSeconds) =>
        Math.Max(1, (int)Math.Round(sampleRate * periodSeconds));

    public int Push(ReadOnlySpan<double> samples, ICollection<double> output)
    {
        var emitted = 0;
        foreach (var sample in samples)
        {
            _sumSquares += sample * sample;
            _filled++;
            SamplesConsumed++;

            if (_filled < Hop)
            {
                continue;
            }

            output.Add(Math.Sqrt(_sumSquares / Hop));
            emitted++;
            EnvelopeSamplesEmitted++;
            _sumSquares = 0.0;
            _filled = 0;
        }

        return emitted;
    }

    public void Reset()
    {
        _sumSquares = 0.0;
        _filled = 0;
        SamplesConsumed = 0;
        EnvelopeSamplesEmitted = 0;
    }
}
