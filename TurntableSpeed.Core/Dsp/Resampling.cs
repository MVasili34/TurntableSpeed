namespace TurntableSpeed.Core.Dsp;

/// <summary>A signal on a uniform time grid.</summary>
public readonly record struct UniformSeries(double StartTime, double SampleInterval, double[] Values)
{
    public int Count => Values.Length;

    public double SampleRate => SampleInterval > 0.0 ? 1.0 / SampleInterval : double.NaN;

    public double Duration => Count * SampleInterval;

    public double TimeAt(int index) => StartTime + index * SampleInterval;

    public static UniformSeries Empty { get; } = new(0.0, 0.0, []);
}

/// <summary>
/// Puts irregularly timestamped sensor data onto a uniform grid. Android sensor callbacks
/// jitter badly, and every FFT downstream assumes uniform spacing.
/// </summary>
public static class UniformResampler
{
    /// <summary>Median spacing of a timestamp series; robust against dropped samples.</summary>
    public static double MedianInterval(ReadOnlySpan<double> t)
    {
        if (t.Length < 2)
        {
            return double.NaN;
        }

        var deltas = new double[t.Length - 1];
        for (var i = 1; i < t.Length; i++)
        {
            deltas[i - 1] = t[i] - t[i - 1];
        }

        return SeriesMath.Median(deltas);
    }

    /// <summary>
    /// Linear interpolation onto a uniform grid. <paramref name="t"/> must be non-decreasing.
    /// Pass <paramref name="sampleInterval"/> ≤ 0 to use the median input interval.
    /// </summary>
    public static UniformSeries Resample(ReadOnlySpan<double> t, ReadOnlySpan<double> y, double sampleInterval = 0.0)
    {
        var n = Math.Min(t.Length, y.Length);
        if (n < 2)
        {
            return UniformSeries.Empty;
        }

        if (sampleInterval <= 0.0)
        {
            sampleInterval = MedianInterval(t.Slice(0, n));
        }

        if (!double.IsFinite(sampleInterval) || sampleInterval <= 0.0)
        {
            return UniformSeries.Empty;
        }

        var start = t[0];
        var end = t[n - 1];
        var span = end - start;
        if (!(span > 0.0))
        {
            return UniformSeries.Empty;
        }

        var count = (int)Math.Floor(span / sampleInterval) + 1;
        if (count < 2)
        {
            return UniformSeries.Empty;
        }

        var values = new double[count];
        var cursor = 0;
        for (var i = 0; i < count; i++)
        {
            var target = start + i * sampleInterval;
            while (cursor < n - 2 && t[cursor + 1] < target)
            {
                cursor++;
            }

            var t0 = t[cursor];
            var t1 = t[cursor + 1];
            var dt = t1 - t0;
            if (!(dt > 0.0))
            {
                values[i] = y[cursor];
                continue;
            }

            var alpha = Math.Clamp((target - t0) / dt, 0.0, 1.0);
            values[i] = y[cursor] * (1.0 - alpha) + y[cursor + 1] * alpha;
        }

        return new UniformSeries(start, sampleInterval, values);
    }
}

/// <summary>
/// Bandlimited (Lanczos) resampling. Used to simulate a turntable running off-speed: playing
/// a record <c>speedFactor</c> times too fast shortens the signal by that factor and raises
/// the pitch by 1200·log₂(speedFactor) cents.
/// </summary>
public static class Resampler
{
    public const int DefaultLobes = 8;

    public static float[] ChangeSpeed(ReadOnlySpan<float> input, double speedFactor, int lobes = DefaultLobes)
    {
        if (!(speedFactor > 0.0) || !double.IsFinite(speedFactor))
        {
            throw new ArgumentOutOfRangeException(nameof(speedFactor));
        }

        if (input.Length == 0)
        {
            return [];
        }

        var outputLength = (int)Math.Floor(input.Length / speedFactor);
        if (outputLength <= 0)
        {
            return [];
        }

        // Speeding up compresses the spectrum toward Nyquist; slow the kernel down instead of
        // aliasing when speedFactor > 1.
        var kernelScale = Math.Max(1.0, speedFactor);
        var output = new float[outputLength];

        for (var i = 0; i < outputLength; i++)
        {
            var position = i * speedFactor;
            var centre = (int)Math.Floor(position);
            var radius = (int)Math.Ceiling(lobes * kernelScale);

            var sum = 0.0;
            var weightSum = 0.0;
            for (var k = centre - radius; k <= centre + radius; k++)
            {
                if (k < 0 || k >= input.Length)
                {
                    continue;
                }

                var w = Lanczos((position - k) / kernelScale, lobes);
                if (w == 0.0)
                {
                    continue;
                }

                sum += w * input[k];
                weightSum += w;
            }

            output[i] = weightSum > 0.0 ? (float)(sum / weightSum) : 0f;
        }

        return output;
    }

    private static double Lanczos(double x, int lobes)
    {
        if (Math.Abs(x) >= lobes)
        {
            return 0.0;
        }

        if (Math.Abs(x) < 1e-12)
        {
            return 1.0;
        }

        var pix = Math.PI * x;
        return lobes * Math.Sin(pix) * Math.Sin(pix / lobes) / (pix * pix);
    }
}
