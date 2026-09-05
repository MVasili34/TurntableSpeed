namespace TurntableSpeed.Core.Dsp;

/// <summary>Small statistics and de-trending helpers used across the estimators.</summary>
public static class SeriesMath
{
    public static double Mean(ReadOnlySpan<double> x)
    {
        if (x.Length == 0)
        {
            return double.NaN;
        }

        var sum = 0.0;
        foreach (var v in x)
        {
            sum += v;
        }

        return sum / x.Length;
    }

    /// <summary>Sample variance (divides by n−1).</summary>
    public static double Variance(ReadOnlySpan<double> x)
    {
        if (x.Length < 2)
        {
            return double.NaN;
        }

        var mean = Mean(x);
        var sum = 0.0;
        foreach (var v in x)
        {
            var d = v - mean;
            sum += d * d;
        }

        return sum / (x.Length - 1);
    }

    public static double StandardDeviation(ReadOnlySpan<double> x) => Math.Sqrt(Variance(x));

    public static double RootMeanSquare(ReadOnlySpan<double> x)
    {
        if (x.Length == 0)
        {
            return double.NaN;
        }

        var sum = 0.0;
        foreach (var v in x)
        {
            sum += v * v;
        }

        return Math.Sqrt(sum / x.Length);
    }

    public static double Min(ReadOnlySpan<double> x)
    {
        var m = double.PositiveInfinity;
        foreach (var v in x)
        {
            if (v < m)
            {
                m = v;
            }
        }

        return m;
    }

    public static double Max(ReadOnlySpan<double> x)
    {
        var m = double.NegativeInfinity;
        foreach (var v in x)
        {
            if (v > m)
            {
                m = v;
            }
        }

        return m;
    }

    /// <summary>Linear-interpolated percentile, <paramref name="p"/> in [0, 1]. Copies the input.</summary>
    public static double Percentile(ReadOnlySpan<double> x, double p)
    {
        if (x.Length == 0)
        {
            return double.NaN;
        }

        var sorted = x.ToArray();
        Array.Sort(sorted);
        return PercentileOfSorted(sorted, p);
    }

    public static double PercentileOfSorted(ReadOnlySpan<double> sorted, double p)
    {
        if (sorted.Length == 0)
        {
            return double.NaN;
        }

        var position = Math.Clamp(p, 0.0, 1.0) * (sorted.Length - 1);
        var low = (int)Math.Floor(position);
        var high = (int)Math.Ceiling(position);
        if (low == high)
        {
            return sorted[low];
        }

        var frac = position - low;
        return sorted[low] * (1.0 - frac) + sorted[high] * frac;
    }

    public static double Median(ReadOnlySpan<double> x) => Percentile(x, 0.5);

    /// <summary>Median absolute deviation, scaled to be a consistent estimator of σ for normal data.</summary>
    public static double MedianAbsoluteDeviation(ReadOnlySpan<double> x)
    {
        if (x.Length == 0)
        {
            return double.NaN;
        }

        var median = Median(x);
        var deviations = new double[x.Length];
        for (var i = 0; i < x.Length; i++)
        {
            deviations[i] = Math.Abs(x[i] - median);
        }

        return 1.4826 * Median(deviations);
    }

    public static void RemoveMeanInPlace(Span<double> x)
    {
        var mean = Mean(x);
        if (!double.IsFinite(mean))
        {
            return;
        }

        for (var i = 0; i < x.Length; i++)
        {
            x[i] -= mean;
        }
    }

    /// <summary>
    /// Remove the best-fit line assuming a uniform sample grid. Required before any FFT of a
    /// drifting series, otherwise the trend smears across the low bins and buries the wow peak.
    /// </summary>
    public static void DetrendLinearInPlace(Span<double> y)
    {
        var n = y.Length;
        if (n < 2)
        {
            RemoveMeanInPlace(y);
            return;
        }

        // x = 0..n−1: closed-form sums avoid an extra allocation.
        var sumX = (n - 1) * n / 2.0;
        var sumXx = (n - 1.0) * n * (2.0 * n - 1.0) / 6.0;
        var sumY = 0.0;
        var sumXy = 0.0;
        for (var i = 0; i < n; i++)
        {
            sumY += y[i];
            sumXy += i * y[i];
        }

        var denominator = n * sumXx - sumX * sumX;
        if (Math.Abs(denominator) < 1e-300)
        {
            RemoveMeanInPlace(y);
            return;
        }

        var slope = (n * sumXy - sumX * sumY) / denominator;
        var intercept = (sumY - slope * sumX) / n;
        for (var i = 0; i < n; i++)
        {
            y[i] -= slope * i + intercept;
        }
    }

    public static double[] DetrendLinear(ReadOnlySpan<double> y)
    {
        var copy = y.ToArray();
        DetrendLinearInPlace(copy);
        return copy;
    }

    /// <summary>Centred moving average with edge clamping; <paramref name="window"/> is forced odd.</summary>
    public static double[] MovingAverage(ReadOnlySpan<double> x, int window)
    {
        if (window <= 1 || x.Length == 0)
        {
            return x.ToArray();
        }

        if (window % 2 == 0)
        {
            window++;
        }

        var half = window / 2;
        var result = new double[x.Length];
        for (var i = 0; i < x.Length; i++)
        {
            var from = Math.Max(0, i - half);
            var to = Math.Min(x.Length - 1, i + half);
            var sum = 0.0;
            for (var j = from; j <= to; j++)
            {
                sum += x[j];
            }

            result[i] = sum / (to - from + 1);
        }

        return result;
    }

    /// <summary>
    /// Subtract a running median. This is what separates impulsive clicks from the stationary
    /// programme material: the median tracks the music, the residual keeps the transients.
    /// </summary>
    public static double[] SubtractRunningMedian(ReadOnlySpan<double> x, int window)
    {
        var median = RunningMedian(x, window);
        var result = new double[x.Length];
        for (var i = 0; i < x.Length; i++)
        {
            result[i] = x[i] - median[i];
        }

        return result;
    }

    /// <summary>Centred running median, edges clamped. O(n·log w) via an ordered window.</summary>
    public static double[] RunningMedian(ReadOnlySpan<double> x, int window)
    {
        if (window <= 1 || x.Length == 0)
        {
            return x.ToArray();
        }

        if (window % 2 == 0)
        {
            window++;
        }

        var half = window / 2;
        var result = new double[x.Length];
        var ordered = new List<double>(window);

        for (var i = 0; i < x.Length; i++)
        {
            var from = Math.Max(0, i - half);
            var to = Math.Min(x.Length - 1, i + half);

            if (i == 0)
            {
                for (var j = from; j <= to; j++)
                {
                    Insert(ordered, x[j]);
                }
            }
            else
            {
                var previousFrom = Math.Max(0, i - 1 - half);
                var previousTo = Math.Min(x.Length - 1, i - 1 + half);
                if (from > previousFrom)
                {
                    Remove(ordered, x[previousFrom]);
                }

                if (to > previousTo)
                {
                    Insert(ordered, x[to]);
                }
            }

            result[i] = ordered.Count % 2 == 1
                ? ordered[ordered.Count / 2]
                : 0.5 * (ordered[ordered.Count / 2 - 1] + ordered[ordered.Count / 2]);
        }

        return result;

        static void Insert(List<double> list, double value)
        {
            var index = list.BinarySearch(value);
            if (index < 0)
            {
                index = ~index;
            }

            list.Insert(index, value);
        }

        static void Remove(List<double> list, double value)
        {
            var index = list.BinarySearch(value);
            if (index >= 0)
            {
                list.RemoveAt(index);
            }
        }
    }

    /// <summary>Lag-1 autocorrelation of a series; used to inflate standard errors when residuals are not white.</summary>
    public static double Lag1Correlation(ReadOnlySpan<double> x)
    {
        if (x.Length < 3)
        {
            return 0.0;
        }

        var mean = Mean(x);
        var numerator = 0.0;
        var denominator = 0.0;
        for (var i = 0; i < x.Length; i++)
        {
            var d = x[i] - mean;
            denominator += d * d;
            if (i > 0)
            {
                numerator += d * (x[i - 1] - mean);
            }
        }

        if (denominator <= 0.0)
        {
            return 0.0;
        }

        return Math.Clamp(numerator / denominator, -0.999, 0.999);
    }
}
