using System.Numerics;

namespace TurntableSpeed.Core.Dsp;

/// <summary>Phase unwrapping and circular statistics.</summary>
public static class PhaseMath
{
    public const double TwoPi = 2.0 * Math.PI;

    /// <summary>Fold an angle into (−π, π].</summary>
    public static double WrapToPi(double angle)
    {
        if (!double.IsFinite(angle))
        {
            return angle;
        }

        var wrapped = Math.IEEERemainder(angle, TwoPi);
        if (wrapped <= -Math.PI)
        {
            wrapped += TwoPi;
        }
        else if (wrapped > Math.PI)
        {
            wrapped -= TwoPi;
        }

        return wrapped;
    }

    /// <summary>Fold a value into [0, <paramref name="period"/>).</summary>
    public static double Mod(double value, double period)
    {
        var r = value % period;
        return r < 0.0 ? r + period : r;
    }

    /// <summary>
    /// Continue an unwrapped phase sequence: given the previous unwrapped value and the next
    /// wrapped measurement, return the next unwrapped value. Streaming counterpart of
    /// <see cref="Unwrap(System.ReadOnlySpan{double})"/>.
    /// </summary>
    public static double UnwrapStep(double previousUnwrapped, double wrapped) =>
        previousUnwrapped + WrapToPi(wrapped - previousUnwrapped);

    /// <summary>Remove ±2π jumps from a phase sequence.</summary>
    public static double[] Unwrap(ReadOnlySpan<double> wrapped)
    {
        var result = new double[wrapped.Length];
        if (wrapped.Length == 0)
        {
            return result;
        }

        result[0] = wrapped[0];
        for (var i = 1; i < wrapped.Length; i++)
        {
            result[i] = UnwrapStep(result[i - 1], wrapped[i]);
        }

        return result;
    }

    /// <summary>
    /// Robust rate of phase advance, radians per second: the median of the step-to-step wrapped
    /// change divided by the time step.
    /// <para>
    /// Immune to isolated bad samples in a way that <see cref="Unwrap(System.ReadOnlySpan{double})"/>
    /// is not — its steps are bounded to ±π by construction, so a sample thrown more than half a
    /// turn off does not produce a large step, it silently costs 2π of accumulated phase and
    /// shifts every later value. Requires |rate·Δt| &lt; π, i.e. more than two samples per turn.
    /// </para>
    /// </summary>
    public static double MedianRate(ReadOnlySpan<double> times, ReadOnlySpan<double> wrapped)
    {
        var n = Math.Min(times.Length, wrapped.Length);
        if (n < 2)
        {
            return double.NaN;
        }

        var rates = new List<double>(n - 1);
        for (var i = 1; i < n; i++)
        {
            var dt = times[i] - times[i - 1];
            if (dt > 0.0 && double.IsFinite(wrapped[i]) && double.IsFinite(wrapped[i - 1]))
            {
                rates.Add(WrapToPi(wrapped[i] - wrapped[i - 1]) / dt);
            }
        }

        return rates.Count == 0 ? double.NaN : SeriesMath.Median(rates.ToArray());
    }

    /// <summary>
    /// Unwrap by snapping each wrapped measurement onto the 2π branch closest to a predicted
    /// phase, instead of chaining from the previous sample. An outlier then costs only its own
    /// residual — it cannot displace the samples that follow it.
    /// </summary>
    public static double[] UnwrapAround(ReadOnlySpan<double> wrapped, ReadOnlySpan<double> predicted)
    {
        var n = Math.Min(wrapped.Length, predicted.Length);
        var result = new double[n];
        for (var i = 0; i < n; i++)
        {
            result[i] = wrapped[i] + TwoPi * Math.Round((predicted[i] - wrapped[i]) / TwoPi);
        }

        return result;
    }

    /// <summary>
    /// Unwrap a phase series that advances at a roughly constant rate, resistant both to heavy
    /// noise and to isolated gross outliers.
    /// <para>
    /// Each sample is placed on the 2π branch nearest a prediction from an α–β tracker rather
    /// than chained onto its predecessor. The tracker's own update uses a clipped innovation, so
    /// a sample thrown half a turn off lands on a sensible branch, contributes its own residual,
    /// and does not drag the prediction — which is what stops one bad sample from costing a
    /// whole turn of accumulated phase and shifting everything after it.
    /// </para>
    /// <para>
    /// The two failure modes pull in opposite directions and both have to be handled: chaining
    /// is exactly right under noise and hopeless under outliers, while snapping to a single
    /// global straight line is the reverse, because the line's own slope has to be estimated
    /// from the very data the outliers have corrupted.
    /// </para>
    /// </summary>
    /// <param name="trackingGain">
    /// α of the tracker, per sample. Larger follows genuine speed changes faster and averages
    /// noise less.
    /// </param>
    /// <param name="innovationClip">
    /// Largest phase innovation, in radians, allowed to steer the tracker. Symmetric, so it
    /// biases nothing; it only stops outliers from being believed.
    /// </param>
    public static double[] UnwrapSteady(
        ReadOnlySpan<double> times,
        ReadOnlySpan<double> wrapped,
        double trackingGain = 0.08,
        double innovationClip = 0.6)
    {
        var n = Math.Min(times.Length, wrapped.Length);
        if (n < 3)
        {
            return Unwrap(wrapped);
        }

        var rate = MedianRate(times, wrapped);
        if (!double.IsFinite(rate))
        {
            rate = 0.0;
        }

        var alpha = Math.Clamp(trackingGain, 1e-4, 1.0);
        var beta = alpha * alpha * 0.5;

        var unwrapped = new double[n];
        var level = wrapped[0];
        unwrapped[0] = level;

        for (var i = 1; i < n; i++)
        {
            var dt = times[i] - times[i - 1];
            if (!(dt > 0.0) || !double.IsFinite(dt))
            {
                dt = 0.0;
            }

            var predicted = level + rate * dt;
            var innovation = WrapToPi(wrapped[i] - predicted);

            // The measurement keeps its full innovation: that is its residual, and the
            // regression downstream is what decides how much to believe it.
            unwrapped[i] = predicted + innovation;

            // The tracker only accepts a bounded correction.
            var clipped = Math.Clamp(innovation, -innovationClip, innovationClip);
            level = predicted + alpha * clipped;
            if (dt > 0.0)
            {
                rate += beta * clipped / dt;
            }
        }

        return unwrapped;
    }

    /// <summary>Circular mean of angles in radians, with optional weights.</summary>
    /// <returns>
    /// <c>Mean</c> in (−π, π] and <c>Resultant</c> in [0, 1] — the concentration of the sample.
    /// Resultant near 0 means the angles carry no usable direction.
    /// </returns>
    public static (double Mean, double Resultant) CircularMean(
        ReadOnlySpan<double> angles,
        ReadOnlySpan<double> weights = default)
    {
        var sumSin = 0.0;
        var sumCos = 0.0;
        var sumWeight = 0.0;

        for (var i = 0; i < angles.Length; i++)
        {
            var w = weights.Length == angles.Length ? weights[i] : 1.0;
            if (!double.IsFinite(angles[i]) || !double.IsFinite(w) || w <= 0.0)
            {
                continue;
            }

            sumSin += w * Math.Sin(angles[i]);
            sumCos += w * Math.Cos(angles[i]);
            sumWeight += w;
        }

        if (sumWeight <= 0.0)
        {
            return (double.NaN, 0.0);
        }

        var meanSin = sumSin / sumWeight;
        var meanCos = sumCos / sumWeight;
        var resultant = Math.Sqrt(meanSin * meanSin + meanCos * meanCos);
        return (Math.Atan2(meanSin, meanCos), Math.Min(1.0, resultant));
    }

    /// <summary>Circular mean of a set of complex phasors (weights are their magnitudes).</summary>
    public static (double Mean, double Resultant) CircularMean(ReadOnlySpan<Complex> phasors)
    {
        var sum = Complex.Zero;
        var magnitude = 0.0;
        foreach (var p in phasors)
        {
            if (!double.IsFinite(p.Real) || !double.IsFinite(p.Imaginary))
            {
                continue;
            }

            sum += p;
            magnitude += p.Magnitude;
        }

        if (magnitude <= 0.0)
        {
            return (double.NaN, 0.0);
        }

        return (sum.Phase, Math.Min(1.0, sum.Magnitude / magnitude));
    }

    /// <summary>
    /// Circular mean over an arbitrary period rather than 2π — used for pitch-class folding,
    /// where the period is one semitone.
    /// </summary>
    public static (double Mean, double Resultant) CircularMeanOverPeriod(
        ReadOnlySpan<double> values,
        double period,
        ReadOnlySpan<double> weights = default)
    {
        var scale = TwoPi / period;
        var angles = new double[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            angles[i] = values[i] * scale;
        }

        var (mean, resultant) = CircularMean(angles, weights);
        return (double.IsNaN(mean) ? double.NaN : mean / scale, resultant);
    }
}
