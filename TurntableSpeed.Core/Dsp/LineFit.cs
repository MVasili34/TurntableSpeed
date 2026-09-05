namespace TurntableSpeed.Core.Dsp;

/// <summary>
/// A fitted straight line together with everything needed to state an uncertainty.
/// The slope standard error is what turns a phase ramp into a confidence interval on speed.
/// </summary>
public readonly record struct LineFit(
    double Slope,
    double Intercept,
    double SlopeStandardError,
    double InterceptStandardError,
    double ResidualStandardDeviation,
    double RSquared,
    int Count,
    double Lag1ResidualCorrelation)
{
    public static LineFit Invalid { get; } =
        new(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, 0, 0.0);

    public bool IsValid => Count >= 3 && double.IsFinite(Slope) && double.IsFinite(SlopeStandardError);

    public double ValueAt(double x) => Slope * x + Intercept;

    /// <summary>
    /// Effective sample size after accounting for serial correlation of the residuals:
    /// n·(1−ρ)/(1+ρ). Slow magnetic wander makes residuals correlated, and pretending they
    /// are independent would understate the interval.
    /// </summary>
    public double EffectiveCount
    {
        get
        {
            var rho = Lag1ResidualCorrelation;
            if (rho <= 0.0)
            {
                return Count;
            }

            return Math.Max(3.0, Count * (1.0 - rho) / (1.0 + rho));
        }
    }

    /// <summary>Slope standard error inflated for serially correlated residuals.</summary>
    public double SlopeStandardErrorCorrected
    {
        get
        {
            if (!IsValid)
            {
                return double.NaN;
            }

            var inflation = Math.Sqrt(Count / EffectiveCount);
            return SlopeStandardError * inflation;
        }
    }
}

public static class WeightedLeastSquares
{
    /// <summary>
    /// Fit y = slope·x + intercept. <paramref name="weights"/> are relative precisions; pass
    /// an empty span for an unweighted fit. Weights are normalised internally, so scaling
    /// them all by a constant does not change the reported uncertainty.
    /// </summary>
    public static LineFit Fit(ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> weights = default)
    {
        var n = Math.Min(x.Length, y.Length);
        if (n < 3)
        {
            return LineFit.Invalid;
        }

        var useWeights = weights.Length == n;

        var weightSum = 0.0;
        var used = 0;
        for (var i = 0; i < n; i++)
        {
            var w = useWeights ? weights[i] : 1.0;
            if (!double.IsFinite(x[i]) || !double.IsFinite(y[i]) || !double.IsFinite(w) || w <= 0.0)
            {
                continue;
            }

            weightSum += w;
            used++;
        }

        if (used < 3 || weightSum <= 0.0)
        {
            return LineFit.Invalid;
        }

        // Normalise so the mean weight is 1: keeps the residual variance interpretable.
        var normalisation = used / weightSum;

        double sw = 0, sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (var i = 0; i < n; i++)
        {
            var w = (useWeights ? weights[i] : 1.0);
            if (!double.IsFinite(x[i]) || !double.IsFinite(y[i]) || !double.IsFinite(w) || w <= 0.0)
            {
                continue;
            }

            w *= normalisation;
            sw += w;
            sx += w * x[i];
            sy += w * y[i];
            sxx += w * x[i] * x[i];
            sxy += w * x[i] * y[i];
        }

        var denominator = sw * sxx - sx * sx;
        if (Math.Abs(denominator) < 1e-300)
        {
            return LineFit.Invalid;
        }

        var slope = (sw * sxy - sx * sy) / denominator;
        var intercept = (sxx * sy - sx * sxy) / denominator;

        var weightedMeanY = sy / sw;
        var residualSumSquares = 0.0;
        var totalSumSquares = 0.0;
        var residuals = new double[used];
        var k = 0;
        for (var i = 0; i < n; i++)
        {
            var w = (useWeights ? weights[i] : 1.0);
            if (!double.IsFinite(x[i]) || !double.IsFinite(y[i]) || !double.IsFinite(w) || w <= 0.0)
            {
                continue;
            }

            w *= normalisation;
            var residual = y[i] - (slope * x[i] + intercept);
            residuals[k++] = residual;
            residualSumSquares += w * residual * residual;
            var deviation = y[i] - weightedMeanY;
            totalSumSquares += w * deviation * deviation;
        }

        var residualVariance = residualSumSquares / (used - 2);
        var slopeVariance = residualVariance * sw / denominator;
        var interceptVariance = residualVariance * sxx / denominator;
        var rSquared = totalSumSquares > 0.0 ? 1.0 - residualSumSquares / totalSumSquares : double.NaN;

        return new LineFit(
            slope,
            intercept,
            Math.Sqrt(Math.Max(0.0, slopeVariance)),
            Math.Sqrt(Math.Max(0.0, interceptVariance)),
            Math.Sqrt(Math.Max(0.0, residualVariance)),
            rSquared,
            used,
            SeriesMath.Lag1Correlation(residuals));
    }
}

/// <summary>
/// Huber-weighted robust line fit by iteratively reweighted least squares. A momentary
/// magnetic disturbance — a passing motor, a speaker magnet — must not tilt the phase ramp.
/// </summary>
public static class RobustLineFit
{
    public const double DefaultTuning = 1.345;

    /// <param name="tuning">Huber constant in units of robust σ. 1.345 gives 95% efficiency on normal data.</param>
    /// <param name="maxIterations">IRLS is cheap; convergence normally takes 3–5 passes.</param>
    public static LineFit Huber(
        ReadOnlySpan<double> x,
        ReadOnlySpan<double> y,
        ReadOnlySpan<double> priorWeights = default,
        double tuning = DefaultTuning,
        int maxIterations = 20,
        double tolerance = 1e-12)
    {
        var n = Math.Min(x.Length, y.Length);
        if (n < 3)
        {
            return LineFit.Invalid;
        }

        var weights = new double[n];
        for (var i = 0; i < n; i++)
        {
            weights[i] = priorWeights.Length == n ? priorWeights[i] : 1.0;
        }

        var fit = WeightedLeastSquares.Fit(x, y, weights);
        if (!fit.IsValid)
        {
            return fit;
        }

        var residuals = new double[n];
        var previousSlope = fit.Slope;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            for (var i = 0; i < n; i++)
            {
                residuals[i] = y[i] - fit.ValueAt(x[i]);
            }

            var scale = SeriesMath.MedianAbsoluteDeviation(residuals);
            if (!double.IsFinite(scale) || scale <= 0.0)
            {
                break;
            }

            var threshold = tuning * scale;
            for (var i = 0; i < n; i++)
            {
                var prior = priorWeights.Length == n ? priorWeights[i] : 1.0;
                var absolute = Math.Abs(residuals[i]);
                weights[i] = absolute <= threshold ? prior : prior * threshold / absolute;
            }

            fit = WeightedLeastSquares.Fit(x, y, weights);
            if (!fit.IsValid)
            {
                return LineFit.Invalid;
            }

            if (Math.Abs(fit.Slope - previousSlope) <= tolerance * Math.Max(1.0, Math.Abs(fit.Slope)))
            {
                break;
            }

            previousSlope = fit.Slope;
        }

        return priorWeights.Length == n ? fit : WithRobustStandardErrors(x, y, fit, tuning);
    }

    /// <summary>
    /// Replace the weighted-least-squares standard errors with the ones an M-estimator actually
    /// has.
    /// <para>
    /// The IRLS loop ends in a weighted fit, and the weighted formula treats its weights as
    /// known precisions — but they are not, they are a function of the residuals. Its residual
    /// variance <c>Σw·r²/(n−2)</c> is deflated by exactly the down-weighting that makes the fit
    /// robust, so the interval comes out about 10% too tight on clean normal data. For the
    /// primary instrument of the whole app that is not acceptable: the estimate would be
    /// accurate and the stated uncertainty would be a lie.
    /// </para>
    /// <para>
    /// The correct scale is Huber's: τ² = s²·E[ψ²]/E[ψ′]², with ψ the clipped residual and ψ′
    /// its derivative — 1 inside the clip, 0 outside. On clean normal data this lands 2.6%
    /// above the ordinary least-squares error, which is the price of the robustness, rather
    /// than 10% below it.
    /// </para>
    /// </summary>
    private static LineFit WithRobustStandardErrors(
        ReadOnlySpan<double> x,
        ReadOnlySpan<double> y,
        in LineFit fit,
        double tuning)
    {
        var n = Math.Min(x.Length, y.Length);
        if (!fit.IsValid || n < 3)
        {
            return fit;
        }

        var residuals = new double[n];
        var used = 0;
        double sx = 0.0, sxx = 0.0;

        for (var i = 0; i < n; i++)
        {
            if (!double.IsFinite(x[i]) || !double.IsFinite(y[i]))
            {
                continue;
            }

            residuals[used++] = y[i] - fit.ValueAt(x[i]);
            sx += x[i];
            sxx += x[i] * x[i];
        }

        if (used < 3)
        {
            return fit;
        }

        var scale = SeriesMath.MedianAbsoluteDeviation(residuals.AsSpan(0, used));
        if (!double.IsFinite(scale) || scale <= 0.0)
        {
            return fit;
        }

        var threshold = tuning * scale;
        var sumPsiSquared = 0.0;
        var inside = 0;

        for (var i = 0; i < used; i++)
        {
            var psi = Math.Clamp(residuals[i], -threshold, threshold);
            sumPsiSquared += psi * psi;
            if (Math.Abs(residuals[i]) <= threshold)
            {
                inside++;
            }
        }

        if (inside == 0)
        {
            return fit;
        }

        var meanPsiPrime = inside / (double)used;
        var tauSquared = sumPsiSquared / used / (meanPsiPrime * meanPsiPrime);

        var centredSumSquares = sxx - sx * sx / used;
        if (!(centredSumSquares > 0.0))
        {
            return fit;
        }

        var mean = sx / used;
        var slopeVariance = tauSquared / centredSumSquares;
        var interceptVariance = tauSquared * (1.0 / used + mean * mean / centredSumSquares);

        return fit with
        {
            SlopeStandardError = Math.Sqrt(slopeVariance),
            InterceptStandardError = Math.Sqrt(interceptVariance),
        };
    }

    /// <summary>Fraction of points Huber down-weighted below <paramref name="cutoff"/> — an outlier rate indicator.</summary>
    public static double OutlierFraction(
        ReadOnlySpan<double> x,
        ReadOnlySpan<double> y,
        in LineFit fit,
        double cutoff = 3.0)
    {
        var n = Math.Min(x.Length, y.Length);
        if (n == 0 || !fit.IsValid)
        {
            return double.NaN;
        }

        var residuals = new double[n];
        for (var i = 0; i < n; i++)
        {
            residuals[i] = y[i] - fit.ValueAt(x[i]);
        }

        var scale = SeriesMath.MedianAbsoluteDeviation(residuals);
        if (!double.IsFinite(scale) || scale <= 0.0)
        {
            return 0.0;
        }

        var count = 0;
        foreach (var residual in residuals)
        {
            if (Math.Abs(residual) > cutoff * scale)
            {
                count++;
            }
        }

        return count / (double)n;
    }
}
