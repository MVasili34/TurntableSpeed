using System.Numerics;

namespace TurntableSpeed.Core.Dsp;

/// <summary>
/// One second-order section, transposed direct form II. Stateful and streaming: audio
/// arrives in blocks and the filter must not ring at block boundaries.
/// </summary>
public sealed class Biquad
{
    private readonly double _b0;
    private readonly double _b1;
    private readonly double _b2;
    private readonly double _a1;
    private readonly double _a2;
    private double _z1;
    private double _z2;

    public Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        if (a0 == 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(a0), "a0 must be non-zero.");
        }

        _b0 = b0 / a0;
        _b1 = b1 / a0;
        _b2 = b2 / a0;
        _a1 = a1 / a0;
        _a2 = a2 / a0;
    }

    public double Process(double x)
    {
        var y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        return y;
    }

    public void Reset()
    {
        _z1 = 0.0;
        _z2 = 0.0;
    }

    /// <summary>Complex response at a normalised frequency (cycles/sample, 0..0.5).</summary>
    public Complex Response(double normalizedFrequency)
    {
        var w = 2.0 * Math.PI * normalizedFrequency;
        var z1 = Complex.FromPolarCoordinates(1.0, -w);
        var z2 = z1 * z1;
        var num = _b0 + _b1 * z1 + _b2 * z2;
        var den = Complex.One + _a1 * z1 + _a2 * z2;
        return num / den;
    }
}

/// <summary>A chain of <see cref="Biquad"/> sections.</summary>
public sealed class BiquadCascade
{
    private readonly Biquad[] _sections;

    public BiquadCascade(params Biquad[] sections) => _sections = sections;

    public int SectionCount => _sections.Length;

    /// <summary>The individual sections, in order. Useful for chaining two designs together.</summary>
    public IReadOnlyList<Biquad> Sections => _sections;

    public double Process(double x)
    {
        foreach (var section in _sections)
        {
            x = section.Process(x);
        }

        return x;
    }

    public void Process(ReadOnlySpan<float> input, Span<double> output)
    {
        for (var i = 0; i < input.Length; i++)
        {
            output[i] = Process(input[i]);
        }
    }

    public void ProcessInPlace(Span<double> buffer)
    {
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = Process(buffer[i]);
        }
    }

    public double[] Process(ReadOnlySpan<double> input)
    {
        var output = new double[input.Length];
        for (var i = 0; i < input.Length; i++)
        {
            output[i] = Process(input[i]);
        }

        return output;
    }

    public void Reset()
    {
        foreach (var section in _sections)
        {
            section.Reset();
        }
    }

    /// <summary>Magnitude of the transfer function at <paramref name="frequency"/> Hz.</summary>
    public double MagnitudeResponse(double frequency, double sampleRate)
    {
        var normalized = frequency / sampleRate;
        var h = Complex.One;
        foreach (var section in _sections)
        {
            h *= section.Response(normalized);
        }

        return h.Magnitude;
    }

    public double MagnitudeResponseDb(double frequency, double sampleRate) =>
        20.0 * Math.Log10(Math.Max(1e-300, MagnitudeResponse(frequency, sampleRate)));
}

/// <summary>
/// Butterworth designs by the bilinear transform (RBJ cookbook sections with Butterworth
/// pole Q values), which places the −3 dB point exactly at the requested digital frequency.
/// </summary>
public static class Butterworth
{
    /// <summary>Pole Q of section <paramref name="section"/> of an even-order Butterworth filter.</summary>
    public static double SectionQ(int order, int section)
    {
        if (order < 2 || order % 2 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(order), "Only even orders ≥ 2 are supported.");
        }

        if (section < 0 || section >= order / 2)
        {
            throw new ArgumentOutOfRangeException(nameof(section));
        }

        return 1.0 / (2.0 * Math.Cos(Math.PI * (2.0 * section + 1.0) / (2.0 * order)));
    }

    public static BiquadCascade LowPass(double cutoffHz, double sampleRate, int order = 4)
    {
        Validate(cutoffHz, sampleRate, order);
        var sections = new Biquad[order / 2];
        for (var i = 0; i < sections.Length; i++)
        {
            sections[i] = LowPassSection(cutoffHz, sampleRate, SectionQ(order, i));
        }

        return new BiquadCascade(sections);
    }

    public static BiquadCascade HighPass(double cutoffHz, double sampleRate, int order = 4)
    {
        Validate(cutoffHz, sampleRate, order);
        var sections = new Biquad[order / 2];
        for (var i = 0; i < sections.Length; i++)
        {
            sections[i] = HighPassSection(cutoffHz, sampleRate, SectionQ(order, i));
        }

        return new BiquadCascade(sections);
    }

    /// <summary>Band-pass built as a high-pass followed by a low-pass. Order applies to each half.</summary>
    public static BiquadCascade BandPass(double lowHz, double highHz, double sampleRate, int order = 4)
    {
        if (lowHz >= highHz)
        {
            throw new ArgumentOutOfRangeException(nameof(lowHz), "lowHz must be below highHz.");
        }

        var high = HighPass(lowHz, sampleRate, order);
        var low = LowPass(highHz, sampleRate, order);
        var sections = new List<Biquad>(high.SectionCount + low.SectionCount);
        sections.AddRange(high.Sections);
        sections.AddRange(low.Sections);
        return new BiquadCascade(sections.ToArray());
    }

    private static Biquad LowPassSection(double cutoffHz, double sampleRate, double q)
    {
        var w0 = 2.0 * Math.PI * cutoffHz / sampleRate;
        var cos = Math.Cos(w0);
        var alpha = Math.Sin(w0) / (2.0 * q);

        var b0 = (1.0 - cos) / 2.0;
        var b1 = 1.0 - cos;
        var b2 = b0;
        var a0 = 1.0 + alpha;
        var a1 = -2.0 * cos;
        var a2 = 1.0 - alpha;
        return new Biquad(b0, b1, b2, a0, a1, a2);
    }

    private static Biquad HighPassSection(double cutoffHz, double sampleRate, double q)
    {
        var w0 = 2.0 * Math.PI * cutoffHz / sampleRate;
        var cos = Math.Cos(w0);
        var alpha = Math.Sin(w0) / (2.0 * q);

        var b0 = (1.0 + cos) / 2.0;
        var b1 = -(1.0 + cos);
        var b2 = b0;
        var a0 = 1.0 + alpha;
        var a1 = -2.0 * cos;
        var a2 = 1.0 - alpha;
        return new Biquad(b0, b1, b2, a0, a1, a2);
    }

    private static void Validate(double cutoffHz, double sampleRate, int order)
    {
        if (sampleRate <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        if (cutoffHz <= 0.0 || cutoffHz >= sampleRate / 2.0)
        {
            throw new ArgumentOutOfRangeException(nameof(cutoffHz), "Cutoff must be inside (0, Nyquist).");
        }

        if (order < 2 || order % 2 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(order), "Only even orders ≥ 2 are supported.");
        }
    }
}
