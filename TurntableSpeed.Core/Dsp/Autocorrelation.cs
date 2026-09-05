using System.Numerics;

namespace TurntableSpeed.Core.Dsp;

/// <summary>FFT-based autocorrelation. Linear (not circular): the input is zero-padded.</summary>
public static class Autocorrelation
{
    /// <summary>
    /// Autocorrelation for lags 0..<paramref name="maxLag"/>.
    /// </summary>
    /// <param name="removeMean">Subtract the mean first — almost always what you want.</param>
    /// <param name="unbiased">
    /// Divide each lag by the number of overlapping samples instead of by n. Keeps long lags
    /// from being artificially suppressed, at the cost of noisier estimates near the end.
    /// </param>
    /// <param name="normalize">Scale so that lag 0 equals 1.</param>
    public static double[] Compute(
        ReadOnlySpan<double> x,
        int maxLag,
        bool removeMean = true,
        bool unbiased = true,
        bool normalize = true)
    {
        var n = x.Length;
        if (n == 0 || maxLag < 0)
        {
            return [];
        }

        maxLag = Math.Min(maxLag, n - 1);
        if (maxLag < 0)
        {
            return [];
        }

        var centred = x.ToArray();
        if (removeMean)
        {
            SeriesMath.RemoveMeanInPlace(centred);
        }

        var fftLength = Fft.NextPowerOfTwo(n + maxLag + 1);
        var buffer = new Complex[fftLength];
        for (var i = 0; i < n; i++)
        {
            buffer[i] = new Complex(centred[i], 0.0);
        }

        Fft.ForwardInPlace(buffer);
        for (var i = 0; i < fftLength; i++)
        {
            var magnitudeSquared = buffer[i].Real * buffer[i].Real + buffer[i].Imaginary * buffer[i].Imaginary;
            buffer[i] = new Complex(magnitudeSquared, 0.0);
        }

        Fft.InverseInPlace(buffer);

        var result = new double[maxLag + 1];
        for (var lag = 0; lag <= maxLag; lag++)
        {
            var raw = buffer[lag].Real;
            result[lag] = unbiased ? raw / (n - lag) : raw / n;
        }

        if (normalize)
        {
            var zero = result[0];
            if (zero > 0.0 && double.IsFinite(zero))
            {
                for (var lag = 0; lag <= maxLag; lag++)
                {
                    result[lag] /= zero;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Noise floor of an autocorrelation function outside a protected region around the peak.
    /// Used to convert peak sharpness into a lag uncertainty.
    /// </summary>
    public static double NoiseFloor(ReadOnlySpan<double> acf, int from, int to, int peakIndex, int exclusionRadius)
    {
        from = Math.Max(0, from);
        to = Math.Min(acf.Length - 1, to);
        var values = new List<double>(Math.Max(0, to - from + 1));
        for (var i = from; i <= to; i++)
        {
            if (Math.Abs(i - peakIndex) <= exclusionRadius)
            {
                continue;
            }

            values.Add(acf[i]);
        }

        if (values.Count < 8)
        {
            return double.NaN;
        }

        var array = values.ToArray();
        return SeriesMath.StandardDeviation(array);
    }

    /// <summary>
    /// Background level of an autocorrelation function, measured so that genuine structure does
    /// not inflate it: the median absolute deviation, scaled to be comparable with a standard
    /// deviation on Gaussian data.
    /// <para>
    /// Needed because <see cref="NoiseFloor"/> answers a different question. With several clicks
    /// per revolution the curve is a comb, and the standard deviation over the search band
    /// counts the comb's own teeth as noise: a 45 rpm record with two clicks per revolution
    /// scores 1589 by this measure and 11 by that one, on the same recording. Deciding whether a
    /// peak exists at all against a figure that rises with the strength of the evidence is
    /// exactly backwards, so detection uses this and uncertainty keeps the standard deviation.
    /// </para>
    /// </summary>
    public static double RobustNoiseFloor(
        ReadOnlySpan<double> acf,
        int from,
        int to,
        int peakIndex,
        int exclusionRadius)
    {
        from = Math.Max(0, from);
        to = Math.Min(acf.Length - 1, to);
        var values = new List<double>(Math.Max(0, to - from + 1));
        for (var i = from; i <= to; i++)
        {
            if (Math.Abs(i - peakIndex) <= exclusionRadius)
            {
                continue;
            }

            values.Add(acf[i]);
        }

        if (values.Count < 8)
        {
            return double.NaN;
        }

        var array = values.ToArray();
        Array.Sort(array);
        var median = SeriesMath.PercentileOfSorted(array, 0.5);

        for (var i = 0; i < array.Length; i++)
        {
            array[i] = Math.Abs(array[i] - median);
        }

        Array.Sort(array);

        // 1.4826 · MAD equals σ for a normal distribution, so the threshold this feeds keeps
        // being expressible in the sigmas the rest of the estimator talks in.
        return SeriesMath.PercentileOfSorted(array, 0.5) * 1.4826;
    }
}
