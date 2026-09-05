namespace TurntableSpeed.Core.Dsp;

/// <summary>Sub-sample peak location by fitting a parabola through three samples.</summary>
/// <param name="Offset">Peak position relative to the centre sample, in samples, within [−0.5, 0.5].</param>
/// <param name="Value">Interpolated peak height.</param>
/// <param name="Curvature">
/// c in y ≈ Value − c·(x − peak)². Positive for a maximum. A displacement ε of the sample
/// values moves the located peak by roughly ε/(2c), which is where the lag uncertainty
/// of the autocorrelation estimator comes from.
/// </param>
public readonly record struct ParabolicPeak(double Offset, double Value, double Curvature);

public static class PeakInterpolation
{
    /// <summary>Fit a parabola through (−1, yPrev), (0, yCentre), (+1, yNext).</summary>
    public static ParabolicPeak Parabolic(double yPrev, double yCentre, double yNext)
    {
        // y = a·x² + b·x + c with c = yCentre.
        var a = 0.5 * (yPrev + yNext) - yCentre;
        var b = 0.5 * (yNext - yPrev);

        if (Math.Abs(a) < 1e-300)
        {
            return new ParabolicPeak(0.0, yCentre, 0.0);
        }

        var offset = -b / (2.0 * a);
        if (!double.IsFinite(offset))
        {
            return new ParabolicPeak(0.0, yCentre, 0.0);
        }

        offset = Math.Clamp(offset, -1.0, 1.0);
        var value = yCentre + 0.5 * b * offset;
        return new ParabolicPeak(offset, value, -a);
    }

    /// <summary>
    /// Locate the maximum of <paramref name="values"/> within [<paramref name="from"/>,
    /// <paramref name="to"/>] and refine it parabolically. Returns the fractional index.
    /// </summary>
    public static (double Index, ParabolicPeak Peak) RefineMaximum(
        ReadOnlySpan<double> values,
        int from,
        int to)
    {
        if (values.Length == 0)
        {
            return (double.NaN, default);
        }

        from = Math.Max(0, from);
        to = Math.Min(values.Length - 1, to);
        if (from > to)
        {
            return (double.NaN, default);
        }

        var bestIndex = from;
        for (var i = from + 1; i <= to; i++)
        {
            if (values[i] > values[bestIndex])
            {
                bestIndex = i;
            }
        }

        if (bestIndex <= 0 || bestIndex >= values.Length - 1)
        {
            return (bestIndex, new ParabolicPeak(0.0, values[bestIndex], 0.0));
        }

        var peak = Parabolic(values[bestIndex - 1], values[bestIndex], values[bestIndex + 1]);
        return (bestIndex + peak.Offset, peak);
    }

    /// <summary>Indices of strict local maxima inside [<paramref name="from"/>, <paramref name="to"/>].</summary>
    public static List<int> LocalMaxima(ReadOnlySpan<double> values, int from = 0, int to = int.MaxValue)
    {
        var result = new List<int>();
        from = Math.Max(1, from);
        to = Math.Min(values.Length - 2, to);
        for (var i = from; i <= to; i++)
        {
            if (values[i] > values[i - 1] && values[i] >= values[i + 1])
            {
                result.Add(i);
            }
        }

        return result;
    }
}
