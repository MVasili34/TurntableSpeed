using TurntableSpeed.Core.Contracts;
using TurntableSpeed.Core.Dsp;

namespace TurntableSpeed.Core.Tests.Dsp;

/// <summary>
/// PCA is what finds the plane the magnetic field sweeps out, and hence the rotation axis. If
/// the plane is wrong the projected trajectory is not an ellipse and the phase is meaningless.
/// </summary>
public class PrincipalAxesTests
{
    private static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    [Fact]
    public void SymmetricEigenSolverRecoversADiagonalMatrix()
    {
        var m = new double[3, 3];
        m[0, 0] = 5.0;
        m[1, 1] = 9.0;
        m[2, 2] = 1.0;

        var eigen = SymmetricEigen3.Decompose(m);

        TestAssertions.Close(9.0, eigen.Value0, 1e-12, "largest eigenvalue");
        TestAssertions.Close(5.0, eigen.Value1, 1e-12, "middle eigenvalue");
        TestAssertions.Close(1.0, eigen.Value2, 1e-12, "smallest eigenvalue");
        TestAssertions.Close(1.0, Math.Abs(eigen.Vector0.Y), 1e-12, "eigenvector of the largest value");
    }

    [Fact]
    public void SymmetricEigenSolverSatisfiesTheEigenEquation()
    {
        var m = new double[3, 3]
        {
            { 4.0, 1.5, -0.5 },
            { 1.5, 3.0, 0.75 },
            { -0.5, 0.75, 2.0 },
        };

        var eigen = SymmetricEigen3.Decompose(m);

        for (var k = 0; k < 3; k++)
        {
            var v = eigen.Vector(k);
            var lambda = eigen[k];

            var mv = (
                m[0, 0] * v.X + m[0, 1] * v.Y + m[0, 2] * v.Z,
                m[1, 0] * v.X + m[1, 1] * v.Y + m[1, 2] * v.Z,
                m[2, 0] * v.X + m[2, 1] * v.Y + m[2, 2] * v.Z);

            TestAssertions.Close(lambda * v.X, mv.Item1, 1e-10, $"eigen equation X for pair {k}");
            TestAssertions.Close(lambda * v.Y, mv.Item2, 1e-10, $"eigen equation Y for pair {k}");
            TestAssertions.Close(lambda * v.Z, mv.Item3, 1e-10, $"eigen equation Z for pair {k}");
            TestAssertions.Close(1.0, Math.Sqrt(Dot(v, v)), 1e-12, $"eigenvector {k} is a unit vector");
        }

        Assert.True(eigen.Value0 >= eigen.Value1 && eigen.Value1 >= eigen.Value2, "eigenvalues must descend");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(15.0)]
    [InlineData(40.0)]
    public void FindsThePlaneOfARotatingVector(double tiltDegrees)
    {
        var tilt = tiltDegrees * Math.PI / 180.0;
        var samples = new List<Vector3Sample>();

        for (var i = 0; i < 500; i++)
        {
            var t = 2.0 * Math.PI * i / 500.0;
            var x = 20.0 * Math.Cos(t);
            var y = 20.0 * Math.Sin(t);
            const double z = 0.0;

            // Rotate the circle about the X axis by the tilt.
            samples.Add(new Vector3Sample(
                i * 0.02,
                x + 3.0,
                y * Math.Cos(tilt) - z * Math.Sin(tilt) - 1.0,
                y * Math.Sin(tilt) + z * Math.Cos(tilt) + 45.0));
        }

        var axes = PrincipalAxes.Compute(samples);

        Assert.NotNull(axes);

        // The centroid is the centre of the circle.
        TestAssertions.Close(3.0, axes!.Mean.X, 1e-9, "centroid X");
        TestAssertions.Close(-1.0, axes.Mean.Y, 1e-9, "centroid Y");
        TestAssertions.Close(45.0, axes.Mean.Z, 1e-9, "centroid Z");

        // A perfect circle lies exactly in a plane.
        Assert.True(axes.Flatness < 1e-12, $"flatness was {axes.Flatness}");

        // The normal is the tilted Z axis, up to sign.
        var expectedNormal = (0.0, -Math.Sin(tilt), Math.Cos(tilt));
        TestAssertions.Close(1.0, Math.Abs(Dot(axes.Normal, expectedNormal)), 1e-9, "plane normal");
    }

    [Fact]
    public void ProjectionOfACircleIsACircle()
    {
        var samples = new List<Vector3Sample>();
        const double radius = 17.0;
        var tilt = 0.35;

        for (var i = 0; i < 360; i++)
        {
            var t = 2.0 * Math.PI * i / 360.0;
            var x = radius * Math.Cos(t);
            var y = radius * Math.Sin(t);
            samples.Add(new Vector3Sample(
                i,
                x,
                y * Math.Cos(tilt),
                y * Math.Sin(tilt)));
        }

        var axes = PrincipalAxes.Compute(samples)!;

        foreach (var sample in samples)
        {
            var (u, v) = axes.Project(sample);
            TestAssertions.Close(radius, Math.Sqrt(u * u + v * v), 1e-9, "projected radius");
        }
    }

    [Fact]
    public void FlatnessGrowsForANonPlanarCloud()
    {
        var rng = new Random(53);
        var samples = new List<Vector3Sample>();
        for (var i = 0; i < 1000; i++)
        {
            samples.Add(new Vector3Sample(i, rng.NextDouble(), rng.NextDouble(), rng.NextDouble()));
        }

        var axes = PrincipalAxes.Compute(samples)!;

        Assert.True(axes.Flatness > 0.5, $"an isotropic cloud should not look planar, flatness was {axes.Flatness}");
    }

    [Fact]
    public void MeanDirectionKeepsTheSign()
    {
        var positive = new List<Vector3Sample>();
        var negative = new List<Vector3Sample>();
        for (var i = 0; i < 100; i++)
        {
            positive.Add(new Vector3Sample(i, 0.0, 0.0, 3.5));
            negative.Add(new Vector3Sample(i, 0.0, 0.0, -3.5));
        }

        var up = PrincipalAxes.MeanDirection(positive);
        var down = PrincipalAxes.MeanDirection(negative);

        TestAssertions.Close(1.0, up.Z, 1e-12, "positive rotation direction");
        TestAssertions.Close(-1.0, down.Z, 1e-12, "negative rotation direction");
        TestAssertions.Close(3.5, up.Norm, 1e-12, "magnitude");
    }

    [Fact]
    public void TooFewSamplesReturnNull()
    {
        Assert.Null(PrincipalAxes.Compute(new List<Vector3Sample> { new(0, 1, 2, 3), new(1, 2, 3, 4) }));
    }
}
