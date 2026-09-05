using TurntableSpeed.Core.Dsp;
using TurntableSpeed.Core.Tests.Synth;

namespace TurntableSpeed.Core.Tests.Dsp;

/// <summary>
/// The line fit is where a phase ramp becomes a speed <em>and an interval</em>. Recovering the
/// slope is the easy half; reporting an honest uncertainty for it is the half that matters, so
/// the standard errors are checked by simulation rather than assumed.
/// </summary>
public class LineFitTests
{
    private const double Slope = 3.4906585;
    private const double Intercept = 1.25;

    private static (double[] X, double[] Y) Line(int count, double noiseSigma, Random rng, double dx = 0.02)
    {
        var x = new double[count];
        var y = new double[count];
        for (var i = 0; i < count; i++)
        {
            x[i] = i * dx;
            y[i] = Intercept + Slope * x[i] + GyroSignalGenerator.Gaussian(rng, noiseSigma);
        }

        return (x, y);
    }

    /// <summary>
    /// Simulate many fits and return the spread of z = (slope − truth) / reported error. A
    /// correctly stated interval gives 1; below 1 the fit is being pessimistic, above 1 it is
    /// claiming a precision it does not have.
    /// </summary>
    private static (double Rms, double Mean) SlopeErrorCalibration(
        Func<double[], double[], LineFit> fit,
        int trials,
        int pointsPerTrial = 200,
        double noiseSigma = 0.05)
    {
        var rng = new Random(20_250_807);
        var sum = 0.0;
        var sumSquares = 0.0;
        var used = 0;

        for (var trial = 0; trial < trials; trial++)
        {
            var (x, y) = Line(pointsPerTrial, noiseSigma, rng);
            var result = fit(x, y);
            if (!result.IsValid || !(result.SlopeStandardError > 0.0))
            {
                continue;
            }

            var z = (result.Slope - Slope) / result.SlopeStandardError;
            sum += z;
            sumSquares += z * z;
            used++;
        }

        return (Math.Sqrt(sumSquares / used), sum / used);
    }

    [Fact]
    public void RecoversAKnownLineExactlyFromNoiselessPoints()
    {
        var (x, y) = Line(100, 0.0, new Random(1));

        var fit = WeightedLeastSquares.Fit(x, y);

        TestAssertions.Close(Slope, fit.Slope, 1e-9, "slope");
        TestAssertions.Close(Intercept, fit.Intercept, 1e-9, "intercept");
        TestAssertions.Close(1.0, fit.RSquared, 1e-12, "R²");
        TestAssertions.Close(0.0, fit.ResidualStandardDeviation, 1e-9, "residual σ");
    }

    [Fact]
    public void LeastSquaresStandardErrorIsCalibrated()
    {
        var (rms, mean) = SlopeErrorCalibration((x, y) => WeightedLeastSquares.Fit(x, y), trials: 2000);

        Assert.InRange(rms, 0.94, 1.06);
        Assert.InRange(mean, -0.1, 0.1);
    }

    [Fact]
    public void RobustStandardErrorIsCalibratedToo()
    {
        // The defect this pins down: the IRLS loop ends in a weighted fit, and the weighted
        // variance formula treats its Huber weights as known precisions. They are not — they are
        // a function of the residuals — so Σw·r² is deflated by exactly the down-weighting that
        // makes the fit robust, and the interval came out about 10% too tight (RMS(z) ≈ 1.10).
        var (rms, mean) = SlopeErrorCalibration((x, y) => RobustLineFit.Huber(x, y), trials: 2000);

        Assert.InRange(rms, 0.94, 1.06);
        Assert.InRange(mean, -0.1, 0.1);
    }

    [Fact]
    public void RobustnessCostsALittlePrecisionAndTheIntervalSaysSo()
    {
        // Huber at the standard tuning is about 95% efficient on clean normal data, so its
        // interval should average a couple of percent wider than least squares — and above all
        // must not average narrower, which is what the deflated weighted formula made it do.
        //
        // Averaged, not counted trial by trial: the mean gap is only ~2.6% while the two
        // estimators' scales scatter by ~9% from sample to sample (the robust scale comes from a
        // median absolute deviation, which is a noisier estimate of σ than a sum of squares), so
        // the sign of any single trial says very little.
        var rng = new Random(31);
        var sum = 0.0;
        const int trials = 400;

        for (var trial = 0; trial < trials; trial++)
        {
            var (x, y) = Line(200, 0.05, rng);
            var ordinary = WeightedLeastSquares.Fit(x, y);
            var robust = RobustLineFit.Huber(x, y);

            sum += robust.SlopeStandardError / ordinary.SlopeStandardError;
        }

        var meanRatio = sum / trials;

        Assert.True(meanRatio is > 1.0 and < 1.12,
            $"robust interval averaged {meanRatio:F4}× the least-squares one; expected a little " +
            $"above 1 (the price of robustness), and never below it");
    }

    [Fact]
    public void HuberResistsOutliersThatDragLeastSquaresOff()
    {
        var rng = new Random(41);
        var (x, y) = Line(400, 0.02, rng);

        // Five percent of the points thrown a long way off.
        for (var i = 0; i < y.Length; i += 20)
        {
            y[i] += 25.0;
        }

        var ordinary = WeightedLeastSquares.Fit(x, y);
        var robust = RobustLineFit.Huber(x, y);

        TestAssertions.WithinPercent(Slope, robust.Slope, 1.0, "robust slope under contamination");
        Assert.True(Math.Abs(robust.Slope - Slope) < Math.Abs(ordinary.Slope - Slope),
            $"robust {robust.Slope:F5} should beat ordinary {ordinary.Slope:F5} against truth {Slope:F5}");
    }

    [Fact]
    public void OutlierFractionReportsTheContaminationRate()
    {
        var rng = new Random(43);
        var (x, y) = Line(1000, 0.02, rng);
        for (var i = 0; i < y.Length; i += 20)
        {
            y[i] += 25.0;
        }

        var fit = RobustLineFit.Huber(x, y);

        TestAssertions.Close(0.05, RobustLineFit.OutlierFraction(x, y, fit), 0.01, "reported outlier fraction");
    }

    [Fact]
    public void SerialCorrelationInflatesTheInterval()
    {
        // Residuals that wander are not independent, and pretending otherwise would shrink the
        // interval like 1/√n on data that carries nowhere near n independent observations.
        var rng = new Random(47);
        const int n = 2000;
        var x = new double[n];
        var y = new double[n];
        var wander = 0.0;

        for (var i = 0; i < n; i++)
        {
            wander = 0.95 * wander + GyroSignalGenerator.Gaussian(rng, 0.02);
            x[i] = i * 0.02;
            y[i] = Intercept + Slope * x[i] + wander;
        }

        var fit = WeightedLeastSquares.Fit(x, y);

        Assert.True(fit.Lag1ResidualCorrelation > 0.8,
            $"the residuals are plainly correlated, lag-1 was {fit.Lag1ResidualCorrelation:F3}");
        Assert.True(fit.EffectiveCount < fit.Count / 10.0,
            $"effective count {fit.EffectiveCount:F0} should be far below {fit.Count}");
        Assert.True(fit.SlopeStandardErrorCorrected > 3.0 * fit.SlopeStandardError,
            "the corrected interval must be substantially wider");
    }

    [Fact]
    public void IndependentResidualsAreNotPenalised()
    {
        var rng = new Random(53);
        var (x, y) = Line(2000, 0.05, rng);

        var fit = WeightedLeastSquares.Fit(x, y);

        Assert.InRange(fit.Lag1ResidualCorrelation, -0.1, 0.1);
        TestAssertions.WithinPercent(fit.SlopeStandardError, fit.SlopeStandardErrorCorrected, 15.0,
            "white residuals need no inflation");
    }

    [Fact]
    public void WeightsScaleDoesNotChangeTheReportedUncertainty()
    {
        var rng = new Random(59);
        var (x, y) = Line(300, 0.05, rng);

        var unit = new double[x.Length];
        var scaled = new double[x.Length];
        Array.Fill(unit, 1.0);
        Array.Fill(scaled, 1000.0);

        var a = WeightedLeastSquares.Fit(x, y, unit);
        var b = WeightedLeastSquares.Fit(x, y, scaled);

        TestAssertions.Close(a.Slope, b.Slope, 1e-12, "slope");
        TestAssertions.Close(a.SlopeStandardError, b.SlopeStandardError, 1e-12, "slope standard error");
    }

    [Fact]
    public void TooFewPointsProduceAnInvalidFitRatherThanANumber()
    {
        Assert.False(WeightedLeastSquares.Fit(new[] { 1.0, 2.0 }, new[] { 1.0, 2.0 }).IsValid);
        Assert.False(RobustLineFit.Huber(new[] { 1.0, 2.0 }, new[] { 1.0, 2.0 }).IsValid);
    }

    [Fact]
    public void VerticalDataProducesAnInvalidFit()
    {
        var x = new double[10];
        var y = new double[10];
        for (var i = 0; i < 10; i++)
        {
            y[i] = i;
        }

        Assert.False(WeightedLeastSquares.Fit(x, y).IsValid);
    }
}
