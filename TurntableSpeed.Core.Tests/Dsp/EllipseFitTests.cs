using TurntableSpeed.Core.Dsp;
using TurntableSpeed.Core.Tests.Synth;

namespace TurntableSpeed.Core.Tests.Dsp;

/// <summary>
/// Spec §7.3: generate points on a known ellipse, recover the parameters. This is the
/// hard-iron / soft-iron model of the magnetometer estimator — the centre is the offset and
/// the axis ratio is the distortion, so an error here becomes a speed error.
/// </summary>
public class EllipseFitTests
{
    private static (double[] X, double[] Y) OnEllipse(
        double cx, double cy, double a, double b, double angle, int count,
        double noiseSigma = 0.0, int seed = 1, double arcFraction = 1.0)
    {
        var xs = new double[count];
        var ys = new double[count];
        var rng = new Random(seed);
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);

        for (var i = 0; i < count; i++)
        {
            var t = 2.0 * Math.PI * arcFraction * i / count;
            var u = a * Math.Cos(t);
            var v = b * Math.Sin(t);
            xs[i] = cx + u * cos - v * sin + GyroSignalGenerator.Gaussian(rng, noiseSigma);
            ys[i] = cy + u * sin + v * cos + GyroSignalGenerator.Gaussian(rng, noiseSigma);
        }

        return (xs, ys);
    }

    [Theory]
    [InlineData(0.0, 0.0, 10.0, 10.0, 0.0)]
    [InlineData(3.5, -2.25, 20.0, 12.0, 0.0)]
    [InlineData(-40.0, 15.0, 25.0, 18.0, 0.7)]
    [InlineData(0.0, 0.0, 30.0, 5.0, -1.2)]
    public void RecoversKnownEllipseParameters(double cx, double cy, double a, double b, double angle)
    {
        var (xs, ys) = OnEllipse(cx, cy, a, b, angle, 360);

        var fit = EllipseFit.Fit(xs, ys);

        Assert.NotNull(fit);
        TestAssertions.Close(cx, fit!.CenterX, Tolerances.EllipseParameter * Math.Max(1.0, Math.Abs(cx)), "centre X");
        TestAssertions.Close(cy, fit.CenterY, Tolerances.EllipseParameter * Math.Max(1.0, Math.Abs(cy)), "centre Y");
        TestAssertions.Close(Math.Max(a, b), fit.SemiMajor, Tolerances.EllipseParameter * a, "semi-major axis");
        TestAssertions.Close(Math.Min(a, b), fit.SemiMinor, Tolerances.EllipseParameter * a, "semi-minor axis");
        Assert.True(fit.ResidualRms < 1e-6, $"residual RMS was {fit.ResidualRms}");

        if (Math.Abs(a - b) > 1e-9)
        {
            // The major-axis direction is defined modulo π.
            var expected = a >= b ? angle : angle + Math.PI / 2.0;
            TestAssertions.CloseModulo(expected, fit.Angle, Math.PI, 1e-4, "major-axis angle");
        }
    }

    [Fact]
    public void RecoversParametersFromNoisyPoints()
    {
        const double a = 20.0;
        const double b = 16.0;
        var (xs, ys) = OnEllipse(5.0, -3.0, a, b, 0.4, 1000, noiseSigma: 0.2, seed: 3);

        var fit = EllipseFit.Fit(xs, ys);

        Assert.NotNull(fit);
        TestAssertions.Close(5.0, fit!.CenterX, a * Tolerances.EllipseParameterNoisy, "noisy centre X");
        TestAssertions.Close(-3.0, fit.CenterY, a * Tolerances.EllipseParameterNoisy, "noisy centre Y");
        TestAssertions.WithinPercent(a, fit.SemiMajor, Tolerances.EllipseParameterNoisy * 100.0, "noisy semi-major");
        TestAssertions.WithinPercent(b, fit.SemiMinor, Tolerances.EllipseParameterNoisy * 100.0, "noisy semi-minor");
    }

    [Fact]
    public void NormalizeMapsEllipsePointsOntoTheUnitCircle()
    {
        var (xs, ys) = OnEllipse(-8.0, 4.0, 22.0, 11.0, 0.9, 200);

        var fit = EllipseFit.Fit(xs, ys);

        Assert.NotNull(fit);
        for (var i = 0; i < xs.Length; i++)
        {
            var (nx, ny) = fit!.Normalize(xs[i], ys[i]);
            TestAssertions.Close(1.0, Math.Sqrt(nx * nx + ny * ny), 1e-6, $"radius of normalised point {i}");
        }
    }

    [Fact]
    public void PhaseAdvancesUniformlyForUniformParameterSteps()
    {
        const int count = 360;
        var (xs, ys) = OnEllipse(2.0, -6.0, 30.0, 12.0, -0.5, count);

        var fit = EllipseFit.Fit(xs, ys);
        Assert.NotNull(fit);

        var phases = new double[count];
        for (var i = 0; i < count; i++)
        {
            phases[i] = fit!.Phase(xs[i], ys[i]);
        }

        var unwrapped = PhaseMath.Unwrap(phases);
        var expectedStep = 2.0 * Math.PI / count;

        for (var i = 1; i < count; i++)
        {
            TestAssertions.Close(expectedStep, unwrapped[i] - unwrapped[i - 1], 1e-6, $"phase step {i}");
        }
    }

    [Fact]
    public void AxisRatioReportsSoftIronDistortion()
    {
        var (xs, ys) = OnEllipse(0.0, 0.0, 24.0, 12.0, 0.0, 200);

        var fit = EllipseFit.Fit(xs, ys);

        Assert.NotNull(fit);
        TestAssertions.Close(2.0, fit!.AxisRatio, 1e-5, "axis ratio");
    }

    [Fact]
    public void CollinearPointsAreRejectedRatherThanFitted()
    {
        var xs = new double[50];
        var ys = new double[50];
        for (var i = 0; i < 50; i++)
        {
            xs[i] = i;
            ys[i] = 2.0 * i + 1.0;
        }

        Assert.Null(EllipseFit.Fit(xs, ys));
    }

    [Fact]
    public void TooFewPointsAreRejected()
    {
        var (xs, ys) = OnEllipse(0.0, 0.0, 10.0, 8.0, 0.0, EllipseFit.MinimumPoints - 1);

        Assert.Null(EllipseFit.Fit(xs, ys));
    }

    [Fact]
    public void ConicDiscriminantIsNegativeForAnEllipse()
    {
        // x²/9 + y²/4 = 1  →  4x² + 9y² − 36 = 0.
        var conic = new ConicCoefficients(4.0, 0.0, 9.0, 0.0, 0.0, -36.0);

        Assert.True(conic.Discriminant < 0.0);

        var geometry = EllipseFit.FromConic(conic);

        Assert.NotNull(geometry);
        TestAssertions.Close(3.0, geometry!.Value.SemiMajor, 1e-9, "semi-major from conic");
        TestAssertions.Close(2.0, geometry.Value.SemiMinor, 1e-9, "semi-minor from conic");
        TestAssertions.Close(0.0, geometry.Value.CenterX, 1e-9, "centre X from conic");
    }
}
